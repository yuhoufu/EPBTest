using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Timing;

namespace Controller
{
    /// <summary>
    ///     EpbManager 扩展：批量启动（学习 + 正式），并为每个压力组建立“锚点时间轴”，
    ///     让每个通道以固定相位（电源组内索引 × Δ）锁相到这条时间轴，保证“每圈对齐 + 组内错峰”。
    ///     使用方式：
    ///     await manager.StartBatchSynchronizedAsync(new[]{1,2,4,6}, learnCycles:5, token);
    /// </summary>
    public partial class EpbManager
    {
        // 字段区
        private readonly Dictionary<int, EpbCycleRunner> _runnerCache = new();
        private readonly Dictionary<int, HighPrecisionTimer> _timerCache = new();


        #region 对外主入口 Batch Start (Learning + Formal) with Group Anchor + Stagger Phases

        /// <summary>
        ///     批量启动 EPB 通道（学习 + 正式），每圈都对齐到“压力组锚点 + 固定相位”。<br />
        ///     关键增强：
        ///     <list type="number">
        ///         <item>为每个压力组的 <c>t0</c> 预留 <see cref="AnchorWarmupMs" /> 预热裕度，确保首圈（k=0）也有正的延时。</item>
        ///         <item>正式学习阶段内：对每个“圈 × 组”先起“液压锚点”任务，并在每个通道任务里 <c>await</c> 该任务（作为屏障）。</item>
        ///         <item>对很小/负的 <c>delay</c> 不再直接“零等待”，而是 <c>Task.Yield()</c> 打散同刻调度，降低通道扎堆概率。</item>
        ///     </list>
        /// </summary>
        /// <param name="channels">要启动的 EPB 通道（1..12）。例如 new[]{1,2,4,6}</param>
        /// <param name="learnCycles">自学习圈数。=0 则跳过学习。</param>
        /// <param name="token">取消令牌。</param>
        /// <remarks>
        ///     依赖：<br />
        ///     - <c>GroupByPressure(int[])</c>：将通道按压力组（1/2）分组。<br />
        ///     - <c>CeilToBoundary(DateTime,int)</c>：把时间上取整到周期边界。<br />
        ///     - <c>PreReleaseBatchStaggeredAsync</c>（可选）：你的“预释放三波错峰”方法。<br />
        ///     - <c>RunLearningPhaseAsync</c>（见下方替换版）：圈内并发 + 相位错峰。<br />
        ///     - <c>StartFormalPhaseTimers</c>：正式阶段的高精计时器（你已有）。<br />
        /// </remarks>
        /// <exception cref="ArgumentException">当 <paramref name="channels" /> 为空时抛出。</exception>
        public async Task StartBatchSynchronizedAsync(int[] channels, int learnCycles, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空", nameof(channels));

            // —— 1) 按压力组归类，并为每组计算“锚点零相位” t0（含预热裕度 + 周期上取整）—— //
            var nowUtc = DateTime.UtcNow;
            var groups = GroupByPressure(channels); // Dictionary<int, List<int>>，键为 1/2
            var t0OfGroup = new Dictionary<int, DateTime>(); // key: PG(1/2), value: t0(UTC)

            foreach (var kv in groups)
            {
                var pg = kv.Key;
                var list = kv.Value;
                if (list == null || list.Count == 0) continue;

                // 预热裕度：避免首圈 k=0 时 delay ≤ 0 造成“零等待”扎堆
                var warm = nowUtc.AddMilliseconds(AnchorWarmupMs);
                // 上取整到下一个周期边界（使所有 pg 的 t0 对齐到统一节拍）
                t0OfGroup[pg] = CeilToBoundary(warm, PeriodMs);
            }

            // —— 2) （可选）学习前“预释放”：三波错峰，仅做一次，避免每圈额外能耗 —— //
            if (learnCycles > 0)
            {
                var all = groups.Values.SelectMany(v => v).Distinct().OrderBy(x => x).ToArray();
                _log?.Info($"批量预释放：通道[{string.Join(",", all)}]，三波错峰，Δ={StaggerDeltaMs}ms。", "EPB");

                // keepMs=null → 由 Runner 内部使用 DefaultPreReleaseKeepMs
                await PreReleaseBatchStaggeredAsync(all, /*keepMs*/ null, /*deltaMs*/ StaggerDeltaMs, token)
                    .ConfigureAwait(false);
            }

            // —— 3) 学习阶段：次数不多，用“每圈循环 + 锚点屏障 + 相位延时”实现稳定对齐 —— //
            if (learnCycles > 0)
                await RunLearningPhaseAsync(groups, t0OfGroup, learnCycles, token).ConfigureAwait(false);

            // —— 4) 正式阶段：为每个通道创建对齐到“锚点+相位”的高精计时器 —— //
            StartFormalPhaseTimers(groups, t0OfGroup, token);
        }

        #endregion

        #region 正式阶段（12 个计时器锁相到锚点 + 固定相位）

        /// <summary>
        ///     为每个通道创建并启动 HighPrecisionTimer，使其每圈在 t0 + k*Period + phase 触发。
        ///     计时器采用 AlignToWallClock，保证“每圈都与墙钟对齐（不累积误差）”。
        /// </summary>
        private void StartFormalPhaseTimers(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            CancellationToken token)
        {
            foreach (var kv in groups)
            {
                var pg = kv.Key;
                var list = kv.Value;

                if (list.Count == 0) continue;

                var t0 = t0OfGroup[pg];
                var enabled = list.OrderBy(x => x).ToList();

                foreach (var ch in enabled)
                {
                    var phase = IndexInPowerGroup(ch) * StaggerDeltaMs;
                    var initialDelay = (int)(t0.AddMilliseconds(phase) - DateTime.UtcNow).TotalMilliseconds;

                    // 若 warmup 偏小导致已过相位，滚动到下一（几）圈的相位
                    if (initialDelay < 0)
                    {
                        var rounds = -initialDelay / PeriodMs + 1;
                        initialDelay += rounds * PeriodMs;
                    }

                    var runner = GetRunner(ch);
                    PrepareRunnerForNoHeadAndTailCompensation(ch);

                    var timer = GetTimer(ch, PeriodMs, OverrunPolicy.AlignToWallClock);

                    // —— 计时器每圈工作（cycleIndex 从 1 开始） —— //
                    _ = timer.StartAsync(
                        int.MaxValue,
                        initialDelay,
                        async (cycleIndex, ct) =>
                        {
                            // 1) 在本圈锚点时刻为该压力组建压（协调器要幂等）
                            await HydraulicEnterAtGroupAnchorAsync(pg, ct).ConfigureAwait(false);

                            // 2) 计算本圈的绝对“硬截止”时刻（用于 Runner 保证统一收尾）
                            var k = cycleIndex - 1;
                            var deadlineUtc = t0.AddMilliseconds((k + 1) * PeriodMs);

                            // 3) 跑一圈（Runner 内部不做①；在⑧中扣回相位，并以 deadline 收尾）
                            return await runner.RunOneAlignedAsync(
                                PeriodMs,
                                T8BaseMs,
                                phase,
                                T8MinMs,
                                deadlineUtc,
                                ct
                            ).ConfigureAwait(false);
                        });
                }
            }
        }

        #endregion


        #region 学习阶段（循环+延时：轻量且每圈对齐）

        /// <summary>
        ///     学习阶段外壳：并发“圈 × 组”，同组内按固定相位（0/Δ/2Δ）错峰起跑，每圈都与压力组锚点对齐。<br />
        ///     关键增强：
        ///     <list type="number">
        ///         <item>对每个“圈 × 组”先创建 <c>HydraulicEnterAtGroupAnchorAsync</c> 任务作为屏障；</item>
        ///         <item>
        ///             若计算得到的 <c>at = tk + phase</c> 已落后于当前时刻，则按 <c>PeriodMs</c> 向前“整周期滚动”到未来（见
        ///             <see cref="RollForwardToFuture" />）；
        ///         </item>
        ///         <item>确保首圈也不会出现负延时导致的“同刻上电”。</item>
        ///     </list>
        /// </summary>
        /// <param name="groups">按压力组分组的通道集合（key: 1/2）。</param>
        /// <param name="t0OfGroup">
        ///     各压力组的零相位锚点（UTC）。建议由 <c>CeilToBoundary(DateTime.UtcNow.AddMilliseconds(AnchorWarmupMs), PeriodMs)</c> 生成，
        ///     以便给首圈留下预热裕度。
        /// </param>
        /// <param name="learnCycles">学习圈数（&gt;0）。</param>
        /// <param name="token">取消令牌。</param>
        private async Task RunLearningPhaseAsyncOld(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            int learnCycles,
            CancellationToken token)
        {
            // —— 保护：无任务直接返回 —— //
            if (groups == null || groups.Count == 0 || learnCycles <= 0)
                return;

            // —— 0) 让所有 Runner 进入“无① + ⑧外壳收尾（学习不等尾）”模式，并开启聚合 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);
                    r.UseNoHeadPhase = true; // 学习不做①，错峰由外层“相位”承担
                    r.EnableTailCompensation = true; // ⑧尾部由外壳统一对齐（学习单圈不等待）
                    r.TailMinMs = T8MinMs;
                    r.BeginLearnAggregation(); // 清空本轮学习样本
                }
            }

            // —— 1) 多圈学习 —— //
            for (var k = 0; k < learnCycles; k++)
            {
                token.ThrowIfCancellationRequested();
                var tasksAllGroups = new List<Task>();

                foreach (var kv in groups)
                {
                    var pg = kv.Key; // 压力组 ID：1/2
                    var list = kv.Value;
                    if (list == null || list.Count == 0) continue;

                    // 本圈该压力组的锚点时刻
                    var t0 = t0OfGroup[pg];
                    var tk = t0.AddMilliseconds(k * PeriodMs);

                    // —— 1.1) 组锚点任务（屏障） —— //
                    var anchorTask = HydraulicEnterAtGroupAnchorAsync(pg, token);
                    tasksAllGroups.Add(anchorTask); // 并入等待，便于异常汇总

                    // —— 1.2) 组内通道：相位错峰（0/Δ/2Δ）+ 过时滚动到未来 —— //
                    var enabled = list.OrderBy(x => x).ToList();
                    for (var i = 0; i < enabled.Count; i++)
                    {
                        var ch = enabled[i];
                        var phase = IndexInPowerGroup(ch) * StaggerDeltaMs; // 0/Δ/2Δ
                        var at = tk.AddMilliseconds(phase);

                        tasksAllGroups.Add(Task.Run(async () =>
                        {
                            // ① 等待液压锚点到位（屏障）
                            await anchorTask.ConfigureAwait(false);

                            // ② 若 at 已过时 → 推进到未来的“下一个/下N个周期”的同相位时刻
                            //    这样可避免负延时导致的“首圈同刻上电”
                            var now = DateTime.UtcNow;
                            var atFuture = RollForwardToFuture(at, now, PeriodMs, /*safetyMs:*/ 2);

                            // ③ 计算剩余并等待
                            var delay = atFuture - now;

                            _log?.Error(
                                $"通道{ch}: tk={tk:HH:mm:ss.fff}, phase={phase}ms, at={at:HH:mm:ss.fff}, delay={delay.TotalMilliseconds}ms");

                            var ms = (int)Math.Floor(delay.TotalMilliseconds);
                            if (ms > 0)
                                await Task.Delay(ms, token).ConfigureAwait(false);
                            else
                                await Task.Yield(); // 极小/微负：让出一次时间片，打散调度

                            // ④ 执行单圈学习核心（不做①；⑧由外壳统一收口）
                            var runner = GetRunner(ch);
                            var sample = await runner.LearnOneAlignedCoreAsync(
                                PeriodMs, T8BaseMs, phase, T8MinMs, token
                            ).ConfigureAwait(false);

                            if (sample != null)
                                runner.ApplyLearnSample(sample);
                        }, token));
                    }
                }

                // 本圈所有任务结束后进入下一圈
                await Task.WhenAll(tasksAllGroups).ConfigureAwait(false);
            }

            // —— 2) 学习聚合结束：写回中位数/统计量 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    GetRunner(ch).FinalizeLearnAggregation();
                }
            }
        }


        /// <summary>
        ///     学习阶段外壳：并发“圈 × 组”，同组内按固定相位（0/Δ/2Δ）错峰起跑，每圈都与压力组锚点对齐。<br />
        ///     关键增强：
        ///     <list type="number">
        ///         <item>对每个“圈 × 组”先创建 <c>HydraulicEnterAtGroupAnchorAsync</c> 任务作为屏障；</item>
        ///         <item>若计算得到的 <c>at = tk + phase</c> 已落后于当前时刻，则按 <c>PeriodMs</c> 向前“整周期滚动”到未来；</item>
        ///         <item>确保首圈也不会出现负延时导致的“同刻上电”。</item>
        ///         <item>【新增】在学习阶段的首尾对 <c>SafetyMargin</c> 做“开始聚合/收敛落地”。</item>
        ///     </list>
        /// </summary>
        /// <param name="groups">按压力组分组的通道集合（key: 1/2）。</param>
        /// <param name="t0OfGroup">
        ///     各压力组的零相位锚点（UTC）。建议由 <c>CeilToBoundary(DateTime.UtcNow.AddMilliseconds(AnchorWarmupMs), PeriodMs)</c> 生成，
        ///     以便给首圈留下预热裕度。
        /// </param>
        /// <param name="learnCycles">学习圈数（&gt;0）。</param>
        /// <param name="token">取消令牌。</param>
        private async Task RunLearningPhaseAsync(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            int learnCycles,
            CancellationToken token)
        {
            // —— 保护：无任务直接返回 —— //
            if (groups == null || groups.Count == 0 || learnCycles <= 0)
                return;

            // —— 0) 让所有 Runner 进入“无① + ⑧外壳收尾（学习不等尾）”模式，并开启聚合 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);

                    r.UseNoHeadPhase = true;          // 学习不做①，错峰由外层“相位”承担
                    r.EnableTailCompensation = true;  // ⑧尾部由外壳统一对齐（学习单圈不等待）
                    r.TailMinMs = T8MinMs;

                    // 原有：开始“学习样本聚合”（时间/阶段统计用）
                    r.BeginLearnAggregation();

                    // ★新增：开始“SafetyMargin 聚合”（清空学习轨迹、设置临时裕量）
                    r.BeginSafetyMarginLearning();

                    _log?.Info($"EPB[{ch}] SafetyMargin 学习开始！", "EPB");
                }
            }

            // —— 1) 多圈学习 —— //
            for (var k = 0; k < learnCycles; k++)
            {
                token.ThrowIfCancellationRequested();
                var tasksAllGroups = new List<Task>();

                foreach (var kv in groups)
                {
                    var pg = kv.Key; // 压力组 ID：1/2
                    var list = kv.Value;
                    if (list == null || list.Count == 0) continue;

                    // 本圈该压力组的锚点时刻
                    var t0 = t0OfGroup[pg];
                    var tk = t0.AddMilliseconds(k * PeriodMs);

                    // —— 1.1) 组锚点任务（屏障） —— //
                    var anchorTask = HydraulicEnterAtGroupAnchorAsync(pg, token);
                    tasksAllGroups.Add(anchorTask); // 并入等待，便于异常汇总

                    // —— 1.2) 组内通道：相位错峰（0/Δ/2Δ）+ 过时滚动到未来 —— //
                    var enabled = list.OrderBy(x => x).ToList();
                    for (var i = 0; i < enabled.Count; i++)
                    {
                        var ch = enabled[i];
                        var phase = IndexInPowerGroup(ch) * StaggerDeltaMs; // 0/Δ/2Δ
                        var at = tk.AddMilliseconds(phase);

                        tasksAllGroups.Add(Task.Run(async () =>
                        {
                            // ① 等待液压锚点到位（屏障）
                            await anchorTask.ConfigureAwait(false);

                            // ② 若 at 已过时 → 推进到未来的“下一个/下N个周期”的同相位时刻
                            var now = DateTime.UtcNow;
                            var atFuture = RollForwardToFuture(at, now, PeriodMs, /*safetyMs:*/ 2);

                            // ③ 计算剩余并等待
                            var delay = atFuture - now;

                            // 这里你之前用 Error 打日志，我保持一致
                            _log?.Error(
                                $"通道{ch}: tk={tk:HH:mm:ss.fff}, phase={phase}ms, at={at:HH:mm:ss.fff}, delay={delay.TotalMilliseconds}ms");

                            var ms = (int)Math.Floor(delay.TotalMilliseconds);
                            if (ms > 0)
                                await Task.Delay(ms, token).ConfigureAwait(false);
                            else
                                await Task.Yield(); // 极小/微负：让出一次时间片，打散调度

                            // ④ 执行单圈学习核心（不做①；⑧由外壳统一收口）
                            var runner = GetRunner(ch);
                            var sample = await runner.LearnOneAlignedCoreAsync(
                                PeriodMs, T8BaseMs, phase, T8MinMs, token
                            ).ConfigureAwait(false);

                            if (sample != null)
                                runner.ApplyLearnSample(sample); // 你已有的学习样本聚合（非 SafetyMargin）
                        }, token));
                    }
                }

                // 本圈所有任务结束后进入下一圈
                await Task.WhenAll(tasksAllGroups).ConfigureAwait(false);
            }

            // —— 2) 学习聚合结束：写回中位数/统计量 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list == null ? null : list.OrderBy(x => x).ToList();
                if (enabled == null || enabled.Count == 0) continue;

                for (var i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);

                    // 原有：结束“学习样本聚合”
                    r.FinalizeLearnAggregation();

                    // ★新增：结束“SafetyMargin 聚合”→鲁棒收敛→一次性写回 _safetyMarginA
                    r.FinalizeSafetyMarginLearning();
                }
            }
        }



        /// <summary>
        ///     若 <paramref name="at" /> 已早于 <paramref name="now" />（或离现在太近），
        ///     则按 <paramref name="periodMs" /> 的整周期，把它前滚到 <c>now + safetyMs</c> 之后，
        ///     同时保持“原有相位（相对周期边界）”不变。<br />
        ///     例如：at=10:00:30.350 已过时，period=5000ms（5s），则滚到 10:00:35.350/10:00:40.350/... 中的第一个 ≥ now+safetyMs 的时刻。
        /// </summary>
        private static DateTime RollForwardToFuture(DateTime at, DateTime now, int periodMs, int safetyMs)
        {
            // 允许留一个极小的“安全裕度”，避免边界上 now≈at 导致 0/负延时
            var refTime = now.AddMilliseconds(Math.Max(0, safetyMs));

            // 未过时，原样返回
            if (at >= refTime) return at;

            // 需要滚动的毫秒差
            var diffMs = (refTime - at).TotalMilliseconds;
            var n = (int)Math.Ceiling(diffMs / Math.Max(1, periodMs)); // 至少滚 1 个周期
            return at.AddMilliseconds(n * periodMs);
        }

        #endregion

        #region 可调参数（你可转为从 TestConfig 读取）

        /// <summary>每圈目标周期（毫秒）。必须与现有配置一致。</summary>
        public int PeriodMs { get; set; } = 30000;

        /// <summary>组内错峰步长 Δ（毫秒）。索引 0/1/2 → 0/Δ/2Δ。</summary>
        public int StaggerDeltaMs { get; set; } = 350; // 原先120ms

        /// <summary>将旧①“头部未上电”的时间并入⑧后的“尾段基准时长”（毫秒）。</summary>
        public int T8BaseMs { get; set; } = 800;

        /// <summary>⑧尾段最小时长保护（毫秒）。不足时先压缩⑦，再降 Δ 并告警。</summary>
        public int T8MinMs { get; set; } = 150;

        /// <summary>锚点预热（毫秒）：让建压有窗口覆盖 0/Δ/2Δ 三波上电。</summary>
        public int AnchorWarmupMs { get; set; } = 2000;

        #endregion

        #region 与现有工程对接的辅助（把这些方法体替换成你的实际实现）

        /// <summary>按压力组归类：PG1=1..6，PG2=7..12。</summary>
        private static Dictionary<int, List<int>> GroupByPressure(IEnumerable<int> channels)
        {
            var dict = new Dictionary<int, List<int>>
            {
                [1] = new(),
                [2] = new()
            };

            foreach (var ch in channels)
            {
                var pg = ch >= 1 && ch <= 6 ? 1 : 2;
                dict[pg].Add(ch);
            }

            return dict;
        }

        /*/// <summary>电源组内索引（固定映射）：1/4/7/10→0；2/5/8/11→1；3/6/9/12→2。</summary>
        private static int IndexInPowerGroup(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch - 1) % 3;
        }*/

        /// <summary>向上取整到周期边界（UTC）。</summary>
        private static DateTime CeilToBoundary(DateTime utcNow, int periodMs)
        {
            var ticksPerMs = TimeSpan.TicksPerMillisecond;
            var ms = utcNow.Ticks / ticksPerMs;
            var rem = ms % periodMs;
            var add = rem == 0 ? 0 : periodMs - rem;
            return new DateTime((ms + add) * ticksPerMs, DateTimeKind.Utc);
        }

        /// <summary>
        ///     使 Runner 进入“无① + ⑧扣回 + 尾段保护”的工作模式。
        ///     该方法只设置标志位，不会触发任何动作。
        /// </summary>
        private void PrepareRunnerForNoHeadAndTailCompensation(int ch)
        {
            var r = GetRunner(ch);
            r.UseNoHeadPhase = true;
            r.EnableTailCompensation = true;
            r.TailMinMs = T8MinMs;
        }

        /// <summary>获取指定通道的 EPB 循环运行器</summary>
        private IEpbCycleRunner GetRunnerOld(int channel)
        {
            // 如果已有缓存，直接返回
            if (_runnerCache.TryGetValue(channel, out var cachedRunner))
                return cachedRunner;

            // 如果已经在 _runners 字典中存在，则返回并缓存
            if (_runners.TryGetValue(channel, out var existingRunner))
            {
                _runnerCache[channel] = existingRunner;
                return existingRunner;
            }

            // 创建新的 EpbCycleRunner 实例
            var hydId = channel <= 6 ? 1 : 2;

            // 获取配置
            var rcfg = _cfg.Test?.GetEpbRunner(channel) ?? new EpbCycleRunnerConfig();
            var sampleMs = 2;

            var limitRecord = _cfg.Test.EpbLimits
                .FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            var forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA",
                "ForwardThresholdA");
            var holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");

            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = holdMs <= 0 ? 1000 : holdMs;

            // 创建新的运行器实例
            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _acq,
                _hydraulic,
                forwardA,
                holdMs,
                sampleMs,
                rcfg.PeakIgnoreMs,
                rcfg.EwmaAlpha,
                rcfg.EmptyBandA,
                rcfg.StableWinMs,
                _log,
                _cfg,
                this);

            // 缓存运行器实例
            _runnerCache[channel] = runner;

            // 同时放入 _runners 字典以保持一致性
            _runners[channel] = runner;

            return runner;
        }

        /// <summary>
        /// 获取指定通道的 EPB 循环运行器。
        /// 注意：如果命中 _runnerCache（上一次运行留下的实例），需要重新登记到 _runners，
        /// 以便采集回调 OnFastEpbCurrent 能再次把样本喂给该 Runner。
        /// </summary>
        private IEpbCycleRunner GetRunner(int channel)
        {
            // ① 缓存命中：把旧 Runner 重新放回 _runners（关键修复点）
            EpbCycleRunner cachedRunner;
            if (_runnerCache.TryGetValue(channel, out cachedRunner))
            {
                _runners[channel] = cachedRunner; // 重新登记，让 OnFastEpbCurrent 能找到它
                return cachedRunner;
            }

            // ② 正在运行表命中：也放回缓存表，保持一致性
            EpbCycleRunner existingRunner;
            if (_runners.TryGetValue(channel, out existingRunner))
            {
                _runnerCache[channel] = existingRunner;
                return existingRunner;
            }

            // ③ 都未命中：创建新 Runner（保持你现有逻辑不变，下略...）
            var hydId = channel <= 6 ? 1 : 2;
            var rcfg = _cfg.Test?.GetEpbRunner(channel) ?? new EpbCycleRunnerConfig();
            var sampleMs = 2;

            var limitRecord = _cfg.Test.EpbLimits
                .FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            var forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA", "ForwardThresholdA");
            var holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");
            holdMs = holdMs <= 0 ? 1000 : holdMs;

            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _acq,
                _hydraulic,
                forwardA,
                holdMs,
                sampleMs,
                rcfg.PeakIgnoreMs,
                rcfg.EwmaAlpha,
                rcfg.EmptyBandA,
                rcfg.StableWinMs,
                _log,
                _cfg,
                this);

            _runnerCache[channel] = runner;
            _runners[channel] = runner; // 立即登记，保证采集回调可用

            return runner;
        }



        /// <summary>获取指定通道的高精计时器（必须在 StartChannelAsync 后调用）</summary>
        private HighPrecisionTimer GetTimer(int ch, int periodMs, OverrunPolicy overrunPolicy)
        {
            // 如果已经在 _timers 字典中存在，则直接返回
            if (_timers.TryGetValue(ch, out var existingTimer))
                return existingTimer;

            /*// 暂时注释
            // 如果缓存中存在，则返回并放入 _timers 字典
            if (_timerCache.TryGetValue(ch, out var cachedTimer))
            {
                _timers[ch] = cachedTimer;
                return cachedTimer;
            }*/

            // 创建新的 HighPrecisionTimer 实例
            // 使用与 StartChannelAsync 相同的参数和创建方式
            var timer = new HighPrecisionTimer(periodMs, overrunPolicy, _log);

            // 同时放入两个字典以保持一致性
            _timers[ch] = timer;
            _timerCache[ch] = timer;

            return timer;
        }

        /// <summary>
        ///     在"压力组锚点"触发建压保持（每圈一次）。协调器内部需幂等，覆盖 0/Δ/2Δ 三波上电窗口。
        /// </summary>
        private Task HydraulicEnterAtGroupAnchorAsync(int pressureGroupId, CancellationToken token)
        {
            // 如果有液压协调器，调用其锚点进入方法
            if (_hydCoordinator != null) return _hydCoordinator.EnterElectricalPhaseAsync(pressureGroupId, token);

            // // 如果没有协调器，回退到基础的液压控制器
            // if (_hydraulic != null)
            // {
            //     _log.Info($"压力组[{pressureGroupId}] 使用基础液压控制器建压保持", "液压协调");
            //     return _hydraulic.BeginHoldAsync(pressureGroupId, token);
            // }

            // 如果连基础液压控制器都没有，记录警告并返回空任务
            _log.Warn($"压力组[{pressureGroupId}] 无可用液压控制器，跳过建压保持", "液压协调");
            return Task.CompletedTask;
        }

        #endregion
    }

    #region 对接所需接口（如果你的类型名不同，请改成你的）

    // public enum OverrunPolicy
    // {
    //     AlignToWallClock,
    //     RunToCompletionSkipMissed,
    //     SkipNextIfOverrun,
    //     Throw
    // }

    // public interface IHighPrecisionTimer
    // {
    //     /// <summary>启动周期性任务。</summary>
    //     Task StartAsync(int repeat, int startDelayMs, Func<int, CancellationToken, Task<bool>> work);
    //
    //     void Stop();
    // }

    public interface IEpbCycleRunnerOld
    {
        /// <summary>是否移除①头部等待（周期起点即进入上电相关流程）。</summary>
        bool UseNoHeadPhase { get; set; }

        /// <summary>是否在⑧尾段中扣回“相位延时 + 迟到量”。</summary>
        bool EnableTailCompensation { get; set; }

        /// <summary>⑧尾段最小时长保护（毫秒）。</summary>
        int TailMinMs { get; set; }

        /// <summary>
        ///     学习单圈（对齐外壳版）：不做①，⑧按 (tailBaseMs - phaseMs) 扣回，保证周期结束点对齐。
        /// </summary>
        Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token);

        /// <summary>
        ///     正式单圈（对齐外壳版）：不做①，⑧按 (tailBaseMs - phaseMs - lateness) 扣回，
        ///     并以 <paramref name="deadlineUtc" /> 为“硬截止”统一收尾。
        /// </summary>
        Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc,
            CancellationToken token);
    }


    /// <summary>
    ///     EPB 单通道“单圈执行 + 学习/运行外壳”接口。
    /// </summary>
    public interface IEpbCycleRunner
    {
        /// <summary>是否移除①头部等待（由外部“相位对齐”承担）。</summary>
        bool UseNoHeadPhase { get; set; }

        /// <summary>是否在⑧尾段由外壳统一扣回（相位延时 + 迟到量）。</summary>
        bool EnableTailCompensation { get; set; }

        /// <summary>⑧尾段最小时长保护（毫秒）。</summary>
        int TailMinMs { get; set; }

        /// <summary>
        ///     学习单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs)</c> 扣回（统一收尾）。
        ///     <para>注意：该方法为历史兼容入口，在“批量学习”中推荐使用 <see cref="LearnOneAlignedCoreAsync" /> + 聚合。</para>
        /// </summary>
        Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token);

        /// <summary>
        ///     正式单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs - lateness)</c> 扣回，
        ///     并以 <paramref name="deadlineUtc" /> 为“硬截止”统一收尾。
        /// </summary>
        Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc,
            CancellationToken token);

        // ===================== 批量学习（推荐） =====================

        /// <summary>
        ///     开始一轮批量学习的聚合（清空内部样本缓存）。
        ///     必须在发起多圈学习前调用一次。
        /// </summary>
        void BeginLearnAggregation();

        /// <summary>
        ///     单圈学习的“核心版本”：<b>不做①</b>、⑧不等待（交由外壳统一对齐），
        ///     返回本圈测得的各阶段耗时/电流作为样本；失败圈返回 null。
        /// </summary>
        Task<EpbCycleRunner.LearnSample> LearnOneAlignedCoreAsync(int periodMs, int tailBaseMs, int phaseMs,
            int tailMinMs, CancellationToken token);

        /// <summary>
        ///     将 <paramref name="sample" /> 并入当前聚合容器（仅在 <see cref="BeginLearnAggregation" /> 之后有效）。
        /// </summary>
        void ApplyLearnSample(EpbCycleRunner.LearnSample sample);

        /// <summary>
        ///     结束本轮聚合：将样本的统计量（建议中位数）写回 Runner 的估计字段（如 _tFwdPeakDecayMs 等）。
        /// </summary>
        void FinalizeLearnAggregation();

        Task<bool> PreReleaseAsync(int? keepMs, CancellationToken token);
        void BeginSafetyMarginLearning();
        void FinalizeSafetyMarginLearning();
    }

    #endregion
}