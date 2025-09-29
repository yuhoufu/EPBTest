using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using DataOperation;
using IO.NI;
using Timing;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    ///     12个卡钳统一编排：同组电控“首启”错峰（液压不延时），
    ///     每通道独立高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed class EpbManager
    {
        /// <summary>可选的圈记录器，外部在创建后赋值。</summary> // 2025.09.16 新增
        public IEpbCycleRecorder Recorder { get; set; } 

        private readonly AoController _ao;

        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly IAppLogger _log;


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();

        private readonly TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器
        private HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器

        private readonly Dictionary<int, EpbCycleRunner> _runners = new();
        private readonly long _wallBaseTicks = Stopwatch.GetTimestamp();
        private readonly DateTime _wallBaseUtc = DateTime.UtcNow;

        // 将 DateTime（采集回调给的 ts）换算为当前进程 Stopwatch Ticks
        private long ToStopwatchTicks(DateTime tsUtc)
        {
            var dtSec = (tsUtc - _wallBaseUtc).TotalSeconds;
            return _wallBaseTicks + (long)(dtSec * Stopwatch.Frequency);
        }

        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            HydraulicController hydraulic,
            EpbCycleRunner.ReadCurrentDelegate readCurrent,
            IAppLogger log = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _hydraulic = hydraulic ?? throw new ArgumentNullException(nameof(hydraulic));
            _readCurrent = readCurrent ?? (_ => 0.0);
            _log = log ?? NullLogger.Instance;

            // ★ 尝试创建协调器（此构造器拿不到 AO 和读压委托；仅当你的 HydraulicController 内部提供 BeginHold / RequestRelease 时才可用）
            //   若暂时拿不到 AO/读压，可先传 null；协调器将使用 HydraulicController 的保持/释放能力。
            try
            {
                _hydCoordinator = new HydraulicGroupCoordinator(
                    _cfg.Test,
                    _cfg.DO,
                    _do,
                    null, // 没有也没关系：协调器优先走 HydraulicController 的保持模式
                    null, // 无 AO 也可：不会走 Fallback
                    _hydraulic,
                    _log);
            }
            catch (Exception ex)
            {
                _log.Warn($"液压组协调器初始化（构造器1）失败：{ex.Message}。将跳过“组内统一释压”增强逻辑。", "液压协调");
                _hydCoordinator = null;
            }
        }

        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            AoController aoController,
            TwoDeviceAiAcquirer acq,
            IAppLogger log = null)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (doController == null) throw new ArgumentNullException(nameof(doController));
            if (aoController == null) throw new ArgumentNullException(nameof(aoController));
            if (acq == null) throw new ArgumentNullException(nameof(acq));

            _cfg = cfg;
            _do = doController;
            _ao = aoController;
            //_readCurrent = acq.ReadCurrent;
            _readCurrent = acq.ReadCurrentFast;
            _log = log ?? NullLogger.Instance;
            _acq = acq;

            // —— 订阅“低时延电流样本”并转发给对应 Runner —— //
            _acq.OnFastEpbCurrent += (ch, amps, ts) =>
            {
                if (_runners.TryGetValue(ch, out var r))
                {
                    var tick = ToStopwatchTicks(ts.ToUniversalTime());
                    r.FeedCurrentSample(ch, tick, amps);
                }
            };

            _hydraulic = new HydraulicController(
                _do,
                _cfg.Test,
                acq.ReadPressure,
                aoController,
                _log);

            // ★ 创建协调器（具备 AO 与读压，可用 Fallback/保持两种实现）
            _hydCoordinator = new HydraulicGroupCoordinator(
                _cfg.Test,
                _cfg.DO,
                _do,
                acq.ReadPressure,
                aoController,
                _hydraulic,
                _log);
        }

        /// <summary>
        ///     读取指定液压组的压力值（委托给 HydraulicController）
        /// </summary>
        /// <param name="hydId"></param>
        /// <returns></returns>
        private double ReadPressure(int hydId)
        {
            return _acq.ReadPressure(hydId);
        }

        // EpbManager.cs 里（EpbManager 类内）新增：
        public Task HydraulicEnterAsync(int channel, CancellationToken token)
        {
            return _hydCoordinator?.EnterElectricalPhaseAsync(channel, token) ?? Task.CompletedTask;
        }


        /// <summary>
        /// 释放液压
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public Task HydraulicMarkReleaseAsync(int channel)
        {
            return _hydCoordinator?.MarkVoltageReleaseAsync(channel) ?? Task.CompletedTask;
        }
        
        // （保留你已有的 StartChannelAsync / Pause/Resume/Stop 等实现，不改对外签名）
        public async Task StartChannelAsync(int channel, CancellationToken uiToken = default)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            var hydId = channel <= 6 ? 1 : 2;

            //var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            var rcfg = _cfg.Test?.GetEpbRunner(channel) ?? new EpbCycleRunnerConfig();
            var periodMs = _cfg.Test.PeriodMs;
            var sampleMs = 2;

            var limitRecord = _cfg.Test.EpbLimits
                .FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            var forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA",
                "ForwardThresholdA");
            var holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");
            
            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = (holdMs <= 0) ? 1000 : holdMs;  // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

            var staggerMs = 0;
            foreach (var g in _cfg.Test.Groups)
                if (g.Members.Contains(channel))
                {
                    var indexInGroup = g.Members.OrderBy(x => x).ToList().IndexOf(channel);

                    staggerMs = g.StaggerMs * Math.Max(0, indexInGroup);
                    _log.Info($"EPB[{channel}] 归属组 {g.Id} 首启错峰 {staggerMs}ms（组内位置={indexInGroup}）", "EPB");
                    break;
                }

            //日志记录周期
            _log.Info($"高精度定时器，  EPB[{channel}] 周期 {periodMs}ms，采样 {sampleMs}ms，前进阈值 {forwardA}A，保持时间 {holdMs}ms",
                "EPB");

            var timer = new HighPrecisionTimer(periodMs, _cfg.Test.OverrunPolicy, _log);
            _timers[channel] = timer;


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

            // —— 新增：登记 Runner —— //
            _runners[channel] = runner;

            var learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5;

            if (learnCycles > 0)
            {
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
                    await runner.LearnAsync(learnCycles, uiToken, periodMs).ConfigureAwait(false);
                    _log.Info($"EPB[{channel}] 自学习完成，进入正式试验。", "EPB");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"EPB[{channel}] 自学习异常：{ex.Message}，仍将尝试进入正式试验。", "EPB");
                }
            }
           
            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");


                // —— 新增：圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）
                Recorder?.BeginCycle(channel, i, DateTime.UtcNow);


                var ok = await runner.RunOneAsync(periodMs, token).ConfigureAwait(false);
                
                // —— 新增：圈结束（取本圈累计样本数做 finalN；若 Recorder 为 null 则 finalN=0）
                var finalN = Recorder?.GetCurrentCycleSampleCount(channel) ?? 0;
                Recorder?.CompleteCycle(channel, i, finalN, DateTime.UtcNow);

                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }


        /// <summary>
        /// 并发自学习（同组内按索引错峰） + 正式试验同步起跑（同组内按索引错峰、组间同锚点）。
        /// 适用场景：同一电源组的卡钳同时上电可能过流，因此需要在“学习阶段”和“首个周期”都做组内错峰；
        /// 不同电源组之间无需错峰，采用同一启动锚点对齐。
        /// </summary>
        /// <param name="channels">要运行的 EPB 通道号集合（例如 new[]{1,2,7,8}）。</param>
        /// <param name="uiToken">UI 层传入的取消令牌。</param>
        /// <param name="abortAllIfAnyLearnFailed">
        /// 当任一通道学习失败时是否整体中止（true）；
        /// 若为 false（默认），则跳过失败通道，其他通道继续进入正式试验。
        /// </param>
        /// <exception cref="ArgumentException">当未提供任何通道时抛出。</exception>
        /// <remarks>
        /// 规则摘要：
        /// - 学习阶段：按“组”为单位执行；对“本次被选中且属于该组”的成员，按通道号升序编号 i=0..n-1；
        ///   然后对每个成员延时 i * StaggerMs 后触发 LearnAsync（同组错峰，不同组可并行）。
        /// - 正式试验阶段：全局计算 Anchor（当前时间 + 2s，向上对齐到 PeriodMs 边界）；
        ///   首次触发时间 = Anchor + i * StaggerMs（i 为该通道在其所属组内的索引）。不同组共享同一 Anchor。
        /// - 仅对“首个周期”应用错峰，后续周期按统一 PeriodMs 运行，不再扩大相位差。
        /// </remarks>
        public async Task StartChannelsSynchronizedPowerAwareAsync(
            IEnumerable<int> channels,
            CancellationToken uiToken = default,
            bool abortAllIfAnyLearnFailed = false)
        {
            if (channels == null || !channels.Any())
                throw new ArgumentException("必须至少指定一个通道。", nameof(channels));

            var selected = channels.Distinct().OrderBy(x => x).ToList();
            int periodMs = Math.Max(1, _cfg.Test?.PeriodMs ?? 5000);

            // 1) 预准备：为每个通道构建 Runner 与高精度定时器（不启动）
            foreach (var ch in selected)
            {
                if (_timers.ContainsKey(ch)) // 已在运行则不重复准备
                {
                    _log.Warn($"EPB[{ch}] 已在运行，跳过重复准备。", "EPB");
                    continue;
                }
                await PrepareRunnerAndTimerAsync(ch, uiToken).ConfigureAwait(false);
            }

            // 2) —— 学习阶段（同组内错峰，不同组并行） —— //
            // 2.1 建立“通道 -> 组”映射
            //var groups = _cfg.Test?.Groups ?? Array.Empty<ElectricalGroup>();
            // 也可直接返回空 List，保证两边同为 List<ElectricalGroup>
            var groups = _cfg.Test?.Groups ?? new List<Config.ElectricalGroup>();
            // 但前提是 _cfg.Test?.Groups 的静态类型是 List<Config.ElectricalGroup> 或者能隐式转换到 List<>

            var groupByChannel = MapChannelToGroup(groups);

            // 2.2 计算“组内索引”：仅对“本次被选中 ∩ 该组成员”的通道，按升序编号 i=0..m-1
            var indexInGroup = ComputeIndexInGroup(selected, groupByChannel);

            // 2.3 构建学习任务：对每个通道，延时 i * StaggerMs 后执行 LearnAsync
            var learnTasks = new List<Task<(int ch, bool ok, Exception ex)>>();
            foreach (var ch in selected)
            {
                if (!_runners.TryGetValue(ch, out var runner))
                {
                    // 未能准备成功的通道，标记学习失败
                    learnTasks.Add(Task.FromResult((ch, false, new Exception("Runner 未创建"))));
                    continue;
                }

                var rcfg = _cfg.Test?.GetEpbRunner(ch) ?? new EpbCycleRunnerConfig();
                int learnCycles = (int)(rcfg.LearnCycles > 0 ? rcfg.LearnCycles : 5);

                // 该通道所属组的错峰间隔（若没分组或未配置则视为 0）
                var grp = groupByChannel.TryGetValue(ch, out var g) ? g : null;
                int staggerMs = grp != null ? Math.Max(0, grp.StaggerMs) : 0;
                int idx = indexInGroup.TryGetValue(ch, out var i) ? i : 0;
                int delayMs = idx * staggerMs;

                learnTasks.Add(StartOneLearnWithDelayAsync(ch, runner, learnCycles, periodMs, delayMs, uiToken));
            }

            var learnResults = await Task.WhenAll(learnTasks).ConfigureAwait(false);

            // 2.4 汇总学习结果
            var failed = learnResults.Where(r => !r.ok).Select(r => (r.ch, r.ex)).ToList();
            if (failed.Count > 0)
            {
                foreach (var (ch, ex) in failed)
                    _log.Warn($"EPB[{ch}] 学习失败：{ex?.Message ?? "未知错误"}。", "EPB");

                if (abortAllIfAnyLearnFailed)
                {
                    _log.Warn("检测到学习失败，按策略：整体中止。", "EPB");
                    return;
                }
            }

            // 保留学习成功的通道进入正式试验
            var toRun = learnResults.Where(r => r.ok).Select(r => r.ch).OrderBy(x => x).ToList();
            if (toRun.Count == 0)
            {
                _log.Warn("无可运行通道（学习阶段全部失败或被剔除）。", "EPB");
                return;
            }

            // 3) —— 正式试验阶段：统一锚点 + 组内索引错峰（仅首周期） —— //
            var anchorUtc = ComputeAlignedAnchorUtc(periodMs, secondsAhead: 2);

            foreach (var ch in toRun)
            {
                if (!_timers.TryGetValue(ch, out var timer) || !_runners.TryGetValue(ch, out var runner))
                {
                    _log.Warn($"EPB[{ch}] 定时器或 Runner 未创建，跳过启动。", "EPB");
                    continue;
                }

                var grp = groupByChannel.TryGetValue(ch, out var g) ? g : null;
                int staggerMs = grp != null ? Math.Max(0, grp.StaggerMs) : 0;
                int idx = indexInGroup.TryGetValue(ch, out var i) ? i : 0;

                // 首次触发：Anchor + i * StaggerMs
                var firstUtc = anchorUtc.AddMilliseconds(idx * staggerMs);
                int initialDelayMs = (int)Math.Max(0, (firstUtc - DateTime.UtcNow).TotalMilliseconds);

                string grpName = $"Group_{grp.Id}";
                _log.Info($"EPB[{ch}] 起跑：Group={grpName}, Index={idx}, StaggerMs={staggerMs}, " +
                          $"Anchor={anchorUtc:HH:mm:ss.fff}Z, InitialDelay={initialDelayMs}ms, Period={periodMs}ms。", "EPB");

                int totalCycles = _cfg.Test.TestTarget;

                timer.StartAsync(totalCycles, initialDelayMs, async (cycleIndex, token) =>
                {
                    Recorder?.BeginCycle(ch, cycleIndex, DateTime.UtcNow);
                    var ok = await runner.RunOneAsync(periodMs, token).ConfigureAwait(false);
                    var finalN = Recorder?.GetCurrentCycleSampleCount(ch) ?? 0;
                    Recorder?.CompleteCycle(ch, cycleIndex, finalN, DateTime.UtcNow);
                    return ok;
                });
            }
        }

        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause();
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
        }

        public void StopChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Stop();
            _timers.Remove(channel);

            // —— 新增：移除 Runner —— //
            _runners.Remove(channel);

            // —— 新增：停止时强制把最近 N=10 圈落盘（含常开圈0）
            Recorder?.FlushRecent(channel, 10);

            _do.SetEpbOff(channel); // 安全落位
        }

        public void StopAll()
        {
            foreach (var ch in _timers.Keys.ToArray()) StopChannel(ch);
        }

        // —— 反射兜底读取配置字段（兼容不同旧配置命名）—— //
        private static T GetProp<T>(object obj, string name)
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null) return default;
            var v = p.GetValue(obj);
            if (v == null) return default;
            return (T)Convert.ChangeType(v, typeof(T));
        }

        private static T GetProp<T>(object obj, params string[] tryNames)
        {
            foreach (var n in tryNames)
            {
                var p = obj.GetType().GetProperty(n);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                return (T)Convert.ChangeType(v, typeof(T));
            }

            return default;
        }


        // —— 圈开始（如仍保留该方法供其他调用）
        private void OnCycleBegin(int epbId, int cycleNumber)
        {
            Recorder?.BeginCycle(epbId, cycleNumber, DateTime.UtcNow);
        }

        // —— 圈结束
        private void OnCycleComplete(int epbId, int cycleNumber)
        {
            var finalN = Recorder?.GetCurrentCycleSampleCount(epbId) ?? 0;
            Recorder?.CompleteCycle(epbId, cycleNumber, finalN, DateTime.UtcNow);
        }


        #region —— 私有辅助：学习延时、锚点、索引等 ——

        /// <summary>
        /// 延时后启动单通道学习。
        /// </summary>
        private async Task<(int ch, bool ok, Exception ex)> StartOneLearnWithDelayAsync(
            int channel,
            EpbCycleRunner runner,
            int learnCycles,
            int periodMs,
            int delayMs,
            CancellationToken token)
        {
            try
            {
                if (delayMs > 0)
                {
                    _log.Info($"EPB[{channel}] 学习延时 {delayMs}ms（组内错峰）。", "EPB");
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }

                _log.Info($"EPB[{channel}] 开始学习（{learnCycles} 次）。", "EPB");
                bool ok = await runner.LearnAsync(learnCycles, token, periodMs).ConfigureAwait(false);
                _log.Info($"EPB[{channel}] 学习 {(ok ? "完成" : "失败")}。", "EPB");

                // 返回带名字的元组：ch、ok、ex（学习成功时 ex 为 null）
                return (channel, ok, ok ? null : new Exception("LearnAsync 返回 false"));
            }
            catch (OperationCanceledException oce)
            {
                _log.Warn($"EPB[{channel}] 学习被取消：{oce.Message}", "EPB");
                return (channel, false, oce);
            }
            catch (Exception ex)
            {
                return (channel, false, ex);
            }
        }

        /// <summary>
        /// 构建“通道 -> 组”的映射。若通道未出现在任何组中，则不加入映射（视作独立组）。
        /// </summary>
        private static Dictionary<int, ElectricalGroup> MapChannelToGroup(IEnumerable<ElectricalGroup> groups)
        {
            var map = new Dictionary<int, ElectricalGroup>();
            foreach (var g in groups ?? Array.Empty<ElectricalGroup>())
            {
                if (g?.Members == null) continue;
                foreach (var ch in g.Members)
                {
                    // 若一个通道在多个组中，只保留第一次出现（配置应避免重复归属）
                    if (!map.ContainsKey(ch))
                        map[ch] = g;
                }
            }
            return map;
        }

        /// <summary>
        /// 计算“组内索引”：对“本次被选中 ∩ 该组成员”的通道，按通道号升序编号 i=0..n-1。
        /// 未分组通道的索引为 0。
        /// </summary>
        private static Dictionary<int, int> ComputeIndexInGroup(
            IList<int> selected,
            Dictionary<int, ElectricalGroup> groupByChannel)
        {
            var result = new Dictionary<int, int>();

            // 先按“组”分类
            var buckets = new Dictionary<ElectricalGroup?, List<int>>();
            foreach (var ch in selected)
            {
                groupByChannel.TryGetValue(ch, out var grp); // grp 可能为 null（无组）
                if (!buckets.TryGetValue(grp, out var list))
                {
                    list = new List<int>();
                    buckets[grp] = list;
                }
                list.Add(ch);
            }

            // 每个桶内部按升序重新编号
            foreach (var kv in buckets)
            {
                var list = kv.Value.OrderBy(x => x).ToList();
                for (int i = 0; i < list.Count; i++)
                    result[list[i]] = i;
            }

            return result;
        }

        /// <summary>
        /// 计算“现在 + secondsAhead”后向上对齐到 PeriodMs 边界的 UTC 锚点。
        /// </summary>
        private static DateTime ComputeAlignedAnchorUtc(int periodMs, int secondsAhead)
        {
            var nowUtc = DateTime.UtcNow;
            var baseUtc = nowUtc.AddSeconds(secondsAhead);

            // 以 Unix Epoch 做整数对齐，减少多定时器首发相位误差
            long msFromEpoch = (long)(baseUtc - new DateTime(1970, 1, 1)).TotalMilliseconds;
            long aligned = ((msFromEpoch + periodMs - 1) / periodMs) * periodMs;
            return new DateTime(1970, 1, 1).AddMilliseconds(aligned);
        }

        /// <summary>
        /// 为单通道准备 Runner 与 Timer（仅构建，不启动）。复用原有解析逻辑。
        /// </summary>
        private async Task PrepareRunnerAndTimerAsync(int channel, CancellationToken uiToken)
        {
            if (_timers.ContainsKey(channel)) return;

            var hydId = channel <= 6 ? 1 : 2;
            var rcfg = _cfg.Test?.GetEpbRunner(channel) ?? new EpbCycleRunnerConfig();
            var periodMs = Math.Max(1, _cfg.Test?.PeriodMs ?? 5000);
            var sampleMs = 2;

            var limitRecord = _cfg.Test.EpbLimits.FirstOrDefault(x => GetProp<int>(x, "Channel") == channel)
                               ?? throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            var forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA", "ForwardThresholdA");
            var holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");
            if (holdMs <= 0) holdMs = 1000;

            var timer = new HighPrecisionTimer(periodMs, _cfg.Test.OverrunPolicy, _log);
            _timers[channel] = timer;

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

            _runners[channel] = runner;

            await Task.CompletedTask;
        }

        #endregion



    }
}