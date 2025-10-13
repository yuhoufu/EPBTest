using Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly Dictionary<int, EpbCycleRunner> _runnerCache = new Dictionary<int, EpbCycleRunner>();
        private readonly Dictionary<int, HighPrecisionTimer> _timerCache = new Dictionary<int, HighPrecisionTimer>();


        #region 对外主入口

        /// <summary>
        ///     批量启动 EPB 通道（学习 + 正式），每圈都对齐到“压力组锚点 + 固定相位”。
        /// </summary>
        /// <param name="channels">要启动的 EPB 通道（1..12）。例如 new[]{1,2,4,6}</param>
        /// <param name="learnCycles">自学习圈数。=0 则跳过学习。</param>
        /// <param name="token">取消令牌。</param>
        public async Task StartBatchSynchronizedAsync(int[] channels, int learnCycles, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空", nameof(channels));

            // —— 1) 按压力组归类，并为每组计算“锚点零相位”t0 —— //
            var nowUtc = DateTime.UtcNow;
            var groups = GroupByPressure(channels);
            var t0OfGroup = new Dictionary<int, DateTime>(); // key: PG(1/2), value: t0(UTC)

            foreach (var kv in groups)
            {
                var pg = kv.Key;
                var list = kv.Value;

                if (list.Count == 0) continue;
                t0OfGroup[pg] = CeilToBoundary(nowUtc.AddMilliseconds(AnchorWarmupMs), PeriodMs);
            }


            // >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>
            // 【新增】学习前批量“预释放”：三波错峰（电源组索引 0/Δ/2Δ）
            //   - 只做一次，避免每圈都反向上电带来的额外能耗/时间占用。
            //   - keepMs 传 null：各 Runner 内部使用自身 DefaultPreReleaseKeepMs。
            // >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>
            if (learnCycles > 0)
            {
                // 展平出所有参与学习/正式运行的通道（已在 groups 中）
                var allChannels = groups.Values.SelectMany(v => v).Distinct().OrderBy(x => x).ToArray();

                _log.Info($"批量预释放：通道[{string.Join(",", allChannels)}]，三波错峰，Δ={StaggerDeltaMs}ms。", "EPB");
                await PreReleaseBatchStaggeredAsync(allChannels, /*keepMs*/ null, /*deltaMs*/ StaggerDeltaMs, token)
                    .ConfigureAwait(false);
            }


            // —— 2) （可选）学习阶段：次数不多，用“每圈循环 + Task.Delay”实现同样对齐 —— //
            if (learnCycles > 0)
            {
                /*// ========== 新增：修正时间基准 ==========
                // 设置从现在开始的下一个完整周期作为基准时间
                var baseTime = DateTime.UtcNow.AddMilliseconds(500);

                // 为每个压力组设置正确的时间基准
                foreach (var pg in groups.Keys)
                {
                    t0OfGroup[pg] = baseTime;
                    _log?.Error($"压力组{pg}时间基准已设置为: {baseTime:HH:mm:ss.fff}");
                }*/
                await RunLearningPhaseAsync(groups, t0OfGroup, learnCycles, token).ConfigureAwait(false);

            }

            // —— 3) 正式阶段：为每个通道创建对齐到“锚点+相位”的高精计时器 —— //
            StartFormalPhaseTimers(groups, t0OfGroup, token);
        }

        #endregion

        #region 学习阶段（循环+延时：轻量且每圈对齐）

        /// <summary>
        ///     学习阶段：第 k 圈对齐到 t_k = t0 + k*Period，通道相位 = (ch 索引)×Δ。
        ///     每圈先 “组内建压（幂等）”，再分波次触发学习一圈。
        /// </summary>
        private async Task RunLearningPhaseAsyncOld(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            int learnCycles,
            CancellationToken token)
        {
            // —— Runner 进入“无① + ⑧扣回”模式 —— //
            foreach (var list in groups.Values.Select(v => v.OrderBy(x => x)))
            foreach (var ch in list)
                PrepareRunnerForNoHeadAndTailCompensation(ch);

            for (var k = 0; k < learnCycles; k++)
            {
                token.ThrowIfCancellationRequested();

                var tasksAllGroups = new List<Task>();

                foreach (var kv in groups)
                {
                    var pg = kv.Key;
                    var list = kv.Value;

                    if (list.Count == 0) continue;

                    var t0 = t0OfGroup[pg];
                    var tk = t0.AddMilliseconds(k * PeriodMs);
                    var enabled = list.OrderBy(x => x).ToList();

                    // —— 每圈锚点：组内建压保持（协调器内部要“幂等”） —— //
                    tasksAllGroups.Add(HydraulicEnterAtGroupAnchorAsync(pg, token));

                    // —— 分通道：按相位（0/Δ/2Δ）触发学习一圈 —— //
                    foreach (var ch in enabled)
                    {
                        var phase = IndexInPowerGroup(ch) * StaggerDeltaMs;
                        var at = tk.AddMilliseconds(phase);
                        var delay = at - DateTime.UtcNow;

                        tasksAllGroups.Add(Task.Run(async () =>
                        {
                            if (delay.TotalMilliseconds > 1)
                                await Task.Delay(delay, token).ConfigureAwait(false);

                            // 调用“单圈学习”，要求 Runner 内部不再做①头部等待，并在⑧中扣回相位
                            await GetRunner(ch).LearnOneAlignedAsync(
                                PeriodMs,
                                T8BaseMs,
                                phase,
                                T8MinMs,
                                token
                            ).ConfigureAwait(false);
                        }, token));
                    }
                }

                await Task.WhenAll(tasksAllGroups).ConfigureAwait(false);
            }
        }

        private async Task RunLearningPhaseAsync(
            Dictionary<int, List<int>> groups,
            Dictionary<int, DateTime> t0OfGroup,
            int learnCycles,
            CancellationToken token)
        {
            // —— 保护：无任务直接返回 —— //
            if (groups == null || groups.Count == 0 || learnCycles <= 0)
                return;

            // —— 1) 让所有参与 Runner 进入“无① + ⑧外壳收尾（学习不等尾）”模式，并开启聚合 —— //
            foreach (var list in groups.Values)
            {
                // 升序保证次序稳定（非必须）
                var enabled = list.OrderBy(x => x).ToList();
                for (int i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);
                    r.UseNoHeadPhase = true; // 学习不做①，错峰由外层“相位”承担
                    r.EnableTailCompensation = true; // ⑧尾部由外壳统一对齐（学习单圈不等待）
                    r.TailMinMs = T8MinMs;
                    r.BeginLearnAggregation(); // 清空本轮学习样本
                }
            }

            // —— 2) 多圈学习 —— //
            for (int k = 0; k < learnCycles; k++)
            {
                token.ThrowIfCancellationRequested();

                // 用于收集“本圈内所有组/通道”的任务（并发执行）
                var tasksAllGroups = new List<Task>();

                // 遍历每个压力组
                foreach (var kv in groups)
                {
                    var pg = kv.Key; // 压力组 ID：1 或 2
                    var list = kv.Value; // 该组启用的通道集合
                    if (list == null || list.Count == 0) continue;

                    var t0 = t0OfGroup[pg];
                    var tk = t0.AddMilliseconds(k * PeriodMs); // 本圈该压力组的锚点时刻

                    // —— 2.1) 每圈锚点：组内“建压并保持”（协调器内部要“幂等”） —— //
                    tasksAllGroups.Add(HydraulicEnterAtGroupAnchorAsync(pg, token));

                    // —— 2.2) 组内各通道：按“相位 0/Δ/2Δ”错峰触发“单圈学习核心” —— //
                    var enabled = list.OrderBy(x => x).ToList();
                    for (int i = 0; i < enabled.Count; i++)
                    {
                        var ch = enabled[i];
                        var phase = IndexInPowerGroup(ch) * StaggerDeltaMs; // 0/Δ/2Δ
                        var at = tk.AddMilliseconds(phase);
                        var delay = at - DateTime.UtcNow;

                        if (i == 0)
                        {
                            _log?.Error($"通道{ch}: tk={tk:HH:mm:ss.fff}, phase={phase}ms, at={at:HH:mm:ss.fff}, delay={delay.TotalMilliseconds}ms");
                        }

                        var runner = GetRunner(ch);

                        tasksAllGroups.Add(Task.Run(async () =>
                        {
                            // 到“锚点+相位”再起跑（每圈都对齐）
                            if (delay.TotalMilliseconds > 1)
                                await Task.Delay(delay, token).ConfigureAwait(false);

                            // —— 学习单圈核心：不做①；⑧由外壳统一对齐（此处不等待） —— //
                            var sample = await runner.LearnOneAlignedCoreAsync(
                                PeriodMs, T8BaseMs, phase, T8MinMs, token
                            ).ConfigureAwait(false);

                            // 成功圈：并入聚合；失败圈（null）——跳过
                            if (sample != null)
                                runner.ApplyLearnSample(sample);
                        }, token));
                    }
                }

                // 等待本圈所有任务结束，再进入下一圈
                await Task.WhenAll(tasksAllGroups).ConfigureAwait(false);
            }

            // —— 3) 学习聚合结束：写回中位数/统计量 —— //
            foreach (var list in groups.Values)
            {
                var enabled = list.OrderBy(x => x).ToList();
                for (int i = 0; i < enabled.Count; i++)
                {
                    var ch = enabled[i];
                    var r = GetRunner(ch);
                    r.FinalizeLearnAggregation();
                }
            }
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

        #region 可调参数（你可转为从 TestConfig 读取）

        /// <summary>每圈目标周期（毫秒）。必须与现有配置一致。</summary>
        public int PeriodMs { get; set; } = 5000;

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
        private IEpbCycleRunner GetRunner(int channel)
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
                _hydraulic,
                forwardA,
                holdMs,
                sampleMs,
                rcfg.PeakIgnoreMs,
                rcfg.EwmaAlpha,
                rcfg.EmptyBandA,
                rcfg.StableWinMs,
                _log,
                this);

            // 缓存运行器实例
            _runnerCache[channel] = runner;

            // 同时放入 _runners 字典以保持一致性
            _runners[channel] = runner;

            return runner;
        }

        /// <summary>获取指定通道的高精计时器（必须在 StartChannelAsync 后调用）</summary>
        private HighPrecisionTimer GetTimer(int ch, int periodMs, OverrunPolicy overrunPolicy)
        {
            // 如果已经在 _timers 字典中存在，则直接返回
            if (_timers.TryGetValue(ch, out var existingTimer))
                return existingTimer;

            // 如果缓存中存在，则返回并放入 _timers 字典
            if (_timerCache.TryGetValue(ch, out var cachedTimer))
            {
                _timers[ch] = cachedTimer;
                return cachedTimer;
            }

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
            if (_hydCoordinator != null)
            {
                return _hydCoordinator.EnterElectricalPhaseAsync(pressureGroupId, token);
            }

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
    /// EPB 单通道“单圈执行 + 学习/运行外壳”接口。
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
        /// 学习单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs)</c> 扣回（统一收尾）。
        /// <para>注意：该方法为历史兼容入口，在“批量学习”中推荐使用 <see cref="LearnOneAlignedCoreAsync"/> + 聚合。</para>
        /// </summary>
        Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token);

        /// <summary>
        /// 正式单圈（对齐外壳版）：<b>不做①</b>，⑧按 <c>(tailBaseMs - phaseMs - lateness)</c> 扣回，
        /// 并以 <paramref name="deadlineUtc"/> 为“硬截止”统一收尾。
        /// </summary>
        Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, DateTime deadlineUtc,
            CancellationToken token);

        // ===================== 批量学习（推荐） =====================

        /// <summary>
        /// 开始一轮批量学习的聚合（清空内部样本缓存）。
        /// 必须在发起多圈学习前调用一次。
        /// </summary>
        void BeginLearnAggregation();

        /// <summary>
        /// 单圈学习的“核心版本”：<b>不做①</b>、⑧不等待（交由外壳统一对齐），
        /// 返回本圈测得的各阶段耗时/电流作为样本；失败圈返回 null。
        /// </summary>
        Task<EpbCycleRunner.LearnSample> LearnOneAlignedCoreAsync(int periodMs, int tailBaseMs, int phaseMs,
            int tailMinMs, CancellationToken token);

        /// <summary>
        /// 将 <paramref name="sample"/> 并入当前聚合容器（仅在 <see cref="BeginLearnAggregation"/> 之后有效）。
        /// </summary>
        void ApplyLearnSample(EpbCycleRunner.LearnSample sample);

        /// <summary>
        /// 结束本轮聚合：将样本的统计量（建议中位数）写回 Runner 的估计字段（如 _tFwdPeakDecayMs 等）。
        /// </summary>
        void FinalizeLearnAggregation();

        Task<bool> PreReleaseAsync(int? keepMs, CancellationToken token);
    }

    #endregion
}