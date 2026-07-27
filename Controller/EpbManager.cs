using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Config.Models;
using Controller.Adaptive;
using Controller.Alarm;
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
    public sealed partial class EpbManager
    {
        /// <summary>可选的圈记录器，外部在创建后赋值。</summary>
        /// // 2025.09.16 新增
        public IEpbCycleRecorder Recorder { get; set; }

        /// <summary>可选：报警管理器（M-7055D/RS-485），由 UI 初始化后注入。</summary>
        public AlarmManager Alarm { get; set; }

        /// <summary>可选：报警配置（用于 Runner 报警阈值/报警快照参数），由 UI 初始化后注入。</summary>
        public AlarmConfig AlarmConfig { get; set; }

        /// <summary>事件：某个通道触发了报警。</summary>
        public event Action<int, string> ChannelAlarmRaised;

        /// <summary>事件：某个通道产生软预警；不停止通道、不触发蜂鸣器。</summary>
        public event Action<int, string> ChannelWarningRaised;

        /// <summary>事件：某个通道被暂停。</summary>
        public event Action<int> ChannelPaused;

        /// <summary>事件：某个通道恢复运行。</summary>
        public event Action<int> ChannelResumed;

        private readonly TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器

        private readonly AoController _ao;

        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly IAppLogger _log;

        private readonly SafetyMarginControlMode _safetyMarginControlMode;
        private readonly EpbControlMode _epbControlMode;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveProfileStore _adaptiveProfileStore;
        private readonly HashSet<int> _adaptiveChannels;


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;

        private readonly Dictionary<int, EpbCycleRunner> _runners = new();
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();
        private readonly long _wallBaseTicks = Stopwatch.GetTimestamp();
        private readonly DateTime _wallBaseUtc = DateTime.UtcNow;
        private readonly HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器

        private readonly SemaphoreSlim _alarmSnapshotGate = new(1, 1);
        private readonly Dictionary<int, DateTime> _lastAlarmSnapshotUtcByChannel = new();

        // 报警触发“立即停机”去重：避免同一通道短时间内重复 Stop
        private readonly ConcurrentDictionary<int, byte> _alarmStopRequested = new();

        // ★ 跟踪每个通道“当前已 BeginCycle 的圈号”：用于报警停机时把当前圈封为 status='alarm'，避免遗留 running 悬挂圈
        private readonly ConcurrentDictionary<int, int> _currentCycleNumberByChannel = new();

        // 通道级“硬停机”取消源：用于中断当前圈内仍在运行的异步流程（Delay/等待判据等）
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _stopCtsByChannel = new();

        // ★ 当前仍参与“液压组判定”的通道集合：用于把“报警停机/提前结束”的通道排除出释压条件
        // 说明：
        // - 批量对齐启动中，液压建压锚点每圈会对“参与通道”调用 EnterElectricalPhaseAsync 并登记 InFlight。
        // - 若某通道报警停机或提前结束，但仍被重复登记进 InFlight，则会阻塞其它正常通道到达“电压释放点”后的统一释压。
        // - 因此这里维护一个并发集合，确保每圈只登记“仍在跑/仍参与本轮判定”的通道。
        private readonly ConcurrentDictionary<int, byte> _hydraulicParticipants = new();

        /// <summary>
        ///     将指定通道标记为“参与液压判定”。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     该集合用于批量对齐启动的“建压锚点”过滤：只有参与者才会被登记进
        ///     <see cref="HydraulicGroupCoordinator"/> 的 InFlight，从而避免报警停机/提前结束通道影响释压条件。
        /// </remarks>
        private void MarkHydraulicParticipant(int channel)
        {
            _hydraulicParticipants[channel] = 0;
        }

        /// <summary>
        ///     将指定通道从“参与液压判定”集合中移除。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     触发场景：
        ///     <list type="bullet">
        ///         <item>报警触发快速停机；</item>
        ///         <item>人工停止；</item>
        ///         <item>该通道自然完成全部圈数（批量模式下各通道圈数可能不同）。</item>
        ///     </list>
        ///     移除后，该通道不会再被纳入后续每圈的 InFlight 登记，因此不会阻塞其它正常通道的释压。
        /// </remarks>
        private void UnmarkHydraulicParticipant(int channel)
        {
            _hydraulicParticipants.TryRemove(channel, out _);
        }

        /// <summary>
        ///     判断指定通道是否仍参与液压判定。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private bool IsHydraulicParticipant(int channel)
        {
            return _hydraulicParticipants.ContainsKey(channel);
        }


        /// <summary>
        ///     判断指定通道是否已进入“报警停机”流程（用于圈结账时写入 <c>status='alarm'</c>）。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <returns>若该通道已触发报警停机则返回 true，否则返回 false。</returns>
        private bool IsAlarmStopRequested(int channel)
        {
            return _alarmStopRequested.ContainsKey(channel);
        }


        /// <summary>
        ///     记录通道“当前圈号”（BeginCycle 后调用），用于报警停机时封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <param name="cycleNumber">当前圈号（与 Recorder.BeginCycle 一致）。</param>
        private void MarkCurrentCycleNumber(int channel, int cycleNumber)
        {
            _currentCycleNumberByChannel[channel] = cycleNumber;
        }


        /// <summary>
        ///     清除通道“当前圈号”（Complete/Alarm 封圈后调用），避免后续误封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void ClearCurrentCycleNumber(int channel)
        {
            _currentCycleNumberByChannel.TryRemove(channel, out _);
        }


        /// <summary>
        ///     在报警停机路径中，尝试把“当前圈”封为 <c>status='alarm'</c>。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     线程模型：可在报警事件回调线程/后台任务中调用；内部不抛异常（仅 best-effort）。
        ///     <para>
        ///     设计目的：保证 UI(EpbTestRecord) 计数与落盘圈数一致，避免 BeginCycle 后未 Complete 导致的“running 悬挂圈”。
        ///     </para>
        /// </remarks>
        private void TryFinalizeCurrentCycleAsAlarm(int channel)
        {
            var recorder = Recorder;
            if (recorder == null) return;

            if (!_currentCycleNumberByChannel.TryGetValue(channel, out var cycleNumber))
                return;

            try
            {
                var finalN = recorder.GetCurrentCycleSampleCount(channel);
                recorder.AlarmCycle(channel, cycleNumber, finalN, DateTime.UtcNow);
            }
            catch
            {
                // ignore
            }
            finally
            {
                ClearCurrentCycleNumber(channel);
            }
        }


        /// <summary>
        ///     通道“自然完成全部圈数”后的统一收尾：
        ///     <list type="number">
        ///         <item>将通道从“液压判定参与者”集合中移除，避免影响其它通道释压；</item>
        ///         <item>将通道从运行字典（Timer/Runner）中移除，避免被误判为仍在运行；</item>
        ///         <item>执行安全落位：断电 + 请求液压释放；</item>
        ///         <item>导出最近10圈（FlushRecent），满足“停止即存最近10圈”的现场要求。</item>
        ///     </list>
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     该方法用于两种运行模式：
        ///     <list type="bullet">
        ///         <item>单通道 StartChannelAsync 的自然完成；</item>
        ///         <item>批量对齐启动（BatchStart）中某通道 runs 不一致导致的提前完成。</item>
        ///     </list>
        ///     注意：这里不会调用 Timer.Stop()；因为调用时机在“最后一圈回调”内，计时器即将自然退出。
        /// </remarks>
        private void FinalizeChannelAfterNaturalCompletion(int channel)
        {
            // 1) 先从液压判定参与者中移除
            UnmarkHydraulicParticipant(channel);

            // 2) 取消并释放硬停机 CTS（该通道已完成）
            try { CancelStopCts(channel); } catch { /* ignore */ }

            // 3) 从运行表移除：避免后续逻辑（例如“导出所有运行通道”）误把它当成仍在运行
            try { _timers.Remove(channel); } catch { /* ignore */ }
            try { _timerCache.Remove(channel); } catch { /* ignore */ }

            if (_runners.TryGetValue(channel, out var runnerObj))
            {
                try
                {
                    runnerObj.ChannelCycleCompleted -= OnRunnerChannelCycleCompleted;
                    runnerObj.AlarmRaised -= OnRunnerAlarmRaised;
                    runnerObj.WarningRaised -= OnRunnerWarningRaised;
                }
                catch
                {
                    // ignore
                }

                try { _runners.Remove(channel); } catch { /* ignore */ }
            }

            try { _runnerCache.Remove(channel); } catch { /* ignore */ }

            // 4) 安全落位：断电 + 请求液压释放
            try { _do.SetEpbOff(channel); } catch { /* ignore */ }
            try { _ = HydraulicMarkReleaseAsync(channel); } catch { /* ignore */ }

            // 5) 停止即存最近10圈：不阻塞当前线程
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    _ = Task.Run(() =>
                    {
                        try { recorder.FlushRecent(channel, 10); } catch { /* ignore */ }
                    });
            }
            catch
            {
                // ignore
            }

            TryEndBatchSessionWhenIdle();
        }

        /// <summary>
        /// 为指定通道创建新的“硬停机”取消源；若已存在则先取消并释放旧实例。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <returns>新的取消源实例。</returns>
        private CancellationTokenSource RenewStopCts(int channel)
        {
            if (_stopCtsByChannel.TryRemove(channel, out var old))
            {
                try { old.Cancel(); } catch { /* ignore */ }
                try { old.Dispose(); } catch { /* ignore */ }
            }

            var cts = new CancellationTokenSource();
            _stopCtsByChannel[channel] = cts;
            return cts;
        }

        /// <summary>
        /// 取消并移除指定通道的“硬停机”取消源。
        /// </summary>
        private void CancelStopCts(int channel)
        {
            if (_stopCtsByChannel.TryRemove(channel, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                try { cts.Dispose(); } catch { /* ignore */ }
            }
        }


        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            AoController aoController,
            TwoDeviceAiAcquirer acq,
            IAppLogger log = null,
            SafetyMarginControlMode safetyMarginControlMode = SafetyMarginControlMode.Legacy20251010,
            EpbControlMode epbControlMode = EpbControlMode.LegacyFixedTiming,
            bool adaptiveShadowMode = true)
        {
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController ?? throw new ArgumentNullException(nameof(aoController));
            _acq = acq ?? throw new ArgumentNullException(nameof(acq));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

            _do = doController;
            _ao = aoController;
            //_readCurrent = acq.ReadCurrent;
            _readCurrent = acq.ReadCurrentFast;
            _log = log ?? NullLogger.Instance;
            _acq = acq;

            _safetyMarginControlMode = safetyMarginControlMode;
            _epbControlMode = ReadEpbControlMode(epbControlMode);
            _adaptiveShadowMode = ReadAdaptiveShadowMode(adaptiveShadowMode);
            _adaptiveChannels = ReadAdaptiveChannels();

            try
            {
                var projectConfigDir = ConfigLoader.GetProjectConfigDir(cfg.Test.StoreDir, cfg.Test.TestName);
                _adaptiveProfileStore = new EpbAdaptiveProfileStore(projectConfigDir, _log);
                _log.Info(
                    $"EPB 控制模式={_epbControlMode}，灰度通道={string.Join(",", _adaptiveChannels)}，" +
                    $"影子判定={_adaptiveShadowMode}，模型={_adaptiveProfileStore.FilePath}",
                    "EPB");
            }
            catch (Exception ex)
            {
                _log.Warn($"初始化 EPB 自适应模型存储失败，将使用内存空模型：{ex.Message}", "EPB");
            }

            // 从cfg中获取控制参数；
            PeriodMs = cfg.Test.PeriodMs; // 周期时长
            TestCycle = cfg.Test.TestTarget; // 总周期数

            // 添加每个epb通道的目标次数
            foreach (var epbRecord in cfg.Test.EpbRecords)
            {

                EpbTestCycle!.Add(epbRecord.Id,epbRecord.TotalCount - epbRecord.RunCount);  // 需要能够每次开始由总次数-已运行次数
                
            }
            



            // —— 订阅“低时延电流样本”并转发给对应 Runner —— //
            _acq.OnFastEpbCurrent += (ch, amps, ts) =>
            {
                if (_runners.TryGetValue(ch, out var r))
                {
                    var tick = ToStopwatchTicks(ts.ToUniversalTime());
                    r.FeedCurrentSample(ch, tick, amps);
                }
            };

            #region 写盘批次桥接：TwoDeviceAiAcquirer → IEpbCycleRecorder

            // —— 写盘批次桥接：TwoDeviceAiAcquirer → IEpbCycleRecorder —— //
            _acq.OnDiskBatch += (device, tsUtc, currentsByEpb, pressureGroup1, pressureGroup2) =>
            {
                var recorder = Recorder;
                if (recorder == null) return;
                if (tsUtc == null || tsUtc.Length == 0) return;
                if (currentsByEpb == null || currentsByEpb.Count == 0) return;

                foreach (var kvp in currentsByEpb)
                {
                    var epbId = kvp.Key; // 1..12
                    var currents = kvp.Value; // 电流数组
                    if (currents == null || currents.Length == 0)
                        continue;

                    // === ★ 电流取绝对值（不修改原数组，避免影响其它模块） ===
                    var absCurrents = new double[currents.Length];
                    for (int i = 0; i < currents.Length; i++)
                    {
                        absCurrents[i] = Math.Abs(currents[i]);
                    }

                    // 1..6 → 压力组1，7..12 → 压力组2
                    double[] pressure = null;
                    if (epbId >= 1 && epbId <= 6)
                        pressure = pressureGroup1;
                    else if (epbId >= 7 && epbId <= 12)
                        pressure = pressureGroup2;

                    // 没有压力时，用 0 填充数组，仍然让电流落盘
                    if (pressure == null || pressure.Length == 0)
                        pressure = new double[currents.Length];

                    // 对齐长度：取三者最小值
                    var n = Math.Min(tsUtc.Length, Math.Min(absCurrents.Length, pressure.Length));
                    if (n <= 0)
                        continue;

                    if (n == tsUtc.Length && n == absCurrents.Length && n == pressure.Length)
                    {
                        recorder.WriteBatch(epbId, tsUtc, absCurrents, pressure);
                    }
                    else
                    {
                        var tsBuf = new DateTime[n];
                        var curBuf = new double[n];
                        var prBuf = new double[n];

                        Array.Copy(tsUtc, tsBuf, n);
                        Array.Copy(absCurrents, curBuf, n);
                        Array.Copy(pressure, prBuf, n);

                        recorder.WriteBatch(epbId, tsBuf, curBuf, prBuf);
                    }
                }
            };


            #endregion


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

        // 将 DateTime（采集回调给的 ts）换算为当前进程 Stopwatch Ticks
        private long ToStopwatchTicks(DateTime tsUtc)
        {
            var dtSec = (tsUtc - _wallBaseUtc).TotalSeconds;
            return _wallBaseTicks + (long)(dtSec * Stopwatch.Frequency);
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
        ///     释放液压
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

            // 若上一次因报警触发过停机，这里允许重新启动
            _alarmStopRequested.TryRemove(channel, out _);

            // 本次启动为该通道刷新“硬停机”取消源
            var stopCts = RenewStopCts(channel);

            var hydId = channel <= 6 ? 1 : 2;

            //var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            var rcfg = _cfg.Test?.EpbCycleRunner.GetRunnerChannel(channel);


            var periodMs = _cfg.Test.PeriodMs;
            var sampleMs = 2;


            var forwardA = rcfg.ForwardA;
            var holdMs = rcfg.HoldMs;

            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = holdMs <= 0 ? 1000 : holdMs; // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

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

            // 标记为“参与液压判定”（用于后续批量建压锚点过滤；单通道模式也保持一致）
            MarkHydraulicParticipant(channel);

            // 峰值超限报警增量阈值（可配；<=0 表示禁用）
            var overshootDeltaA = 0.0;
            try
            {
                var m = AlarmConfig?.Mappings?.Epb?.FirstOrDefault(x => x.Channel == channel);
                overshootDeltaA = m?.OvershootAlarmDeltaA ?? AlarmConfig?.Behavior?.OvershootAlarmDeltaA ?? 0.0;
            }
            catch
            {
                overshootDeltaA = 0.0;
            }


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
                _log,
                _cfg,
                this,
                overshootAlarmDeltaA: overshootDeltaA,
                safetyMarginControlMode: _safetyMarginControlMode,
                epbControlMode: GetEpbControlMode(channel),
                adaptiveShadowMode: _adaptiveShadowMode,
                adaptiveProfile: GetAdaptiveProfile(channel),
                saveAdaptiveProfile: SaveAdaptiveProfile);

            runner.AlarmRaised += OnRunnerAlarmRaised;
            runner.WarningRaised += OnRunnerWarningRaised;

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

            _ = timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopCts.Token);
                var ct = linked.Token;

                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");


                // —— 圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）——
                Recorder?.BeginCycle(channel, i, DateTime.UtcNow);
                MarkCurrentCycleNumber(channel, i);

                var ok = false;
                try
                {
                    ok = await runner.RunOneAsync(periodMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ok = false;
                }
                catch
                {
                    ok = false;
                }

                // —— 圈结束：根据是否报警停机决定封圈状态 ——
                var recorder = Recorder;
                if (recorder != null)
                {
                    try
                    {
                        var finalN = recorder.GetCurrentCycleSampleCount(channel);
                        if (IsAlarmStopRequested(channel) ||
                            runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.HardFault)
                            recorder.AlarmCycle(channel, i, finalN, DateTime.UtcNow);
                        else if (runner.LastCycleOutcome.IsSuccess)
                            recorder.CompleteCycle(channel, i, finalN, DateTime.UtcNow);
                        else
                            recorder.AbortCycle(
                                channel,
                                i,
                                finalN,
                                DateTime.UtcNow,
                                runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.Canceled
                                    ? "canceled"
                                    : "failed");
                    }
                    catch
                    {
                        // ignore
                    }
                }

                ClearCurrentCycleNumber(channel);

                // 若本通道自然完成最后一圈，则做统一收尾（含“停止即存最近10圈”）
                if (i >= _cfg.Test.TestTarget)
                    FinalizeChannelAfterNaturalCompletion(channel);

                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }

        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause();
            ChannelPaused?.Invoke(channel);
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
            ChannelResumed?.Invoke(channel);
        }


        /// <summary>
        /// 停止指定通道：
        /// 1) 停止并移除当前轮正在使用的计时器；
        /// 2) 同时清理计时器缓存（不再复用旧实例）；
        /// 3) 移除运行器与其缓存；
        /// 4) 落位并做必要的收尾。
        /// </summary>
        public void StopChannel(int channel)
        {
            // 该通道停止后不再参与液压判定
            UnmarkHydraulicParticipant(channel);

            // 先取消“硬停机”Token，尽快中断当前圈内仍在运行的异步逻辑
            try { CancelStopCts(channel); } catch { /* ignore */ }

            // —— 停止“当前轮”的计时器 —— //
            HighPrecisionTimer t;
            if (_timers.TryGetValue(channel, out t))
            {
                try
                {
                    t.Stop();
                }
                catch
                {
                    /* 忽略 Stop 异常 */
                }

                _timers.Remove(channel);
            }

            // —— 同步清理“缓存计时器”，只 Stop + Remove，不做 Dispose（类型未实现 IDisposable）—— //
            HighPrecisionTimer cached;
            if (_timerCache.TryGetValue(channel, out cached))
            {
                try
                {
                    cached.Stop();
                }
                catch
                {
                    /* 忽略 */
                }

                _timerCache.Remove(channel); // 关键：不要留下以免二次启动被误复用
            }

            // —— 安全落位（优先）：尽快断电并请求液压释放 —— //
            try
            {
                _do.SetEpbOff(channel);
            }
            catch
            {
                /* 忽略 */
            }

            try
            {
                _ = HydraulicMarkReleaseAsync(channel);
            }
            catch
            {
                /* 忽略 */
            }

            // —— Runner 同样清理：运行表与缓存表都移除 —— //
            EpbCycleRunner runnerObj;
            if (_runners.TryGetValue(channel, out runnerObj))
            {
                // 退出前解绑事件，防止潜在内存泄漏
                runnerObj.ChannelCycleCompleted -= OnRunnerChannelCycleCompleted;
                runnerObj.AlarmRaised -= OnRunnerAlarmRaised;
                runnerObj.WarningRaised -= OnRunnerWarningRaised;

                _runners.Remove(channel);
            }

            _runnerCache.Remove(channel);

            // —— 收尾：落盘导出（Stop 场景保留原逻辑）—— //
            try
            {
                Recorder?.FlushRecent(channel, 10);
            }
            catch
            {
                /* 忽略 */
            }

            TryEndBatchSessionWhenIdle();
        }


        /// <summary>
        /// 报警触发时的“快速停机”：
        /// - 立即停止该通道计时器（不再进入下一圈）；
        /// - 立即断电并请求液压释放；
        /// - 清理 Runner 引用，避免采集回调继续喂样本；
        /// - 不做任何耗时导出（快照导出在报警链路中单独处理）。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void StopChannelOnAlarm(int channel)
        {
            // 报警停机：立即从“参与液压判定”集合中移除，防止其阻塞其它通道释压
            UnmarkHydraulicParticipant(channel);

            // 先取消“硬停机”Token，尽快中断当前圈内仍在运行的异步逻辑
            try { CancelStopCts(channel); } catch { /* ignore */ }

            // ★关键：在报警停机路径里尽早把“当前圈”封为 alarm，避免 FlushRecent 时看不到该圈/或遗留 running 悬挂圈
            try { TryFinalizeCurrentCycleAsAlarm(channel); } catch { /* ignore */ }

            // —— 停止“当前轮”的计时器 —— //
            if (_timers.TryGetValue(channel, out var t))
            {
                try { t.Stop(); } catch { /* ignore */ }
                _timers.Remove(channel);
            }

            // —— 同步清理“缓存计时器” —— //
            if (_timerCache.TryGetValue(channel, out var cached))
            {
                try { cached.Stop(); } catch { /* ignore */ }
                _timerCache.Remove(channel);
            }

            // —— 安全落位：立即断电 + 请求液压释放 —— //
            try { _do.SetEpbOff(channel); } catch { /* ignore */ }
            try { _ = HydraulicMarkReleaseAsync(channel); } catch { /* ignore */ }

            // —— 清理 Runner（避免继续喂样本/回调）—— //
            if (_runners.TryGetValue(channel, out var runnerObj))
            {
                try
                {
                    runnerObj.ChannelCycleCompleted -= OnRunnerChannelCycleCompleted;
                    runnerObj.AlarmRaised -= OnRunnerAlarmRaised;
                    runnerObj.WarningRaised -= OnRunnerWarningRaised;
                }
                catch
                {
                    // ignore
                }

                _runners.Remove(channel);
            }

            _runnerCache.Remove(channel);

            // —— 现场要求：停止即存最近10圈 ——
            // 说明：报警停机路径不应阻塞 Runner/定时器线程，因此这里用后台任务异步 Flush。
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    _ = Task.Run(() =>
                    {
                        try { recorder.FlushRecent(channel, 10); } catch { /* ignore */ }
                    });
            }
            catch
            {
                // ignore
            }

            TryEndBatchSessionWhenIdle();
        }


        private void OnRunnerAlarmRaised(int channel, string reason)
        {
            // ★同步去重 latch：保证计时器回调能尽快识别“本圈应封为 alarm”，但不在此线程做 IO
            if (!_alarmStopRequested.TryAdd(channel, 0))
                return;

            ChannelAlarmRaised?.Invoke(channel, reason);

            // 不阻塞 Runner/定时器线程
            _ = Task.Run(async () =>
            {
                try { StopChannelOnAlarm(channel); } catch { /* ignore */ }

                try
                {
                    if (Alarm != null)
                        await Alarm.SetAlarmAsync(channel, true, reason).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    await ExportAlarmSnapshotAsync(channel, reason).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            });
        }

        private void OnRunnerWarningRaised(int channel, string reason)
        {
            _log.Warn($"EPB[{channel}] 自适应软预警：{reason}", "EPB");
            try { ChannelWarningRaised?.Invoke(channel, reason); }
            catch { /* UI 订阅者异常不得影响控制线程 */ }
        }

        private EpbAdaptiveProfile GetAdaptiveProfile(int channel)
        {
            try
            {
                return _adaptiveProfileStore?.GetOrCreate(channel) ??
                       new EpbAdaptiveProfile { Channel = channel };
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{channel}] 加载自适应模型失败，使用空模型：{ex.Message}", "EPB");
                return new EpbAdaptiveProfile { Channel = channel };
            }
        }

        private void SaveAdaptiveProfile(EpbAdaptiveProfile profile)
        {
            if (profile == null || _adaptiveProfileStore == null) return;
            try
            {
                _adaptiveProfileStore.Save(profile);
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{profile.Channel}] 保存自适应模型失败：{ex.Message}", "EPB");
            }
        }

        private EpbControlMode ReadEpbControlMode(EpbControlMode fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbControlMode"];
                return EpbControlModeParser.ParseOrDefault(raw, fallback);
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbControlMode 失败，使用 {fallback}：{ex.Message}", "EPB");
                return fallback;
            }
        }

        private bool ReadAdaptiveShadowMode(bool fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbAdaptiveShadowMode"];
                return bool.TryParse(raw, out var enabled) ? enabled : fallback;
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbAdaptiveShadowMode 失败，使用 {fallback}：{ex.Message}", "EPB");
                return fallback;
            }
        }

        private HashSet<int> ReadAdaptiveChannels()
        {
            var channels = new HashSet<int>();
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbAdaptiveChannels"] ?? "10";
                foreach (var token in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token.Trim(), out var channel) && channel >= 1 && channel <= 12)
                        channels.Add(channel);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbAdaptiveChannels 失败，回退 EPB10：{ex.Message}", "EPB");
            }

            if (channels.Count == 0) channels.Add(10);
            return channels;
        }

        private EpbControlMode GetEpbControlMode(int channel)
        {
            return _epbControlMode == EpbControlMode.AdaptiveCurrent && _adaptiveChannels.Contains(channel)
                ? EpbControlMode.AdaptiveCurrent
                : EpbControlMode.LegacyFixedTiming;
        }


        private async Task ExportAlarmSnapshotAsync(int alarmChannel, string reason)
        {
            var recorder = Recorder;
            if (recorder == null) return;

            // 快照去抖：同一通道在 cooldown 内只导出一次
            var cooldownMs = AlarmConfig?.Behavior?.SnapshotCooldownMs ?? 2000;
            var now = DateTime.UtcNow;
            lock (_lastAlarmSnapshotUtcByChannel)
            {
                if (_lastAlarmSnapshotUtcByChannel.TryGetValue(alarmChannel, out var last))
                {
                    if ((now - last).TotalMilliseconds < cooldownMs)
                        return;
                }

                _lastAlarmSnapshotUtcByChannel[alarmChannel] = now;
            }

            await _alarmSnapshotGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lastN = AlarmConfig?.Behavior?.SnapshotLastNCycles ?? 10;
                lastN = Math.Max(1, lastN);

                // 根目录：StoreDir\TestName\AlarmSnapshots
                var baseDir = System.IO.Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "AlarmSnapshots");
                System.IO.Directory.CreateDirectory(baseDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var snapshotDir = System.IO.Path.Combine(baseDir, $"{stamp}-EPB{alarmChannel:D2}");
                System.IO.Directory.CreateDirectory(snapshotDir);

                int[] running;
                try
                {
                    running = _timers.Keys.ToArray();
                }
                catch
                {
                    running = Array.Empty<int>();
                }

                // 可能在报警回调里已先 StopChannelOnAlarm 导致 _timers 不再包含报警通道；
                // 但快照必须包含报警通道本身，因此这里补回。
                if (!running.Contains(alarmChannel))
                    running = running.Concat(new[] { alarmChannel }).ToArray();

                foreach (var ch in running)
                {
                    var subName = ch == alarmChannel ? $"EPB{ch:D2}_ALARM" : $"EPB{ch:D2}";
                    var subDir = System.IO.Path.Combine(snapshotDir, subName);
                    System.IO.Directory.CreateDirectory(subDir);

                    try
                    {
                        recorder.FlushRecentTo(ch, lastN, subDir, includeRunningCycle: true);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"报警快照导出失败：EPB[{ch}] {ex.Message}", "落盘");
                    }
                }

                _log.Warn($"报警快照已导出：EPB[{alarmChannel}] {reason} -> {snapshotDir}", "落盘");
            }
            finally
            {
                _alarmSnapshotGate.Release();
            }
        }


        /// <summary>
        /// 停止所有通道：依次调用 <see cref="StopChannel"/> ，
        /// 并做一次兜底清空，确保下一次开始是“干净环境”。 
        /// </summary>
        public void StopAll()
        {
            // 学习阶段尚未创建通道 Timer 时，也必须能通过控制层自己的 CTS 停止。
            EndBatchSession(cancel: true);

            var keys = _timers.Keys.ToArray(); // 拷贝快照，避免枚举期间修改
            for (int i = 0; i < keys.Length; i++)
                StopChannel(keys[i]);

            // 兜底清空（防御式）
            _timers.Clear();
            _runnerCache.Clear();
            _timerCache.Clear();
            _runners.Clear();
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
        ///     延时后启动单通道学习。
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
                var ok = await runner.LearnAsync(learnCycles, token, periodMs).ConfigureAwait(false);
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
        ///     构建“通道 -> 组”的映射。若通道未出现在任何组中，则不加入映射（视作独立组）。
        /// </summary>
        private static Dictionary<int, ElectricalGroup> MapChannelToGroup(IEnumerable<ElectricalGroup> groups)
        {
            var map = new Dictionary<int, ElectricalGroup>();
            foreach (var g in groups ?? Array.Empty<ElectricalGroup>())
            {
                if (g?.Members == null) continue;
                foreach (var ch in g.Members)
                    // 若一个通道在多个组中，只保留第一次出现（配置应避免重复归属）
                    if (!map.ContainsKey(ch))
                        map[ch] = g;
            }

            return map;
        }

        /// <summary>
        ///     计算“组内索引”：对“本次被选中 ∩ 该组成员”的通道，按通道号升序编号 i=0..n-1。
        ///     未分组通道的索引为 0。
        /// </summary>
        private static Dictionary<int, int> ComputeIndexInGroup(
            IList<int> selected,
            Dictionary<int, ElectricalGroup> groupByChannel)
        {
            var result = new Dictionary<int, int>();

            // 先按“组”分类：无组通道单独一个桶（避免使用可空引用类型注解）
            var buckets = new Dictionary<ElectricalGroup, List<int>>();
            var ungrouped = new List<int>();
            foreach (var ch in selected)
            {
                if (groupByChannel.TryGetValue(ch, out var grp) && grp != null)
                {
                    if (!buckets.TryGetValue(grp, out var list))
                    {
                        list = new List<int>();
                        buckets[grp] = list;
                    }

                    list.Add(ch);
                }
                else
                {
                    ungrouped.Add(ch);
                }
            }

            // 每个桶内部按升序重新编号
            foreach (var kv in buckets)
            {
                var list = kv.Value.OrderBy(x => x).ToList();
                for (var i = 0; i < list.Count; i++)
                    result[list[i]] = i;
            }

            // 无组通道索引统一为 0
            foreach (var ch in ungrouped)
                result[ch] = 0;

            return result;
        }

        /// <summary>
        ///     计算“现在 + secondsAhead”后向上对齐到 PeriodMs 边界的 UTC 锚点。
        /// </summary>
        private static DateTime ComputeAlignedAnchorUtc(int periodMs, int secondsAhead)
        {
            var nowUtc = DateTime.UtcNow;
            var baseUtc = nowUtc.AddSeconds(secondsAhead);

            // 以 Unix Epoch 做整数对齐，减少多定时器首发相位误差
            var msFromEpoch = (long)(baseUtc - new DateTime(1970, 1, 1)).TotalMilliseconds;
            var aligned = (msFromEpoch + periodMs - 1) / periodMs * periodMs;
            return new DateTime(1970, 1, 1).AddMilliseconds(aligned);
        }

        #endregion


        #region 卡钳预释放

        /// <summary>
        /// 批量执行“预释放”（反向进入空行程并保持）。
        /// </summary>
        /// <param name="channels">要执行预释放的通道号（1..12）。</param>
        /// <param name="keepMs">
        /// 反向空行程保持时长（毫秒）。为 <c>null</c> 时，每个通道使用其 Runner 的默认值
        ///（通常来自配置字段 <c>_revEmptyKeepMs</c>）。
        /// </param>
        /// <param name="token">取消令牌。</param>
        /// <returns>全部通道任务完成的 <see cref="Task"/>。</returns>
        /// <remarks>
        /// - 默认并发执行全部通道的预释放。若你希望遵守“电源组错峰”，可以按 IndexInPowerGroup 分三波执行。<br/>
        /// - 该方法仅做“学习前的姿态归零”，不做液压建压/释压；正式流程仍由“每圈锚点”统一控制。
        /// </remarks>
        public async Task PreReleaseBatchAsync(int[] channels, int? keepMs, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            // 并发跑每个通道的预释放
            var tasks = new List<Task>();
            var enabled = channels.Distinct().OrderBy(x => x).ToArray();

            foreach (var ch in enabled)
            {
                var runner = GetRunner(ch); // 你在 BatchStart.cs 中实现的对接

                // 若 keepMs==null，runner 内部会使用 DefaultPreReleaseKeepMs
                tasks.Add(runner.PreReleaseAsync(keepMs, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// （可选增强）按“电源组相位 0/Δ/2Δ”三波错峰执行批量预释放。
        /// 当你担心同时反向上电电流过大时使用。
        /// </summary>
        public async Task PreReleaseBatchStaggeredAsync(int[] channels, int? keepMs, int deltaMs,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();

            // 三个相位桶：索引 0：1/4/7/10；索引 1：2/5/8/11；索引 2：3/6/9/12
            var buckets = new[] { new List<int>(), new List<int>(), new List<int>() };
            for (int i = 0; i < enabled.Length; i++)
            {
                var ch = enabled[i];
                var idx = IndexInPowerGroup(ch);
                buckets[idx].Add(ch);
            }

            var t0 = DateTime.UtcNow.AddMilliseconds(500); // 给 500ms 预热时间（可按需调整）

            for (int phaseIdx = 0; phaseIdx < 3; phaseIdx++)
            {
                var bucket = buckets[phaseIdx];
                if (bucket.Count == 0) continue;

                var at = t0.AddMilliseconds(phaseIdx * deltaMs);
                var delay = at - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 1)
                    await Task.Delay(delay, token).ConfigureAwait(false);

                var tasks = new List<Task>();
                for (int j = 0; j < bucket.Count; j++)
                {
                    var ch = bucket[j];
                    var runner = GetRunner(ch);
                    
                    tasks.Add(runner.PreReleaseAsync(keepMs, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        // 你已有的工具：电源组索引（1/4/7/10→0；2/5/8/11→1；3/6/9/12→2）
        private static int IndexInPowerGroup(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch - 1) % 3;
        }

        #endregion
    }
}
