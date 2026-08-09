using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Configuration;
using System.Runtime.CompilerServices;
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
    internal sealed class DaqRecoveryAttemptWindow
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Queue<long>> _attempts =
            new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
        private readonly int _maximumAttempts;
        private readonly long _windowTicks;

        public DaqRecoveryAttemptWindow(int maximumAttempts, TimeSpan window)
        {
            _maximumAttempts = Math.Max(1, maximumAttempts);
            _windowTicks = Math.Max(1L, (long)Math.Round(
                Math.Max(1, window.TotalSeconds) * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero));
        }

        public bool TryRegister(string device, long nowTicks, out int attemptsInWindow)
        {
            lock (_gate)
            {
                if (!_attempts.TryGetValue(device ?? string.Empty, out var queue))
                {
                    queue = new Queue<long>();
                    _attempts[device ?? string.Empty] = queue;
                }
                var cutoff = nowTicks - _windowTicks;
                while (queue.Count > 0 && queue.Peek() <= cutoff) queue.Dequeue();
                if (queue.Count >= _maximumAttempts)
                {
                    attemptsInWindow = queue.Count;
                    return false;
                }
                queue.Enqueue(nowTicks);
                attemptsInWindow = queue.Count;
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate) _attempts.Clear();
        }
    }

    /// <summary>
    ///     12个卡钳统一编排：同组电控“首启”错峰（液压不延时），
    ///     每通道独立高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed partial class EpbManager
    {
        /// <summary>可选的圈记录器，外部在创建后赋值。</summary>
        /// // 2025.09.16 新增
        private IEpbCycleRecorder _recorder;
        public IEpbCycleRecorder Recorder
        {
            get => _recorder;
            set
            {
                _recorder = value;
                if (value is IActiveCycleLimitConfigurator configurable && _acq != null)
                {
                    var periodMs = Math.Max(1, _cfg?.Test?.PeriodMs ?? 1);
                    var maxRecords = (int)Math.Ceiling(
                        Math.Max(1.0, _acq.SampleRate) * periodMs / 1000.0 * 1.25);
                    configurable.SetMaxActiveCycleRecords(maxRecords);
                    _log?.Info(
                        $"活动圈样本硬上限已设置：{maxRecords} 条（采样率={_acq.SampleRate:F1}Hz，周期={periodMs}ms，裕量=1.25）。",
                        "落盘");
                }
            }
        }

        /// <summary>可选：报警管理器（M-7055D/RS-485），由 UI 初始化后注入。</summary>
        public AlarmManager Alarm { get; set; }

        /// <summary>可选：报警配置（用于 Runner 报警阈值/报警快照参数），由 UI 初始化后注入。</summary>
        public AlarmConfig AlarmConfig { get; set; }

        /// <summary>事件：某个通道触发了报警。</summary>
        public event Action<int, string> ChannelAlarmRaised;

        /// <summary>事件：某个通道产生软预警；不停止通道、不触发蜂鸣器。</summary>
        public event Action<int, string> ChannelWarningRaised;

        /// <summary>不可自恢复报警要求当前项目永久取消该通道启用。</summary>
        public event Action<int, string> ChannelDisableRequested;

        /// <summary>通道已停机但项目禁用状态写盘失败，UI必须高可见度提示。</summary>
        public event Action<int, string> ChannelDisablePersistenceFailed;

        /// <summary>事件：某个通道被暂停。</summary>
        public event Action<int> ChannelPaused;

        /// <summary>事件：某个通道恢复运行。</summary>
        public event Action<int> ChannelResumed;

        /// <summary>程控电源遥测更新；订阅者不得阻塞控制线程。</summary>
        public event Action<PowerSupplyTelemetry> PowerSupplyTelemetryUpdated;

        /// <summary>程控电源组级硬故障。</summary>
        public event Action<PowerSupplyFault> PowerSupplyFaultRaised;

        /// <summary>结构化控制故障；共享资源故障会携带完整受影响成员。</summary>
        public event Action<ControlFault> ControlFaultRaised;
        /// <summary>
        /// 应用级共享系统故障；由主程序执行安全自重启。独立DAQ软件故障走设备组
        /// 自维护，不得发布到此事件，也不得进入物理报警链路。
        /// </summary>
        public event Action<ControlFault> SystemFaultRaised;
        /// <summary>
        /// 在人工停止、关闭程序或硬件故障执行任何取消/断电动作前同步发布。
        /// 订阅者只能执行短小、可靠的持久化操作（例如撤销无人值守续测授权）。
        /// </summary>
        public event Action<StopContext> RunAuthorizationRevoking;
        public event Action<DaqRecoveryResult> DaqRecoveryStateChanged;
        public event Action<DaqPersistenceStateChanged> DaqPersistenceStateChanged;
        public event Action<PressureQualification> PressureQualificationChanged;
        public event Action<ChannelRuntimeStateChangedEvent> ChannelRuntimeStateChanged;

        /// <summary>
        /// 人工复位指定电源组的故障锁存。只有输出已关闭、保护已解除且身份校验通过时才会成功；
        /// 下次启动仍执行完整预检。
        /// </summary>
        public Task ResetPowerSupplyFaultAsync(int electricalGroupId, CancellationToken token = default)
        {
            if (_powerSupply == null)
                throw new InvalidOperationException("程控电源控制未初始化。");
            return _powerSupply.ResetFaultAsync(electricalGroupId, token);
        }

        private readonly TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器

        private readonly AoController _ao;

        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly IAppLogger _log;
        private readonly IPowerSupplyCoordinator _powerSupply;
        private readonly bool _requirePowerSupply;
        private PowerSupplyTelemetryCsvRecorder _powerTelemetryRecorder;
        private readonly TaskSupervisor _taskSupervisor;
        private long _energizedChannelsMask;
        private readonly long _dev1ChannelMask;
        private readonly long _dev2ChannelMask;
        private readonly ChannelRuntimeStateStore _channelRuntimeStateStore = new();

        private readonly SafetyMarginControlMode _safetyMarginControlMode;
        private readonly EpbControlMode _epbControlMode;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveProfileStore _adaptiveProfileStore;
        private readonly EpbProgramSafetySettings _programSafetySettings;
        private readonly HashSet<int> _adaptiveChannels;


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;

        private readonly ChannelRuntimeStore<EpbCycleRunner> _runnerRuntime = new();
        private readonly ChannelRuntimeStore<HighPrecisionTimer> _timerRuntime = new();
        private ConcurrentDictionary<int, EpbCycleRunner> _runners => _runnerRuntime.Active;
        private ConcurrentDictionary<int, HighPrecisionTimer> _timers => _timerRuntime.Active;
        private readonly long _wallBaseTicks = Stopwatch.GetTimestamp();
        private readonly HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器

        private readonly SemaphoreSlim _alarmSnapshotGate = new(1, 1);
        private readonly Dictionary<int, DateTime> _lastAlarmSnapshotUtcByChannel = new();
        private readonly FaultConfirmationTracker _faultConfirmationTracker = new();
        private readonly ConcurrentDictionary<int, long> _currentAttemptIdByChannel = new();
        private readonly ConcurrentDictionary<int, int> _frozenFaultCycleByChannel = new();
        private long _cycleAttemptSequence;

        // 报警触发“立即停机”去重：同一次运行只处理首个报警；新运行必须显式复位
        private readonly ChannelAlarmStopLatch _alarmStopLatch = new();
        private readonly DaqIncidentLatch _daqIncidentLatch = new();
        private readonly ConcurrentDictionary<int, byte> _manualStopRequestedChannels = new();
        private readonly ConcurrentDictionary<int, byte> _nonRecoverableChannelFaultLatch = new();
        private readonly ConcurrentDictionary<int, CancellationTokenSource>
            _recoverableChannelRestartJobs = new();

        private void FlushPersistentLog(bool durable = true)
        {
            try
            {
                if (_log is IAsyncFlushableAppLogger asynchronous)
                    asynchronous.RequestFlush(durable);
                else
                    (_log as IFlushableAppLogger)?.Flush(durable);
            }
            catch
            {
                // 日志刷新失败不得回流控制链路。
            }
        }

        public IReadOnlyList<ChannelRuntimeStateChangedEvent> GetChannelRuntimeStates()
        {
            return _channelRuntimeStateStore.Snapshot();
        }

        internal static bool ShouldPreserveDaqRecoveringState(
            ChannelRuntimeState requestedState,
            bool daqRecoveryActive)
        {
            return daqRecoveryActive &&
                   (requestedState == ChannelRuntimeState.Running ||
                    requestedState == ChannelRuntimeState.WarningRunning);
        }

        private void PublishChannelRuntimeState(
            int channel,
            ChannelRuntimeState state,
            string reasonCode,
            string reasonText,
            int? sourceChannel = null,
            IEnumerable<int> affectedChannels = null,
            Guid correlationId = default,
            bool allowTerminalReset = false,
            bool allowSystemFaultReset = false,
            Guid runIdOverride = default)
        {
            // 物理安全目标可以包含同组全部硬件成员，但禁用通道不属于当前运行状态机。
            // 这是中央不变量：任何故障、恢复或迟到事件都不能把 Enabled=false 污染为
            // Paused/Recovering/Alarm。硬件 OFF 证据仍由 DO 追踪单独保留。
            var normalizedState = NormalizeRuntimeStateForEnabled(IsChannelEnabled(channel), state);
            if (normalizedState != state)
            {
                state = normalizedState;
                reasonCode = "DisabledChannelInvariant";
                reasonText = "通道未启用；忽略组级故障或恢复状态污染。";
                sourceChannel = null;
                affectedChannels = new[] { channel };
                correlationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
                allowTerminalReset = true;
                allowSystemFaultReset = true;
            }
            // DAQ恢复拥有受影响通道的状态机，直至恢复终态提交并移除上下文。
            // 圈尾软预警/旧Runner回调不得把“系统自恢复”覆盖回“运行/软预警”。
            if (state == ChannelRuntimeState.Running ||
                state == ChannelRuntimeState.WarningRunning)
            {
                var device = _acq.GetDeviceForEpbChannel(channel);
                DaqAutoRecoveryContext recovery = null;
                var recoveryActive = !string.IsNullOrWhiteSpace(device) &&
                                     _daqAutoRecovery.TryGetValue(device, out recovery) &&
                                     recovery.Terminal.Current == DaqRecoveryTerminal.None;
                if (ShouldPreserveDaqRecoveringState(state, recoveryActive))
                {
                    state = ChannelRuntimeState.Recovering;
                    reasonCode = "DaqRecoveryActive";
                    reasonText = "DAQ软件恢复尚未进入终态；忽略旧运行状态更新。";
                    affectedChannels = recovery.AffectedChannels;
                    correlationId = recovery.CorrelationId;
                    allowTerminalReset = false;
                    allowSystemFaultReset = false;
                }
            }
            var previous = _channelRuntimeStateStore.Get(channel);
            var update = _channelRuntimeStateStore.Publish(
                new ChannelRuntimeStateChangedEvent
                {
                    Channel = channel,
                    State = state,
                    ReasonCode = reasonCode ?? string.Empty,
                    ReasonText = reasonText ?? string.Empty,
                    SourceChannel = sourceChannel,
                    AffectedChannels = (affectedChannels ?? new[] { channel }).Distinct().ToArray(),
                    TimestampUtc = DateTime.UtcNow,
                    CorrelationId = correlationId,
                    RunId = runIdOverride == Guid.Empty ? _activeBatchId : runIdOverride,
                    RunEpoch = Interlocked.Read(ref _runEpoch),
                    Enabled = IsChannelEnabled(channel),
                    FormalPhaseCommitted = IsFormalPhaseCommitted,
                    TimerActive = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel),
                    RunnerActive = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel),
                    Energized = IsChannelEnergized(channel)
                },
                allowTerminalReset,
                allowSystemFaultReset);
            if (previous == null ||
                previous.State != update.State ||
                !string.Equals(previous.ReasonCode, update.ReasonCode, StringComparison.Ordinal) ||
                previous.RunId != update.RunId ||
                previous.CorrelationId != update.CorrelationId)
                LogFieldRuntimeStateMetric(update);
            NonCriticalObserver.Invoke(
                ChannelRuntimeStateChanged,
                update,
                ex => _log?.Warn($"通道状态观察者异常已隔离：{ex.Message}", "EPB"));
        }

        private void PublishFaultRuntimeStates(ControlFault fault, int? sourceChannel)
        {
            if (fault == null) return;
            var active = (fault.AffectedChannels ?? Array.Empty<int>())
                .Where(channel =>
                    channel == sourceChannel ||
                    _timers.ContainsKey(channel) ||
                    _runners.ContainsKey(channel) ||
                    IsHydraulicParticipant(channel))
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            foreach (var channel in active)
                PublishChannelRuntimeState(
                    channel,
                    channel == sourceChannel
                        ? ChannelRuntimeState.AlarmStopped
                        : ChannelRuntimeState.InterlockStopped,
                    fault.Code,
                    fault.Reason,
                    sourceChannel,
                    active,
                    fault.CorrelationId);
        }

        private void WriteRecorderBatchSafely(
            IEpbCycleRecorder recorder,
            int epbId,
            DateTime[] timestampsUtc,
            double[] currents,
            double[] pressures)
        {
            try
            {
                recorder.WriteBatch(epbId, timestampsUtc, currents, pressures);
            }
            catch (ActiveCycleDataLimitExceededException ex)
            {
                // 先触发控制层安全停机；封存、快照和 UI 通知均在其后异步执行。
                OnRunnerAlarmRaised(epbId, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{epbId}] 写盘失败：{ex.Message}", "落盘", ex);
                NonCriticalObserver.Invoke(
                    SnapshotExportFailed,
                    ex.Message,
                    observerEx => _log?.Warn(
                        $"快照失败观察者异常已隔离：{observerEx.Message}",
                        "落盘"));
            }
        }

        // ★ 跟踪每个通道“当前已 BeginCycle 的圈号”：用于报警停机时把当前圈封为 status='alarm'，避免遗留 running 悬挂圈
        private readonly ConcurrentDictionary<int, int> _currentCycleNumberByChannel = new();
        private readonly ConcurrentDictionary<int, byte> _alarmCycleFinalizationRetries = new();

        // 通道级“硬停机”取消源：用于中断当前圈内仍在运行的异步流程（Delay/等待判据等）
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _stopCtsByChannel = new();
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _cyclePauseCtsByChannel = new();
        private readonly EmergencyPowerGroupLatch _emergencyPowerGroupLatch = new();
        private readonly ConcurrentDictionary<int, byte> _powerSoftwareRecoveryGroups = new();
        private readonly ConcurrentDictionary<int, byte> _hydraulicSoftwareRecoveryGroups = new();
        private readonly ConcurrentDictionary<string, byte> _softwareRecoveryCircuitDiagnostics =
            new(StringComparer.Ordinal);

        // ★ 当前仍参与“液压组判定”的通道集合：用于把“报警停机/提前结束”的通道排除出释压条件
        // 说明：
        // - 批量对齐启动中，液压建压锚点每圈会对“参与通道”调用 EnterElectricalPhaseAsync 并登记 InFlight。
        // - 若某通道报警停机或提前结束，但仍被重复登记进 InFlight，则会阻塞其它正常通道到达“电压释放点”后的统一释压。
        // - 因此这里维护一个并发集合，确保每圈只登记“仍在跑/仍参与本轮判定”的通道。
        private readonly ConcurrentDictionary<int, byte> _hydraulicParticipants = new();
        // participant 版本与通道锁用于拒绝迟到的旧恢复清理。旧代只能移除它在
        // 截止开始时看到的 participant，不能删除后来共同重入的新代状态。
        private readonly ConcurrentDictionary<int, long> _hydraulicParticipantVersions = new();
        private readonly ConcurrentDictionary<int, object> _hydraulicParticipantGates = new();
        private long _hydraulicParticipantVersionSequence;
        // 恢复通道在未来正式槽重新加入时，只有从该槽开始才可进入液压成员快照。
        // 不能只用全局 participant 布尔集合，否则新成员会污染仍在执行的上一槽。
        private readonly ConcurrentDictionary<int, long> _firstEligibleFormalSlotByChannel = new();
        private readonly object[] _formalRejoinGates =
            { new object(), new object(), new object() };
        private readonly ConcurrentDictionary<int, HydraulicChannelLeaseScope> _hydraulicLeaseByChannel = new();
        private long _singleHydraulicGeneration;
        private long _startupPositioningGeneration;
        private readonly ConcurrentDictionary<string, int> _daqRecoveryAttemptsByDevice = new();
        private readonly DaqPersistenceCoordinator _persistence;
        private readonly int _daqPersistenceQueueCapacity;
        private readonly int _daqPersistencePauseDepth;
        private readonly int _daqPersistenceResumeDepth;
        private readonly double _daqPersistencePauseAgeMs;
        private readonly double _daqPersistenceResumeAgeMs;
        private readonly int _daqPersistenceRecoveryTimeoutMs;
        private readonly int _daqPersistenceRequiredFreshBatches;
        private readonly int _daqClockRecoveryFreshBatches;
        private readonly int _daqClockRecoveryMaxAttempts;
        private readonly int _daqClockRecoveryWindowMinutes;
        private readonly DaqRecoveryAttemptWindow _daqClockRecoveryAttempts;
        private readonly IDaqHardwareProbe _daqHardwareProbe;
        private readonly ConcurrentDictionary<string, DaqAutoRecoveryContext> _daqAutoRecovery =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Guid, DateTime> _daqRecoveryTerminalCorrelations = new();
        private readonly object _daqRecoveryCommitGate = new();
        private long _runEpoch;
        private long _recoveryEpoch;
        private readonly ConcurrentDictionary<long, byte> _daqClockAbortedCycles = new();
        private readonly object _stopSafetyGate = new();
        private Task<StopSafetyResult> _stopSafetyTask;
        private StopSource _stopSafetyTaskSource = StopSource.UnknownLegacy;
        private StopSafetyResult _lastStopSafetyResult;

        private sealed class DaqAutoRecoveryContext
        {
            public string Device;
            public Guid RunId;
            public Guid CorrelationId;
            public DateTime StartedUtc;
            public DateTime CutoffUtc;
            public int[] AffectedChannels;
            public int[] PreviouslyRunningChannels;
            public int[] PreviouslyActiveChannels;
            public string TriggerCode;
            public int RestartDaq;
            public int Completing;
            public readonly DaqRecoveryTerminalGate Terminal = new DaqRecoveryTerminalGate();
            public int SnapshotSequence;
            public readonly ConcurrentDictionary<string, byte> SnapshotPhases =
                new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            public int RecoveryAttempt;
            public int MaintenanceScheduled;
            public readonly RecoveryFailureBackoffState FailureBackoff =
                new RecoveryFailureBackoffState();
            public readonly object ValidationFailureLogGate = new object();
            public long LastValidationFailureLogTicks;
            public string LastValidationFailureSignature;
            public long RunEpoch;
            public long RecoveryEpoch;
            public string TriggerReason;
            public int RecoverableAlarmChannel;
            public int RaiseRecoverableAlarm;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly TaskCompletionSource<DaqRecoveryResult> Completion =
                new TaskCompletionSource<DaqRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            public long PreviousGeneration;
            public long RecoveredGeneration;
            public long FirstVerifiedSequence;
            public long LastVerifiedSequence;
            public DaqFreshnessSnapshot BeforeClock;
            public DaqFreshnessSnapshot AfterClock;
            public string ValidationPhase;
            public HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[] Ownerships;
            public CancellationTokenRegistration[] OwnershipCancellationRegistrations;
            public int OwnershipReleased;
            public readonly DaqRecoveryPhaseGate Phase = new DaqRecoveryPhaseGate();
            public Dictionary<int, long> CutoffParticipantVersions;
            public Dictionary<int, int> CutoffCycles;
            public long CutoffPersistenceBoundary;
            public int CutoffCyclesFinalized;
        }

        internal static ChannelRuntimeState NormalizeRuntimeStateForEnabled(
            bool enabled,
            ChannelRuntimeState requested)
        {
            return enabled ? requested : ChannelRuntimeState.NotEnabled;
        }

        private static long DaqAbortedCycleKey(int channel, int cycleNumber)
        {
            return ((long)channel << 32) | (uint)cycleNumber;
        }

        private void MarkDaqClockCycleAborted(int channel, int cycleNumber)
        {
            _daqClockAbortedCycles[DaqAbortedCycleKey(channel, cycleNumber)] = 0;
        }

        private bool TryConsumeDaqClockCycleAbort(int channel, int cycleNumber)
        {
            return _daqClockAbortedCycles.TryRemove(
                DaqAbortedCycleKey(channel, cycleNumber),
                out _);
        }

        private bool DiscardCurrentCycleForSoftwareRecovery(
            int channel,
            DateTime cutoffUtc,
            string reason,
            int? expectedCycleNumber = null)
        {
            int cycleNumber;
            if (expectedCycleNumber.HasValue)
            {
                var expected = new KeyValuePair<int, int>(channel, expectedCycleNumber.Value);
                if (!((ICollection<KeyValuePair<int, int>>)_currentCycleNumberByChannel)
                        .Remove(expected))
                {
                    _currentCycleNumberByChannel.TryGetValue(channel, out var actualCycle);
                    _log.Warn(
                        $"EPB[{channel}] 拒绝作废非当前圈。" +
                        $"ExpectedCycle={expectedCycleNumber} ActualCycle={actualCycle} " +
                        $"Reason={reason}",
                        "落盘");
                    return false;
                }
                cycleNumber = expectedCycleNumber.Value;
            }
            else if (!_currentCycleNumberByChannel.TryRemove(channel, out cycleNumber))
            {
                return false;
            }
            _currentAttemptIdByChannel.TryRemove(channel, out var attemptId);
            try
            {
                if (Recorder is IBatchedEpbCycleRecorder batched)
                    batched.SealCycleWindow(channel, cycleNumber, cutoffUtc);
                AbortCycleAfterPersistence(
                    Recorder,
                    channel,
                    cycleNumber,
                    cutoffUtc,
                    "AbortedBySoftwareRecovery");
                _formalPersistenceRecoveryPendingCycles.TryRemove(channel, out _);
                // 正式圈回调稍后收尾时只消费此标记，不得把已作废圈再次封账。
                MarkDaqClockCycleAborted(channel, cycleNumber);
                _log.Warn(
                    $"EPB[{channel}] Cycle={cycleNumber} 因软件自愈作废；" +
                    $"不计正式完成数。Reason={reason}",
                    "落盘");
                return true;
            }
            catch (Exception ex)
            {
                // 封圈失败时恢复活动圈所有权，后续自维护必须继续重试；不能留下
                // “字典已移除但 SQLite 仍 running”的半提交状态。
                _currentCycleNumberByChannel.TryAdd(channel, cycleNumber);
                if (attemptId > 0) _currentAttemptIdByChannel.TryAdd(channel, attemptId);
                _log.Warn(
                    $"EPB[{channel}] 软件自愈作废当前圈失败：{ex.Message}",
                    "落盘");
                return false;
            }
        }

        private Dictionary<int, int> CaptureSoftwareRecoveryCycles(
            IEnumerable<int> channels)
        {
            var result = new Dictionary<int, int>();
            foreach (var channel in (channels ?? Array.Empty<int>()).Distinct())
            {
                if (_currentCycleNumberByChannel.TryGetValue(channel, out var cycleNumber))
                    result[channel] = cycleNumber;
            }
            return result;
        }

        private bool TrySealSoftwareRecoveryCycleWindows(
            IReadOnlyDictionary<int, int> cycles,
            DateTime cutoffUtc,
            string reason)
        {
            if (cycles == null || cycles.Count == 0) return true;
            if (!(Recorder is IBatchedEpbCycleRecorder batched)) return true;
            var succeeded = true;
            foreach (var pair in cycles)
            {
                try
                {
                    batched.SealCycleWindow(pair.Key, pair.Value, cutoffUtc);
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    _log.Warn(
                        $"EPB[{pair.Key}] 软件恢复截止时间窗封闭失败，保持停机重试。" +
                        $"Cycle={pair.Value} CutoffUtc={cutoffUtc:O} Reason={reason} Error={ex.Message}",
                        "落盘");
                }
            }
            return succeeded;
        }

        /// <summary>
        /// 通道级/设备组级软件恢复的统一数据收口。先封闭当前圈时间窗，再等待截止时
        /// 已进入 Raw 后台管线的批次完成发布，并等待各设备的 FIFO 耐久前缀越过截止
        /// 序号；最后才提交 AbortedBySoftwareRecovery。健康同设备通道可继续写更高序号，
        /// 不要求整设备队列清零。
        /// </summary>
        private async Task<bool> TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
            IReadOnlyDictionary<int, int> cycles,
            DateTime cutoffUtc,
            string reason,
            int timeoutMs,
            CancellationToken token)
        {
            if (cycles == null || cycles.Count == 0) return true;
            if (!await TryWaitForCycleDurableCutoffAsync(
                    cycles,
                    cutoffUtc,
                    reason,
                    timeoutMs,
                    token)
                .ConfigureAwait(false))
                return false;

            foreach (var pair in cycles)
            {
                if (!_currentCycleNumberByChannel.TryGetValue(pair.Key, out var actualCycle))
                    continue;
                if (actualCycle != pair.Value)
                {
                    _log.Warn(
                        $"EPB[{pair.Key}] 软件恢复截止圈身份已变化，拒绝作废新圈。" +
                        $"Expected={pair.Value} Actual={actualCycle} Reason={reason}",
                        "落盘");
                    return false;
                }
                if (!DiscardCurrentCycleForSoftwareRecovery(
                        pair.Key,
                        cutoffUtc,
                        reason,
                        pair.Value))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 等待一组圈在指定截止时间前的 Raw 数据全部发布并形成真实耐久前缀，但不改变
        /// 圈终态。正式圈、学习圈、报警圈和各类软件恢复共用该屏障，避免任何收尾路径
        /// 在迟到批次仍可能进入时提前 Complete/Alarm/Abort。
        /// </summary>
        private async Task<bool> TryWaitForCycleDurableCutoffAsync(
            IReadOnlyDictionary<int, int> cycles,
            DateTime cutoffUtc,
            string reason,
            int timeoutMs,
            CancellationToken token)
        {
            if (cycles == null || cycles.Count == 0) return true;
            if (!TrySealSoftwareRecoveryCycleWindows(cycles, cutoffUtc, reason)) return false;

            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            var rawTimeoutMs = (int)Math.Max(
                1,
                (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
            if (!await _acq.DrainBackgroundPipelinesAsync(rawTimeoutMs, token)
                    .ConfigureAwait(false))
            {
                _log.Warn(
                    $"软件恢复截止 Raw 管线排空超时；保持圈事务开放且禁止重入。" +
                    $"Channels=[{string.Join(",", cycles.Keys)}] Reason={reason}",
                    "落盘");
                return false;
            }

            var devices = cycles.Keys
                .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var device in devices)
            {
                var boundary = _acq.GetLastDiskPublishedSequence(device);
                var remainingMs = (int)Math.Max(
                    1,
                    (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
                if (!await _persistence.WaitForDurablePrefixAsync(
                        device,
                        boundary,
                        remainingMs,
                        token)
                    .ConfigureAwait(false))
                {
                    var snapshot = _persistence.GetSnapshot(device);
                    _log.Warn(
                        $"软件恢复截止耐久前缀超时；保持圈事务开放且禁止重入。" +
                        $"Device={device} Boundary={boundary} Persisted={snapshot.Sequence} " +
                        $"Depth={snapshot.QueueDepth} State={snapshot.State} " +
                        $"Channels=[{string.Join(",", cycles.Keys)}] Reason={reason}",
                        "落盘");
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Stop 已经完成整台设备 Raw 排空、队列清零和耐久边界确认后，直接提交仍在
        /// 内存登记的非报警活动圈终态。此处不得再次调用通道前缀门禁，因为 Stop 的
        /// SuppressAfter 仍保持有效；整设备边界本身就是更强的前置证明。
        /// </summary>
        private bool TryFinalizeStoppedCyclesAfterDurableBoundary(
            IReadOnlyDictionary<int, int> cycles,
            DateTime cutoffUtc,
            string status)
        {
            if (cycles == null || cycles.Count == 0) return true;
            var recorder = Recorder;
            if (recorder == null)
            {
                _log.Warn(
                    $"Stop耐久边界后仍有{cycles.Count}个活动圈，但Recorder不可用；" +
                    "保留圈身份并禁止同进程重启。",
                    "落盘");
                return false;
            }

            var allFinalized = true;
            foreach (var pair in cycles)
            {
                if (!_currentCycleNumberByChannel.TryGetValue(pair.Key, out var actualCycle))
                    continue;
                if (actualCycle != pair.Value || IsAlarmStopRequested(pair.Key))
                {
                    allFinalized = false;
                    continue;
                }
                try
                {
                    var finalN = recorder.GetCurrentCycleSampleCount(pair.Key);
                    recorder.AbortCycle(pair.Key, pair.Value, finalN, cutoffUtc, status);
                    var removed =
                        ((ICollection<KeyValuePair<int, int>>)_currentCycleNumberByChannel)
                        .Remove(pair);
                    if (!removed &&
                        _currentCycleNumberByChannel.TryGetValue(pair.Key, out var currentCycle))
                    {
                        // 终态提交后若通道身份已经变成另一圈，绝不能清除新圈的 attempt/
                        // recovery 标记，更不能把本次 Stop 宣告为已完全收口。
                        allFinalized = false;
                        _log.Warn(
                            $"EPB[{pair.Key}] Stop圈终态已提交，但内存圈身份发生变化，" +
                            $"禁止同进程重启。FinalizedCycle={pair.Value} " +
                            $"CurrentCycle={currentCycle}",
                            "落盘");
                        continue;
                    }
                    _currentAttemptIdByChannel.TryRemove(pair.Key, out _);
                    _formalPersistenceRecoveryPendingCycles.TryRemove(pair.Key, out _);
                    MarkDaqClockCycleAborted(pair.Key, pair.Value);
                    QueuePendingWarningSnapshotsForCycle(pair.Key, pair.Value);
                }
                catch (Exception ex)
                {
                    allFinalized = false;
                    _log.Warn(
                        $"EPB[{pair.Key}] Stop耐久边界后提交圈终态失败，禁止同进程重启。" +
                        $"Cycle={pair.Value} Error={ex.Message}",
                        "落盘");
                }
            }
            return allFinalized;
        }

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
            lock (GetHydraulicParticipantGate(channel))
            {
                _hydraulicParticipantVersions[channel] =
                    Interlocked.Increment(ref _hydraulicParticipantVersionSequence);
                _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
                _hydraulicParticipants[channel] = 0;
            }
        }

        private void MarkHydraulicParticipantFromFormalSlot(int channel, long firstEligibleSlot)
        {
            lock (GetHydraulicParticipantGate(channel))
            {
                _hydraulicParticipantVersions[channel] =
                    Interlocked.Increment(ref _hydraulicParticipantVersionSequence);
                _firstEligibleFormalSlotByChannel[channel] = Math.Max(0, firstEligibleSlot);
                _hydraulicParticipants[channel] = 0;
            }
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
            lock (GetHydraulicParticipantGate(channel))
            {
                _hydraulicParticipants.TryRemove(channel, out _);
                _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
            }
        }

        private object GetHydraulicParticipantGate(int channel)
        {
            return _hydraulicParticipantGates.GetOrAdd(channel, _ => new object());
        }

        private long CaptureHydraulicParticipantVersion(int channel)
        {
            lock (GetHydraulicParticipantGate(channel))
            {
                return _hydraulicParticipants.ContainsKey(channel) &&
                       _hydraulicParticipantVersions.TryGetValue(channel, out var version)
                    ? version
                    : 0;
            }
        }

        private bool TryUnmarkHydraulicParticipant(
            int channel,
            long expectedVersion,
            string reason)
        {
            lock (GetHydraulicParticipantGate(channel))
            {
                var participantExists = _hydraulicParticipants.ContainsKey(channel);
                _hydraulicParticipantVersions.TryGetValue(channel, out var currentVersion);
                if (!RecoveryEpochGuard.CanApplyParticipantCleanup(
                        participantExists,
                        expectedVersion,
                        currentVersion))
                {
                    _log?.Warn(
                        $"EPB[{channel}] 拒绝迟到的液压参与状态清理。" +
                        $"ExpectedVersion={expectedVersion} CurrentVersion={currentVersion} " +
                        $"Reason={reason}",
                        "液压协调");
                    return false;
                }

                _hydraulicParticipants.TryRemove(channel, out _);
                _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
                return true;
            }
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
            return _alarmStopLatch.IsStopRequested(channel);
        }


        /// <summary>
        ///     记录通道“当前圈号”（BeginCycle 后调用），用于报警停机时封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <param name="cycleNumber">当前圈号（与 Recorder.BeginCycle 一致）。</param>
        private void MarkCurrentCycleNumber(int channel, int cycleNumber)
        {
            _currentCycleNumberByChannel[channel] = cycleNumber;
            _currentAttemptIdByChannel[channel] = Interlocked.Increment(ref _cycleAttemptSequence);
        }


        /// <summary>
        ///     清除通道“当前圈号”（Complete/Alarm 封圈后调用），避免后续误封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void ClearCurrentCycleNumber(int channel)
        {
            if (_formalPersistenceRecoveryPendingCycles.TryGetValue(
                    channel,
                    out var pendingCycle) &&
                _currentCycleNumberByChannel.TryGetValue(channel, out var currentCycle) &&
                currentCycle == pendingCycle)
            {
                _log.Warn(
                    $"EPB[{channel}] 圈{currentCycle}仍等待耐久恢复，拒绝清除当前圈身份。",
                    "落盘");
                return;
            }
            _currentCycleNumberByChannel.TryRemove(channel, out _);
            _currentAttemptIdByChannel.TryRemove(channel, out _);
        }


        /// <summary>
        ///     报警快照流程结束后封圈：只有当前报警圈的 CSV/BIN 均已落盘时才写
        ///     <c>status='alarm'</c>；快照失败则写为 <c>failed</c>。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     线程模型：可在报警事件回调线程/后台任务中调用；内部不抛异常（仅 best-effort）。
        ///     <para>
        ///     设计目的：保证 UI(EpbTestRecord) 计数与落盘圈数一致，避免 BeginCycle 后未 Complete 导致的“running 悬挂圈”。
        ///     </para>
        /// </remarks>
        private void TryFinalizeCurrentCycleAfterSnapshot(int channel, bool hasSnapshotFiles)
        {
            var recorder = Recorder;
            if (recorder == null) return;

            if (!_currentCycleNumberByChannel.TryGetValue(channel, out var cycleNumber))
                return;

            if (TryFinalizeCurrentCycleAfterSnapshotOnce(
                    recorder,
                    channel,
                    cycleNumber,
                    hasSnapshotFiles))
                return;

            if (!_alarmCycleFinalizationRetries.TryAdd(channel, 0)) return;
            var sessionToken = _batchSessionCts?.Token ?? CancellationToken.None;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                try
                {
                    while (_currentCycleNumberByChannel.TryGetValue(channel, out var current) &&
                           current == cycleNumber)
                    {
                        attempt++;
                        await Task.Delay(
                                GetDaqSelfMaintenanceDelayMs(attempt),
                                sessionToken)
                            .ConfigureAwait(false);
                        if (TryFinalizeCurrentCycleAfterSnapshotOnce(
                                recorder,
                                channel,
                                cycleNumber,
                                hasSnapshotFiles))
                            return;
                    }
                }
                finally
                {
                    _alarmCycleFinalizationRetries.TryRemove(channel, out _);
                }
            }), "AlarmCycleDurableFinalization", channel);
        }

        private bool TryFinalizeCurrentCycleAfterSnapshotOnce(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            bool hasSnapshotFiles)
        {
            try
            {
                var finalUtc = DateTime.UtcNow;
                var finalN = FinalizeCyclePersistence(
                    recorder,
                    channel,
                    cycleNumber,
                    finalUtc,
                    recorder.GetCurrentCycleSampleCount(channel));
                if (hasSnapshotFiles)
                {
                    recorder.AlarmCycle(channel, cycleNumber, finalN, finalUtc);
                }
                else
                {
                    // FinalizeCyclePersistence 已经完成统一 Raw/耐久屏障，此处只提交终态。
                    recorder.AbortCycle(channel, cycleNumber, finalN, finalUtc, "failed");
                    _log.Warn(
                        $"EPB[{channel}] 报警快照文件未完整生成，当前圈记为 failed，不写入 alarm。",
                        "落盘");
                }
                ((ICollection<KeyValuePair<int, int>>)_currentCycleNumberByChannel)
                    .Remove(new KeyValuePair<int, int>(channel, cycleNumber));
                _currentAttemptIdByChannel.TryRemove(channel, out _);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"EPB[{channel}] 报警圈 Raw/耐久封存边界尚未确认，" +
                    $"保持圈事务并持续重试。Cycle={cycleNumber} Error={ex.Message}",
                    "落盘");
                return false;
            }
        }

        internal void GetPeakCaptureIdentity(int channel, out Guid runId, out int cycleNumber)
        {
            runId = _activeBatchId;
            if (runId == Guid.Empty) runId = Guid.Empty;
            cycleNumber = _currentCycleNumberByChannel.TryGetValue(channel, out var current)
                ? current
                : 0;
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
            _log.Info(
                $"EPB[{channel}] 已完成全部目标圈数，开始安全断电、液压释放和最近10圈持久化。",
                "EPB");

            // 1) 先从液压判定参与者中移除
            UnmarkHydraulicParticipant(channel);

            // 2) 取消并释放硬停机 CTS（该通道已完成）
            try { CancelStopCts(channel); } catch { /* ignore */ }

            // 3) 原子移除运行对象，避免与并发启动/采集路由交叉。
            RemoveTimerRuntime(channel, nameof(FinalizeChannelAfterNaturalCompletion));
            RemoveRunnerRuntime(channel, nameof(FinalizeChannelAfterNaturalCompletion));

            // 4) 安全落位：断电 + 请求液压释放
            try { CommandEpbOff(channel, nameof(FinalizeChannelAfterNaturalCompletion)); } catch { /* ignore */ }
            try { ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), "NaturalCompletionRelease", channel); }
            catch { /* ignore */ }

            // 5) 停止即存最近10圈：不阻塞当前线程
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    ObserveBackgroundTask(Task.Run(() =>
                    {
                        try
                        {
                            recorder.FlushRecent(channel, 10);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
                        }
                    }), "FlushRecentAfterNaturalCompletion", channel);
            }
            catch
            {
                // ignore
            }

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Completed,
                "Completed",
                "已完成全部目标圈数");
            TryEndBatchSessionWhenIdle("Complete");
            FlushPersistentLog();
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
            bool adaptiveShadowMode = true,
            IPowerSupplyCoordinator powerSupply = null,
            bool requirePowerSupply = false,
            IDaqHardwareProbe daqHardwareProbe = null)
        {
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController ?? throw new ArgumentNullException(nameof(aoController));
            _acq = acq ?? throw new ArgumentNullException(nameof(acq));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

            _do = doController;
            _do.HighPriorityOffCompleted += OnHighPriorityOffCompleted;
            _ao = aoController;
            //_readCurrent = acq.ReadCurrent;
            _readCurrent = acq.ReadCurrentFast;
            _log = log ?? NullLogger.Instance;
            _taskSupervisor = new TaskSupervisor(_log);
            _acq = acq;
            _dev1ChannelMask = BuildDaqChannelMask("Dev1");
            _dev2ChannelMask = BuildDaqChannelMask("Dev2");
            _acq.SetControlActivityProvider(IsDaqDeviceControlActive);
            _requirePowerSupply = requirePowerSupply || ReadBooleanAppSetting("PowerSupplyIntegrationRequired", false);
            if (powerSupply == null && _requirePowerSupply)
            {
                var powerConfigPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Config",
                    "PowerSupplyConfig.xml");
                var powerConfig = PowerSupplyConfigLoader.Load(powerConfigPath);
                powerSupply = new PowerSupplyCoordinator(powerConfig, cfg.Test.Groups, _log);
            }
            _powerSupply = powerSupply;
            _daqHardwareProbe = daqHardwareProbe ?? new NIDaqHardwareProbe();
            if (_powerSupply != null)
            {
                _powerSupply.TelemetryUpdated += OnPowerSupplyTelemetryUpdated;
                _powerSupply.FaultRaised += OnPowerSupplyFaultRaised;
            }

            _safetyMarginControlMode = safetyMarginControlMode;
            _epbControlMode = ReadEpbControlMode(epbControlMode);
            _adaptiveShadowMode = ReadAdaptiveShadowMode(adaptiveShadowMode);
            _adaptiveChannels = ReadAdaptiveChannels();
            _programSafetySettings = EpbProgramSafetySettings.Load(_log);

            try
            {
                var projectConfigDir = ConfigLoader.GetProjectConfigDir(cfg.Test.StoreDir, cfg.Test.TestName);
                _adaptiveProfileStore = new EpbAdaptiveProfileStore(projectConfigDir, _log);
                _log.Info(
                    $"EPB 控制模式={_epbControlMode}，灰度通道={string.Join(",", _adaptiveChannels)}，" +
                    $"影子判定={_adaptiveShadowMode}，模型={_adaptiveProfileStore.FilePath}",
                    "EPB");
                _log.Info(
                    "项目 TestConfig.xml 中旧的正/反向失速与断电清零字段仅为兼容读取，" +
                    "运行时统一使用 EXE 同名配置中的程序级安全策略。",
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
            _daqPersistenceQueueCapacity = ReadIntAppSetting("DaqPersistenceQueueCapacity", 256, 2, 4096);
            _daqPersistencePauseDepth = Math.Min(
                _daqPersistenceQueueCapacity - 1,
                ReadIntAppSetting("DaqPersistencePauseDepth", 128, 1, 4095));
            _daqPersistenceResumeDepth = Math.Min(
                _daqPersistencePauseDepth - 1,
                ReadIntAppSetting("DaqPersistenceResumeDepth", 32, 0, 4094));
            _daqPersistencePauseAgeMs = ReadDoubleAppSetting("DaqPersistencePauseAgeMs", 1000, 100, 60000);
            _daqPersistenceResumeAgeMs = Math.Min(
                _daqPersistencePauseAgeMs,
                ReadDoubleAppSetting("DaqPersistenceResumeAgeMs", 100, 1, 10000));
            _daqPersistenceRecoveryTimeoutMs = ReadIntAppSetting(
                "DaqPersistenceRecoveryTimeoutMs", 10000, 1000, 60000);
            _daqPersistenceRequiredFreshBatches = ReadIntAppSetting(
                "DaqPersistenceRequiredFreshBatches", 10, 1, 100);
            _daqClockRecoveryFreshBatches = ReadIntAppSetting(
                "DaqClockRecoveryFreshBatches", 10, 1, 100);
            _daqClockRecoveryMaxAttempts = ReadIntAppSetting(
                "DaqClockRecoveryMaxAttempts", 3, 1, 20);
            _daqClockRecoveryWindowMinutes = ReadIntAppSetting(
                "DaqClockRecoveryWindowMinutes", 10, 1, 1440);
            _daqClockRecoveryAttempts = new DaqRecoveryAttemptWindow(
                _daqClockRecoveryMaxAttempts,
                TimeSpan.FromMinutes(_daqClockRecoveryWindowMinutes));
            _persistence = new DaqPersistenceCoordinator(
                () => Recorder,
                _log,
                _daqPersistenceQueueCapacity,
                _daqPersistencePauseDepth,
                _daqPersistenceResumeDepth,
                _daqPersistencePauseAgeMs,
                _daqPersistenceResumeAgeMs,
                _daqPersistenceRecoveryTimeoutMs,
                _daqPersistenceRequiredFreshBatches,
                _acq.RecordExternalDiagnostic,
                _acq.RecordPersistenceTiming);
            _persistence.StateChanged += OnDaqPersistenceStateChanged;
            _acq.DeviceFaultDetected += OnDaqDeviceFaultSafetyDetected;
            _acq.DeviceFaultPublicationRequested += OnDaqDeviceFaultDetected;
            _acq.OnFastEpbCurrentSample += sample =>
            {
                if (_runners.TryGetValue(sample.Channel, out var r))
                    r.FeedCurrentSample(sample);
            };

            _acq.DiskBatchReady += batch =>
            {
                var device = batch.Device;
                var sequence = batch.Sequence;
                if (!_persistence.Enqueue(batch))
                    throw new InvalidOperationException(
                        $"{device} 序号 {sequence} 未被持久化队列接收；禁止推进已发布边界。");
            };

            _hydraulic = new HydraulicController(
                _do,
                _cfg.Test,
                acq.ReadPressureSample,
                aoController,
                _log);

            // ★ 创建协调器（具备 AO 与读压，可用 Fallback/保持两种实现）
            _hydCoordinator = new HydraulicGroupCoordinator(
                _cfg.Test,
                _cfg.DO,
                _do,
                acq.ReadPressureSample,
                aoController,
                _hydraulic,
                _log);
            _hydCoordinator.FaultRaised += OnHydraulicFaultRaised;

            for (var channel = 1; channel <= 12; channel++)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.NotEnabled,
                    "NotEnabled",
                    "本轮未启用",
                    allowTerminalReset: true);

            _timerRuntimeWatchdogIntervalMs = ReadIntAppSetting(
                "TimerRuntimeWatchdogIntervalMs", 2000, 500, 30000);
            _timerRuntimeSilenceThresholdMs = ReadIntAppSetting(
                "TimerRuntimeSilenceThresholdMs",
                Math.Max(30000, PeriodMs * 2),
                5000,
                600000);
            _timerRuntimeWatchdog = new System.Threading.Timer(
                InspectTimerRuntimeHealth,
                null,
                _timerRuntimeWatchdogIntervalMs,
                _timerRuntimeWatchdogIntervalMs);
        }

        private CancellationTokenSource RenewCyclePauseCts(int channel)
        {
            var cts = new CancellationTokenSource();
            if (_cyclePauseCtsByChannel.TryGetValue(channel, out var previous))
            {
                try { previous.Cancel(); } catch { }
                try { previous.Dispose(); } catch { }
            }
            _cyclePauseCtsByChannel[channel] = cts;
            return cts;
        }

        private void CancelCyclePauseCts(int channel)
        {
            if (_cyclePauseCtsByChannel.TryRemove(channel, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private void ReleaseCyclePauseCts(int channel, CancellationTokenSource cts)
        {
            if (cts == null) return;
            if (_cyclePauseCtsByChannel.TryGetValue(channel, out var current) && ReferenceEquals(current, cts))
                _cyclePauseCtsByChannel.TryRemove(channel, out _);
            try { cts.Dispose(); } catch { }
        }

        private static int ReadIntAppSetting(string key, int fallback, int min, int max)
        {
            try
            {
                return int.TryParse(ConfigurationManager.AppSettings[key], out var value)
                    ? Math.Max(min, Math.Min(max, value))
                    : fallback;
            }
            catch { return fallback; }
        }

        private static double ReadDoubleAppSetting(string key, double fallback, double min, double max)
        {
            try
            {
                return double.TryParse(
                           ConfigurationManager.AppSettings[key],
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture,
                           out var value)
                    ? Math.Max(min, Math.Min(max, value))
                    : fallback;
            }
            catch { return fallback; }
        }

        private void SaveProgramSafetySnapshot()
        {
            var projectConfigDir = ConfigLoader.GetProjectConfigDir(
                _cfg?.Test?.StoreDir,
                _cfg?.Test?.TestName);
            if (_programSafetySettings == null) return;
            _log.Info(
                $"本次试验EPB程序级安全策略：Policy={EpbProgramSafetySettings.SafetyPolicyVersion}; " +
                _programSafetySettings.ToAuditLogText(),
                "EPB");
            _programSafetySettings.SaveEffectiveSnapshot(projectConfigDir, _log);
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
        public async Task HydraulicEnterAsync(int channel, CancellationToken token)
        {
            if (_hydCoordinator == null) return;
            if (_hydraulicLeaseByChannel.TryGetValue(channel, out var existing))
            {
                if (!existing.IsClosed) return;
                await existing.Completion.ConfigureAwait(false);
                ((ICollection<KeyValuePair<int, HydraulicChannelLeaseScope>>)_hydraulicLeaseByChannel)
                    .Remove(new KeyValuePair<int, HydraulicChannelLeaseScope>(channel, existing));
            }

            var hydId = channel <= 6 ? 1 : 2;
            var runId = _activeBatchId == Guid.Empty ? Guid.NewGuid() : _activeBatchId;
            var key = new HydraulicGenerationKey(
                runId,
                hydId,
                HydraulicPhaseKind.SingleChannel,
                Interlocked.Increment(ref _singleHydraulicGeneration));
            await EnterSingleHydraulicGenerationAsync(key, channel, token).ConfigureAwait(false);
        }

        private void OnHighPriorityOffCompleted(HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null) return;
            LogHighPriorityOffFieldMetric(telemetry);
            RecordLateTerminalOffCompletion(telemetry);
            if (telemetry.Result) SetChannelEnergized(telemetry.Channel, false);
            if (!telemetry.LateHardwareSuccess) return;

            _log.Warn(
                $"EPB[{telemetry.Channel}] 高优先级OFF调用方曾超时，但同一命令已迟到成功。" +
                $"CommandId={telemetry.CommandId:N} NIWrite={telemetry.NiWriteMs:F3}ms " +
                $"Total={telemetry.TotalMs:F3}ms Classification=LateHardwareSuccess；" +
                "保持电源组失效安全，后续以新鲜电流/电源回读完成最终对账。",
                "DO性能");
        }

        private async Task EnterSingleHydraulicGenerationAsync(
            HydraulicGenerationKey key,
            int channel,
            CancellationToken token)
        {
            var lease = await _hydCoordinator.EnterGenerationAsync(key, new[] { channel }, token)
                .ConfigureAwait(false);
            _hydraulicLeaseByChannel[channel] = _hydCoordinator.CreateChannelScope(lease, channel);
            NonCriticalObserver.Invoke(
                PressureQualificationChanged,
                lease.Qualification,
                ex => _log?.Warn($"压力资格观察者异常已隔离：{ex.Message}", "液压协调"));
        }

        /// <summary>
        ///     释放液压
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task HydraulicMarkReleaseAsync(int channel)
        {
            if (_hydCoordinator == null) return;
            if (!_hydraulicLeaseByChannel.TryGetValue(channel, out var scope))
            {
                await _hydCoordinator.MarkVoltageReleaseAsync(channel).ConfigureAwait(false);
                return;
            }

            try
            {
                await scope.CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                // 只移除本次取得的旧 lease。若通道已进入新的正式代次，
                // 迟到的旧释放任务不得删除新 lease。
                ((ICollection<KeyValuePair<int, HydraulicChannelLeaseScope>>)_hydraulicLeaseByChannel)
                    .Remove(new KeyValuePair<int, HydraulicChannelLeaseScope>(channel, scope));
            }
        }

        private async Task AbortHydraulicLeaseForChannelAsync(int channel, string reason)
        {
            if (!_hydraulicLeaseByChannel.TryGetValue(channel, out var scope)) return;
            // OFF 未确认时，TryEnsureSoftwareRecoveryOutputOff 已升级为电气组紧急关闭。
            // 此时必须保留租约映射，禁止把仍可能带电的成员伪装成已归还。
            RequireSoftwareRecoveryOutputOff(channel, reason ?? "HydraulicLeaseAbort");
            try
            {
                await scope.AbortAsync(reason).ConfigureAwait(false);
            }
            finally
            {
                ((ICollection<KeyValuePair<int, HydraulicChannelLeaseScope>>)_hydraulicLeaseByChannel)
                    .Remove(new KeyValuePair<int, HydraulicChannelLeaseScope>(channel, scope));
            }
        }

        private void ObserveSafetyTask(Task task, string operation, int channel)
        {
            ObserveBackgroundTask(task, operation, channel);
        }

        internal void ObserveBackgroundTask(Task task, string operation, int channel = 0)
        {
            _taskSupervisor.Observe(task, operation, _activeBatchId, channel);
        }

        internal TaskSupervisorEntry[] CaptureBackgroundTasks()
        {
            return _taskSupervisor.Snapshot();
        }

        internal Task<bool> DrainBackgroundTasksAsync(int timeoutMs)
        {
            return _taskSupervisor.DrainAsync(timeoutMs);
        }

        // （保留你已有的 StartChannelAsync / Pause/Resume/Stop 等实现，不改对外签名）
        public async Task StartChannelAsync(int channel, CancellationToken uiToken = default)
        {
            if (!IsChannelEnabled(channel))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 当前项目已取消启用；请先在试验设置中重新勾选并保存后再启动。");
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            var singleRunId = Guid.NewGuid();
            _activeBatchId = singleRunId;
            Interlocked.Exchange(ref _idleSessionClosureScheduled, 0);
            BeginDaqIncidentRun(singleRunId, new[] { channel });
            InvalidateStopSafetyCache();
            ResetTransientFaultStateForRestart(new[] { channel }, "FreshSingleChannelStart");
            // 单通道启动的DAQ自愈最多三次；提前建立停止令牌，使“停止”按钮随时可取消。
            var stopCts = RenewStopCts(channel);
            using var startLinked = CancellationTokenSource.CreateLinkedTokenSource(uiToken, stopCts.Token);
            try
            {
                await EnsureDaqReadyBeforeStartAsync(new[] { channel }, startLinked.Token).ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                SaveProgramSafetySnapshot();
                await EnsurePowerSupplyReadyBeforeStartAsync(new[] { channel }, startLinked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                uiToken.IsCancellationRequested || stopCts.IsCancellationRequested)
            {
                CancelStopCts(channel);
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.ManualStopped,
                    "StartCanceled",
                    "启动自愈已由用户停止。",
                    channel,
                    new[] { channel },
                    singleRunId);
                throw;
            }
            catch (Exception ex)
            {
                CancelStopCts(channel);
                PublishStartBlockedAfterCleanup(
                    channel,
                    "PreflightFailed",
                    ex.Message,
                    singleRunId);
                throw;
            }

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Starting,
                "Starting",
                "安全预检通过，正在启动",
                correlationId: singleRunId,
                allowTerminalReset: true);

            // 若上一次因报警触发过停机，这里允许重新启动
            _manualStopRequestedChannels.TryRemove(channel, out _);
            _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
            _alarmStopLatch.BeginRun(channel);

            //var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            var rcfg = _cfg.Test?.EpbCycleRunner.GetRunnerChannel(channel);


            var periodMs = _cfg.Test.PeriodMs;
            var sampleMs = 2;


            var forwardA = rcfg.ForwardA;
            var holdMs = rcfg.HoldMs;

            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = holdMs <= 0 ? 1000 : holdMs; // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

            var singleChannelPlan = ElectricalStaggerPlanner.Build(
                new[] { channel },
                _cfg.Test.Groups,
                periodMs);
            RegisterRunContext(singleRunId, singleChannelPlan);
            var singleAssignment = singleChannelPlan.Get(channel);
            var staggerMs = singleAssignment.PhaseMs;
            _log.Info(
                $"EPB[{channel}] 单通道运行 Run={singleRunId:N}，归属组 {singleAssignment.ElectricalGroupId}，" +
                $"按已选集合重新编号后首启相位={staggerMs}ms。",
                "EPB");

            //日志记录周期
            _log.Info($"高精度定时器，  EPB[{channel}] 周期 {periodMs}ms，采样 {sampleMs}ms，前进阈值 {forwardA}A，保持时间 {holdMs}ms",
                "EPB");

            var timer = GetTimer(channel, periodMs, _cfg.Test.OverrunPolicy);

            // 标记为“参与液压判定”（用于后续批量建压锚点过滤；单通道模式也保持一致）
            MarkHydraulicParticipant(channel);

            var runner = (EpbCycleRunner)GetRunner(channel);

            var learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5;

            if (learnCycles > 0)
            {
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Learning,
                    "Learning",
                    "正在执行自学习",
                    correlationId: singleRunId);
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
                    // 单卡钳启动与批量启动共用相同语义：软件失败有界自愈，三次失败
                    // 转入启动回滚；只有本次实时确认的控制/硬件故障才锁存卡钳。
                    var positioningFailures = await PreReleaseBatchWithPlanAsync(
                            new[] { channel },
                            null,
                            singleChannelPlan,
                            startLinked.Token)
                        .ConfigureAwait(false);
                    if (positioningFailures.Length > 0)
                        throw new InvalidOperationException(
                            $"EPB[{channel}] 启动定位确认实时故障：" +
                            $"{positioningFailures[0].Code}/{positioningFailures[0].Reason}");

                    runner.UseNoHeadPhase = true;
                    runner.EnableTailCompensation = true;
                    runner.TailMinMs = T8MinMs;
                    var pressureGroup = channel <= 6 ? 1 : 2;
                    for (var ordinal = 1; ordinal <= learnCycles; ordinal++)
                    {
                        startLinked.Token.ThrowIfCancellationRequested();
                        await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                                new HydraulicGenerationKey(
                                    singleRunId,
                                    pressureGroup,
                                    HydraulicPhaseKind.Learning,
                                    ordinal),
                                new[] { channel },
                                startLinked.Token)
                            .ConfigureAwait(false);
                        MarkElectricalPhaseDue(channel, DateTime.UtcNow);
                        await RunLearningLogicalCycleWithSelfHealingAsync(
                                runner,
                                channel,
                                pressureGroup,
                                staggerMs,
                                ordinal,
                                singleRunId,
                                startLinked.Token)
                            .ConfigureAwait(false);
                    }
                    EnsureAdaptiveProfilesReady(new[] { channel });
                    _log.Info($"EPB[{channel}] 自学习完成，进入正式试验。", "EPB");
                }
                catch (OperationCanceledException) when (
                    uiToken.IsCancellationRequested || stopCts.IsCancellationRequested)
                {
                    try { StopChannel(channel); } catch { }
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.ManualStopped,
                        "StartCanceled",
                        "单卡钳启动/学习自愈已由用户停止。",
                        channel,
                        new[] { channel },
                        singleRunId);
                    throw;
                }
                catch (Exception ex)
                {
                    PublishStartBlockedAfterCleanup(
                        channel,
                        "LiveLearningFault",
                        ex.Message,
                        singleRunId);
                    _log.Error(
                        $"EPB[{channel}] 本次启动定位/学习确认实时故障，已隔离该通道：{ex.Message}",
                        "EPB",
                        ex);
                    throw;
                }
            }

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Running,
                "Running",
                "正式试验运行中",
                correlationId: singleRunId);
            LogFieldSessionMetric("Start", singleRunId, new[] { channel }, false, "SingleFormal");
            var singleFormalAnchorUtc = DateTime.UtcNow.AddMilliseconds(staggerMs);
            var singleSuccessfulCycles = 0;
            ObserveBackgroundTask(timer.StartAsync(null, staggerMs, async (i, token) =>
            {
                var cyclePauseCts = RenewCyclePauseCts(channel);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    stopCts.Token,
                    cyclePauseCts.Token);
                var ct = linked.Token;
                var actualStartUtc = DateTime.UtcNow;
                var nominalDueUtc = singleFormalAnchorUtc.AddMilliseconds((long)(i - 1) * periodMs);
                var elapsedSinceNominalMs = (actualStartUtc - nominalDueUtc).TotalMilliseconds;
                var rolledPeriods = elapsedSinceNominalMs <= 0
                    ? 0L
                    : (long)Math.Floor(elapsedSinceNominalMs / periodMs);
                var plannedStartUtc = nominalDueUtc.AddMilliseconds(rolledPeriods * periodMs);
                MarkElectricalPhaseDue(channel, plannedStartUtc);

                _log.Info(
                    $"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始，Run={singleRunId:N} " +
                    $"Group={singleAssignment.ElectricalGroupId} Phase={singleAssignment.PhaseMs}ms " +
                    $"PlannedUtc={plannedStartUtc:O} ActualUtc={actualStartUtc:O} " +
                    $"DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3}。",
                    "EPB");


                // —— 圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）——
                var recorder = Recorder;
                if (!TryBeginFormalCycle(recorder, channel, i, DateTime.UtcNow))
                {
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }

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
                var controlSucceeded = IsFormalControlSucceeded(
                    ok,
                    runner.LastCycleOutcome.IsSuccess);
                var controlNeedsSoftwareRecovery =
                    runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.SoftwareRecovery;
                if (controlNeedsSoftwareRecovery)
                    ReportFormalControlSoftwareRecovery(
                        channel,
                        i,
                        runner.LastCycleOutcome.Reason);

                // —— 圈结束：根据是否报警停机决定封圈状态 ——
                var persistenceCommitted = recorder == null;
                if (recorder != null)
                {
                    try
                    {
                        var finalN = recorder.GetCurrentCycleSampleCount(channel);
                        if (IsAlarmStopRequested(channel))
                        {
                            // 报警后台流程负责在快照文件存在后封圈。
                        }
                        else if (controlNeedsSoftwareRecovery)
                            AbortCycleAfterPersistence(
                                recorder,
                                channel,
                                i,
                                DateTime.UtcNow,
                                "AbortedBySoftwareRecovery");
                        else if (runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.HardFault)
                            AbortCycleAfterPersistence(recorder, channel, i, DateTime.UtcNow, "failed");
                        else if (controlSucceeded)
                            persistenceCommitted = CompleteCycleAndScheduleEvidence(
                                recorder,
                                channel,
                                i,
                                finalN,
                                DateTime.UtcNow);
                        else
                            AbortCycleAfterPersistence(
                                recorder,
                                channel,
                                i,
                                DateTime.UtcNow,
                                runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.Canceled
                                    ? "canceled"
                                    : "failed");
                    }
                    catch (Exception ex)
                    {
                        PreserveFormalCycleForPersistenceRecovery(
                            channel,
                            i,
                            "CycleFinalizer",
                            ex);
                    }
                }

                // 若本通道自然完成最后一圈，则做统一收尾（含“停止即存最近10圈”）
                if (IsFormalCycleCountable(
                        controlSucceeded,
                        persistenceCommitted))
                {
                    var committedCycles = Interlocked.Increment(ref singleSuccessfulCycles);
                    var nonRecoverableAlarm =
                        OnFormalCycleCommittedAndEvaluateClampFault(
                            runner,
                            channel,
                            i,
                            committedCycles);
                    if (!nonRecoverableAlarm && committedCycles >= _cfg.Test.TestTarget)
                    {
                        FinalizeChannelAfterNaturalCompletion(channel);
                        timer.Stop();
                    }
                }

                if (!IsAlarmStopRequested(channel))
                    ClearCurrentCycleNumber(channel);

                _log.Info(
                    $"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} " +
                    $"{(ok && persistenceCommitted ? "完成" : "失败/作废")}",
                    "EPB");
                ReleaseCyclePauseCts(channel, cyclePauseCts);
                return controlSucceeded && persistenceCommitted;
            }), "SingleChannelTimer", channel);
        }

        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause("ManualPause");
            PublishChannelRuntimeState(channel, ChannelRuntimeState.Paused, "Paused", "试验已暂停");
            NonCriticalObserver.Invoke(
                ChannelPaused,
                channel,
                ex => _log?.Warn($"EPB[{channel}] 暂停观察者异常已隔离：{ex.Message}", "EPB"));
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
            PublishChannelRuntimeState(channel, ChannelRuntimeState.Running, "Running", "试验已恢复");
            NonCriticalObserver.Invoke(
                ChannelResumed,
                channel,
                ex => _log?.Warn($"EPB[{channel}] 恢复观察者异常已隔离：{ex.Message}", "EPB"));
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
            _manualStopRequestedChannels[channel] = 0;
            if (_recoverableChannelRestartJobs.TryGetValue(channel, out var recovery))
            {
                try { recovery.Cancel(); } catch { }
            }
            StopChannelForInternalCleanup(channel);
        }

        private void StopChannelForInternalCleanup(int channel)
        {
            // 该通道停止后不再参与液压判定
            UnmarkHydraulicParticipant(channel);

            // 先取消“硬停机”Token，尽快中断当前圈内仍在运行的异步逻辑
            try { CancelStopCts(channel); } catch { /* ignore */ }

            RemoveTimerRuntime(channel, nameof(StopChannel));

            // —— 安全落位（优先）：尽快断电并请求液压释放 —— //
            try
            {
                CommandEpbOff(channel, nameof(StopChannel));
            }
            catch
            {
                /* 忽略 */
            }

            try
            {
                ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), "StopChannelRelease", channel);
            }
            catch
            {
                /* 忽略 */
            }

            RemoveRunnerRuntime(channel, nameof(StopChannel));

            // —— 收尾：落盘导出（Stop 场景保留原逻辑）—— //
            try
            {
                Recorder?.FlushRecent(channel, 10);
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
            }

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.ManualStopped,
                "ManualStopped",
                "人工停止");
            TryEndBatchSessionWhenIdle("ChannelStop");
            TryDisableIdlePowerGroup(channel, "通道停止后电源组已无运行通道");
        }

        /// <summary>
        /// 非运行终态提交前的唯一清场入口。先撤销 Timer/Runner/液压参与权并确认 OFF，
        /// 再允许发布 StartBlocked 等终态，杜绝“红色终态但后台仍继续发命令”。
        /// </summary>
        private bool RevokeChannelExecutionBeforeTerminalState(int channel, string reason)
        {
            UnmarkHydraulicParticipant(channel);
            try { CancelStopCts(channel); } catch { }
            RemoveTimerRuntime(channel, reason);

            var wasEnergized = IsChannelEnergized(channel);
            var offSucceeded = !wasEnergized;
            try { offSucceeded = CommandEpbOffHighPriority(channel, reason) || !wasEnergized; }
            catch { }
            if (offSucceeded) SetChannelEnergized(channel, false);

            try { ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), reason + "Release", channel); }
            catch { }
            RemoveRunnerRuntime(channel, reason);

            return IsTerminalExecutionQuiescent(
                _timers.ContainsKey(channel),
                _timerCache.ContainsKey(channel),
                _runners.ContainsKey(channel),
                _runnerCache.ContainsKey(channel),
                IsChannelEnergized(channel),
                offSucceeded);
        }

        internal static bool IsTerminalExecutionQuiescent(
            bool timerActive,
            bool timerCached,
            bool runnerActive,
            bool runnerCached,
            bool energized,
            bool offConfirmed)
        {
            return !timerActive && !timerCached &&
                   !runnerActive && !runnerCached &&
                   !energized && offConfirmed;
        }

        private void PublishStartBlockedAfterCleanup(
            int channel,
            string reasonCode,
            string reasonText,
            Guid correlationId,
            IEnumerable<int> affectedChannels = null)
        {
            var revoked = RevokeChannelExecutionBeforeTerminalState(
                channel,
                "StartBlocked:" + (reasonCode ?? "Unknown"));
            PublishChannelRuntimeState(
                channel,
                revoked ? ChannelRuntimeState.StartBlocked : ChannelRuntimeState.SystemFault,
                revoked ? reasonCode : "TerminalCleanupUnverified",
                revoked
                    ? reasonText
                    : "启动失败后的执行资源或电机OFF未确认；保持系统故障和失效安全，禁止显示为已停止。" +
                      $" Original={reasonCode}/{reasonText}",
                channel,
                affectedChannels ?? new[] { channel },
                correlationId,
                allowTerminalReset: true,
                allowSystemFaultReset: false);
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

            RemoveTimerRuntime(channel, nameof(StopChannelOnAlarm));

            // —— 安全落位：立即断电 + 请求液压释放 —— //
            try { CommandEpbOffHighPriority(channel, nameof(StopChannelOnAlarm)); } catch { /* ignore */ }
            try { ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), "AlarmStopRelease", channel); }
            catch { /* ignore */ }

            RemoveRunnerRuntime(channel, nameof(StopChannelOnAlarm));

            // —— 现场要求：停止即存最近10圈 ——
            // 说明：报警停机路径不应阻塞 Runner/定时器线程，因此这里用后台任务异步 Flush。
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    ObserveBackgroundTask(Task.Run(() =>
                    {
                        try
                        {
                            recorder.FlushRecent(channel, 10);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
                        }
                    }), "FlushRecentAfterAlarmStop", channel);
            }
            catch
            {
                // ignore
            }

            TryEndBatchSessionWhenIdle("AlarmStop");
            TryDisableIdlePowerGroup(channel, "报警通道停止后电源组已无运行通道");
        }


        private void OnRunnerAlarmRaised(int channel, string reason)
        {
            reason ??= string.Empty;
            var faultCode = ExtractFaultCode(reason);
            var immediateCurrentHardFault = IsImmediateCurrentHardFault(reason);
            var alreadyCycleConfirmed = IsAlreadyCycleConfirmedFault(reason);
            var hardwareLatched = IsHardwareLatchedControlFault(reason);
            if (hardwareLatched)
            {
                RequestElectricalGroupEmergencyShutdown(
                    channel,
                    $"ChannelOutputControlSelfHealing:{reason}");
                return;
            }

            if (!immediateCurrentHardFault && !alreadyCycleConfirmed && !hardwareLatched)
            {
                var attemptId = _currentAttemptIdByChannel.TryGetValue(channel, out var currentAttempt)
                    ? currentAttempt
                    : Interlocked.Increment(ref _cycleAttemptSequence);
                var confirmation = _faultConfirmationTracker.Observe(
                    $"Channel:{channel}",
                    faultCode,
                    attemptId,
                    AlarmConfig?.Behavior?.GenericFaultConfirmCycles ?? 3);
                if (confirmation.DuplicateAttempt) return;
                if (confirmation.Disposition == FaultConfirmationDisposition.RecoverableWarningFault)
                {
                    PublishRecoverableControlFaultWarning(
                        channel,
                        reason,
                        faultCode,
                        attemptId,
                        confirmation);
                    return;
                }
                reason += $" Streak={confirmation.Streak}/{confirmation.ConfirmThreshold}";
            }

            var recoveryPolicy = ResolveChannelFaultRecoveryPolicy(
                faultCode,
                hardwareLatched);
            if (recoveryPolicy == FaultRecoveryPolicy.Recoverable)
                _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
            else
                _nonRecoverableChannelFaultLatch[channel] = 0;

            if (immediateCurrentHardFault)
            {
                var groupId = GetElectricalGroupId(channel);
                if (_powerSupply != null && groupId > 0 &&
                    _powerSupply.HasFreshPowerFaultEvidence(groupId))
                {
                    var snapshot = _powerSupply.GetLatestSnapshot(groupId);
                    var affected = _cfg.Test.Groups
                        .First(x => x.Id == groupId)
                        .Members
                        .Where(x => IsHydraulicParticipant(x) || _timers.ContainsKey(x) || x == channel)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();
                    OnPowerSupplyFaultRaised(new PowerSupplyFault
                    {
                        TimestampUtc = DateTime.UtcNow,
                        SupplyId = snapshot?.SupplyId ?? groupId,
                        ElectricalGroupId = groupId,
                        Code = "SharedPowerLimiting",
                        Reason = $"支路平台与程控电源限流/低压证据同时出现。首发EPB={channel}；{reason}",
                        AffectedChannels = affected.Length > 0 ? affected : new[] { channel },
                        Snapshot = snapshot
                    });
                    return;
                }
            }

            var channelFaultCorrelationId = Guid.NewGuid();
            if (recoveryPolicy != FaultRecoveryPolicy.Recoverable)
                NotifyRunAuthorizationRevoking(
                    StopSource.AlarmInterlock,
                    reason,
                    recoveryPolicy == FaultRecoveryPolicy.NonRecoverableDisableChannel
                        ? "NonRecoverableDisableChannel"
                        : "ChannelHardwareFault",
                    channelFaultCorrelationId,
                    FaultScope.Channel);

            if (_currentCycleNumberByChannel.TryGetValue(channel, out var frozenCycle))
                _frozenFaultCycleByChannel[channel] = frozenCycle;

            // ★同步去重 latch：保证计时器回调能尽快识别“本圈应封为 alarm”，但不在此线程做 IO
            if (!_alarmStopLatch.TryRequestStop(channel))
                return;

            // 通道硬故障只取消本通道。共享压力/电源/DAQ故障由各自组级处理器扩大范围。
            try { CancelStopCts(channel); } catch { }

            var alarmUtc = DateTime.UtcNow;
            var channelFault = new ControlFault(
                faultCode,
                reason,
                FaultScope.Channel,
                new[] { channel },
                null,
                alarmUtc,
                channelFaultCorrelationId,
                recoveryPolicy == FaultRecoveryPolicy.Recoverable
                    ? FaultClassification.SystemFault
                    : FaultClassification.HardwareConfirmed,
                recoveryPolicy);
            _log.Error(
                $"EPB[{channel}] {(recoveryPolicy == FaultRecoveryPolicy.NonRecoverableDisableChannel ? "不可自恢复故障" : hardwareLatched ? "硬件锁存故障" : "确认故障")}，" +
                "立即停止该通道并导出报警快照。" +
                $"RecoveryPolicy={recoveryPolicy} CorrelationId={channelFault.CorrelationId:N} 原因={reason}",
                "报警");
            FlushPersistentLog();
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                channelFault,
                ex => _log?.Warn($"控制故障观察者异常已隔离：{ex.Message}", "报警"));
            PublishFaultRuntimeStates(channelFault, channel);
            NonCriticalObserver.Invoke(
                ChannelAlarmRaised,
                channel,
                reason,
                ex => _log?.Warn($"EPB[{channel}] 报警观察者异常已隔离：{ex.Message}", "报警"));

            // 不阻塞 Runner/定时器线程
            ObserveBackgroundTask(Task.Run(async () =>
            {
                foreach (var affectedChannel in ChannelFaultIsolationPolicy.GetChannelsToStop(channel))
                {
                    try { StopChannelOnAlarm(affectedChannel); } catch { /* ignore */ }
                }

                try
                {
                    if (Alarm != null)
                        await Alarm.SetAlarmAsync(channel, true, reason).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                AlarmCycleSnapshotEvidence snapshotEvidence = null;
                try
                {
                    snapshotEvidence = await ExportAlarmSnapshotAsync(channel, reason, alarmUtc).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                if (snapshotEvidence == null)
                    TryFinalizeCurrentCycleAfterSnapshot(channel, false);
                else
                    ClearCurrentCycleNumber(channel);

                if (recoveryPolicy == FaultRecoveryPolicy.NonRecoverableDisableChannel)
                    PersistentlyDisableChannel(channel, reason);

                if (recoveryPolicy == FaultRecoveryPolicy.Recoverable)
                    BeginRecoverableChannelRestartLoop(channel, reason, channelFaultCorrelationId);
            }), "ChannelAlarmHandling", channel);
        }

        private void BeginRecoverableChannelRestartLoop(
            int channel,
            string reason,
            Guid correlationId)
        {
            var recoveryRunId = _activeBatchId;
            var recoveryRunEpoch = Interlocked.Read(ref _runEpoch);
            var cancellation = new CancellationTokenSource();
            if (!_recoverableChannelRestartJobs.TryAdd(channel, cancellation))
            {
                cancellation.Dispose();
                return;
            }
            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                try
                {
                    while (CanContinueRecoverableChannelRestart(
                               IsChannelEnabled(channel),
                               _manualStopRequestedChannels.ContainsKey(channel),
                               _nonRecoverableChannelFaultLatch.ContainsKey(channel)) &&
                           IsSoftwareRecoveryRunCurrent(
                               recoveryRunId,
                               recoveryRunEpoch,
                               new[] { channel }))
                    {
                        attempt++;
                        try
                        {
                            await WaitForStandaloneAlarmRecoveryWindowAsync(
                                    channel,
                                    correlationId,
                                    cancellation.Token)
                                .ConfigureAwait(false);
                            _log.Info(
                                $"EPB[{channel}] 可恢复卡钳故障开始第{attempt}次无人值守预检与资格复核。" +
                                $"CorrelationId={correlationId:N} Reason={reason}",
                                "报警");
                            await ResumeAlarmStoppedChannelAsync(
                                    channel,
                                    true,
                                    cancellation.Token,
                                    unattendedRecovery: true)
                                .ConfigureAwait(false);
                            var resumed = _channelRuntimeStateStore.Get(channel);
                            if (resumed == null ||
                                (resumed.State != ChannelRuntimeState.Running &&
                                 resumed.State != ChannelRuntimeState.WarningRunning))
                                throw new SoftwareSelfHealingRetryException(
                                    $"恢复入口返回但通道未进入运行态：{resumed?.State}");
                            _log.Info(
                                $"EPB[{channel}] 可恢复故障已自动续测。Attempt={attempt}",
                                "报警");
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (!CanContinueRecoverableChannelRestart(
                                    IsChannelEnabled(channel),
                                    _manualStopRequestedChannels.ContainsKey(channel),
                                    _nonRecoverableChannelFaultLatch.ContainsKey(channel)) ||
                                !IsSoftwareRecoveryRunCurrent(
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    new[] { channel }))
                                return;
                            if (TryEscalateSoftwareRecoveryCircuitOpen(
                                    "RecoverableChannelRestart",
                                    $"Channel={channel}; FaultCorrelation={correlationId:N}; " +
                                    $"Error={ex.Message}",
                                    new[] { channel },
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    attempt))
                                return;
                            if (ShouldEscalateSoftwareRecovery(attempt))
                            {
                                // Legacy/single-channel paths without a valid RunId cannot safely
                                // request an unattended batch recycle.  They must still stop the
                                // local retry loop instead of remaining in self-maintenance forever.
                                _log.Error(
                                    $"EPB[{channel}] 自动恢复已达有界阈值，但当前没有可用的整批RunId；" +
                                    $"保持报警停机，等待人工重新启动。Attempt={attempt}",
                                    "报警");
                                return;
                            }
                            // 启动回滚/截止取消可能让通道停留在 Recovering、Qualification
                            // 等过程态。下一轮无人值守恢复若直接进入许可检查，会被自身遗留
                            // 状态拒绝。此处硬件已经由失败路径断电，统一提交为可重试停机态。
                            PublishChannelRuntimeState(
                                channel,
                                ChannelRuntimeState.AlarmStopped,
                                "UnattendedRecoveryRetryPending",
                                $"第{attempt}次恢复未通过，保持安全停机并继续重试。",
                                affectedChannels: new[] { channel },
                                correlationId: correlationId,
                                allowTerminalReset: true);
                            var delayMs = SelectTimerRecoveryRetryDelayMs(attempt);
                            _log.Warn(
                                $"EPB[{channel}] 自动恢复第{attempt}次未通过，{delayMs}ms后继续有界重试；" +
                                $"三次失败后整批重建：" +
                                ex.Message,
                                "报警");
                            await Task.Delay(delayMs, cancellation.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _log.Info(
                        $"EPB[{channel}] 无人值守恢复已由人工停止或运行结束取消。",
                        "报警");
                }
                finally
                {
                    _recoverableChannelRestartJobs.TryRemove(channel, out _);
                    cancellation.Dispose();
                }
            }), "RecoverableChannelRestart", channel);
        }

        private async Task WaitForStandaloneAlarmRecoveryWindowAsync(
            int channel,
            Guid correlationId,
            CancellationToken token)
        {
            var logged = false;
            while (!CanRunStandaloneAlarmRecovery(
                       IsBatchSessionActive,
                       IsFormalPhaseCommitted))
            {
                token.ThrowIfCancellationRequested();
                if (!logged)
                {
                    logged = true;
                    _log.Info(
                        $"EPB[{channel}] 报警发生在批量启动/学习/资格阶段；" +
                        "恢复所有权保留在批次协调器，禁止单通道提前创建正式Timer。" +
                        $"Batch={_activeBatchId:N} FormalCommitted={IsFormalPhaseCommitted} " +
                        $"CorrelationId={correlationId:N}",
                        "报警");
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        internal static bool CanRunStandaloneAlarmRecovery(
            bool batchSessionActive,
            bool formalPhaseCommitted)
        {
            return !batchSessionActive || formalPhaseCommitted;
        }

        internal static bool CanContinueRecoverableChannelRestart(
            bool channelEnabled,
            bool manualStopRequested,
            bool nonRecoverableHardwareLatched)
        {
            return channelEnabled &&
                   !manualStopRequested &&
                   !nonRecoverableHardwareLatched;
        }

        internal static FaultRecoveryPolicy ResolveChannelFaultRecoveryPolicy(
            string faultCode,
            bool hardwareLatched)
        {
            if (string.Equals(
                    faultCode,
                    "ForwardLoadRiseNotStarted",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    faultCode,
                    "OpenCircuitOrOutputFault",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    faultCode,
                    "ForwardLowPlateauConfirmed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    faultCode,
                    "ForwardPeakOvershoot2AConfirmed",
                    StringComparison.OrdinalIgnoreCase))
                return FaultRecoveryPolicy.NonRecoverableDisableChannel;

            return FaultRecoveryPolicy.Recoverable;
        }

        private void PersistentlyDisableChannel(int channel, string reason)
        {
            try
            {
                var test = _cfg?.Test ??
                           throw new InvalidOperationException("当前项目配置未加载");
                lock (test)
                {
                    test.EnsureEpbRecords(12);
                    test.GetEpbRecord(channel).Enabled = false;
                }

                NonCriticalObserver.Invoke(
                    ChannelDisableRequested,
                    channel,
                    reason,
                    ex => _log?.Warn(
                        $"EPB[{channel}] 持久禁用UI观察者异常已隔离：{ex.Message}",
                        "报警"));

                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    test.StoreDir,
                    test.TestName);
                if (string.IsNullOrWhiteSpace(projectPath) || !System.IO.File.Exists(projectPath))
                    throw new System.IO.FileNotFoundException(
                        "当前项目专用 TestConfig.xml 不存在，禁止回退修改默认模板。",
                        projectPath);

                ConfigLoader.UpdateTestEpbEnabled(projectPath, channel, false);
                _log.Error(
                    $"EPB[{channel}] 已锁存不可自恢复报警并持久禁用当前项目通道。" +
                    $"Enabled=false Path={projectPath} Reason={reason}",
                    "报警");
                FlushPersistentLog();
            }
            catch (Exception ex)
            {
                var message =
                    $"卡钳{channel}已停机，但项目禁用状态持久化失败；" +
                    $"重启前请人工确认该通道保持未启用。原因：{ex.Message}";
                _log.Error(message, "报警", ex);
                FlushPersistentLog();
                NonCriticalObserver.Invoke(
                    ChannelDisablePersistenceFailed,
                    channel,
                    message,
                    observerEx => _log?.Warn(
                        $"EPB[{channel}] 禁用持久化失败提示观察者异常：{observerEx.Message}",
                        "报警"));
            }
        }

        private void PublishRecoverableControlFaultWarning(
            int channel,
            string reason,
            string faultCode,
            long attemptId,
            FaultConfirmationObservation confirmation)
        {
            var correlationId = Guid.NewGuid();
            if (_currentCycleNumberByChannel.TryGetValue(channel, out var cycleNumber))
                _frozenFaultCycleByChannel[channel] = cycleNumber;
            OnRunnerWarningEvidenceRaised(new AdaptiveWarningEvent
            {
                Channel = channel,
                Code = AdaptiveWarningCode.RecoverableControlFaultWarning,
                OccurredUtc = DateTime.UtcNow,
                FaultCode = faultCode,
                ScopeKey = $"Channel:{channel}",
                CorrelationId = correlationId,
                AttemptId = attemptId,
                Streak = confirmation.Streak,
                ConfirmThreshold = confirmation.ConfirmThreshold,
                Reason = reason
            });
            NonCriticalObserver.Invoke(
                ChannelWarningRaised,
                channel,
                $"{faultCode} 连续={confirmation.Streak}/{confirmation.ConfirmThreshold}；" +
                "已安全断电，本圈作废并自动重试。",
                ex => _log?.Warn($"EPB[{channel}] 预警观察者异常已隔离：{ex.Message}", "EPB"));
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.WarningRunning,
                faultCode,
                reason,
                affectedChannels: new[] { channel },
                correlationId: correlationId,
                allowTerminalReset: false);
            if (!TryEnsureSoftwareRecoveryOutputOff(channel, "ConfirmedFaultWarning")) return;
            var pauseCompletion = _timers.TryGetValue(channel, out var timer)
                ? timer.PauseAfterCurrentCycleAsync($"RecoverableWarning:{faultCode}")
                : Task.CompletedTask;
            UnmarkHydraulicParticipant(channel);
            Task releaseTask;
            try { releaseTask = HydraulicMarkReleaseAsync(channel); }
            catch (Exception ex) { releaseTask = Task.FromException(ex); }
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(new[] { channel });
            TrySealSoftwareRecoveryCycleWindows(
                cutoffCycles,
                cutoffUtc,
                $"{faultCode} Streak={confirmation.Streak}/{confirmation.ConfirmThreshold}");
            _log.Warn(
                $"EPB[{channel}] 非电流故障未达到报警门槛，已安全断电并封闭当前尝试圈时间窗。" +
                $"Code={faultCode} Streak={confirmation.Streak}/{confirmation.ConfirmThreshold}",
                "EPB");
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(pauseCompletion, releaseTask).ConfigureAwait(false);
                    if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                            cutoffCycles,
                            cutoffUtc,
                            $"RecoverableWarning:{faultCode}",
                            _daqPersistenceRecoveryTimeoutMs,
                            CancellationToken.None)
                        .ConfigureAwait(false))
                        throw new SoftwareSelfHealingRetryException(
                            "警告圈截止 Raw/耐久边界尚未闭合；保持停机并转入持续恢复。");
                    if (!_timers.ContainsKey(channel) ||
                        IsAlarmStopRequested(channel) ||
                        _channelPausedUtc.ContainsKey(channel) ||
                        ShouldHoldDaqRecoveredChannelsForBatchPause(CurrentBatchPauseState))
                        return;
                    var plan = GetCompatibleStaggerPlan(new[] { channel });
                    RejoinFormalChannelsAtSharedFutureSlot(
                        new[] { channel },
                        plan,
                        "RecoverableWarningSelfHealed",
                        "警告圈已安全断电并完成机械释放，按未来完整节律槽自动重试",
                        allowTerminalReset: false);
                }
                catch (Exception ex)
                {
                    PublishIsolatedSoftwareFault(
                        "RecoverableWarningRecoveryFailed",
                        ex.Message,
                        new[] { channel },
                        correlationId);
                    _log.Warn(
                        $"EPB[{channel}] 警告后直接恢复未完成，已转入持续组级自恢复：" +
                        ex.Message,
                        "EPB");
                }
            }), "RecoverableWarningRetry", channel);
        }

        internal static bool IsImmediateCurrentHardFault(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return reason.IndexOf("OverCurrent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("AbnormalHighCurrentPlateau", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsAlreadyCycleConfirmedFault(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return reason.IndexOf("Streak=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (reason.IndexOf("ForwardPeakOvershoot", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    reason.IndexOf("Policy=Consecutive", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsHardwareLatchedControlFault(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            return reason.IndexOf("OutputCommandFailed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("TerminalOffCommandFailed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("SoftwareRecoveryOffFailed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("OffCurrentNotCleared", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractFaultCode(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "ChannelFault";
            reason = reason.Trim();
            const string adaptivePrefix = "AdaptiveHardFault ";
            if (reason.StartsWith(adaptivePrefix, StringComparison.OrdinalIgnoreCase))
                reason = reason.Substring(adaptivePrefix.Length).TrimStart();
            const string unhandledPrefix = "AdaptiveUnhandledException ";
            if (reason.StartsWith(unhandledPrefix, StringComparison.OrdinalIgnoreCase))
                return "UnhandledException";
            var end = reason.IndexOfAny(new[] { ' ', ':', ';' });
            return end > 0 ? reason.Substring(0, end) : reason;
        }

        internal static string ClassifyDaqStaleRoot(
            DaqFreshnessSnapshot freshness,
            double staleThresholdMs = 100)
        {
            if (freshness == null) return "DaqSampleStale";
            var threshold = Math.Max(1, staleThresholdMs);
            if (freshness.CallbackAgeMs > threshold) return "DaqCallbackStale";
            if (freshness.ControlEnqueueAgeMs > threshold) return "ControlEnqueueStale";
            if (freshness.ControlProcessedAgeMs > threshold) return "ControlProcessingStale";
            return "DaqSampleStale";
        }

        private void OnRunnerRecoverableFaultRaised(int channel, string reason)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device))
            {
                PublishIsolatedSoftwareFault(
                    "DaqRecoveryUnavailable",
                    reason,
                    new[] { channel },
                    Guid.NewGuid());
                return;
            }

            var affectedChannels = GetDaqGroupChannels(device);
            if (affectedChannels.Length == 0)
                affectedChannels = GetAllDaqDeviceChannels(device);
            if (affectedChannels.Length == 0)
                affectedChannels = new[] { channel };
            var faultCode = ExtractFaultCode(reason);
            var faultReason = reason;
            if (faultCode.IndexOf("DaqSampleStale", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var freshness = _acq.GetDaqFreshnessSnapshot(device, 100);
                faultCode = ClassifyDaqStaleRoot(freshness, 100);
                faultReason =
                    $"{faultCode}>100ms CallbackAge={freshness.CallbackAgeMs:F1}ms " +
                    $"ControlEnqueueAge={freshness.ControlEnqueueAgeMs:F1}ms " +
                    $"ControlProcessedAge={freshness.ControlProcessedAgeMs:F1}ms " +
                    $"ProcessedSampleUtc={(freshness.ProcessedSampleUtc == default ? "none" : freshness.ProcessedSampleUtc.ToString("O"))} " +
                    $"Original={reason}";
            }
            var attemptId = _currentAttemptIdByChannel.TryGetValue(channel, out var currentAttempt)
                ? currentAttempt
                : Interlocked.Increment(ref _cycleAttemptSequence);
            var confirmation = _faultConfirmationTracker.Observe(
                $"Daq:{device}",
                faultCode,
                attemptId,
                AlarmConfig?.Behavior?.GenericFaultConfirmCycles ?? 3);
            if (confirmation.DuplicateAttempt) return;
            if (_currentCycleNumberByChannel.TryGetValue(channel, out var frozenCycle))
                _frozenFaultCycleByChannel[channel] = frozenCycle;
            if (confirmation.Disposition == FaultConfirmationDisposition.RecoverableWarningFault)
            {
                OnRunnerWarningEvidenceRaised(new AdaptiveWarningEvent
                {
                    Channel = channel,
                    Code = AdaptiveWarningCode.RecoverableControlFaultWarning,
                    OccurredUtc = DateTime.UtcNow,
                    FaultCode = faultCode,
                    ScopeKey = $"Daq:{device}",
                    CorrelationId = Guid.NewGuid(),
                    AttemptId = attemptId,
                    Streak = confirmation.Streak,
                    ConfirmThreshold = confirmation.ConfirmThreshold,
                    Reason = faultReason
                });
                NonCriticalObserver.Invoke(
                    ChannelWarningRaised,
                    channel,
                    $"{faultCode} 连续={confirmation.Streak}/{confirmation.ConfirmThreshold}；" +
                    "DAQ组将安全断电并自动恢复。",
                    ex => _log?.Warn($"DAQ预警观察者异常已隔离：{ex.Message}", "AI"));
            }
            var observation = ObserveDaqIncident(
                device,
                faultCode,
                faultReason,
                DateTime.UtcNow,
                affectedChannels,
                primaryChannel: channel);

            if (observation.PrimaryChanged)
                _log.Warn(
                    $"DAQ事故补充了更高优先级软件证据。Device={device} " +
                    $"Code={observation.Context.PrimaryCode} CorrelationId={observation.Context.CorrelationId:N}",
                    "AI");
            ObserveBackgroundTask(BeginDaqAutoRecoveryAsync(
                device,
                faultCode,
                faultReason,
                observation.Context.CorrelationId,
                restartDaq: true,
                DateTime.UtcNow,
                raiseRecoverableAlarm: confirmation.Disposition ==
                                         FaultConfirmationDisposition.ConfirmedRecoverableAlarm,
                recoverableAlarmChannel: channel),
                "BeginDaqAutoRecoveryFromChannelFault",
                channel);
        }

        private void OnDaqDeviceFaultDetected(DaqDeviceFault deviceFault)
        {
            if (deviceFault == null) return;
            var affected = GetDaqGroupChannels(deviceFault.Device);
            if (affected.Length == 0)
                affected = GetAllDaqDeviceChannels(deviceFault.Device);
            if (!_daqClockRecoveryAttempts.TryRegister(
                    deviceFault.Device,
                    Stopwatch.GetTimestamp(),
                    out var attempt))
            {
                // Repeated software jitter must not become a global StopAll. Keep the affected
                // group de-energized and let the existing/new recovery context self-maintain;
                // confirmed hardware absence is handled separately by two independent probes.
                _log.Warn(
                    $"Device={deviceFault.Device} {_daqClockRecoveryWindowMinutes}分钟内已发生" +
                    $"{attempt}次DAQ软件恢复，超过阈值{_daqClockRecoveryMaxAttempts}次；" +
                    "不升级全局故障，进入持续自维护。",
                    "AI");
            }
            var observation = ObserveDaqIncident(
                deviceFault.Device,
                deviceFault.Code,
                deviceFault.Reason,
                deviceFault.TimestampUtc,
                affected,
                deviceFault.Generation);
            ObserveBackgroundTask(BeginDaqAutoRecoveryAsync(
                deviceFault.Device,
                deviceFault.Code,
                deviceFault.Reason,
                observation.Context.CorrelationId,
                restartDaq: true,
                deviceFault.TimestampUtc,
                recoveryAttempt: attempt),
                "BeginDaqAutoRecoveryFromDeviceFault");
        }

        private void OnDaqDeviceFaultSafetyDetected(DaqDeviceFault deviceFault)
        {
            if (deviceFault == null) return;
            // DAQ数据链故障全部先按可恢复软件故障处理；硬件报警必须等待独立探测证据。
            ExecuteDaqDeviceFaultSafetyFirst(deviceFault.Device, backgroundQueue: true);
        }

        private void OnDaqPersistenceStateChanged(DaqPersistenceStateChanged update)
        {
            if (update == null) return;
            if (update.State == DaqPersistenceState.Paused)
            {
                ObserveBackgroundTask(BeginDaqAutoRecoveryAsync(
                    update.Device,
                    update.Code,
                    update.Reason,
                    update.CorrelationId,
                    restartDaq: false,
                    update.TimestampUtc), "BeginDaqAutoRecovery");
            }
            else if (update.State == DaqPersistenceState.Recovered)
            {
                ObserveBackgroundTask(
                    TryCompleteDaqAutoRecoveryAsync(update.Device),
                    "CompleteDaqAutoRecovery");
            }
            else if (update.State == DaqPersistenceState.Failed)
            {
                if (string.Equals(
                        update.Code,
                        "ActiveCycleDataLimitExceeded",
                        StringComparison.OrdinalIgnoreCase))
                    ObserveBackgroundTask(
                        HandleActiveCycleDataLimitExceededAsync(update),
                        "HandleActiveCycleDataLimitExceeded");
                else
                    ObserveBackgroundTask(
                        BeginAndEscalateDaqPersistenceFailureAsync(update),
                        "EscalateDaqPersistenceFailure");
            }
            // 必须先启动控制层恢复，再通知 UI/诊断观察者；观察者不得拖住自愈入口。
            NonCriticalObserver.Invoke(
                DaqPersistenceStateChanged,
                update,
                ex => _log?.Warn($"DAQ持久化状态观察者异常，已隔离：{ex.Message}", "AI"));
        }

        private async Task BeginAndEscalateDaqPersistenceFailureAsync(
            DaqPersistenceStateChanged update)
        {
            // Failed 可能是首个状态（例如硬容量满）。先复用统一 Cutoff 路径，保证
            // DO、当前整圈和写盘准入均已安全处理，再进入持续自维护；不得升级全局故障。
            await BeginDaqAutoRecoveryAsync(
                    update.Device,
                    update.Code,
                    update.Reason,
                    update.CorrelationId,
                    restartDaq: false,
                    update.TimestampUtc)
                .ConfigureAwait(false);
            await EscalateDaqAutoRecoveryAsync(
                    update.Device,
                    update.Code,
                    update.Reason,
                    update.CorrelationId)
                .ConfigureAwait(false);
        }

        internal static bool RequiresDaqTaskRecreate(string triggerCode)
        {
            // Consumer/backlog faults can recover by dropping stale history only after the
            // affected group is safely de-energized. Callback, clock and producer-identity
            // faults still require a real NI task rebuild.
            return !string.Equals(triggerCode, "ActiveCycleDataLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlProcessingStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlEnqueueStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlQueueFull", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "BackgroundQueueFull", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqPersistenceLag", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqPersistenceQueueFull", StringComparison.OrdinalIgnoreCase);
        }

        internal static int GetDaqSelfMaintenanceDelayMs(int consecutiveFailures)
        {
            if (consecutiveFailures <= 1) return 1000;
            if (consecutiveFailures == 2) return 2000;
            if (consecutiveFailures == 3) return 5000;
            if (consecutiveFailures == 4) return 10000;
            return 30000;
        }

        private async Task<bool> TryFinalizeDaqCutoffCyclesAfterPersistenceAsync(
            DaqAutoRecoveryContext context,
            int timeoutMs,
            CancellationToken token)
        {
            if (context == null || !IsCurrentRecovery(context)) return false;
            if (Volatile.Read(ref context.CutoffCyclesFinalized) == 2) return true;

            var rawDrained = await _acq.DrainBackgroundPipelinesAsync(
                    Math.Max(1, timeoutMs),
                    token)
                .ConfigureAwait(false);
            if (!rawDrained || !IsCurrentRecovery(context)) return false;

            var published = _acq.GetLastDiskPublishedSequence(context.Device);
            long previous;
            do
            {
                previous = Interlocked.Read(ref context.CutoffPersistenceBoundary);
                if (previous >= published) break;
            } while (Interlocked.CompareExchange(
                         ref context.CutoffPersistenceBoundary,
                         published,
                         previous) != previous);

            var boundary = Interlocked.Read(ref context.CutoffPersistenceBoundary);
            if (!await _persistence.WaitForDurableBoundaryAsync(
                    context.Device,
                    boundary,
                    Math.Max(1, timeoutMs),
                    token).ConfigureAwait(false))
            {
                var snapshot = _persistence.GetSnapshot(context.Device);
                _log.Warn(
                    $"DAQ截止圈等待真实落盘边界超时 Device={context.Device} " +
                    $"Boundary={boundary} Persisted={snapshot.Sequence} " +
                    $"Depth={snapshot.QueueDepth} State={snapshot.State} " +
                    $"DurabilityBlocked={snapshot.DurabilityBlocked}；不封圈、不重入。",
                    "落盘");
                return false;
            }

            if (Interlocked.CompareExchange(ref context.CutoffCyclesFinalized, 1, 0) != 0)
                return Volatile.Read(ref context.CutoffCyclesFinalized) == 2;
            try
            {
                var allFinalized = true;
                foreach (var pair in context.CutoffCycles ?? new Dictionary<int, int>())
                {
                    if (pair.Value == 0) continue;
                    var finalized = DiscardCurrentCycleForSoftwareRecovery(
                        pair.Key,
                        context.CutoffUtc,
                        $"DAQ:{context.TriggerCode}",
                        pair.Value);
                    if (!finalized &&
                        _currentCycleNumberByChannel.TryGetValue(pair.Key, out var actualCycle) &&
                        actualCycle != pair.Value)
                    {
                        // 取消与当前圈自然收尾竞态时，以暂停后仍存在的实际圈为准。
                        finalized = DiscardCurrentCycleForSoftwareRecovery(
                            pair.Key,
                            context.CutoffUtc,
                            $"DAQ:{context.TriggerCode}:ActualCycle",
                            actualCycle);
                    }
                    else if (!finalized &&
                             !_currentCycleNumberByChannel.ContainsKey(pair.Key))
                    {
                        // 当前圈已经由其自身取消/失败收尾封存，无需再次作废。
                        finalized = true;
                    }
                    allFinalized &= finalized;
                }
                if (!allFinalized) return false;
                Volatile.Write(ref context.CutoffCyclesFinalized, 2);
                _log.Info(
                    $"DAQ截止圈已在真实落盘边界后封存 Device={context.Device} " +
                    $"Boundary={boundary} Cycles=[{string.Join(",", (context.CutoffCycles ?? new Dictionary<int, int>()).Values.Where(value => value != 0))}]。",
                    "落盘");
                return true;
            }
            finally
            {
                if (Volatile.Read(ref context.CutoffCyclesFinalized) != 2)
                    Interlocked.Exchange(ref context.CutoffCyclesFinalized, 0);
            }
        }

        private DaqRecoveryResult CreateDaqRecoveryTimeoutResult(string device)
        {
            return new DaqRecoveryResult
            {
                Device = device ?? string.Empty,
                Recovered = false,
                FailureReason = "DaqRecoveryTimeout",
                FailureKind = "DaqRecoveryTimeout",
                Classification = FaultClassification.SystemFault,
                RequiredFreshCallbacks = _daqPersistenceRequiredFreshBatches,
                ElapsedMs = _daqPersistenceRecoveryTimeoutMs
            };
        }

        private async Task BeginDaqAutoRecoveryAsync(
            string device,
            string code,
            string reason,
            Guid correlationId,
            bool restartDaq,
            DateTime eventUtc,
            int recoveryAttempt = 0,
            bool raiseRecoverableAlarm = false,
            int recoverableAlarmChannel = 0)
        {
            if (string.IsNullOrWhiteSpace(device)) return;
            var affected = GetDaqGroupChannels(device);
            if (affected.Length == 0) return;
            var context = new DaqAutoRecoveryContext
            {
                Device = device,
                RunId = _activeBatchId,
                CorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
                StartedUtc = DateTime.UtcNow,
                CutoffUtc = eventUtc == default ? DateTime.UtcNow : eventUtc.ToUniversalTime(),
                AffectedChannels = affected,
                PreviouslyRunningChannels = affected
                    .Where(channel => _timers.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray(),
                PreviouslyActiveChannels = affected
                    .Where(channel =>
                        _timers.ContainsKey(channel) ||
                        _runners.ContainsKey(channel) ||
                        IsHydraulicParticipant(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray(),
                TriggerCode = string.IsNullOrWhiteSpace(code) ? "DaqPersistenceLag" : code,
                RestartDaq = restartDaq ? 1 : 0,
                RecoveryAttempt = recoveryAttempt,
                RunEpoch = Interlocked.Read(ref _runEpoch),
                RecoveryEpoch = 0,
                TriggerReason = reason ?? string.Empty,
                RaiseRecoverableAlarm = raiseRecoverableAlarm ? 1 : 0,
                RecoverableAlarmChannel = recoverableAlarmChannel,
                BeforeClock = _acq.GetDaqFreshnessSnapshot(device, 100),
                PreviousGeneration = _acq.GetCurrentGeneration(device),
                CutoffParticipantVersions = affected.ToDictionary(
                    channel => channel,
                    CaptureHydraulicParticipantVersion),
                CutoffCycles = new Dictionary<int, int>(),
                CutoffPersistenceBoundary = 0
            };
            if (!_daqAutoRecovery.TryAdd(device, context)) return;
            // 只有真正取得该设备恢复所有权的请求才消耗 epoch；重复请求不会再制造
            // “恢复代次前进但没有对应恢复任务”的假象。
            context.RecoveryEpoch = Interlocked.Increment(ref _recoveryEpoch);
            if (!context.Phase.BeginCutoff())
            {
                CompleteCancelledRecovery(context, "DaqCutoffPhaseRejected");
                return;
            }
            StartDaqRecoveryWatchdog(context);
            StartAffectedGroupRecoveryDeadline(context);
            try
            {
                await AcquireDaqRecoveryOwnershipsAsync(context).ConfigureAwait(false);
                ObserveBackgroundTask(
                    ExportDaqIncidentSnapshotAsync(context, reason, "00-trigger"),
                    "ExportDaqIncidentTriggerSnapshot");
                if (!IsCurrentRecovery(context))
                {
                    CompleteCancelledRecovery(context, "RunEpochChangedBeforeCutoff");
                    return;
                }

                _persistence.SuppressAfter(device, context.CutoffUtc, context.CorrelationId);
                var hydraulicReleaseTasks = new List<Task>(affected.Length);
                foreach (var channel in affected)
                {
                    // 先对整组发布状态并断电，再做任何可能等待落盘边界的圈封存。
                    // 不能让第一个通道的封存等待推迟其它通道的安全断电。
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        context.TriggerCode,
                        reason,
                        affectedChannels: affected,
                        correlationId: context.CorrelationId);
                    NonCriticalObserver.Invoke(
                        ChannelPaused,
                        channel,
                        ex => _log?.Warn($"DAQ自愈暂停观察者异常，已隔离：{ex.Message}", "AI"));
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.Pause($"DaqSoftwareRecovery:{context.TriggerCode}");
                    CancelCyclePauseCts(channel);
                    try { CommandEpbOffHighPriority(channel, "DaqSoftwareRecovery"); } catch { }
                    context.CutoffParticipantVersions.TryGetValue(
                        channel,
                        out var expectedParticipantVersion);
                    TryUnmarkHydraulicParticipant(
                        channel,
                        expectedParticipantVersion,
                        $"DaqCutoff:{context.Device}:RecoveryEpoch={context.RecoveryEpoch}");
                    try { hydraulicReleaseTasks.Add(HydraulicMarkReleaseAsync(channel)); }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"DAQ截止液压释放请求失败 EPB={channel}: {ex.Message}",
                        "液压协调");
                    }
                }
                // 所有通道的 Timer 已暂停、当前圈取消已发出后，再捕获真正需要封存的圈和
                // 数据边界；避免等待恢复所有权期间圈号自然前进导致永久重试旧圈号。
                context.CutoffCycles = affected.ToDictionary(
                    channel => channel,
                    channel => _currentCycleNumberByChannel.TryGetValue(channel, out var cycle)
                        ? cycle
                        : 0);
                Interlocked.Exchange(
                    ref context.CutoffPersistenceBoundary,
                    _acq.GetLastAcceptedSequence(device));
                await RecoveryStageDeadline.RunAsync(
                        "DaqCutoffHydraulicRelease",
                        RecoveryStageTimeoutMs,
                        _ => Task.WhenAll(hydraulicReleaseTasks),
                        context.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!await TryFinalizeDaqCutoffCyclesAfterPersistenceAsync(
                        context,
                        _daqPersistenceRecoveryTimeoutMs,
                        context.Cancellation.Token).ConfigureAwait(false))
                {
                    await EscalateDaqAutoRecoveryAsync(
                            device,
                            "DaqPersistenceBoundaryPending",
                            "安全断电已完成，但截止圈原始批次尚未真实落盘；" +
                            "保持圈事务开放并持续自维护，禁止提前作废或重入试验。",
                            context.CorrelationId,
                            new DaqRecoveryResult
                            {
                                Device = device,
                                Recovered = false,
                                FailureKind = "DaqPersistenceBoundaryPending",
                                FailureReason = "DaqPersistenceBoundaryPending",
                                Classification = FaultClassification.SoftwareTransient
                            })
                        .ConfigureAwait(false);
                    return;
                }

                if (context.RaiseRecoverableAlarm != 0 && context.RecoverableAlarmChannel > 0)
                {
                    var alarmChannel = context.RecoverableAlarmChannel;
                    var alarmReason = $"{context.TriggerCode} 连续复现达到门槛；报警停机后自动恢复。{reason}";
                    NonCriticalObserver.Invoke(
                        ChannelAlarmRaised,
                        alarmChannel,
                        alarmReason,
                        ex => _log?.Warn($"DAQ确认报警观察者异常已隔离：{ex.Message}", "AI"));
                    try
                    {
                        if (Alarm != null)
                            await RecoveryStageDeadline.RunAsync(
                                    "DaqRecoverableAlarmOutput",
                                    RecoveryStageTimeoutMs,
                                    ct => Alarm.SetAlarmAsync(
                                        alarmChannel,
                                        true,
                                        alarmReason,
                                        ct),
                                    context.Cancellation.Token)
                                .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"DAQ确认报警输出失败：{ex.Message}", "报警");
                    }
                    ObserveBackgroundTask(
                        ExportAlarmSnapshotAsync(alarmChannel, alarmReason, DateTime.UtcNow),
                        "ExportDaqAlarmSnapshot",
                        alarmChannel);
                }

                if (!IsCurrentRecovery(context)) return;

                if (!TryEnsureDaqRecoveryGroupDeenergized(context))
                {
                    await EscalateDaqAutoRecoveryAsync(
                            device,
                            "DaqOutputCutoffUnconfirmed",
                            "受影响DAQ组仍有通道处于带电位图，禁止丢弃旧批次或重建DAQ；保持断电重试。",
                            context.CorrelationId,
                            new DaqRecoveryResult
                            {
                                Device = device,
                                Recovered = false,
                                FailureKind = "DaqOutputCutoffUnconfirmed",
                                FailureReason = "DaqOutputCutoffUnconfirmed",
                                Classification = FaultClassification.SoftwareTransient
                            })
                        .ConfigureAwait(false);
                    return;
                }

                // The whole DAQ group is now de-energized. It is safe to abandon stale control
                // history and validate from the newest batch; while energized this operation is
                // forbidden because skipped batches could contain an over-current peak.
                var fastResyncDiscarded = 0;
                try
                {
                    fastResyncDiscarded = _acq.ResynchronizeInactiveControlToLatest(device);
                }
                catch (Exception ex)
                {
                    _log.Warn($"DAQ快速重同步未执行 Device={device}: {ex.Message}", "AI");
                }

                if (!context.Phase.CompleteCutoff())
                    throw new InvalidOperationException(
                        $"DAQ截止阶段无法提交 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                if (!restartDaq && !context.Phase.EnableValidation())
                    throw new InvalidOperationException(
                        $"DAQ截止后无法开放验证 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");

                _log.Warn(
                    $"DAQ软件自恢复开始 Device={device} Affected=[{string.Join(",", affected)}] " +
                    $"CorrelationId={context.CorrelationId:N} Code={context.TriggerCode} " +
                    $"RecoveryEpoch={context.RecoveryEpoch} " +
                    $"FastResyncDiscarded={fastResyncDiscarded} CutoffCompleted=true。",
                    "AI");
                PublishRecoveryProgress(context, "安全断电已完成，正在恢复数据链。");
                ObserveBackgroundTask(
                    ExportDaqIncidentSnapshotAsync(context, reason, "10-cutoff"),
                    "ExportDaqIncidentCutoffSnapshot");

                if (restartDaq)
                {
                    DaqRecoveryResult result = null;
                    try
                    {
                        await RecoveryStageDeadline.RunAsync(
                                "DaqTaskRecovery",
                                _daqPersistenceRecoveryTimeoutMs,
                                async ct =>
                                {
                                    result = await _acq.RecoverDeviceAsync(
                                    device,
                                    _daqPersistenceRecoveryTimeoutMs,
                                    _daqPersistenceRequiredFreshBatches,
                                    (int)_daqPersistenceResumeAgeMs,
                                    ct,
                                    forceRecreate: RequiresDaqTaskRecreate(context.TriggerCode))
                                        .ConfigureAwait(false);
                                },
                                context.Cancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
                    {
                        CompleteCancelledRecovery(context, "RecoveryCancelledByStop");
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        result = CreateDaqRecoveryTimeoutResult(device);
                    }
                    catch (RecoveryStageTimeoutException)
                    {
                        result = CreateDaqRecoveryTimeoutResult(device);
                    }
                    if (!IsCurrentRecovery(context))
                    {
                        CompleteCancelledRecovery(context, "RunEpochChangedAfterRebuild");
                        return;
                    }
                    context.PreviousGeneration = result.PreviousGeneration;
                    context.RecoveredGeneration = result.RecoveredGeneration;
                    context.FirstVerifiedSequence = result.FirstVerifiedSequence;
                    context.LastVerifiedSequence = result.LastVerifiedSequence;
                    context.AfterClock = _acq.GetDaqFreshnessSnapshot(device, _daqPersistenceResumeAgeMs);
                    ObserveBackgroundTask(
                        ExportDaqIncidentSnapshotAsync(context, result.FailureReason, "20-rebuild"),
                        "ExportDaqIncidentRebuildSnapshot");
                    if (!result.Recovered)
                    {
                        await EscalateDaqAutoRecoveryAsync(
                                device,
                                "DaqRecoveryFailed",
                                result.FailureReason,
                                context.CorrelationId,
                                result)
                            .ConfigureAwait(false);
                        return;
                    }
                    _persistence.AcceptGeneration(device, _acq.GetCurrentGeneration(device));
                    if (!context.Phase.EnableValidation())
                        throw new InvalidOperationException(
                            $"DAQ重建后无法开放验证 Device={device} " +
                            $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                    await TryCompleteDaqAutoRecoveryAsync(device).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                CompleteCancelledRecovery(context, "RecoveryCancelled");
            }
            catch (Exception ex)
            {
                await EscalateDaqAutoRecoveryAsync(
                        device,
                        "DaqRecoveryUnhandledException",
                        ex.Message,
                        context.CorrelationId)
                    .ConfigureAwait(false);
            }
        }

        private async Task TryCompleteDaqAutoRecoveryAsync(string device)
        {
            if (!_daqAutoRecovery.TryGetValue(device, out var context)) return;
            if (Interlocked.CompareExchange(ref context.Completing, 1, 0) != 0) return;
            try
            {
                context.ValidationPhase = "CutoffBarrier";
                await context.Phase.WaitForCutoffAsync(context.Cancellation.Token)
                    .ConfigureAwait(false);
                context.ValidationPhase = "ValidationReadyBarrier";
                await context.Phase.WaitForValidationReadyAsync(context.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!IsCurrentRecovery(context))
                {
                    CompleteCancelledRecovery(context, "RunEpochChangedBeforeValidation");
                    return;
                }
                if (!context.Phase.TryBeginValidation())
                    return;
                context.ValidationPhase = "PersistenceQueue";
                var queue = _persistence.GetSnapshot(device);
                if (queue.QueueDepth > _daqPersistenceResumeDepth ||
                    queue.OldestBatchAgeMs > _daqPersistenceResumeAgeMs ||
                    queue.Generation != _acq.GetCurrentGeneration(device))
                    return;
                context.ValidationPhase = "FreshDaqSamples";
                DaqRecoveryResult[] ready = null;
                await RecoveryStageDeadline.RunAsync(
                        "FreshDaqSamples",
                        _daqPersistenceRecoveryTimeoutMs,
                        async ct =>
                        {
                            ready = await _acq.EnsureChannelsReadyAsync(
                                    context.AffectedChannels,
                                    _daqPersistenceRecoveryTimeoutMs,
                                    string.Equals(
                                        context.TriggerCode,
                                        "DaqClockModelInvalid",
                                        StringComparison.OrdinalIgnoreCase)
                                        ? _daqClockRecoveryFreshBatches
                                        : _daqPersistenceRequiredFreshBatches,
                                    (int)_daqPersistenceResumeAgeMs,
                                    ct)
                                .ConfigureAwait(false);
                        },
                        context.Cancellation.Token)
                    .ConfigureAwait(false);
                if (ready.Any(x => !x.Recovered)) return;
                if (!IsCurrentRecovery(context)) return;
                context.AfterClock = _acq.GetDaqFreshnessSnapshot(device, _daqPersistenceResumeAgeMs);
                ObserveBackgroundTask(
                    ExportDaqIncidentSnapshotAsync(
                        context,
                        "DAQ与持久化新鲜度验证通过",
                        "30-validate"),
                    "ExportDaqIncidentValidationSnapshot");
                context.ValidationPhase = "TerminalOffCurrentVerification";
                var terminalOffTasks = context.AffectedChannels
                    .Select(channel => _runners.TryGetValue(channel, out var runner)
                        ? runner.GetTerminalOffCurrentVerificationTask()
                        : Task.CompletedTask)
                    .ToArray();
                await RecoveryStageDeadline.RunAsync(
                        "TerminalOffCurrentVerification",
                        RecoveryStageTimeoutMs,
                        _ => Task.WhenAll(terminalOffTasks),
                        context.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!IsCurrentRecovery(context)) return;

                var batchPauseState = CurrentBatchPauseState;
                var holdForBatchPause = ShouldHoldDaqRecoveredChannelsForBatchPause(batchPauseState);
                var powerRecoveryChannels = SelectDaqRecoveryPowerChannels(
                    context.PreviouslyActiveChannels ?? context.AffectedChannels,
                    IsAlarmStopRequested,
                    channel => _channelPausedUtc.ContainsKey(channel),
                    IsChannelEnabled);
                var rejoinChannels = (context.PreviouslyRunningChannels ?? Array.Empty<int>())
                    .Where(channel =>
                        _timers.ContainsKey(channel) &&
                        !IsAlarmStopRequested(channel) &&
                        !_channelPausedUtc.ContainsKey(channel))
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray();
                ElectricalStaggerPlan rejoinPlan = null;
                if (rejoinChannels.Length > 0)
                    rejoinPlan = GetCompatibleStaggerPlan(rejoinChannels);
                if (!holdForBatchPause && powerRecoveryChannels.Length > 0)
                {
                    context.ValidationPhase = "EmergencyPowerOffBarrier";
                    if (_powerSupply != null)
                    {
                        foreach (var groupId in powerRecoveryChannels
                                     .Select(GetElectricalGroupId)
                                     .Where(groupId => groupId > 0 &&
                                                       _emergencyPowerGroupLatch.ContainsKey(groupId))
                                     .Distinct()
                                     .OrderBy(groupId => groupId))
                        {
                            await RecoveryStageDeadline.RunAsync(
                                    "EmergencyPowerOffBarrier",
                                    RecoveryStageTimeoutMs,
                                    ct => _powerSupply.DisableGroupAsync(
                                        groupId,
                                        $"DaqRecoveryPreEnableBarrier:{device}",
                                        ct),
                                    context.Cancellation.Token)
                                .ConfigureAwait(false);
                        }
                    }
                    if (!IsCurrentRecovery(context)) return;

                    context.ValidationPhase = "PowerEnableThenMechanicalRelease";
                    await ExecuteDaqRecoveryRejoinPrerequisitesAsync(
                            powerRecoveryChannels,
                            _powerSupply == null
                                ? null
                                : (channels, ct) => _powerSupply.PrepareAndEnableAsync(channels, ct),
                            rejoinChannels.Length == 0
                                ? null
                                : (_, ct) => EnsureMotorReleasedBeforeFormalRejoinAsync(
                                    rejoinChannels,
                                    rejoinPlan,
                                    $"DaqRecovery:{device}",
                                    ct),
                            context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;
                    ResetTransientFaultStateForRestart(rejoinChannels, "DaqRecoveryRejoin");
                }
                var result = new DaqRecoveryResult
                {
                    Device = device,
                    Recovered = true,
                    PreviousGeneration = context.PreviousGeneration,
                    RecoveredGeneration = _acq.GetCurrentGeneration(device),
                    FirstVerifiedSequence = context.FirstVerifiedSequence,
                    LastVerifiedSequence = context.LastVerifiedSequence,
                    FreshCallbacks = _daqPersistenceRequiredFreshBatches,
                    RequiredFreshCallbacks = _daqPersistenceRequiredFreshBatches,
                    ElapsedMs = (int)Math.Max(0, (DateTime.UtcNow - context.StartedUtc).TotalMilliseconds),
                    Classification = FaultClassification.SoftwareTransient
                };

                // 共同重入和恢复终态提交共用同一把门。人工 Stop/运行代次取消也必须
                // 取得该门，因此不可能在“Timer 已重建、终态尚未提交”的缝隙中抢先取消。
                context.ValidationPhase = "RejoinAndCommit";
                try
                {
                    lock (_daqRecoveryCommitGate)
                    {
                        if (!IsCurrentRecovery(context)) return;
                        if (!context.Phase.TryBeginRejoin())
                            throw new InvalidOperationException(
                                $"DAQ恢复重入阶段顺序无效 Device={device} " +
                                $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");

                        if (!holdForBatchPause && rejoinChannels.Length > 0)
                            RejoinFormalChannelsAtSharedFutureSlot(
                                rejoinChannels,
                                rejoinPlan,
                                "DaqSoftwareRecovered",
                                "DAQ数据链与机械释放均已确认，按当前公共节律槽重新加入",
                                allowTerminalReset: false,
                                ownedByActiveDaqRecovery: true,
                                recoveryEpoch: context.RecoveryEpoch);

                        if (!context.Phase.CompleteRejoin())
                            throw new InvalidOperationException(
                                $"DAQ恢复重入完成阶段顺序无效 Device={device} " +
                                $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                        if (!context.Phase.TryCommit() ||
                            !context.Terminal.TryCommit(DaqRecoveryTerminal.Recovered))
                            return;

                        context.FailureBackoff.CommitSuccess();
                        MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                        _persistence.ResumeAdmission(device);
                        _daqAutoRecovery.TryRemove(device, out _);
                        context.Completion.TrySetResult(result);
                    }
                }
                catch
                {
                    context.Phase.FailRejoin();
                    throw;
                }
                ReleaseDaqRecoveryOwnerships(context);
                if (holdForBatchPause)
                {
                    foreach (var channel in rejoinChannels)
                        PublishChannelRuntimeState(
                            channel,
                            GetDaqRecoveredHeldRuntimeState(batchPauseState),
                            "DaqRecoveredHeldForBatchPause",
                            "DAQ软件数据链已恢复；批次仍处于暂停/恢复预检，保持定时器暂停",
                            affectedChannels: rejoinChannels,
                            correlationId: context.CorrelationId);
                }
                else
                {
                    foreach (var channel in rejoinChannels)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Running,
                            "DaqSoftwareRecoveredCommitted",
                            "DAQ恢复与共同节拍重入已原子提交",
                            affectedChannels: rejoinChannels,
                            correlationId: context.CorrelationId);
                        NonCriticalObserver.Invoke(
                            ChannelResumed,
                            channel,
                            ex => _log?.Warn(
                                $"DAQ恢复继续观察者异常，已隔离：{ex.Message}",
                                "AI"));
                    }
                }
                foreach (var channel in context.AffectedChannels
                             .Where(channel => _channelPausedUtc.ContainsKey(channel))
                             .Distinct())
                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.Paused,
                        "DaqRecoveredHeldForChannelPause",
                        "DAQ软件数据链已恢复；通道仍按人工操作保持暂停",
                        affectedChannels: new[] { channel },
                        correlationId: context.CorrelationId);
                foreach (var groupId in context.AffectedChannels
                             .Select(GetElectricalGroupId)
                             .Where(id => id > 0)
                             .Distinct())
                    _emergencyPowerGroupLatch.TryRemove(groupId);
                _log.Info(
                    holdForBatchPause
                        ? $"DAQ软件自动恢复完成 Device={device}；批次状态={batchPauseState}，" +
                          $"保持定时器暂停，等待批次继续流程统一恢复。" +
                          $"RecoveryEpoch={context.RecoveryEpoch} CorrelationId={context.CorrelationId:N}。"
                        : $"DAQ软件自动恢复完成 Device={device} " +
                          $"RecoveryEpoch={context.RecoveryEpoch} RejoinCommitted=true " +
                          $"CorrelationId={context.CorrelationId:N}。",
                    "AI");
                if (context.RaiseRecoverableAlarm != 0 &&
                    context.RecoverableAlarmChannel > 0 &&
                    Alarm != null)
                {
                    try
                    {
                        await RecoveryStageDeadline.RunAsync(
                                "DaqRecoveredAlarmClear",
                                RecoveryStageTimeoutMs,
                                ct => Alarm.SetAlarmAsync(
                                    context.RecoverableAlarmChannel,
                                    false,
                                    "DAQ自动恢复验证通过",
                                    ct),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        await RecoveryStageDeadline.RunAsync(
                                "DaqRecoveredIndicatorClear",
                                RecoveryStageTimeoutMs,
                                ct => Alarm.SetIndicatorOutputAsync(
                                    context.RecoverableAlarmChannel,
                                    false,
                                    ct),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"DAQ恢复后报警显示清除失败：{ex.Message}", "报警");
                    }
                }
                NonCriticalObserver.Invoke(
                    DaqRecoveryStateChanged,
                    result,
                    ex => _log?.Warn($"DAQ恢复状态观察者异常，已隔离：{ex.Message}", "AI"));
                context.Cancellation.Cancel();
                await ExportDaqIncidentSnapshotAsync(context, "自动恢复成功", "90-recovered")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var phase = string.IsNullOrWhiteSpace(context.ValidationPhase)
                    ? "Unknown"
                    : context.ValidationPhase;
                var signature = phase + ":" + ex.GetType().Name + ":" + ex.Message;
                var nowTicks = Stopwatch.GetTimestamp();
                var shouldLog = false;
                lock (context.ValidationFailureLogGate)
                {
                    if (!string.Equals(
                            context.LastValidationFailureSignature,
                            signature,
                            StringComparison.Ordinal) ||
                        context.LastValidationFailureLogTicks == 0 ||
                        nowTicks - context.LastValidationFailureLogTicks >= Stopwatch.Frequency * 30L)
                    {
                        context.LastValidationFailureSignature = signature;
                        context.LastValidationFailureLogTicks = nowTicks;
                        shouldLog = true;
                    }
                }
                if (shouldLog)
                    _log.Warn(
                        $"DAQ恢复检查失败（同原因30秒内去重） Device={device} " +
                        $"Phase={phase}: {ex.Message}",
                        "AI");
            }
            finally
            {
                Interlocked.Exchange(ref context.Completing, 0);
            }
        }

        private async Task EscalateDaqAutoRecoveryAsync(
            string device,
            string code,
            string reason,
            Guid correlationId,
            DaqRecoveryResult recoveryResult = null)
        {
            if (correlationId != Guid.Empty &&
                _daqRecoveryTerminalCorrelations.ContainsKey(correlationId))
                return;
            if (!_daqAutoRecovery.TryGetValue(device, out var context))
            {
                await BeginDaqAutoRecoveryAsync(
                        device,
                        code,
                        reason,
                        correlationId,
                        restartDaq: false,
                        eventUtc: DateTime.UtcNow)
                    .ConfigureAwait(false);
                if (!_daqAutoRecovery.TryGetValue(device, out context))
                    return;
            }
            if (context.Terminal.Current != DaqRecoveryTerminal.None) return;

            var evidence = await ConfirmDaqHardwareFailureAsync(context, recoveryResult)
                .ConfigureAwait(false);
            if (!IsCurrentRecovery(context))
            {
                CompleteCancelledRecovery(context, "RunEpochChangedDuringEscalation");
                return;
            }
            var disposition = DaqRecoveryFailurePolicy.Evaluate(
                evidence.Length,
                evidence.Length > 0 && evidence.All(x => x.Confirmed));
            if (disposition == DaqRecoveryFailureDisposition.ConfirmedHardwareAlarm &&
                !ShouldAutoRecoverExternalEquipmentFault(FaultScope.DaqGroup))
            {
                lock (_daqRecoveryCommitGate)
                {
                    if (!context.Terminal.TryCommit(DaqRecoveryTerminal.HardwareConfirmed)) return;
                    context.Phase.MarkTerminal();
                    MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                    _daqAutoRecovery.TryRemove(device, out _);
                }
                var primary = context.AffectedChannels.OrderBy(x => x).FirstOrDefault();
                var result = recoveryResult ?? new DaqRecoveryResult { Device = device };
                result.Classification = FaultClassification.HardwareConfirmed;
                result.HardwareEvidence = evidence;
                result.FailureKind = "DaqHardwareConfirmed";
                context.Completion.TrySetResult(result);
                ReleaseDaqRecoveryOwnerships(context);
                NonCriticalObserver.Invoke(
                    DaqRecoveryStateChanged,
                    result,
                    ex => _log?.Warn($"DAQ恢复状态观察者异常，已隔离：{ex.Message}", "AI"));
                try { context.Cancellation.Cancel(); } catch { }
                LatchDaqGroupHardFault(
                    primary,
                    device,
                    context.AffectedChannels,
                    "DaqHardwareConfirmed",
                    reason,
                    context.CorrelationId,
                    classification: FaultClassification.HardwareConfirmed);
                await ExportDaqIncidentSnapshotAsync(context, reason, "90-hardware-confirmed")
                    .ConfigureAwait(false);
                return;
            }

            // Independent DAQ groups are already safely de-energized.  Missing independent
            // hardware evidence must never be converted into a caliper alarm, but software
            // maintenance is still bounded: ScheduleDaqSelfMaintenance opens the common batch
            // recycle circuit after three failed generations instead of leaving the UI in
            // DaqSelfMaintenance forever.
            var maintenanceResult = recoveryResult ?? new DaqRecoveryResult
            {
                Device = device,
                Recovered = false,
                FailureReason = reason,
                FailureKind = code
            };
            maintenanceResult.Classification = FaultClassification.SoftwareTransient;
            maintenanceResult.HardwareEvidence = evidence;
            NonCriticalObserver.Invoke(
                DaqRecoveryStateChanged,
                maintenanceResult,
                ex => _log?.Warn($"DAQ维护状态观察者异常，已隔离：{ex.Message}", "AI"));
            ScheduleDaqSelfMaintenance(context, code, reason);
        }

        private void ScheduleDaqSelfMaintenance(
            DaqAutoRecoveryContext context,
            string code,
            string reason)
        {
            if (!IsCurrentRecovery(context)) return;
            if (Interlocked.CompareExchange(ref context.MaintenanceScheduled, 1, 0) != 0) return;

            var failureCount = context.FailureBackoff.RecordFailure();
            if (TryEscalateSoftwareRecoveryCircuitOpen(
                    "DaqSelfMaintenance",
                    $"Device={context.Device}; Code={code}; Error={reason}",
                    context.AffectedChannels,
                    context.RunId,
                    context.RunEpoch,
                    failureCount))
                return;
            var delayMs = GetDaqSelfMaintenanceDelayMs(failureCount);
            foreach (var channel in context.AffectedChannels)
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Recovering,
                    "DaqSelfMaintenance",
                    $"软件数据链自维护中，第{failureCount}次，{delayMs}ms后重试。",
                    affectedChannels: context.AffectedChannels,
                    correlationId: context.CorrelationId);
            _log.Warn(
                $"DAQ软件自维护等待重试 Device={context.Device} Failure={failureCount} " +
                $"DelayMs={delayMs} Code={code} CorrelationId={context.CorrelationId:N} " +
                $"Affected=[{string.Join(",", context.AffectedChannels)}] Reason={reason}；" +
                "健康DAQ组继续运行。",
                "AI");
            PublishRecoveryProgress(context, $"软件自维护等待 {delayMs}ms 后重试。");
            ObserveBackgroundTask(
                ExportDaqIncidentSnapshotAsync(context, reason, "40-self-maintenance"),
                "ExportDaqIncidentMaintenanceSnapshot");

            ObserveBackgroundTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, context.Cancellation.Token).ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;

                    // Re-check the hard safety boundary on every unattended retry. If an OFF
                    // command did not take effect, retry OFF but never skip data or rebuild the
                    // DAQ while any mapped channel is still considered energized.
                    if (!TryEnsureDaqRecoveryGroupDeenergized(context))
                    {
                        Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                        ScheduleDaqSelfMaintenance(
                            context,
                            "DaqOutputCutoffUnconfirmed",
                            "整组断电尚未确认；保持安全断电重试，不执行DAQ重同步。");
                        return;
                    }

                    DaqRecoveryResult result = null;
                    await RecoveryStageDeadline.RunAsync(
                            "DaqSelfMaintenanceTaskRecovery",
                            _daqPersistenceRecoveryTimeoutMs,
                            async ct =>
                            {
                                result = await _acq.RecoverDeviceAsync(
                                        context.Device,
                                        _daqPersistenceRecoveryTimeoutMs,
                                        _daqPersistenceRequiredFreshBatches,
                                        (int)_daqPersistenceResumeAgeMs,
                                        ct,
                                        forceRecreate: true)
                                    .ConfigureAwait(false);
                            },
                            context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;

                    context.RecoveryAttempt = Math.Max(context.RecoveryAttempt + 1, failureCount + 1);
                    context.PreviousGeneration = result.PreviousGeneration;
                    context.RecoveredGeneration = result.RecoveredGeneration;
                    context.FirstVerifiedSequence = result.FirstVerifiedSequence;
                    context.LastVerifiedSequence = result.LastVerifiedSequence;
                    context.AfterClock = _acq.GetDaqFreshnessSnapshot(
                        context.Device,
                        _daqPersistenceResumeAgeMs);
                    ObserveBackgroundTask(
                        ExportDaqIncidentSnapshotAsync(
                            context,
                            result.FailureReason,
                            "50-self-maintenance-rebuild"),
                        "ExportDaqIncidentMaintenanceRebuildSnapshot");

                    if (!result.Recovered)
                    {
                        Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                        await EscalateDaqAutoRecoveryAsync(
                                context.Device,
                                "DaqSelfMaintenanceRetryFailed",
                                result.FailureReason,
                                context.CorrelationId,
                                result)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (!await TryFinalizeDaqCutoffCyclesAfterPersistenceAsync(
                            context,
                            _daqPersistenceRecoveryTimeoutMs,
                            context.Cancellation.Token).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                        ScheduleDaqSelfMaintenance(
                            context,
                            "DaqPersistenceBoundaryPending",
                            "DAQ已重建，但截止圈原始批次尚未真实落盘；" +
                            "保持圈事务开放并继续重试。");
                        return;
                    }

                    // 初次截止若因输出位图未清零而转入自维护，必须等本次 DAQ
                    // 重建成功后再补交 CutoffCompleted。这样提前到达的 Recovered
                    // 观察者不会在重建仍进行时抢先验证和共同重入。
                    if (context.Phase.Current == DaqRecoveryPhase.CutoffStarted)
                    {
                        var discarded = 0;
                        try
                        {
                            discarded = _acq.ResynchronizeInactiveControlToLatest(context.Device);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn(
                                $"DAQ自维护快速重同步未执行 Device={context.Device}: {ex.Message}",
                                "AI");
                        }
                        if (!context.Phase.CompleteCutoff())
                            throw new InvalidOperationException(
                                $"DAQ自维护无法补交截止阶段 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                        _log.Warn(
                            $"DAQ自维护确认整组断电、重建成功并补交截止阶段 Device={context.Device} " +
                            $"RecoveryEpoch={context.RecoveryEpoch} " +
                            $"FastResyncDiscarded={discarded} CutoffCompleted=true。",
                            "AI");
                    }
                    await context.Phase.WaitForCutoffAsync(context.Cancellation.Token)
                        .ConfigureAwait(false);

                    _persistence.AcceptGeneration(
                        context.Device,
                        _acq.GetCurrentGeneration(context.Device));
                    if (!context.Phase.EnableValidation() &&
                        (int)context.Phase.Current < (int)DaqRecoveryPhase.ValidationReady)
                        throw new InvalidOperationException(
                            $"DAQ自维护重建后无法开放验证 Device={context.Device} " +
                            $"Phase={context.Phase.Current}");

                    // Persistence and PSU validation can lag the DAQ rebuild briefly. Keep
                    // checking without rebuilding again; successful validation commits the
                    // normal Recovered terminal and resumes at the next complete cycle.
                    for (var validation = 0; validation < 40 && IsCurrentRecovery(context); validation++)
                    {
                        await TryCompleteDaqAutoRecoveryAsync(context.Device).ConfigureAwait(false);
                        if (!IsCurrentRecovery(context)) return;
                        await Task.Delay(250, context.Cancellation.Token).ConfigureAwait(false);
                    }

                    if (!IsCurrentRecovery(context)) return;
                    Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                    ScheduleDaqSelfMaintenance(
                        context,
                        "DaqPostRecoveryValidationPending",
                        "DAQ已重建，但持久化或电源复核在10秒内尚未通过。");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                    if (IsCurrentRecovery(context))
                        await EscalateDaqAutoRecoveryAsync(
                                context.Device,
                                "DaqSelfMaintenanceUnhandledException",
                                ex.Message,
                                context.CorrelationId,
                                new DaqRecoveryResult
                                {
                                    Device = context.Device,
                                    Recovered = false,
                                    FailureReason = ex.Message,
                                    FailureKind = "DaqSelfMaintenanceUnhandledException",
                                    Classification = FaultClassification.SoftwareTransient
                                })
                            .ConfigureAwait(false);
                }
            }), "DaqSelfMaintenance");
        }

        private bool TryEnsureDaqRecoveryGroupDeenergized(DaqAutoRecoveryContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return false;
            foreach (var channel in context.AffectedChannels ?? Array.Empty<int>())
            {
                try { CommandEpbOffSafetyImmediate(channel); }
                catch { }
            }
            var deenergized = !IsDaqDeviceControlActive(context.Device);
            if (!deenergized)
                _log.Error(
                    $"DAQ恢复断电确认失败 Device={context.Device} " +
                    $"Affected=[{string.Join(",", context.AffectedChannels ?? Array.Empty<int>())}]；" +
                    "禁止丢弃批次和重建DAQ。",
                    "AI");
            return deenergized;
        }

        private void LatchDaqGroupHardFault(
            int triggeringChannel,
            string device,
            int[] affectedChannels,
            string code,
            string reason,
            Guid correlationId = default,
            DaqIncidentContext incidentContext = null,
            DaqDeviceFault deviceFault = null,
            bool safetyAlreadyApplied = false,
            FaultClassification classification = FaultClassification.HardwareConfirmed)
        {
            var alarmUtc = DateTime.UtcNow;
            var orderedChannels = (affectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (!orderedChannels.Contains(triggeringChannel))
                triggeringChannel = orderedChannels.FirstOrDefault();

            if (classification == FaultClassification.HardwareConfirmed)
                NotifyRunAuthorizationRevoking(
                    StopSource.AlarmInterlock,
                    $"DAQ硬件故障已确认 Device={device}; {reason}",
                    nameof(LatchDaqGroupHardFault),
                    correlationId,
                    FaultScope.DaqGroup);

            // 失效安全顺序：先在当前线程锁存、取消运行并逐通道高优先级断电；
            // 任何日志、UI、数据库封圈、蜂鸣或快照都必须发生在这之后。
            if (!safetyAlreadyApplied)
                ExecuteDaqFaultSafetyFirst(
                    orderedChannels,
                    affectedChannel =>
                    {
                        _alarmStopLatch.TryRequestStop(affectedChannel);
                        try { CancelStopCts(affectedChannel); } catch { }
                    },
                    affectedChannel =>
                    {
                        try { CommandEpbOffSafetyImmediate(affectedChannel); } catch { }
                    });

            var primaryChannel = triggeringChannel;
            var faultCorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                var fault = new ControlFault(
                    string.IsNullOrWhiteSpace(code) ? "DaqSampleStale" : code,
                    $"Device={device} {reason}",
                    FaultScope.DaqGroup,
                    orderedChannels,
                    null,
                    alarmUtc,
                    faultCorrelationId,
                    classification);

                _log.Error(
                    "DAQ设备级硬故障锁存（独立探测证据已确认）。" +
                    $"Code={fault.Code} " +
                    $"Device={device} TriggerEPB={primaryChannel} " +
                    $"Affected=[{string.Join(",", fault.AffectedChannels)}] " +
                    $"CorrelationId={fault.CorrelationId:N} Reason={reason}",
                    "AI");
                FlushPersistentLog();
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"DAQ硬故障观察者异常，已隔离：{ex.Message}", "AI"));
                PublishFaultRuntimeStates(fault, primaryChannel);

                foreach (var affectedChannel in orderedChannels)
                {
                    try { StopChannelOnAlarm(affectedChannel); } catch { }
                    if (affectedChannel != primaryChannel)
                        TryFinalizeCurrentCycleAfterSnapshot(affectedChannel, false);
                }

                if (primaryChannel > 0)
                {
                    NonCriticalObserver.Invoke(
                        ChannelAlarmRaised,
                        primaryChannel,
                        fault.Reason,
                        ex => _log?.Warn($"DAQ报警观察者异常，已隔离：{ex.Message}", "AI"));
                }

                if (incidentContext != null &&
                    _daqIncidentLatch.TryStartSnapshot(
                        incidentContext.RunId,
                        device,
                        out var snapshotContext))
                    ObserveBackgroundTask(
                        ExportDaqHardFaultIncidentSnapshotAsync(snapshotContext, deviceFault),
                        "ExportDaqHardFaultSnapshot");

                try
                {
                    if (Alarm != null && primaryChannel > 0)
                        await Alarm.SetAlarmAsync(primaryChannel, true, fault.Reason).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    var snapshot = primaryChannel > 0
                        ? await ExportAlarmSnapshotAsync(primaryChannel, fault.Reason, alarmUtc)
                            .ConfigureAwait(false)
                        : null;
                    if (snapshot == null && primaryChannel > 0)
                        TryFinalizeCurrentCycleAfterSnapshot(primaryChannel, false);
                    else if (primaryChannel > 0)
                        ClearCurrentCycleNumber(primaryChannel);
                }
                catch
                {
                    if (primaryChannel > 0)
                        TryFinalizeCurrentCycleAfterSnapshot(primaryChannel, false);
                }
            }), "DaqHardFaultHandling", primaryChannel);
        }

        internal static void ExecuteDaqFaultSafetyFirst(
            IReadOnlyList<int> affectedChannels,
            Action<int> latchAndCancel,
            Action<int> immediateOff)
        {
            if (affectedChannels == null) return;
            for (var i = 0; i < affectedChannels.Count; i++)
            {
                latchAndCancel?.Invoke(affectedChannels[i]);
                immediateOff?.Invoke(affectedChannels[i]);
            }
        }

        private void ExecuteDaqDeviceFaultSafetyFirst(string device, bool backgroundQueue)
        {
            var deviceMask = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? _dev1ChannelMask
                : _dev2ChannelMask;
            var activeCount = 0;
            for (var channel = 1; channel <= 12; channel++)
            {
                var bit = 1L << channel;
                if ((deviceMask & bit) == 0) continue;
                if (IsHydraulicParticipant(channel) ||
                    _timers.ContainsKey(channel) ||
                    _runners.ContainsKey(channel))
                    activeCount++;
            }

            for (var channel = 1; channel <= 12; channel++)
            {
                var bit = 1L << channel;
                if ((deviceMask & bit) == 0) continue;
                if (activeCount > 0 &&
                    !IsHydraulicParticipant(channel) &&
                    !_timers.ContainsKey(channel) &&
                    !_runners.ContainsKey(channel))
                    continue;

                if (backgroundQueue)
                {
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.Pause($"DaqDeviceFaultSafety:{device}");
                    CancelCyclePauseCts(channel);
                }
                else
                {
                    _alarmStopLatch.TryRequestStop(channel);
                    try { CancelStopCts(channel); } catch { }
                }
                try { CommandEpbOffSafetyImmediate(channel); } catch { }
            }
        }

        private int[] GetDaqGroupChannels(string device)
        {
            return Enumerable.Range(1, 12)
                .Where(ch => string.Equals(
                    _acq.GetDeviceForEpbChannel(ch),
                    device,
                    StringComparison.OrdinalIgnoreCase))
                .Where(ch => IsHydraulicParticipant(ch) || _timers.ContainsKey(ch) || _runners.ContainsKey(ch))
                .ToArray();
        }

        private int[] GetAllDaqDeviceChannels(string device)
        {
            return Enumerable.Range(1, 12)
                .Where(ch => string.Equals(
                    _acq.GetDeviceForEpbChannel(ch),
                    device,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private void BeginDaqIncidentRun(Guid runId, IEnumerable<int> channels)
        {
            var devices = (channels ?? Array.Empty<int>())
                .Select(_acq.GetDeviceForEpbChannel)
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _daqIncidentLatch.BeginRun(runId, devices);
            foreach (var device in devices)
                _acq.ResetControlSafetyLatch(device);
        }

        private DaqIncidentObservation ObserveDaqIncident(
            string device,
            string code,
            string reason,
            DateTime timestampUtc,
            int[] affectedChannels,
            long generation = 0,
            int primaryChannel = 0)
        {
            var runId = _activeBatchId;
            if (generation <= 0)
                generation = _acq.GetCurrentGeneration(device);
            return _daqIncidentLatch.Observe(
                runId,
                device,
                generation,
                code,
                reason,
                timestampUtc,
                affectedChannels,
                primaryChannel);
        }

        private long BuildDaqChannelMask(string device)
        {
            long mask = 0;
            for (var channel = 1; channel <= 12; channel++)
                if (string.Equals(
                        _acq.GetDeviceForEpbChannel(channel),
                        device,
                        StringComparison.OrdinalIgnoreCase))
                    mask |= 1L << channel;
            return mask;
        }

        private bool IsDaqDeviceControlActive(string device)
        {
            var deviceMask = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? _dev1ChannelMask
                : _dev2ChannelMask;
            return (Interlocked.Read(ref _energizedChannelsMask) & deviceMask) != 0;
        }

        private void SetChannelEnergized(int channel, bool energized)
        {
            if (channel < 1 || channel > 12) return;
            var bit = 1L << channel;
            while (true)
            {
                var current = Interlocked.Read(ref _energizedChannelsMask);
                var next = energized ? current | bit : current & ~bit;
                if (Interlocked.CompareExchange(ref _energizedChannelsMask, next, current) == current)
                    return;
            }
        }

        private async Task WaitForDaqRecoveryAsync(int channel, CancellationToken token)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device)) return;
            if (!_daqAutoRecovery.TryGetValue(device, out var context)) return;
            var completed = await Task.WhenAny(
                    context.Completion.Task,
                    Task.Delay(Timeout.Infinite, token))
                .ConfigureAwait(false);
            if (completed != context.Completion.Task)
            {
                token.ThrowIfCancellationRequested();
                return;
            }
            var result = await context.Completion.Task.ConfigureAwait(false);
            if (!result.Recovered)
                throw new InvalidOperationException(
                    $"DaqRecoveryFailed Device={device} Reason={result.FailureReason}");
        }

        private void OnPowerSupplyTelemetryUpdated(PowerSupplyTelemetry telemetry)
        {
            var recorder = _powerTelemetryRecorder;
            if (recorder != null && telemetry != null)
            {
                var group = _cfg.Test.Groups.FirstOrDefault(x => x.Id == telemetry.ElectricalGroupId);
                var cycle = group == null
                    ? 0
                    : group.Members
                        .Select(channel =>
                            _currentCycleNumberByChannel.TryGetValue(channel, out var value) ? value : 0)
                        .DefaultIfEmpty(0)
                        .Max();
                var stage = telemetry.Snapshot == null || !telemetry.Snapshot.OutputEnabled
                    ? "Preflight/Stopped"
                    : cycle > 0 ? "Running" : "Armed";
                recorder.Enqueue(telemetry, cycle, stage);
            }
            NonCriticalObserver.Invoke(
                PowerSupplyTelemetryUpdated,
                telemetry,
                ex => _log?.Warn($"程控电源遥测观察者异常，已隔离：{ex.Message}", "程控电源"));
        }

        private void OnHydraulicFaultRaised(ControlFault fault)
        {
            if (fault == null) return;
            if (IsBatchSessionActive && CurrentBatchPauseState != BatchPauseState.Running)
            {
                // 启动定位/学习/资格的调用栈正在 await 同一液压代次并执行有界重试。
                // 此处不得再并行创建第二套后台恢复，否则会形成 Recovery 代次风暴。
                _log.Warn(
                    $"启动阶段液压故障由当前批次调用栈收口，不创建并行恢复任务。" +
                    $"Code={fault.Code} Channels=[{string.Join(",", fault.AffectedChannels ?? Array.Empty<int>())}]",
                    "液压协调");
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"液压故障观察者异常，已隔离：{ex.Message}", "液压"));
                return;
            }
            var daqRecoveryChannels = (fault.AffectedChannels ?? Array.Empty<int>())
                .Where(channel =>
                {
                    var device = _acq.GetDeviceForEpbChannel(channel);
                    return !string.IsNullOrWhiteSpace(device) && _daqAutoRecovery.ContainsKey(device);
                })
                .Distinct()
                .ToArray();
            if (daqRecoveryChannels.Length > 0 &&
                daqRecoveryChannels.Length == (fault.AffectedChannels ?? Array.Empty<int>()).Distinct().Count())
            {
                _log.Warn(
                    $"CascadeCanceledByDaqFault Channels=[{string.Join(",", daqRecoveryChannels)}] " +
                    $"HydraulicCode={fault.Code} Reason={fault.Reason}",
                    "液压");
                return;
            }

            if (ShouldAutoRecoverExternalEquipmentFault(fault.Scope))
            {
                BeginHydraulicSoftwareRecovery(fault);
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"液压自愈观察者异常，已隔离：{ex.Message}", "液压"));
                return;
            }

            NotifyRunAuthorizationRevoking(
                StopSource.AlarmInterlock,
                fault.Reason,
                nameof(OnHydraulicFaultRaised),
                fault.CorrelationId,
                fault.Scope);
            // 故障回调首先执行不等待IO的电机断电；液压控制器已在发布事件前回零输出。
            foreach (var channel in fault.AffectedChannels.Distinct())
            {
                try { CommandEpbOff(channel, "HydraulicFailSafe:" + fault.Code); } catch { }
                try { CancelStopCts(channel); } catch { }
                UnmarkHydraulicParticipant(channel);
                ObserveSafetyTask(
                    AbortHydraulicLeaseForChannelAsync(channel, "HydraulicFailSafe:" + fault.Code),
                    "HydraulicFaultLeaseAbort",
                    channel);
            }
            FlushPersistentLog();
            PublishFaultRuntimeStates(
                fault,
                fault.AffectedChannels?.Distinct().OrderBy(x => x).Cast<int?>().FirstOrDefault());
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                fault,
                ex => _log?.Warn($"液压硬故障观察者异常，已隔离：{ex.Message}", "液压"));

            ObserveBackgroundTask(Task.Run(async () =>
            {
                foreach (var channel in fault.AffectedChannels.Distinct().OrderBy(x => x))
                {
                    if (!_alarmStopLatch.TryRequestStop(channel)) continue;
                    NonCriticalObserver.Invoke(
                        ChannelAlarmRaised,
                        channel,
                        fault.Reason,
                        ex => _log?.Warn($"液压报警观察者异常，已隔离：{ex.Message}", "液压"));
                    try { StopChannelOnAlarm(channel); } catch { }
                    try
                    {
                        if (Alarm != null)
                            await Alarm.SetAlarmAsync(channel, true, fault.Reason).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await ExportAlarmSnapshotAsync(channel, fault.Reason, fault.TimestampUtc)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
            }), "HydraulicHardFaultHandling");
        }

        private bool IsChannelEnergized(int channel)
        {
            if (channel < 1 || channel > 12) return false;
            return (Interlocked.Read(ref _energizedChannelsMask) & (1L << channel)) != 0;
        }

        private bool IsChannelEnabled(int channel)
        {
            if (channel < 1 || channel > 12 || _cfg?.Test == null) return false;
            lock (_cfg.Test)
            {
                _cfg.Test.EnsureEpbRecords(12);
                return _cfg.Test.GetEpbRecord(channel).Enabled;
            }
        }

        internal static int[] SelectDaqRecoveryPowerChannels(
            IEnumerable<int> previouslyActiveChannels,
            Func<int, bool> isAlarmStopped,
            Func<int, bool> isPaused,
            Func<int, bool> isEnabled)
        {
            return (previouslyActiveChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => !(isAlarmStopped?.Invoke(channel) ?? false))
                .Where(channel => !(isPaused?.Invoke(channel) ?? false))
                .Where(channel => isEnabled?.Invoke(channel) ?? true)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private async Task EnsurePowerSupplyReadyForChannelsAsync(
            IEnumerable<int> channels,
            CancellationToken token)
        {
            if (_powerSupply == null) return;
            var selected = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(IsChannelEnabled)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;
            var groups = selected.Select(GetElectricalGroupId).Where(id => id > 0).Distinct().ToArray();
            if (groups.Length > 0 && groups.All(group => _powerSupply.HasEnergizationPermit(group, out _)))
                return;
            await _powerSupply.PrepareAndEnableAsync(selected, token).ConfigureAwait(false);
            var denied = groups
                .Select(group => new { Group = group, Allowed = _powerSupply.HasEnergizationPermit(group, out var reason), Reason = reason })
                .FirstOrDefault(item => !item.Allowed);
            if (denied != null)
                throw new InvalidOperationException(
                    $"PowerSupplyEnergizationPermitMissing Group={denied.Group} Reason={denied.Reason}");
        }

        internal void EnsurePowerSupplyEnergizationPermit(int channel)
        {
            if (_powerSupply == null) return;
            var groupId = GetElectricalGroupId(channel);
            var reason = "GroupMappingMissing";
            if (groupId > 0 && _powerSupply.HasEnergizationPermit(groupId, out reason)) return;
            throw new PowerSupplyEnergizationPermitException(channel, groupId, reason);
        }

        /// <summary>
        /// 判断当前低电流是否发生在 DAQ/程控电源计划恢复窗口内。
        /// 在这个窗口内“近零电流”不能作为卡钳开路证据。
        /// </summary>
        internal bool IsInfrastructureTransitionForChannel(int channel, out string reason)
        {
            if (IsDaqRecoveryActiveForChannel(channel))
            {
                reason = "DaqRecoveryActive";
                return true;
            }

            var groupId = GetElectricalGroupId(channel);
            if (groupId > 0 && _powerSoftwareRecoveryGroups.ContainsKey(groupId))
            {
                reason = $"PowerRecoveryActive Group={groupId}";
                return true;
            }

            var permitReason = groupId <= 0 ? "GroupMappingMissing" : string.Empty;
            if (_powerSupply != null &&
                (groupId <= 0 || !_powerSupply.HasEnergizationPermit(groupId, out permitReason)))
            {
                reason = $"PowerPermitMissing Group={groupId} Reason={permitReason}";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private bool IsDaqRecoveryActiveForChannel(int channel)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            return !string.IsNullOrWhiteSpace(device) && _daqAutoRecovery.ContainsKey(device);
        }

        internal static bool ShouldDeferIndependentRejoinForDaq(bool daqRecoveryActive)
        {
            return daqRecoveryActive;
        }

        internal static async Task ExecuteDaqRecoveryRejoinPrerequisitesAsync(
            int[] channels,
            Func<int[], CancellationToken, Task> prepareAndEnablePower,
            Func<int[], CancellationToken, Task> confirmMechanicalRelease,
            CancellationToken token)
        {
            var selected = (channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0) return;

            token.ThrowIfCancellationRequested();
            if (prepareAndEnablePower != null)
                await RecoveryStageDeadline.RunAsync(
                        "PowerEnable",
                        RecoveryStageTimeoutMs,
                        ct => prepareAndEnablePower(selected, ct),
                        token)
                    .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            if (confirmMechanicalRelease != null)
                await RecoveryStageDeadline.RunAsync(
                        "MechanicalRelease",
                        RecoveryMechanicalReleaseTimeoutMs,
                        ct => confirmMechanicalRelease(selected, ct),
                        token)
                    .ConfigureAwait(false);
        }

        private void BeginHydraulicSoftwareRecovery(ControlFault fault)
        {
            var primaryChannel = fault.AffectedChannels?.FirstOrDefault() ?? 0;
            var hydraulicId = fault.GroupId ??
                              (primaryChannel >= 1 && primaryChannel <= 6 ? 1 : 2);
            if (hydraulicId <= 0) return;
            var channels = (fault.AffectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (!_hydraulicSoftwareRecoveryGroups.TryAdd(hydraulicId, 0))
            {
                _log.Warn(
                    $"液压组{hydraulicId}已有软件自愈任务，本次故障并入现有恢复。Code={fault.Code}",
                    "液压协调");
                return;
            }

            var recoveryRunId = _activeBatchId;
            var recoveryRunEpoch = Interlocked.Read(ref _runEpoch);

            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                var hardDeadlineReached = false;
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
                CancellationTokenSource hardDeadline = null;
                CancellationTokenSource recoveryLinked = null;
                try
                {
                    hardDeadline = new CancellationTokenSource(RecoveryGroupHardDeadlineMs);
                    ownership = await _recoveryOwnership.AcquireAsync(
                            hydraulicId,
                            $"HYDRAULIC:{hydraulicId}:{fault.CorrelationId:N}",
                            RecoveryOwnerPriority.Hydraulic,
                            RecoveryOwnershipTakeoverTimeoutMs,
                            hardDeadline.Token)
                        .ConfigureAwait(false);
                    recoveryLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        ownership.Token,
                        hardDeadline.Token);
                    var recoveryToken = recoveryLinked.Token;
                    if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                        return;
                    foreach (var channel in channels)
                    {
                        if (_timers.TryGetValue(channel, out var timer))
                            timer.Pause($"HydraulicSelfHealing:{fault.Code}");
                        CancelCyclePauseCts(channel);
                    }
                    var offFailed = channels
                        .Where(channel => !TryEnsureSoftwareRecoveryOutputOff(
                            channel,
                            "HydraulicSelfHealing"))
                        .ToArray();
                    if (offFailed.Length > 0)
                        _log.Warn(
                            $"液压组{hydraulicId}首次断电确认失败，保持Recovering并持续重试。" +
                            $"Channels=[{string.Join(",", offFailed)}]",
                            "液压协调");
                    var cutoffUtc = DateTime.UtcNow;
                    var cutoffCycles = CaptureSoftwareRecoveryCycles(channels);
                    TrySealSoftwareRecoveryCycleWindows(
                        cutoffCycles,
                        cutoffUtc,
                        $"Hydraulic:{fault.Code}");
                    foreach (var channel in channels)
                    {
                        UnmarkHydraulicParticipant(channel);
                        try
                        {
                            await AbortHydraulicLeaseForChannelAsync(
                                    channel,
                                    "HydraulicSelfHealing:" + fault.Code)
                                .ConfigureAwait(false);
                        }
                        catch (Exception leaseEx)
                        {
                            _log.Warn(
                                $"液压自愈归还EPB[{channel}]租约失败：{leaseEx.Message}",
                                "液压协调");
                        }
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "HydraulicSelfHealing",
                            "液压同步软件自愈中；当前圈时间窗已封闭，等待Raw与耐久边界后作废。",
                            affectedChannels: channels,
                            correlationId: fault.CorrelationId);
                    }
                    _log.Warn(
                        $"液压组{hydraulicId}软件同步故障进入自愈，不发布全局SystemFault。" +
                        $"Code={fault.Code} Channels=[{string.Join(",", channels)}] Reason={fault.Reason}",
                        "液压协调");
                    while (true)
                    {
                        if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                        {
                            _log.Info(
                                $"液压组{hydraulicId}软件自愈因运行已停止或已重新开始而作废。",
                                "液压协调");
                            return;
                        }
                        attempt++;
                        try
                        {
                            var retryOffFailed = channels
                                .Where(channel => !TryEnsureSoftwareRecoveryOutputOff(
                                    channel,
                                    "HydraulicSelfHealingRetry"))
                                .ToArray();
                            if (retryOffFailed.Length > 0)
                                throw new SoftwareSelfHealingRetryException(
                                    $"电机断电仍未确认：EPB[{string.Join(",", retryOffFailed)}]");

                            await RecoveryStageDeadline.RunAsync(
                                    "HydraulicForceRelease",
                                    RecoveryMechanicalReleaseTimeoutMs,
                                    _ => _hydCoordinator.ForceReleaseAsync(
                                        hydraulicId,
                                        $"SoftwareSelfHealing:{fault.Code}"),
                                    recoveryToken)
                                .ConfigureAwait(false);

                            if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                                    cutoffCycles,
                                    cutoffUtc,
                                    $"Hydraulic:{fault.Code}",
                                    _daqPersistenceRecoveryTimeoutMs,
                                    recoveryToken)
                                .ConfigureAwait(false))
                                throw new SoftwareSelfHealingRetryException(
                                    "液压恢复圈截止 Raw/耐久边界尚未闭合。");

                            if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                                return;

                            var batchPauseState = CurrentBatchPauseState;
                            if (ShouldHoldDaqRecoveredChannelsForBatchPause(batchPauseState))
                            {
                                foreach (var channel in channels.Where(ch => _timers.ContainsKey(ch)))
                                    PublishChannelRuntimeState(
                                        channel,
                                        GetDaqRecoveredHeldRuntimeState(batchPauseState),
                                        "HydraulicRecoveredHeldForBatchPause",
                                        "液压同步已恢复；批次仍处于暂停/恢复预检，保持定时器暂停",
                                        affectedChannels: channels,
                                        correlationId: fault.CorrelationId);
                                return;
                            }

                            var rejoinChannels = channels
                                .Where(channel =>
                                    _timers.ContainsKey(channel) &&
                                    !IsAlarmStopRequested(channel) &&
                                    !_channelPausedUtc.ContainsKey(channel))
                                .ToArray();
                            if (rejoinChannels.Length > 0)
                            {
                                var plan = GetCompatibleStaggerPlan(rejoinChannels);
                                await RecoveryStageDeadline.RunAsync(
                                        "HydraulicMechanicalRelease",
                                        RecoveryMechanicalReleaseTimeoutMs,
                                        ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                                            rejoinChannels,
                                            plan,
                                            $"HydraulicRecovery:{fault.Code}",
                                            ct),
                                        recoveryToken)
                                    .ConfigureAwait(false);
                                if (!IsSoftwareRecoveryRunCurrent(
                                        recoveryRunId,
                                        recoveryRunEpoch,
                                        channels))
                                    return;
                                ResetTransientFaultStateForRestart(
                                    rejoinChannels,
                                    "HydraulicRecoveryRejoin");
                                RejoinFormalChannelsAtSharedFutureSlot(
                                    rejoinChannels,
                                    plan,
                                    "HydraulicSelfHealed",
                                    "液压同步与机械释放均已恢复，按当前公共节律槽重新加入",
                                    allowTerminalReset: false);
                            }
                            foreach (var channel in channels
                                         .Where(ch => _channelPausedUtc.ContainsKey(ch)))
                                PublishChannelRuntimeState(
                                    channel,
                                    ChannelRuntimeState.Paused,
                                    "HydraulicRecoveredHeldForChannelPause",
                                    "液压同步已恢复；通道仍按人工操作保持暂停",
                                    affectedChannels: new[] { channel },
                                    correlationId: fault.CorrelationId);
                            _log.Info(
                                $"液压组{hydraulicId}软件自愈完成 Attempt={attempt}；" +
                                "作废圈不计数，机械释放后按公共正式槽继续。",
                                "液压协调");
                            return;
                        }
                        catch (Exception ex)
                        {
                            var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                            _log.Warn(
                                $"液压组{hydraulicId}软件自愈第{attempt}次失败，" +
                                $"{delayMs}ms后重试：{ex.Message}",
                                "液压协调");
                            await Task.Delay(delayMs, recoveryToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (
                    hardDeadline?.IsCancellationRequested == true &&
                    IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                {
                    hardDeadlineReached = true;
                    _log.Error(
                        $"液压组{hydraulicId}软件恢复超过{RecoveryGroupHardDeadlineMs}ms，" +
                        "转入受影响组Stop→Start等价清场。",
                        "液压协调");
                }
                catch (OperationCanceledException)
                {
                    _log.Info(
                        $"液压组{hydraulicId}恢复所有权已移交给更高层恢复。",
                        "液压协调");
                }
                catch (RecoveryOwnershipTimeoutException ex)
                {
                    _log.Info(
                        $"液压组{hydraulicId}恢复已由同组更高层所有者接管：{ex.Message}",
                        "液压协调");
                }
                catch (Exception ex)
                {
                    hardDeadlineReached = IsSoftwareRecoveryRunCurrent(
                        recoveryRunId,
                        recoveryRunEpoch,
                        channels);
                    _log.Error(
                        $"液压组{hydraulicId}恢复协调异常，转入受影响组清场：{ex.Message}",
                        "液压协调",
                        ex);
                }
                finally
                {
                    recoveryLinked?.Dispose();
                    hardDeadline?.Dispose();
                    ownership?.Dispose();
                    _hydraulicSoftwareRecoveryGroups.TryRemove(hydraulicId, out _);
                }
                if (hardDeadlineReached)
                    await ExecuteAffectedGroupResetAsync(
                            channels,
                            $"HydraulicRecoveryHardDeadline:{fault.Code}",
                            fault.CorrelationId,
                            recoveryRunEpoch)
                        .ConfigureAwait(false);
            }), "HydraulicSoftwareRecovery");
        }

        private void BeginPowerSupplyTelemetryRecording(Guid runId)
        {
            if (_powerSupply == null) return;
            var directory = System.IO.Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                "PowerSupplyTelemetry");
            var path = System.IO.Path.Combine(
                directory,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{runId:N}.csv");
            var next = new PowerSupplyTelemetryCsvRecorder(path, _log);
            var previous = Interlocked.Exchange(ref _powerTelemetryRecorder, next);
            previous?.Dispose();
            _log.Info($"程控电源连续遥测文件：{path}", "程控电源");
        }

        private void EndPowerSupplyTelemetryRecording()
        {
            var recorder = Interlocked.Exchange(ref _powerTelemetryRecorder, null);
            recorder?.Dispose();
        }

        private void OnPowerSupplyFaultRaised(PowerSupplyFault fault)
        {
            if (fault == null) return;
            if (fault.Classification == FaultClassification.HardwareConfirmed &&
                !ShouldAutoRecoverExternalEquipmentFault(FaultScope.ElectricalGroup))
                NotifyRunAuthorizationRevoking(
                    StopSource.AlarmInterlock,
                    fault.Reason,
                    nameof(OnPowerSupplyFaultRaised),
                    Guid.NewGuid(),
                    FaultScope.ElectricalGroup);
            var controlFault = new ControlFault(
                "PowerSupply" + fault.Code,
                fault.Reason,
                FaultScope.ElectricalGroup,
                fault.AffectedChannels?.Distinct().ToArray() ?? Array.Empty<int>(),
                fault.ElectricalGroupId,
                fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc,
                Guid.NewGuid(),
                fault.Classification);
            if (ShouldAutoRecoverExternalEquipmentFault(controlFault.Scope))
            {
                BeginPowerSupplySoftwareRecovery(fault, controlFault);
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    controlFault,
                    ex => _log?.Warn($"程控电源自愈观察者异常，已隔离：{ex.Message}", "程控电源"));
                return;
            }

            foreach (var channel in fault.AffectedChannels.Distinct())
            {
                try { CancelStopCts(channel); } catch { }
                try { CommandEpbOffSafetyImmediate(channel); } catch { }
            }

            PublishFaultRuntimeStates(
                controlFault,
                controlFault.AffectedChannels.Distinct().OrderBy(x => x).Cast<int?>().FirstOrDefault());
            NonCriticalObserver.Invoke(
                ControlFaultRaised,
                controlFault,
                ex => _log?.Warn($"程控电源硬故障观察者异常，已隔离：{ex.Message}", "程控电源"));
            NonCriticalObserver.Invoke(
                PowerSupplyFaultRaised,
                fault,
                ex => _log?.Warn($"程控电源故障观察者异常，已隔离：{ex.Message}", "程控电源"));

            ObserveBackgroundTask(Task.Run(async () =>
            {
                var reason = $"PowerSupply[{fault.Code}] {fault.Reason}";
                foreach (var channel in fault.AffectedChannels.Distinct().OrderBy(x => x))
                {
                    if (!_alarmStopLatch.TryRequestStop(channel)) continue;
                    NonCriticalObserver.Invoke(
                        ChannelAlarmRaised,
                        channel,
                        reason,
                        ex => _log?.Warn($"程控电源报警观察者异常，已隔离：{ex.Message}", "程控电源"));
                    try { StopChannelOnAlarm(channel); } catch { }
                    try
                    {
                        if (Alarm != null)
                            await Alarm.SetAlarmAsync(channel, true, reason).ConfigureAwait(false);
                    }
                    catch { }

                    var alarmUtc = fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc;
                    AlarmCycleSnapshotEvidence snapshotEvidence = null;
                    try
                    {
                        snapshotEvidence = await ExportAlarmSnapshotAsync(channel, reason, alarmUtc)
                            .ConfigureAwait(false);
                    }
                    catch { }
                    if (snapshotEvidence == null)
                        TryFinalizeCurrentCycleAfterSnapshot(channel, false);
                    else
                        ClearCurrentCycleNumber(channel);
                }

                // 先完成同组所有 EPB 高优先级断电，再关闭共享电源输出。
                try
                {
                    await _powerSupply.DisableGroupAsync(
                        fault.ElectricalGroupId,
                        reason,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    if (_powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }
            }), "PowerSupplyHardFaultHandling");
        }

        private void BeginPowerSupplySoftwareRecovery(
            PowerSupplyFault sourceFault,
            ControlFault controlFault)
        {
            var groupId = sourceFault.ElectricalGroupId;
            var channels = (controlFault.AffectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (!_powerSoftwareRecoveryGroups.TryAdd(groupId, 0))
            {
                _log.Warn(
                    $"电源组{groupId}已有软件自愈任务，本次故障并入现有恢复。Code={sourceFault.Code}",
                    "程控电源");
                return;
            }

            var recoveryRunId = _activeBatchId;
            var recoveryRunEpoch = Interlocked.Read(ref _runEpoch);

            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                var hardDeadlineReached = false;
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[] ownerships = null;
                CancellationTokenSource hardDeadline = null;
                CancellationTokenSource recoveryLinked = null;
                try
                {
                    hardDeadline = new CancellationTokenSource(RecoveryGroupHardDeadlineMs);
                    ownerships = await AcquireRecoveryOwnershipsAsync(
                            $"POWER:{groupId}:{controlFault.CorrelationId:N}",
                            RecoveryOwnerPriority.PowerSupply,
                            channels,
                            hardDeadline.Token)
                        .ConfigureAwait(false);
                    recoveryLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        ownerships.Select(ownership => ownership.Token)
                            .Concat(new[] { hardDeadline.Token })
                            .ToArray());
                    var recoveryToken = recoveryLinked.Token;
                    if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                        return;
                    foreach (var channel in channels)
                    {
                        if (_timers.TryGetValue(channel, out var timer))
                            timer.Pause($"PowerSupplySelfHealing:{sourceFault.Code}");
                        CancelCyclePauseCts(channel);
                    }
                    var offFailed = channels
                        .Where(channel => !TryEnsureSoftwareRecoveryOutputOff(
                            channel,
                            "PowerSupplySelfHealing"))
                        .ToArray();
                    if (offFailed.Length > 0)
                        _log.Warn(
                            $"电源组{groupId}首次断电确认失败，保持Recovering并持续重试。" +
                            $"Channels=[{string.Join(",", offFailed)}]",
                            "程控电源");
                    var cutoffUtc = DateTime.UtcNow;
                    var cutoffCycles = CaptureSoftwareRecoveryCycles(channels);
                    TrySealSoftwareRecoveryCycleWindows(
                        cutoffCycles,
                        cutoffUtc,
                        $"PowerSupply:{sourceFault.Code}");
                    foreach (var channel in channels)
                    {
                        UnmarkHydraulicParticipant(channel);
                        try
                        {
                            ObserveSafetyTask(
                                HydraulicMarkReleaseAsync(channel),
                                "PowerSoftwareRecovery",
                                channel);
                        }
                        catch { }
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "PowerSupplySelfHealing",
                            "程控电源通信/状态软件自愈中；当前圈时间窗已封闭，等待Raw与耐久边界后作废。",
                            affectedChannels: channels,
                            correlationId: controlFault.CorrelationId);
                    }
                    _log.Warn(
                        $"电源组{groupId}系统类故障进入组内自愈，不发布全局SystemFault。" +
                        $"Code={sourceFault.Code} Channels=[{string.Join(",", channels)}] Reason={sourceFault.Reason}",
                        "程控电源");
                    while (true)
                    {
                        if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                        {
                            _log.Info(
                                $"电源组{groupId}软件自愈因运行已停止或已重新开始而作废。",
                                "程控电源");
                            return;
                        }
                        attempt++;
                        try
                        {
                            var retryOffFailed = channels
                                .Where(channel => !TryEnsureSoftwareRecoveryOutputOff(
                                    channel,
                                    "PowerSupplySelfHealingRetry"))
                                .ToArray();
                            if (retryOffFailed.Length > 0)
                                throw new SoftwareSelfHealingRetryException(
                                    $"电机断电仍未确认：EPB[{string.Join(",", retryOffFailed)}]");

                            try
                            {
                                await RecoveryStageDeadline.RunAsync(
                                        "PowerDisable",
                                        RecoveryStageTimeoutMs,
                                        ct => _powerSupply.DisableGroupAsync(
                                            groupId,
                                            $"SoftwareSelfHealing:{sourceFault.Code}",
                                            ct),
                                        recoveryToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.Warn(
                                    $"电源组{groupId}自愈关闭输出未确认，将继续执行实时重连预检：{ex.Message}",
                                    "程控电源");
                            }

                            await RecoveryStageDeadline.RunAsync(
                                    "PowerPrepareAndEnable",
                                    RecoveryStageTimeoutMs,
                                    ct => _powerSupply.PrepareAndEnableAsync(channels, ct),
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                            {
                                try
                                {
                                    await RecoveryStageDeadline.RunAsync(
                                            "PowerDiscardDisable",
                                            RecoveryStageTimeoutMs,
                                            ct => _powerSupply.DisableGroupAsync(
                                                groupId,
                                                "DiscardStaleSoftwareRecovery",
                                                ct),
                                            recoveryToken)
                                        .ConfigureAwait(false);
                                }
                                catch { }
                                return;
                            }
                            if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                                    cutoffCycles,
                                    cutoffUtc,
                                    $"PowerSupply:{sourceFault.Code}",
                                    _daqPersistenceRecoveryTimeoutMs,
                                    recoveryToken)
                                .ConfigureAwait(false))
                                throw new SoftwareSelfHealingRetryException(
                                    "程控电源恢复圈截止 Raw/耐久边界尚未闭合。");
                            var startupPositioningOwner =
                                sourceFault.Reason?.IndexOf(
                                    "StartupPositioning",
                                    StringComparison.OrdinalIgnoreCase) >= 0;
                            if (startupPositioningOwner)
                            {
                                foreach (var channel in channels.Where(IsChannelEnabled))
                                    PublishChannelRuntimeState(
                                        channel,
                                        ChannelRuntimeState.Recovering,
                                        "PowerSupplyRecoveredForStartup",
                                        "电源控制链已恢复；启动定位原流程继续重试，禁止提前进入正式节律。",
                                        affectedChannels: channels,
                                        correlationId: controlFault.CorrelationId);
                                _emergencyPowerGroupLatch.TryRemove(groupId);
                                return;
                            }
                            var batchPauseState = CurrentBatchPauseState;
                            if (ShouldHoldDaqRecoveredChannelsForBatchPause(batchPauseState))
                            {
                                foreach (var channel in channels.Where(ch => _timers.ContainsKey(ch)))
                                    PublishChannelRuntimeState(
                                        channel,
                                        GetDaqRecoveredHeldRuntimeState(batchPauseState),
                                        "PowerSupplyRecoveredHeldForBatchPause",
                                        "程控电源已恢复；批次仍处于暂停/恢复预检，保持定时器暂停",
                                        affectedChannels: channels,
                                        correlationId: controlFault.CorrelationId);
                                return;
                            }

                            var rejoinChannels = channels
                                .Where(channel =>
                                    _timers.ContainsKey(channel) &&
                                    !IsAlarmStopRequested(channel) &&
                                    !_channelPausedUtc.ContainsKey(channel))
                                .ToArray();
                            if (rejoinChannels.Length > 0)
                            {
                                var plan = GetCompatibleStaggerPlan(rejoinChannels);
                                await RecoveryStageDeadline.RunAsync(
                                        "PowerMechanicalRelease",
                                        RecoveryMechanicalReleaseTimeoutMs,
                                        ct => EnsureMotorReleasedBeforeFormalRejoinAsync(
                                            rejoinChannels,
                                            plan,
                                            $"PowerSupplyRecovery:{sourceFault.Code}",
                                            ct),
                                        recoveryToken)
                                    .ConfigureAwait(false);
                                if (!IsSoftwareRecoveryRunCurrent(
                                        recoveryRunId,
                                        recoveryRunEpoch,
                                        channels))
                                    return;
                                ResetTransientFaultStateForRestart(
                                    rejoinChannels,
                                    "PowerSupplyRecoveryRejoin");
                                RejoinFormalChannelsAtSharedFutureSlot(
                                    rejoinChannels,
                                    plan,
                                    "PowerSupplySelfHealed",
                                    "程控电源与机械释放均已恢复，按当前公共节律槽重新加入",
                                    allowTerminalReset: false);
                            }
                            foreach (var channel in channels
                                         .Where(ch => _channelPausedUtc.ContainsKey(ch)))
                                PublishChannelRuntimeState(
                                    channel,
                                    ChannelRuntimeState.Paused,
                                    "PowerSupplyRecoveredHeldForChannelPause",
                                    "程控电源已恢复；通道仍按人工操作保持暂停",
                                    affectedChannels: new[] { channel },
                                    correlationId: controlFault.CorrelationId);
                            _log.Info(
                                $"电源组{groupId}软件自愈完成 Attempt={attempt}；" +
                                "作废圈不计数，机械释放后按公共正式槽继续。",
                                "程控电源");
                            _emergencyPowerGroupLatch.TryRemove(groupId);
                            return;
                        }
                        catch (Exception ex)
                        {
                            var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                            _log.Warn(
                                $"电源组{groupId}软件自愈第{attempt}次失败，" +
                                $"{delayMs}ms后重试：{ex.Message}",
                                "程控电源");
                            await Task.Delay(delayMs, recoveryToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (
                    hardDeadline?.IsCancellationRequested == true &&
                    IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                {
                    hardDeadlineReached = true;
                    _log.Error(
                        $"电源组{groupId}软件恢复超过{RecoveryGroupHardDeadlineMs}ms，" +
                        "转入受影响组Stop→Start等价清场。",
                        "程控电源");
                }
                catch (OperationCanceledException)
                {
                    _log.Info(
                        $"电源组{groupId}恢复所有权已移交给更高层恢复。",
                        "程控电源");
                }
                catch (RecoveryOwnershipTimeoutException ex)
                {
                    _log.Info(
                        $"电源组{groupId}恢复已由同液压组更高层所有者接管：{ex.Message}",
                        "程控电源");
                }
                catch (Exception ex)
                {
                    hardDeadlineReached = IsSoftwareRecoveryRunCurrent(
                        recoveryRunId,
                        recoveryRunEpoch,
                        channels);
                    _log.Error(
                        $"电源组{groupId}恢复协调异常，转入受影响组清场：{ex.Message}",
                        "程控电源",
                        ex);
                }
                finally
                {
                    recoveryLinked?.Dispose();
                    hardDeadline?.Dispose();
                    foreach (var ownership in ownerships ??
                                 Array.Empty<HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease>())
                        ownership.Dispose();
                    _powerSoftwareRecoveryGroups.TryRemove(groupId, out _);
                }
                if (hardDeadlineReached)
                    await ExecuteAffectedGroupResetAsync(
                            channels,
                            $"PowerRecoveryHardDeadline:{sourceFault.Code}",
                            controlFault.CorrelationId,
                            recoveryRunEpoch)
                        .ConfigureAwait(false);
            }), "PowerSupplySoftwareRecovery");
        }

        private bool IsSoftwareRecoveryRunCurrent(
            Guid expectedRunId,
            long expectedRunEpoch,
            IEnumerable<int> channels)
        {
            if (expectedRunEpoch != Interlocked.Read(ref _runEpoch) ||
                expectedRunId != _activeBatchId)
                return false;

            var affected = (channels ?? Array.Empty<int>()).Distinct().ToArray();
            if (affected.Any(channel =>
                    _timers.ContainsKey(channel) ||
                    _runners.ContainsKey(channel) ||
                    _stopCtsByChannel.ContainsKey(channel)))
                return true;

            return expectedRunId != Guid.Empty && IsBatchSessionActive;
        }

        private void OnRunnerWarningRaised(int channel, string reason)
        {
            _log.Warn($"EPB[{channel}] 自适应软预警：{reason}", "EPB");
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.WarningRunning,
                ExtractFaultCode(reason),
                reason);
            NonCriticalObserver.Invoke(
                ChannelWarningRaised,
                channel,
                reason,
                ex => _log?.Warn($"软预警观察者异常，已隔离：{ex.Message}", "EPB"));
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


        private async Task<AlarmCycleSnapshotEvidence> ExportAlarmSnapshotAsync(
            int alarmChannel,
            string reason,
            DateTime alarmUtc)
        {
            var recorder = Recorder;
            if (recorder == null) return null;
            var hasActiveAlarmCycle = _currentCycleNumberByChannel.TryGetValue(
                alarmChannel,
                out var alarmCycleNumber);
            if (!hasActiveAlarmCycle &&
                !_frozenFaultCycleByChannel.TryGetValue(alarmChannel, out alarmCycleNumber))
            {
                _log.Error(
                    $"EPB[{alarmChannel}] 报警快照缺少活动圈及冻结圈号。Reason={reason}",
                    "落盘");
                return null;
            }

            // 快照去抖：同一通道在 cooldown 内只导出一次
            var cooldownMs = AlarmConfig?.Behavior?.SnapshotCooldownMs ?? 2000;
            var now = DateTime.UtcNow;
            lock (_lastAlarmSnapshotUtcByChannel)
            {
                if (_lastAlarmSnapshotUtcByChannel.TryGetValue(alarmChannel, out var last))
                {
                    if ((now - last).TotalMilliseconds < cooldownMs)
                        return null;
                }

                _lastAlarmSnapshotUtcByChannel[alarmChannel] = now;
            }

            var postOffTailMs = AlarmConfig?.Behavior?.SnapshotPostOffTailMs ?? 1000;
            postOffTailMs = Math.Max(0, Math.Min(3000, postOffTailMs));
            if (postOffTailMs > 0)
                await Task.Delay(postOffTailMs).ConfigureAwait(false);

            await _alarmSnapshotGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lastN = AlarmConfig?.WarningSnapshots?.HardAlarmLastNCycles
                            ?? AlarmConfig?.Behavior?.SnapshotLastNCycles
                            ?? 10;
                lastN = Math.Max(1, lastN);
                var includeSameGroup = AlarmConfig?.WarningSnapshots
                    ?.HardAlarmIncludeSameElectricalGroup == true;
                var sameGroupLastN = Math.Max(
                    1,
                    AlarmConfig?.WarningSnapshots?.HardAlarmSameGroupLastNCycles ?? 10);

                // 根目录：StoreDir\TestName\AlarmSnapshots
                var baseDir = System.IO.Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "AlarmSnapshots");
                System.IO.Directory.CreateDirectory(baseDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var snapshotDir = System.IO.Path.Combine(baseDir, $"{stamp}-EPB{alarmChannel:D2}");
                System.IO.Directory.CreateDirectory(snapshotDir);

                try
                {
                    var diagnosticDevices = ResolveAlarmSnapshotAffectedChannels(alarmChannel, reason)
                        .Select(_acq.GetDeviceForEpbChannel)
                        .Where(device => !string.IsNullOrWhiteSpace(device))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    _acq.ExportDiagnostics(snapshotDir, diagnosticDevices, TimeSpan.FromSeconds(60));
                    _acq.ExportFastCurrentEvidence(
                        snapshotDir,
                        alarmChannel,
                        TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    _log.Warn($"DAQ 60秒时序诊断快照导出失败：{ex.Message}", "AI");
                }

                var affectedChannels = ResolveAlarmSnapshotAffectedChannels(alarmChannel, reason);
                var sameGroupChannels = Array.Empty<int>();
                if (includeSameGroup)
                {
                    var alarmGroupId = GetElectricalGroupId(alarmChannel);
                    if (alarmGroupId > 0)
                    {
                        sameGroupChannels = ResolveAlarmCycleEvidenceChannels(
                                alarmChannel,
                                _cfg.Test.Groups,
                                true)
                            .Where(channel => channel != alarmChannel)
                            .ToArray();
                    }
                    else
                    {
                        _log.Warn(
                            $"EPB[{alarmChannel}] 未找到电源组，报警快照仅导出报警通道。",
                            "落盘");
                    }
                }

                // 圈数据严格限制为报警通道，以及配置允许时的同电源组通道。
                // DAQ、控制时间线和电源遥测仍按原逻辑保存为公共诊断证据。
                var cycleEvidenceChannels = new[] { alarmChannel }
                    .Concat(sameGroupChannels)
                    .Distinct()
                    .ToArray();
                var exportedCycleChannels = new List<int>();
                var skippedCycleChannels = new List<int>();

                AlarmCycleSnapshotEvidence alarmEvidence = null;
                foreach (var ch in cycleEvidenceChannels)
                {
                    var subName = ch == alarmChannel ? $"EPB{ch:D2}_ALARM" : $"EPB{ch:D2}";
                    var subDir = System.IO.Path.Combine(snapshotDir, subName);
                    System.IO.Directory.CreateDirectory(subDir);

                    try
                    {
                        if (ch == alarmChannel)
                        {
                            if (hasActiveAlarmCycle)
                            {
                                var sealUtc = DateTime.UtcNow;
                                FinalizeCyclePersistence(
                                    recorder,
                                    ch,
                                    alarmCycleNumber,
                                    sealUtc,
                                    recorder.GetCurrentCycleSampleCount(ch));
                                alarmEvidence = recorder.SealAndExportAlarmCycle(
                                    ch,
                                    alarmCycleNumber,
                                    subDir,
                                    sealUtc);

                                // 正式圈故障是在本圈完成并可靠提交后才判定。此时计时器收尾可能已把
                                // CurrentCycle 置空，而报警后台仍持有相同圈号。不能把“未取得封存权”
                                // 误判成无证据；应从已经不可变的 completed/alarm/failed 圈回读。
                                if (alarmEvidence?.IsValid != true &&
                                    alarmEvidence?.WasClaimed != true)
                                {
                                    alarmEvidence = AlarmCycleSnapshotRecovery.TryExportFinalizedCycle(
                                        recorder,
                                        ch,
                                        alarmCycleNumber,
                                        subDir,
                                        alarmEvidence);
                                    if (alarmEvidence?.IsValid == true)
                                    {
                                        _log.Info(
                                            $"EPB[{ch}] 报警触发圈已由正式圈收尾封存，" +
                                            $"已从持久化索引回读 Cycle={alarmCycleNumber} CSV/BIN。",
                                            "落盘");
                                    }
                                }
                            }
                            else if (recorder is ICycleAttemptEvidenceExporter exporter)
                            {
                                var frozen = exporter.ExportCycleAttemptTo(
                                    ch,
                                    alarmCycleNumber,
                                    subDir,
                                    true,
                                    true);
                                alarmEvidence = frozen == null
                                    ? new AlarmCycleSnapshotEvidence
                                    {
                                        ValidationError = "冻结故障圈导出器未返回证据。"
                                    }
                                    : EpbDiskWriter.ValidateAlarmCycleSnapshotPair(
                                        frozen.CsvPath,
                                        frozen.BinPath,
                                        ch,
                                        alarmCycleNumber);
                                alarmEvidence.WasClaimed = false;
                                alarmEvidence.FinalStatus = "FrozenAbortedCycle";
                            }
                            if (alarmEvidence?.IsValid == true)
                            {
                                recorder.FlushRecentTo(ch, lastN, subDir, includeRunningCycle: false);
                                exportedCycleChannels.Add(ch);
                            }
                            else
                            {
                                skippedCycleChannels.Add(ch);
                            }
                        }
                        else
                        {
                            recorder.FlushRecentTo(ch, sameGroupLastN, subDir, includeRunningCycle: true);
                            if (System.IO.Directory.EnumerateFiles(
                                    subDir,
                                    "*.*",
                                    System.IO.SearchOption.TopDirectoryOnly)
                                .Any(path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                                             path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                                exportedCycleChannels.Add(ch);
                            else
                                skippedCycleChannels.Add(ch);
                        }
                    }
                    catch (Exception ex)
                    {
                        skippedCycleChannels.Add(ch);
                        _log.Warn($"报警快照导出失败：EPB[{ch}] {ex.Message}", "落盘");
                    }
                }

                var alarmSubDir = System.IO.Path.Combine(snapshotDir, $"EPB{alarmChannel:D2}_ALARM");
                alarmEvidence ??= new AlarmCycleSnapshotEvidence
                {
                    CsvPath = System.IO.Path.Combine(
                        alarmSubDir,
                        $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.csv"),
                    BinPath = System.IO.Path.Combine(
                        alarmSubDir,
                        $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.bin"),
                    ValidationError = "报警圈未完成原子封存。"
                };

                // DO时间线和错峰计划是辅助证据；其写入失败不得改变当前报警圈
                // 由CSV/BIN完整性决定的 alarm/failed 结果。
                try
                {
                    ExportControlEvidence(
                        snapshotDir,
                        alarmChannel,
                        alarmCycleNumber,
                        reason,
                        alarmEvidence,
                        alarmUtc);
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警控制证据导出失败：EPB[{alarmChannel}] {ex.Message}", "落盘");
                }

                try
                {
                    var electricalGroupId = GetElectricalGroupId(alarmChannel);
                    if (_powerSupply != null && electricalGroupId > 0)
                    {
                        var powerTelemetry = _powerSupply.GetRecentTelemetry(
                            electricalGroupId,
                            TimeSpan.FromSeconds(60));
                        PowerSupplyCoordinator.ExportTelemetryCsv(
                            System.IO.Path.Combine(snapshotDir, "power-supply-telemetry.csv"),
                            powerTelemetry,
                            alarmCycleNumber,
                            reason);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警电源遥测导出失败：EPB[{alarmChannel}] {ex.Message}", "落盘");
                }

                try
                {
                    WriteAlarmSnapshotManifest(
                        snapshotDir,
                        alarmChannel,
                        alarmCycleNumber,
                        lastN,
                        reason,
                        affectedChannels,
                        cycleEvidenceChannels,
                        exportedCycleChannels.Distinct().OrderBy(x => x).ToArray(),
                        skippedCycleChannels.Distinct().OrderBy(x => x).ToArray(),
                        includeSameGroup,
                        sameGroupLastN);
                    WriteWarningChain(snapshotDir, alarmChannel, reason, alarmCycleNumber);
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警快照清单写入失败：{ex.Message}", "落盘");
                }

                if (alarmEvidence.IsValid)
                    _log.Warn($"报警快照已导出：EPB[{alarmChannel}] {reason} -> {snapshotDir}", "落盘");
                else
                    _log.Warn(
                        $"报警快照校验失败：EPB[{alarmChannel}] Cycle={alarmCycleNumber} " +
                        $"Error={alarmEvidence.ValidationError} -> {snapshotDir}",
                        "落盘");

                return alarmEvidence;
            }
            finally
            {
                _frozenFaultCycleByChannel.TryRemove(alarmChannel, out _);
                _alarmSnapshotGate.Release();
            }
        }


        /// <summary>
        /// 停止所有通道：依次调用 <see cref="StopChannel"/> ，
        /// 并做一次兜底清空，确保下一次开始是“干净环境”。 
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopAll()
        {
            var caller = new StackTrace().GetFrame(1)?.GetMethod()?.Name;
            StopAll(StopContext.Legacy(caller));
        }

        private int[] ResolveAlarmSnapshotAffectedChannels(int alarmChannel, string reason)
        {
            IEnumerable<int> affected = new[] { alarmChannel };
            if (reason?.IndexOf("Daq", StringComparison.OrdinalIgnoreCase) >= 0 && _acq != null)
            {
                var device = _acq.GetDeviceForEpbChannel(alarmChannel);
                if (!string.IsNullOrWhiteSpace(device))
                    affected = Enumerable.Range(1, 12)
                        .Where(ch => string.Equals(
                            _acq.GetDeviceForEpbChannel(ch), device, StringComparison.OrdinalIgnoreCase));
            }
            else if (reason?.IndexOf("PowerSupply", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var groupId = GetElectricalGroupId(alarmChannel);
                affected = _cfg.Test.Groups.FirstOrDefault(x => x.Id == groupId)?.Members
                           ?? new List<int> { alarmChannel };
            }
            else if (reason?.IndexOf("Hydraulic", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                affected = _cfg.Test.Hydraulics
                    .FirstOrDefault(x => x.Members.Contains(alarmChannel))?.Members
                           ?? new List<int> { alarmChannel };
            }

            return affected
                .Where(ch => ch == alarmChannel || _currentCycleNumberByChannel.ContainsKey(ch))
                .Distinct()
                .OrderBy(ch => ch)
                .ToArray();
        }

        public void StopAll(StopContext context)
        {
            ObserveBackgroundTask(
                StopAllAsync(context ?? StopContext.Legacy(null), CancellationToken.None),
                "StopAll");
        }

        /// <summary>幂等停止全部 EPB，并分别返回电机DO、电源回读和压力证据。</summary>
        public Task<StopSafetyResult> StopAllAsync(CancellationToken token = default)
        {
            return StopAllAsync(StopContext.Legacy(nameof(StopAllAsync)), token);
        }

        public Task<StopSafetyResult> StopAllAsync(StopContext context, CancellationToken token = default)
        {
            context ??= StopContext.Legacy(null);
            lock (_stopSafetyGate)
            {
                if (_stopSafetyTask != null && !_stopSafetyTask.IsCompleted)
                {
                    if (ShouldStopAcquisitionBeforeFinalPersistence(context.Source) &&
                        !ShouldStopAcquisitionBeforeFinalPersistence(_stopSafetyTaskSource))
                    {
                        _stopSafetyTask = ContinueWithFinalExitStopAsync(
                            _stopSafetyTask,
                            context,
                            token);
                        _stopSafetyTaskSource = context.Source;
                    }
                    return _stopSafetyTask;
                }
                if (_lastStopSafetyResult != null && !IsBatchSessionActive &&
                    _activeBatchId == Guid.Empty &&
                    _lastStopSafetyResult.CanRestartInProcess &&
                    (!ShouldStopAcquisitionBeforeFinalPersistence(context.Source) ||
                     ShouldStopAcquisitionBeforeFinalPersistence(_lastStopSafetyResult.Source)) &&
                    CaptureLogicalQuiescenceSnapshot().IsQuiescent)
                    return Task.FromResult(_lastStopSafetyResult.Clone(reused: true));
                _stopSafetyTask = RunStopSafetyAsync(context, token);
                _stopSafetyTaskSource = context.Source;
                return _stopSafetyTask;
            }
        }

        private async Task<StopSafetyResult> ContinueWithFinalExitStopAsync(
            Task<StopSafetyResult> previous,
            StopContext context,
            CancellationToken token)
        {
            try { await previous.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Warn($"等待在途停止流程后执行最终退出收口：{ex.GetBaseException().Message}", "EPB");
            }
            return await RunStopSafetyAsync(context, token).ConfigureAwait(false);
        }

        public Task<bool> ShutdownPersistenceAsync(int timeoutMs = 10000)
        {
            return _persistence.ShutdownAsync(Math.Max(1, Math.Min(10000, timeoutMs)));
        }

        public static int[] ResolveAlarmCycleEvidenceChannels(
            int alarmChannel,
            IEnumerable<ElectricalGroup> groups,
            bool includeSameElectricalGroup)
        {
            if (alarmChannel < 1 || alarmChannel > 12)
                throw new ArgumentOutOfRangeException(nameof(alarmChannel));
            if (!includeSameElectricalGroup) return new[] { alarmChannel };
            var group = (groups ?? Enumerable.Empty<ElectricalGroup>())
                .FirstOrDefault(item => item?.Members?.Contains(alarmChannel) == true);
            return new[] { alarmChannel }
                .Concat(group?.Members ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        public void ReleaseHardwareForRestart()
        {
            try
            {
                if (!_taskSupervisor.DrainAsync(2000).GetAwaiter().GetResult())
                {
                    var pending = string.Join(",", _taskSupervisor.Snapshot()
                        .Select(x => $"{x.Operation}(EPB{x.Channel},RunId={x.RunId:N})"));
                    _log.Warn($"释放硬件前后台任务未在2秒内收口：{pending}", "EPB");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"释放硬件前等待后台任务失败：{ex.Message}", "EPB");
            }
            try
            {
                if (_hydCoordinator != null &&
                    !_hydCoordinator.DrainBackgroundTasksAsync(2000).GetAwaiter().GetResult())
                    _log.Warn("释放硬件前液压后台任务未在2秒内收口。", "液压协调");
            }
            catch (Exception ex)
            {
                _log.Warn($"释放硬件前等待液压后台任务失败：{ex.Message}", "液压协调");
            }
            try { _timerRuntimeWatchdog?.Dispose(); } catch { }
            try { _powerSupply?.Dispose(); } catch { }
            try { _acq?.Dispose(); } catch { }
            try { _ao?.ResetAll(); } catch { }
            try { _ao?.Dispose(); } catch { }
            try { _do?.AllOff(); } catch { }
            try { _do?.Dispose(); } catch { }
        }

        private async Task<StopSafetyResult> RunStopSafetyAsync(StopContext context, CancellationToken token)
        {
            var startedUtc = DateTime.UtcNow;
            var runId = _activeBatchId;
            var stopCycles = CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12));
            var stopCycleWindowsSealed = TrySealSoftwareRecoveryCycleWindows(
                stopCycles,
                startedUtc,
                $"StopAll:{context.Source}");
            var stopPersistenceBoundaries = new Dictionary<string, long>
            {
                ["Dev1"] = _acq.GetLastAcceptedSequence("Dev1"),
                ["Dev2"] = _acq.GetLastAcceptedSequence("Dev2")
            };
            if (context.Source == StopSource.ManualUi ||
                context.Source == StopSource.ApplicationClosing ||
                context.Source == StopSource.ProgramExit ||
                context.Source == StopSource.UnknownLegacy)
            {
                foreach (var channel in Enumerable.Range(1, 12))
                    _manualStopRequestedChannels[channel] = 0;
                foreach (var recovery in _recoverableChannelRestartJobs.Values)
                {
                    try { recovery.Cancel(); } catch { }
                }
            }
            if (context.Source != StopSource.SystemFault)
                NotifyRunAuthorizationRevoking(
                    context.Source,
                    context.Reason,
                    context.Initiator,
                    Guid.TryParse(context.CorrelationId, out var requestedCorrelation)
                        ? requestedCorrelation
                        : Guid.NewGuid(),
                    context.FaultScope);
            _log.Info(
                $"收到停止全部 EPB 请求：{context.ToLogText()}; SafetyActionStartedUtc={startedUtc:O}",
                "EPB");

            // 1. 先使运行代次失效并取消/等待全部恢复，再冻结新启动。
            // 任何已越过 await 的旧恢复任务都无法再提交恢复、恢复计时或重新接纳写盘。
            await CancelAllDaqRecoveriesAsync(
                    $"StopAll:{context.Source}:{context.CorrelationId}")
                .ConfigureAwait(false);
            var recoveryOwnersExited = await _recoveryOwnership.CancelAllAsync(
                    RecoveryOwnershipTakeoverTimeoutMs,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!recoveryOwnersExited)
                _log.Warn(
                    $"StopAll等待恢复所有者退出超过{RecoveryOwnershipTakeoverTimeoutMs}ms；" +
                    "运行代次已失效，后续提交将被拒绝。",
                    "EPB");
            EndBatchSession(cancel: true);
            // 捕获 Stop 请求与运行授权撤销之间极窄竞态内可能刚开始的圈；窗口采用
            // 单调收紧语义，重复 Seal 不会重新放大第一次截止时间。
            foreach (var pair in CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12)))
                stopCycles[pair.Key] = pair.Value;
            stopCycleWindowsSealed = TrySealSoftwareRecoveryCycleWindows(
                                         stopCycles,
                                         startedUtc,
                                         $"StopAll:{context.Source}:AuthorizationRevoked") &&
                                     stopCycleWindowsSealed;
            var stopCorrelation = Guid.TryParse(context.CorrelationId, out var parsedStopCorrelation)
                ? parsedStopCorrelation
                : Guid.NewGuid();
            _persistence.SuppressAfter("Dev1", startedUtc, stopCorrelation);
            _persistence.SuppressAfter("Dev2", startedUtc, stopCorrelation);
            var channels = _timers.Keys
                .Concat(_runners.Keys)
                .Concat(_hydraulicParticipants.Keys)
                .Concat(_hydraulicLeaseByChannel.Keys)
                .Concat(_stopCtsByChannel.Keys)
                .Concat(_cyclePauseCtsByChannel.Keys)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            // 2. 对活动通道发送高优先级全关，并独立记录命令结果。
            var motorOk = true;
            var motorErrors = new List<string>();
            foreach (var channel in channels)
            {
                UnmarkHydraulicParticipant(channel);
                try { CancelCyclePauseCts(channel); } catch { }
                try { CancelStopCts(channel); } catch { }
                RemoveTimerRuntime(channel, nameof(RunStopSafetyAsync));
                RemoveRunnerRuntime(channel, nameof(RunStopSafetyAsync));
                try
                {
                    if (!CommandEpbOffHighPriority(channel, nameof(RunStopSafetyAsync)))
                    {
                        motorOk = false;
                        motorErrors.Add($"EPB{channel:D2}:DO返回失败");
                    }
                }
                catch (Exception ex)
                {
                    motorOk = false;
                    motorErrors.Add($"EPB{channel:D2}:{ex.Message}");
                }
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.ManualStopped,
                    "StopAll",
                    context.Source == StopSource.ManualUi
                        ? $"人工停止，启动/自动恢复已取消。{context.Reason ?? string.Empty}"
                        : context.Reason ?? "停止全部",
                    affectedChannels: channels,
                    correlationId: stopCorrelation,
                    // 现场人工停止是新的权威终态，允许覆盖旧 StartBlocked；
                    // 原报警/启动失败原因仍保留在历史日志和关联号中。
                    allowTerminalReset: context.Source == StopSource.ManualUi,
                    runIdOverride: runId);
            }
            ClearChannelRuntimes(nameof(RunStopSafetyAsync));

            // 3. 液压释放/压力确认和程控电源Disable/回读并行，互不遮蔽结果。
            var pressureTask = ConfirmPressureSafeForStopAsync(context);
            var powerTask = ConfirmPowerOffForStopAsync(context, token);
            var pressure = await pressureTask.ConfigureAwait(false);
            var power = await powerTask.ConfigureAwait(false);
            // 退出进程前必须先停止新回调，再确定最终耐久边界；否则 DAQ 在边界检查
            // 与 Dispose 之间仍可接收新批次，最后一批可能没有机会完成 Raw/SQLite 落盘。
            if (ShouldStopAcquisitionBeforeFinalPersistence(context.Source))
            {
                try { _acq.Stop(); }
                catch (Exception ex)
                {
                    _log.Warn($"退出前停止DAQ失败，将继续按已接收边界排空：{ex.Message}", "AI");
                }
            }
            var rawStorageFlushed = true;
            var rawStorageFlushError = string.Empty;
            if (ShouldStopAcquisitionBeforeFinalPersistence(context.Source))
            {
                var flushRaw = Volatile.Read(ref _pausePersistenceFlush);
                if (flushRaw == null)
                {
                    rawStorageFlushed = false;
                    rawStorageFlushError = "Raw最终落盘回调未注册";
                }
                else
                {
                    try
                    {
                        await flushRaw(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        rawStorageFlushed = false;
                        rawStorageFlushError = ex.GetBaseException().Message;
                        _log.Error($"退出前Raw最终落盘失败，保留进程等待重试：{rawStorageFlushError}", "落盘", ex);
                    }
                }
            }
            var persistenceBoundaries = await WaitForStopPersistenceBoundariesAsync(
                    stopPersistenceBoundaries,
                    10000,
                    RequiresRecoveredPersistenceStateForStop(context.Source))
                .ConfigureAwait(false);
            var persistenceBoundaryConfirmed =
                rawStorageFlushed &&
                persistenceBoundaries.Length == stopPersistenceBoundaries.Count &&
                persistenceBoundaries.All(item => item.Closed);
            if (persistenceBoundaryConfirmed)
            {
                var stopCyclesFinalized = stopCycleWindowsSealed &&
                                          TryFinalizeStoppedCyclesAfterDurableBoundary(
                                              stopCycles,
                                              startedUtc,
                                              "canceled");
                if (!stopCyclesFinalized)
                {
                    persistenceBoundaryConfirmed = false;
                    _log.Error(
                        "停止数据边界已闭合，但仍有活动圈终态未提交；禁止同进程重启。",
                        "落盘");
                }
            }
            if (!persistenceBoundaryConfirmed)
                _log.Error(
                    "停止流程未能在10秒内同时越过Raw发布边界和持久化边界；" +
                    "禁止把本次停止视为可直接重启的完整收尾。",
                    "落盘");
            // Never cancel/dispose the writer while the accepted production prefix is still
            // unresolved. The closing UI remains open and may retry StopAll after the disk
            // recovers; shutting down here would make that retry impossible and could discard
            // the final Raw batch/alarm evidence.
            if (ShouldShutdownPersistenceForStop(context.Source, persistenceBoundaryConfirmed))
                await _persistence.ShutdownAsync(10000).ConfigureAwait(false);

            var logicalState = CaptureLogicalQuiescenceSnapshot();
            var result = new StopSafetyResult
            {
                Source = context.Source,
                CorrelationId = context.CorrelationId ?? string.Empty,
                RunId = runId,
                MotorOffCommandSucceeded = motorOk,
                PowerOffConfirmed = power.ok,
                PressureSafeConfirmed = pressure.ok,
                PersistenceBoundaryConfirmed = persistenceBoundaryConfirmed,
                RawStorageFlushed = rawStorageFlushed,
                StartedUtc = startedUtc,
                CompletedUtc = DateTime.UtcNow,
                MotorError = string.Join("; ", motorErrors),
                PowerError = power.error,
                PressureError = pressure.error,
                PersistenceError = persistenceBoundaryConfirmed
                    ? string.Empty
                    : string.Join("; ",
                        new[] { rawStorageFlushed ? null : $"RawStorage:{rawStorageFlushError}" }
                            .Concat(persistenceBoundaries
                                .Where(item => !item.Closed)
                                .Select(item =>
                                    $"{item.Device}:RawDrained={item.RawPipelineDrained}," +
                                    $"Boundary={item.Boundary},Published={item.Published}," +
                                    $"Persisted={item.Persisted},Depth={item.QueueDepth}," +
                                    $"State={item.PersistenceState}"))
                            .Where(item => !string.IsNullOrWhiteSpace(item))),
                LogicalQuiescenceConfirmed = logicalState.IsQuiescent,
                LogicalError = logicalState.IsQuiescent ? string.Empty : logicalState.ToString(),
                LogicalState = logicalState
            };

            lock (_stopSafetyGate) _lastStopSafetyResult = result.Clone();
            var logText =
                $"StopAll分项结果：CorrelationId={result.CorrelationId}; " +
                $"MotorDO={(result.MotorOffCommandSucceeded ? "Confirmed" : "Unconfirmed")}; " +
                $"Power={(result.PowerOffConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"Pressure={(result.PressureSafeConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"RawStorage={(result.RawStorageFlushed ? "Confirmed" : "Unconfirmed")}; " +
                $"Persistence={(result.PersistenceBoundaryConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"Logical={(result.LogicalQuiescenceConfirmed ? "Confirmed" : "Pending")}; " +
                $"MotorError={result.MotorError}; PowerError={result.PowerError}; " +
                $"PressureError={result.PressureError}; PersistenceError={result.PersistenceError}; " +
                $"LogicalError={result.LogicalError}";
            if (result.CanRestartInProcess)
                _log.Info(logText, "EPB");
            else if (result.FullyConfirmed)
                _log.Warn("【物理安全已确认，但软件逻辑清场尚未完成】" + logText, "EPB");
            else if (result.CanReleaseAcquisition)
                _log.Warn("【电机DO和程控电源已关闭，但压力或逻辑清场未确认】" + logText, "EPB");
            else
                _log.Error(logText, "EPB");
            LogFieldSessionMetric(
                "Stop",
                runId,
                channels,
                result.CanRestartInProcess,
                context.Source.ToString());
            EndPowerSupplyTelemetryRecording();
            FlushPersistentLog();
            return result;
        }

        internal static bool ShouldShutdownPersistenceForStop(
            StopSource source,
            bool persistenceBoundaryConfirmed)
        {
            return persistenceBoundaryConfirmed &&
                   (source == StopSource.ApplicationClosing || source == StopSource.ProgramExit);
        }

        private void NotifyRunAuthorizationRevoking(
            StopSource source,
            string reason,
            string initiator,
            Guid correlationId,
            FaultScope? scope)
        {
            var context = new StopContext
            {
                Source = source,
                Reason = reason,
                Initiator = initiator,
                CorrelationId = (correlationId == Guid.Empty ? Guid.NewGuid() : correlationId)
                    .ToString("N"),
                RunId = _activeBatchId == Guid.Empty ? string.Empty : _activeBatchId.ToString("N"),
                FaultScope = scope,
                RequestedUtc = DateTime.UtcNow
            };
            // 外部授权/UI 是观察者，不能位于硬件断电和软件自愈的同步关键路径上。
            ObserveBackgroundTask(Task.Run(() =>
                NonCriticalObserver.Invoke(
                    RunAuthorizationRevoking,
                    context,
                    ex =>
                    {
                        _log.Error("撤销无人值守续测授权的订阅者执行失败。", "EPB", ex);
                        FlushPersistentLog();
                    })), "RunAuthorizationRevoking");
        }

        public static bool ShouldApplyRunAuthorizationRevocation(
            string checkpointRunId,
            string revokingRunId)
        {
            var checkpoint = NormalizeRunId(checkpointRunId);
            var revoking = NormalizeRunId(revokingRunId);
            // 旧检查点或旧事件没有运行身份时保持失效安全：允许撤销。
            if (checkpoint.Length == 0 || revoking.Length == 0) return true;
            return string.Equals(checkpoint, revoking, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自动恢复/续测属于会改变运行状态的主动动作，必须由两个非空且完全相同的
        /// RunId 授权。与撤权不同，这里不允许旧格式空身份走兼容路径；无法证明来源
        /// 的迟到故障只能保持当前状态，不能停止或重建一个新运行。
        /// </summary>
        public static bool AreSameNonEmptyRunIds(string checkpointRunId, string actionRunId)
        {
            var checkpoint = NormalizeRunId(checkpointRunId);
            var action = NormalizeRunId(actionRunId);
            return checkpoint.Length > 0 && action.Length > 0 &&
                   string.Equals(checkpoint, action, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRunId(string value)
        {
            var normalized = (value ?? string.Empty).Trim().Replace("-", string.Empty);
            return Guid.TryParseExact(normalized, "N", out var parsed)
                ? parsed.ToString("N")
                : string.Empty;
        }

        private async Task<(bool ok, string error)> ConfirmPressureSafeForStopAsync(StopContext context)
        {
            try
            {
                var hydraulicIds = _cfg.Test.Hydraulics.Select(x => x.Id).Distinct().ToArray();
                await Task.WhenAll(hydraulicIds.Select(id =>
                        _hydCoordinator.ForceReleaseAsync(id, $"StopAll:{context.Source}")))
                    .ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private async Task<(bool ok, string error)> ConfirmPowerOffForStopAsync(
            StopContext context,
            CancellationToken token)
        {
            try
            {
                if (_powerSupply != null)
                    await _powerSupply.DisableAllAsync(
                            $"StopAll Source={context.Source} CorrelationId={context.CorrelationId}",
                            token)
                        .ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private void InvalidateStopSafetyCache()
        {
            lock (_stopSafetyGate)
            {
                _lastStopSafetyResult = null;
                if (_stopSafetyTask?.IsCompleted == true)
                {
                    _stopSafetyTask = null;
                    _stopSafetyTaskSource = StopSource.UnknownLegacy;
                }
            }
        }

        private async Task DisableAllPowerSafeAsync(string reason)
        {
            try
            {
                if (_powerSupply != null)
                    await _powerSupply.DisableAllAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.Error($"程控电源关闭未完全确认：{ex.Message}", "程控电源", ex); }
            finally { EndPowerSupplyTelemetryRecording(); }
        }

        private void TryDisableIdlePowerGroup(int channel, string reason)
        {
            if (_powerSupply == null) return;
            var groupId = GetElectricalGroupId(channel);
            if (groupId <= 0) return;
            var members = _cfg.Test.Groups.FirstOrDefault(x => x.Id == groupId)?.Members ?? new List<int>();
            if (!IsPowerGroupIdle(members, IsHydraulicParticipant, x => _timers.ContainsKey(x))) return;
            ObserveBackgroundTask(Task.Run(async () =>
            {
                try { await _powerSupply.DisableGroupAsync(groupId, reason, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                finally
                {
                    if (_powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }
            }), "DisableIdlePowerGroup", channel);
        }

        internal static bool IsPowerGroupIdle(
            IEnumerable<int> members,
            Func<int, bool> isHydraulicParticipant,
            Func<int, bool> hasRunningTimer)
        {
            if (members == null) return true;
            return !members.Any(
                channel =>
                    (isHydraulicParticipant?.Invoke(channel) ?? false) ||
                    (hasRunningTimer?.Invoke(channel) ?? false));
        }

        private int GetElectricalGroupId(int channel)
        {
            return _cfg.Test.Groups.FirstOrDefault(x => x.Members.Contains(channel))?.Id ?? 0;
        }

        internal bool TryGetFreshPowerSupplyCurrent(
            int channel,
            out double measuredCurrentA,
            out double ageMs,
            out bool outputEnabled)
        {
            measuredCurrentA = double.NaN;
            ageMs = double.PositiveInfinity;
            outputEnabled = false;
            var groupId = GetElectricalGroupId(channel);
            var snapshot = groupId > 0 ? _powerSupply?.GetLatestSnapshot(groupId) : null;
            if (snapshot == null) return false;
            ageMs = Math.Max(0, (DateTime.UtcNow - snapshot.TimestampUtc).TotalMilliseconds);
            measuredCurrentA = snapshot.MeasuredCurrent;
            outputEnabled = snapshot.OutputEnabled;
            return snapshot.IsConnected &&
                   !double.IsNaN(measuredCurrentA) &&
                   !double.IsInfinity(measuredCurrentA) &&
                   ageMs <= 1000;
        }

        internal void RequestElectricalGroupEmergencyShutdown(int sourceChannel, string reason)
        {
            var groupId = GetElectricalGroupId(sourceChannel);
            if (groupId <= 0)
            {
                ObserveBackgroundTask(Task.Run(() => _log.Error(
                    $"EPB[{sourceChannel}] 请求电源组紧急关闭，但未找到电气组映射。Reason={reason}",
                    "程控电源")), "EmergencyShutdownMissingGroup", sourceChannel);
                return;
            }
            var members = _cfg.Test.Groups
                .FirstOrDefault(x => x.Id == groupId)?
                .Members
                .Distinct()
                .OrderBy(x => x)
                .ToArray() ?? new[] { sourceChannel };

            var sourceDevice = _acq.GetDeviceForEpbChannel(sourceChannel);
            DaqIncidentContext daqIncident = null;
            var daqDerived = !string.IsNullOrWhiteSpace(reason) &&
                reason.IndexOf(
                    "OffCurrentUnverifiableDaqStale",
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                _daqIncidentLatch.TryGet(_activeBatchId, sourceDevice, out daqIncident);
            var registration = _emergencyPowerGroupLatch.Register(
                groupId,
                daqDerived ? daqIncident.CorrelationId : Guid.NewGuid(),
                DateTime.UtcNow,
                nonDaqFault: !daqDerived);
            if (daqDerived && registration.IsFirst)
                ObserveDaqIncident(
                    sourceDevice,
                    "OffCurrentUnverifiableDaqStale",
                    reason,
                    DateTime.UtcNow,
                    GetAllDaqDeviceChannels(sourceDevice));

            // 先在当前线程阻止同组任何通道继续执行，并逐路发出高优先级DO关闭；
            // 网络电源关闭及回读随后独立执行，不能阻塞采样回调。
            foreach (var member in members)
            {
                if (_timers.TryGetValue(member, out var timer))
                    timer.Pause($"ElectricalGroupEmergency:{reason}");
                CancelCyclePauseCts(member);
                try { CommandEpbOffSafetyImmediate(member); } catch { }
            }

            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(members);
            TrySealSoftwareRecoveryCycleWindows(
                cutoffCycles,
                cutoffUtc,
                $"ElectricalGroupEmergency:{reason}");

            var correlationId = registration.CorrelationId;

            ObserveBackgroundTask(Task.Run(async () =>
            {
                foreach (var member in members)
                {
                    try { UnmarkHydraulicParticipant(member); } catch { }
                    try
                    {
                        ObserveSafetyTask(
                            HydraulicMarkReleaseAsync(member),
                            "ElectricalGroupEmergencyRelease",
                            member);
                    }
                    catch { }
                }

                if (registration.IsFirst)
                {
                    _log.Error(
                        $"电源组{groupId}触发失效安全联锁：Source=EPB{sourceChannel} " +
                        $"Affected=[{string.Join(",", members)}] CorrelationId={correlationId:N} " +
                        $"DaqDerived={daqDerived} Reason={reason}",
                        "程控电源");
                }
                else
                {
                    _log.Warn(
                        $"电源组{groupId}收到重复失效安全联锁请求，已复用原关联号并重新执行" +
                        $"逐路DO关闭和电源OFF回读。Source=EPB{sourceChannel} " +
                        $"Affected=[{string.Join(",", members)}] CorrelationId={correlationId:N} " +
                        $"OriginalStartedUtc={registration.StartedUtc:O} " +
                        $"RequestCount={registration.RequestCount} DaqDerived={daqDerived} Reason={reason}",
                        "程控电源");
                }

                if (registration.ShouldPublishNonDaqFault)
                {
                    var interlockFault = new ControlFault(
                        ExtractFaultCode(reason),
                        reason,
                        FaultScope.ElectricalGroup,
                        members,
                        groupId,
                        DateTime.UtcNow,
                        correlationId,
                        FaultClassification.SystemFault,
                        FaultRecoveryPolicy.Recoverable);
                    NonCriticalObserver.Invoke(
                        ControlFaultRaised,
                        interlockFault,
                        ex => _log?.Warn($"电源联锁观察者异常，已隔离：{ex.Message}", "程控电源"));
                    foreach (var member in members.Where(IsChannelEnabled))
                        PublishChannelRuntimeState(
                            member,
                            ChannelRuntimeState.Recovering,
                            "ElectricalGroupEmergencySelfHealing",
                            $"电源组安全断电异常，正在持续复核并自动续测。Reason={reason}",
                            sourceChannel,
                            members,
                            correlationId);
                }

                try
                {
                    if (_powerSupply != null)
                        await _powerSupply.DisableGroupAsync(
                                groupId,
                                $"EPB失效安全联锁 Source={sourceChannel} Reason={reason}",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"电源组{groupId}紧急关闭未得到可靠回读：{ex.Message}",
                        "程控电源",
                        ex);
                }
                finally
                {
                    if (_powerSupply == null || _powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }

                // DAQ 派生联锁由统一 DAQ 截止流程负责抑制准入和整设备耐久收口；
                // 非 DAQ 联锁则在启动电源自愈前完成通道级 FIFO 耐久前缀与圈终态。
                if (!daqDerived &&
                    !await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                            cutoffCycles,
                            cutoffUtc,
                            $"ElectricalGroupEmergency:{reason}",
                            _daqPersistenceRecoveryTimeoutMs,
                            CancellationToken.None)
                        .ConfigureAwait(false))
                    _log.Warn(
                        $"电源组{groupId}紧急联锁圈耐久边界尚未闭合；" +
                        "保持Recovering并交由电源自愈循环继续重试。",
                        "落盘");

                if (registration.ShouldPublishNonDaqFault)
                {
                    var recoveryFault = new PowerSupplyFault
                    {
                        TimestampUtc = DateTime.UtcNow,
                        SupplyId = groupId,
                        ElectricalGroupId = groupId,
                        Code = "OutputOffCommandFailed",
                        Reason = reason,
                        AffectedChannels = members,
                        Classification = FaultClassification.SystemFault
                    };
                    BeginPowerSupplySoftwareRecovery(
                        recoveryFault,
                        new ControlFault(
                            "PowerSupplyOutputOffCommandFailed",
                            reason,
                            FaultScope.ElectricalGroup,
                            members,
                            groupId,
                            DateTime.UtcNow,
                            correlationId,
                            FaultClassification.SystemFault,
                            FaultRecoveryPolicy.Recoverable));
                }
            }), "ElectricalGroupEmergencyShutdown", sourceChannel);
        }

        private LogicalQuiescenceSnapshot CaptureLogicalQuiescenceSnapshot()
        {
            var hydraulicGroups = _hydCoordinator == null
                ? Array.Empty<HydraulicGenerationSnapshot>()
                : (_cfg.Test?.Hydraulics ?? new List<HydraulicItem>())
                .Where(item => item?.Enabled == true)
                .Select(item => item.Id)
                .Distinct()
                .OrderBy(id => id)
                .Select(_hydCoordinator.ProbeGroupHealth)
                .ToArray();
            return new LogicalQuiescenceSnapshot
            {
                BatchLifecycleBusy = _batchLifecycleGate.IsBusy,
                BatchSessionActive = IsBatchSessionActive,
                ActiveBatchId = _activeBatchId,
                TimerCount = _timers.Count + _timerCache.Count,
                RunnerCount = _runners.Count + _runnerCache.Count,
                StopCtsCount = _stopCtsByChannel.Count,
                CycleCtsCount = _cyclePauseCtsByChannel.Count,
                HydraulicParticipantCount = _hydraulicParticipants.Count,
                HydraulicLeaseCount = _hydraulicLeaseByChannel.Count,
                DaqRecoveryCount = _daqAutoRecovery.Count,
                SoftwareRecoveryCount = _hydraulicSoftwareRecoveryGroups.Count +
                                        _powerSoftwareRecoveryGroups.Count +
                                        _affectedGroupResetInProgress.Count +
                                        _isolatedInfrastructureRecoveryScheduled.Count +
                                        _timerRuntimeRecoveries.Count +
                                        _activeCycleLimitRecoveries.Count +
                                        _alarmCycleFinalizationRetries.Count +
                                        _formalPersistenceRecoveryPendingCycles.Count +
                                        _currentCycleNumberByChannel.Count,
                RecoveryOwnerCount = _recoveryOwnership.ActiveCount,
                HydraulicGroups = hydraulicGroups
            };
        }

        private async Task<StopSafetyResult> FinalizeLogicalQuiescenceForRestartAsync(
            StopSafetyResult physicalResult,
            string reason,
            CancellationToken token)
        {
            var result = (physicalResult ?? new StopSafetyResult()).Clone();
            var errors = new List<string>();

            foreach (var channel in _cyclePauseCtsByChannel.Keys.ToArray())
                try { CancelCyclePauseCts(channel); } catch { }
            foreach (var channel in _stopCtsByChannel.Keys.ToArray())
                try { CancelStopCts(channel); } catch { }
            foreach (var channel in _hydraulicParticipants.Keys.ToArray())
                UnmarkHydraulicParticipant(channel);

            foreach (var channel in _hydraulicLeaseByChannel.Keys.ToArray())
            {
                try
                {
                    await AbortHydraulicLeaseForChannelAsync(
                            channel,
                            reason + ":LogicalQuiescence")
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add($"EPB{channel}Lease:{ex.GetBaseException().Message}");
                }
            }

            if (_hydCoordinator != null)
            {
                foreach (var hydraulicId in (_cfg.Test?.Hydraulics ?? new List<HydraulicItem>())
                             .Where(item => item?.Enabled == true)
                             .Select(item => item.Id)
                             .Distinct()
                             .OrderBy(id => id))
                {
                    var snapshot = _hydCoordinator.ProbeGroupHealth(hydraulicId);
                    if (snapshot.IsHealthyForFreshStart) continue;
                    if (snapshot.ActiveGenerationCount != 0 ||
                        snapshot.ActiveOperationCount != 0 ||
                        snapshot.ActiveLeaseCount != 0)
                    {
                        errors.Add("ActiveHydraulicWork:" + snapshot);
                        continue;
                    }
                    try
                    {
                        await _hydCoordinator.RebuildGroupAsync(
                                hydraulicId,
                                reason,
                                timeoutMs: 10000,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"H{hydraulicId}Rebuild:{ex.GetBaseException().Message}");
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            var logical = CaptureLogicalQuiescenceSnapshot();
            result.LogicalState = logical;
            result.LogicalQuiescenceConfirmed = logical.IsQuiescent && errors.Count == 0;
            result.LogicalError = result.LogicalQuiescenceConfirmed
                ? string.Empty
                : string.Join("; ", errors.Concat(new[] { logical.ToString() }));
            result.CompletedUtc = DateTime.UtcNow;
            lock (_stopSafetyGate) _lastStopSafetyResult = result.Clone();
            if (result.LogicalQuiescenceConfirmed)
                _log.Info("重新开始逻辑清场不变量全部通过：" + logical, "EPB");
            else
                _log.Error("重新开始逻辑清场失败：" + result.LogicalError, "EPB");
            return result;
        }

        private void EnsureStrictCurveControl(IEnumerable<int> channels)
        {
            if (_requirePowerSupply && _powerSupply == null)
                throw new InvalidOperationException(
                    "程控电源闭环未初始化，禁止启动。请检查 PowerSupplyConfig.xml。");
            foreach (var channel in channels)
            {
                if (GetEpbControlMode(channel) != EpbControlMode.AdaptiveCurrent)
                    throw new InvalidOperationException(
                        $"EPB{channel} 未启用 AdaptiveCurrent 严格完整曲线控制，禁止启动正式试验。");
            }
            if (_adaptiveShadowMode)
                throw new InvalidOperationException("EpbAdaptiveShadowMode=true 时只观察不保护，禁止启动正式试验。");
        }

        private void EnsureAdaptiveProfilesReady(IEnumerable<int> channels)
        {
            var notReady = (channels ?? Enumerable.Empty<int>())
                .Distinct()
                .Where(channel => !GetAdaptiveProfile(channel).IsStable)
                .OrderBy(channel => channel)
                .ToArray();
            if (notReady.Length > 0)
                throw new InvalidOperationException(
                    $"严格完整曲线基线尚未形成：EPB[{string.Join(",", notReady)}]。" +
                    "每路至少需要5个完整有效学习圈，禁止进入正式试验。");
        }

        private void ResetTransientFaultStateForRestart(IEnumerable<int> channels, string reason)
        {
            foreach (var channel in (channels ?? Enumerable.Empty<int>())
                         .Distinct()
                         .OrderBy(x => x))
            {
                _formalPersistenceRecoveryAttempts.TryRemove(channel, out _);
                _formalControlRecoveryAttempts.TryRemove(channel, out _);
                _formalPersistenceRecoveryPendingCycles.TryRemove(channel, out _);
                try
                {
                    bool changed;
                    if (_runnerCache.TryGetValue(channel, out var runner))
                    {
                        changed = runner.ResetTransientRunState();
                    }
                    else
                    {
                        var profile = GetAdaptiveProfile(channel);
                        changed = profile.ResetTransientFaultStreaks();
                        if (changed) SaveAdaptiveProfile(profile);
                    }
                    if (changed)
                        _log.Info(
                            $"EPB[{channel}] 已清除上一运行的瞬态连续故障计数；" +
                            $"保留学习模型。Reason={reason}",
                            "EPB");
                }
                catch (Exception ex)
                {
                    // 清理诊断计数失败不能成为新的启动门槛；本次完整圈仍会按正常逻辑覆盖计数。
                    _log.Warn(
                        $"EPB[{channel}] 清理瞬态连续故障计数失败，继续执行新启动：{ex.Message}",
                        "EPB");
                }
            }
        }

        internal static bool ShouldHoldDaqRecoveredChannelsForBatchPause(BatchPauseState state)
        {
            return state == BatchPauseState.PausePending ||
                   state == BatchPauseState.Paused ||
                   state == BatchPauseState.ResumeChecking ||
                   state == BatchPauseState.Qualification ||
                   state == BatchPauseState.Stopping;
        }

        internal static ChannelRuntimeState GetDaqRecoveredHeldRuntimeState(BatchPauseState state)
        {
            return state == BatchPauseState.ResumeChecking || state == BatchPauseState.Qualification
                ? ChannelRuntimeState.ResumeChecking
                : ChannelRuntimeState.Paused;
        }

        private static bool ReadBooleanAppSetting(string key, bool fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings[key];
                return bool.TryParse(raw, out var value) ? value : fallback;
            }
            catch
            {
                return fallback;
            }
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
            CompleteCycleAndScheduleEvidence(
                Recorder,
                epbId,
                cycleNumber,
                finalN,
                DateTime.UtcNow);
        }


        #region 卡钳预释放

        /// <summary>
        /// 批量执行启动定位（正向确认位置、断电确认、反向释放）。
        /// </summary>
        /// <param name="channels">要执行预释放的通道号（1..12）。</param>
        /// <param name="keepMs">
        /// 反向空行程保持时长（毫秒）。为 <c>null</c> 时，每个通道使用其 Runner 的默认值
        ///（通常来自配置字段 <c>_revEmptyKeepMs</c>）。
        /// </param>
        /// <param name="token">取消令牌。</param>
        /// <returns>全部通道任务完成的 <see cref="Task"/>。</returns>
        /// <remarks>
        /// - 按本次选中集合生成XML驱动的不可变错峰计划；不同电源组可并行，同组按计划相位启动。<br/>
        /// - 启动定位同样受实际压力资格联锁保护，压力未达标时不会给卡钳电机上电。
        /// </remarks>
        public async Task PreReleaseBatchAsync(int[] channels, int? keepMs, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var plan = ElectricalStaggerPlanner.Build(enabled, _cfg.Test.Groups, PeriodMs);
            var runId = Guid.NewGuid();
            RegisterRunContext(runId, plan);
            LogStaggerPlan(runId, plan);
            var failed = await PreReleaseBatchWithPlanAsync(enabled, keepMs, plan, token).ConfigureAwait(false);
            EnsurePreReleaseBatchSucceeded(failed.Select(x => x.Channel), _log);
        }

        /// <summary>
        /// 保留原有公共签名以兼容调用方。错峰值统一从XML电气组读取，
        /// <paramref name="deltaMs"/> 不再参与安全调度。
        /// </summary>
        public async Task PreReleaseBatchStaggeredAsync(
            int[] channels,
            int? keepMs,
            int deltaMs,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var plan = ElectricalStaggerPlanner.Build(enabled, _cfg.Test.Groups, PeriodMs);
            var runId = Guid.NewGuid();
            RegisterRunContext(runId, plan);
            LogStaggerPlan(runId, plan);
            _log.Warn(
                $"PreReleaseBatchStaggeredAsync 的 deltaMs={deltaMs} 已忽略；实际使用XML ElectricalGroups/StaggerMs。",
                "EPB");
            var failed = await PreReleaseBatchWithPlanAsync(enabled, keepMs, plan, token).ConfigureAwait(false);
            EnsurePreReleaseBatchSucceeded(failed.Select(x => x.Channel), _log);
        }

        /// <summary>
        /// 按批次不可变错峰计划启动定位。所有任务一次性创建，不等待前一相位完成。
        /// </summary>
        private async Task<StartupPositioningResult[]> PreReleaseBatchWithPlanAsync(
            int[] channels,
            int? keepMs,
            ElectricalStaggerPlan staggerPlan,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));
            if (staggerPlan == null)
                throw new ArgumentNullException(nameof(staggerPlan));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var failedResults = new ConcurrentBag<StartupPositioningResult>();
            var runId = _activeBatchId == Guid.Empty ? Guid.NewGuid() : _activeBatchId;
            // 每一次人工启动/恢复都必须是新的液压预释放代次。旧实现固定使用 Slot=0，
            // 单卡钳恢复时会命中整批启动遗留的 [4,5] 成员快照，从而拒绝 Requested=[4]。
            var positioningSlot = Interlocked.Increment(ref _startupPositioningGeneration);

            // P0：任何预释放电机动作前，先按压力组完成实际压力资格。
            foreach (var group in enabled.GroupBy(ch => ch <= 6 ? 1 : 2))
            {
                var members = group.ToArray();
                await EnterHydraulicStartupPhaseWithSelfHealingAsync(
                        new HydraulicGenerationKey(
                            runId,
                            group.Key,
                            HydraulicPhaseKind.PreRelease,
                            positioningSlot),
                        members,
                        token)
                    .ConfigureAwait(false);
            }

            // 液压资格确认可能耗时数秒。锚点必须在资格确认完成后创建，
            // 否则 0/800ms 相位均会过期并被同刻放行。
            var anchorUtc = ElectricalStaggerExecutor.EnsureAnchorInFuture(
                DateTime.UtcNow.AddMilliseconds(500),
                DateTime.UtcNow);

            try
            {
                await ElectricalStaggerExecutor.RunAsync(
                    enabled,
                    staggerPlan,
                    anchorUtc,
                    async (ch, ct) =>
                    {
                        var assignment = staggerPlan.Get(ch);
                        var plannedStartUtc = anchorUtc.AddMilliseconds(assignment.PhaseMs);
                        var actualStartUtc = DateTime.UtcNow;
                        MarkElectricalPhaseDue(ch, plannedStartUtc);
                        _log.Info(
                            $"EPB[{ch}] 启动定位计划：Group={assignment.ElectricalGroupId}，" +
                            $"Index={assignment.SelectedIndexInGroup}，Phase={assignment.PhaseMs}ms，" +
                            $"PlannedUtc={plannedStartUtc:O}，ActualUtc={actualStartUtc:O}，" +
                            $"DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3}。",
                            "EPB");
                        var runner = GetRunner(ch);
                        var concreteRunner = runner as EpbCycleRunner
                            ?? throw new InvalidOperationException($"EPB[{ch}] Runner 类型不支持启动定位。");
                        var detectMs = Math.Max(1, runner.DefaultPreReleaseDetectTimeoutMs);
                        var attempt = 0;
                        while (true)
                        {
                            attempt++;
                            StartupPositioningResult result;
                            try
                            {
                                result = await concreteRunner.StartupPositioningAsync(keepMs, detectMs, ct)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                // 启动定位返回值会携带真实过流等现场证据；直接抛出的异常通常是
                                // 通信、状态机或旧任务残留等软件瞬态。不要让它冒泡为整批启动失败，
                                // 先关闭本通道并按同一次人工启动意图持续重试。
                                var offSucceeded = false;
                                try { offSucceeded = CommandEpbOffSafetyImmediate(ch); } catch { }
                                if (!offSucceeded)
                                {
                                    RequestElectricalGroupEmergencyShutdown(
                                        ch,
                                        "StartupPositioningOutputOffCommandFailed");
                                    if (ShouldEscalateSoftwareRecovery(attempt))
                                        throw new SoftwareSelfHealingExhaustedException(
                                            "StartupPositioningOutputOff",
                                            attempt,
                                            new SoftwareSelfHealingRetryException(
                                                $"EPB[{ch}] 启动定位断电连续{attempt}次未确认。",
                                                ex));
                                    var offDelayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                                    PublishChannelRuntimeState(
                                        ch,
                                        ChannelRuntimeState.Recovering,
                                        "StartupPositioningOutputOffSelfHealing",
                                        $"启动定位断电尚未确认，{offDelayMs}ms后有界重试；三次失败将整批重建。",
                                        affectedChannels: enabled,
                                        correlationId: runId,
                                        allowTerminalReset: false);
                                    await Task.Delay(offDelayMs, ct).ConfigureAwait(false);
                                    continue;
                                }
                                if (ShouldEscalateSoftwareRecovery(attempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningException",
                                        attempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位连续{attempt}次软件异常。",
                                            ex));
                                var exceptionDelayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                                PublishChannelRuntimeState(
                                    ch,
                                    ChannelRuntimeState.Recovering,
                                    "StartupPositioningExceptionSelfHealing",
                                    $"启动定位软件异常，第{attempt}次有界自愈，{exceptionDelayMs}ms后重试。",
                                    affectedChannels: enabled,
                                    correlationId: runId,
                                    allowTerminalReset: false);
                                _log.Warn(
                                    $"EPB[{ch}] 启动定位抛出软件异常，自动重试且不标记启动受阻。" +
                                    $"Attempt={attempt} DelayMs={exceptionDelayMs} " +
                                    $"Exception={ex.GetType().Name} Message={ex.Message}",
                                    "EPB");
                                await Task.Delay(exceptionDelayMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            if (result.Succeeded) break;
                            if (IsStartupPositioningHardwareConfirmed(result))
                            {
                                failedResults.Add(result);
                                await PublishStartupPositioningFailureAsync(result).ConfigureAwait(false);
                                break;
                            }

                            var retryOffSucceeded = false;
                            try { retryOffSucceeded = CommandEpbOffSafetyImmediate(ch); } catch { }
                            if (!retryOffSucceeded)
                            {
                                RequestElectricalGroupEmergencyShutdown(
                                    ch,
                                    "StartupPositioningOutputOffCommandFailed");
                                if (ShouldEscalateSoftwareRecovery(attempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningOutputOff",
                                        attempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位重试前断电连续{attempt}次未确认。"));
                                var offDelayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                                PublishChannelRuntimeState(
                                    ch,
                                    ChannelRuntimeState.Recovering,
                                    "StartupPositioningOutputOffSelfHealing",
                                    $"启动定位重试前断电尚未确认，{offDelayMs}ms后有界重试；三次失败将整批重建。",
                                    affectedChannels: enabled,
                                    correlationId: runId,
                                    allowTerminalReset: false);
                                await Task.Delay(offDelayMs, ct).ConfigureAwait(false);
                                continue;
                            }
                            if (ShouldEscalateSoftwareRecovery(attempt))
                                throw new SoftwareSelfHealingExhaustedException(
                                    "StartupPositioning",
                                    attempt,
                                    new SoftwareSelfHealingRetryException(
                                        $"EPB[{ch}] 启动定位连续{attempt}次软件瞬态未通过。" +
                                        $"Code={result.Code} Reason={result.Reason}"));
                            var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                            PublishChannelRuntimeState(
                                ch,
                                ChannelRuntimeState.Recovering,
                                "StartupPositioningSelfHealing",
                                $"启动定位软件瞬态未通过，第{attempt}次有界自愈，{delayMs}ms后重试。",
                                affectedChannels: enabled,
                                correlationId: runId,
                                allowTerminalReset: false);
                            _log.Warn(
                                $"EPB[{ch}] 启动定位软件瞬态自动重试，不标记启动受阻。" +
                                $"Attempt={attempt} DelayMs={delayMs} Code={result.Code} Reason={result.Reason}",
                                "EPB");
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }
                    },
                    token).ConfigureAwait(false);
            }
            finally
            {
                var releases = enabled.Select(HydraulicMarkReleaseAsync).ToArray();
                try { await Task.WhenAll(releases).ConfigureAwait(false); } catch { }
            }

            var failed = failedResults
                .GroupBy(x => x.Channel)
                .Select(x => x.First())
                .OrderBy(x => x.Channel)
                .ToArray();
            if (failed.Length == 0) return Array.Empty<StartupPositioningResult>();

            foreach (var item in failed)
            {
                var channel = item.Channel;
                try { CommandEpbOff(channel, nameof(PreReleaseBatchWithPlanAsync)); }
                catch (Exception ex)
                {
                    _log.Warn($"启动定位失败回滚时 EPB[{channel}] 断电命令异常：{ex.Message}", "EPB");
                }
            }

            return failed;
        }

        internal static void EnsurePreReleaseBatchSucceeded(
            IEnumerable<int> failedChannels,
            IAppLogger logger = null)
        {
            var failed = (failedChannels ?? Enumerable.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (failed.Length == 0) return;

            var message =
                $"批量启动定位失败：通道[{string.Join(",", failed)}]未完成正向定位及反向释放；" +
                "已拒绝进入学习/正式阶段。";
            logger?.Error(message, "EPB");
            throw new InvalidOperationException(message);
        }

        #endregion
    }
}
