using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Configuration;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Config.Models;
using Controller.Adaptive;
using Controller.Alarm;
using DataOperation;
using IO.NI;
using PowerSupply.Core;
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
                if (value is IMechanicalCycleRecorder mechanicalRecorder)
                {
                    foreach (var channel in Enumerable.Range(1, 12))
                    {
                        try
                        {
                            var durableCount = mechanicalRecorder
                                .GetMechanicalCycleCompletedCount(channel);
                            _mechanicalCycleBaseline.AddOrUpdate(
                                channel,
                                durableCount,
                                (_, existing) => Math.Max(existing, durableCount));
                            var lastCompletedUtc = mechanicalRecorder
                                .GetLastMechanicalCycleCompletedUtc(channel);
                            if (lastCompletedUtc.HasValue)
                                _watchdogLastMechanicalCompletedUtcTicks[channel] =
                                    lastCompletedUtc.Value.Ticks;
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn(
                                $"EPB[{channel}] 初始化机械完成耐久水位失败：{ex.Message}",
                                "落盘");
                        }
                    }
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
        public event Action<ChannelWarningOverlayChangedEvent> ChannelWarningOverlayChanged;

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
        // All Runner adaptive callbacks and manager lifecycle outputs use the
        // same production port.  The port is also the deterministic replay
        // seam; it never invents a second control path for diagnostics.
        private readonly EpbManagerAdaptiveLifecyclePort _adaptiveLifecyclePort;
        private readonly RecoveryTaskRegistry _recoveryTaskRegistry = new RecoveryTaskRegistry();
        // One gate covers incident registration, owner publication and terminal
        // cleanup.  This closes the small race in which a Timer/DAQ callback
        // could expose Recovering before its worker had an identity.
        private readonly object _recoveryContractGate = new object();
        // Linearizes recovery publication/start against StopAll admission.
        // StopAll closes this gate before revoking execution permits, so no
        // new Recovering owner can appear after the stop boundary.
        private readonly object _recoveryAdmissionGate = new object();
        private readonly Dictionary<Guid, RecoveryIncidentHandle> _activeRecoveryContracts =
            new Dictionary<Guid, RecoveryIncidentHandle>();
        private readonly RecoveryIncidentCoordinator _recoveryIncidentCoordinator;
        // Single committed source for watchdog recovery evidence.  All source
        // publishers replace immutable copies; heartbeat capture never scans
        // the live dictionaries to assemble an aggregate.
        private readonly RecoveryAggregateStore _recoveryAggregateStore =
            new RecoveryAggregateStore();
        private long _energizedChannelsMask;
        private readonly long _dev1ChannelMask;
        private readonly long _dev2ChannelMask;
        private readonly ChannelRuntimeStateStore _channelRuntimeStateStore = new();
        private readonly ChannelWarningOverlayStore _channelWarningOverlayStore = new();
        private readonly ChannelExecutionFence _channelExecutionFence = new();
        private readonly object[] _channelExecutionGates =
            Enumerable.Range(0, 12).Select(_ => new object()).ToArray();
        private readonly ConcurrentDictionary<int, long> _watchdogLastMechanicalCompletedUtcTicks = new();
        private readonly ConcurrentDictionary<int, long> _watchdogMechanicalCompletedCount = new();
        private readonly ConcurrentDictionary<int, long> _watchdogChannelProgressVersion = new();
        private readonly ConcurrentDictionary<int, long> _watchdogLastProgressUtcTicks = new();
        private readonly ConcurrentDictionary<int, string> _watchdogProgressKind = new();
        private readonly ConcurrentDictionary<int, long> _mechanicalCycleBaseline = new();
        private readonly ConcurrentDictionary<int, int> _watchdogConsecutiveSoftwareAborts = new();
        private readonly ConcurrentDictionary<int, long> _watchdogDoCommandSequence = new();
        private readonly ConcurrentDictionary<int, long> _physicalEnergizationEdges = new();

        private readonly SafetyMarginControlMode _safetyMarginControlMode;
        private readonly EpbControlMode _epbControlMode;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveProfileStore _adaptiveProfileStore;
        private readonly EpbProgramSafetySettings _programSafetySettings;
        private readonly HashSet<int> _adaptiveChannels;
        private readonly bool _historicalStorageEnabled;
        private readonly int _historicalRetainCyclesPerChannel;
        private readonly long _historicalMinimumFreeBytes;
        private readonly StorageFormatLevel _alarmStorageLevel;
        private readonly LearningRetentionMode _learningRetentionMode;
        private readonly int _learningSuccessfulRunRetainCount;
        private readonly int _learningFailedRunRetainCount;
        private DataHousekeepingService _dataHousekeeping;
        private IncidentHousekeepingService _incidentHousekeeping;


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
        private readonly ConcurrentDictionary<int, string> _nonRecoverableChannelFaultReasons =
            new();
        // ForwardUnderTargetHighLoadStall 具有明确的负载侧/机械侧风险，不能进入
        // 通用软件瞬态的无人值守重试。该锁存只允许在人工确认、空模型完整学习和
        // 两圈资格复核全部成功后清除。
        private readonly ConcurrentDictionary<int, string> _operatorFullRelearningRequired =
            new();
        private readonly ConcurrentDictionary<int, int> _periodOverrunStreaks = new();
        // 物理隔离已经成立、但项目 Enabled=false 尚未耐久提交时，必须把这个事实
        // 保留到无人值守启动结果。否则进程重启会从旧XML重新选中故障卡钳。
        private readonly ConcurrentDictionary<int, string> _disablePersistenceFailureReasons =
            new();
        private readonly ConcurrentDictionary<int, CancellationTokenSource>
            _recoverableChannelRestartJobs = new();

        private void FlushPersistentLog(bool durable = false)
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

        public IReadOnlyList<ChannelWarningOverlayChangedEvent> GetChannelWarningOverlays()
        {
            return _channelWarningOverlayStore.Snapshot();
        }

        internal static bool ShouldPreserveDaqRecoveringState(
            ChannelRuntimeState requestedState,
            bool daqRecoveryActive)
        {
            return daqRecoveryActive &&
                   (requestedState == ChannelRuntimeState.Running ||
                    requestedState == ChannelRuntimeState.WarningRunning ||
                    requestedState == ChannelRuntimeState.WaitingForSlotBarrier);
        }

        internal static ChannelRuntimeState ResolveRuntimeStateForRunnerWarning(
            ChannelRuntimeState? currentState)
        {
            // V2.13.0.20 起预警使用独立 overlay；该兼容入口只返回原生命周期，绝不再
            // 生成 WarningRunning。没有历史状态时按 Running 处理，但调用方仍只应发布 overlay。
            return currentState ?? ChannelRuntimeState.Running;
        }

        private void OnRunnerWarningOverlayRaised(AdaptiveWarningEvent warning)
        {
            if (warning == null || warning.Channel < 1 || warning.Channel > 12) return;
            if (warning.OccurredUtc == default) warning.OccurredUtc = DateTime.UtcNow;
            if (warning.CorrelationId == Guid.Empty) warning.CorrelationId = Guid.NewGuid();
            if (warning.AttemptId <= 0 &&
                _currentAttemptIdByChannel.TryGetValue(warning.Channel, out var attemptId))
                warning.AttemptId = attemptId;
            if (warning.CycleNumber == 0 &&
                _currentCycleNumberByChannel.TryGetValue(warning.Channel, out var cycleNumber))
                warning.CycleNumber = cycleNumber;
            if (string.IsNullOrWhiteSpace(warning.ScopeKey))
                warning.ScopeKey = $"Channel:{warning.Channel}";

            var runId = _runIdByChannel.TryGetValue(warning.Channel, out var channelRunId)
                ? channelRunId
                : _activeBatchId;
            var update = _channelWarningOverlayStore.Publish(new ChannelWarningOverlayChangedEvent
            {
                Channel = warning.Channel,
                Active = true,
                WarningCode = warning.NormalizedCode,
                WarningText = warning.Reason ?? string.Empty,
                TimestampUtc = warning.OccurredUtc,
                CorrelationId = warning.CorrelationId,
                RunId = runId,
                RunEpoch = Interlocked.Read(ref _runEpoch),
                RaisedAttemptId = warning.AttemptId,
                RaisedCycleNumber = warning.CycleNumber,
                RaisedUtc = warning.OccurredUtc
            });
            NonCriticalObserver.Invoke(
                ChannelWarningOverlayChanged,
                update,
                ex => _log?.Warn($"通道预警覆盖层观察者异常已隔离：{ex.Message}", "EPB"));
            var lifecycle = _channelRuntimeStateStore.Get(warning.Channel);
            _log.Info(
                $"FieldMetric WARNING_OVERLAY EPB={warning.Channel} Code={warning.NormalizedCode} " +
                $"Revision={update.Revision} Attempt={warning.AttemptId} Cycle={warning.CycleNumber} " +
                $"LifecyclePreserved={lifecycle?.State} LifecycleRevision={lifecycle?.Revision ?? 0}",
                "FIELD");
        }

        private void ClearChannelWarningOverlay(int channel, Guid correlationId = default)
        {
            var current = _channelWarningOverlayStore.Get(channel);
            if (current == null || !current.Active) return;
            var update = _channelWarningOverlayStore.Clear(
                channel,
                _activeBatchId,
                Interlocked.Read(ref _runEpoch),
                correlationId);
            NonCriticalObserver.Invoke(
                ChannelWarningOverlayChanged,
                update,
                ex => _log?.Warn($"通道预警覆盖层观察者异常已隔离：{ex.Message}", "EPB"));
        }

        internal static bool IsTrustedWarningOverlayRecoveryCandidate(
            ChannelWarningOverlayChangedEvent warning,
            Guid recoveredRunId,
            long recoveredRunEpoch,
            long recoveredAttemptId,
            int recoveredCycleNumber,
            EpbCycleOutcome outcome)
        {
            if (warning == null || !warning.Active) return false;
            if (recoveredRunId == Guid.Empty || warning.RunId != recoveredRunId) return false;
            if (recoveredRunEpoch <= 0 || warning.RunEpoch != recoveredRunEpoch) return false;
            if (recoveredAttemptId <= 0 || recoveredCycleNumber == 0) return false;
            if (outcome == null ||
                outcome.Kind != EpbCycleOutcomeKind.Success ||
                !outcome.MechanicalCycleCompleted)
                return false;

            // 预警所属尝试本身即便被误标为 Success，也绝不能在同圈末尾清除。
            // RaisedAttemptId=0 仅兼容进入首圈前的启动定位预警。
            return warning.RaisedAttemptId <= 0 || recoveredAttemptId > warning.RaisedAttemptId;
        }

        private bool TryClearChannelWarningOverlayAfterTrustedCycle(
            int channel,
            Guid runId,
            long runEpoch,
            int cycleNumber,
            long attemptId,
            EpbCycleOutcome outcome,
            string phase)
        {
            var current = _channelWarningOverlayStore.Get(channel);
            if (!IsTrustedWarningOverlayRecoveryCandidate(
                    current,
                    runId,
                    runEpoch,
                    attemptId,
                    cycleNumber,
                    outcome))
                return false;

            var reason = $"SubsequentTrustedCycleHealthy:{phase ?? "Unknown"}";
            if (!_channelWarningOverlayStore.TryClearIfCurrent(
                    channel,
                    current.RunId,
                    current.RunEpoch,
                    current.Revision,
                    current.WarningCode,
                    attemptId,
                    cycleNumber,
                    current.CorrelationId,
                    reason,
                    out var cleared))
            {
                var latest = _channelWarningOverlayStore.Get(channel);
                if (latest?.Active == true &&
                    latest.Revision == current.Revision &&
                    latest.RunId == current.RunId &&
                    latest.RunEpoch == current.RunEpoch &&
                    string.Equals(
                        latest.WarningCode,
                        current.WarningCode,
                        StringComparison.Ordinal))
                {
                    _log.Warn(
                        $"FieldMetric WARNING_OVERLAY_CLEAR_MISSING EPB={channel} " +
                        $"Code={current.WarningCode} Revision={current.Revision} " +
                        $"RecoveredAttempt={attemptId} RecoveredCycle={cycleNumber} Phase={phase}",
                        "FIELD");
                }
                return false;
            }

            NonCriticalObserver.Invoke(
                ChannelWarningOverlayChanged,
                cleared,
                ex => _log?.Warn($"通道预警覆盖层清除观察者异常已隔离：{ex.Message}", "EPB"));
            var latencyMs = current.RaisedUtc == default
                ? 0D
                : Math.Max(0D, (cleared.TimestampUtc - current.RaisedUtc).TotalMilliseconds);
            _log.Info(
                $"FieldMetric WARNING_OVERLAY_CLEARED EPB={channel} Code={current.WarningCode} " +
                $"RaisedRevision={current.Revision} ClearRevision={cleared.Revision} " +
                $"RaisedAttempt={current.RaisedAttemptId} RecoveredAttempt={attemptId} " +
                $"RecoveredCycle={cycleNumber} ClearLatencyMs={latencyMs:F3} Reason={reason}",
                "FIELD");
            return true;
        }

        internal static bool ShouldRejectRecoveryPauseOverride(
            ChannelRuntimeState previousState,
            ChannelRuntimeState requestedState,
            bool manualPauseOwned)
        {
            return previousState == ChannelRuntimeState.Recovering &&
                   !manualPauseOwned &&
                   (requestedState == ChannelRuntimeState.PausePending ||
                    requestedState == ChannelRuntimeState.Paused);
        }

        internal static bool IsValidFirstRecoveryPublication(
            Guid updateRunId,
            long runEpoch,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId,
            long ownerGeneration)
        {
            return updateRunId != Guid.Empty &&
                   runEpoch > 0 &&
                   ownerKind != RecoveryOwnerKind.None &&
                   ownerKind != RecoveryOwnerKind.Unknown &&
                   targetPhase != RecoveryTargetPhase.None &&
                   ownerId != Guid.Empty &&
                   ownerGeneration == runEpoch;
        }

        /// <summary>
        /// Unified entry point for every first Recovering publication outside
        /// the explicit TryBeginRecoveryIncident transaction.  Keeping these
        /// calls behind a named recovery boundary makes it auditable that a
        /// caller is publishing lifecycle state (and not a diagnostic overlay).
        /// The central publisher still performs the contract/lease gate and
        /// rejects a missing real worker; this method never creates a monitor
        /// task or a start-signal sentinel.
        /// </summary>
        private void PublishRecoveryIncidentState(
            int channel,
            ChannelRuntimeState state,
            string reasonCode,
            string reasonText,
            int? sourceChannel = null,
            IEnumerable<int> affectedChannels = null,
            Guid correlationId = default,
            bool allowTerminalReset = false,
            bool allowSystemFaultReset = false,
            Guid runIdOverride = default,
            RecoveryOwnerKind recoveryOwnerKind = RecoveryOwnerKind.None,
            RecoveryTargetPhase recoveryTargetPhase = RecoveryTargetPhase.None,
            Guid recoveryOwnerId = default,
            long recoveryOwnerGeneration = 0,
            long runEpochOverride = 0)
        {
            PublishChannelRuntimeState(
                channel,
                state,
                reasonCode,
                reasonText,
                sourceChannel,
                affectedChannels,
                correlationId,
                allowTerminalReset,
                allowSystemFaultReset,
                runIdOverride,
                recoveryOwnerKind,
                recoveryTargetPhase,
                recoveryOwnerId,
                recoveryOwnerGeneration,
                runEpochOverride);
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
            Guid runIdOverride = default,
            RecoveryOwnerKind recoveryOwnerKind = RecoveryOwnerKind.None,
            RecoveryTargetPhase recoveryTargetPhase = RecoveryTargetPhase.None,
            Guid recoveryOwnerId = default,
            long recoveryOwnerGeneration = 0,
            long runEpochOverride = 0)
        {
            // 物理安全目标可以包含同组全部硬件成员，但禁用通道不属于当前运行状态机。
            // 这是中央不变量：任何故障、恢复或迟到事件都不能把 Enabled=false 污染为
            // Paused/Recovering/Alarm。硬件 OFF 证据仍由 DO 追踪单独保留。
            var permanentAlarmLatched = _cfg.Test.GetEpbRecord(channel).PermanentAlarmLatched;
            var normalizedState = NormalizeRuntimeStateForEnabled(
                IsChannelEnabled(channel),
                state,
                permanentAlarmLatched);
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
                state == ChannelRuntimeState.WarningRunning ||
                state == ChannelRuntimeState.WaitingForSlotBarrier)
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
                    recoveryOwnerKind = RecoveryOwnerKind.DaqRecovery;
                    recoveryTargetPhase = IsFormalPhaseCommitted
                        ? RecoveryTargetPhase.Formal
                        : RecoveryTargetPhase.Startup;
                    recoveryOwnerId = recovery.CorrelationId;
                    recoveryOwnerGeneration = Interlocked.Read(ref _runEpoch);
                }
            }
            if ((state == ChannelRuntimeState.Running || state == ChannelRuntimeState.WarningRunning) &&
                _businessRejoinPending.TryGetValue(channel, out var pendingBusiness) &&
                pendingBusiness.RunId == _activeBatchId && pendingBusiness.RunEpoch == Interlocked.Read(ref _runEpoch))
            {
                state = ChannelRuntimeState.Starting;
                reasonCode = "AwaitingVerifiedRecoveryCycle";
                reasonText = "执行体已建立，等待首圈实际动作和有效数据提交。";
            }
            var previous = _channelRuntimeStateStore.Get(channel);
            var manualPauseOwned = _channelPausedUtc.ContainsKey(channel) ||
                                   CurrentBatchPauseState == BatchPauseState.PausePending ||
                                   CurrentBatchPauseState == BatchPauseState.Paused ||
                                   CurrentBatchPauseState == BatchPauseState.PauseHolding;
            if (previous != null &&
                ShouldRejectRecoveryPauseOverride(
                    previous.State,
                    state,
                    manualPauseOwned))
            {
                _log?.Warn(
                    $"ChannelRuntimeTransitionRejected Channel={channel} " +
                    $"Previous={previous.State} Requested={state} " +
                    $"Code=RecoveryOwnsChannelLifecycle ReasonCode={reasonCode}",
                    "EPB");
                return;
            }
            // Terminal cleanup may legitimately run after CancelAll advanced
            // the global epoch.  Callers which hold a frozen incident
            // identity may supply that epoch explicitly; ordinary lifecycle
            // publications continue to use the current run epoch.
            var runEpoch = runEpochOverride > 0
                ? runEpochOverride
                : Interlocked.Read(ref _runEpoch);
            var updateRunId = runIdOverride == Guid.Empty ? _activeBatchId : runIdOverride;
            var pause = CaptureBatchPauseSnapshot();
            var pauseOwned = pause.Owns(updateRunId, runEpoch, channel);
            var firstRecovering = state == ChannelRuntimeState.Recovering &&
                                  (previous == null ||
                                   previous.State != ChannelRuntimeState.Recovering);
            if (state == ChannelRuntimeState.Recovering)
            {
                // 同一恢复事务中的迟到状态更新允许省略所有权，但只能继承上一条结构化所有权。
                if (!firstRecovering &&
                    recoveryOwnerKind == RecoveryOwnerKind.None &&
                    RecoveryOwnershipPolicy.HasExplicitOwner(previous))
                {
                    recoveryOwnerKind = previous.RecoveryOwnerKind;
                    recoveryTargetPhase = previous.RecoveryTargetPhase;
                    recoveryOwnerId = previous.RecoveryOwnerId;
                    recoveryOwnerGeneration = previous.RecoveryOwnerGeneration;
                }
                else if (!firstRecovering &&
                         recoveryOwnerKind != RecoveryOwnerKind.None &&
                         !RecoveryOwnershipPolicy.MatchesExplicitOwner(
                             previous,
                             recoveryOwnerKind,
                             recoveryTargetPhase,
                             recoveryOwnerId,
                             recoveryOwnerGeneration))
                {
                    _log?.Warn(
                        $"ChannelRuntimeTransitionRejected Channel={channel} " +
                        "ReasonCode=RecoveryOwnerIdentityMismatch",
                        "EPB");
                    return;
                }

                if (firstRecovering &&
                    !IsValidFirstRecoveryPublication(
                        updateRunId,
                        runEpoch,
                        recoveryOwnerKind,
                        recoveryTargetPhase,
                        recoveryOwnerId,
                        recoveryOwnerGeneration))
                {
                    // A first Recovering without a complete current owner is
                    // not a recoverable display state.  Safety order is OFF
                    // first, then one terminal StartBlocked publication.
                    try { CommandEpbOffHighPriority(channel, "RecoveryOwnerInvalid"); }
                    catch { }
                    state = ChannelRuntimeState.StartBlocked;
                    var requestedReason = reasonCode;
                    reasonCode = "RecoveryOwnerMissingSafeTerminal";
                    reasonText =
                        $"恢复状态缺少结构化owner，已拒绝进入Recovering并保持安全终态。" +
                        $"原始Reason={requestedReason}";
                    affectedChannels = affectedChannels ?? new[] { channel };
                    recoveryOwnerKind = RecoveryOwnerKind.None;
                    recoveryTargetPhase = RecoveryTargetPhase.None;
                    recoveryOwnerId = Guid.Empty;
                    recoveryOwnerGeneration = 0;
                    _log?.Error(
                        $"RecoveryOwnerMissingSafeTerminal Channel={channel} " +
                        $"RunEpoch={runEpoch}",
                        "EPB");
                }
            }
            else
            {
                recoveryOwnerKind = RecoveryOwnerKind.None;
                recoveryTargetPhase = RecoveryTargetPhase.None;
                recoveryOwnerId = Guid.Empty;
                recoveryOwnerGeneration = 0;
            }
            var sourceStateRevision = previous?.Revision ?? 0;
            if (state == ChannelRuntimeState.Recovering &&
                previous?.State == ChannelRuntimeState.Recovering &&
                previous.RunId == updateRunId &&
                previous.RunEpoch == runEpoch &&
                previous.RecoveryOwnerKind == recoveryOwnerKind &&
                previous.RecoveryOwnerId == recoveryOwnerId &&
                previous.RecoveryOwnerGeneration == recoveryOwnerGeneration &&
                previous.RecoveryTargetPhase == recoveryTargetPhase &&
                previous.SourceStateRevision > 0)
                sourceStateRevision = previous.SourceStateRevision;
            if (state == ChannelRuntimeState.Recovering &&
                firstRecovering &&
                IsValidFirstRecoveryPublication(
                    updateRunId,
                    runEpoch,
                    recoveryOwnerKind,
                    recoveryTargetPhase,
                    recoveryOwnerId,
                    recoveryOwnerGeneration))
            {
                RecoveryIncidentHandle existingRecoveryContract;
                lock (_recoveryContractGate)
                {
                    existingRecoveryContract = FindActiveRecoveryContractLocked(
                        channel,
                        updateRunId,
                        runEpoch,
                        RecoveryOwnerKind.None,
                        RecoveryTargetPhase.None,
                        Guid.Empty);
                }
                var ownerConflict = existingRecoveryContract != null &&
                                     (existingRecoveryContract.Contract.OwnerKind != recoveryOwnerKind ||
                                      existingRecoveryContract.Contract.TargetPhase != recoveryTargetPhase ||
                                      existingRecoveryContract.Contract.OwnerId != recoveryOwnerId);
                if (existingRecoveryContract == null || ownerConflict)
                {
                    var failCode = ownerConflict
                        ? "RecoveryOwnerConflictSafeTerminal"
                        : "RecoveryContractMissingSafeTerminal";
                    foreach (var affected in (affectedChannels ?? new[] { channel }).Distinct())
                    {
                        try { CommandEpbOffHighPriority(affected, failCode); }
                        catch { }
                    }
                    state = ChannelRuntimeState.StartBlocked;
                    reasonCode = failCode;
                    reasonText = ownerConflict
                        ? "当前通道已有其它恢复事务占有；已拒绝第二个Recovering所有者并保持安全终态。"
                        : "首发Recovering缺少统一恢复合同与真实执行体，已先断电并保持安全终态。";
                    recoveryOwnerKind = RecoveryOwnerKind.None;
                    recoveryTargetPhase = RecoveryTargetPhase.None;
                    recoveryOwnerId = Guid.Empty;
                    recoveryOwnerGeneration = 0;
                    allowTerminalReset = true;
                    allowSystemFaultReset = true;
                    _log?.Error(
                        $"{failCode} Channel={channel}; RunEpoch={runEpoch}",
                        "EPB");
                }
                else if (correlationId == Guid.Empty)
                {
                    correlationId = existingRecoveryContract.Contract.IncidentId;
                }
            }
            ChannelRuntimeStateChangedEvent update;
            // The state store operation is part of the same contract gate used by
            // TryBeginRecoveryIncident and CompleteRecoveryIncident.  Observers
            // therefore cannot observe a new owner without its Recovering state,
            // or a terminal state after its owner has already been removed.
            lock (_recoveryContractGate)
            {
                update = _channelRuntimeStateStore.Publish(
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
                            RunId = updateRunId,
                            RunEpoch = runEpoch,
                            PauseGeneration = pauseOwned ? pause.Generation : 0,
                            PauseCommandId = pauseOwned ? pause.CommandId : Guid.Empty,
                            Enabled = IsChannelEnabled(channel),
                            FormalPhaseCommitted = IsFormalPhaseCommitted,
                            TimerActive = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel),
                            RunnerActive = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel),
                            Energized = IsChannelEnergized(channel),
                            PermanentAlarmLatched = _cfg.Test.GetEpbRecord(channel).PermanentAlarmLatched,
                            PermanentAlarmCode = _cfg.Test.GetEpbRecord(channel).PermanentAlarmCode,
                            PermanentAlarmUtc = _cfg.Test.GetEpbRecord(channel).PermanentAlarmUtc,
                            ConsecutivePeriodOverrunCount = _periodOverrunStreaks.TryGetValue(
                                channel,
                                out var overrunStreak)
                                ? overrunStreak
                                : 0,
                            RecoveryOwnerKind = recoveryOwnerKind,
                            RecoveryOwnerId = recoveryOwnerId,
                            RecoveryOwnerGeneration = recoveryOwnerGeneration,
                            RecoveryTargetPhase = recoveryTargetPhase,
                            SourceStateRevision = sourceStateRevision
                        },
                        allowTerminalReset,
                        allowSystemFaultReset);
                if (update.State == ChannelRuntimeState.Recovering)
                    _recoveryTaskRegistry.ReportProgressForChannel(
                        update.RunEpoch,
                        update.Channel,
                        update.ReasonCode);
                PublishRecoveryAggregateOwnershipSourceLocked();
                // Refresh the logical and operational sources at the same
                // producer boundary.  The resulting aggregate is the only
                // heartbeat truth; capture never reconstructs it from live
                // dictionaries.
                CaptureLogicalQuiescenceSnapshotLocked();
            }
            TrackRecoveringRuntimeTransition(previous, update);
            if (previous == null ||
                previous.State != update.State ||
                !string.Equals(previous.ReasonCode, update.ReasonCode, StringComparison.Ordinal) ||
                previous.RunId != update.RunId ||
                previous.CorrelationId != update.CorrelationId)
                LogFieldRuntimeStateMetric(update);
            _adaptiveLifecyclePort.PublishRuntimeState(update);
            if (state == ChannelRuntimeState.Starting ||
                state == ChannelRuntimeState.Completed ||
                ChannelRuntimeStateStore.IsLatchedStop(state))
                ClearChannelWarningOverlay(channel, update.CorrelationId);
        }

        /// <summary>
        /// Replace the aggregate ownership source while the same contract gate
        /// protects the channel state, incident contracts and registry lease
        /// relation.  The aggregate store performs its own short copy/commit
        /// lock; no live Task or dictionary escapes to the watchdog.
        /// </summary>
        private void PublishRecoveryAggregateOwnershipSourceLocked()
        {
            var contracts = _activeRecoveryContracts.Values
                .Where(incident => incident != null &&
                                   incident.TerminalPublished == 0 &&
                                   incident.Contract != null)
                .Select(incident => incident.Contract);
            var leases = _recoveryTaskRegistry.CaptureSnapshot()
                .Select(lease => new WatchdogRecoveryLeaseSnapshot(
                    lease.Id,
                    lease.Operation,
                    lease.RunEpoch,
                    lease.Channels,
                    lease.IsBound,
                    lease.IsTerminal));
            _recoveryAggregateStore.PublishOwnershipSource(
                _channelRuntimeStateStore.Snapshot(),
                contracts,
                leases);
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
        private readonly int _daqRecoveryStableWindowMs;
        private Func<bool> _recoveryInfrastructureHealthProvider = () => true;
        private readonly double _epbPowerSupplyOffProofThresholdA;
        private readonly int _epbPowerSupplyOffProofMaxAgeMs;
        private readonly DaqRecoveryAttemptWindow _daqClockRecoveryAttempts;
        private readonly IDaqHardwareProbe _daqHardwareProbe;
        private readonly ConcurrentDictionary<string, DaqAutoRecoveryContext> _daqAutoRecovery =
            new(StringComparer.OrdinalIgnoreCase);
        // A committed/terminal incident remains observable for a short,
        // bounded window after its live owner is removed.  Without this
        // evidence window the heartbeat could jump directly from Rejoining to
        // no recovery at all, and a watchdog could misclassify a healthy commit
        // as a lost owner.
        private const int DaqRecoveryTerminalSnapshotRetentionMs = 2_000;
        private readonly ConcurrentDictionary<string, DaqRecoveryTerminalSnapshot>
            _daqRecoveryTerminalSnapshots =
                new(StringComparer.OrdinalIgnoreCase);
        // Keep a separate committed evidence record.  A terminal record alone
        // cannot prove that the watchdog observed the successful rejoin before
        // the owner was removed; both records are retained independently.
        private readonly ConcurrentDictionary<string, DaqRecoveryTerminalSnapshot>
            _daqRecoveryCommittedSnapshots =
                new(StringComparer.OrdinalIgnoreCase);
        // 同一扫描的双DAQ故障共享批次 CorrelationId，但每台设备仍需各自完成终态。
        // 因此终态去重身份必须包含 Device；仅按 Guid 会让先完成的 Dev1 错误吞掉
        // Dev2 的升级/恢复。
        private readonly ConcurrentDictionary<string, DateTime> _daqRecoveryTerminalCorrelations = new(
            StringComparer.OrdinalIgnoreCase);
        // A short post-terminal grace keeps a 1.2s DAQ transient on the same
        // incident identity.  It is generation-scoped and expires quickly, so
        // a genuinely new DAQ generation still creates a new incident.
        private sealed class DaqIncidentGrace
        {
            internal Guid CorrelationId;
            internal long Generation;
            internal long ExpiresMonotonicTicks;
        }

        private sealed class DaqRecoveryTerminalSnapshot
        {
            internal string Device;
            internal Guid RunId;
            internal Guid CorrelationId;
            internal long RunEpoch;
            internal DaqRecoveryTerminal Terminal;
            internal DaqRecoveryPhase ProgressStage;
            internal long ProgressVersion;
            internal DateTime StartedUtc;
            internal DateTime StageStartedUtc;
            internal DateTime HardDeadlineUtc;
            internal DateTime RetainedUntilUtc;
            internal int[] AffectedChannels;
            internal bool TerminalClosureBlocked;
            internal string TerminalFailureReason;
        }
        private readonly ConcurrentDictionary<string, DaqIncidentGrace> _daqIncidentGrace =
            new(StringComparer.OrdinalIgnoreCase);
        // One SafeIdle decision per DAQ incident.  This circuit is deliberately
        // separate from the software batch-recycle circuit: a permanent DAQ
        // disconnect must never emit SystemFaultRaised or request a process
        // restart, and repeated callbacks must not publish duplicate terminals.
        private readonly ConcurrentDictionary<string, byte> _daqSafeIdleCircuitKeys =
            new(StringComparer.OrdinalIgnoreCase);
        // Batch-scoped correlation IDs are the only safe signal that both DAQ
        // devices are expected in one Incident root.  The first device may
        // publish before the second recovery context is created, so retain the
        // marker until terminal manifest publication observes both devices.
        private readonly ConcurrentDictionary<string, byte> _sharedDaqIncidentCorrelations =
            new(StringComparer.OrdinalIgnoreCase);
        // Freeze the DAQ devices selected for the active run before any fault
        // callback can arrive.  This lets a direct Dev1 safety callback carry
        // Dev2 in ExpectedDevices even when the liveness watchdog has not yet
        // created Dev2's recovery context.
        private readonly ConcurrentDictionary<string, string[]> _daqIncidentExpectedDevicesByRun =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _daqRecoveryCommitGate = new();
        private long _runEpoch;
        private long _recoveryEpoch;
        internal event Action<StopContext> RunAuthorizationRevocationBarrier;
        private readonly System.Threading.Timer _daqLivenessWatchdog;
        private readonly int _daqLivenessWatchdogIntervalMs;
        private readonly double _daqLivenessWarnThresholdMs;
        private readonly double _daqLivenessSuspectThresholdMs;
        private readonly double _daqLivenessTripThresholdMs;
        private readonly ConcurrentDictionary<string, DaqLivenessDeviceState> _daqLivenessStates =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _daqLivenessLatchedGeneration =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _daqLivenessObservedGapEvents =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DaqLivenessLogTransitionGate _daqLivenessLogTransitions =
            new DaqLivenessLogTransitionGate();
        private int _daqLivenessWatchdogBusy;
        // 事故圈作废标记必须带完整运行身份；仅用(channel,cycle)会让旧Run的迟到
        // Finalizer在圈号复用后误伤新Run正式圈。
        private readonly ConcurrentDictionary<string, byte> _daqClockAbortedCycles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _daqRecoveredGapAbortedCycles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _daqFreshnessSafetyCutoffGeneration =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, long> _daqFreshnessEscalationWatchGeneration =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, long> _daqMechanicalRequalificationGeneration = new();
        private readonly object _daqMechanicalRequalificationGate = new();
        private readonly object _stopSafetyGate = new();
        private Task<StopSafetyResult> _stopSafetyTask;
        // A closing request may arrive while the operator stop is still in
        // flight.  Keep one continuation per process so final-exit cleanup
        // cannot create parallel stop cores.
        private Task<StopSafetyResult> _stopSafetyFinalExitTask;
        private StopSource _stopSafetyTaskSource = StopSource.UnknownLegacy;
        private StopSafetyResult _lastStopSafetyResult;
        private readonly ConcurrentDictionary<long, PowerShutdownDisposition>
            _stopPowerDispositionByGeneration = new();
        private readonly object _hardwareReleaseGate = new object();
        private int _hardwareReleaseState;
        private int _hardwareReleaseExecutionCount;

        private sealed class DaqAutoRecoveryContext
        {
            public string Device;
            public Guid RunId;
            public Guid CorrelationId;
            public DateTime StartedUtc;
            public DateTime CutoffUtc;
            public BatchPauseState AdmissionBatchPauseState;
            public long AdmissionBatchPauseGeneration;
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
            // A terminal publication retry is a supervised continuation of
            // this incident, never a second recovery owner/worker.
            public int TerminalRetryScheduled;
            // Set only after bounded terminal receipt retries are exhausted.
            // The fail-closed snapshot is retained as an explicit, observable
            // safety outcome; it is never confused with a successful recovery.
            public int TerminalClosureBlocked;
            public string TerminalFailureReason;
            // Frozen identity for the safety/terminal transaction.  This is
            // deliberately independent of _runEpoch: StopAll/CancelAll may
            // revoke the global epoch while this exact owner still has to
            // finish its terminal receipt.
            public Guid RecoveryTransactionId;
            // The registry lease is the heartbeat-visible owner/task
            // identity for this same context.  It is completed only after the
            // authoritative terminal state has been published.
            public RecoveryTaskRegistry.RecoveryTaskLease RegistryLease;
            // The production terminal supervisor is shared by normal
            // recovery and SafeIdle/cancelled closure.  It retries the same
            // transaction/owner and never creates a second worker.
            public readonly DaqRecoveryTerminalSupervisor TerminalSupervisor =
                new DaqRecoveryTerminalSupervisor();
            // Controller-owned monotonic progress.  The UI/Watchdog must consume
            // these values and never derive a new version from a string signature.
            public long ProgressVersion;
            public DaqRecoveryPhase ProgressStage;
            public DateTime StageStartedUtc;
            public DateTime HardDeadlineUtc;
            public readonly object ProgressGate = new object();
            public readonly RecoveryFailureBackoffState FailureBackoff =
                new RecoveryFailureBackoffState();
            public readonly object ValidationFailureLogGate = new object();
            public long LastValidationFailureLogTicks;
            public string LastValidationFailureSignature;
            public long RunEpoch;
            public DaqRecoveryParticipantToken BatchBarrierParticipant;
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
            public string ValidationDetail;
            public HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[] Ownerships;
            public CancellationTokenRegistration[] OwnershipCancellationRegistrations;
            public int OwnershipReleased;
            public readonly DaqRecoveryPhaseGate Phase = new DaqRecoveryPhaseGate();
            // Safety-boundary facts (OFF admission, physical completion,
            // incident SafeIdle and stable checkpoint) are owned by one
            // transaction.  The long-running DAQ rebuild remains in this
            // context, but it cannot publish a safety stage by inference.
            public DaqRecoveryTransaction Transaction;
            public Dictionary<int, long> CutoffParticipantVersions;
            public Dictionary<int, int> CutoffCycles;
            public long CutoffPersistenceBoundary;
            public DaqCutoffSnapshot CutoffSnapshot;
            public Task[] PowerDisableTasks = Array.Empty<Task>();
            // 保留电源组到关闭任务的映射；仅保存 Task[] 会丢失“哪一组”卡在
            // DisableCore/Gate 的证据，而 runtime telemetry 可能尚未反映 pending。
            public Dictionary<int, Task<(bool ok, string error)>> PowerDisableTasksByGroup =
                new Dictionary<int, Task<(bool ok, string error)>>();
            // Runtime telemetry may lag an accepted OFF command.  Keep the
            // exact recovery-epoch receipt so pending OFF is not misclassified
            // as a hard failure from a stale status bitmap.
            public readonly ConcurrentDictionary<int, DaqRecoveryPowerOffReceipt>
                PowerOffReceipts = new ConcurrentDictionary<int, DaqRecoveryPowerOffReceipt>();
            public long PowerDisableStartedTicks;
            public long PowerDisableStartedUtcTicks;
            public int PowerDisableDeadlineLogged;
            // A pending OFF receipt is recovery progress, not a failed DAQ
            // attempt.  It therefore has its own diagnostic/retry markers
            // and must not consume the software-maintenance retry budget.
            public int PowerOffPendingPublished;
            public int PowerOffPendingRetryScheduled;
            public int BoundaryContradiction;
            public string BoundaryContradictionReason;
            public int CutoffCyclesFinalized;
            public readonly object CutoffCyclesFinalizationGate = new object();
            public TaskCompletionSource<bool> CutoffCyclesFinalizationCompletion;
        }

        private sealed class DaqRecoveryPowerOffReceipt
        {
            public int GroupId;
            public long RecoveryEpoch;
            public long PowerOperationEpoch;
            public DateTime SubmittedUtc;
            public DateTime TaskCompletedUtc;
            public DateTime StateObservedUtc;
            public DateTime LastTelemetryUtc;
            public bool TaskCompleted;
            public bool OutputConfirmedOff;
            public bool PowerOffPending;
            public string Failure = string.Empty;
            public SafetyOffReceipt Evidence;
        }

        internal sealed class RecoveryIncidentHandle
        {
            private readonly EpbManager _manager;
            private readonly RecoveryIncidentCoordinator.Incident _inner;

            internal RecoveryIncidentHandle(
                EpbManager manager,
                RecoveryIncidentCoordinator.Incident inner)
            {
                _manager = manager ?? throw new ArgumentNullException(nameof(manager));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            internal RecoveryContractSnapshot Contract => _inner.Contract;
            internal RecoveryTaskRegistry.RecoveryTaskLease TaskLease => _inner.TaskLease;
            internal Task WorkerTask => _inner.WorkerTask;
            internal int TerminalPublished => _inner.TerminalPublished;
            internal int TerminalPublishing => _inner.TerminalPublishing;

            internal bool Start() => _manager.StartRecoveryIncident(_inner);

            internal bool CompleteAfterTerminal(
                Action<RecoveryContractSnapshot> publishTerminal)
            {
                return _manager.CompleteRecoveryIncident(this, publishTerminal);
            }

            internal bool CompleteOnCoordinator(
                Action<RecoveryContractSnapshot> publishTerminal) =>
                _inner.CompleteAfterTerminal(publishTerminal);
        }

        /// <summary>
        /// 一次 DAQ 恢复只能生成一个不可扩大的截止快照。字典在构造时复制，
        /// 后续 Finalizer、StopAll 和重试均只能复用该 FrozenBoundary。
        /// </summary>
        private sealed class DaqCutoffSnapshot
        {
            public DaqCutoffSnapshot(
                string device,
                DateTime cutoffUtc,
                long frozenBoundary,
                IReadOnlyDictionary<int, int> cycles)
            {
                Device = device ?? string.Empty;
                CutoffUtc = cutoffUtc;
                FrozenBoundary = Math.Max(0, frozenBoundary);
                Cycles = (cycles ?? new Dictionary<int, int>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            public string Device { get; }
            public DateTime CutoffUtc { get; }
            public long FrozenBoundary { get; }
            public IReadOnlyDictionary<int, int> Cycles { get; }
        }

        internal static ChannelRuntimeState NormalizeRuntimeStateForEnabled(
            bool enabled,
            ChannelRuntimeState requested,
            bool permanentAlarmLatched = false)
        {
            if (permanentAlarmLatched) return ChannelRuntimeState.AlarmStopped;
            return enabled ? requested : ChannelRuntimeState.NotEnabled;
        }

        internal static string FormatDaqRecoveryCycleKey(
            Guid runId,
            long runEpoch,
            int channel,
            int cycleNumber)
            => $"{runId:N}:{runEpoch}:{channel}:{cycleNumber}";

        private static string DaqAbortedCycleKey(Guid runId, long runEpoch, int channel, int cycleNumber)
            => FormatDaqRecoveryCycleKey(runId, runEpoch, channel, cycleNumber);

        private void MarkDaqClockCycleAborted(
            Guid runId,
            long runEpoch,
            int channel,
            int cycleNumber)
        {
            if (runId == Guid.Empty || runEpoch <= 0 || channel < 1 || cycleNumber <= 0) return;
            _daqClockAbortedCycles[DaqAbortedCycleKey(runId, runEpoch, channel, cycleNumber)] = 0;
        }

        private static string FormatDaqRecoveryCycleIdentity(Guid runId, long runEpoch, int channel, int cycle)
            => $"RunId={runId:N};RunEpoch={runEpoch};EPB={channel};Cycle={cycle}";

        private void MarkDaqRecoveryCyclesAborted(DaqAutoRecoveryContext context)
        {
            if (context == null) return;
            foreach (var pair in (context.CutoffCycles ?? new Dictionary<int, int>())
                         .Where(item => item.Key >= 1 && item.Value > 0))
            {
                MarkDaqClockCycleAborted(context.RunId, context.RunEpoch, pair.Key, pair.Value);
                var identity = FormatDaqRecoveryCycleIdentity(
                    context.RunId,
                    context.RunEpoch,
                    pair.Key,
                    pair.Value);
                _log.Info(
                    $"DaqRecoveryCycleAbortLatched {identity} " +
                    $"Device={context.Device} CorrelationId={context.CorrelationId:N}",
                    "落盘");
            }
        }

        private bool TryConsumeDaqClockCycleAbort(
            Guid runId,
            long runEpoch,
            int channel,
            int cycleNumber)
        {
            if (runId == Guid.Empty || runEpoch <= 0 || channel < 1 || cycleNumber <= 0)
                return false;
            return _daqClockAbortedCycles.TryRemove(
                DaqAbortedCycleKey(runId, runEpoch, channel, cycleNumber), out _);
        }

        private bool IsDaqClockCycleAborted(
            Guid runId,
            long runEpoch,
            int channel,
            int cycleNumber)
        {
            if (runId == Guid.Empty || runEpoch <= 0 || channel < 1 || cycleNumber <= 0)
                return false;
            return _daqClockAbortedCycles.ContainsKey(
                DaqAbortedCycleKey(runId, runEpoch, channel, cycleNumber));
        }

        private bool DiscardCurrentCycleForSoftwareRecovery(
            int channel,
            DateTime cutoffUtc,
            string reason,
            int? expectedCycleNumber = null,
            bool durableBoundaryAlreadyConfirmed = false,
            string terminalStatus = "AbortedBySoftwareRecovery")
        {
            if (_cycleAttempts.TryGetCurrent(channel, out var context))
            {
                if (expectedCycleNumber.HasValue && context.Cycle != expectedCycleNumber.Value)
                {
                    _log.Warn(
                        $"EPB[{channel}] 拒绝作废非当前统一圈尝试。" +
                        $"ExpectedCycle={expectedCycleNumber} ActualCycle={context.Cycle} " +
                        $"Attempt={context.AttemptId} Reason={reason}",
                        "落盘");
                    return false;
                }

                context.CancelAttempt();
                try
                {
                    var committed = context.AbortRecorderOnce(
                        () =>
                        {
                            SealCyclePersistenceWindow(
                                Recorder,
                                channel,
                                context.Cycle,
                                cutoffUtc);
                            if (durableBoundaryAlreadyConfirmed)
                                AbortCycleAtConfirmedDurableBoundary(
                                    Recorder,
                                    channel,
                                    context.Cycle,
                                    cutoffUtc,
                                    terminalStatus);
                            else
                                AbortCycleAfterPersistence(
                                    Recorder,
                                    channel,
                                    context.Cycle,
                                    cutoffUtc,
                                    terminalStatus);
                            return true;
                        },
                        RemoveCycleAttemptAfterDurableTerminal);
                    if (!committed && !context.IsDurablyCommitted) return false;

                    _formalPersistenceRecoveryPendingCycles.TryRemove(channel, out _);
                    MarkDaqClockCycleAborted(
                        context.RunId,
                        context.RunEpoch,
                        channel,
                        context.Cycle);
                    _log.Warn(
                        $"EPB[{channel}] Cycle={context.Cycle} Attempt={context.AttemptId} " +
                        $"因软件自愈作废；不计正式完成数。Reason={reason}",
                        "落盘");
                    return true;
                }
                catch (Exception ex)
                {
                    // context 与旧字典投影均保持活动；恢复代次稍后可精确重试。
                    _log.Warn(
                        $"EPB[{channel}] 软件自愈作废统一圈尝试失败：" +
                        $"Cycle={context.Cycle} Attempt={context.AttemptId} Error={ex.Message}",
                        "落盘");
                    return false;
                }
            }

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
                SealCyclePersistenceWindow(Recorder, channel, cycleNumber, cutoffUtc);
                if (durableBoundaryAlreadyConfirmed)
                    AbortCycleAtConfirmedDurableBoundary(
                        Recorder,
                        channel,
                        cycleNumber,
                        cutoffUtc,
                        terminalStatus);
                else
                    AbortCycleAfterPersistence(
                        Recorder,
                        channel,
                        cycleNumber,
                        cutoffUtc,
                        terminalStatus);
                _formalPersistenceRecoveryPendingCycles.TryRemove(channel, out _);
                // 正式圈回调稍后收尾时只消费此标记，不得把已作废圈再次封账。
                MarkDaqClockCycleAborted(
                    _activeBatchId,
                    Interlocked.Read(ref _runEpoch),
                    channel,
                    cycleNumber);
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

        private readonly struct CycleDaqBoundary
        {
            public CycleDaqBoundary(string device, long generation, long sequence)
            {
                Device = device ?? string.Empty;
                Generation = Math.Max(0, generation);
                Sequence = Math.Max(0, sequence);
            }

            public string Device { get; }
            public long Generation { get; }
            public long Sequence { get; }
        }

        internal static long SelectAuthoritativeCycleEndSequence(
            long lastAcceptedSequence,
            long lastDiskPublishedSequence,
            long? frozenRecoveryBoundary = null)
        {
            if (lastAcceptedSequence < 0) throw new ArgumentOutOfRangeException(nameof(lastAcceptedSequence));
            if (lastDiskPublishedSequence < 0)
                throw new ArgumentOutOfRangeException(nameof(lastDiskPublishedSequence));
            if (frozenRecoveryBoundary.HasValue && frozenRecoveryBoundary.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(frozenRecoveryBoundary));

            // Published 只表示后台当前已经走到哪里，不能作为圈尾：Accepted 与 Published
            // 之间的批次已经被实时链正式接纳，封口后仍必须等待并归入本圈。
            return frozenRecoveryBoundary ?? lastAcceptedSequence;
        }

        private Dictionary<string, CycleDaqBoundary> CaptureCycleDaqBoundaries(
            IReadOnlyDictionary<int, int> cycles)
        {
            var result = new Dictionary<string, CycleDaqBoundary>(StringComparer.OrdinalIgnoreCase);
            if (cycles == null || cycles.Count == 0) return result;

            foreach (var device in cycles.Keys
                         .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                         .Where(device => !string.IsNullOrWhiteSpace(device))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var generation = _acq.GetCurrentGeneration(device);
                var accepted = _acq.GetLastAcceptedSequence(device);
                var published = _acq.GetLastDiskPublishedSequence(device);
                long? frozenBoundary = null;
                if (_daqAutoRecovery.TryGetValue(device, out var recovery) &&
                    recovery?.CutoffSnapshot != null &&
                    recovery.CutoffSnapshot.Cycles.Any(pair =>
                        cycles.TryGetValue(pair.Key, out var requestedCycle) &&
                        requestedCycle == pair.Value))
                {
                    frozenBoundary = recovery.CutoffSnapshot.FrozenBoundary;
                    generation = recovery.PreviousGeneration;
                }

                // A cycle belongs to the DAQ generation captured before BeginCycle.  If
                // the device has already been replaced, seal the aborted cycle at its
                // begin boundary.  This deliberately records a truncated/aborted attempt
                // instead of trying to attach samples from the new generation.
                var attempts = cycles
                    .Select(pair => TryGetCycleAttempt(pair.Key, pair.Value, out var attempt)
                        ? attempt
                        : null)
                    .Where(attempt => attempt != null &&
                                      string.Equals(
                                          attempt.Device,
                                          device,
                                          StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (attempts.Length > 0)
                {
                    var attemptGeneration = attempts[0].DaqGeneration;
                    if (attempts.All(item => item.DaqGeneration == attemptGeneration) &&
                        attemptGeneration > 0 &&
                        attemptGeneration != generation)
                    {
                        generation = attemptGeneration;
                        frozenBoundary = attempts.Min(item => item.DaqBeginSequence);
                        _log.Warn(
                            $"HydraulicGroupAbortCrossGeneration Device={device} " +
                            $"CycleGeneration={attemptGeneration} CurrentGeneration=" +
                            $"{_acq.GetCurrentGeneration(device)} EndSequence={frozenBoundary} " +
                            $"Channels=[{string.Join(",", attempts.Select(item => item.Channel))}]",
                            "落盘");
                    }
                }

                result[device] = new CycleDaqBoundary(
                    device,
                    generation,
                    SelectAuthoritativeCycleEndSequence(accepted, published, frozenBoundary));
            }
            return result;
        }

        private bool TrySealSoftwareRecoveryCycleWindows(
            IReadOnlyDictionary<int, int> cycles,
            DateTime cutoffUtc,
            string reason,
            Func<bool> canMutate = null,
            IReadOnlyDictionary<string, CycleDaqBoundary> capturedBoundaries = null)
        {
            if (cycles == null || cycles.Count == 0) return true;
            if (!(Recorder is IBatchedEpbCycleRecorder)) return true;
            capturedBoundaries ??= CaptureCycleDaqBoundaries(cycles);
            var succeeded = true;
            foreach (var pair in cycles)
            {
                if (canMutate != null && !canMutate()) return false;
                if (TryGetCycleAttempt(pair.Key, pair.Value, out var context))
                {
                    if (context.BeginState == CycleAttemptBeginState.Registered)
                    {
                        context.CancelAttempt();
                        _log.Warn(
                            $"EPB[{pair.Key}] Recorder.BeginCycle尚未返回，" +
                            $"拒绝提前封闭不存在的圈窗口。Cycle={pair.Value} Reason={reason}",
                            "落盘");
                        return false;
                    }
                    if (context.BeginState == CycleAttemptBeginState.Failed)
                        continue;
                }
                try
                {
                    var device = _acq.GetDeviceForEpbChannel(pair.Key);
                    CycleDaqBoundary? boundary = null;
                    if (!string.IsNullOrWhiteSpace(device) &&
                        capturedBoundaries.TryGetValue(device, out var captured))
                        boundary = captured;
                    SealCyclePersistenceWindow(
                        Recorder,
                        pair.Key,
                        pair.Value,
                        cutoffUtc,
                        boundary);
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

        private void SealCyclePersistenceWindow(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime cutoffUtc,
            CycleDaqBoundary? capturedBoundary = null)
        {
            if (recorder is ISequencedEpbCycleRecorder sequenced)
            {
                var device = _acq.GetDeviceForEpbChannel(channel);
                if (!string.IsNullOrWhiteSpace(device))
                {
                    var boundary = capturedBoundary ?? new CycleDaqBoundary(
                        device,
                        _acq.GetCurrentGeneration(device),
                        SelectAuthoritativeCycleEndSequence(
                            _acq.GetLastAcceptedSequence(device),
                            _acq.GetLastDiskPublishedSequence(device)));
                    sequenced.SealCycleWindowAtDaqBoundary(
                        channel,
                        cycleNumber,
                        cutoffUtc,
                        boundary.Device,
                        boundary.Generation,
                        boundary.Sequence);
                    return;
                }
            }
            if (recorder is IBatchedEpbCycleRecorder batched)
                batched.SealCycleWindow(channel, cycleNumber, cutoffUtc);
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
            CancellationToken token,
            Func<bool> canMutate = null,
            string terminalStatus = "AbortedBySoftwareRecovery")
        {
            if (cycles == null || cycles.Count == 0) return true;
            if (!await TryWaitForCycleDurableCutoffAsync(
                    cycles,
                    cutoffUtc,
                    reason,
                    timeoutMs,
                    token,
                    canMutate)
                .ConfigureAwait(false))
                return false;

            foreach (var pair in cycles)
            {
                if (canMutate != null && !canMutate()) return false;
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
                if (canMutate != null && !canMutate()) return false;
                if (!DiscardCurrentCycleForSoftwareRecovery(
                        pair.Key,
                        cutoffUtc,
                        reason,
                        pair.Value,
                        terminalStatus: terminalStatus))
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
            CancellationToken token,
            Func<bool> canMutate = null)
        {
            if (cycles == null || cycles.Count == 0) return true;
            var devices = cycles.Keys
                .Select(channel => _acq.GetDeviceForEpbChannel(channel))
                .Where(device => !string.IsNullOrWhiteSpace(device))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // 同一次冻结快照同时驱动圈封口、Raw 排空和 SQLite 耐久等待。Published
            // 只是后台进度，不能截断已经被实时链 Accepted、但尚未发布到落盘链的尾批。
            var boundaries = CaptureCycleDaqBoundaries(cycles);
            if (!TrySealSoftwareRecoveryCycleWindows(
                    cycles,
                    cutoffUtc,
                    reason,
                    canMutate,
                    boundaries))
                return false;
            foreach (var device in devices)
            {
                if (!boundaries.TryGetValue(device, out var captured)) return false;
                if (_daqAutoRecovery.TryGetValue(device, out var recovery) &&
                    recovery?.CutoffSnapshot != null &&
                    captured.Sequence == recovery.CutoffSnapshot.FrozenBoundary)
                    _log.Info(
                        $"通用圈 Finalizer 复用 DAQ 冻结边界 Device={device} " +
                        $"FrozenBoundary={captured.Sequence} Reason={reason}",
                        "落盘");
            }

            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            var rawTimeoutMs = (int)Math.Max(
                1,
                (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
            var rawDrain = await _acq.DrainBackgroundPipelinesToBoundariesDetailedAsync(
                    boundaries.TryGetValue("Dev1", out var dev1Boundary)
                        ? dev1Boundary.Sequence
                        : 0,
                    boundaries.TryGetValue("Dev2", out var dev2Boundary)
                        ? dev2Boundary.Sequence
                        : 0,
                    rawTimeoutMs,
                    token)
                .ConfigureAwait(false);
            if (!rawDrain.Completed)
            {
                _log.Warn(
                    $"软件恢复截止 Raw 固定边界排空超时；保持圈事务开放且禁止重入。" +
                    $"Dev1Boundary={dev1Boundary.Sequence} Dev2Boundary={dev2Boundary.Sequence} " +
                    $"Pending={rawDrain.PendingPredicate} Channels=[{string.Join(",", cycles.Keys)}] Reason={reason}",
                    "落盘");
                return false;
            }

            if (canMutate != null && !canMutate()) return false;

            foreach (var device in devices)
            {
                if (canMutate != null && !canMutate()) return false;
                var boundary = boundaries[device].Sequence;
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

            if (canMutate != null && !canMutate()) return false;
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
                    if (TryGetCycleAttempt(pair.Key, pair.Value, out var context))
                    {
                        context.CancelAttempt();
                        var committed = context.AbortRecorderOnce(
                            () =>
                            {
                                var finalN = recorder.GetCurrentCycleSampleCount(pair.Key);
                                recorder.AbortCycle(pair.Key, pair.Value, finalN, cutoffUtc, status);
                                return true;
                            },
                            RemoveCycleAttemptAfterDurableTerminal);
                        if (!committed && !context.IsDurablyCommitted)
                        {
                            allFinalized = false;
                            continue;
                        }
                        _formalPersistenceRecoveryPendingCycles.TryRemove(pair.Key, out _);
                        MarkDaqClockCycleAborted(
                            _activeBatchId,
                            Interlocked.Read(ref _runEpoch),
                            pair.Key,
                            pair.Value);
                        QueuePendingWarningSnapshotsForCycle(pair.Key, pair.Value);
                        continue;
                    }

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
                    MarkDaqClockCycleAborted(
                        _activeBatchId,
                        Interlocked.Read(ref _runEpoch),
                        pair.Key,
                        pair.Value);
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
            RegisterFormalParticipantLease(channel);
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
            _formalParticipantLeases.TryGetValue(channel, out var formalLease);
            lock (GetHydraulicParticipantGate(channel))
            {
                _hydraulicParticipants.TryRemove(channel, out _);
                _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
            }
            RequestFormalParticipantRetirement(
                formalLease,
                "HydraulicParticipantRemoved");
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
            string reason,
            bool logRejected = true)
        {
            FormalBatchParticipantLease formalLease = null;
            lock (GetHydraulicParticipantGate(channel))
            {
                var participantExists = _hydraulicParticipants.ContainsKey(channel);
                _hydraulicParticipantVersions.TryGetValue(channel, out var currentVersion);
                if (!RecoveryEpochGuard.CanApplyParticipantCleanup(
                        participantExists,
                        expectedVersion,
                        currentVersion))
                {
                    if (logRejected)
                        _log?.Warn(
                            $"EPB[{channel}] 拒绝迟到的液压参与状态清理。" +
                            $"ExpectedVersion={expectedVersion} CurrentVersion={currentVersion} " +
                            $"Reason={reason}",
                            "液压协调");
                    return false;
                }

                _hydraulicParticipants.TryRemove(channel, out _);
                _firstEligibleFormalSlotByChannel.TryRemove(channel, out _);
                _formalParticipantLeases.TryGetValue(channel, out formalLease);
                RequestFormalParticipantRetirement(
                    formalLease,
                    reason);
                return true;
            }
        }

        private FormalBatchParticipantLease RegisterFormalParticipantLease(int channel)
        {
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            if (runId == Guid.Empty || runEpoch <= 0)
                throw new InvalidOperationException(
                    $"FormalParticipantRegistrationRejected EPB={channel} Run={runId:N} Epoch={runEpoch}");
            var lease = _formalBatchSlots.RegisterParticipant(runId, runEpoch, channel);
            lease.ExecutionPermit = _channelExecutionFence.Capture(channel);
            _formalParticipantLeases[channel] = lease;
            return lease;
        }

        private FormalBatchParticipantLease CaptureFormalParticipantLease(int channel)
        {
            if (!_formalParticipantLeases.TryGetValue(channel, out var lease) ||
                lease == null || lease.RunId != _activeBatchId ||
                lease.RunEpoch != Interlocked.Read(ref _runEpoch))
                throw new InvalidOperationException(
                    $"FormalParticipantLeaseMissing EPB={channel} Run={_activeBatchId:N} " +
                    $"Epoch={Interlocked.Read(ref _runEpoch)}");
            return lease;
        }

        private FormalBatchParticipantLease[] CaptureFormalParticipantLeases(
            IEnumerable<int> channels)
        {
            return (channels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(channel => channel)
                .Select(CaptureFormalParticipantLease)
                .ToArray();
        }

        private void RequestFormalParticipantRetirement(
            FormalBatchParticipantLease lease,
            string reason)
        {
            if (lease == null) return;
            if (!_formalBatchSlots.RequestRetirement(lease, reason)) return;
            lock (_channelExecutionGates[lease.Channel - 1])
                _channelExecutionFence.RevokeIfCurrent(lease.ExecutionPermit);
            ObserveSafetyTask(
                CompleteFormalParticipantRetirementFenceAsync(
                    lease,
                    reason),
                "FormalParticipantRetirementFence",
                lease.Channel);
        }

        private async Task CompleteFormalParticipantRetirementFenceAsync(
            FormalBatchParticipantLease participantLease,
            string reason)
        {
            if (participantLease == null) return;
            var runId = participantLease.RunId;
            var runEpoch = participantLease.RunEpoch;
            var channel = participantLease.Channel;
            var requestedTimeoutMs = Math.Max(1L, PeriodMs) * 2L + 5000L;
            var timeoutMs = (int)Math.Max(5000L, Math.Min(60000L, requestedTimeoutMs));
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_activeBatchId != runId || Interlocked.Read(ref _runEpoch) != runEpoch)
                    return;
                if (!_formalParticipantLeases.TryGetValue(channel, out var currentLease) ||
                    !participantLease.SameIdentity(currentLease))
                    return;

                var attemptClosed = true;
                if (_cycleAttempts.TryGetCurrent(channel, out var attempt) &&
                    attempt.RunId == runId && attempt.RunEpoch == runEpoch)
                {
                    attemptClosed = attempt.IsDurablyCommitted;
                    if (!attemptClosed)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) break;
                        var completed = await Task.WhenAny(
                                attempt.DurableCompletion,
                                Task.Delay(Math.Min(100, Math.Max(1, (int)remaining.TotalMilliseconds))))
                            .ConfigureAwait(false);
                        attemptClosed = completed == attempt.DurableCompletion &&
                                        attempt.IsDurablyCommitted;
                    }
                }

                var motorOff = !IsChannelEnergized(channel);
                var hydraulicReleased =
                    !_hydraulicLeaseByChannel.TryGetValue(channel, out var lease) ||
                    lease.IsClosed;
                var executionRevoked =
                    !_channelExecutionFence.IsCurrent(participantLease.ExecutionPermit);
                if (attemptClosed && motorOff && hydraulicReleased && executionRevoked)
                {
                    _formalBatchSlots.ConfirmRetirement(
                        participantLease,
                        new FormalBatchParticipantTerminal
                        {
                            Channel = channel,
                            MotorOffConfirmed = true,
                            HydraulicMemberReleased = true,
                            PersistenceBoundaryRequired = false,
                            PersistenceCommitted = true,
                            RetirementBoundaryRequired = true,
                            ExecutionPermitRevoked = true,
                            PermanentlyIsolated = true,
                            Result = reason ?? "ParticipantRetiredAfterSafetyFence",
                            CompletedUtc = DateTime.UtcNow
                        });
                    return;
                }
                await Task.Delay(25).ConfigureAwait(false);
            }

            if (_activeBatchId != runId || Interlocked.Read(ref _runEpoch) != runEpoch ||
                !_formalParticipantLeases.TryGetValue(channel, out var finalParticipant) ||
                !participantLease.SameIdentity(finalParticipant)) return;
            var finalMotorOff = !IsChannelEnergized(channel);
            var finalHydraulicReleased =
                !_hydraulicLeaseByChannel.TryGetValue(channel, out var finalLease) ||
                finalLease.IsClosed;
            var finalPersistenceClosed =
                !_cycleAttempts.TryGetCurrent(channel, out var finalAttempt) ||
                finalAttempt.RunId != runId ||
                finalAttempt.RunEpoch != runEpoch ||
                finalAttempt.IsDurablyCommitted;
            var finalExecutionRevoked =
                !_channelExecutionFence.IsCurrent(participantLease.ExecutionPermit);
            _formalBatchSlots.ConfirmRetirement(
                participantLease,
                new FormalBatchParticipantTerminal
                {
                    Channel = channel,
                    MotorOffConfirmed = finalMotorOff,
                    HydraulicMemberReleased = finalHydraulicReleased,
                    PersistenceBoundaryRequired = true,
                    PersistenceCommitted = finalPersistenceClosed,
                    RetirementBoundaryRequired = true,
                    ExecutionPermitRevoked = finalExecutionRevoked,
                    PermanentlyIsolated = true,
                    Result = "ParticipantRetirementSafetyFenceTimeout",
                    CompletedUtc = DateTime.UtcNow
                });
            if (finalMotorOff && finalHydraulicReleased && finalPersistenceClosed && finalExecutionRevoked)
                return;
            _log?.Error($"ParticipantRetirementBlocked EPB={channel} Run={runId:N}/{runEpoch} " +
                $"Participant={participantLease.ParticipantGeneration} ExecutionRevoked={finalExecutionRevoked} " +
                $"AttemptClosed={finalPersistenceClosed} Reason={reason}", "周期屏障");
            ReportFormalSlotSafetyBoundaryFailure(
                channel,
                -1,
                finalMotorOff,
                finalHydraulicReleased,
                finalPersistenceClosed);
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
                bool PersistAlarmTerminal()
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
                    return true;
                }

                if (TryGetCycleAttempt(channel, cycleNumber, out var context))
                {
                    var committed = hasSnapshotFiles
                        ? context.AlarmRecorderOnce(
                            PersistAlarmTerminal,
                            RemoveCycleAttemptAfterDurableTerminal)
                        : context.AbortRecorderOnce(
                            PersistAlarmTerminal,
                            RemoveCycleAttemptAfterDurableTerminal);
                    return committed || context.IsDurablyCommitted;
                }

                PersistAlarmTerminal();
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
            if (_cycleAttempts.TryGetCurrent(channel, out var attempt))
            {
                runId = attempt.RunId;
                cycleNumber = attempt.Cycle;
                return;
            }
            runId = _activeBatchId;
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
        private void FinalizeChannelAfterNaturalCompletion(int channel, int terminalCycleNumber)
        {
            _log.Info(
                $"EPB[{channel}] 已完成全部目标圈数，开始安全断电、液压释放和最近10圈持久化。",
                "EPB");

            RevokeChannelExecutionPermit(channel, "NaturalCompletion");

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
                ObserveBackgroundTask(
                    EnsureLatestStopSnapshotAsync(
                        channel,
                        terminalCycleNumber,
                        "NaturalCompletion"),
                    "FlushRecentAfterNaturalCompletion",
                    channel);
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
            FlushPersistentLog(true);
        }

        /// <summary>
        /// 为指定通道创建新的“硬停机”取消源；若已存在则先取消并释放旧实例。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <returns>新的取消源实例。</returns>
        private CancellationTokenSource RenewStopCts(int channel) =>
            RenewStopCts(channel, out _);

        private CancellationTokenSource RenewStopCts(
            int channel,
            out CancellationToken token)
        {
            if (_stopCtsByChannel.TryRemove(channel, out var old))
            {
                try { old.Cancel(); } catch { /* ignore */ }
                try { old.Dispose(); } catch { /* ignore */ }
            }

            var cts = new CancellationTokenSource();
            // Capture the value token before another stop/renew owner can
            // dispose the published source.
            token = cts.Token;
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
            IDaqHardwareProbe daqHardwareProbe = null,
            IEnumerable<Guid> protectedLearningRootIds = null)
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
            _adaptiveLifecyclePort = new EpbManagerAdaptiveLifecyclePort(
                alarmRaised: OnRunnerAlarmRaised,
                warningRaised: OnRunnerWarningRaised,
                warningOverlayRaised: OnRunnerWarningOverlayRaised,
                warningEvidenceRaised: OnRunnerWarningEvidenceRaised,
                recoverableFaultRaised: OnRunnerRecoverableFaultRaised,
                decisionObserved: OnRunnerAdaptiveDecisionObserved,
                diagnosticObserved: OnRunnerAdaptiveDiagnosticObserved,
                diagnosticSummaryObserved: OnRunnerAdaptiveDiagnosticSummaryObserved,
                channelPaused: lifecycle => NonCriticalObserver.Invoke(
                    ChannelPaused,
                    lifecycle.Channel,
                    ex => _log?.Warn(
                        $"EPB[{lifecycle.Channel}] 暂停观察者异常已隔离：{ex.Message}",
                        "EPB")),
                runtimeStateChanged: lifecycle => NonCriticalObserver.Invoke(
                    ChannelRuntimeStateChanged,
                    lifecycle.RuntimeState,
                    ex => _log?.Warn(
                        $"EPB[{lifecycle.Channel}] 状态观察者异常已隔离：{ex.Message}",
                        "EPB")),
                systemFault: lifecycle => NonCriticalObserver.Invoke(
                    SystemFaultRaised,
                    lifecycle.ControlFault,
                    ex => _log?.Warn(
                        $"系统故障观察者异常已隔离：{ex.Message}",
                        "EPB")),
                runIdForChannel: channel =>
                    _runIdByChannel.TryGetValue(channel, out var runId)
                        ? runId
                        : _activeBatchId,
                runEpochForChannel: _ => Interlocked.Read(ref _runEpoch));
            var startupStoragePolicy = ProgramStoragePolicy.Load(
                message => _log.Warn(message, "Storage"));
            _historicalStorageEnabled = startupStoragePolicy.HistoricalEnabled;
            _historicalRetainCyclesPerChannel = startupStoragePolicy.HistoricalRetainCyclesPerChannel;
            _historicalMinimumFreeBytes = startupStoragePolicy.HistoricalMinimumFreeBytes;
            _alarmStorageLevel = startupStoragePolicy.Alarm;
            _learningRetentionMode = startupStoragePolicy.LearningRetentionMode;
            _learningSuccessfulRunRetainCount = startupStoragePolicy.LearningSuccessfulRunRetainCount;
            _learningFailedRunRetainCount = startupStoragePolicy.LearningFailedRunRetainCount;
            _log.Info(startupStoragePolicy.ToStartupLogLine(), "Storage");
            try
            {
                var learningRoot = System.IO.Path.Combine(
                    cfg.Test.StoreDir, cfg.Test.TestName, "LearningCycles");
                _dataHousekeeping = new DataHousekeepingService(
                    learningRoot,
                    new DataHousekeepingOptions
                    {
                        // Unlimited must never schedule a startup scan.  Keep
                        // the service object for lifecycle symmetry, but gate
                        // every scan/enqueue/process path through RetentionEnabled.
                        AutoScanOnStart = _learningRetentionMode == LearningRetentionMode.Count,
                        RetentionEnabled = _learningRetentionMode == LearningRetentionMode.Count,
                        IsCurrentPath = path => IsCurrentLearningChainPath(path),
                        ProtectedRootIds = protectedLearningRootIds,
                        BusyStateProvider = GetHousekeepingBusyState,
                        WarningSink = message => _log.Warn(message, "Housekeeping")
                    });
                _log.Info(
                    $"Learning在线保留器已启动：Mode={_learningRetentionMode} " +
                    $"Successful={_learningSuccessfulRunRetainCount} Failed={_learningFailedRunRetainCount}",
                    "Storage");
                if (_learningRetentionMode == LearningRetentionMode.Count)
                    _dataHousekeeping.EnqueueNewManifestChains(
                        _learningSuccessfulRunRetainCount,
                        _learningFailedRunRetainCount);
                var incidentRoot = System.IO.Path.Combine(
                    cfg.Test.StoreDir, cfg.Test.TestName, "IncidentSnapshots");
                var incidentPolicy = IncidentSessionPolicy.FromAppSettings(
                    System.Configuration.ConfigurationManager.AppSettings,
                    message => _log.Warn(message, "Housekeeping"));
                _incidentHousekeeping = new IncidentHousekeepingService(
                    incidentPolicy.Options.CompleteRetentionPerDevice,
                    () => GetHousekeepingBusyState().IsBusy,
                    message => _log.Warn(message, "Housekeeping"));
                // Resume any terminal roots left by a previous process. Unknown,
                // active, legacy or corrupt roots are skipped by the planner.
                _incidentHousekeeping.Enqueue(incidentRoot);
            }
            catch (Exception ex)
            {
                _log.Warn($"Learning在线保留器初始化失败，保持数据不清理：{ex.Message}", "Storage");
            }
            _taskSupervisor = new TaskSupervisor(_log);
            _recoveryIncidentCoordinator = CreateRecoveryIncidentCoordinator();
            _acq = acq;
            _dev1ChannelMask = BuildDaqChannelMask("Dev1");
            _dev2ChannelMask = BuildDaqChannelMask("Dev2");
            _acq.SetControlActivityProvider(IsDaqDeviceControlActive);
            _requirePowerSupply = requirePowerSupply || ReadBooleanAppSetting("PowerSupplyIntegrationRequired", false);
            if (powerSupply == null && _requirePowerSupply)
            {
                var powerConfigPath = RuntimeConfigPaths.GetPath("PowerSupplyConfig.xml");
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

            // 配置加载层会归一化；这里再次防御直接构造 GlobalConfig 的调用方。
            // 使用索引赋值保证即便外部并发替换了列表，也不会因重复键阻断整机初始化。
            var initialStartPlan = cfg.Test.CreateEpbStartPlan(cfg.Test.TestTarget, 12);
            foreach (var epbRecord in cfg.Test.EnsureEpbRecords(12))
            {
                _mechanicalCycleBaseline[epbRecord.Id] = epbRecord.EffectiveMechanicalCycleCount;
                EpbTestCycle[epbRecord.Id] = initialStartPlan[epbRecord.Id];
                _periodOverrunStreaks[epbRecord.Id] = Math.Max(
                    0,
                    epbRecord.ConsecutivePeriodOverrunCount);
                if (epbRecord.PermanentAlarmLatched)
                {
                    _nonRecoverableChannelFaultLatch[epbRecord.Id] = 0;
                    _nonRecoverableChannelFaultReasons[epbRecord.Id] =
                        string.IsNullOrWhiteSpace(epbRecord.PermanentAlarmReason)
                            ? epbRecord.PermanentAlarmCode
                            : epbRecord.PermanentAlarmReason;
                }
                if (epbRecord.OperatorFullRelearningRequired)
                {
                    _operatorFullRelearningRequired[epbRecord.Id] =
                        string.IsNullOrWhiteSpace(epbRecord.OperatorFullRelearningReason)
                            ? "ForwardUnderTargetHighLoadStall"
                            : epbRecord.OperatorFullRelearningReason;
                }
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
            _daqRecoveryStableWindowMs = ReadIntAppSetting(
                "DaqRecoveryStableWindowMs", 5000, 1000, 30000);
            _epbPowerSupplyOffProofThresholdA = ReadDoubleAppSetting(
                "EpbPowerSupplyOffProofThresholdA", 0.5, 0.01, 20.0);
            _epbPowerSupplyOffProofMaxAgeMs = ReadIntAppSetting(
                "EpbPowerSupplyOffProofMaxAgeMs", 1000, 100, 10000);
            _daqClockRecoveryAttempts = new DaqRecoveryAttemptWindow(
                _daqClockRecoveryMaxAttempts,
                TimeSpan.FromMinutes(_daqClockRecoveryWindowMinutes));
            _daqLivenessWatchdogIntervalMs = ReadIntAppSetting(
                "DaqLivenessWatchdogIntervalMs", 20, 10, 1000);
            _daqLivenessWarnThresholdMs = ReadDoubleAppSetting(
                "DaqLivenessWarnThresholdMs", 75, 20, 200);
            _daqLivenessSuspectThresholdMs = ReadDoubleAppSetting(
                "DaqLivenessSuspectThresholdMs", 100, 50, 249);
            _daqLivenessTripThresholdMs = ReadDoubleAppSetting(
                "DaqLivenessTripThresholdMs", 250, 100, 5000);
            if (!AreDaqLivenessThresholdsStrictlyIncreasing(
                    _daqLivenessWarnThresholdMs,
                    _daqLivenessSuspectThresholdMs,
                    _daqLivenessTripThresholdMs))
                throw new InvalidOperationException(
                    "DAQ存活阈值必须满足 Warn < Suspect < Trip。" +
                    $" Effective={_daqLivenessWarnThresholdMs:0.#}/" +
                    $"{_daqLivenessSuspectThresholdMs:0.#}/" +
                    $"{_daqLivenessTripThresholdMs:0.#}ms");
            _globalFormalSlotAdmissionWindowMs = ReadIntAppSetting(
                "GlobalFormalSlotAdmissionWindowMs", 300, 200, 500);
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
            {
                var record = _cfg.Test.GetEpbRecord(channel);
                PublishChannelRuntimeState(
                    channel,
                    record.PermanentAlarmLatched
                        ? ChannelRuntimeState.AlarmStopped
                        : ChannelRuntimeState.NotEnabled,
                    record.PermanentAlarmLatched
                        ? record.PermanentAlarmCode
                        : "NotEnabled",
                    record.PermanentAlarmLatched
                        ? record.PermanentAlarmReason
                        : "本轮未启用",
                    correlationId: record.PermanentAlarmCorrelationId,
                    allowTerminalReset: true);
            }

            _timerRuntimeWatchdogIntervalMs = ReadIntAppSetting(
                // Recovering owner/task 丢失必须在 1 秒内被观察到；扫描只读取
                // 12 路内存状态，不执行设备 I/O。
                "TimerRuntimeWatchdogIntervalMs", 500, 250, 1000);
            _timerRuntimeSilenceThresholdMs = ReadIntAppSetting(
                "TimerRuntimeSilenceThresholdMs",
                Math.Max(30000, PeriodMs * 2),
                5000,
                600000);
            _recoveryOrphanGraceMs = ReadIntAppSetting(
                "RecoveryOrphanGraceMs", 10000, 10000, 60000);
            _timerRuntimeWatchdog = new System.Threading.Timer(
                InspectTimerRuntimeHealth,
                null,
                _timerRuntimeWatchdogIntervalMs,
                _timerRuntimeWatchdogIntervalMs);
            _daqLivenessWatchdog = new System.Threading.Timer(
                InspectDaqLiveness,
                null,
                _daqLivenessWatchdogIntervalMs,
                _daqLivenessWatchdogIntervalMs);
            _log.Info(
                $"FieldMetric DAQ_LIVENESS Result=Configured " +
                $"IntervalMs={_daqLivenessWatchdogIntervalMs} " +
                $"WarnMs={_daqLivenessWarnThresholdMs:F0} " +
                $"SuspectMs={_daqLivenessSuspectThresholdMs:F0} " +
                $"TripMs={_daqLivenessTripThresholdMs:F0} " +
                $"PowerOffProofA={_epbPowerSupplyOffProofThresholdA:F3} " +
                $"PowerOffProofMaxAgeMs={_epbPowerSupplyOffProofMaxAgeMs} " +
                $"ProcessId={Process.GetCurrentProcess().Id}",
                "FIELD");
        }

        private CancellationTokenSource RenewCyclePauseCts(int channel) =>
            RenewCyclePauseCts(channel, out _);

        private CancellationTokenSource RenewCyclePauseCts(
            int channel,
            out CancellationToken token)
        {
            var cts = new CancellationTokenSource();
            // Capture before publishing the source. StopAll may cancel and
            // dispose it immediately after publication.
            token = cts.Token;
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

        internal static bool AreDaqLivenessThresholdsStrictlyIncreasing(
            double warnMs,
            double suspectMs,
            double tripMs)
        {
            return !double.IsNaN(warnMs) && !double.IsInfinity(warnMs) && warnMs > 0 &&
                   !double.IsNaN(suspectMs) && !double.IsInfinity(suspectMs) &&
                   suspectMs > warnMs &&
                   !double.IsNaN(tripMs) && !double.IsInfinity(tripMs) &&
                   tripMs > suspectMs;
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
        internal async Task HydraulicEnterAsync(
            int channel,
            ChannelExecutionPermit permit,
            CancellationToken token)
        {
            if (!IsChannelExecutionPermitCurrent(channel, permit))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 旧执行代次禁止进入液压代次。");
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                token,
                permit.RevocationToken);
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
            await EnterSingleHydraulicGenerationAsync(
                    key,
                    channel,
                    permit,
                    executionCts.Token)
                .ConfigureAwait(false);
        }

        private void OnHighPriorityOffCompleted(HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null) return;
            RecordPhysicalEnergizationEdge(telemetry);
            LogHighPriorityOffFieldMetric(telemetry);
            RecordLateTerminalOffCompletion(telemetry);
            if (telemetry.Result)
                ObserveEnergizationEdge(telemetry.Channel, false);
            // Route the physical completion to the owning DAQ incident.  The
            // transaction checks commandId, channel and accepted receipt, so
            // a late completion from an older run cannot confirm a new one.
            foreach (var recovery in _daqAutoRecovery.Values.ToArray())
            {
                if (recovery == null || recovery.Transaction == null ||
                    !(recovery.AffectedChannels ?? Array.Empty<int>())
                        .Contains(telemetry.Channel))
                    continue;
                recovery.Transaction.ReportPhysicalCompletion(
                    telemetry.Channel,
                    telemetry.CommandId,
                    telemetry.Result);
            }
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
            ChannelExecutionPermit permit,
            CancellationToken token)
        {
            if (!IsChannelExecutionPermitCurrent(channel, permit))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 液压进入前执行许可已经失效。");
            var lease = await _hydCoordinator.EnterGenerationAsync(key, new[] { channel }, token)
                .ConfigureAwait(false);
            var scope = _hydCoordinator.CreateChannelScope(lease, channel);
            if (!IsChannelExecutionPermitCurrent(channel, permit))
            {
                try { await scope.AbortAsync("StaleExecutionPermit").ConfigureAwait(false); }
                catch { }
                throw new InvalidOperationException(
                    $"EPB[{channel}] 液压进入完成时执行许可已经失效；新代次已作废。");
            }
            _hydraulicLeaseByChannel[channel] = scope;
            // Pressure qualification is a real hydraulic evidence callback.  It
            // may advance an active StopAll material-progress clock, but the
            // recorder never changes the current stage deadline.
            var qualification = lease.Qualification;
            if (qualification != null)
            {
                RecordStopSafetyMaterialProgress(
                    Interlocked.Read(ref _stopSafetyGeneration),
                    $"HydraulicQualification:{qualification.HydraulicId}",
                    qualification.GenerationId,
                    $"压力资格材料事件 Hydraulic={qualification.HydraulicId};" +
                    $"Generation={qualification.GenerationId};" +
                    $"ActualBar={qualification.ActualBar:F3}");
            }
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

        /// <summary>
        /// Records the physical OFF receipt edge from the real DO worker.
        /// This is intentionally fed by HighPriorityOffCompleted rather than
        /// by a Controller-level test seam, so the EPB4/EPB10 scope and the
        /// command receipt remain the same ones used in production.
        /// </summary>
        internal void RecordPhysicalEnergizationEdge(HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null || telemetry.Channel < 1 || telemetry.Channel > 12)
                return;
            _physicalEnergizationEdges.AddOrUpdate(
                telemetry.Channel,
                1L,
                (_, previous) => previous == long.MaxValue ? previous : previous + 1L);
        }

        /// <summary>
        /// Single energized-state edge used by real Forward/Reverse success,
        /// real OFF receipts, and deterministic acceptance setup.  The stop
        /// scope observes only this runtime state; it never imports a config
        /// channel list supplied by a test seam.
        /// </summary>
        internal void ObserveEnergizationEdge(int channel, bool energized)
        {
            if (channel < 1 || channel > 12) return;
            var bit = 1L << channel;
            while (true)
            {
                var current = Interlocked.Read(ref _energizedChannelsMask);
                var next = energized ? current | bit : current & ~bit;
                if (Interlocked.CompareExchange(
                        ref _energizedChannelsMask,
                        next,
                        current) == current)
                    return;
            }
        }

        internal long GetPhysicalEnergizationEdgeCount(int channel)
        {
            return _physicalEnergizationEdges.TryGetValue(channel, out var count)
                ? count
                : 0L;
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

        private async Task RetireOrAbortHydraulicLeaseForChannelAsync(
            int channel,
            string abortReason,
            string completedForceReleaseReason,
            int expectedHydraulicId)
        {
            if (!_hydraulicLeaseByChannel.TryGetValue(channel, out var scope)) return;

            RequireSoftwareRecoveryOutputOff(
                channel,
                abortReason ?? "HydraulicLeaseRetireOrAbort");
            if (!string.IsNullOrWhiteSpace(completedForceReleaseReason) &&
                _hydCoordinator != null &&
                _hydCoordinator.TryRetireForceReleasedScope(
                    scope,
                    expectedHydraulicId,
                    completedForceReleaseReason))
            {
                ((ICollection<KeyValuePair<int, HydraulicChannelLeaseScope>>)_hydraulicLeaseByChannel)
                    .Remove(new KeyValuePair<int, HydraulicChannelLeaseScope>(channel, scope));
                _log.Info(
                    $"液压旧租约已按已完成ForceRelease精确退役：EPB={channel} " +
                    $"Hydraulic={expectedHydraulicId} Reason={completedForceReleaseReason}",
                    "液压协调");
                return;
            }

            await AbortHydraulicLeaseForChannelAsync(channel, abortReason)
                .ConfigureAwait(false);
        }

        private void ObserveSafetyTask(Task task, string operation, int channel)
        {
            ObserveBackgroundTask(task, operation, channel);
        }

        internal void ObserveBackgroundTask(Task task, string operation, int channel = 0)
        {
            _taskSupervisor.Observe(task, operation, _activeBatchId, channel);
            _recoveryTaskRegistry.Track(
                task,
                operation,
                Interlocked.Read(ref _runEpoch),
                channel);
        }

        internal void ObserveBackgroundTask(
            Task task,
            string operation,
            IEnumerable<int> channels)
        {
            var affected = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            _taskSupervisor.Observe(
                task,
                operation,
                _activeBatchId,
                affected.FirstOrDefault());
            _recoveryTaskRegistry.Track(
                task,
                operation,
                Interlocked.Read(ref _runEpoch),
                affected);
        }

        // Recovery entry points own their lifecycle through
        // TryBeginRecoveryIncident.  Callers may still need supervision for
        // the public async facade, but that facade must not be tracked as a
        // second recovery worker before the real, bound wrapper exists.
        internal void ObserveNonRecoveryLifecycleTask(
            Task task,
            string operation,
            int channel = 0)
        {
            _taskSupervisor.Observe(task, operation, _activeBatchId, channel);
        }

        internal void ObserveNonRecoveryLifecycleTask(
            Task task,
            string operation,
            IEnumerable<int> channels)
        {
            var firstChannel = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .FirstOrDefault();
            _taskSupervisor.Observe(task, operation, _activeBatchId, firstChannel);
        }

        private RecoveryIncidentCoordinator CreateRecoveryIncidentCoordinator()
        {
            return new RecoveryIncidentCoordinator(
                _recoveryContractGate,
                new RecoveryIncidentCoordinator.Port
                {
                    Reserve = contract => _recoveryTaskRegistry.Reserve(contract),
                    Schedule = body => Task.Run(body),
                    Bind = (lease, worker) => lease != null && lease.TryBind(worker),
                    PublishRecovering = contract => { },
                    Observe = (incident, worker) =>
                    {
                        _taskSupervisor.Observe(
                            worker,
                            incident.Contract.Operation,
                            _activeBatchId,
                            incident.Contract.Channels.FirstOrDefault());
                    },
                    CommandOff = (contract, reason) =>
                    {
                        foreach (var channel in contract.SafetyAffectedChannels ?? Array.Empty<int>())
                            CommandEpbOffHighPriority(channel, reason);
                    },
                    PublishSafeTerminal = (contract, reasonCode, reasonText) =>
                        PublishRecoverySafeTerminal(contract, reasonCode, reasonText),
                    IsRecoveringPublished = IsRecoveryContractPublished,
                    IsTerminalCommitted = IsRecoveryTerminalStateCommitted,
                    IsRegistered = contract =>
                    {
                        if (contract == null) return false;
                        lock (_recoveryContractGate)
                            return _activeRecoveryContracts.ContainsKey(contract.IncidentId);
                    },
                    Start = incident => incident.ReleaseStartSignal(),
                     Register = incident =>
                     {
                         lock (_recoveryContractGate)
                         {
                             _activeRecoveryContracts[incident.Contract.IncidentId] =
                                 new RecoveryIncidentHandle(this, incident);
                             PublishRecoveryAggregateOwnershipSourceLocked();
                         }
                     },
                     Unregister = incident =>
                     {
                         if (incident == null) return;
                         lock (_recoveryContractGate)
                         {
                             _activeRecoveryContracts.Remove(incident.Contract.IncidentId);
                             _recoveryAttemptFences.TryRemove(incident.Contract.IncidentId, out _);
                             PublishRecoveryAggregateOwnershipSourceLocked();
                         }
                     },
                    Fault = (stage, error) =>
                        _log?.Error(
                            $"RecoveryIncidentCoordinator {stage} failed: {error?.Message}",
                            "EPB",
                            error)
                });
        }

        /// <summary>
        /// Starts a recovery incident using the required transaction order:
        /// reserve lease -> register immutable contract -> create/bind a
        /// non-running wrapper -> publish owner+Recovering.  The caller must
        /// explicitly call RecoveryIncidentHandle.Start() after this method
        /// returns true.  The factory returns a body delegate, never a running
        /// Task; this is what makes a factory/publish failure unable to leak a
        /// worker into the hardware path.
        /// </summary>
        internal bool TryBeginRecoveryIncident(
            string operation,
            Guid runId,
            long runEpoch,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId,
            IEnumerable<int> channels,
            Func<RecoveryContractSnapshot, Func<Task>> workerFactory,
            Action<RecoveryContractSnapshot> publishRecovering,
            out RecoveryIncidentHandle incident,
            Guid incidentId = default)
        {
            incident = null;
            var safetyAffected = (channels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var ownedChannels = safetyAffected
                .Where(IsChannelEnabled)
                .ToArray();
            if (ownedChannels.Length == 0) return false;

            lock (_recoveryAdmissionGate)
            {
                if (!IsRecoveryMemoryAdmissionAllowed() ||
                    IsEnergizationRevoked || runId != _activeBatchId ||
                    runEpoch != Interlocked.Read(ref _runEpoch))
                    return false;

                var result = _recoveryIncidentCoordinator.TryBegin(
                    operation,
                    runId,
                    runEpoch,
                    ownerKind,
                    targetPhase,
                    ownerId,
                    ownedChannels,
                    safetyAffected,
                    workerFactory,
                    publishRecovering,
                    out var createdIncident,
                    incidentId);
                if (result != RecoveryIncidentCoordinator.BeginResult.Created ||
                    createdIncident == null)
                    return false;

                lock (_recoveryContractGate)
                    return _activeRecoveryContracts.TryGetValue(
                               createdIncident.Contract.IncidentId,
                               out incident);
            }
        }

        private bool StartRecoveryIncident(RecoveryIncidentCoordinator.Incident incident)
        {
            if (incident == null) return false;
            lock (_recoveryAdmissionGate)
            {
                var contract = incident.Contract;
                if (IsEnergizationRevoked || contract == null ||
                    contract.RunId != _activeBatchId ||
                    contract.RunEpoch != Interlocked.Read(ref _runEpoch))
                    return false;
                return incident.Start();
            }
        }

        private RecoveryIncidentHandle FindActiveRecoveryContractLocked(
            int channel,
            Guid runId,
            long runEpoch,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId)
        {
            return _activeRecoveryContracts.Values
                .Where(incident => incident?.Contract != null &&
                                   incident.TerminalPublished == 0)
                .Where(incident => incident.Contract.RunId == runId &&
                                   incident.Contract.RunEpoch == runEpoch &&
                                   (incident.Contract.Channels ?? Array.Empty<int>()).Contains(channel))
                .Where(incident => ownerKind == RecoveryOwnerKind.None ||
                                   (incident.Contract.OwnerKind == ownerKind &&
                                    incident.Contract.TargetPhase == targetPhase &&
                                    incident.Contract.OwnerId == ownerId))
                .OrderBy(incident => incident.Contract.StartedUtc)
                .FirstOrDefault();
        }

        private bool IsRecoveryContractPublished(RecoveryContractSnapshot contract)
        {
            if (contract == null) return false;
            foreach (var channel in contract.Channels ?? Array.Empty<int>())
            {
                var state = _channelRuntimeStateStore.Get(channel);
                if (state == null ||
                    state.State != ChannelRuntimeState.Recovering ||
                    state.RunId != contract.RunId ||
                    state.RunEpoch != contract.RunEpoch ||
                    state.RecoveryOwnerKind != contract.OwnerKind ||
                    state.RecoveryOwnerId != contract.OwnerId ||
                    state.RecoveryOwnerGeneration != contract.RunEpoch ||
                    state.RecoveryTargetPhase != contract.TargetPhase)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Abort a transaction that has not been handed to its caller.  The
        /// safety order is deliberate and observable: cancel the non-running
        /// wrapper, submit high-priority OFF for every affected channel, publish
        /// exactly one safe terminal, then remove the contract and lease.
        /// </summary>
        /// <summary>Publish one idempotent terminal fallback without a worker owner.</summary>
        private void PublishRecoverySafeTerminal(
            RecoveryContractSnapshot contract,
            string reasonCode,
            string reasonText)
        {
            if (contract == null) return;
            foreach (var channel in contract.Channels ?? Array.Empty<int>())
            {
                lock (_channelExecutionGates[channel - 1])
                {
                    var current = _channelRuntimeStateStore.Get(channel);
                    if (!CanCleanupRecoveryChannel(contract, current, _activeBatchId,
                            Interlocked.Read(ref _runEpoch)))
                        continue;
                    // A safety terminal is also the ownership/resource terminal.
                    // Leaving a cached Timer or Runner behind makes the watchdog
                    // observe StartBlocked together with active runtime resources
                    // and can keep the process in an unrecoverable confirmation
                    // loop.
                    try { CancelCyclePauseCts(channel); } catch { }
                    try { CancelStopCts(channel); } catch { }
                    try { RemoveTimerRuntime(channel, "RecoverySafeTerminal"); } catch { }
                    try { RemoveRunnerRuntime(channel, "RecoverySafeTerminal"); } catch { }
                    try { UnmarkHydraulicParticipant(channel); } catch { }
                    if (current != null &&
                        current.CorrelationId == contract.IncidentId &&
                        ChannelRuntimeStateStore.IsLatchedStop(current.State))
                        continue;
                    try
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.StartBlocked,
                            reasonCode ?? "RecoverySafeTerminal",
                            reasonText ?? "恢复事务已安全收口。",
                            correlationId: contract.IncidentId,
                            allowTerminalReset: true,
                            allowSystemFaultReset: true,
                            runIdOverride: contract.RunId);
                    }
                    catch (Exception terminalError)
                    {
                        // The normal publisher may be fault-injected or an
                        // observer/store can fail.  Keep the safety terminal in
                        // the authoritative store and never retain owner state.
                        try
                        {
                            _channelRuntimeStateStore.Publish(
                                new ChannelRuntimeStateChangedEvent
                                {
                                    Channel = channel,
                                    State = ChannelRuntimeState.StartBlocked,
                                    ReasonCode = reasonCode ?? "RecoverySafeTerminal",
                                    ReasonText = reasonText ?? "恢复事务已安全收口。",
                                    TimestampUtc = DateTime.UtcNow,
                                    CorrelationId = contract.IncidentId,
                                    RunId = contract.RunId,
                                    RunEpoch = contract.RunEpoch,
                                    Enabled = IsChannelEnabled(channel),
                                    AffectedChannels = new[] { channel }
                                },
                                allowTerminalReset: true,
                                allowSystemFaultReset: true);
                        }
                        catch (Exception fallbackError)
                        {
                            _log?.Error(
                                $"Recovery safe terminal fallback failed. " +
                                $"Incident={contract.IncidentId:N}; EPB={channel}; " +
                                $"Primary={terminalError.Message}; Fallback={fallbackError.Message}",
                                "EPB",
                                fallbackError);
                        }
                    }
                }
            }
        }

        internal static bool CanCleanupRecoveryChannel(RecoveryContractSnapshot contract,
            ChannelRuntimeStateChangedEvent current, Guid activeRun, long activeEpoch)
        {
            if (contract == null || contract.RunId != activeRun || contract.RunEpoch != activeEpoch)
                return false;
            if (current == null) return true;
            if (current.RunId != contract.RunId || current.RunEpoch != contract.RunEpoch)
                return false;
            if (ChannelRuntimeStateStore.IsLatchedStop(current.State)) return false;
            if (current.State == ChannelRuntimeState.Recovering)
                return current.RecoveryOwnerId == contract.OwnerId &&
                       current.CorrelationId == contract.IncidentId;
            // A worker which already committed Starting/Running/Paused owns its
            // new resources. Only a pre-admission state can be rolled back here.
            return current.TimestampUtc <= contract.StartedUtc;
        }

        /// <summary>
        /// Normalize the post-worker state before releasing a recovery
        /// contract.  Rejoin helpers may have already moved a channel to
        /// Running/Paused with their own correlation id; that is a real
        /// terminal lifecycle transition, but CompleteRecoveryIncident still
        /// requires the immutable incident correlation to be observable before
        /// releasing the lease.  Re-publish the existing lifecycle state with
        /// the contract id, or fail closed to StartBlocked if it is still
        /// Recovering/missing.
        /// </summary>
        private void CommitRecoveryIncidentStateForRelease(
            RecoveryContractSnapshot contract,
            string reasonCode,
            string reasonText)
        {
            if (contract == null) return;
            foreach (var channel in contract.Channels ?? Array.Empty<int>())
            {
                var current = _channelRuntimeStateStore.Get(channel);
                if (contract.RunId != _activeBatchId || contract.RunEpoch != Interlocked.Read(ref _runEpoch) ||
                    (current != null && (current.RunId != contract.RunId || current.RunEpoch != contract.RunEpoch)))
                    continue;
                if (current == null || current.State == ChannelRuntimeState.Recovering)
                {
                    PublishRecoverySafeTerminal(
                        new RecoveryContractSnapshot(contract.IncidentId, contract.RunId, contract.RunEpoch,
                            contract.OwnerId, contract.OwnerKind, contract.TargetPhase, contract.Operation,
                            contract.StartedUtc, contract.HardDeadlineUtc, new[] { channel }),
                        reasonCode ?? "RecoveryTerminalWithoutRejoin",
                        reasonText ?? "恢复worker未完成重入，已保持安全终态。 ");
                    continue;
                }
                // A successor already owns this lifecycle. The retiring transaction must
                // neither rewrite its evidence nor remove its execution resources.

            }
        }

        /// <summary>
        /// Complete a bounded recovery attempt without converting it into a
        /// permanent safe terminal. The retry stays in the same run/epoch,
        /// clears the recovery owner through an incident-correlated lifecycle
        /// publication, and deliberately keeps Runner/Timer/permit resources.
        /// </summary>
        private void CommitRecoveryIncidentStateForRetry(
            RecoveryContractSnapshot contract,
            ChannelRuntimeState retryState,
            string reasonCode,
            string reasonText)
        {
            if (contract == null) return;
            if (retryState != ChannelRuntimeState.Starting &&
                retryState != ChannelRuntimeState.Learning &&
                retryState != ChannelRuntimeState.Running &&
                retryState != ChannelRuntimeState.WarningRunning)
                throw new ArgumentOutOfRangeException(
                    nameof(retryState),
                    retryState,
                    "RetryReady 只能回到可继续执行的非终态。");

            foreach (var channel in contract.Channels ?? Array.Empty<int>())
            {
                var current = _channelRuntimeStateStore.Get(channel);
                var sameOwner = current != null &&
                                current.State == ChannelRuntimeState.Recovering &&
                                current.RunId == contract.RunId &&
                                current.RunEpoch == contract.RunEpoch &&
                                current.RecoveryOwnerKind == contract.OwnerKind &&
                                current.RecoveryTargetPhase == contract.TargetPhase &&
                                current.RecoveryOwnerId == contract.OwnerId &&
                                current.RecoveryOwnerGeneration == contract.RunEpoch;
                if (!sameOwner)
                    throw new InvalidOperationException(
                        $"EPB[{channel}] RetryReady提交时恢复所有权已变化；" +
                        $"Incident={contract.IncidentId:N} State={current?.State} " +
                        $"Owner={current?.RecoveryOwnerKind}/{current?.RecoveryOwnerId:N}。");

                PublishChannelRuntimeState(
                    channel,
                    retryState,
                    reasonCode ?? "RecoveryRetryReady",
                    reasonText ?? "恢复尝试已完成，原执行流程继续重试。",
                    affectedChannels: contract.Channels,
                    correlationId: contract.IncidentId,
                    allowTerminalReset: true,
                    allowSystemFaultReset: false,
                    runIdOverride: contract.RunId,
                    runEpochOverride: contract.RunEpoch);
            }
        }

        private bool IsRecoveryTerminalStateCommitted(
            RecoveryContractSnapshot contract)
        {
            if (contract == null) return false;
            if (!_recoveryAttemptFences.TryGetValue(contract.IncidentId, out var fence)) fence = long.MaxValue;
            foreach (var channel in contract.Channels ?? Array.Empty<int>())
            {
                var state = _channelRuntimeStateStore.Get(channel);
                var ownsAttempt = _cycleAttempts.TryGetCurrent(channel, out var attempt) &&
                    RecoveryOwnsAttempt(contract, fence, attempt);
                var ownsExecution = _cycleAttempts.TryGetLastExecution(channel, out var execution) &&
                    RecoveryOwnsAttempt(contract, fence, execution) &&
                    !execution.IsExecutionCompleted;
                if (ownsAttempt || ownsExecution || state == null) return false;
                if (state.State == ChannelRuntimeState.Recovering &&
                    state.RunId == contract.RunId && state.RunEpoch == contract.RunEpoch &&
                    state.RecoveryOwnerId == contract.OwnerId) return false;
                // A later owner or a completed Stop owns the projection now. The old
                // transaction may retire its own lease without overwriting that state.
                if (state.RunId == contract.RunId && state.RunEpoch == contract.RunEpoch &&
                    state.TimestampUtc < contract.StartedUtc) return false;
            }
            return true;
        }

        private bool CompleteRecoveryIncident(
            RecoveryIncidentHandle incident,
            Action<RecoveryContractSnapshot> publishTerminal)
        {
            return incident?.CompleteOnCoordinator(publishTerminal) == true;
        }

        internal bool HasActiveRecoveryContract(int channel, long runEpoch)
        {
            lock (_recoveryContractGate)
                return _activeRecoveryContracts.Values.Any(contract =>
                    contract?.Contract != null &&
                    contract.Contract.RunEpoch == runEpoch &&
                    contract.TerminalPublished == 0 &&
                    (contract.Contract.Channels ?? Array.Empty<int>()).Contains(channel));
        }

        internal TaskSupervisorEntry[] CaptureBackgroundTasks()
        {
            return _taskSupervisor.Snapshot();
        }

        internal async Task<bool> DrainBackgroundTasksAsync(int timeoutMs)
        {
            var boundedTimeoutMs = Math.Max(1, timeoutMs);
            // Incident evidence is an independent diagnostic persistence
            // worker. It must not extend StopAll/release-hardware waits or
            // enter the control/recovery TaskSupervisor drain boundary.
            return await _taskSupervisor.DrainAsync(boundedTimeoutMs)
                .ConfigureAwait(false);
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
            var singleRunEpoch = Interlocked.Increment(ref _runEpoch);
            Interlocked.Exchange(ref _formalPhaseCommitted, 0);
            Interlocked.Exchange(ref _idleSessionClosureScheduled, 0);
            BeginDaqIncidentRun(singleRunId, new[] { channel });
            InvalidateStopSafetyCache();
            ResetTransientFaultStateForRestart(new[] { channel }, "FreshSingleChannelStart");
            // 单通道启动的DAQ自愈最多三次；提前建立停止令牌，使“停止”按钮随时可取消。
            var stopCts = RenewStopCts(channel, out var stopToken);
            using var startLinked = CancellationTokenSource.CreateLinkedTokenSource(uiToken, stopToken);
            try
            {
                // 空闲台架的单通道开始同样必须消除上次进程退出后可能遗留的物理DO状态。
                // 已有其它通道运行时不能全局关闭四台电源；该场景继续使用现有运行链的
                // 组级上电许可，不得破坏正在运行的通道。
                if (!IsBatchSessionActive && _timers.IsEmpty && _runners.IsEmpty)
                    await EstablishColdStartSafeBaselineAsync(
                            new[] { channel },
                            startLinked.Token)
                        .ConfigureAwait(false);
                await EnsureDaqReadyBeforeStartAsync(new[] { channel }, startLinked.Token).ConfigureAwait(false);
                EnsureStrictCurveControl(new[] { channel });
                SaveProgramSafetySnapshot();
                var powerHardwareDisabled = await EnsurePowerSupplyReadyBeforeStartAsync(
                        new[] { channel },
                        startLinked.Token)
                    .ConfigureAwait(false);
                if (powerHardwareDisabled.Contains(channel))
                {
                    CancelStopCts(channel);
                    _log.Error(
                        $"EPB[{channel}] 所属电气组启动保护已确认并永久隔离；" +
                        "保持AlarmStopped，不进入启动/学习。",
                        "程控电源");
                    return;
                }
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

            AuthorizeFreshRunAfterSafetyPreflight();
            AuthorizeChannelExecution(channel, singleRunEpoch);
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
            _nonRecoverableChannelFaultReasons.TryRemove(channel, out _);
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
                $"固定批次={singleAssignment.BatchOrdinal}，首启相位={staggerMs}ms。",
                "EPB");

            //日志记录周期
            _log.Info($"高精度定时器，  EPB[{channel}] 周期 {periodMs}ms，采样 {sampleMs}ms，前进阈值 {forwardA}A，保持时间 {holdMs}ms",
                "EPB");

            var timer = GetTimer(channel, periodMs, _cfg.Test.OverrunPolicy);

            // 标记为“参与液压判定”（用于后续批量建压锚点过滤；单通道模式也保持一致）
            MarkHydraulicParticipant(channel);

            var runner = (EpbCycleRunner)GetRunner(channel);
            var learningEvidence = CaptureLearningEvidenceContext(singleRunId);

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
                    {
                        var positioningFailure = positioningFailures[0];
                        if (IsInfrastructureHardwareIsolationFailureCode(positioningFailure.Code))
                        {
                            _log.Error(
                                $"EPB[{channel}] 所属液压组启动硬件故障已确认并永久隔离；" +
                                "保持AlarmStopped，不进入学习/正式运行。",
                                "液压协调");
                            return;
                        }
                        throw new InvalidOperationException(
                            $"EPB[{channel}] 启动定位确认实时故障：" +
                            $"{positioningFailure.Code}/{positioningFailure.Reason}");
                    }

                    runner.UseNoHeadPhase = true;
                    runner.EnableTailCompensation = true;
                    runner.TailMinMs = T8MinMs;
                    var pressureGroup = channel <= 6 ? 1 : 2;
                    for (var ordinal = 1; ordinal <= learnCycles; ordinal++)
                    {
                        if (IsMechanicalTargetReached(channel))
                            break;
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
                                learningEvidence,
                                startLinked.Token)
                            .ConfigureAwait(false);
                    }
                    if (!IsMechanicalTargetReached(channel))
                    {
                        EnsureAdaptiveProfilesReady(new[] { channel });
                        _log.Info($"EPB[{channel}] 自学习完成，进入正式试验。", "EPB");
                    }
                    else
                    {
                        _log.Info(
                            $"EPB[{channel}] 自学习已消费最后的机械目标圈；不再进入正式试验。",
                            "EPB");
                    }
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
                    if (!IsChannelEnabled(channel) &&
                        (_nonRecoverableChannelFaultLatch.ContainsKey(channel) ||
                         IsAlarmStopRequested(channel)))
                    {
                        _log.Error(
                            $"EPB[{channel}] 启动/学习期间已由硬件确认处理器永久隔离；" +
                            "保持AlarmStopped，不覆盖为StartBlocked。" + ex.Message,
                            "EPB",
                            ex);
                        return;
                    }
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

            if (IsMechanicalTargetReached(channel))
            {
                FinalizeChannelAfterNaturalCompletion(
                    channel,
                    Recorder?.GetLastCycleNumber(channel) ?? 0);
                timer.Stop();
                return;
            }

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Running,
                "Running",
                "正式试验运行中",
                correlationId: singleRunId);
            LogFieldSessionMetric("Start", singleRunId, new[] { channel }, false, "SingleFormal");
            var singleFormalAnchorUtc = DateTime.UtcNow.AddMilliseconds(staggerMs);
            var singleBaseCycle = Recorder?.GetLastCycleNumber(channel) ?? 0;
            var singleSuccessfulCycles = 0;
            ObserveBackgroundTask(timer.StartAsync(null, staggerMs, async (i, token) =>
            {
                var cyclePauseCts = RenewCyclePauseCts(
                    channel,
                    out var cyclePauseToken);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    token,
                    stopToken,
                    cyclePauseToken);
                var ct = linked.Token;
                var actualStartUtc = DateTime.UtcNow;
                var nominalDueUtc = singleFormalAnchorUtc.AddMilliseconds((long)(i - 1) * periodMs);
                var elapsedSinceNominalMs = (actualStartUtc - nominalDueUtc).TotalMilliseconds;
                var rolledPeriods = elapsedSinceNominalMs <= 0
                    ? 0L
                    : (long)Math.Floor(elapsedSinceNominalMs / periodMs);
                var plannedStartUtc = nominalDueUtc.AddMilliseconds(rolledPeriods * periodMs);
                var cycleNumber = singleBaseCycle + i;
                MarkElectricalPhaseDue(channel, plannedStartUtc);

                if (!await WaitForPreviousCycleExecutionAsync(channel, ct)
                        .ConfigureAwait(false))
                {
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }

                _log.Info(
                    $"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始，Run={singleRunId:N} " +
                    $"Group={singleAssignment.ElectricalGroupId} Phase={singleAssignment.PhaseMs}ms " +
                    $"PlannedUtc={plannedStartUtc:O} ActualUtc={actualStartUtc:O} " +
                    $"DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3}。",
                    "EPB");


                // —— 圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）——
                var recorder = Recorder;
                if (!TryBeginFormalCycleAttempt(
                        recorder,
                        channel,
                        cycleNumber,
                        DateTime.UtcNow,
                        singleRunId,
                        CycleAttemptKind.FormalSingle,
                        ct,
                        out var cycleAttempt))
                {
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }

                if (!cycleAttempt.MarkExecutionStarted())
                {
                    if (!cycleAttempt.IsExecutionStarted)
                        CompleteCycleAttemptExecution(cycleAttempt);
                    ReleaseCyclePauseCts(channel, cyclePauseCts);
                    return false;
                }
                using var executionScope =
                    CompleteCycleAttemptExecutionOnCallbackExit(cycleAttempt);

                var ok = false;
                EpbCycleOutcome cycleOutcome;
                try
                {
                    ok = await runner.RunOneAsync(periodMs, cycleAttempt.AttemptCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ok = false;
                }
                catch
                {
                    ok = false;
                }
                finally
                {
                    // execution scope 尚未释放，故此处取得的引用只属于本 attempt。
                    cycleOutcome = runner.LastCycleOutcome;
                }
                var controlSucceeded = IsFormalControlSucceeded(
                    ok,
                    cycleOutcome.IsSuccess);
                if (controlSucceeded)
                    _watchdogConsecutiveSoftwareAborts[channel] = 0;
                var mechanicalTargetReached = false;
                if (cycleOutcome.MechanicalCycleCompleted)
                {
                    OnMechanicalCycleCompleted(
                        channel,
                        CycleAttemptKind.FormalSingle,
                        cycleNumber);
                    mechanicalTargetReached = IsMechanicalTargetReached(channel);
                }
                var controlNeedsSoftwareRecovery =
                    cycleOutcome.Kind == EpbCycleOutcomeKind.SoftwareRecovery;
                if (controlNeedsSoftwareRecovery)
                {
                    RecordWatchdogSoftwareAbort(channel);
                    ReportFormalControlSoftwareRecovery(
                        channel,
                        cycleNumber,
                        cycleOutcome.Reason);
                }

                // —— 圈结束：根据是否报警停机决定封圈状态 ——
                var persistenceCommitted = false;
                try
                {
                    var abortKey = DaqAbortedCycleKey(
                        cycleAttempt.RunId,
                        cycleAttempt.RunEpoch,
                        channel,
                        cycleNumber);
                    var abortedByRecoveredGap =
                        _daqRecoveredGapAbortedCycles.ContainsKey(abortKey);
                    if (TryConsumeDaqClockCycleAbort(
                            cycleAttempt.RunId,
                            cycleAttempt.RunEpoch,
                            channel,
                            cycleNumber))
                    {
                        if (abortedByRecoveredGap)
                            _daqRecoveredGapAbortedCycles.TryRemove(abortKey, out _);
                        // 标记在恢复入口即锁存，Runner 可能先于 DAQ Finalizer 退出；此处
                        // 必须使用真正的 AbortRecorderOnce，而不是仅清理内存身份，确保
                        // 事故圈恰好写入一次 AbortedBySoftwareRecovery。
                        AbortFormalCycleAttempt(
                            cycleAttempt,
                            recorder,
                            DateTime.UtcNow,
                            abortedByRecoveredGap
                                ? "AbortedByDaqGap"
                                : "AbortedBySoftwareRecovery");
                        _log.Warn(
                            $"EPB[{channel}] 单通道周期 {cycleNumber} 已由DAQ流程封存，跳过重复终态提交。",
                            "落盘");
                    }
                    else
                    {
                        var finalN = recorder?.GetCurrentCycleSampleCount(channel) ?? 0;
                        if (IsAlarmStopRequested(channel))
                        {
                            // 报警后台流程负责在快照文件存在后封圈；context 保持可见。
                        }
                        else if (controlNeedsSoftwareRecovery)
                            AbortFormalCycleAttempt(
                                cycleAttempt,
                                recorder,
                                DateTime.UtcNow,
                                "AbortedBySoftwareRecovery");
                        else if (cycleOutcome.Kind == EpbCycleOutcomeKind.HardFault)
                            AbortFormalCycleAttempt(
                                cycleAttempt,
                                recorder,
                                DateTime.UtcNow,
                                "failed");
                        else if (controlSucceeded)
                            persistenceCommitted = CompleteFormalCycleAttempt(
                                cycleAttempt,
                                recorder,
                                finalN,
                                DateTime.UtcNow);
                        else
                            AbortFormalCycleAttempt(
                                cycleAttempt,
                                recorder,
                                DateTime.UtcNow,
                                cycleOutcome.Kind == EpbCycleOutcomeKind.Canceled
                                    ? "canceled"
                                    : "failed");
                    }
                }
                catch (Exception ex)
                {
                    PreserveFormalCycleForPersistenceRecovery(
                        channel,
                        cycleNumber,
                        "CycleFinalizer",
                        ex);
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
                             cycleNumber,
                             committedCycles,
                             i,
                             cycleAttempt,
                             cycleOutcome);
                    if (!nonRecoverableAlarm && mechanicalTargetReached)
                    {
                        FinalizeChannelAfterNaturalCompletion(channel, cycleNumber);
                        timer.Stop();
                    }
                }
                else if (mechanicalTargetReached && !IsAlarmStopRequested(channel))
                {
                    FinalizeChannelAfterNaturalCompletion(channel, cycleNumber);
                    timer.Stop();
                }

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
            _channelPausedUtc[channel] = DateTime.UtcNow;
            PublishChannelRuntimeState(channel, ChannelRuntimeState.Paused, "Paused", "试验已暂停");
            _adaptiveLifecyclePort.PublishChannelPaused(
                channel,
                _activeBatchId,
                Interlocked.Read(ref _runEpoch));
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
            _channelPausedUtc.TryRemove(channel, out _);
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
            _adaptiveLifecyclePort.PublishStop(
                channel,
                "StopChannel",
                _activeBatchId,
                Interlocked.Read(ref _runEpoch));
            _currentCycleNumberByChannel.TryGetValue(channel, out var interruptedCycleNumber);
            RevokeChannelExecutionPermit(channel, "StopChannel");

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

            // —— 收尾：先把活动圈提交为明确终态，再强制让 Latest 包包含该终态圈。—— //
            var stopEvidenceComplete = TryFinalizeManualStopCycle(channel, interruptedCycleNumber);
            if (stopEvidenceComplete)
                stopEvidenceComplete = EnsureLatestStopSnapshotAsync(
                        channel,
                        interruptedCycleNumber > 0 ? interruptedCycleNumber : 0,
                        "ManualStop")
                    .GetAwaiter()
                    .GetResult();

            PublishChannelRuntimeState(
                channel,
                stopEvidenceComplete
                    ? ChannelRuntimeState.ManualStopped
                    : ChannelRuntimeState.SystemFault,
                stopEvidenceComplete ? "ManualStopped" : "StopEvidenceIncomplete",
                stopEvidenceComplete
                    ? "人工停止"
                    : "人工停止安全动作已执行，但圈终态或最近快照未完整落盘");
            TryEndBatchSessionWhenIdle("ChannelStop");
            TryDisableIdlePowerGroup(channel, "通道停止后电源组已无运行通道");
        }

        /// <summary>
        /// 非运行终态提交前的唯一清场入口。先撤销 Timer/Runner/液压参与权并确认 OFF，
        /// 再允许发布 StartBlocked 等终态，杜绝“红色终态但后台仍继续发命令”。
        /// </summary>
        private bool RevokeChannelExecutionBeforeTerminalState(int channel, string reason)
        {
            RevokeChannelExecutionPermit(channel, reason);
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

        private void AuthorizeChannelExecution(int channel, long runEpoch)
        {
            lock (_channelExecutionGates[channel - 1])
                _channelExecutionFence.Authorize(channel, runEpoch);
        }

        private void RevokeChannelExecutionPermit(int channel, string reason)
        {
            lock (_channelExecutionGates[channel - 1])
                _channelExecutionFence.Revoke(channel);
            var hydraulicGroup = GetHydraulicGroupForChannel(channel);
            if (hydraulicGroup > 0)
                _recoveryOwnership.CancelGroup(hydraulicGroup);
            _log?.Info(
                $"EPB[{channel}] 执行代际授权已撤销。Reason={reason}",
                "EPB并发");
        }

        private ChannelExecutionPermit RequireCurrentChannelExecutionPermit(
            int channel,
            string resource)
        {
            var permit = _channelExecutionFence.Capture(channel);
            var state = _channelRuntimeStateStore.Get(channel);
            if (!_channelExecutionFence.IsCurrent(permit) ||
                permit.RunEpoch != Interlocked.Read(ref _runEpoch) ||
                !ChannelExecutionFence.CanCreateRuntime(
                    state,
                    IsChannelEnabled(channel),
                    RequiresProcessRestart))
                throw new InvalidOperationException(
                    $"EPB[{channel}] 禁止创建{resource}：执行授权缺失、代际过期或生命周期已终止。" +
                    $" State={state?.State} PermitEpoch={permit.RunEpoch} " +
                    $"CurrentEpoch={Interlocked.Read(ref _runEpoch)}");
            return permit;
        }

        private bool IsChannelExecutionPermitCurrent(
            int channel,
            ChannelExecutionPermit permit)
        {
            return permit.Channel == channel &&
                   _channelExecutionFence.IsCurrent(permit) &&
                   permit.RunEpoch == Interlocked.Read(ref _runEpoch) &&
                   IsChannelEnabled(channel) &&
                   !RequiresProcessRestart;
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
            RevokeChannelExecutionPermit(channel, "AlarmStop");
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

            // Latest 由报警圈原子封账后的 EnsureLatestStopSnapshotAsync 单一发布。
            // 快速停机阶段只锁存安全动作，禁止生成不含终止圈的临时 Latest。

            TryEndBatchSessionWhenIdle("AlarmStop");
            TryDisableIdlePowerGroup(channel, "报警通道停止后电源组已无运行通道");
        }


        private void OnRunnerAlarmRaised(int channel, string reason)
        {
            OnRunnerAlarmRaised(channel, reason, 0);
        }

        private void OnRunnerAlarmRaised(
            int channel,
            string reason,
            int confirmedTerminalCycleNumber)
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
            var requiresOperatorFullRelearning =
                RequiresOperatorFullRelearning(faultCode, reason);
            if (requiresOperatorFullRelearning)
                _operatorFullRelearningRequired[channel] = reason ?? faultCode;
            if (recoveryPolicy == FaultRecoveryPolicy.Recoverable)
            {
                _nonRecoverableChannelFaultLatch.TryRemove(channel, out _);
                _nonRecoverableChannelFaultReasons.TryRemove(channel, out _);
            }
            else
            {
                _nonRecoverableChannelFaultLatch[channel] = 0;
                _nonRecoverableChannelFaultReasons[channel] = reason ?? faultCode;
            }

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

            _currentCycleNumberByChannel.TryGetValue(channel, out var activeCycleNumber);
            _frozenFaultCycleByChannel.TryGetValue(channel, out var existingFrozenCycleNumber);
            var alarmCycleNumber = ResolveAlarmSnapshotCycle(
                confirmedTerminalCycleNumber,
                activeCycleNumber,
                existingFrozenCycleNumber);

            // ★同步去重 latch：保证计时器回调能尽快识别“本圈应封为 alarm”，但不在此线程做 IO
            if (!_alarmStopLatch.TryRequestStop(channel))
                return;

            // 只有取得本次报警停机所有权后才发布冻结圈，避免重复/迟到报警覆盖首发证据。
            if (alarmCycleNumber != 0)
                _frozenFaultCycleByChannel[channel] = alarmCycleNumber;

            // 触发相证据必须先于取消、DO OFF、电源OFF以及后置尾巴等待。
            // 否则导出的“现场”只会描述已经停机后的终态。
            var alarmUtc = DateTime.UtcNow;
            var alarmTriggerDiagnostics = CaptureAlarmLiveDiagnostics(
                channel,
                alarmUtc);

            // 通道硬故障只取消本通道。共享压力/电源/DAQ故障由各自组级处理器扩大范围。
            try { CancelStopCts(channel); } catch { }

            if (requiresOperatorFullRelearning)
            {
                // 先撤去输出再做同步项目持久化，避免磁盘延迟扩大带电窗口。
                try { CommandEpbOffHighPriority(channel, "OperatorFullRelearningLatch"); }
                catch { }
                PersistOperatorFullRelearningState(
                    channel,
                    required: true,
                    reason,
                    alarmUtc,
                    channelFaultCorrelationId);
            }
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
            FlushPersistentLog(true);
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
                    snapshotEvidence = await ExportAlarmSnapshotAsync(
                            channel,
                            reason,
                            alarmUtc,
                            alarmTriggerDiagnostics)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                if (snapshotEvidence == null)
                    TryFinalizeCurrentCycleAfterSnapshot(channel, false);
                else
                    CommitCycleAttemptAfterSnapshotEvidence(channel, snapshotEvidence);

                await EnsureLatestStopSnapshotAsync(
                        channel,
                        alarmCycleNumber,
                        "AlarmStop")
                    .ConfigureAwait(false);

                if (recoveryPolicy == FaultRecoveryPolicy.NonRecoverableDisableChannel)
                    PersistentlyDisableChannel(
                        channel,
                        faultCode,
                        reason,
                        channelFaultCorrelationId);

                if (recoveryPolicy == FaultRecoveryPolicy.Recoverable)
                    BeginRecoverableChannelRestartLoop(
                        channel,
                        faultCode,
                        reason,
                        channelFaultCorrelationId);
            }), "ChannelAlarmHandling", channel);
        }

        internal static int ResolveAlarmSnapshotCycle(
            int confirmedTerminalCycleNumber,
            int activeCycleNumber,
            int frozenCycleNumber)
        {
            if (confirmedTerminalCycleNumber != 0) return confirmedTerminalCycleNumber;
            if (activeCycleNumber != 0) return activeCycleNumber;
            return frozenCycleNumber;
        }

        private bool TryFinalizeManualStopCycle(int channel, int interruptedCycleNumber)
        {
            if (interruptedCycleNumber == 0) return true;
            if (!_currentCycleNumberByChannel.TryGetValue(channel, out var current) ||
                current != interruptedCycleNumber)
                return true;

            var recorder = Recorder;
            if (recorder == null)
            {
                _log.Error(
                    $"EPB[{channel}] 人工停止无法提交活动圈终态：Recorder不可用。" +
                    $"Cycle={interruptedCycleNumber}",
                    "落盘");
                return false;
            }

            try
            {
                if (TryGetCycleAttempt(channel, interruptedCycleNumber, out var context))
                {
                    context.CancelAttempt();
                    var committed = AbortFormalCycleAttempt(
                        context,
                        recorder,
                        DateTime.UtcNow,
                        "canceled");
                    return committed || context.IsDurablyCommitted;
                }

                AbortCycleAfterPersistence(
                    recorder,
                    channel,
                    interruptedCycleNumber,
                    DateTime.UtcNow,
                    "canceled");
                ((ICollection<KeyValuePair<int, int>>)_currentCycleNumberByChannel)
                    .Remove(new KeyValuePair<int, int>(channel, interruptedCycleNumber));
                _currentAttemptIdByChannel.TryRemove(channel, out _);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"EPB[{channel}] 人工停止活动圈终态提交失败。" +
                    $"Cycle={interruptedCycleNumber} Error={ex.GetBaseException().Message}",
                    "落盘",
                    ex);
                return false;
            }
        }

        private async Task<bool> EnsureLatestStopSnapshotAsync(
            int channel,
            int requiredTerminalCycleNumber,
            string reason)
        {
            var recorder = Recorder;
            if (recorder == null)
            {
                _log.Error(
                    $"EPB[{channel}] 停机最近快照无法生成：Recorder不可用。Reason={reason}",
                    "落盘");
                return false;
            }

            Exception lastError = null;
            var deadline = Stopwatch.GetTimestamp() + 10L * Stopwatch.Frequency;
            do
            {
                try
                {
                    if (requiredTerminalCycleNumber > 0 &&
                        recorder is IStopRecentCycleEvidenceExporter stopExporter)
                    {
                        stopExporter.FlushRecentForStop(
                            channel,
                            10,
                            requiredTerminalCycleNumber);
                    }
                    else
                    {
                        recorder.FlushRecent(channel, 10);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (Stopwatch.GetTimestamp() >= deadline) break;
                    await Task.Delay(200).ConfigureAwait(false);
                }
            } while (Stopwatch.GetTimestamp() < deadline);

            _log.Error(
                $"EPB[{channel}] 停机最近快照未能包含要求的终态圈。" +
                $"RequiredCycle={requiredTerminalCycleNumber} Reason={reason} " +
                $"Error={lastError?.GetBaseException().Message}",
                "落盘",
                lastError);
            return false;
        }

        private void BeginRecoverableChannelRestartLoop(
            int channel,
            string faultCode,
            string reason,
            Guid correlationId)
        {
            if (RequiresOperatorFullRelearning(faultCode, reason) ||
                _operatorFullRelearningRequired.ContainsKey(channel))
            {
                _log.Warn(
                    $"EPB[{channel}] 故障 {faultCode} 已锁存为人工完整重学习；" +
                    "禁止无人值守自动调用报警恢复入口，输出保持OFF。",
                    "报警");
                return;
            }
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

        internal static bool RequiresOperatorFullRelearning(string faultCode, string reason)
        {
            return string.Equals(
                       faultCode,
                       "ForwardUnderTargetHighLoadStall",
                       StringComparison.OrdinalIgnoreCase) ||
                   (reason?.IndexOf(
                        "ForwardUnderTargetHighLoadStall",
                        StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        public bool RequiresOperatorFullRelearning(int channel, out string reason)
        {
            return _operatorFullRelearningRequired.TryGetValue(channel, out reason);
        }

        private bool PersistOperatorFullRelearningState(
            int channel,
            bool required,
            string reason,
            DateTime utc,
            Guid correlationId)
        {
            try
            {
                var test = _cfg?.Test ??
                           throw new InvalidOperationException("当前项目配置未加载");
                EpbAlarmPersistenceUpdate update;
                lock (test)
                {
                    test.EnsureEpbRecords(12);
                    var record = test.GetEpbRecord(channel);
                    if (required)
                        record.RequireOperatorFullRelearning(reason, utc, correlationId);
                    update = new EpbAlarmPersistenceUpdate
                    {
                        Channel = channel,
                        PermanentAlarmLatched = record.PermanentAlarmLatched,
                        PermanentAlarmCode = record.PermanentAlarmCode,
                        PermanentAlarmReason = record.PermanentAlarmReason,
                        PermanentAlarmUtc = record.PermanentAlarmUtc,
                        PermanentAlarmCorrelationId = record.PermanentAlarmCorrelationId,
                        OperatorFullRelearningRequired = required,
                        OperatorFullRelearningReason = required
                            ? reason ?? string.Empty
                            : string.Empty,
                        OperatorFullRelearningUtc = required ? utc : null,
                        OperatorFullRelearningCorrelationId = required
                            ? correlationId
                            : Guid.Empty,
                        ConsecutivePeriodOverrunCount =
                            record.ConsecutivePeriodOverrunCount,
                        LastPeriodOverrunUtc = record.LastPeriodOverrunUtc
                    };
                }

                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    test.StoreDir,
                    test.TestName);
                if (string.IsNullOrWhiteSpace(projectPath) ||
                    !System.IO.File.Exists(projectPath))
                    throw new System.IO.FileNotFoundException(
                        "当前项目专用 TestConfig.xml 不存在，禁止回退修改默认模板。",
                        projectPath);
                ConfigLoader.UpdateTestEpbAlarmState(projectPath, new[] { update });

                lock (test)
                {
                    var record = test.GetEpbRecord(channel);
                    if (required)
                        record.RequireOperatorFullRelearning(reason, utc, correlationId);
                    else
                        record.ClearOperatorFullRelearning();
                }
                if (required)
                    _operatorFullRelearningRequired[channel] = reason ?? string.Empty;
                else
                    _operatorFullRelearningRequired.TryRemove(channel, out _);
                _disablePersistenceFailureReasons.TryRemove(channel, out _);
                _log.Info(
                    $"EPB[{channel}] 人工完整重学习门禁已{(required ? "锁存" : "清除")}并原子持久化。" +
                    $"Path={projectPath} CorrelationId={correlationId:N}",
                    "报警");
                FlushPersistentLog(true);
                return true;
            }
            catch (Exception ex)
            {
                var message =
                    $"EPB[{channel}] 人工完整重学习门禁{(required ? "锁存" : "清除")}持久化失败；" +
                    (required
                        ? "本进程保持锁存，重启前禁止继续试验。"
                        : "保持锁存和输出OFF，禁止正式重入。") +
                    "原因：" + ex.GetBaseException().Message;
                if (required)
                    _operatorFullRelearningRequired[channel] = reason ?? string.Empty;
                _disablePersistenceFailureReasons[channel] = message;
                _nonRecoverableChannelFaultLatch[channel] = 0;
                _nonRecoverableChannelFaultReasons[channel] = message;
                _log.Error(message, "报警", ex);
                FlushPersistentLog(true);
                return false;
            }
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
                    "OpenCircuitOrOutputFault",
                    StringComparison.OrdinalIgnoreCase))
                return FaultRecoveryPolicy.CurrentRunDisableChannel;

            if (string.Equals(
                    faultCode,
                    "ForwardLoadRiseNotStarted",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    faultCode,
                    "ForwardLowPlateauConfirmed",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    faultCode,
                    "ForwardPeakOvershoot2AConfirmed",
                    StringComparison.OrdinalIgnoreCase))
                return FaultRecoveryPolicy.CurrentRunDisableChannel;

            return FaultRecoveryPolicy.Recoverable;
        }

        private void PersistentlyDisableChannel(
            int channel,
            string code,
            string reason,
            Guid correlationId)
        {
            PersistentlyDisableChannels(new[] { channel }, code, reason, correlationId);
        }

        private bool PersistentlyDisableChannels(
            IEnumerable<int> requestedChannels,
            string code,
            string reason,
            Guid correlationId)
        {
            var channels = (requestedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (channels.Length == 0) return true;
            var alarmUtc = DateTime.UtcNow;
            if (correlationId == Guid.Empty) correlationId = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(code)) code = "PermanentAlarm";
            try
            {
                var test = _cfg?.Test ??
                           throw new InvalidOperationException("当前项目配置未加载");
                lock (test)
                {
                    test.EnsureEpbRecords(12);
                    foreach (var channel in channels)
                    {
                        var record = test.GetEpbRecord(channel);
                        record.LatchPermanentAlarm(code, reason, alarmUtc, correlationId);
                        _periodOverrunStreaks[channel] = Math.Max(
                            0,
                            record.ConsecutivePeriodOverrunCount);
                    }
                }

                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    test.StoreDir,
                    test.TestName);
                if (string.IsNullOrWhiteSpace(projectPath) || !System.IO.File.Exists(projectPath))
                    throw new System.IO.FileNotFoundException(
                        "当前项目专用 TestConfig.xml 不存在，禁止回退修改默认模板。",
                        projectPath);

                // 共享故障cohort一次原子替换，进程在任意时刻退出都不会留下
                // “只禁用了一半通道”的项目配置。
                ConfigLoader.UpdateTestEpbAlarmState(
                    projectPath,
                    channels.Select(channel =>
                    {
                        var record = test.GetEpbRecord(channel);
                        return new EpbAlarmPersistenceUpdate
                        {
                            Channel = channel,
                            Enabled = false,
                            PermanentAlarmLatched = true,
                            PermanentAlarmCode = code,
                            PermanentAlarmReason = reason ?? string.Empty,
                            PermanentAlarmUtc = alarmUtc,
                            PermanentAlarmCorrelationId = correlationId,
                            ConsecutivePeriodOverrunCount = record.ConsecutivePeriodOverrunCount,
                            LastPeriodOverrunUtc = record.LastPeriodOverrunUtc
                        };
                    }));
                foreach (var channel in channels)
                {
                    _disablePersistenceFailureReasons.TryRemove(channel, out _);
                    NonCriticalObserver.Invoke(
                        ChannelDisableRequested,
                        channel,
                        reason,
                        ex => _log?.Warn(
                            $"EPB[{channel}] 持久禁用UI观察者异常已隔离：{ex.Message}",
                            "报警"));
                }
                _log.Error(
                    $"EPB[{string.Join(",", channels)}] 已锁存不可自恢复报警并原子持久禁用当前项目通道。" +
                    $"Enabled=false Path={projectPath} Reason={reason}",
                    "报警");
                FlushPersistentLog(true);
                return true;
            }
            catch (Exception ex)
            {
                var message =
                    $"卡钳[{string.Join(",", channels)}]已停机，但项目禁用状态持久化失败；" +
                    "本进程保持禁用，重启前必须阻止自动恢复。原因：" + ex.Message;
                _log.Error(message, "报警", ex);
                FlushPersistentLog(true);
                foreach (var channel in channels)
                {
                    _disablePersistenceFailureReasons[channel] = message;
                    // 磁盘失败也要立即同步UI；另发高可见失败，禁止把“UI未勾选”
                    // 误认为已经耐久保存。
                    NonCriticalObserver.Invoke(
                        ChannelDisableRequested,
                        channel,
                        reason,
                        observerEx => _log?.Warn(
                            $"EPB[{channel}] 禁用UI观察者异常已隔离：{observerEx.Message}",
                            "报警"));
                    NonCriticalObserver.Invoke(
                        ChannelDisablePersistenceFailed,
                        channel,
                        message,
                        observerEx => _log?.Warn(
                            $"EPB[{channel}] 禁用持久化失败提示观察者异常：{observerEx.Message}",
                            "报警"));
                }
                return false;
            }
        }

        private string ResolveInfrastructureHardwareStartFailureCode(
            int channel,
            string durablyIsolatedCode)
        {
            return _disablePersistenceFailureReasons.ContainsKey(channel)
                ? "InfrastructureHardwareIsolationPersistenceFailed"
                : durablyIsolatedCode;
        }

        private string AppendInfrastructureDisablePersistenceFailure(
            int channel,
            string reason)
        {
            return _disablePersistenceFailureReasons.TryGetValue(channel, out var failure)
                ? (reason ?? string.Empty) + "; " + failure
                : reason ?? string.Empty;
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
            var warning = new AdaptiveWarningEvent
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
            };
            OnRunnerWarningOverlayRaised(warning);
            OnRunnerWarningEvidenceRaised(warning);
            NonCriticalObserver.Invoke(
                ChannelWarningRaised,
                channel,
                $"{faultCode} 连续={confirmation.Streak}/{confirmation.ConfirmThreshold}；" +
                "已安全断电，本圈作废并自动重试。",
                ex => _log?.Warn($"EPB[{channel}] 预警观察者异常已隔离：{ex.Message}", "EPB"));
            if (!TryEnsureSoftwareRecoveryOutputOff(channel, "ConfirmedFaultWarning")) return;

            var currentLifecycle = _channelRuntimeStateStore.Get(channel);
            if (currentLifecycle?.State == ChannelRuntimeState.Recovering)
            {
                _log?.Info(
                    $"RecoverableWarningDelegatedToLifecycleOwner EPB={channel} " +
                    $"State={currentLifecycle.State} " +
                    $"Owner={currentLifecycle.RecoveryOwnerKind} " +
                    $"Target={currentLifecycle.RecoveryTargetPhase} Code={faultCode}",
                    "EPB");
                return;
            }

            var recoveryRunId = currentLifecycle != null &&
                                currentLifecycle.RunId != Guid.Empty
                ? currentLifecycle.RunId
                : _activeBatchId;
            var recoveryRunEpoch = currentLifecycle != null &&
                                   currentLifecycle.RunEpoch > 0
                ? currentLifecycle.RunEpoch
                : Interlocked.Read(ref _runEpoch);
            var restartCancellation = new CancellationTokenSource();
            if (!_recoverableChannelRestartJobs.TryAdd(channel, restartCancellation))
            {
                restartCancellation.Dispose();
                _log?.Info(
                    $"RecoverableWarningAlreadyOwned EPB={channel} Code={faultCode}",
                    "EPB");
                return;
            }

            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    var token = restartCancellation.Token;
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
                        $"EPB[{channel}] 可恢复故障未达到报警门槛，已安全断电并由恢复Owner封闭当前尝试圈。" +
                        $"Code={faultCode} Streak={confirmation.Streak}/{confirmation.ConfirmThreshold}",
                        "EPB");

                    await Task.WhenAll(pauseCompletion, releaseTask).ConfigureAwait(false);
                    if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                            cutoffCycles,
                            cutoffUtc,
                            $"RecoverableWarning:{faultCode}",
                            _daqPersistenceRecoveryTimeoutMs,
                            token)
                        .ConfigureAwait(false))
                        throw new SoftwareSelfHealingRetryException(
                            "警告圈截止 Raw/耐久边界尚未闭合；保持停机并转入持续恢复。");
                    token.ThrowIfCancellationRequested();
                    if (!_timers.ContainsKey(channel) ||
                        IsAlarmStopRequested(channel) ||
                        _channelPausedUtc.ContainsKey(channel) ||
                        ShouldHoldDaqRecoveredChannelsForBatchPause(CurrentBatchPauseState))
                        return;
                    currentLifecycle = _channelRuntimeStateStore.Get(channel);
                    if (!CanRouteRecoverableWarningToFormalRejoin(
                            currentLifecycle,
                            IsFormalPhaseCommitted))
                    {
                        _log?.Info(
                            $"RecoverableWarningDelegatedToLifecycleOwner EPB={channel} " +
                            $"State={currentLifecycle?.State} " +
                            $"Owner={currentLifecycle?.RecoveryOwnerKind} " +
                            $"Target={currentLifecycle?.RecoveryTargetPhase} " +
                            $"Code={faultCode}",
                            "EPB");
                        return;
                    }
                    var plan = GetCompatibleStaggerPlan(new[] { channel });
                    RejoinFormalChannelsAtSharedFutureSlot(
                        new[] { channel },
                        plan,
                        "RecoverableWarningSelfHealed",
                        "警告圈已安全断电并完成机械释放，按未来完整节律槽自动重试",
                        allowTerminalReset: false);
                };
            }

            if (!TryBeginRecoveryIncident(
                    "RecoverableWarningRetry",
                    recoveryRunId,
                    recoveryRunEpoch,
                    RecoveryOwnerKind.FormalTimer,
                    RecoveryTargetPhase.Formal,
                    correlationId,
                    new[] { channel },
                    _ => BuildRecoveryWorker(),
                    contract => PublishRecoveryIncidentState(
                        channel,
                        ChannelRuntimeState.Recovering,
                        "RecoverableWarningRecovery",
                        $"{faultCode} 单次观察已断电；恢复Owner正在封存本圈并安排受监管重试。",
                        affectedChannels: contract.Channels,
                        correlationId: contract.IncidentId,
                        allowTerminalReset: true,
                        recoveryOwnerKind: contract.OwnerKind,
                        recoveryTargetPhase: contract.TargetPhase,
                        recoveryOwnerId: contract.OwnerId,
                        recoveryOwnerGeneration: contract.RunEpoch),
                    out recoveryIncident,
                    correlationId))
            {
                _recoverableChannelRestartJobs.TryRemove(channel, out _);
                restartCancellation.Dispose();
                return;
            }

            async Task ObserveRecoveryAsync()
            {
                try
                {
                    if (!recoveryIncident.Start())
                        throw new InvalidOperationException(
                            $"EPB[{channel}] 可恢复预警Owner启动许可被拒绝。");
                    await recoveryIncident.WorkerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (restartCancellation.IsCancellationRequested)
                {
                    _log?.Info(
                        $"EPB[{channel}] 可恢复预警重试已由停止流程取消。Code={faultCode}",
                        "EPB");
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
                finally
                {
                    recoveryIncident.CompleteAfterTerminal(contract =>
                        CommitRecoveryIncidentStateForRelease(
                            contract,
                            "RecoverableWarningRecoveryCommitted",
                            "可恢复预警Owner已完成重入或安全终态提交。"));
                    _recoverableChannelRestartJobs.TryRemove(channel, out _);
                    restartCancellation.Dispose();
                }
            }

            _taskSupervisor.Observe(
                ObserveRecoveryAsync(),
                "RecoverableWarningRetry",
                _activeBatchId,
                channel);
        }

        internal static bool CanRouteRecoverableWarningToFormalRejoin(
            ChannelRuntimeStateChangedEvent lifecycle,
            bool formalPhaseCommitted)
        {
            if (!formalPhaseCommitted || lifecycle == null ||
                !lifecycle.FormalPhaseCommitted)
                return false;
            return lifecycle.State == ChannelRuntimeState.Running ||
                   lifecycle.State == ChannelRuntimeState.WarningRunning ||
                   (lifecycle.State == ChannelRuntimeState.Recovering &&
                    lifecycle.RecoveryOwnerKind == RecoveryOwnerKind.FormalTimer &&
                    lifecycle.RecoveryTargetPhase == RecoveryTargetPhase.Formal &&
                    RecoveryOwnershipPolicy.IsOwnerCurrent(lifecycle));
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
            double staleThresholdMs = 250)
        {
            if (freshness == null) return "DaqSampleStale";
            var threshold = Math.Max(1, staleThresholdMs);
            if (freshness.CallbackAgeMs > threshold) return "DaqCallbackStale";
            if (freshness.ControlEnqueueAgeMs > threshold) return "ControlEnqueueStale";
            if (freshness.ControlProcessedAgeMs > threshold) return "ControlProcessingStale";
            return "DaqSampleStale";
        }

        internal static bool IsRunnerDaqFreshnessSafetyCutoff(string reason)
        {
            return (reason ?? string.Empty).IndexOf(
                       "DaqSampleStale",
                       StringComparison.OrdinalIgnoreCase) >= 0;
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
            if (IsRunnerDaqFreshnessSafetyCutoff(faultCode))
            {
                var freshness = _acq.GetDaqFreshnessSnapshot(
                    device,
                    _daqLivenessWarnThresholdMs);
                faultCode = ClassifyDaqStaleRoot(
                    freshness,
                    _daqLivenessWarnThresholdMs);
                faultReason =
                    $"DaqFreshnessSafetyCutoff>{_daqLivenessWarnThresholdMs:0.#}ms " +
                    $"Root={faultCode} " +
                    $"CallbackAge={freshness?.CallbackAgeMs ?? double.PositiveInfinity:F1}ms " +
                    $"ControlEnqueueAge={freshness?.ControlEnqueueAgeMs ?? double.PositiveInfinity:F1}ms " +
                    $"ControlProcessedAge={freshness?.ControlProcessedAgeMs ?? double.PositiveInfinity:F1}ms " +
                    $"ProcessedSampleUtc={(freshness == null || freshness.ProcessedSampleUtc == default ? "none" : freshness.ProcessedSampleUtc.ToString("O"))} " +
                    $"Original={reason}";
                var generation = Math.Max(0, freshness?.Generation ?? 0);
                _daqFreshnessEscalationWatchGeneration[device] = generation;
                if (_currentCycleNumberByChannel.TryGetValue(channel, out var cutoffCycle))
                {
                    MarkDaqClockCycleAborted(
                        _activeBatchId,
                        Interlocked.Read(ref _runEpoch),
                        channel,
                        cutoffCycle);
                    _daqRecoveredGapAbortedCycles[DaqAbortedCycleKey(
                        _activeBatchId,
                        Interlocked.Read(ref _runEpoch),
                        channel,
                        cutoffCycle)] = 0;
                }
                _log.Warn(
                    $"FieldMetric DAQ_FRESHNESS_SAFETY_CUTOFF Device={device} EPB={channel} " +
                    $"Generation={generation} Action=MotorOffAndAbortCurrentCycle;" +
                    $"DaqRestart=false;EscalationOwner=DaqLivenessSupervisor Detail={faultReason}",
                    "FIELD");
                NonCriticalObserver.Invoke(
                    ChannelWarningRaised,
                    channel,
                    $"{faultCode}：已立即断电并作废本圈；仅当独立存活监督达到" +
                    $"{_daqLivenessTripThresholdMs:F0}ms×3 才重建DAQ。",
                    ex => _log?.Warn($"DAQ安全切断预警观察者异常已隔离：{ex.Message}", "AI"));
                return;
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
                var warning = new AdaptiveWarningEvent
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
                };
                OnRunnerWarningOverlayRaised(warning);
                OnRunnerWarningEvidenceRaised(warning);
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
            ObserveNonRecoveryLifecycleTask(BeginDaqAutoRecoveryAsync(
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
            => PublishDaqRecoveryIncident(deviceFault, Guid.Empty, fromSafetyEvent: false);

        private void OnDaqDeviceFaultDetected(
            DaqDeviceFault deviceFault,
            Guid batchCorrelationId)
            => PublishDaqRecoveryIncident(deviceFault, batchCorrelationId, fromSafetyEvent: false);

        private void OnDaqDeviceFaultSafetyDetected(DaqDeviceFault deviceFault)
            => PublishDaqRecoveryIncident(deviceFault, Guid.Empty, fromSafetyEvent: true);

        private void OnDaqDeviceFaultSafetyDetected(
            DaqDeviceFault deviceFault,
            Guid batchCorrelationId)
            => PublishDaqRecoveryIncident(deviceFault, batchCorrelationId, fromSafetyEvent: true);

        /// <summary>
        /// 唯一的 DAQ 事故发布入口。采集器可能先同步发布安全事件、随后异步发布
        /// publication；独立 liveness 扫描也可能在二者之间发现同一代次。事故锁存的
        /// IsFirst 是幂等提交点：只有首个事件登记恢复次数并创建上下文，后续事件只
        /// 合并诊断，不能重复消耗恢复预算或生成另一套安全动作。
        /// </summary>
        private void PublishDaqRecoveryIncident(
            DaqDeviceFault deviceFault,
            Guid batchCorrelationId,
            bool fromSafetyEvent)
        {
            if (deviceFault == null || string.IsNullOrWhiteSpace(deviceFault.Device)) return;
            if (batchCorrelationId != Guid.Empty)
            {
                var sharedKey = $"{_activeBatchId:N}:{Interlocked.Read(ref _runEpoch)}:{batchCorrelationId:N}";
                _sharedDaqIncidentCorrelations.TryAdd(sharedKey, 0);
            }
            var affected = GetDaqGroupChannels(deviceFault.Device);
            if (affected.Length == 0)
                affected = GetAllDaqDeviceChannels(deviceFault.Device);
            if (affected.Length == 0) return;

            // 若首个事件已将上下文提交到当前 Run，任何重复 event 都不再进行观察、
            // 尝试次数注册或 Begin；检查放在 Observe 前可避免无谓日志副作用。
            if (_daqAutoRecovery.TryGetValue(deviceFault.Device, out var existingRecovery) &&
                existingRecovery != null &&
                existingRecovery.RunId == _activeBatchId &&
                existingRecovery.RunEpoch == Interlocked.Read(ref _runEpoch) &&
                existingRecovery.Terminal.Current == DaqRecoveryTerminal.None)
            {
                _log.Info(
                    $"DaqDeviceFaultDuplicateSuppressed Device={deviceFault.Device} " +
                    $"Generation={deviceFault.Generation} RunId={_activeBatchId:N} " +
                    $"RunEpoch={Interlocked.Read(ref _runEpoch)} " +
                    $"CorrelationId={existingRecovery.CorrelationId:N} " +
                    $"Source={(fromSafetyEvent ? "Safety" : "Publication")}",
                    "AI");
                return;
            }

            if (TrySuppressDaqIncidentGrace(deviceFault))
                return;

            DaqIncidentObservation observation;
            try
            {
                observation = ObserveDaqIncident(
                    deviceFault.Device,
                    deviceFault.Code,
                    deviceFault.Reason,
                    deviceFault.TimestampUtc,
                    affected,
                    deviceFault.Generation,
                    correlationId: batchCorrelationId);
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"DAQ故障无法登记事故身份，拒绝进入无主暂停。Device={deviceFault.Device} " +
                    $"Error={ex.Message}",
                    "AI",
                    ex);
                return;
            }

            // DaqIncidentLatch may reuse an already-created per-device
            // correlation (for example when a publication follows a safety
            // callback). Preserve the explicit shared-batch marker under that
            // effective correlation too, so storage/ExpectedDevices resolve to
            // the same shared root for both devices.
            if (batchCorrelationId != Guid.Empty &&
                observation.Context != null &&
                observation.Context.CorrelationId != Guid.Empty)
            {
                _sharedDaqIncidentCorrelations.TryAdd(
                    $"{_activeBatchId:N}:{Interlocked.Read(ref _runEpoch)}:" +
                    $"{observation.Context.CorrelationId:N}",
                    0);
            }

            if (!observation.IsFirst)
            {
                // 首个安全回调若在创建 RecoveryOwner 前就被异常中断，后到的
                // publication 仍须补建同一事故恢复；正常路径已存在 owner 时此
                // 分支只做诊断合并，不重复注册 attempt。
                if (!HasCurrentDaqRecoveryOwner(deviceFault.Device))
                {
                    ObserveNonRecoveryLifecycleTask(
                        BeginDaqAutoRecoveryAsync(
                            deviceFault.Device,
                            deviceFault.Code,
                            deviceFault.Reason,
                            observation.Context.CorrelationId,
                            restartDaq: true,
                            deviceFault.TimestampUtc),
                        "BeginDaqAutoRecoveryFromMergedFault",
                        observation.Context.PrimaryChannel);
                }
                _log.Info(
                    $"DaqDeviceFaultDuplicateMerged Device={deviceFault.Device} " +
                    $"Generation={deviceFault.Generation} RunId={_activeBatchId:N} " +
                    $"RunEpoch={Interlocked.Read(ref _runEpoch)} " +
                    $"CorrelationId={observation.Context.CorrelationId:N} " +
                    $"Source={(fromSafetyEvent ? "Safety" : "Publication")}",
                    "AI");
                return;
            }

            if (!_daqClockRecoveryAttempts.TryRegister(
                    deviceFault.Device,
                    Stopwatch.GetTimestamp(),
                    out var attempt))
            {
                // 频繁软件抖动仍不是卡钳硬件损坏证据，但也不能无限留在本进程。
                // 既有恢复上下文按第3次/60秒门槛转 UnattendedBatchRecycle；只有两份
                // 独立硬件探测证据才允许锁存DAQ硬件报警。
                _log.Warn(
                    $"Device={deviceFault.Device} {_daqClockRecoveryWindowMinutes}分钟内已发生" +
                    $"{attempt}次DAQ软件恢复，超过阈值{_daqClockRecoveryMaxAttempts}次；" +
                    "保持安全断能，并由有界恢复门槛转入整批回收。",
                    "AI");
            }

            // BeginDaqAutoRecoveryAsync 在第一次 await 前完成 TryAdd、RunEpoch 校验、
            // 冻结边界和所有安全动作，因此 RecoveryOwner 在任何可能阻塞的 DO/电源
            // I/O 之前已经可观察；安全事件和 publication 统一走此入口。
            ObserveNonRecoveryLifecycleTask(
                BeginDaqAutoRecoveryAsync(
                    deviceFault.Device,
                    deviceFault.Code,
                    deviceFault.Reason,
                    observation.Context.CorrelationId,
                    restartDaq: true,
                    deviceFault.TimestampUtc,
                    recoveryAttempt: attempt),
                fromSafetyEvent
                    ? "BeginDaqAutoRecoveryFromSafetyFault"
                    : "BeginDaqAutoRecoveryFromDeviceFault",
                observation.Context.PrimaryChannel);
        }

        private bool HasCurrentDaqRecoveryOwner(string device)
        {
            return !string.IsNullOrWhiteSpace(device) &&
                   _daqAutoRecovery.TryGetValue(device, out var context) &&
                   context != null &&
                   context.RunId == _activeBatchId &&
                   context.RunEpoch == Interlocked.Read(ref _runEpoch) &&
                   context.Terminal.Current == DaqRecoveryTerminal.None;
        }

        private bool TrySuppressDaqIncidentGrace(DaqDeviceFault deviceFault)
        {
            if (deviceFault == null || string.IsNullOrWhiteSpace(deviceFault.Device))
                return false;
            if (!_daqIncidentGrace.TryGetValue(deviceFault.Device.Trim(), out var grace) ||
                grace == null)
                return false;
            var now = Stopwatch.GetTimestamp();
            if (now > grace.ExpiresMonotonicTicks ||
                (grace.Generation > 0 && deviceFault.Generation > 0 &&
                 grace.Generation != deviceFault.Generation))
            {
                _daqIncidentGrace.TryRemove(deviceFault.Device.Trim(), out _);
                return false;
            }
            _log.Info(
                $"DaqIncidentTransientGraceSuppressed Device={deviceFault.Device} " +
                $"Generation={deviceFault.Generation} CorrelationId={grace.CorrelationId:N} " +
                "WindowMs=1200;SameIncident=true",
                "AI");
            return true;
        }

        private void OnDaqPersistenceStateChanged(DaqPersistenceStateChanged update)
        {
            if (update == null) return;
            // A DAQ/persistence callback is material evidence only when a StopAll
            // transaction is active.  The stop-progress recorder ignores ordinary
            // heartbeats and keeps the stage deadline unchanged.  A repeated
            // state callback must also carry a strictly advanced persisted or
            // terminal sequence; State/Code text alone is not evidence.
            var daqEvidenceVersion = new[]
                {
                    update.Sequence,
                    update.LastTerminallyHandledSequence,
                    update.SuppressThroughSequence,
                    update.LastSuppressedSequence
                }
                .Where(value => value > 0)
                .DefaultIfEmpty(0)
                .Max();
            if (daqEvidenceVersion > 0)
            {
                var device = string.IsNullOrWhiteSpace(update.Device)
                    ? "Unknown"
                    : update.Device.Trim();
                var generation = update.Generation > 0 ? update.Generation : 0;
                RecordStopSafetyMaterialProgress(
                    Interlocked.Read(ref _stopSafetyGeneration),
                    $"DaqPersistence:{device}:Generation={generation}",
                    daqEvidenceVersion,
                    $"DAQ持久化材料事件 State={update.State};Device={device};" +
                    $"Code={update.Code};Sequence={daqEvidenceVersion}");
            }
            if (update.State == DaqPersistenceState.Paused)
            {
                ObserveNonRecoveryLifecycleTask(BeginDaqAutoRecoveryAsync(
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

        public void SetRecoveryInfrastructureHealthProvider(Func<bool> provider)
        {
            _recoveryInfrastructureHealthProvider = provider ?? (() => false);
        }

        internal static bool RequiresDaqTaskRecreate(string triggerCode)
        {
            // Consumer/backlog faults can recover by dropping stale history only after the
            // affected group is safely de-energized。DaqCallbackStale 先复核当前 generation：
            // 现场短暂停顿后回调已自行恢复，强制 Stop/Start 反而扩大故障；若500ms内仍不
            // 新鲜，RecoverDeviceAsync 会自动进入真实 NI task 重建。时钟/生产者身份故障
            // 仍要求立即重建。
            return !string.Equals(triggerCode, "ActiveCycleDataLimitExceeded", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlProcessingStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlEnqueueStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "ControlQueueFull", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "BackgroundQueueFull", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "BackgroundProcessingStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "BackgroundWorkerFault", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "BackgroundBatchTransferFault", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "RawPersistenceTransferFault", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "RawPersistencePermanentFault", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqCallbackStale", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqClockModelInvalid", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqWallClockStep", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqPersistenceLag", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqPersistenceQueueFull", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(triggerCode, "DaqPersistenceWriteStall", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsDaqClockRecoveryTrigger(string triggerCode)
        {
            return string.Equals(
                       triggerCode,
                       "DaqClockModelInvalid",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static int GetDaqSelfMaintenanceDelayMs(int consecutiveFailures)
        {
            if (consecutiveFailures <= 1) return 1000;
            if (consecutiveFailures == 2) return 2000;
            if (consecutiveFailures == 3) return 5000;
            if (consecutiveFailures == 4) return 10000;
            return 30000;
        }

        /// <summary>
        /// 固化 DAQ 恢复进入任何所有权等待前的安全顺序。调用方可以把每一步拆成
        /// 整组操作，因此能够证明“全部暂停”早于“冻结边界”，“冻结+抑制”早于
        /// 当前圈取消和最高优先级 OFF 提交，而且电源 Disable/拒绝项兜底已经启动后，
        /// 必须在任何所有权 await 前撤销液压参与权并投递 lease release；最后才允许
        /// 日志、UI 或其它观察者运行。
        /// </summary>
        internal static void ExecuteDaqCutoffBeforeOwnershipWait(
            Action startWatchdogs,
            Action pauseAll,
            Action freezeCutoffAndSuppressTail,
            Action cancelCyclesAndSubmitOffAll,
            Action startPowerDisable = null,
            Action startRejectedOffFallbacks = null,
            Action revokeHydraulicAndStartRelease = null,
            Action publishRecoveringAndDiagnostics = null)
        {
            if (startWatchdogs == null) throw new ArgumentNullException(nameof(startWatchdogs));
            if (pauseAll == null)
                throw new ArgumentNullException(nameof(pauseAll));
            if (freezeCutoffAndSuppressTail == null)
                throw new ArgumentNullException(nameof(freezeCutoffAndSuppressTail));
            if (cancelCyclesAndSubmitOffAll == null)
                throw new ArgumentNullException(nameof(cancelCyclesAndSubmitOffAll));

            startWatchdogs();
            pauseAll();
            freezeCutoffAndSuppressTail();
            cancelCyclesAndSubmitOffAll();
            startPowerDisable?.Invoke();
            startRejectedOffFallbacks?.Invoke();
            revokeHydraulicAndStartRelease?.Invoke();
            publishRecoveringAndDiagnostics?.Invoke();
        }

        private async Task<bool> TryFinalizeDaqCutoffCyclesAfterPersistenceAsync(
            DaqAutoRecoveryContext context,
            int timeoutMs,
            CancellationToken token)
        {
            if (context == null || !IsCurrentRecovery(context)) return false;
            TaskCompletionSource<bool> completion;
            var ownsFinalization = false;
            lock (context.CutoffCyclesFinalizationGate)
            {
                if (Volatile.Read(ref context.CutoffCyclesFinalized) == 2) return true;
                completion = context.CutoffCyclesFinalizationCompletion;
                if (completion == null)
                {
                    completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    context.CutoffCyclesFinalizationCompletion = completion;
                    Volatile.Write(ref context.CutoffCyclesFinalized, 1);
                    ownsFinalization = true;
                }
            }

            // 后到恢复调用必须等待同一个收尾事务。旧实现看到 InProgress 就立即返回
            // false，上层会把“另一个所有者正在工作”误报成持久化失败并提前增加恢复次数。
            if (!ownsFinalization)
                return await completion.Task.ConfigureAwait(false);

            var succeeded = false;
            try
            {
                succeeded = await FinalizeDaqCutoffCyclesCoreAsync(
                        context,
                        timeoutMs,
                        token)
                    .ConfigureAwait(false);
                return succeeded;
            }
            finally
            {
                lock (context.CutoffCyclesFinalizationGate)
                {
                    Volatile.Write(ref context.CutoffCyclesFinalized, succeeded ? 2 : 0);
                    if (!succeeded)
                        context.CutoffCyclesFinalizationCompletion = null;
                    completion.TrySetResult(succeeded);
                }
            }
        }

        private async Task<bool> FinalizeDaqCutoffCyclesCoreAsync(
            DaqAutoRecoveryContext context,
            int timeoutMs,
            CancellationToken token)
        {
            // CutoffSnapshot 在首次恢复登记时已经冻结。这里只排该设备的
            // 冻结前缀；另一设备传 0，既不等待健康设备的新流量，也绝不重新抓取一个
            // 更大的 LastAccepted/Published 边界。
            var cutoff = context.CutoffSnapshot;
            if (cutoff == null)
            {
                MarkRecoveryBoundaryContradiction(context, "FrozenSnapshotMissing");
                return false;
            }
            var boundary = cutoff.FrozenBoundary;
            var capturedBoundaries = new Dictionary<string, CycleDaqBoundary>(
                StringComparer.OrdinalIgnoreCase)
            {
                [context.Device] = new CycleDaqBoundary(
                    context.Device,
                    context.PreviousGeneration,
                    boundary)
            };
            if (!TrySealSoftwareRecoveryCycleWindows(
                    context.CutoffCycles,
                    context.CutoffUtc,
                    $"DAQ:{context.TriggerCode}:FrozenBoundary",
                    () => IsCurrentRecovery(context),
                    capturedBoundaries))
                return false;

            var legacyBoundary = Interlocked.Read(ref context.CutoffPersistenceBoundary);
            if (legacyBoundary != boundary)
            {
                MarkRecoveryBoundaryContradiction(
                    context,
                    $"FrozenBoundaryChanged Snapshot={boundary} Context={legacyBoundary}");
                return false;
            }
            var suppression = _persistence.GetSnapshot(context.Device);
            if ((suppression.SuppressAfterSequence > 0 && suppression.SuppressAfterSequence != boundary) ||
                (suppression.FirstSuppressedSequence > 0 && suppression.FirstSuppressedSequence <= boundary))
            {
                MarkRecoveryBoundaryContradiction(
                    context,
                    $"SuppressionMismatch Frozen={boundary} SuppressAfter={suppression.SuppressAfterSequence} " +
                    $"FirstSuppressed={suppression.FirstSuppressedSequence}");
                return false;
            }
            var dev1Boundary = string.Equals(
                context.Device,
                "Dev1",
                StringComparison.OrdinalIgnoreCase)
                ? boundary
                : 0;
            var dev2Boundary = string.Equals(
                context.Device,
                "Dev2",
                StringComparison.OrdinalIgnoreCase)
                ? boundary
                : 0;
            var rawDrain = await _acq.DrainBackgroundPipelinesToBoundariesDetailedAsync(
                    dev1Boundary,
                    dev2Boundary,
                    Math.Max(1, timeoutMs),
                    token)
                .ConfigureAwait(false);
            if (!rawDrain.Completed || !IsCurrentRecovery(context))
            {
                var persistence = _persistence.GetSnapshot(context.Device);
                _log.Warn(
                    $"DAQ截止后台边界未闭合 Device={context.Device} Boundary={boundary} " +
                    $"Published={SelectDeviceValue(context.Device, rawDrain.Dev1Published, rawDrain.Dev2Published)} " +
                    $"RawTransferred={SelectDeviceValue(context.Device, rawDrain.Dev1RawTransferred, rawDrain.Dev2RawTransferred)} " +
                    $"Persisted={persistence.Sequence} Head={persistence.PendingHeadSequence} " +
                    $"InFlight={persistence.InFlightSequence} Pending={rawDrain.PendingPredicate}",
                    "落盘");
                return false;
            }

            var durablePrefix = await _persistence.WaitForDurablePrefixDetailedAsync(
                    context.Device,
                    boundary,
                    Math.Max(1, timeoutMs),
                    token).ConfigureAwait(false);
            if (!durablePrefix.Completed)
            {
                _log.Warn(
                    $"DAQ截止圈等待真实落盘边界超时 Device={context.Device} " +
                    $"Boundary={boundary} Persisted={durablePrefix.Persisted} " +
                    $"Head={durablePrefix.PendingHeadSequence} InFlight={durablePrefix.InFlightSequence} " +
                    $"DurabilityBlocked={durablePrefix.DurabilityBlocked} " +
                    $"Pending={durablePrefix.PendingPredicate}；不封圈、不重入。",
                    "落盘");
                return false;
            }
            if (!IsCurrentRecovery(context)) return false;

            var allFinalized = true;
            foreach (var pair in context.CutoffCycles ?? new Dictionary<int, int>())
            {
                if (!IsCurrentRecovery(context)) return false;
                if (pair.Value == 0) continue;
                var finalized = DiscardCurrentCycleForSoftwareRecovery(
                    pair.Key,
                    context.CutoffUtc,
                    $"DAQ:{context.TriggerCode}",
                    pair.Value,
                    durableBoundaryAlreadyConfirmed: true);
                if (!finalized &&
                    !_currentCycleNumberByChannel.ContainsKey(pair.Key))
                {
                    // 当前圈已经由其自身取消/失败收尾封存，无需再次作废。
                    finalized = true;
                }
                allFinalized &= finalized;
            }
            if (!allFinalized) return false;
            _log.Info(
                $"DAQ截止圈已在冻结耐久前缀后封存 Device={context.Device} " +
                $"Boundary={boundary} Cycles=[{string.Join(",", (context.CutoffCycles ?? new Dictionary<int, int>()).Values.Where(value => value != 0))}]。",
                "落盘");
            return true;
        }

        private void MarkRecoveryBoundaryContradiction(
            DaqAutoRecoveryContext context,
            string detail)
        {
            if (context == null) return;
            var contradictionReason = string.Empty;
            if (!TryCaptureFirstBoundaryContradiction(
                    context.ValidationFailureLogGate,
                    ref context.BoundaryContradiction,
                    ref context.BoundaryContradictionReason,
                    detail))
                return;
            contradictionReason = context.BoundaryContradictionReason;
            _log.Error(
                $"RecoveryBoundaryContradiction Device={context.Device} " +
                $"CorrelationId={context.CorrelationId:N} RunId={context.RunId:N} " +
                $"RunEpoch={context.RunEpoch} Detail={contradictionReason}；" +
                "事故圈保持作废语义，禁止扩大边界或继续同类重试。",
                "落盘");
        }

        internal static bool TryCaptureFirstBoundaryContradiction(
            object gate,
            ref int committed,
            ref string reason,
            string detail)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            lock (gate)
            {
                if (Volatile.Read(ref committed) != 0)
                    return false;
                reason = string.IsNullOrWhiteSpace(detail) ? "Unknown" : detail;
                Volatile.Write(ref committed, 1);
                return true;
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

            // A concurrent signal may arrive while the first incident is
            // still in its pre-await admission window.  It is a diagnostic
            // merge, not a second owner/worker.
            var effectiveCorrelationId = correlationId == Guid.Empty
                ? Guid.NewGuid()
                : correlationId;
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            lock (_recoveryContractGate)
            {
                if (_daqAutoRecovery.ContainsKey(device) ||
                    FindActiveRecoveryContractLocked(
                        affected[0],
                        runId,
                        runEpoch,
                        RecoveryOwnerKind.DaqRecovery,
                        IsFormalPhaseCommitted
                            ? RecoveryTargetPhase.Formal
                            : RecoveryTargetPhase.Startup,
                        effectiveCorrelationId) != null)
                    return;
            }

            RecoveryIncidentHandle incident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                    try
                    {
                        await BeginDaqAutoRecoveryBodyAsync(
                                device,
                                code,
                                reason,
                                effectiveCorrelationId,
                                restartDaq,
                                eventUtc,
                                recoveryAttempt,
                                raiseRecoverableAlarm,
                                recoverableAlarmChannel)
                            .ConfigureAwait(false);

                        // Self-maintenance may outlive the initial cutoff
                        // attempt.  Keep this real worker bound until the
                        // authoritative DAQ context reaches its terminal
                        // result, so the contract cannot disappear while a
                        // retry task is still changing lifecycle state.
                        if (_daqAutoRecovery.TryGetValue(device, out var context) &&
                            context.Terminal.Current == DaqRecoveryTerminal.None)
                            await context.Completion.Task.ConfigureAwait(false);
                    }
                    finally
                    {
                        incident?.CompleteAfterTerminal(contract =>
                            CommitRecoveryIncidentStateForRelease(
                                contract,
                                "DaqRecoveryTerminalWithoutRejoin",
                                "DAQ恢复执行体已结束；已完成终态复核并释放恢复合同。"));
                    }
                };
            }

            var started = TryBeginRecoveryIncident(
                "DaqAutoRecovery",
                runId,
                runEpoch,
                RecoveryOwnerKind.DaqRecovery,
                IsFormalPhaseCommitted
                    ? RecoveryTargetPhase.Formal
                    : RecoveryTargetPhase.Startup,
                effectiveCorrelationId,
                affected,
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    foreach (var channel in contract.Channels ?? Array.Empty<int>())
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            string.IsNullOrWhiteSpace(code) ? "DaqRecovery" : code,
                            reason ?? "DAQ自动恢复已建立真实执行任务。",
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                },
                out incident);
            if (!started || incident == null) return;

            try
            {
                _taskSupervisor.Observe(
                    incident.WorkerTask,
                    "DaqAutoRecovery",
                    _activeBatchId,
                    affected.FirstOrDefault());
                if (!incident.Start())
                {
                    incident.CompleteAfterTerminal(contract =>
                        PublishRecoverySafeTerminal(
                            contract,
                            "DaqRecoveryStartRejected",
                            "DAQ恢复启动许可被拒绝，已保持安全终态。"));
                    return;
                }
                await incident.WorkerTask.ConfigureAwait(false);
            }
            catch
            {
                incident?.CompleteAfterTerminal(contract =>
                    CommitRecoveryIncidentStateForRelease(
                        contract,
                        "DaqRecoveryWorkerFailed",
                        "DAQ恢复执行体异常，已完成终态复核并保持安全状态。"));
                throw;
            }
        }

        /// <summary>
        /// Creates the DAQ safety transaction used by the real recovery body.
        /// The port deliberately returns command receipts from the actual
        /// high-priority DO worker and routes its completion callback back into
        /// this same incident.  No UI/watchdog callback is allowed to infer a
        /// submitted or physically confirmed stage.
        /// </summary>
        private DaqRecoveryTransaction CreateDaqRecoveryTransaction(
            DaqAutoRecoveryContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return new DaqRecoveryTransaction(
                context.AffectedChannels,
                new DaqRecoveryTransaction.Port
                {
                    TrySubmitOff = channel =>
                    {
                        Guid commandId;
                        var accepted = TrySubmitEpbOffHighPriority(
                            channel,
                            telemetry =>
                            {
                                if (telemetry == null) return;
                                context.Transaction?.ReportPhysicalCompletion(
                                    telemetry.Channel,
                                    telemetry.CommandId,
                                    telemetry.Result);
                            },
                            out commandId);
                        return new DaqRecoveryTransaction.OffReceipt(
                            channel,
                            commandId,
                            accepted,
                            false,
                            accepted ? string.Empty : "DaqOffAdmissionRejected",
                            new SafetyOffReceipt
                            {
                                CommandId = commandId,
                                CorrelationId = context.CorrelationId,
                                TargetKind = SafetyOffTargetKind.DigitalOutput,
                                TargetId = channel,
                                RunEpoch = context.RunEpoch,
                                OperationGeneration = context.RecoveryEpoch,
                                SubmittedUtc = DateTime.UtcNow,
                                Status = accepted
                                    ? SafetyOffEvidenceStatus.Submitted
                                    : SafetyOffEvidenceStatus.Rejected,
                                EvidenceSource = "HighPriorityDoWorker",
                                Error = accepted ? string.Empty : "DaqOffAdmissionRejected"
                            });
                    },
                    PublishPhase = phase =>
                    {
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                phase,
                                "DaqRecoveryTransaction:" + phase))
                        {
                            // A duplicate callback is harmless when the
                            // controller already published this exact stage;
                            // an out-of-order callback is not.
                            if (context.Phase.Current != phase)
                                throw new InvalidOperationException(
                                    $"DAQ事务阶段发布失败 Phase={phase};" +
                                    $"Current={context.Phase.Current};" +
                                    $"Device={context.Device}");
                        }
                    },
                    SubmitSafetyOff = missing =>
                    {
                        var channels = (missing ?? Array.Empty<int>())
                            .Where(channel => channel >= 1 && channel <= 12)
                            .Distinct()
                            .OrderBy(channel => channel)
                            .ToArray();
                        if (channels.Length == 0) return;
                        var rejected = SubmitEpbOffHighPriorityBatch(
                            channels,
                            "DaqTransactionSafeIdleOffAdmissionRejected",
                            "DaqTransactionSafeIdleOffSubmissionException");
                        ScheduleRejectedOffFallbacks(
                            rejected,
                            "DaqTransactionSafeIdleImmediateOffFallback");
                    },
                    DisablePower = _ => StartDaqRecoveryPowerDisableOnce(context),
                    PublishTerminal = missing =>
                    {
                        var published = new List<int>();
                        // Normal recovery must close the channel-state barrier
                        // while the contract/context is still alive.  The old
                        // path returned the requested list unconditionally and
                        // let Terminal.TryCommit run before the state store had
                        // left Recovering, which made a failed terminal
                        // publication indistinguishable from a completed run.
                        if (context.Phase.Current == DaqRecoveryPhase.Committed)
                        {
                            var requested = (missing ?? Array.Empty<int>())
                                .Where(channel => channel >= 1 && channel <= 12)
                                .Distinct()
                                .OrderBy(channel => channel)
                                .ToArray();
                            lock (_recoveryContractGate)
                            {
                                foreach (var channel in requested)
                                {
                                    var state = _channelRuntimeStateStore.Get(channel);
                                    if (state == null ||
                                        state.RunId != context.RunId ||
                                        state.RunEpoch != context.RunEpoch)
                                        continue;
                                    if (state.State == ChannelRuntimeState.Recovering)
                                    {
                                        // This is an authoritative terminal
                                        // barrier publication, not the normal
                                        // public helper: that helper correctly
                                        // preserves Recovering while the DAQ
                                        // context is active.  Here the
                                        // transaction has already committed
                                        // rejoin, and the context remains the
                                        // owner until the later stable terminal
                                        // aggregate receipt succeeds.
                                        if (!IsFormalPhaseCommitted || !_timers.ContainsKey(channel) ||
                                            !_runners.ContainsKey(channel)) continue;
                                        var terminalState = state.Clone();
                                        terminalState.State = ChannelRuntimeState.Starting;
                                        terminalState.ReasonCode =
                                            "AwaitingVerifiedRecoveryCycle";
                                        terminalState.ReasonText =
                                            "采样与执行体已恢复，等待首个动作及有效数据提交。";
                                        terminalState.TimestampUtc = DateTime.UtcNow;
                                        terminalState.CorrelationId = context.CorrelationId;
                                        terminalState.RecoveryOwnerKind = RecoveryOwnerKind.None;
                                        terminalState.RecoveryOwnerId = Guid.Empty;
                                        terminalState.RecoveryOwnerGeneration = 0;
                                        terminalState.RecoveryTargetPhase = RecoveryTargetPhase.None;
                                        _channelRuntimeStateStore.Publish(
                                            terminalState,
                                            allowTerminalReset: true,
                                            allowSystemFaultReset: true);
                                    }
                                    var after = _channelRuntimeStateStore.Get(channel);
                                    if (after != null &&
                                        after.RunId == context.RunId &&
                                        after.RunEpoch == context.RunEpoch &&
                                        after.State != ChannelRuntimeState.Recovering)
                                        published.Add(channel);
                                }
                                PublishRecoveryAggregateOwnershipSourceLocked();
                            }
                            return published;
                        }
                        if (context.Phase.Current == DaqRecoveryPhase.SafeIdle)
                        {
                            var requested = (missing ?? Array.Empty<int>()).ToArray();
                            if (PublishDaqSafeTerminalStates(
                                    context,
                                    "DaqRecoveryTransactionSafeTerminal",
                                    SelectDaqRecoverySafeTerminalState(context),
                                    requested))
                                return requested;
                        }
                        foreach (var channel in missing ?? Array.Empty<int>())
                        {
                            var state = _channelRuntimeStateStore.Get(channel);
                            if (state != null &&
                                state.RunId == context.RunId &&
                                state.RunEpoch == context.RunEpoch &&
                                state.State != ChannelRuntimeState.Recovering)
                                published.Add(channel);
                        }
                        return published;
                    },
                    // The aggregate store will replace this publisher in the
                    // next component.  For now this is an explicit stable,
                    // monotonic in-memory production checkpoint rather than an
                    // implicit permission to delete the live recovery owner.
                    PublishCheckpoint = version =>
                    {
                        var checkpoint = PublishDaqAggregateCheckpoint(context);
                        return new DaqRecoveryTransaction.CheckpointReceipt(
                            checkpoint.Version,
                            checkpoint.Stable && checkpoint.Version >= version,
                            checkpoint.Detail);
                    },
                    PublishTerminalCheckpoint = version =>
                    {
                        var terminal = PublishDaqAggregateTerminal(context);
                        return new DaqRecoveryTransaction.CheckpointReceipt(
                            terminal.Version,
                            terminal.Stable && terminal.Version > version,
                            terminal.Detail);
                    }
                },
                context.RecoveryTransactionId);
        }

        /// <summary>
        /// Publishes one Controller-owned DAQ source revision.  The context is
        /// sampled under its progress gate, then the aggregate store performs
        /// the immutable source replacement under its own short lock.
        /// </summary>
        private RecoveryAggregateStore.DaqCheckpointReceipt
            PublishDaqAggregateCheckpoint(DaqAutoRecoveryContext context)
        {
            if (context == null)
                return new RecoveryAggregateStore.DaqCheckpointReceipt(0, false, "NullDaqContext");

            DaqRecoveryPhase stage;
            long progressVersion;
            DateTime stageStartedUtc;
            DateTime hardDeadlineUtc;
            lock (context.ProgressGate)
            {
                stage = context.ProgressStage;
                progressVersion = context.ProgressVersion;
                stageStartedUtc = context.StageStartedUtc;
                hardDeadlineUtc = context.HardDeadlineUtc;
            }
            var receipt = _recoveryAggregateStore.PublishDaqCheckpoint(
                _daqAutoRecovery.Count,
                stage.ToString(),
                progressVersion,
                stageStartedUtc,
                hardDeadlineUtc,
                stage == DaqRecoveryPhase.Committed
                    ? stage.ToString()
                    : null,
                stage == DaqRecoveryPhase.Committed ? progressVersion : 0,
                stage == DaqRecoveryPhase.Committed ? stageStartedUtc : default(DateTime),
                stage == DaqRecoveryPhase.Committed ? hardDeadlineUtc : default(DateTime),
                "DaqRecoveryCheckpoint",
                context.RunId,
                context.RunEpoch);
            lock (_recoveryContractGate)
                PublishRecoveryOperationalSourcesLocked();
            return receipt;
        }

        /// <summary>
        /// Retains the later terminal aggregate version.  It is called after
        /// the Controller has advanced the DAQ context to Terminal and before
        /// its owner/lease cleanup.
        /// </summary>
        private RecoveryAggregateStore.DaqCheckpointReceipt
            PublishDaqAggregateTerminal(DaqAutoRecoveryContext context)
        {
            if (context == null)
                return new RecoveryAggregateStore.DaqCheckpointReceipt(0, false, "NullDaqContext");

            DaqRecoveryPhase stage;
            long progressVersion;
            DateTime stageStartedUtc;
            DateTime hardDeadlineUtc;
            lock (context.ProgressGate)
            {
                stage = context.ProgressStage;
                progressVersion = context.ProgressVersion;
                stageStartedUtc = context.StageStartedUtc;
                hardDeadlineUtc = context.HardDeadlineUtc;
            }
            var receipt = _recoveryAggregateStore.PublishDaqTerminal(
                stage.ToString(),
                progressVersion,
                stageStartedUtc,
                hardDeadlineUtc,
                "DaqRecoveryTerminal",
                context.RunId,
                context.RunEpoch);
            lock (_recoveryContractGate)
                PublishRecoveryOperationalSourcesLocked();
            return receipt;
        }

        private void StartDaqRecoveryPowerDisableOnce(DaqAutoRecoveryContext context)
        {
            if (context == null || _powerSupply == null) return;
            if (Interlocked.CompareExchange(
                    ref context.PowerDisableStartedTicks,
                    Stopwatch.GetTimestamp(),
                    0) != 0)
                return;
            var powerDisableTasks = StartElectricalGroupSafetyDisables(
                context.AffectedChannels,
                $"DAQ截止安全断电 Device={context.Device} " +
                    $"CorrelationId={context.CorrelationId:N}",
                "DaqCutoffPowerDisable");
            context.PowerDisableTasks = powerDisableTasks.Values
                .Where(task => task != null)
                .ToArray();
            context.PowerDisableTasksByGroup = powerDisableTasks
                .Where(pair => pair.Key > 0 && pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            Interlocked.Exchange(
                ref context.PowerDisableStartedUtcTicks,
                DateTime.UtcNow.Ticks);
            StartDaqPowerDisableDeadline(context);
        }

        private async Task BeginDaqAutoRecoveryBodyAsync(
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
            var pauseAdmission = CaptureBatchPauseSnapshot();
            var context = new DaqAutoRecoveryContext
            {
                Device = device,
                RunId = _activeBatchId,
                CorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
                StartedUtc = DateTime.UtcNow,
                CutoffUtc = eventUtc == default ? DateTime.UtcNow : eventUtc.ToUniversalTime(),
                AdmissionBatchPauseState = pauseAdmission.State,
                AdmissionBatchPauseGeneration = pauseAdmission.Generation,
                AffectedChannels = affected,
                PreviouslyRunningChannels = affected
                    .Where(channel => IsChannelEnabled(channel) && IsFormalPhaseCommitted)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray(),
                PreviouslyActiveChannels = affected
                    .Where(channel =>
                        IsChannelEnabled(channel))
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
                RecoveryTransactionId = Guid.NewGuid(),
                BeforeClock = _acq.GetDaqFreshnessSnapshot(
                    device,
                    _daqLivenessWarnThresholdMs),
                PreviousGeneration = _acq.GetCurrentGeneration(device),
                CutoffParticipantVersions = affected.ToDictionary(
                    channel => channel,
                    CaptureHydraulicParticipantVersion),
                CutoffCycles = new Dictionary<int, int>(),
                CutoffPersistenceBoundary = 0
            };
            context.StageStartedUtc = context.StartedUtc;
            context.HardDeadlineUtc = context.StartedUtc.AddMilliseconds(
                Math.Max(1, RecoveryGroupHardDeadlineMs));
            context.ProgressStage = DaqRecoveryPhase.StaleDetected;
            context.Transaction = CreateDaqRecoveryTransaction(context);
            if (!_daqAutoRecovery.TryAdd(device, context)) return;
            AdvanceDaqRecoveryStage(
                context,
                DaqRecoveryPhase.StaleDetected,
                "DAQ回调陈旧/失联事故已登记。");
            // TryAdd 与 StopAll 的 run-epoch 撤权必须在同一提交门内复核。若 Stop 已经
            // 完成快照并递增 epoch，迟到上下文不得漏过 CancelAll 后再开始截止/取锁。
            var registrationCurrent = false;
            lock (_daqRecoveryCommitGate)
            {
                registrationCurrent = context.RunId == _activeBatchId &&
                                      context.RunEpoch == Interlocked.Read(ref _runEpoch) &&
                                      _daqAutoRecovery.TryGetValue(device, out var registered) &&
                                      ReferenceEquals(registered, context);
                if (registrationCurrent)
                {
                    // 只有真正取得该设备恢复所有权的请求才消耗 epoch；重复或迟到请求
                    // 不会制造“恢复代次前进但没有对应恢复任务”的假象。
                    context.RecoveryEpoch = Interlocked.Increment(ref _recoveryEpoch);
                }
            }
            if (!registrationCurrent)
            {
                CompleteCancelledRecovery(context, "DaqRecoveryRegistrationRunEpochChanged");
                return;
            }
            if (!context.Phase.BeginCutoff())
            {
                CompleteCancelledRecovery(context, "DaqCutoffPhaseRejected");
                return;
            }
            // 在第一次 Pause/DO/电源调用之前锁定当前圈的“事故身份”。这样即使 Runner
            // 的迟到完成回调先于持久化 Finalizer 返回，也只能走 AbortedBySoftwareRecovery
            // 分支，不能把事故圈晚到写成正式 completed。后续截止阶段只允许补充尚未
            // 观察到的当前圈，绝不替换已经冻结的 (RunId,RunEpoch,Channel,Cycle)。
            context.CutoffCycles = CaptureSoftwareRecoveryCycles(affected);
            MarkDaqRecoveryCyclesAborted(context);
            try
            {
                var cutoffSafetyDiagnostics = new List<string>();
                Dictionary<int, string> cutoffOffFallbacks = null;
                var cutoffOffSubmissionAccepted = false;
                var hydraulicReleaseTasks = new List<Task>(affected.Length);
                ExecuteDaqCutoffBeforeOwnershipWait(
                    () =>
                    {
                        StartDaqRecoveryWatchdog(context);
                        StartAffectedGroupRecoveryDeadline(context);
                    },
                    () =>
                    {
                        // 安全扫面只做整组 Pause；状态发布、日志和外部观察者必须等到
                        // 全组 OFF 以及共享电源 Disable 均已启动后，避免首通道观察者
                        // 阻塞时漏停后续兄弟通道。
                        foreach (var channel in affected)
                        {
                            try
                            {
                                if (_timers.TryGetValue(channel, out var timer))
                                    timer.Pause($"DaqSoftwareRecovery:{context.TriggerCode}");
                            }
                            catch (Exception ex)
                            {
                                cutoffSafetyDiagnostics.Add(
                                    $"DAQ截止暂停Timer失败 EPB={channel}: {ex.Message}");
                            }
                        }
                    },
                    () =>
                    {
                        // 仅在全组暂停后冻结一次圈身份和 LastAccepted。SuppressAfter
                        // 与该同一边界配对，后续任何重试都不得扩大正式耐久前缀。
                        context.CutoffParticipantVersions = affected.ToDictionary(
                            channel => channel,
                            CaptureHydraulicParticipantVersion);
                        foreach (var pair in CaptureSoftwareRecoveryCycles(affected))
                        {
                            if (context.CutoffCycles.ContainsKey(pair.Key)) continue;
                            context.CutoffCycles[pair.Key] = pair.Value;
                            MarkDaqClockCycleAborted(
                                context.RunId,
                                context.RunEpoch,
                                pair.Key,
                                pair.Value);
                        }
                        var frozenBoundary = _persistence.InstallCutoff(
                            device,
                            context.CutoffUtc,
                            () => _acq.GetLastAcceptedSequence(device),
                            context.CorrelationId,
                            context.RunId,
                            context.RunEpoch);
                        var snapshot = new DaqCutoffSnapshot(
                            device,
                            context.CutoffUtc,
                            frozenBoundary,
                            context.CutoffCycles);
                        context.CutoffSnapshot = snapshot;
                        Interlocked.Exchange(
                            ref context.CutoffPersistenceBoundary,
                            snapshot.FrozenBoundary);
                    },
                    () =>
                    {
                        // 截止身份已经冻结后再取消当前圈，并把整组 OFF 非阻塞提交到
                        // 最高优先级专用 worker。物理完成由全局完成事件更新带电位图；
                        // 所有权等待期间不得再补发同一批 OFF。
                        foreach (var channel in affected)
                        {
                            try { CancelCyclePauseCts(channel); }
                            catch (Exception ex)
                            {
                                cutoffSafetyDiagnostics.Add(
                                    $"DAQ截止取消当前圈失败 EPB={channel}: {ex.Message}");
                            }
                        }
                        var transactionAccepted = context.Transaction != null &&
                            context.Transaction.SubmitOffBatch();
                        cutoffOffFallbacks = (context.Transaction?.OffReceipts ??
                                              Array.Empty<DaqRecoveryTransaction.OffReceipt>())
                            .Where(receipt => !receipt.Accepted)
                            .GroupBy(receipt => receipt.Channel)
                            .ToDictionary(
                                group => group.Key,
                                group => group.Last().Failure);
                        // DoOffSubmitted is a real admission fact, not a
                        // declaration made when the cutoff worker is entered.
                        // Any rejected/exceptional channel keeps the incident
                        // before this stage and is closed through SafeIdle by
                        // the transaction itself.
                        cutoffOffSubmissionAccepted = transactionAccepted &&
                            context.Transaction.Phase >= DaqRecoveryPhase.DoOffSubmitted &&
                            context.Transaction.CurrentOutcome ==
                                DaqRecoveryTransaction.Outcome.Active;
                    },
                    () =>
                    {
                        StartDaqRecoveryPowerDisableOnce(context);
                    },
                    () => ScheduleRejectedOffFallbacks(
                        cutoffOffFallbacks,
                        "DaqCutoffImmediateOffFallback"),
                    () =>
                    {
                        // 撤销液压参与身份必须与 Pause/OFF 同属截止提交，不能等待恢复
                        // ownership。lease release 每路独立投递，首路协调器阻塞不能挡住
                        // 兄弟通道退出当前液压代次。
                        foreach (var channel in affected)
                        {
                            context.CutoffParticipantVersions.TryGetValue(
                                channel,
                                out var expectedParticipantVersion);
                            if (!TryUnmarkHydraulicParticipant(
                                    channel,
                                    expectedParticipantVersion,
                                    $"DaqCutoff:{context.Device}:RecoveryEpoch={context.RecoveryEpoch}",
                                    logRejected: false))
                                cutoffSafetyDiagnostics.Add(
                                    $"DAQ截止拒绝迟到液压撤权 EPB={channel} " +
                                    $"ExpectedVersion={expectedParticipantVersion}");
                        }

                        var scheduledReleases = new List<KeyValuePair<int, Task>>(affected.Length);
                        foreach (var channel in affected)
                        {
                            var capturedChannel = channel;
                            try
                            {
                                scheduledReleases.Add(
                                    new KeyValuePair<int, Task>(
                                        capturedChannel,
                                        Task.Run(() => HydraulicMarkReleaseAsync(capturedChannel))));
                            }
                            catch (Exception ex)
                            {
                                cutoffSafetyDiagnostics.Add(
                                    $"DAQ截止无法投递液压释放 EPB={capturedChannel}: {ex.Message}");
                            }
                        }
                        foreach (var release in scheduledReleases)
                        {
                            hydraulicReleaseTasks.Add(release.Value);
                            ObserveSafetyTask(
                                release.Value,
                                "DaqCutoffHydraulicReleaseSubmitted",
                                release.Key);
                        }
                    },
                    () => ObserveBackgroundTask(Task.Run(() =>
                    {
                        if (!IsCurrentRecovery(context)) return;
                        foreach (var diagnostic in cutoffSafetyDiagnostics)
                            _log.Warn(diagnostic, "AI");
                        foreach (var channel in affected)
                        {
                            if (!IsCurrentRecovery(context)) break;
                            try
                            {
                                PublishRecoveryIncidentState(
                                    channel,
                                    ChannelRuntimeState.Recovering,
                                    context.TriggerCode,
                                    reason,
                                    affectedChannels: affected,
                                    correlationId: context.CorrelationId,
                                    recoveryOwnerKind: RecoveryOwnerKind.DaqRecovery,
                                    recoveryTargetPhase: IsFormalPhaseCommitted
                                        ? RecoveryTargetPhase.Formal
                                        : RecoveryTargetPhase.Startup,
                                    recoveryOwnerId: context.CorrelationId,
                                    recoveryOwnerGeneration: context.RunEpoch);
                            }
                            catch (Exception ex)
                            {
                                _log.Warn(
                                    $"DAQ截止发布Recovering失败 EPB={channel}: {ex.Message}",
                                    "AI");
                            }
                            NonCriticalObserver.Invoke(
                                ChannelPaused,
                                channel,
                                ex => _log?.Warn(
                                    $"DAQ自愈暂停观察者异常，已隔离：{ex.Message}",
                                    "AI"));
                        }
                    }), "DaqCutoffStatePublication"));

                if (!cutoffOffSubmissionAccepted)
                {
                    CompleteCancelledRecovery(
                        context,
                        "DaqOffSubmissionNotAcceptedSafeIdle");
                    return;
                }

                if (TryEscalatePermanentDataContinuityGap(
                        "BeginDaqAutoRecovery",
                        context.AffectedChannels,
                        context.RunId,
                        context.RunEpoch,
                        context.CorrelationId,
                        context))
                    return;

                await AcquireDaqRecoveryOwnershipsAsync(context).ConfigureAwait(false);
                SubmitDaqIncidentSnapshot(context, reason, "00-trigger");
                if (!IsCurrentRecovery(context))
                {
                    CompleteCancelledRecovery(context, "RunEpochChangedBeforeCutoff");
                    return;
                }

                // participant 撤权和每路 release 已在第一个 await 前投递；取得恢复
                // ownership 后只等待这些既有任务完成，禁止重新提交或扩大截止身份。
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
                    if (Volatile.Read(ref context.BoundaryContradiction) != 0)
                    {
                        await EscalateDaqAutoRecoveryAsync(
                                device,
                                "RecoveryBoundaryContradiction",
                                context.BoundaryContradictionReason,
                                context.CorrelationId,
                                new DaqRecoveryResult
                                {
                                    Device = device,
                                    Recovered = false,
                                    FailureKind = "RecoveryBoundaryContradiction",
                                    FailureReason = context.BoundaryContradictionReason,
                                    Classification = FaultClassification.SystemFault
                                })
                            .ConfigureAwait(false);
                        return;
                    }
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

                if (!await EnsureDaqRecoveryGroupDeenergizedAsync(context)
                        .ConfigureAwait(false))
                {
                    if (IsDaqRecoveryPowerOffPending(context))
                    {
                        ScheduleDaqPowerOffPendingRetry(
                            context,
                            "DaqOutputCutoffPowerOffPending");
                        return;
                    }
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

                // The stage order is intentionally stricter than the physical
                // submission order: power disable may be queued while the
                // high-priority DO command is settling, but it is not exposed
                // as a recovery stage until DO OFF is confirmed.  This keeps
                // watchdog/heartbeat consumers from observing PowerOff* before
                // the preceding safety evidence exists.
                context.Transaction?.ConfirmPhysicalFromController(
                    channel => !IsChannelEnergized(channel));
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.DoOffConfirmed,
                        "受影响通道DO OFF已确认。",
                        allowAlreadyCommitted: true))
                    throw new InvalidOperationException(
                        $"DAQ DO OFF确认阶段顺序无效 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PowerOffSubmitted,
                        "受影响电源组Disable提交已确认进入截止阶段。"))
                    throw new InvalidOperationException(
                        $"DAQ电源Disable提交阶段顺序无效 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PowerOffConfirmed,
                        "受影响电源组OFF已确认。"))
                    throw new InvalidOperationException(
                        $"DAQ电源OFF确认阶段顺序无效 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");

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
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.CutoffCompleted,
                        "整组安全截止已完成。" ) &&
                    context.ProgressStage != DaqRecoveryPhase.CutoffCompleted)
                    throw new InvalidOperationException(
                        $"DAQ截止完成阶段发布失败 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                _log.Warn(
                    $"DAQ软件自恢复开始 Device={device} Affected=[{string.Join(",", affected)}] " +
                    $"CorrelationId={context.CorrelationId:N} Code={context.TriggerCode} " +
                    $"RecoveryEpoch={context.RecoveryEpoch} " +
                    $"FastResyncDiscarded={fastResyncDiscarded} CutoffCompleted=true。",
                    "AI");
                PublishRecoveryProgress(context, "安全断电已完成，正在恢复数据链。");
                if (!restartDaq &&
                    !AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.DaqRestartStarted,
                        "DAQ任务仍在线，跳过重建并开始首批新鲜数据确认。"))
                    throw new InvalidOperationException(
                        $"DAQ截止后无法进入首批数据确认阶段 Device={device} " +
                        $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                SubmitDaqIncidentSnapshot(context, reason, "10-cutoff");

                if (restartDaq)
                {
                    DaqRecoveryResult result = null;
                    try
                    {
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.DaqRestartStarted,
                                "DAQ任务重建已开始。" ) &&
                            context.ProgressStage != DaqRecoveryPhase.DaqRestartStarted)
                            throw new InvalidOperationException(
                                $"DAQ重建开始阶段发布失败 Device={device} " +
                                $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
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
                    if (result.Recovered &&
                        !AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.FirstFreshBatch,
                            "DAQ重建后首批新鲜数据已确认。" ) &&
                        context.ProgressStage != DaqRecoveryPhase.FirstFreshBatch)
                        throw new InvalidOperationException(
                            $"DAQ首批新鲜数据阶段发布失败 Device={device} " +
                            $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                    SubmitDaqIncidentSnapshot(context, result.FailureReason, "20-rebuild");
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
                    if (TryEscalatePermanentDataContinuityGap(
                            "AfterDaqRebuild",
                            context.AffectedChannels,
                            context.RunId,
                            context.RunEpoch,
                            context.CorrelationId,
                            context))
                        return;
                    _persistence.AcceptGeneration(device, _acq.GetCurrentGeneration(device));
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
                if (TryEscalatePermanentDataContinuityGap(
                        "TryCompleteEntry",
                        context.AffectedChannels,
                        context.RunId,
                        context.RunEpoch,
                        context.CorrelationId,
                        context))
                    return;
                context.ValidationDetail = "CutoffBarrier";
                await context.Phase.WaitForCutoffAsync(context.Cancellation.Token)
                    .ConfigureAwait(false);
                if (!IsCurrentRecovery(context))
                {
                    CompleteCancelledRecovery(context, "RunEpochChangedBeforeValidation");
                    return;
                }

                // A restart-capable recovery must not let a concurrent
                // persistence callback validate while the DAQ task is still
                // being rebuilt.  The no-restart path publishes this stage as
                // soon as its cutoff is confirmed; the restart path publishes
                // it immediately before RecoverDeviceAsync.
                if (context.RestartDaq != 0 &&
                    (int)context.Phase.Current < (int)DaqRecoveryPhase.FirstFreshBatch)
                    return;
                context.ValidationDetail = "PersistenceQueue";
                var queue = _persistence.GetSnapshot(device);
                if (queue.QueueDepth > _daqPersistenceResumeDepth ||
                    queue.OldestBatchAgeMs > _daqPersistenceResumeAgeMs ||
                    queue.Generation != _acq.GetCurrentGeneration(device))
                    return;
                context.ValidationDetail = "FreshDaqSamples";
                DaqRecoveryResult[] ready = null;
                await RecoveryStageDeadline.RunAsync(
                        "FreshDaqSamples",
                        _daqPersistenceRecoveryTimeoutMs,
                        async ct =>
                        {
                            ready = await _acq.EnsureChannelsReadyAsync(
                                    context.AffectedChannels,
                                    _daqPersistenceRecoveryTimeoutMs,
                                    IsDaqClockRecoveryTrigger(context.TriggerCode)
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
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.FirstFreshBatch,
                        "验证阶段首批新鲜DAQ数据已确认。" ) &&
                    context.ProgressStage != DaqRecoveryPhase.FirstFreshBatch)
                    return;
                if (TryEscalatePermanentDataContinuityGap(
                        "FreshDaqSamplesValidated",
                        context.AffectedChannels,
                        context.RunId,
                        context.RunEpoch,
                        context.CorrelationId,
                        context))
                    return;
                context.AfterClock = _acq.GetDaqFreshnessSnapshot(device, _daqPersistenceResumeAgeMs);
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PressureRevalidated,
                        "DAQ新鲜度与压力相关采样资格已复核。" ) &&
                    context.ProgressStage != DaqRecoveryPhase.PressureRevalidated)
                    return;
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PersistenceBoundaryClosed,
                        "持久化截止边界已关闭，允许进入重入。" ) &&
                    context.ProgressStage != DaqRecoveryPhase.PersistenceBoundaryClosed)
                    return;
                if (!context.Phase.TryBeginValidation())
                    return;
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.Validating,
                        "DAQ恢复验证条件已满足。" ) &&
                    context.ProgressStage != DaqRecoveryPhase.Validating)
                    return;
                SubmitDaqIncidentSnapshot(
                    context,
                    "DAQ与持久化新鲜度验证通过",
                    "30-validate");
                context.ValidationDetail = "TerminalOffCurrentVerification";
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
                if (TryEscalatePermanentDataContinuityGap(
                        "BeforePowerAndMechanicalRejoin",
                        context.AffectedChannels,
                        context.RunId,
                        context.RunEpoch,
                        context.CorrelationId,
                        context))
                    return;

                var batchPauseSnapshot = CaptureBatchPauseSnapshot();
                var batchPauseState = batchPauseSnapshot.State;
                var holdForBatchPause = ShouldHoldDaqRecoveryForPause(
                    context,
                    batchPauseSnapshot);
                if (holdForBatchPause)
                {
                    context.BatchBarrierParticipant?.Withdraw("HeldForManualPause");
                    _log.Info(
                        $"DAQ恢复已在运行态屏障前转入人工暂停终态。" +
                        $"Device={context.Device};PauseGeneration={batchPauseSnapshot.Generation};" +
                        $"PauseCommandId={batchPauseSnapshot.CommandId:N};" +
                        $"CorrelationId={context.CorrelationId:N}",
                        "AI");
                }
                else
                {
                    context.ValidationDetail = "DaqBatchRecoveryBarrier";
                    if (!await WaitForDaqRecoveryBatchBarrierAsync(context).ConfigureAwait(false))
                        return;
                    if (!IsCurrentRecovery(context)) return;
                }

                var heldPausePowerOffConfirmed = false;
                var powerRecoveryChannels = SelectDaqRecoveryPowerChannels(
                    context.PreviouslyActiveChannels ?? context.AffectedChannels,
                    IsAlarmStopRequested,
                    channel => _channelPausedUtc.ContainsKey(channel),
                    IsChannelEnabled);
                if (!holdForBatchPause && !IsFormalPhaseCommitted && powerRecoveryChannels.Length > 0)
                {
                    context.ValidationDetail = "LearningExecutionRebuildRequired";
                    TryEscalateSoftwareRecoveryCircuitOpen(
                        "LearningExecutionRebuildRequired",
                        "采样已恢复；旧学习执行体已撤销，需要安全重建批次并重新学习和资格验证。",
                        context.PreviouslyActiveChannels, context.RunId, context.RunEpoch,
                        SoftwareRecoveryEscalationAttempts, "ExecutionRecoveryRequired");
                    return;
                }
                var rejoinChannels = (context.PreviouslyRunningChannels ?? Array.Empty<int>())
                    .Where(channel =>
                        IsChannelEnabled(channel) &&
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
                    context.ValidationDetail = "StableRejoinGate";
                    await DaqRecoveryRejoinGate.WaitAsync(
                            TimeSpan.FromMilliseconds(_daqRecoveryStableWindowMs),
                            TimeSpan.FromMilliseconds(Math.Max(
                                _daqPersistenceRecoveryTimeoutMs,
                                _daqRecoveryStableWindowMs * 3)),
                            () =>
                            {
                                var freshness = _acq.GetDaqFreshnessSnapshot(
                                    device,
                                    _daqPersistenceResumeAgeMs);
                                var persistence = _persistence.GetSnapshot(device);
                                var infrastructureHealthy =
                                    _recoveryInfrastructureHealthProvider();
                                return new DaqRecoveryRejoinObservation(
                                    freshness.IsFresh,
                                    freshness.Generation,
                                    freshness.LastProcessedSequence,
                                    freshness.CallbackGapEventCount,
                                    freshness.ControlDiscontinuityCount,
                                    persistence.QueueDepth <= _daqPersistenceResumeDepth &&
                                    persistence.OldestBatchAgeMs <= _daqPersistenceResumeAgeMs &&
                                    persistence.Generation == freshness.Generation,
                                    HasDaqRecoverySafeOffEvidence(context),
                                    infrastructureHealthy,
                                    !infrastructureHealthy);
                            },
                            context.Cancellation.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;
                    context.ValidationDetail = "EmergencyPowerOffBarrier";
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

                    context.ValidationDetail = "PowerEnableThenMechanicalRelease";
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
                    foreach (var rejoinedChannel in rejoinChannels)
                        CompleteDaqMechanicalRequalification(
                            rejoinedChannel,
                            context.PreviousGeneration);
                    ResetTransientFaultStateForRestart(rejoinChannels, "DaqRecoveryRejoin");
                }
                if (holdForBatchPause)
                {
                    await EnsureDaqHeldPowerOffBeforeTerminalAsync(
                            device,
                            powerRecoveryChannels)
                        .ConfigureAwait(false);
                    heldPausePowerOffConfirmed = true;
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
                context.ValidationDetail = "RejoinAndCommit";
                while (true)
                {
                    var retryCommitAfterPausePowerOff = false;
                    try
                    {
                        lock (_daqRecoveryCommitGate)
                        {
                            if (!IsCurrentRecovery(context)) return;
                            if (TryEscalatePermanentDataContinuityGap(
                                    "RejoinAndCommit",
                                    context.AffectedChannels,
                                    context.RunId,
                                    context.RunEpoch,
                                    context.CorrelationId,
                                    context))
                                return;
                            batchPauseSnapshot = CaptureBatchPauseSnapshot();
                            batchPauseState = batchPauseSnapshot.State;
                            holdForBatchPause = ShouldHoldDaqRecoveryForPause(
                                context,
                                batchPauseSnapshot);
                            if (!CanCommitDaqRecoveredHeldTerminal(
                                    holdForBatchPause,
                                    heldPausePowerOffConfirmed))
                            {
                                retryCommitAfterPausePowerOff = true;
                            }
                            else
                            {
                        if (!context.Phase.TryBeginRejoin())
                            throw new InvalidOperationException(
                                $"DAQ恢复重入阶段顺序无效 Device={device} " +
                                $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.Rejoining,
                                "DAQ恢复开始共同节拍重入。" ) &&
                            context.ProgressStage != DaqRecoveryPhase.Rejoining)
                            throw new InvalidOperationException(
                                $"DAQ重入阶段发布失败 Device={device} " +
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
                        if (!context.Phase.TryCommit())
                            return;
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.Committed,
                                "DAQ恢复与共同节拍重入已提交。" ) &&
                            context.ProgressStage != DaqRecoveryPhase.Committed)
                            throw new InvalidOperationException(
                                $"DAQCommitted阶段发布失败 Device={device} " +
                                $"Phase={context.Phase.Current} RecoveryEpoch={context.RecoveryEpoch}");

                        // Committed is observable only after the transaction
                        // has produced a stable checkpoint receipt.  A
                        // checkpoint failure retains the live incident so a
                        // later maintenance tick can retry it, rather than
                        // deleting the owner and forcing a watchdog restart.
                        context.Transaction?.SynchronizeControllerPhase(
                            DaqRecoveryPhase.Committed);
                        if (context.Transaction != null &&
                            !context.Transaction.PublishCommittedCheckpoint())
                        {
                            Interlocked.Exchange(ref context.Completing, 0);
                            return;
                        }

                        // Commit is a separately observable evidence point.
                        // Retain it before moving to Terminal so a heartbeat
                        // cannot lose the committed evidence when the live
                        // owner is removed immediately afterwards.
                        RetainDaqRecoveryCommittedSnapshot(context);

                        // Keep the committed and terminal evidence observable
                        // for the watchdog before removing the live owner.
                        // TryTerminal performs the complete normal ordering:
                        // authoritative channel exit -> Controller Terminal
                        // phase -> stable terminal aggregate receipt.  The
                        // context terminal gate is deliberately committed only
                        // after that transaction succeeds.
                        if (context.Transaction != null &&
                            !context.Transaction.TryTerminal())
                        {
                            ScheduleDaqTerminalRetry(
                                context,
                                result,
                                rejoinChannels,
                                holdForBatchPause,
                                batchPauseState);
                            Interlocked.Exchange(ref context.Completing, 0);
                            return;
                        }
                        if (context.Transaction == null)
                        {
                            if (!AdvanceDaqRecoveryStage(
                                    context,
                                    DaqRecoveryPhase.Terminal,
                                    "DAQ恢复终态已提交；保留终态快照供看门狗确认.") &&
                                context.ProgressStage != DaqRecoveryPhase.Terminal)
                                return;
                            PublishDaqAggregateTerminal(context);
                        }
                        if (!TryFinalizeDaqRecoveredTerminalLocked(context, result))
                        {
                            ScheduleDaqTerminalRetry(
                                context,
                                result,
                                rejoinChannels,
                                holdForBatchPause,
                                batchPauseState);
                            Interlocked.Exchange(ref context.Completing, 0);
                            return;
                        }
                        RetainDaqRecoveryTerminalSnapshot(context);
                            }
                        }
                    }
                    catch
                    {
                        context.Phase.FailRejoin();
                        throw;
                    }
                    if (!retryCommitAfterPausePowerOff) break;
                    await EnsureDaqHeldPowerOffBeforeTerminalAsync(
                            device,
                            powerRecoveryChannels)
                        .ConfigureAwait(false);
                    heldPausePowerOffConfirmed = true;
                }
                ReleaseDaqRecoveryOwnerships(context);
                if (holdForBatchPause && batchPauseState == BatchPauseState.PauseHolding)
                {
                    var currentPause = CaptureBatchPauseSnapshot();
                    if (currentPause.State == BatchPauseState.PauseHolding &&
                        currentPause.Generation == batchPauseSnapshot.Generation &&
                        currentPause.CommandId == batchPauseSnapshot.CommandId)
                    {
                        SetBatchPauseState(
                            BatchPauseState.Paused,
                            currentPause.FrozenChannels,
                            "DAQ健康恢复完成，保持安全暂停，可继续试验。");
                        batchPauseState = BatchPauseState.Paused;
                    }
                }
                if (holdForBatchPause)
                {
                    foreach (var channel in rejoinChannels)
                        PublishChannelRuntimeState(
                            channel,
                            GetDaqRecoveredHeldRuntimeState(batchPauseState),
                            "DaqRecoveredHeldForManualPause",
                            "DAQ软件数据链已恢复；人工暂停代次仍有效，保持定时器和电源暂停",
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
                    _emergencyPowerGroupLatch.TryRemove(
                        groupId,
                        context.CorrelationId);
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
                // Recovered 已在提交锁内完成并释放恢复所有权；终态重证据只受监督异步
                // 导出，禁止再次延长恢复状态机或阻止卡钳按公共节拍重入。
                SubmitDaqIncidentSnapshot(context, "自动恢复成功", "90-recovered");
            }
            catch (Exception ex)
            {
                var phase = string.IsNullOrWhiteSpace(context.ValidationDetail)
                    ? (string.IsNullOrWhiteSpace(context.ValidationPhase)
                        ? "Unknown"
                        : context.ValidationPhase)
                    : context.ValidationDetail;
                if (RequiresImmediateDaqRejoinSafetyRollback(phase))
                {
                    RollbackDaqRecoveryRejoinSafety(context, phase, ex.Message);
                    // When called from the self-maintenance validation loop, propagate to its
                    // outer bounded-recovery handler so the loop cannot re-enable the PSU forty
                    // times at 250ms intervals. A standalone persistence-recovered callback has
                    // no such owner, so explicitly create one bounded maintenance attempt.
                    if (Volatile.Read(ref context.MaintenanceScheduled) != 0)
                        throw;
                    ScheduleDaqSelfMaintenance(
                        context,
                        "DaqRejoinValidationFailed",
                        $"Phase={phase}; Error={ex.Message}; 已立即回滚整组断电。");
                    return;
                }
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

        /// <summary>
        /// Complete the recovered incident only after the transaction has
        /// published its Terminal phase and stable terminal aggregate receipt.
        /// The caller holds _daqRecoveryCommitGate.  This method is also used
        /// by the bounded terminal retry supervisor, so a transient state-store
        /// or checkpoint failure never requires a second recovery owner.
        /// </summary>
        private bool TryFinalizeDaqRecoveredTerminalLocked(
            DaqAutoRecoveryContext context,
            DaqRecoveryResult result)
        {
            if (context == null || result == null || !IsCurrentRecovery(context))
                return false;

            if (context.Transaction != null)
            {
                if (!context.Transaction.TryTerminal())
                    return false;
                if (context.Transaction.CurrentOutcome !=
                        DaqRecoveryTransaction.Outcome.Terminal ||
                    context.Transaction.TerminalAggregateVersion <=
                        context.Transaction.CommittedAggregateVersion)
                    return false;
            }
            else
            {
                if (!AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.Terminal,
                        "DAQ恢复终态已提交；保留终态快照供看门狗确认.") &&
                    context.ProgressStage != DaqRecoveryPhase.Terminal)
                    return false;
                var terminal = PublishDaqAggregateTerminal(context);
                if (terminal == null || !terminal.Stable)
                    return false;
            }

            if (context.Phase.Current != DaqRecoveryPhase.Terminal)
                return false;
            if (!context.Terminal.TryCommit(DaqRecoveryTerminal.Recovered))
                return false;

            // Retain terminal evidence before removing the live context and
            // releasing its incident lease.  A heartbeat can therefore see a
            // complete terminal snapshot even while cleanup callbacks run.
            RetainDaqRecoveryTerminalSnapshot(context);
            context.FailureBackoff.CommitSuccess();
            MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
            MarkDaqRecoveryBatchTerminal(context);
            _persistence.ResumeAdmission(
                context.Device,
                _acq.GetLastAcceptedSequence(context.Device));
            LogDaqRecoveryFieldMetric(context, "Recovered", "RejoinAndCommit");
            TryRemoveExactDaqRecoveryContext(context);
            context.Completion.TrySetResult(result);
            return true;
        }

        /// <summary>
        /// Supervise a partial terminal publication without creating another
        /// RecoveryIncident.  The same context/contract/lease remains live;
        /// each attempt only asks the transaction for missing terminal routes
        /// and retries the failed phase/checkpoint with bounded backoff.
        /// </summary>
        private void ScheduleDaqTerminalRetry(
            DaqAutoRecoveryContext context,
            DaqRecoveryResult result,
            int[] rejoinChannels,
            bool holdForBatchPause,
            BatchPauseState batchPauseState)
        {
            if (context == null || result == null || !IsCurrentRecovery(context))
                return;
            if (Interlocked.CompareExchange(ref context.TerminalRetryScheduled, 1, 0) != 0)
                return;

            // Both the normal Recovered path and the SafeIdle/cancel path use
            // this same production supervisor.  It retries the original
            // transaction/owner and only releases it after a stable terminal
            // aggregate receipt has been observed.
            ScheduleDaqTerminalRetryProduction(
                context.TerminalSupervisor,
                () =>
                {
                    lock (_daqRecoveryCommitGate)
                        return IsCurrentRecovery(context) &&
                               TryFinalizeDaqRecoveredTerminalLocked(context, result);
                },
                () => IsCurrentRecovery(context),
                () =>
                {
                    Interlocked.Exchange(ref context.TerminalRetryScheduled, 0);
                    ReleaseDaqRecoveryOwnerships(context);
                    foreach (var channel in rejoinChannels ?? Array.Empty<int>())
                    {
                        PublishChannelRuntimeState(
                            channel,
                            holdForBatchPause
                                ? GetDaqRecoveredHeldRuntimeState(batchPauseState)
                                : ChannelRuntimeState.Running,
                            holdForBatchPause
                                ? "DaqRecoveredHeldForBatchPause"
                                : "DaqSoftwareRecoveredCommitted",
                            holdForBatchPause
                                ? "DAQ软件数据链已恢复；批次仍处于暂停/恢复预检。"
                                : "DAQ恢复终态已提交，通道已按公共节律重入。",
                            affectedChannels: rejoinChannels,
                            correlationId: context.CorrelationId);
                    }
                    try { context.Cancellation.Cancel(); } catch { }
                    SubmitDaqIncidentSnapshot(
                        context,
                        "自动恢复终态重试成功",
                        "90-recovered-terminal-retry");
                },
                reason =>
                {
                    Interlocked.Exchange(ref context.TerminalRetryScheduled, 0);
                    if (!TryFinalizeDaqTerminalRetryFailClosed(context, reason))
                        _log?.Error(
                            $"DAQ终态aggregate凭证持续失败，安全终态尚未完成复核，保留owner。" +
                            $"Device={context.Device}; CorrelationId={context.CorrelationId:N};" +
                            $"Reason={reason}",
                            "AI");
                },
                "DaqTerminalRetryDeadline;SafeIdleOnly",
                onFinished: () => Interlocked.Exchange(
                    ref context.TerminalRetryScheduled,
                    0));
        }

        private async Task EscalateDaqAutoRecoveryAsync(
            string device,
            string code,
            string reason,
            Guid correlationId,
            DaqRecoveryResult recoveryResult = null)
        {
            if (IsDaqRecoveryTerminalCorrelation(device, correlationId))
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
            // 同一设备可能先以 Lag 建立恢复上下文，随后看门狗确认永久写阻塞或
            // 处理链形成确定性空洞。后到的高严重度身份必须覆盖最初的瞬态身份，
            // 否则自维护会继续按 Lag 无限局部重试。
            if (IsDeterministicProcessingGap(code)) context.TriggerCode = code;
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
                    AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.SafeIdle,
                        "DAQ硬件失联证据已确认，进入单次SafeIdle。");
                    if (!PublishDaqSafeTerminalStates(
                            context,
                            "DAQ硬件失联证据已确认，通道已进入安全终态。",
                            ChannelRuntimeState.AlarmStopped))
                    {
                        _log?.Error(
                            $"DAQ硬件确认安全终态尚未覆盖整组，保留context/owner。" +
                            $"Device={context.Device}; CorrelationId={context.CorrelationId:N}",
                            "AI");
                        return;
                    }
                    if (!context.Terminal.TryCommit(DaqRecoveryTerminal.HardwareConfirmed)) return;
                    AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.Terminal,
                        "DAQ硬件确认事故已进入终态。");
                    RetainDaqRecoveryTerminalSnapshot(context);
                    MarkDaqRecoveryTerminal(context.CorrelationId, context.Device);
                    MarkDaqRecoveryBatchTerminal(context);
                    TryRemoveExactDaqRecoveryContext(context);
                }
                var primary = context.AffectedChannels.OrderBy(x => x).FirstOrDefault();
                var result = recoveryResult ?? new DaqRecoveryResult { Device = device };
                result.Classification = FaultClassification.HardwareConfirmed;
                result.HardwareEvidence = evidence;
                result.FailureKind = "DaqHardwareConfirmed";
                context.Completion.TrySetResult(result);
                ReleaseDaqRecoveryOwnerships(context);
                try { context.Cancellation.Cancel(); } catch { }
                LatchDaqGroupHardFault(
                    primary,
                    device,
                    context.AffectedChannels,
                    "DaqHardwareConfirmed",
                    reason,
                    context.CorrelationId,
                    classification: FaultClassification.HardwareConfirmed);
                // 硬件终态的整组异步OFF、共享电源Disable和故障发布均已启动后，才
                // 允许外部恢复状态观察者运行；观察者阻塞不能吞掉冗余安全动作。
                NonCriticalObserver.Invoke(
                    DaqRecoveryStateChanged,
                    result,
                    ex => _log?.Warn($"DAQ恢复状态观察者异常，已隔离：{ex.Message}", "AI"));
                SubmitDaqIncidentSnapshot(context, reason, "90-hardware-confirmed");
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

        internal static bool RequiresImmediateDaqRejoinSafetyRollback(string phase)
        {
            return string.Equals(
                       phase,
                       "PowerEnableThenMechanicalRelease",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       phase,
                       "RejoinAndCommit",
                       StringComparison.OrdinalIgnoreCase);
        }

        private void RollbackDaqRecoveryRejoinSafety(
            DaqAutoRecoveryContext context,
            string phase,
            string error)
        {
            if (context == null) return;
            Dictionary<int, string> rejectedOff = null;
            ExecuteNonBlockingSafetyIsolationOrder(
                () => FreezeAndCancelSafetyChannels(
                    context.AffectedChannels,
                    "DaqRejoinSafetyRollback",
                    cancelStopTokens: false),
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    context.AffectedChannels,
                    "DaqRejoinRollbackOffAdmissionRejected",
                    "DaqRejoinRollbackOffSubmissionException"),
                () => StartElectricalGroupSafetyDisables(
                    context.AffectedChannels,
                    $"DAQ恢复重入失败安全回滚 Device={context.Device} Phase={phase}",
                    "DaqRejoinRollbackPowerDisable"),
                () => _log.Error(
                    $"DAQ恢复在重新使能后的阶段失败，已立即暂停整组、提交电机OFF并关闭程控电源。" +
                    $"Device={context.Device} Phase={phase} CorrelationId={context.CorrelationId:N} " +
                    $"Error={error}",
                    "AI"),
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    "DaqRejoinRollbackImmediateOffFallback"));
        }

        private void ScheduleDaqPowerOffPendingRetry(
            DaqAutoRecoveryContext context,
            string reason)
        {
            if (!IsCurrentRecovery(context)) return;
            if (Interlocked.CompareExchange(
                    ref context.PowerOffPendingRetryScheduled,
                    1,
                    0) != 0)
                return;

            ObserveNonRecoveryLifecycleTask(Task.Run(async () =>
            {
                try
                {
                    while (IsCurrentRecovery(context))
                    {
                        var remainingMs = GetDaqRecoveryRemainingMs(context);
                        if (remainingMs <= 0)
                        {
                            var pause = CaptureBatchPauseSnapshot();
                            if (ShouldHoldDaqRecoveryForPause(context, pause) &&
                                HasDaqRecoverySafeOffEvidence(context))
                                SetBatchPauseState(
                                    BatchPauseState.PauseHolding,
                                    pause.FrozenChannels,
                                    "DAQ恢复达到60秒硬截止；已确认断电，保持暂停等待受控停止。");
                            CompleteCancelledRecovery(
                                context,
                                "DaqPowerOffPendingHardDeadline; " +
                                (reason ?? "unknown") + "; SafeIdleOnly");
                            return;
                        }

                        await Task.Delay(Math.Min(1000, remainingMs), context.Cancellation.Token)
                            .ConfigureAwait(false);
                        if (!IsCurrentRecovery(context)) return;

                        RetryDaqRecoveryPowerDisableTasks(context);
                        if (!await EnsureDaqRecoveryGroupDeenergizedAsync(
                                context,
                                retryUnresolvedOff: true).ConfigureAwait(false))
                            continue;

                        // The original recovery body intentionally stopped at
                        // the pending receipt.  Re-enter through the existing
                        // maintenance path without consuming a failure attempt:
                        // this is a late acknowledgement, not a failed DAQ
                        // recovery generation.
                        Interlocked.Exchange(ref context.PowerOffPendingPublished, 0);
                        ScheduleDaqSelfMaintenance(
                            context,
                            "DaqPowerOffReceiptConfirmed",
                            "当前恢复代际电源OFF回执已确认，继续DAQ恢复。",
                            countAsFailure: false);
                        return;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (IsCurrentRecovery(context))
                    {
                        _log.Warn(
                            $"DAQ恢复PowerOffPending重试异常 Device={context.Device}: {ex.Message}",
                            "程控电源");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref context.PowerOffPendingRetryScheduled, 0);
                }
            }), "DaqPowerOffPendingRetry");
        }

        private void RetryDaqRecoveryPowerDisableTasks(DaqAutoRecoveryContext context)
        {
            if (context == null || _powerSupply == null) return;
            var groups = (context.AffectedChannels ?? Array.Empty<int>())
                .Select(GetElectricalGroupId)
                .Where(groupId => groupId > 0)
                .Distinct()
                .OrderBy(groupId => groupId)
                .ToArray();
            var retryGroups = groups.Where(groupId =>
            {
                if (context.PowerOffReceipts.TryGetValue(groupId, out var receipt) &&
                    receipt.RecoveryEpoch == context.RecoveryEpoch &&
                    receipt.TaskCompleted && receipt.OutputConfirmedOff)
                    return false;
                return context.PowerDisableTasksByGroup == null ||
                       !context.PowerDisableTasksByGroup.TryGetValue(groupId, out var task) ||
                       task == null || task.IsFaulted || task.IsCanceled;
            }).ToArray();
            if (retryGroups.Length == 0) return;

            var retried = StartElectricalGroupSafetyDisables(
                context.AffectedChannels,
                $"DAQ恢复PowerOffPending重试 Device={context.Device} " +
                $"RecoveryEpoch={context.RecoveryEpoch}",
                "DaqPowerOffPendingRetry");
            lock (context.ProgressGate)
            {
                var tasks = context.PowerDisableTasksByGroup ??
                            new Dictionary<int, Task<(bool ok, string error)>>();
                foreach (var groupId in retryGroups)
                {
                    if (!retried.TryGetValue(groupId, out var task) || task == null) continue;
                    tasks[groupId] = task;
                    var receipt = context.PowerOffReceipts.GetOrAdd(
                        groupId,
                        id => new DaqRecoveryPowerOffReceipt { GroupId = id });
                    receipt.RecoveryEpoch = context.RecoveryEpoch;
                    receipt.SubmittedUtc = DateTime.UtcNow;
                    receipt.TaskCompleted = false;
                    receipt.OutputConfirmedOff = false;
                    receipt.PowerOffPending = true;
                    receipt.Failure = "PowerOffRetrySubmitted";
                    receipt.Evidence = new SafetyOffReceipt
                    {
                        CorrelationId = context.CorrelationId,
                        TargetKind = SafetyOffTargetKind.PowerSupplyGroup,
                        TargetId = groupId,
                        RunEpoch = context.RunEpoch,
                        OperationGeneration = context.RecoveryEpoch,
                        SubmittedUtc = receipt.SubmittedUtc,
                        Status = SafetyOffEvidenceStatus.Submitted,
                        EvidenceSource = "PowerSupplySafetyDisable"
                    };
                }
                context.PowerDisableTasksByGroup = tasks;
                context.PowerDisableTasks = tasks.Values.Where(task => task != null).ToArray();
            }
        }

        private void ScheduleDaqSelfMaintenance(
            DaqAutoRecoveryContext context,
            string code,
            string reason,
            bool countAsFailure = true)
        {
            if (!IsCurrentRecovery(context)) return;
            if (Interlocked.CompareExchange(ref context.MaintenanceScheduled, 1, 0) != 0) return;

            var failureCount = countAsFailure
                ? context.FailureBackoff.RecordFailure()
                : context.FailureBackoff.Current;
            if (countAsFailure && failureCount >= SoftwareRecoveryEscalationAttempts)
            {
                // DAQ software maintenance is bounded per incident.  Reaching
                // the budget is a single SafeIdle/Terminal outcome, not an
                // instruction to recycle the batch or relaunch the process.
                CompleteCancelledRecovery(
                    context,
                    $"DaqSelfMaintenanceBudgetExhausted Device={context.Device}; " +
                    $"Failure={failureCount}; Code={code}; SafeIdleOnly");
                return;
            }
            var delayMs = countAsFailure
                ? GetDaqSelfMaintenanceDelayMs(failureCount)
                : 0;
            foreach (var channel in context.AffectedChannels)
                PublishRecoveryIncidentState(
                    channel,
                    ChannelRuntimeState.Recovering,
                    "DaqSelfMaintenance",
                    $"软件数据链自维护中，第{failureCount}次，{delayMs}ms后重试。",
                    affectedChannels: context.AffectedChannels,
                    correlationId: context.CorrelationId,
                    recoveryOwnerKind: RecoveryOwnerKind.DaqRecovery,
                    recoveryTargetPhase: IsFormalPhaseCommitted
                        ? RecoveryTargetPhase.Formal
                        : RecoveryTargetPhase.Startup,
                    recoveryOwnerId: context.CorrelationId,
                    recoveryOwnerGeneration: context.RunEpoch);
            _log.Warn(
                $"DAQ软件自维护等待重试 Device={context.Device} Failure={failureCount} " +
                $"DelayMs={delayMs} Code={code} CorrelationId={context.CorrelationId:N} " +
                $"Affected=[{string.Join(",", context.AffectedChannels)}] Reason={reason}；" +
                "健康DAQ组继续运行。",
                "AI");
            PublishRecoveryProgress(context, $"软件自维护等待 {delayMs}ms 后重试。");
            SubmitDaqIncidentSnapshot(context, reason, "40-self-maintenance");

            // The DAQ incident wrapper remains bound until context.Completion
            // is signalled.  This maintenance continuation is therefore a
            // subordinate safety operation, not a second recovery owner or
            // registry entry.
            ObserveNonRecoveryLifecycleTask(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, context.Cancellation.Token).ConfigureAwait(false);
                    if (!IsCurrentRecovery(context)) return;

                    // Re-check the hard safety boundary on every unattended retry. If an OFF
                    // command did not take effect, retry OFF but never skip data or rebuild the
                    // DAQ while any mapped channel is still considered energized.
                    if (!await EnsureDaqRecoveryGroupDeenergizedAsync(
                            context,
                            retryUnresolvedOff: true).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref context.MaintenanceScheduled, 0);
                        if (IsDaqRecoveryPowerOffPending(context))
                        {
                            ScheduleDaqPowerOffPendingRetry(
                                context,
                                "DaqSelfMaintenancePowerOffPending");
                            return;
                        }
                        ScheduleDaqSelfMaintenance(
                            context,
                            "DaqOutputCutoffUnconfirmed",
                            "整组断电尚未确认；保持安全断电重试，不执行DAQ重同步。");
                        return;
                    }
                    // The physical cutoff is authoritative before any DAQ
                    // restart.  Commit it now so the monotonic phase cannot
                    // later move backward from DaqRestartStarted.
                    if ((int)context.Phase.Current < (int)DaqRecoveryPhase.CutoffCompleted)
                    {
                        try
                        {
                            _acq.ResynchronizeInactiveControlToLatest(context.Device);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn(
                                $"DAQ自维护快速重同步未执行 Device={context.Device}: {ex.Message}",
                                "AI");
                        }
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.DoOffConfirmed,
                                "DAQ自维护重试确认整组DO OFF。",
                                allowAlreadyCommitted: true))
                            throw new InvalidOperationException(
                                $"DAQ自维护DO OFF确认阶段顺序无效 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.PowerOffSubmitted,
                                "DAQ自维护确认电源Disable提交。"))
                            throw new InvalidOperationException(
                                $"DAQ自维护电源Disable提交阶段顺序无效 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.PowerOffConfirmed,
                                "DAQ自维护重试确认整组电源OFF。"))
                            throw new InvalidOperationException(
                                $"DAQ自维护电源OFF确认阶段顺序无效 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                        if (!context.Phase.CompleteCutoff())
                            throw new InvalidOperationException(
                                $"DAQ自维护无法提交截止阶段 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.CutoffCompleted,
                                "DAQ自维护确认整组安全截止完成。" ) &&
                            context.ProgressStage != DaqRecoveryPhase.CutoffCompleted)
                            throw new InvalidOperationException(
                                $"DAQ自维护截止完成阶段发布失败 Device={context.Device} " +
                                $"Phase={context.Phase.Current}");
                    }

                    DaqRecoveryResult result = null;
                    AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.DaqRestartStarted,
                        "DAQ自维护重建任务已开始。");
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
                                        forceRecreate: RequiresDaqTaskRecreate(context.TriggerCode))
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
                    if (result.Recovered)
                        AdvanceDaqRecoveryStage(
                            context,
                            DaqRecoveryPhase.FirstFreshBatch,
                            "DAQ自维护重建后的首批新鲜数据已确认。");
                    SubmitDaqIncidentSnapshot(
                        context,
                        result.FailureReason,
                        "50-self-maintenance-rebuild");

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
                        if (Volatile.Read(ref context.BoundaryContradiction) != 0)
                        {
                            await EscalateDaqAutoRecoveryAsync(
                                    context.Device,
                                    "RecoveryBoundaryContradiction",
                                    context.BoundaryContradictionReason,
                                    context.CorrelationId,
                                    new DaqRecoveryResult
                                    {
                                        Device = context.Device,
                                        Recovered = false,
                                        FailureKind = "RecoveryBoundaryContradiction",
                                        FailureReason = context.BoundaryContradictionReason,
                                        Classification = FaultClassification.SystemFault
                                    })
                                .ConfigureAwait(false);
                            return;
                        }
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
                        if (!AdvanceDaqRecoveryStage(
                                context,
                                DaqRecoveryPhase.CutoffCompleted,
                                "DAQ自维护补交整组安全截止。" ) &&
                            context.ProgressStage != DaqRecoveryPhase.CutoffCompleted)
                            throw new InvalidOperationException(
                                $"DAQ自维护补交截止阶段发布失败 Device={context.Device} " +
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
                    AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PressureRevalidated,
                        "DAQ自维护压力与新鲜度已复核。");
                    AdvanceDaqRecoveryStage(
                        context,
                        DaqRecoveryPhase.PersistenceBoundaryClosed,
                        "DAQ自维护持久化截止边界已关闭。");
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

        private async Task<bool> EnsureDaqRecoveryGroupDeenergizedAsync(
            DaqAutoRecoveryContext context,
            bool retryUnresolvedOff = false)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.Device)) return false;
            if (retryUnresolvedOff)
            {
                if (context.Transaction != null)
                {
                    // The incident transaction owns the missing-route set and
                    // never resubmits a route whose physical completion is
                    // already authoritative.
                    context.Transaction.RetryMissingOff();
                    context.Transaction.ConfirmPhysicalFromController(
                        channel => !IsChannelEnergized(channel));
                }
                else
                {
                foreach (var channel in context.AffectedChannels ?? Array.Empty<int>())
                {
                    // 初次截止已经为每个通道提交过一次 OFF。维护阶段只对仍在带电
                    // 位图中的通道补发，避免成功通道重复 NI 写和重复完成事件。
                    if (!IsChannelEnergized(channel)) continue;
                    var accepted = false;
                    try
                    {
                        accepted = TrySubmitEpbOffHighPriority(channel, null, out _);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"DAQ维护OFF重试提交异常 EPB={channel}: {ex.Message}",
                            "AI");
                    }
                    if (accepted) continue;
                    try
                    {
                        ObserveBackgroundTask(
                            Task.Run(() => TryExecuteImmediateOffFallback(
                                channel,
                                "DaqMaintenanceAdmissionRejected",
                                out _)),
                            "DaqMaintenanceImmediateOffFallback",
                            channel);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"DAQ维护OFF兜底调度失败 EPB={channel}: {ex.Message}",
                            "AI");
                    }
                }
                }
            }
            var deenergized = !IsDaqDeviceControlActive(context.Device);
            if (deenergized && _powerSupply != null)
            {
                var groups = (context.AffectedChannels ?? Array.Empty<int>())
                    .Select(GetElectricalGroupId)
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                foreach (var groupId in groups)
                {
                    var state = _powerSupply.GetRuntimeState(groupId);
                    var receipt = context.PowerOffReceipts.GetOrAdd(
                        groupId,
                        id => new DaqRecoveryPowerOffReceipt
                        {
                            GroupId = id,
                            RecoveryEpoch = context.RecoveryEpoch,
                            SubmittedUtc = DateTime.UtcNow,
                            Evidence = new SafetyOffReceipt
                            {
                                CorrelationId = context.CorrelationId,
                                TargetKind = SafetyOffTargetKind.PowerSupplyGroup,
                                TargetId = id,
                                RunEpoch = context.RunEpoch,
                                OperationGeneration = context.RecoveryEpoch,
                                SubmittedUtc = DateTime.UtcNow,
                                Status = SafetyOffEvidenceStatus.Submitted,
                                EvidenceSource = "PowerSupplySafetyDisable"
                            }
                        });
                    receipt.RecoveryEpoch = context.RecoveryEpoch;
                    receipt.PowerOperationEpoch = state.OperationEpoch;
                    if (context.PowerDisableTasksByGroup == null ||
                        !context.PowerDisableTasksByGroup.TryGetValue(groupId, out var disableTask) ||
                        disableTask == null)
                    {
                        receipt.PowerOffPending = true;
                        receipt.Failure = "PowerOffTaskMissingForRecoveryEpoch";
                        receipt.StateObservedUtc = DateTime.UtcNow;
                        receipt.LastTelemetryUtc = state.TelemetryUtc;
                        PublishDaqPowerOffPending(context, groupId, receipt.Failure);
                        deenergized = false;
                        continue;
                    }

                    try
                    {
                        if (!disableTask.IsCompleted)
                        {
                            var remainingMs = GetDaqRecoveryRemainingMs(context);
                            if (remainingMs <= 0)
                            {
                                receipt.PowerOffPending = true;
                                receipt.Failure = "RecoveryHardDeadlineElapsedBeforePowerOffReceipt";
                                return false;
                            }
                            var diagnosticBudgetMs = Math.Min(
                                _daqPersistenceRecoveryTimeoutMs,
                                remainingMs);
                            var completed = await Task.WhenAny(
                                    disableTask,
                                    Task.Delay(diagnosticBudgetMs, context.Cancellation.Token))
                                .ConfigureAwait(false);
                            if (completed != disableTask)
                            {
                                context.Cancellation.Token.ThrowIfCancellationRequested();
                                receipt.PowerOffPending = true;
                                receipt.Failure = "PowerOffPending";
                                receipt.StateObservedUtc = DateTime.UtcNow;
                                receipt.LastTelemetryUtc = state.TelemetryUtc;
                                PublishDaqPowerOffPending(context, groupId, receipt.Failure);
                                deenergized = false;
                                continue;
                            }
                        }
                        var powerReceipt = await disableTask.ConfigureAwait(false);
                        if (!powerReceipt.ok)
                        {
                            receipt.PowerOffPending = true;
                            receipt.Failure = powerReceipt.error ?? "PowerOffUnconfirmed";
                            receipt.Evidence.Status = SafetyOffEvidenceStatus.Failed;
                            receipt.Evidence.Error = receipt.Failure;
                            receipt.Evidence.CompletedUtc = DateTime.UtcNow;
                            deenergized = false;
                            continue;
                        }
                        receipt.TaskCompleted = true;
                        receipt.TaskCompletedUtc = DateTime.UtcNow;
                    }
                    catch (OperationCanceledException) when (
                        context.Cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        receipt.PowerOffPending = true;
                        receipt.Failure = ex.GetBaseException().Message;
                        receipt.StateObservedUtc = DateTime.UtcNow;
                        receipt.LastTelemetryUtc = state.TelemetryUtc;
                        PublishDaqPowerOffPending(context, groupId, receipt.Failure);
                        deenergized = false;
                        continue;
                    }

                    // The current recovery epoch's completed Disable task is
                    // the OFF receipt.  Runtime Active/Expected/Telemetry can
                    // lag by a few milliseconds, so retain them strictly as
                    // diagnostics; they are never a second failure verdict.
                    state = _powerSupply.GetRuntimeState(groupId);
                    receipt.StateObservedUtc = DateTime.UtcNow;
                    receipt.LastTelemetryUtc = state.TelemetryUtc;
                    var reportedOn = state.ExpectedOutputEnabled ||
                                     state.TelemetryOutputEnabled || state.Active;
                    receipt.OutputConfirmedOff =
                        IsCurrentDaqRecoveryPowerOffReceiptAuthoritative(
                            receipt.TaskCompleted,
                            reportedOn);
                    receipt.Evidence = receipt.Evidence ?? new SafetyOffReceipt();
                    receipt.Evidence.CorrelationId = context.CorrelationId;
                    receipt.Evidence.TargetKind = SafetyOffTargetKind.PowerSupplyGroup;
                    receipt.Evidence.TargetId = groupId;
                    receipt.Evidence.RunEpoch = context.RunEpoch;
                    receipt.Evidence.OperationGeneration = state.OperationEpoch;
                    receipt.Evidence.SubmittedUtc = receipt.SubmittedUtc;
                    receipt.Evidence.CompletedUtc = receipt.TaskCompletedUtc;
                    receipt.Evidence.HardwareObservedUtc = receipt.StateObservedUtc;
                    receipt.Evidence.Status = receipt.OutputConfirmedOff
                        ? SafetyOffEvidenceStatus.ConfirmedOff
                        : SafetyOffEvidenceStatus.Failed;
                    receipt.Evidence.EvidenceSource = "PowerSupplyReadback";
                    receipt.PowerOffPending = false;
                    receipt.Failure = string.Empty;
                    if (reportedOn)
                    {
                        _log.Warn(
                            $"DAQ恢复收到当前代际电源OFF回执，但状态缓存尚未收敛；仅记录诊断。 " +
                            $"Device={context.Device} Group={groupId} " +
                            $"RecoveryEpoch={context.RecoveryEpoch} " +
                            $"OperationEpoch={state.OperationEpoch} TelemetryUtc={state.TelemetryUtc:O}",
                            "程控电源");
                    }
                }
            }
            if (!deenergized)
                _log.Warn(
                    $"DAQ恢复断电仍待确认 Device={context.Device} " +
                    $"Affected=[{string.Join(",", context.AffectedChannels ?? Array.Empty<int>())}]；" +
                    "发布PowerOffPending，禁止丢弃批次和重建DAQ。",
                    "AI");
            return deenergized;
        }

        private void PublishDaqPowerOffPending(
            DaqAutoRecoveryContext context,
            int groupId,
            string detail)
        {
            if (context == null) return;
            if (Interlocked.Exchange(ref context.PowerOffPendingPublished, 1) != 0) return;
            PublishRecoveryProgress(
                context,
                $"PowerOffPending Group={groupId}；等待当前恢复代际OFF回执。" +
                $"Detail={detail ?? string.Empty}");
            _log.Warn(
                $"DAQ恢复电源OFF回执等待中 Device={context.Device} Group={groupId} " +
                $"RecoveryEpoch={context.RecoveryEpoch} Detail={detail}",
                "程控电源");
        }

        private static bool IsDaqRecoveryPowerOffPending(DaqAutoRecoveryContext context)
        {
            return context != null && GetDaqRecoveryRemainingMs(context) > 0 &&
                   context.PowerOffReceipts.Values.Any(receipt =>
                       receipt != null &&
                       receipt.RecoveryEpoch == context.RecoveryEpoch &&
                       receipt.PowerOffPending);
        }

        // A completed Disable task belongs to the current RecoveryEpoch and
        // is the authoritative OFF acknowledgement.  The cached status bit is
        // deliberately an input for diagnostics only; it must not turn the
        // 4–100 ms publication race into StartBlocked.
        internal static bool IsCurrentDaqRecoveryPowerOffReceiptAuthoritative(
            bool disableTaskCompleted,
            bool cachedRuntimeReportsOn)
        {
            return disableTaskCompleted;
        }

        internal static bool ShouldKeepDaqRecoveryPowerOffPending(
            bool disableTaskCompleted,
            int remainingHardDeadlineMs)
        {
            return !disableTaskCompleted && remainingHardDeadlineMs > 0;
        }

        private bool HasDaqRecoveryPowerOffReceipts(DaqAutoRecoveryContext context)
        {
            if (context == null || _powerSupply == null) return true;
            var groups = (context.AffectedChannels ?? Array.Empty<int>())
                .Select(GetElectricalGroupId)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            return groups.All(groupId => context.PowerOffReceipts.TryGetValue(groupId, out var receipt) &&
                                        receipt.RecoveryEpoch == context.RecoveryEpoch &&
                                        receipt.TaskCompleted && receipt.OutputConfirmedOff &&
                                        receipt.Evidence?.ConfirmedOff == true);
        }

        private bool HasDaqRecoverySafeOffEvidence(DaqAutoRecoveryContext context)
        {
            return context != null &&
                   !IsDaqDeviceControlActive(context.Device) &&
                   HasDaqRecoveryPowerOffReceipts(context);
        }

        private static int GetDaqRecoveryRemainingMs(DaqAutoRecoveryContext context)
        {
            if (context == null || context.HardDeadlineUtc == default) return 0;
            var remaining = context.HardDeadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return 0;
            return (int)Math.Min(int.MaxValue, Math.Ceiling(remaining.TotalMilliseconds));
        }

        /// <summary>
        /// 固化故障入口的非阻塞安全顺序。所有通道必须先整体冻结，再完成整组异步
        /// OFF 准入；共享电源关闭和故障/恢复调度都已经启动后，才允许为未接纳项
        /// 启动独立同步兜底。任何单路 NI 调用都不能占用兄弟通道或电源关闭的预算。
        /// </summary>
        internal static void ExecuteNonBlockingSafetyIsolationOrder(
            Action freezeAndCancelAll,
            Action submitOffAll,
            Action startPowerDisable,
            Action publishOrSchedule,
            Action startRejectedOffFallbacks)
        {
            if (freezeAndCancelAll == null)
                throw new ArgumentNullException(nameof(freezeAndCancelAll));
            if (submitOffAll == null)
                throw new ArgumentNullException(nameof(submitOffAll));

            freezeAndCancelAll();
            submitOffAll();
            startPowerDisable?.Invoke();
            publishOrSchedule?.Invoke();
            startRejectedOffFallbacks?.Invoke();
        }

        private void FreezeAndCancelSafetyChannels(
            IEnumerable<int> affectedChannels,
            string reason,
            bool cancelStopTokens)
        {
            var channels = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();

            // 两阶段处理：先暂停整组，再撤销任何单通道推进令牌。不能在逐通道
            // Pause/Cancel/OFF 时让尚未轮到的兄弟通道继续进入下一个动作相位。
            foreach (var channel in channels)
            {
                try
                {
                    if (_timers.TryGetValue(channel, out var timer))
                        timer.Pause(reason ?? "SafetyIsolation");
                }
                catch { }
            }
            foreach (var channel in channels)
            {
                try { CancelCyclePauseCts(channel); } catch { }
                if (!cancelStopTokens) continue;
                try { CancelStopCts(channel); } catch { }
            }
        }

        private Dictionary<int, string> SubmitEpbOffHighPriorityBatch(
            IEnumerable<int> affectedChannels,
            string rejectedStage,
            string exceptionStage)
        {
            var rejected = new Dictionary<int, string>();
            foreach (var channel in (affectedChannels ?? Array.Empty<int>())
                         .Where(channel => channel >= 1 && channel <= 12)
                         .Distinct()
                         .OrderBy(channel => channel))
            {
                try
                {
                    if (!TrySubmitEpbOffHighPriority(channel, null, out _))
                        rejected[channel] = rejectedStage ?? "SafetyOffAdmissionRejected";
                }
                catch
                {
                    // 生产者路径不在这里同步写日志或执行 NI 兜底。异常身份冻结到字典，
                    // 等共享电源关闭/故障发布已经启动后再由受监督后台任务处理。
                    rejected[channel] = exceptionStage ?? "SafetyOffSubmissionException";
                }
            }
            return rejected;
        }

        private void ScheduleRejectedOffFallbacks(
            IReadOnlyDictionary<int, string> rejected,
            string operation)
        {
            if (rejected == null || rejected.Count == 0) return;
            // 先把所有拒绝项各自投递到线程池，再逐个登记监督。首个同步 NI 写即使
            // 永久不返回，也不能阻止后续兄弟通道取得自己的兜底执行机会。
            var fallbackTasks = StartIndependentSafetyFallbackTasks(
                rejected,
                (channel, stage) => TryExecuteImmediateOffFallback(
                    channel,
                    stage,
                    out _));
            ObserveIndependentSafetyFallbackTasks(fallbackTasks, operation);
        }

        private void ObserveIndependentSafetyFallbackTasks(
            IReadOnlyDictionary<int, Task<bool>> fallbackTasks,
            string operation)
        {
            if (fallbackTasks == null || fallbackTasks.Count == 0) return;
            foreach (var pair in fallbackTasks.OrderBy(item => item.Key))
            {
                var channel = pair.Key;
                try
                {
                    ObserveBackgroundTask(
                        pair.Value,
                        operation ?? "SafetyImmediateOffFallback",
                        channel);
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"EPB[{channel}] 无法监督独立同步OFF兜底。Error={ex.Message}",
                        "DO性能",
                        ex);
                }
            }
        }

        internal static IReadOnlyDictionary<int, Task<bool>> StartIndependentSafetyFallbackTasks(
            IReadOnlyDictionary<int, string> rejected,
            Func<int, string, bool> fallback)
        {
            var tasks = new Dictionary<int, Task<bool>>();
            if (rejected == null || rejected.Count == 0) return tasks;
            if (fallback == null) throw new ArgumentNullException(nameof(fallback));
            foreach (var pair in rejected.OrderBy(item => item.Key))
            {
                var channel = pair.Key;
                var stage = pair.Value;
                try
                {
                    tasks[channel] = Task.Factory.StartNew(
                        () => fallback(channel, stage),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    var failed = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    failed.TrySetException(ex);
                    tasks[channel] = failed.Task;
                }
            }
            return tasks;
        }

        private Dictionary<int, Task<(bool ok, string error)>> StartElectricalGroupSafetyDisables(
            IEnumerable<int> affectedChannels,
            string reason,
            string operation)
        {
            var tasks = new Dictionary<int, Task<(bool ok, string error)>>();
            if (_powerSupply == null) return tasks;
            foreach (var groupId in (affectedChannels ?? Array.Empty<int>())
                         .Select(GetElectricalGroupId)
                         .Where(id => id > 0)
                         .Distinct()
                         .OrderBy(id => id))
            {
                // 调用 async 方法本身会在第一次 await 前立即进入 DisableGroupAsync；
                // 因此 owner OFF 已经启动，而不是等到后台调度器稍后才开始。
                var task = ConfirmElectricalGroupOffSafetyAsync(groupId, reason);
                tasks[groupId] = task;
                ObserveBackgroundTask(task, operation ?? "ElectricalGroupSafetyDisable");
            }
            return tasks;
        }

        private async Task<(bool ok, string error)> ConfirmElectricalGroupOffSafetyAsync(
            int groupId,
            string reason)
        {
            try
            {
                if (_powerSupply != null)
                {
                    var receipt = await _powerSupply.DisableGroupForSafetyAsync(
                            groupId,
                            reason ?? "SafetyIsolation",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return receipt != null && receipt.ConfirmedOff
                        ? (true, string.Empty)
                        : (false, receipt?.Error ?? "PowerOffReceiptMissing");
                }
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"电源组{groupId}安全关闭未得到可靠回读：{ex.Message}",
                    "程控电源",
                    ex);
                return (false, ex.GetBaseException().Message);
            }
            finally
            {
                if (_powerSupply == null || _powerSupply.ActiveGroups.Count == 0)
                    EndPowerSupplyTelemetryRecording();
            }
        }

        /// <summary>
        /// 异步最高优先级 OFF 未获接纳时的同步安全兜底。仅在准入失败/抛异常后调用，
        /// 因而不会与已经接纳的命令形成重复 NI 写。
        /// </summary>
        private bool TryExecuteImmediateOffFallback(
            int channel,
            string stage,
            out string error)
        {
            error = string.Empty;
            try
            {
                var succeeded = CommandEpbOffSafetyImmediate(channel);
                if (succeeded)
                {
                    _log.Warn(
                        $"EPB[{channel}] 异步OFF未接纳，已由同步安全兜底确认断电。Stage={stage}",
                        "DO性能");
                    return true;
                }

                error = "同步安全兜底返回失败";
                _log.Error(
                    $"EPB[{channel}] 异步OFF未接纳且同步安全兜底失败。Stage={stage}",
                    "DO性能");
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                _log.Error(
                    $"EPB[{channel}] 异步OFF未接纳且同步安全兜底抛出异常。" +
                    $"Stage={stage} Error={error}",
                    "DO性能",
                    ex);
                return false;
            }
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

            var primaryChannel = triggeringChannel;
            var faultCorrelationId = correlationId == Guid.Empty ? Guid.NewGuid() : correlationId;
            async Task PublishHardFaultAsync()
            {
                if (classification == FaultClassification.HardwareConfirmed)
                    NotifyRunAuthorizationRevoking(
                        StopSource.AlarmInterlock,
                        $"DAQ硬件故障已确认 Device={device}; {reason}",
                        nameof(LatchDaqGroupHardFault),
                        faultCorrelationId,
                        FaultScope.DaqGroup);
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
                FlushPersistentLog(true);
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
                    SubmitDaqHardFaultIncidentSnapshot(snapshotContext, deviceFault);

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
                        CommitCycleAttemptAfterSnapshotEvidence(primaryChannel, snapshot);
                }
                catch
                {
                    if (primaryChannel > 0)
                        TryFinalizeCurrentCycleAfterSnapshot(primaryChannel, false);
                }
            }

            Task hardFaultPublication = null;
            void StartFaultPublication()
            {
                hardFaultPublication = Task.Factory.StartNew(
                        PublishHardFaultAsync,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .Unwrap();
            }

            if (safetyAlreadyApplied)
            {
                StartFaultPublication();
                ObserveBackgroundTask(
                    hardFaultPublication,
                    "DaqHardFaultHandling",
                    primaryChannel);
                return;
            }

            Dictionary<int, string> rejectedOff = null;
            IReadOnlyDictionary<int, Task<bool>> rejectedFallbackTasks = null;
            ExecuteDaqFaultSafetyFirst(
                () =>
                {
                    foreach (var affectedChannel in orderedChannels)
                        _alarmStopLatch.TryRequestStop(affectedChannel);
                    FreezeAndCancelSafetyChannels(
                        orderedChannels,
                        $"DaqHardFault:{device}",
                        cancelStopTokens: true);
                },
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    orderedChannels,
                    "DaqHardFaultOffAdmissionRejected",
                    "DaqHardFaultOffSubmissionException"),
                () => StartElectricalGroupSafetyDisables(
                    orderedChannels,
                    $"DAQ硬件故障断电 Device={device} CorrelationId={faultCorrelationId:N}",
                    "DaqHardFaultPowerDisable"),
                StartFaultPublication,
                () => rejectedFallbackTasks = StartIndependentSafetyFallbackTasks(
                    rejectedOff,
                    (channel, stage) => TryExecuteImmediateOffFallback(
                        channel,
                        stage,
                        out _)));
            // 到这里故障发布任务和全部拒绝项兜底均已真正启动；随后登记监督，即使
            // 某个监督器/日志实现异常缓慢，也不会再阻止兄弟通道取得执行机会。
            ObserveBackgroundTask(
                hardFaultPublication,
                "DaqHardFaultHandling",
                primaryChannel);
            ObserveIndependentSafetyFallbackTasks(
                rejectedFallbackTasks,
                "DaqHardFaultImmediateOffFallback");
        }

        internal static void ExecuteDaqFaultSafetyFirst(
            Action freezeAndCancelAll,
            Action submitOffAll,
            Action startPowerDisable,
            Action publishFault,
            Action startRejectedOffFallbacks)
        {
            ExecuteNonBlockingSafetyIsolationOrder(
                freezeAndCancelAll,
                submitOffAll,
                startPowerDisable,
                publishFault,
                startRejectedOffFallbacks);
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

            var channels = Enumerable.Range(1, 12)
                .Where(channel => (deviceMask & (1L << channel)) != 0)
                .Where(channel => activeCount == 0 ||
                                  IsHydraulicParticipant(channel) ||
                                  _timers.ContainsKey(channel) ||
                                  _runners.ContainsKey(channel))
                .ToArray();
            Dictionary<int, string> rejected = null;
            ExecuteNonBlockingSafetyIsolationOrder(
                () =>
                {
                    if (!backgroundQueue)
                    {
                        foreach (var channel in channels)
                            _alarmStopLatch.TryRequestStop(channel);
                    }
                    FreezeAndCancelSafetyChannels(
                        channels,
                        $"DaqDeviceFaultSafety:{device}",
                        cancelStopTokens: !backgroundQueue);
                },
                () => rejected = SubmitEpbOffHighPriorityBatch(
                    channels,
                    "DaqDeviceFaultOffAdmissionRejected",
                    "DaqDeviceFaultOffSubmissionException"),
                () => StartElectricalGroupSafetyDisables(
                    channels,
                    $"DAQ故障安全断电 Device={device}",
                    "DaqDeviceFaultPowerDisable"),
                // OnDaqDeviceFaultDetected 随后负责事故关联与恢复发布；这里仅需保证
                // producer 返回前 OFF 和共享电源关闭均已启动。
                null,
                () => ScheduleRejectedOffFallbacks(
                    rejected,
                    "DaqDeviceFaultImmediateOffFallback"));
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
                .Select(IncidentSessionPolicy.NormalizeDevice)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _daqIncidentLatch.BeginRun(runId, devices);
            foreach (var device in devices)
                _daqIncidentGrace.TryRemove(device, out _);
            var runKey = $"{runId:N}:{Interlocked.Read(ref _runEpoch)}";
            foreach (var key in _daqIncidentExpectedDevicesByRun.Keys)
                if (!string.Equals(key, runKey, StringComparison.OrdinalIgnoreCase))
                    _daqIncidentExpectedDevicesByRun.TryRemove(key, out _);
            _daqIncidentExpectedDevicesByRun[runKey] = devices;
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
            int primaryChannel = 0,
            Guid correlationId = default)
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
                primaryChannel,
                correlationId);
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
            ObserveEnergizationEdge(channel, energized);
        }

        private async Task WaitForDaqRecoveryAsync(int channel, CancellationToken token)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device)) return;
            if (_daqAutoRecovery.TryGetValue(device, out var context))
            {
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

            if (!_daqFreshnessSafetyCutoffGeneration.ContainsKey(device)) return;
            var stableSince = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (_daqAutoRecovery.TryGetValue(device, out var lateRecovery) &&
                    lateRecovery.Terminal.Current == DaqRecoveryTerminal.None)
                {
                    var completed = await Task.WhenAny(
                            lateRecovery.Completion.Task,
                            Task.Delay(Timeout.Infinite, token))
                        .ConfigureAwait(false);
                    if (completed != lateRecovery.Completion.Task)
                    {
                        token.ThrowIfCancellationRequested();
                        return;
                    }
                    var lateResult = await lateRecovery.Completion.Task.ConfigureAwait(false);
                    if (!lateResult.Recovered)
                        throw new InvalidOperationException(
                            $"DaqRecoveryFailed Device={device} Reason={lateResult.FailureReason}");
                    stableSince = Stopwatch.GetTimestamp();
                }
                var freshness = _acq.GetDaqFreshnessSnapshot(
                    device,
                    _daqLivenessWarnThresholdMs);
                var fresh = freshness?.IsFresh == true &&
                            freshness.CallbackAgeMs <= _daqLivenessWarnThresholdMs &&
                            freshness.ControlEnqueueAgeMs <= _daqLivenessWarnThresholdMs &&
                            freshness.ControlProcessedAgeMs <= _daqLivenessWarnThresholdMs;
                if (!fresh)
                    stableSince = Stopwatch.GetTimestamp();
                else if ((Stopwatch.GetTimestamp() - stableSince) * 1000.0 /
                         Stopwatch.Frequency >= 500)
                {
                    if (TryGetDaqMechanicalRequalification(channel, out var requiredGeneration))
                    {
                        await EnsurePowerSupplyReadyForChannelsAsync(new[] { channel }, token)
                            .ConfigureAwait(false);
                        var plan = GetCompatibleStaggerPlan(new[] { channel });
                        await EnsureMotorReleasedBeforeFormalRejoinAsync(
                                new[] { channel },
                                plan,
                                $"DaqFreshnessRequalification:{device}:Generation={requiredGeneration}",
                                token)
                            .ConfigureAwait(false);
                        CompleteDaqMechanicalRequalification(channel, requiredGeneration);
                    }
                    if (TryClearDaqMechanicalRequalificationFence(device))
                        return;
                }
                await Task.Delay(20, token).ConfigureAwait(false);
            }
        }

        private void RequireDaqMechanicalRequalification(int channel, long generation)
        {
            lock (_daqMechanicalRequalificationGate)
            {
                if (!_daqMechanicalRequalificationGeneration.TryGetValue(channel, out var existing) ||
                    generation > existing)
                    _daqMechanicalRequalificationGeneration[channel] = generation;
            }
        }

        private bool TryGetDaqMechanicalRequalification(int channel, out long generation)
        {
            lock (_daqMechanicalRequalificationGate)
                return _daqMechanicalRequalificationGeneration.TryGetValue(channel, out generation);
        }

        private void CompleteDaqMechanicalRequalification(int channel, long generation)
        {
            lock (_daqMechanicalRequalificationGate)
            {
                if (_daqMechanicalRequalificationGeneration.TryGetValue(channel, out var required) &&
                    required <= generation)
                    _daqMechanicalRequalificationGeneration.Remove(channel);
            }
        }

        private void RequireDaqDeviceMechanicalRequalification(string device, long generation)
        {
            if (string.IsNullOrWhiteSpace(device)) return;
            var channels = GetDaqGroupChannels(device);
            if (channels.Length == 0) return;
            var normalizedGeneration = Math.Max(0, generation);
            _daqFreshnessSafetyCutoffGeneration.AddOrUpdate(
                device,
                normalizedGeneration,
                (_, current) => Math.Max(current, normalizedGeneration));
            foreach (var channel in channels)
                RequireDaqMechanicalRequalification(channel, normalizedGeneration);
        }

        private bool TryClearDaqMechanicalRequalificationFence(string device)
        {
            if (string.IsNullOrWhiteSpace(device)) return true;
            var activeChannels = new HashSet<int>(GetDaqGroupChannels(device));
            lock (_daqMechanicalRequalificationGate)
            {
                foreach (var staleChannel in _daqMechanicalRequalificationGeneration.Keys
                             .Where(channel =>
                                 string.Equals(
                                     _acq.GetDeviceForEpbChannel(channel),
                                     device,
                                     StringComparison.OrdinalIgnoreCase) &&
                                 !activeChannels.Contains(channel))
                             .ToArray())
                    _daqMechanicalRequalificationGeneration.Remove(staleChannel);
                var pending = _daqMechanicalRequalificationGeneration.Keys.Any(channel =>
                    string.Equals(
                        _acq.GetDeviceForEpbChannel(channel),
                        device,
                        StringComparison.OrdinalIgnoreCase));
                if (pending) return false;
                _daqFreshnessSafetyCutoffGeneration.TryRemove(device, out _);
                return true;
            }
        }

        private void ClearDaqMechanicalRequalificationFences()
        {
            _daqFreshnessSafetyCutoffGeneration.Clear();
            _daqFreshnessEscalationWatchGeneration.Clear();
            lock (_daqMechanicalRequalificationGate)
                _daqMechanicalRequalificationGeneration.Clear();
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
            if (fault.Classification == FaultClassification.HardwareConfirmed)
            {
                HandleConfirmedInfrastructureHardwareFault(
                    fault,
                    "液压",
                    "HydraulicHardwareConfirmed");
                return;
            }
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
            FlushPersistentLog(true);
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

        private void HandleConfirmedInfrastructureHardwareFault(
            ControlFault fault,
            string domain,
            string reasonCode)
        {
            var eventChannels = (fault.AffectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var channels = SelectConfirmedGroupDisableChannels(
                eventChannels,
                IsChannelEnabled);
            if (channels.Length == 0)
            {
                if (fault.Scope == FaultScope.ElectricalGroup)
                    StartElectricalGroupSafetyDisables(
                        eventChannels,
                        reasonCode + ":AlreadyDisabled",
                        reasonCode + "PowerDisable");
                NonCriticalObserver.Invoke(
                    ControlFaultRaised,
                    fault,
                    ex => _log?.Warn($"{domain}硬件故障观察者异常，已隔离：{ex.Message}", domain));
                return;
            }

            var alarmUtc = fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc;
            var configuredGroupChannels = fault.Scope == FaultScope.HydraulicGroup &&
                                          fault.GroupId.HasValue
                ? (_cfg.Test.Hydraulics
                       .FirstOrDefault(item => item.Id == fault.GroupId.Value)?.Members ??
                   new List<int>())
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray()
                : eventChannels;
            var reason =
                $"{domain}硬件故障已由连续新鲜证据确认；当前故障组在本次运行中隔离，" +
                $"健康独立组继续运行。Group={fault.GroupId}; Code={fault.Code}; " +
                $"ConfiguredMembers=[{string.Join(",", configuredGroupChannels)}]; " +
                $"EnabledAffected=[{string.Join(",", channels)}]; {fault.Reason} " +
                "共享管路无法自动判断具体漏液卡钳，请现场检查组内成员。";
            Dictionary<int, string> rejectedOff = null;
            ExecuteConfirmedInfrastructureIsolationOrder(
                () =>
                {
                    foreach (var channel in channels)
                    {
                        _nonRecoverableChannelFaultLatch[channel] = 0;
                        _nonRecoverableChannelFaultReasons[channel] = reason;
                        _alarmStopLatch.TryRequestStop(channel);
                    }
                    foreach (var hydraulicGroup in channels
                                 .Select(GetHydraulicGroupForChannel)
                                 .Where(group => group > 0)
                                 .Distinct())
                        _recoveryOwnership.CancelGroup(hydraulicGroup);
                    FreezeAndCancelSafetyChannels(
                        channels,
                        reasonCode,
                        cancelStopTokens: true);
                    foreach (var channel in channels)
                        UnmarkHydraulicParticipant(channel);
                },
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    channels,
                    reasonCode + "OffAdmissionRejected",
                    reasonCode + "OffSubmissionException"),
                () =>
                {
                    if (fault.Scope == FaultScope.ElectricalGroup)
                        StartElectricalGroupSafetyDisables(
                            eventChannels,
                            reasonCode,
                            reasonCode + "PowerDisable");
                },
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    reasonCode + "ImmediateOffFallback"),
                () =>
                {
                    foreach (var channel in channels)
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.AlarmStopped,
                            reasonCode,
                            reason,
                            sourceChannel: channel,
                            affectedChannels: channels,
                            correlationId: fault.CorrelationId);
                },
                () =>
                {
                    // 各通道声光报警独立投递，禁止首个串口/继电器输出迟滞后续通道。
                    // 这里只证明命令已受监督发起；物理灯/蜂鸣器仍需现场点检验收。
                    foreach (var alarmChannel in channels)
                        ObserveBackgroundTask(Task.Run(async () =>
                        {
                            try
                            {
                                if (Alarm != null)
                                    await Alarm.SetAlarmAsync(alarmChannel, true, reason)
                                        .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.Warn(
                                    $"{domain}硬故障声光报警输出失败 EPB[{alarmChannel}]：{ex.Message}",
                                    "报警");
                            }
                        }), reasonCode + "AlarmOutput", alarmChannel);
                    _log.Error(
                        $"{reasonCode}Isolation Channels=[{string.Join(",", channels)}] " +
                        $"CorrelationId={fault.CorrelationId:N} Reason={reason}",
                        domain);
                    if (fault.Scope == FaultScope.HydraulicGroup)
                        _log.Warn(
                            $"HydraulicGroupIsolatedHealthyGroupsContinuing " +
                            $"Hydraulic={fault.GroupId} " +
                            $"Configured=[{string.Join(",", configuredGroupChannels)}] " +
                            $"Isolated=[{string.Join(",", channels)}] " +
                            $"RunId={_activeBatchId:N} RunEpoch={Interlocked.Read(ref _runEpoch)}",
                            "液压协调");
                    FlushPersistentLog(true);
                    NonCriticalObserver.Invoke(
                        ControlFaultRaised,
                        fault,
                        ex => _log?.Warn(
                            $"{domain}硬件故障观察者异常，已隔离：{ex.Message}",
                            domain));
                    foreach (var channel in channels)
                        NonCriticalObserver.Invoke(
                            ChannelAlarmRaised,
                            channel,
                            reason,
                            ex => _log?.Warn(
                                $"EPB[{channel}] {domain}报警观察者异常，已隔离：{ex.Message}",
                                domain));
                });

            ObserveBackgroundTask(Task.Run(async () =>
            {
                foreach (var channel in channels)
                {
                    try { StopChannelOnAlarm(channel); } catch { }
                    try
                    {
                        await AbortHydraulicLeaseForChannelAsync(
                                channel,
                                reasonCode)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"{domain}硬故障隔离归还EPB[{channel}]租约失败：{ex.Message}",
                            domain);
                    }
                    try
                    {
                        await ExportAlarmSnapshotAsync(channel, reason, alarmUtc).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await EnsureLatestStopSnapshotAsync(
                                channel,
                                _currentCycleNumberByChannel.TryGetValue(channel, out var cycle)
                                    ? cycle
                                    : 0,
                                reasonCode)
                            .ConfigureAwait(false);
                    }
                    catch { }
                    TryDisableIdlePowerGroup(channel, reasonCode);
                }
            }), reasonCode + "FaultHandling", channels);
        }

        internal static void ExecuteConfirmedInfrastructureIsolationOrder(
            Action latchFreezeAndCancel,
            Action submitOffAll,
            Action startPowerDisable,
            Action startRejectedOffFallbacks,
            Action commitRuntimeIsolation,
            Action publishDiagnostics)
        {
            if (latchFreezeAndCancel == null)
                throw new ArgumentNullException(nameof(latchFreezeAndCancel));
            if (submitOffAll == null)
                throw new ArgumentNullException(nameof(submitOffAll));
            if (commitRuntimeIsolation == null)
                throw new ArgumentNullException(nameof(commitRuntimeIsolation));

            // 安全动作和本次运行隔离状态均先于日志、UI、串口报警与快照；
            // 任何诊断阻塞都不能延迟故障组OFF。
            latchFreezeAndCancel();
            submitOffAll();
            startPowerDisable?.Invoke();
            startRejectedOffFallbacks?.Invoke();
            commitRuntimeIsolation();
            publishDiagnostics?.Invoke();
        }

        internal static int[] SelectConfirmedGroupDisableChannels(
            IEnumerable<int> affectedChannels,
            Func<int, bool> isEnabled)
        {
            return (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => isEnabled?.Invoke(channel) ?? true)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private HousekeepingBusyState GetHousekeepingBusyState()
        {
            bool stopBusy;
            lock (_stopSafetyGate)
                stopBusy = _stopSafetyTask != null && !_stopSafetyTask.IsCompleted;
            var alarmBusy = _alarmSnapshotGate.CurrentCount == 0 ||
                            _alarmCycleFinalizationRetries.Count > 0 ||
                            Enumerable.Range(1, 12).Any(channel => IsAlarmStopRequested(channel));
            var state = new HousekeepingBusyState
            {
                // A running batch is not itself a stop window.  Retention only
                // defers while physical stop, alarm finalization, or recovery
                // ownership is actually in flight.
                StopBusy = stopBusy,
                RecoveryBusy = _daqAutoRecovery.Values.Any(item =>
                            item != null && item.Terminal.Current == DaqRecoveryTerminal.None),
                AlarmBusy = alarmBusy
            };
            state.RecoveryBusy |= _recoveryOwnership.ActiveCount > 0 ||
                                  _recoveryTaskRegistry.ActiveCount > 0 ||
                                  _recoverableChannelRestartJobs.Count > 0 ||
                                  _hydraulicSoftwareRecoveryGroups.Count > 0 ||
                                  _powerSoftwareRecoveryGroups.Count > 0;
            try
            {
                foreach (var device in new[] { "Dev1", "Dev2" })
                {
                    var snapshot = _persistence?.GetSnapshot(device);
                    if (snapshot == null) continue;
                    state.DaqQueueNonEmpty |= snapshot.QueueDepth > 0;
                    state.DaqQueueAgeMs = Math.Max(state.DaqQueueAgeMs, snapshot.OldestBatchAgeMs);
                }
            }
            catch { state.DaqQueueNonEmpty = true; }
            return state;
        }

        /// <summary>
        /// Recovery/UI may inject a pending logical root before a scan starts.
        /// The controller never reads the checkpoint itself; it only forwards the
        /// explicit protection decision to the low-priority retention worker.
        /// </summary>
        public void ProtectLearningRoot(Guid rootRunId)
        {
            _dataHousekeeping?.ProtectRoot(rootRunId);
        }

        public void ReleaseLearningRoot(Guid rootRunId)
        {
            _dataHousekeeping?.UnprotectRoot(rootRunId);
        }

        private bool IsCurrentLearningChainPath(string path)
        {
            var root = _activeRunChainIdentity?.EffectiveRootRunId ?? Guid.Empty;
            if (root == Guid.Empty) return false;
            var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                _cfg.Test.StoreDir, _cfg.Test.TestName, "LearningCycles", root.ToString("N")))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var full = System.IO.Path.GetFullPath(path ?? string.Empty)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return string.Equals(full, expected, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(expected + System.IO.Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
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
            var executionPermits = selected.ToDictionary(
                channel => channel,
                channel => _channelExecutionFence.Capture(channel));
            if (executionPermits.Any(pair =>
                    !IsChannelExecutionPermitCurrent(pair.Key, pair.Value)))
                throw new OperationCanceledException(
                    $"PowerEnableRejected StaleExecutionPermit " +
                    $"Channels=[{string.Join(",", selected)}]");
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                new[] { token }.Concat(
                    executionPermits.Values.Select(permit => permit.RevocationToken))
                    .ToArray());
            var groups = selected.Select(GetElectricalGroupId).Where(id => id > 0).Distinct().ToArray();
            var waitingLogged = false;
            while (groups.Any(group => _powerSoftwareRecoveryGroups.ContainsKey(group)))
            {
                executionCts.Token.ThrowIfCancellationRequested();
                if (!waitingLogged)
                {
                    waitingLogged = true;
                    var waitingGroups = groups
                        .Where(group => _powerSoftwareRecoveryGroups.ContainsKey(group))
                        .ToArray();
                    _log.Info(
                        $"ExpectedOutputDisabled AwaitUniquePowerRecoveryOwner " +
                        $"Groups=[{string.Join(",", waitingGroups)}] " +
                        $"Channels=[{string.Join(",", selected)}]",
                        "程控电源");
                }
                await Task.Delay(20, executionCts.Token).ConfigureAwait(false);
            }
            if (groups.Length > 0 && groups.All(group => _powerSupply.HasEnergizationPermit(group, out _)))
                return;
            await _powerSupply.PrepareAndEnableAsync(selected, executionCts.Token).ConfigureAwait(false);
            if (executionPermits.Any(pair =>
                    !IsChannelExecutionPermitCurrent(pair.Key, pair.Value)))
                throw new OperationCanceledException(
                    $"PowerEnableCanceled ExecutionPermitRevoked " +
                    $"Channels=[{string.Join(",", selected)}]");
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
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(channels);

            // 进入恢复队列本身就是安全边界。即使同组已有owner，受影响通道也不能
            // 在等待所有权期间继续RUN或进入下一动作相位。
            FreezeAndCancelSafetyChannels(
                channels,
                $"HydraulicSelfHealingQueued:{fault.Code}",
                cancelStopTokens: false);
            var rejectedOff = SubmitEpbOffHighPriorityBatch(
                channels,
                "HydraulicSelfHealingQueuedOffAdmissionRejected",
                "HydraulicSelfHealingQueuedOffSubmissionException");
            ScheduleRejectedOffFallbacks(
                rejectedOff,
                "HydraulicSelfHealingQueuedImmediateOffFallback");
            foreach (var channel in channels)
            {
                UnmarkHydraulicParticipant(channel);
                try
                {
                    ObserveSafetyTask(
                        Task.Run(() => AbortHydraulicLeaseForChannelAsync(
                            channel,
                            "HydraulicSelfHealingQueued:" + fault.Code)),
                        "HydraulicSelfHealingQueuedLeaseAbort",
                        channel);
                }
                catch { }
            }

            // The queued recovery must have a real, not-yet-running worker
            // bound before Recovering is first published.  The old ordering
            // published the state and only then created Task.Run, leaving a
            // short owner/task coverage hole and making the batch caller look
            // like the recovery owner.
            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                var attempt = 0;
                var hardDeadlineReached = false;
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
                CancellationTokenSource hardDeadline = null;
                CancellationTokenSource recoveryLinked = null;
                try
                {
                    hardDeadline = new CancellationTokenSource(RecoveryGroupHardDeadlineMs);
                    TrySealSoftwareRecoveryCycleWindows(
                        cutoffCycles,
                        cutoffUtc,
                        $"Hydraulic:{fault.Code}:OwnerQueued");
                    ownership = await _recoveryOwnership.AcquireAsync(
                            hydraulicId,
                            $"HYDRAULIC:{hydraulicId}:{fault.CorrelationId:N}",
                            RecoveryOwnerPriority.Hydraulic,
                            RecoveryGroupHardDeadlineMs,
                            hardDeadline.Token)
                        .ConfigureAwait(false);
                    recoveryLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        ownership.Token,
                        hardDeadline.Token);
                    var recoveryToken = recoveryLinked.Token;
                    if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels) &&
                        !TryCaptureExactManualPauseOwner(
                            recoveryRunId,
                            recoveryRunEpoch,
                            channels,
                            out _))
                    {
                        return;
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
                    _log.Warn(
                        $"液压组{hydraulicId}软件同步故障进入自愈，不发布全局SystemFault。" +
                        $"Code={fault.Code} Channels=[{string.Join(",", channels)}] Reason={fault.Reason}",
                        "液压协调");
                    while (true)
                    {
                        if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels) &&
                            !TryCaptureExactManualPauseOwner(
                                recoveryRunId,
                                recoveryRunEpoch,
                                channels,
                                out _))
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
                                    recoveryToken,
                                    terminalStatus: "AbortedByHydraulicGroupFault")
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
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (attempt >= SoftwareRecoveryEscalationAttempts)
                            {
                                hardDeadlineReached = IsSoftwareRecoveryRunCurrent(
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    channels);
                                _log.Error(
                                    $"液压组{hydraulicId}软件自愈连续{attempt}次失败，" +
                                    "转入受影响组Stop→Start等价清场：" + ex.Message,
                                    "液压协调");
                                break;
                            }
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
                    if (hardDeadlineReached)
                    {
                        try
                        {
                            await ExecuteAffectedGroupResetAsync(
                                    channels,
                                    $"HydraulicRecoveryHardDeadline:{fault.Code}",
                                    fault.CorrelationId,
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    RecoveryTargetPhase.Formal)
                                .ConfigureAwait(false);
                        }
                        catch (Exception resetError)
                        {
                            _log.Error(
                                $"液压组{hydraulicId}硬期限清场失败，保持安全终态：{resetError.Message}",
                                "液压协调",
                                resetError);
                        }
                    }

                    recoveryIncident?.CompleteAfterTerminal(contract =>
                        CommitRecoveryIncidentStateForRelease(
                            contract,
                            "HydraulicRecoveryTerminalWithoutRejoin",
                            "液压软件自愈未完成重入，已保持受影响通道安全终态。 "));
                }
                };
            }

            var started = TryBeginRecoveryIncident(
                "HydraulicSoftwareRecovery",
                recoveryRunId,
                recoveryRunEpoch,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                IsFormalPhaseCommitted
                    ? RecoveryTargetPhase.Formal
                    : RecoveryTargetPhase.Startup,
                fault.CorrelationId,
                channels,
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    foreach (var channel in contract.Channels ?? Array.Empty<int>())
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "HydraulicSelfHealingQueued",
                            "液压同步软件自愈已排队；通道已冻结并提交断电，等待同组恢复所有权。",
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                },
                out recoveryIncident);
            if (!started)
            {
                _hydraulicSoftwareRecoveryGroups.TryRemove(hydraulicId, out _);
                return;
            }
            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "HydraulicSoftwareRecovery",
                    _activeBatchId,
                    channels.FirstOrDefault());
            }
            catch (Exception observeError)
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(contract.Channels.FirstOrDefault(), "HydraulicRecoveryObserveFailed"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "HydraulicRecoveryObserveFailed",
                        $"液压软件自愈任务登记失败，已保持安全终态：{observeError.Message}");
                });
                _hydraulicSoftwareRecoveryGroups.TryRemove(hydraulicId, out _);
                return;
            }
            if (!recoveryIncident.Start())
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(contract.Channels.FirstOrDefault(), "HydraulicRecoveryStartRejected"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "HydraulicRecoveryStartRejected",
                        "液压软件自愈启动许可被拒绝，已保持安全终态。 ");
                });
                _hydraulicSoftwareRecoveryGroups.TryRemove(hydraulicId, out _);
            }
        }

        private bool IsHydraulicGroupRecoveryActiveForChannel(int channel)
        {
            var hydraulicId = GetHydraulicGroupForChannel(channel);
            return hydraulicId > 0 &&
                   _hydraulicSoftwareRecoveryGroups.ContainsKey(hydraulicId);
        }

        internal static bool ShouldDelegateFormalPersistenceToHydraulicRecovery(
            bool hydraulicRecoveryActive,
            bool motorOffConfirmed,
            bool hydraulicReleased)
        {
            // Delegation is data ownership, never a physical-safety waiver.  The
            // formal slot may release healthy groups only after the affected member
            // is physically OFF and its shared pressure lease has been released.
            return hydraulicRecoveryActive && motorOffConfirmed && hydraulicReleased;
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
            var next = new PowerSupplyTelemetryCsvRecorder(
                path,
                _log,
                null,
                runId.ToString("N"),
                Interlocked.Read(ref _runEpoch));
            var previous = Interlocked.Exchange(ref _powerTelemetryRecorder, next);
            // Detach immediately; sealing/spill replay continues on the
            // recorder's private background shutdown worker so StopAll does
            // not wait on slow telemetry storage.
            previous?.DetachAndShutdown();
            _log.Info($"程控电源连续遥测文件：{path}", "程控电源");
        }

        private void EndPowerSupplyTelemetryRecording()
        {
            var recorder = Interlocked.Exchange(ref _powerTelemetryRecorder, null);
            // StopAll must not inherit the recorder's bounded drain waits.
            recorder?.DetachAndShutdown();
        }

        private void OnPowerSupplyFaultRaised(PowerSupplyFault fault)
        {
            if (fault == null) return;
            var controlFault = new ControlFault(
                "PowerSupply" + fault.Code,
                fault.Reason,
                FaultScope.ElectricalGroup,
                fault.AffectedChannels?.Distinct().ToArray() ?? Array.Empty<int>(),
                fault.ElectricalGroupId,
                fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc,
                Guid.NewGuid(),
                fault.Classification);
            if (fault.Classification == FaultClassification.HardwareConfirmed)
            {
                HandleConfirmedInfrastructureHardwareFault(
                    controlFault,
                    "程控电源",
                    "PowerSupplyHardwareConfirmed");
                NonCriticalObserver.Invoke(
                    PowerSupplyFaultRaised,
                    fault,
                    ex => _log?.Warn($"程控电源硬故障观察者异常，已隔离：{ex.Message}", "程控电源"));
                return;
            }
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
                        CommitCycleAttemptAfterSnapshotEvidence(channel, snapshotEvidence);
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
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(channels);

            // 同优先级电源恢复可能排队；排队不能成为继续带电运行的窗口。
            FreezeAndCancelSafetyChannels(
                channels,
                $"PowerSupplySelfHealingQueued:{sourceFault.Code}",
                cancelStopTokens: false);
            var rejectedOff = SubmitEpbOffHighPriorityBatch(
                channels,
                "PowerSupplySelfHealingQueuedOffAdmissionRejected",
                "PowerSupplySelfHealingQueuedOffSubmissionException");
            StartElectricalGroupSafetyDisables(
                channels,
                "PowerSupplySelfHealingQueued:" + sourceFault.Reason,
                "PowerSupplySelfHealingQueuedPowerDisable");
            ScheduleRejectedOffFallbacks(
                rejectedOff,
                "PowerSupplySelfHealingQueuedImmediateOffFallback");
            foreach (var channel in channels)
            {
                UnmarkHydraulicParticipant(channel);
                try
                {
                    ObserveSafetyTask(
                        Task.Run(() => AbortHydraulicLeaseForChannelAsync(
                            channel,
                            "PowerSupplySelfHealingQueued:" + sourceFault.Code)),
                        "PowerSupplySelfHealingQueuedLeaseAbort",
                        channel);
                }
                catch { }
            }

            // Establish/bind the actual ownership worker before publishing the
            // first Recovering state.  A queued power recovery must never use
            // the fault callback or a monitor Task as its owner.
            RecoveryIncidentHandle recoveryIncident = null;
            Func<Task> BuildRecoveryWorker()
            {
                return async () =>
                {
                var attempt = 0;
                var hardDeadlineReached = false;
                HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease[] ownerships = null;
                var exitDisposition = RecoveryExitDisposition.SafeIdleFault;
                CancellationTokenSource hardDeadline = null;
                CancellationTokenSource recoveryLinked = null;
                try
                {
                    hardDeadline = new CancellationTokenSource(RecoveryGroupHardDeadlineMs);
                    TrySealSoftwareRecoveryCycleWindows(
                        cutoffCycles,
                        cutoffUtc,
                        $"PowerSupply:{sourceFault.Code}:OwnerQueued");
                    ownerships = await AcquireRecoveryOwnershipsAsync(
                            $"POWER:{groupId}:{controlFault.CorrelationId:N}",
                            RecoveryOwnerPriority.PowerSupply,
                            channels,
                            hardDeadline.Token,
                            RecoveryGroupHardDeadlineMs)
                        .ConfigureAwait(false);
                    recoveryLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        ownerships.Select(ownership => ownership.Token)
                            .Concat(new[] { hardDeadline.Token })
                            .ToArray());
                    var recoveryToken = recoveryLinked.Token;
                    if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels) &&
                        !TryCaptureExactManualPauseOwner(
                            recoveryRunId,
                            recoveryRunEpoch,
                            channels,
                            out _))
                    {
                        exitDisposition = RecoveryExitDisposition.Superseded;
                        return;
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
                    _log.Warn(
                        $"电源组{groupId}系统类故障进入组内自愈，不发布全局SystemFault。" +
                        $"Code={sourceFault.Code} Channels=[{string.Join(",", channels)}] Reason={sourceFault.Reason}",
                        "程控电源");
                    while (true)
                    {
                        if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels) &&
                            !TryCaptureExactManualPauseOwner(
                                recoveryRunId,
                                recoveryRunEpoch,
                                channels,
                                out _))
                        {
                            exitDisposition = RecoveryExitDisposition.Superseded;
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

                            if (TryCaptureExactManualPauseOwner(
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    channels,
                                    out var exactPause))
                            {
                                if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                                        cutoffCycles,
                                        cutoffUtc,
                                        $"PowerSupply:{sourceFault.Code}:HeldForPause",
                                        _daqPersistenceRecoveryTimeoutMs,
                                        recoveryToken)
                                    .ConfigureAwait(false))
                                    throw new SoftwareSelfHealingRetryException(
                                        "人工暂停期间程控电源恢复圈截止尚未耐久闭合。");
                                exitDisposition = RecoveryExitDisposition.HeldForManualPause;
                                foreach (var channel in channels.Where(ch => _timers.ContainsKey(ch)))
                                    PublishChannelRuntimeState(
                                        channel,
                                        GetDaqRecoveredHeldRuntimeState(exactPause.State),
                                        "PowerSupplyRecoveredHeldForManualPause",
                                        "程控电源已确认 OFF 且耐久边界闭合；保持人工暂停运行载体。",
                                        affectedChannels: channels,
                                        correlationId: controlFault.CorrelationId);
                                return;
                            }

                            await RecoveryStageDeadline.RunAsync(
                                    "PowerPrepareAndEnable",
                                    RecoveryStageTimeoutMs,
                                    ct => _powerSupply.PrepareAndEnableAsync(channels, ct),
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!IsSoftwareRecoveryRunCurrent(recoveryRunId, recoveryRunEpoch, channels))
                            {
                                exitDisposition = RecoveryExitDisposition.Superseded;
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
                                        ChannelRuntimeState.Starting,
                                        "PowerSupplyRecoveredForStartup",
                                        "电源控制链已恢复；返回启动定位重试态，禁止提前进入正式节律。",
                                        affectedChannels: channels,
                                        correlationId: controlFault.CorrelationId,
                                        allowTerminalReset: true,
                                        allowSystemFaultReset: true,
                                        runIdOverride: recoveryRunId);
                                _emergencyPowerGroupLatch.TryRemove(
                                    groupId,
                                    controlFault.CorrelationId);
                                exitDisposition = RecoveryExitDisposition.RejoinedRunning;
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
                                exitDisposition = RecoveryExitDisposition.HeldForManualPause;
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
                            _emergencyPowerGroupLatch.TryRemove(
                                groupId,
                                controlFault.CorrelationId);
                            exitDisposition = RecoveryExitDisposition.RejoinedRunning;
                            return;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (attempt >= SoftwareRecoveryEscalationAttempts)
                            {
                                hardDeadlineReached = IsSoftwareRecoveryRunCurrent(
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    channels);
                                _log.Error(
                                    $"电源组{groupId}软件自愈连续{attempt}次失败，" +
                                    "转入受影响组Stop→Start等价清场：" + ex.Message,
                                    "程控电源");
                                break;
                            }
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
                    exitDisposition = RecoveryExitDisposition.CancelledByStop;
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
                    if (hardDeadlineReached)
                    {
                        try
                        {
                            await ExecuteAffectedGroupResetAsync(
                                    channels,
                                    $"PowerRecoveryHardDeadline:{sourceFault.Code}",
                                    controlFault.CorrelationId,
                                    recoveryRunId,
                                    recoveryRunEpoch,
                                    RecoveryTargetPhase.Formal)
                                .ConfigureAwait(false);
                        }
                        catch (Exception resetError)
                        {
                            _log.Error(
                                $"电源组{groupId}硬期限清场失败，保持安全终态：{resetError.Message}",
                                "程控电源",
                                resetError);
                        }
                    }

                    if (exitDisposition == RecoveryExitDisposition.RejoinedRunning)
                    {
                        _emergencyPowerGroupLatch.TryRemove(
                            groupId,
                            controlFault.CorrelationId);
                        recoveryIncident?.CompleteAfterTerminal(_ => { });
                    }
                    else if (exitDisposition == RecoveryExitDisposition.HeldForManualPause)
                    {
                        _emergencyPowerGroupLatch.TryRemove(
                            groupId,
                            controlFault.CorrelationId);
                        recoveryIncident?.CompleteAfterTerminal(contract =>
                        {
                            var pause = CaptureBatchPauseSnapshot();
                            foreach (var channel in contract.Channels ?? Array.Empty<int>())
                            {
                                if (!_timers.ContainsKey(channel)) continue;
                                PublishChannelRuntimeState(
                                    channel,
                                    GetDaqRecoveredHeldRuntimeState(pause.State),
                                    "PowerRecoveryHeldForManualPauseTerminal",
                                    "程控电源恢复以人工暂停终态释放 Owner；Timer/Runner 保留。",
                                    affectedChannels: contract.Channels,
                                    correlationId: contract.IncidentId,
                                    runIdOverride: contract.RunId,
                                    runEpochOverride: contract.RunEpoch);
                            }
                        });
                    }
                    else
                    {
                        _emergencyPowerGroupLatch.TryFail(
                            groupId,
                            controlFault.CorrelationId,
                            hardDeadlineReached
                                ? "PowerRecoveryHardDeadline"
                                : exitDisposition.ToString());
                        recoveryIncident?.CompleteAfterTerminal(contract =>
                            CommitRecoveryIncidentStateForRelease(
                                contract,
                                "PowerRecovery" + exitDisposition,
                                "程控电源恢复未完成运行态重入，已保持安全终态。"));
                    }
                }
                };
            }

            var started = TryBeginRecoveryIncident(
                "PowerSupplySoftwareRecovery",
                recoveryRunId,
                recoveryRunEpoch,
                RecoveryOwnerKind.PowerRecovery,
                IsFormalPhaseCommitted
                    ? RecoveryTargetPhase.Formal
                    : RecoveryTargetPhase.Startup,
                controlFault.CorrelationId,
                channels,
                _ => BuildRecoveryWorker(),
                contract =>
                {
                    foreach (var channel in contract.Channels ?? Array.Empty<int>())
                        PublishRecoveryIncidentState(
                            channel,
                            ChannelRuntimeState.Recovering,
                            "PowerSupplySelfHealingQueued",
                            "程控电源软件自愈已排队；通道已冻结并提交断电，等待同组恢复所有权。",
                            affectedChannels: contract.Channels,
                            correlationId: contract.IncidentId,
                            recoveryOwnerKind: contract.OwnerKind,
                            recoveryTargetPhase: contract.TargetPhase,
                            recoveryOwnerId: contract.OwnerId,
                            recoveryOwnerGeneration: contract.RunEpoch);
                },
                out recoveryIncident);
            if (!started)
            {
                _powerSoftwareRecoveryGroups.TryRemove(groupId, out _);
                _emergencyPowerGroupLatch.TryFail(
                    groupId,
                    controlFault.CorrelationId,
                    "PowerRecoveryIncidentRegistrationRejected");
                return;
            }
            try
            {
                _taskSupervisor.Observe(
                    recoveryIncident.WorkerTask,
                    "PowerSupplySoftwareRecovery",
                    _activeBatchId,
                    channels.FirstOrDefault());
            }
            catch (Exception observeError)
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(contract.Channels.FirstOrDefault(), "PowerRecoveryObserveFailed"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "PowerRecoveryObserveFailed",
                        $"程控电源软件自愈任务登记失败，已保持安全终态：{observeError.Message}");
                });
                _powerSoftwareRecoveryGroups.TryRemove(groupId, out _);
                _emergencyPowerGroupLatch.TryFail(
                    groupId,
                    controlFault.CorrelationId,
                    "PowerRecoveryObserveFailed");
                return;
            }
            if (!recoveryIncident.Start())
            {
                recoveryIncident.CompleteAfterTerminal(contract =>
                {
                    try { CommandEpbOffHighPriority(contract.Channels.FirstOrDefault(), "PowerRecoveryStartRejected"); }
                    catch { }
                    PublishRecoverySafeTerminal(
                        contract,
                        "PowerRecoveryStartRejected",
                        "程控电源软件自愈启动许可被拒绝，已保持安全终态。 ");
                });
                _powerSoftwareRecoveryGroups.TryRemove(groupId, out _);
                _emergencyPowerGroupLatch.TryFail(
                    groupId,
                    controlFault.CorrelationId,
                    "PowerRecoveryStartRejected");
            }
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
            {
                if (channel < 1 || channel > 12) return true;
                var permit = _channelExecutionFence.Capture(channel);
                return !_channelExecutionFence.IsCurrent(permit) ||
                       permit.RunEpoch != expectedRunEpoch;
            }))
                return false;
            // 硬件确认/报警停机是恢复事务的终止事实。旧的软件自愈任务即使仍持有
            // Timer、Runner 或批次身份，也不得在隔离命令之后继续重新使能电源。
            if (!CanContinueSoftwareRecovery(
                    affected,
                    channel => _nonRecoverableChannelFaultLatch.ContainsKey(channel),
                    IsAlarmStopRequested))
                return false;
            if (affected.Any(channel =>
                    _timers.ContainsKey(channel) ||
                    _runners.ContainsKey(channel) ||
                    _stopCtsByChannel.ContainsKey(channel)))
                return true;

            return expectedRunId != Guid.Empty && IsBatchSessionActive;
        }

        internal static bool CanContinueSoftwareRecovery(
            IEnumerable<int> affectedChannels,
            Func<int, bool> isNonRecoverable,
            Func<int, bool> isAlarmStopRequested)
        {
            var affected = (affectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .ToArray();
            return affected.Length > 0 && affected.All(channel =>
                !(isNonRecoverable?.Invoke(channel) ?? false) &&
                !(isAlarmStopRequested?.Invoke(channel) ?? false));
        }

        private void OnRunnerWarningRaised(int channel, string reason)
        {
            _log.Warn($"EPB[{channel}] 自适应软预警：{reason}", "EPB");
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
            // Learning/qualification transactions defer persistence until the
            // corresponding evidence has been durably sealed.  This keeps an
            // aborted or unsealed sample out of the project model while
            // retaining the runner's in-memory algorithm state.
            if (_deferredAdaptivePersistence.ContainsKey(profile.Channel)) return;
            try
            {
                _adaptiveProfileStore.Save(profile);
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{profile.Channel}] 保存自适应模型失败：{ex.Message}", "EPB");
            }
        }

        private string SaveAdaptiveProfileWithReceipt(EpbAdaptiveProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (_adaptiveProfileStore == null)
                throw new InvalidOperationException("自适应模型存储未初始化。");
            var receipt = _adaptiveProfileStore.SaveWithReceipt(profile);
            _learningModelReceipts[profile.Channel] = receipt;
            return receipt;
        }

        private void BeginLearningProfileTransaction(int channel)
        {
            if (channel >= 1 && channel <= 12)
                _deferredAdaptivePersistence[channel] = 0;
        }

        private void EndLearningProfileTransaction(int channel)
        {
            _deferredAdaptivePersistence.TryRemove(channel, out _);
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
            DateTime alarmUtc,
            AlarmLiveDiagnosticsSnapshot triggerDiagnostics = null)
        {
            triggerDiagnostics ??= CaptureAlarmLiveDiagnostics(
                alarmChannel,
                alarmUtc);
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

            if (TryGetCycleAttempt(alarmChannel, alarmCycleNumber, out var attempt))
            {
                if (attempt.BeginState == CycleAttemptBeginState.Registered)
                {
                    attempt.CancelAttempt();
                    _log.Warn(
                        $"EPB[{alarmChannel}] Recorder.BeginCycle尚未返回，报警快照延后重试。" +
                        $"Attempt={attempt.AttemptId} Cycle={attempt.Cycle}",
                        "落盘");
                    return null;
                }
                if (attempt.BeginState == CycleAttemptBeginState.Failed)
                {
                    attempt.AbortOnce(() => true, RemoveCycleAttemptAfterDurableTerminal);
                    return null;
                }
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
            var terminalDiagnostics = CaptureAlarmLiveDiagnostics(
                alarmChannel,
                DateTime.UtcNow);

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
                            else
                            {
                                // 正式圈永久报警发生在 completed 提交之后。此时没有活动 attempt，
                                // 但 confirmedTerminalCycleNumber 已被同步冻结；优先从不可变终态回读。
                                alarmEvidence = AlarmCycleSnapshotRecovery.TryExportFinalizedCycle(
                                    recorder,
                                    ch,
                                    alarmCycleNumber,
                                    subDir,
                                    new AlarmCycleSnapshotEvidence
                                    {
                                        StorageFormat = _alarmStorageLevel.ToString()
                                    });
                                if (alarmEvidence?.IsValid == true)
                                {
                                    _log.Info(
                                        $"EPB[{ch}] 已完成报警触发圈从持久化索引回读成功。" +
                                        $"Cycle={alarmCycleNumber}",
                                        "落盘");
                                }
                                else if (recorder is ICycleAttemptEvidenceExporter exporter)
                                {
                                    var saveAlarmCsv = _alarmStorageLevel == StorageFormatLevel.CsvOnly ||
                                                        _alarmStorageLevel == StorageFormatLevel.CsvAndBin;
                                    var saveAlarmBin = _alarmStorageLevel == StorageFormatLevel.BinOnly ||
                                                        _alarmStorageLevel == StorageFormatLevel.CsvAndBin;
                                    var frozen = exporter.ExportCycleAttemptTo(
                                        ch,
                                        alarmCycleNumber,
                                        subDir,
                                        saveAlarmCsv,
                                        saveAlarmBin);
                                    alarmEvidence = frozen == null
                                        ? new AlarmCycleSnapshotEvidence
                                        {
                                            StorageFormat = _alarmStorageLevel.ToString(),
                                            ValidationError = "冻结故障圈导出器未返回证据。"
                                        }
                                        : EpbDiskWriter.ValidateAlarmCycleSnapshotFiles(
                                            frozen.CsvPath,
                                            frozen.BinPath,
                                            ch,
                                            alarmCycleNumber,
                                            false,
                                            _alarmStorageLevel);
                                    alarmEvidence.WasClaimed = false;
                                    alarmEvidence.FinalStatus = "FrozenAbortedCycle";
                                }
                            }
                            if (alarmEvidence?.IsValid == true)
                            {
                                if (recorder is IAlarmRecentCycleEvidenceExporter alarmRecentExporter)
                                    alarmRecentExporter.FlushRecentForAlarm(
                                        ch,
                                        lastN,
                                        subDir,
                                        includeRunningCycle: false);
                                else
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
                            if (recorder is IAlarmRecentCycleEvidenceExporter alarmRecentExporter)
                                alarmRecentExporter.FlushRecentForAlarm(
                                    ch,
                                    sameGroupLastN,
                                    subDir,
                                    includeRunningCycle: true);
                            else
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
                    CsvPath = _alarmStorageLevel == StorageFormatLevel.BinOnly
                        ? null
                        : System.IO.Path.Combine(
                            alarmSubDir,
                            $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.csv"),
                    BinPath = _alarmStorageLevel == StorageFormatLevel.CsvOnly
                        ? null
                        : System.IO.Path.Combine(
                            alarmSubDir,
                            $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.bin"),
                    StorageFormat = _alarmStorageLevel.ToString(),
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
                    WriteAlarmLiveDiagnostics(
                        snapshotDir,
                        alarmChannel,
                        alarmCycleNumber,
                        reason,
                        triggerDiagnostics,
                        terminalDiagnostics);
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警现场诊断字段导出失败：EPB[{alarmChannel}] {ex.Message}", "落盘");
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
            InstallStopPreemptionFence(context);
            lock (_stopSafetyGate)
            {
                if (_stopSafetyTask != null && !_stopSafetyTask.IsCompleted)
                {
                    // All callers join the same active physical transaction.
                    // FinalExit is a request source, not authority to enqueue
                    // a second core behind an already-running safety action.
                    return CompleteStopRequestForSource(_stopSafetyTask, context.Source);
                }
                if (_lastStopSafetyResult != null && !IsBatchSessionActive &&
                    _activeBatchId == Guid.Empty &&
                    _lastStopSafetyResult.CanRestartInProcess &&
                    CanReuseStopResultForSource(
                        _lastStopSafetyResult.Source,
                        context.Source) &&
                    CaptureLogicalQuiescenceSnapshot().IsQuiescent)
                    return CompleteStopRequestForSource(
                        Task.FromResult(_lastStopSafetyResult.Clone(reused: true)), context.Source);
                var generation = Interlocked.Increment(ref _stopSafetyGeneration);
                _stopSafetyTask = RunBoundedStopSafetyAsync(context, token, generation);
                _stopSafetyTaskSource = context.Source;
                if (IsFinalExitStopSource(context.Source))
                    _stopSafetyFinalExitTask = _stopSafetyTask;
                return _stopSafetyTask;
            }
        }

        private Task<StopSafetyResult> CompleteStopRequestForSource(Task<StopSafetyResult> physical, StopSource source)
        {
            if (!IsFinalExitStopSource(source)) return physical;
            if (_stopSafetyFinalExitTask != null &&
                (!_stopSafetyFinalExitTask.IsCompleted ||
                 (_stopSafetyFinalExitTask.Status == TaskStatus.RanToCompletion &&
                  _stopSafetyFinalExitTask.Result?.PersistenceBoundaryConfirmed == true))) return _stopSafetyFinalExitTask;
            // Same physical transaction, one final data-resource closure. A ManualUi result
            // proves the data boundary but intentionally keeps its writer reusable until exit.
            _stopSafetyFinalExitTask = CompleteFinalPersistenceAsync(physical);
            return _stopSafetyFinalExitTask;
        }

        private async Task<StopSafetyResult> CompleteFinalPersistenceAsync(Task<StopSafetyResult> physical)
        {
            var result = (await physical.ConfigureAwait(false))?.Clone(reused: true);
            if (result?.PersistenceBoundaryConfirmed != true || _persistence == null) return result;
            try
            {
                if (!await _persistence.ShutdownAsync(5000).ConfigureAwait(false))
                    throw new TimeoutException("FinalPersistenceShutdownIncomplete");
            }
            catch (Exception ex)
            {
                result.PersistenceBoundaryConfirmed = false;
                result.PersistenceError = ex.Message;
                result.RequiresProcessRestart = true;
            }
            return result;
        }

        public async Task<StopRequestReceipt> StopAllWithReceiptAsync(
            StopContext context,
            CancellationToken token = default)
        {
            context ??= StopContext.Legacy(nameof(StopAllWithReceiptAsync));
            var requestedUtc = DateTime.UtcNow;
            StopSafetyResult previous;
            bool joined;
            lock (_stopSafetyGate)
            {
                previous = _lastStopSafetyResult?.Clone();
                joined = _stopSafetyTask != null && !_stopSafetyTask.IsCompleted;
            }
            var result = await StopAllAsync(context, token).ConfigureAwait(false);
            return new StopRequestReceipt
            {
                RequestId = Guid.NewGuid(),
                Source = context.Source,
                RequestedUtc = requestedUtc,
                StartedUtc = result?.StartedUtc ?? requestedUtc,
                CompletedUtc = DateTime.UtcNow,
                PhysicalTransactionId = result?.SafetyTransactionId ?? Guid.Empty,
                JoinedActiveTransaction = joined,
                PreviousTransactionId = previous?.SafetyTransactionId ?? Guid.Empty,
                PreviousOutcome = previous?.Outcome ?? StopSafetyOutcome.Unknown,
                Result = result?.Clone()
            };
        }

        private sealed class AlarmLiveDiagnosticsSnapshot
        {
            internal DateTime CapturedUtc;
            internal int ElectricalGroupId;
            internal DateTime? PowerTelemetryUtc;
            internal double? MeasuredVoltageV;
            internal double? MeasuredCurrentA;
            internal bool? OutputEnabled;
            internal bool? ProtectionTripped;
            internal bool DigitalOutputCommandActive;
        }

        private AlarmLiveDiagnosticsSnapshot CaptureAlarmLiveDiagnostics(
            int channel,
            DateTime capturedUtc)
        {
            var electricalGroupId = GetElectricalGroupId(channel);
            PswSnapshot power = null;
            try { power = _powerSupply?.GetLatestSnapshot(electricalGroupId); }
            catch { }
            var commandActive = false;
            try { commandActive = IsChannelEnergized(channel); }
            catch { }
            return new AlarmLiveDiagnosticsSnapshot
            {
                CapturedUtc = capturedUtc.Kind == DateTimeKind.Utc
                    ? capturedUtc
                    : capturedUtc.ToUniversalTime(),
                ElectricalGroupId = electricalGroupId,
                PowerTelemetryUtc = power?.TimestampUtc,
                MeasuredVoltageV = power?.MeasuredVoltage,
                MeasuredCurrentA = power?.MeasuredCurrent,
                OutputEnabled = power?.OutputEnabled,
                ProtectionTripped = power?.ProtectionTripped,
                DigitalOutputCommandActive = commandActive
            };
        }

        private static void AppendAlarmDiagnosticsPhase(
            StringBuilder sb,
            string name,
            AlarmLiveDiagnosticsSnapshot snapshot,
            bool appendComma)
        {
            snapshot ??= new AlarmLiveDiagnosticsSnapshot();
            sb.Append("\n  \"").Append(name).Append("\": {")
                .Append("\n    \"CapturedUtc\": \"")
                .Append(snapshot.CapturedUtc == default
                    ? string.Empty
                    : snapshot.CapturedUtc.ToUniversalTime().ToString("o"))
                .Append("\",")
                .Append("\n    \"ElectricalGroup\": ")
                .Append(snapshot.ElectricalGroupId).Append(',')
                .Append("\n    \"PowerTelemetryUtc\": ")
                .Append(snapshot.PowerTelemetryUtc.HasValue
                    ? "\"" + snapshot.PowerTelemetryUtc.Value.ToUniversalTime().ToString("o") + "\""
                    : "null").Append(',')
                .Append("\n    \"PowerSupply\": {")
                .Append("\n      \"MeasuredVoltageV\": ")
                .Append(snapshot.MeasuredVoltageV.HasValue
                    ? snapshot.MeasuredVoltageV.Value.ToString("F6", CultureInfo.InvariantCulture)
                    : "null").Append(',')
                .Append("\n      \"MeasuredCurrentA\": ")
                .Append(snapshot.MeasuredCurrentA.HasValue
                    ? snapshot.MeasuredCurrentA.Value.ToString("F6", CultureInfo.InvariantCulture)
                    : "null").Append(',')
                .Append("\n      \"OutputEnabled\": ")
                .Append(snapshot.OutputEnabled.HasValue
                    ? snapshot.OutputEnabled.Value ? "true" : "false"
                    : "null").Append(',')
                .Append("\n      \"ProtectionTripped\": ")
                .Append(snapshot.ProtectionTripped.HasValue
                    ? snapshot.ProtectionTripped.Value ? "true" : "false"
                    : "null")
                .Append("\n    },")
                .Append("\n    \"DigitalOutputCommandActive\": ")
                .Append(snapshot.DigitalOutputCommandActive ? "true" : "false").Append(',')
                .Append("\n    \"DigitalOutputReadback\": \"NotMeasured\"")
                .Append("\n  }");
            if (appendComma) sb.Append(',');
        }

        private void WriteAlarmLiveDiagnostics(
            string snapshotDirectory,
            int channel,
            int cycleNumber,
            string reason,
            AlarmLiveDiagnosticsSnapshot triggerDiagnostics,
            AlarmLiveDiagnosticsSnapshot terminalDiagnostics)
        {
            var profile = GetAdaptiveProfile(channel);
            var peakHistory = profile.ForwardPeakErrorHistoryA ?? new List<double>();
            var peakDeltas = peakHistory
                .Skip(Math.Max(0, peakHistory.Count - 10))
                .Select(value => value.ToString("F6", CultureInfo.InvariantCulture));
            var sb = new StringBuilder();
            sb.Append("{\n  \"Channel\": ").Append(channel).Append(',')
                .Append("\n  \"Cycle\": ").Append(cycleNumber).Append(',')
                .Append("\n  \"Reason\": \"").Append(JsonEscape(reason)).Append("\",");
            AppendAlarmDiagnosticsPhase(sb, "Trigger", triggerDiagnostics, appendComma: true);
            AppendAlarmDiagnosticsPhase(sb, "Terminal", terminalDiagnostics, appendComma: true);
            sb.Append("\n  \"Recent10PeakErrorA\": [")
                .Append(string.Join(",", peakDeltas)).Append("],")
                .Append("\n  \"LoadTerminalVoltage\": \"NotMeasured\",")
                .Append("\n  \"RelayFeedback\": \"NotMeasured\",")
                .Append("\n  \"TerminalTemperature\": \"NotMeasured\"\n}\n");
            File.WriteAllText(
                Path.Combine(snapshotDirectory, "alarm-live-diagnostics.json"),
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private void InstallStopPreemptionFence(StopContext context)
        {
            // This is the synchronous linearization point for every StopAll
            // caller.  It runs before the shared stop-task lock and before any
            // stage worker can wait on a recovery/channel gate.  Existing
            // cycle/power-enable linked tokens are cancelled immediately;
            // physical OFF remains owned by the ordered stop transaction.
            lock (_recoveryAdmissionGate)
                Volatile.Write(ref _energizationRevoked, 1);
            foreach (var channel in Enumerable.Range(1, 12))
            {
                try
                {
                    RevokeChannelExecutionPermit(
                        channel,
                        "StopAllAdmission:" + (context?.Source.ToString() ?? "Unknown"));
                }
                catch { }
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
            var generation = Interlocked.Increment(ref _stopSafetyGeneration);
            return await RunBoundedStopSafetyAsync(context, token, generation).ConfigureAwait(false);
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
            lock (_hardwareReleaseGate)
            {
                // Keep the lock for the full synchronous release. Concurrent callers
                // wait for the owner to finish and then observe the completed state;
                // same-thread re-entry is rejected while the outer owner continues.
                if (_hardwareReleaseState == 2) return;
                if (_hardwareReleaseState == 1) return;
                _hardwareReleaseState = 1;
                Interlocked.Increment(ref _hardwareReleaseExecutionCount);
                try
                {
                    ReleaseHardwareForRestartCore();
                }
                catch
                {
                    Volatile.Write(ref _hardwareReleaseState, 0);
                    throw;
                }
                Volatile.Write(ref _hardwareReleaseState, 2);
            }
        }

        internal int HardwareReleaseExecutionCount =>
            Volatile.Read(ref _hardwareReleaseExecutionCount);

        private void ReleaseHardwareForRestartCore()
        {
            try
            {
                if (!DrainBackgroundTasksAsync(2000).GetAwaiter().GetResult())
                {
                    var pending = string.Join(",", _taskSupervisor.Snapshot()
                        .Select(x => $"{x.Operation}(EPB{x.Channel},RunId={x.RunId:N})"));
                    throw new InvalidOperationException($"HardwareReleaseBlocked: 后台任务尚未移交或退出：{pending}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"释放硬件前等待后台任务失败：{ex.Message}", "EPB");
                throw;
            }
            try
            {
                if (_hydCoordinator != null &&
                    !_hydCoordinator.DrainBackgroundTasksAsync(2000).GetAwaiter().GetResult())
                    throw new InvalidOperationException("HardwareReleaseBlocked: 液压后台任务尚未退出。");
            }
            catch (Exception ex)
            {
                _log.Warn($"释放硬件前等待液压后台任务失败：{ex.Message}", "液压协调");
                throw;
            }
            try { _timerRuntimeWatchdog?.Dispose(); } catch { }
            try { _daqLivenessWatchdog?.Dispose(); } catch { }
            try { _dataHousekeeping?.Dispose(); } catch { }
            try { _incidentHousekeeping?.Dispose(); } catch { }
            try { _powerSupply?.Dispose(); } catch { }
            try { _acq?.Dispose(); } catch { }
            try { _ao?.ResetAll(); } catch { }
            try { _ao?.Dispose(); } catch { }
            try { _do?.AllOff(); } catch { }
            try { _do?.Dispose(); } catch { }
        }

        /// <summary>
        /// 固化 Stop 在等待任何恢复所有权之前必须完成的安全动作顺序。
        /// 返回值用于把已经启动、但尚未等待的电源 Disable 任务传给后续确认阶段；
        /// 拒绝项兜底只允许在该任务已经启动后调度。
        /// </summary>
        internal static T ExecuteStopSafetyBeforeRecoveryWait<T>(
            Action revokeBatch,
            Action pauseAllAndFreezeCycles,
            Action cancelAllAndSubmitOff,
            Func<T> startPowerDisable,
            Action startRejectedOffFallbacks = null,
            Action postSafetyEvidence = null)
        {
            if (revokeBatch == null) throw new ArgumentNullException(nameof(revokeBatch));
            if (pauseAllAndFreezeCycles == null)
                throw new ArgumentNullException(nameof(pauseAllAndFreezeCycles));
            if (cancelAllAndSubmitOff == null)
                throw new ArgumentNullException(nameof(cancelAllAndSubmitOff));
            if (startPowerDisable == null)
                throw new ArgumentNullException(nameof(startPowerDisable));

            revokeBatch();
            pauseAllAndFreezeCycles();
            cancelAllAndSubmitOff();
            var powerDisable = startPowerDisable();
            startRejectedOffFallbacks?.Invoke();
            postSafetyEvidence?.Invoke();
            return powerDisable;
        }

        // Retained only for the diagnostic legacy fallback.  Production StopAll
        // is owned by the stage-specific execution port in
        // EpbManager.StopSafetyOperations.cs.
        private async Task<StopSafetyResult> RunStopSafetyLegacyCoreAsync(
            StopContext context,
            CancellationToken token,
            long stopGeneration)
        {
            var startedUtc = DateTime.UtcNow;
            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var stopCorrelation = Guid.TryParse(context.CorrelationId, out var parsedStopCorrelation)
                ? parsedStopCorrelation
                : Guid.NewGuid();
            // 必须在撤销批次 CTS 前先冻结首份活动圈身份；Runner 收到取消后可能立即
            // 清除内存登记，若随后才首次 Capture，会留下 SQLite running 圈。
            var stopCycles = CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12));
            // 这里只冻结内存身份。SealCycleWindow 可能等待正持有 recorder Gate 的同步
            // MMF/SQLite 写；必须等整组 OFF 和电源 Disable 已启动后再执行。
            var stopCycleWindowsSealed = true;
            var stopSafetyDiagnostics = new List<string>();
            var stopSafetyErrors = new List<string>();
            var processingDataGaps = new List<string>();
            var stopPersistenceBoundaries = new Dictionary<string, long>();
            var channels = _timers.Keys
                .Concat(_runners.Keys)
                .Concat(_hydraulicParticipants.Keys)
                .Concat(_hydraulicLeaseByChannel.Keys)
                .Concat(_stopCtsByChannel.Keys)
                .Concat(_cyclePauseCtsByChannel.Keys)
                .Concat(_currentCycleNumberByChannel.Keys)
                .Concat(Enumerable.Range(1, 12).Where(IsChannelEnergized))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var motorOk = true;
            var motorErrors = new List<string>();
            var stopOffCompletions =
                new Dictionary<int, TaskCompletionSource<HighPriorityDoTelemetry>>();
            var stopOffFallbackStages = new Dictionary<int, string>();
            var stopOffFallbackTasks = new Dictionary<int, Task<bool>>();
            if (context.Source == StopSource.ManualUi ||
                context.Source == StopSource.ApplicationClosing ||
                context.Source == StopSource.ProgramExit ||
                context.Source == StopSource.UnknownLegacy)
            {
                foreach (var channel in Enumerable.Range(1, 12))
                    _manualStopRequestedChannels[channel] = 0;
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
            // 1. 在等待任何恢复所有权前先撤销批次、暂停/取消全部活动通道、并行提交
            // 最高优先级 OFF，随后立即启动全电源 Disable。这样即使恢复所有者或同步
            // 驱动永久阻塞，物理安全动作也已经独立发出。
            var powerTask = ExecuteStopSafetyBeforeRecoveryWait(
                () =>
                {
                    // 撤权本身只做内存/令牌变更；BatchPauseState 日志和观察者延后到
                    // 整组 OFF 与电源 Disable 已启动之后。
                    var terminalStatus = context.Source == StopSource.TargetCompleted
                        ? "Successful"
                        : context.Source == StopSource.AlarmInterlock
                            ? "Failed"
                            : "Cancelled";
                    EndBatchSession(
                        cancel: true,
                        publishIdleState: false,
                        terminalStatus: terminalStatus,
                        terminalReason: context.Reason);
                    // EndBatchSession 取消 Runner，但既有 DAQ 恢复上下文只认 run epoch。
                    // 先使其提交资格失效；CancelAll 随后负责终态清理和完成通知。
                    Interlocked.Increment(ref _runEpoch);
                },
                () =>
                {
                    foreach (var channel in channels)
                    {
                        try
                        {
                            if (_timers.TryGetValue(channel, out var timer))
                                timer.Pause($"StopAll:{context.Source}");
                        }
                        catch (Exception ex)
                        {
                            stopSafetyDiagnostics.Add(
                                $"StopAll暂停Timer失败 EPB={channel}: {ex.Message}");
                        }
                    }

                    // Pause 后、Cancel 前补抓撤权竞态中已经存在但首份快照未看到的通道；
                    // 已冻结通道绝不覆盖成新圈号，窗口只允许按同一 startedUtc 收紧。
                    foreach (var pair in CaptureSoftwareRecoveryCycles(Enumerable.Range(1, 12)))
                    {
                        if (!stopCycles.TryGetValue(pair.Key, out var frozenCycle))
                            stopCycles[pair.Key] = pair.Value;
                        else if (frozenCycle != pair.Value)
                        {
                            stopCycleWindowsSealed = false;
                            stopSafetyErrors.Add(
                                $"StopAll暂停前后圈身份变化 EPB={pair.Key} " +
                                $"Frozen={frozenCycle} Observed={pair.Value}；禁止同进程重启。");
                        }
                    }
                },
                () =>
                {
                    // 先让所有取消都生效，再提交任何通道的 OFF，避免逐通道处理时尚未
                    // 轮到的兄弟通道继续推进到下一动作相位。
                    foreach (var channel in channels)
                    {
                        try { UnmarkHydraulicParticipant(channel); } catch { }
                        try { CancelCyclePauseCts(channel); } catch { }
                        try { CancelStopCts(channel); } catch { }
                        try { RemoveTimerRuntime(channel, nameof(RunStopSafetyLegacyCoreAsync)); } catch { }
                        try { RemoveRunnerRuntime(channel, nameof(RunStopSafetyLegacyCoreAsync)); } catch { }
                    }

                    foreach (var channel in channels)
                    {
                        var completion = new TaskCompletionSource<HighPriorityDoTelemetry>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        try
                        {
                            if (TrySubmitEpbOffHighPriority(
                                    channel,
                                    telemetry => completion.TrySetResult(telemetry),
                                    out _))
                            {
                                stopOffCompletions[channel] = completion;
                            }
                            else
                            {
                                stopOffFallbackStages[channel] =
                                    "StopAllAdmissionRejected";
                            }
                        }
                        catch (Exception ex)
                        {
                            stopSafetyErrors.Add(
                                $"StopAll最高优先级OFF提交异常 EPB={channel}: {ex.Message}");
                            stopOffFallbackStages[channel] =
                                "StopAllSubmissionException";
                        }
                    }
                },
                () => ConfirmPowerOffForStopAsync(context, token, stopGeneration),
                () =>
                {
                    // 全部通道的异步 OFF 已经尝试，且总电源 Disable 已经启动。现在才为
                    // 每个拒绝项启动独立同步兜底；一个卡死调用不能阻塞兄弟通道或总电源。
                    foreach (var fallback in stopOffFallbackStages)
                    {
                        var fallbackChannel = fallback.Key;
                        var fallbackStage = fallback.Value;
                        try
                        {
                            var fallbackTask = Task.Run(() => TryExecuteImmediateOffFallback(
                                fallbackChannel,
                                fallbackStage,
                                out _));
                            stopOffFallbackTasks[fallbackChannel] = fallbackTask;
                            ObserveBackgroundTask(
                                fallbackTask,
                                "StopAllImmediateOffFallback",
                                fallbackChannel);
                        }
                        catch (Exception ex)
                        {
                            motorOk = false;
                            motorErrors.Add(
                                $"EPB{fallbackChannel:D2}:OFF安全兜底调度失败:{ex.Message}");
                            _log.Error(
                                $"StopAll无法调度独立OFF安全兜底 EPB={fallbackChannel}: {ex.Message}",
                                "EPB",
                                ex);
                        }
                    }
                },
                () =>
                {
                    // 电源 Disable 与所有拒绝项兜底均已启动后再发布 UI/状态诊断；
                    // SealCycleWindow 也只能从这里开始，避免持久化锁长尾阻止断能。
                    _log.Info(
                        $"收到停止全部 EPB 请求：{context.ToLogText()}; SafetyActionStartedUtc={startedUtc:O}",
                        "EPB");
                    foreach (var recovery in _recoverableChannelRestartJobs.Values)
                    {
                        try { recovery.Cancel(); } catch { }
                    }
                    stopCycleWindowsSealed = TrySealSoftwareRecoveryCycleWindows(
                                                 stopCycles,
                                                 startedUtc,
                                                 $"StopAll:{context.Source}:AfterSafetyActionsStarted") &&
                                             stopCycleWindowsSealed;
                    foreach (var diagnostic in stopSafetyDiagnostics)
                        _log.Warn(diagnostic, "EPB");
                    foreach (var error in stopSafetyErrors)
                        _log.Error(error, "EPB");
                    MarkBatchIdle("批次已取消");

                    // 任何观察者延迟都不能占用物理安全动作的时间预算。
                    foreach (var channel in channels)
                    {
                        try
                        {
                            var stoppedState = context.Source == StopSource.ManualUi ||
                                               context.Source == StopSource.ApplicationClosing ||
                                               context.Source == StopSource.ProgramExit ||
                                               context.Source == StopSource.UnknownLegacy
                                ? ChannelRuntimeState.ManualStopped
                                : context.Source == StopSource.AlarmInterlock
                                    ? ChannelRuntimeState.InterlockStopped
                                    : ChannelRuntimeState.SystemFault;
                            PublishChannelRuntimeState(
                                channel,
                                stoppedState,
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
                        catch (Exception ex)
                        {
                            _log.Warn($"StopAll发布终态失败 EPB={channel}: {ex.Message}", "EPB");
                        }
                    }
                    try { ClearChannelRuntimes(nameof(RunStopSafetyLegacyCoreAsync)); }
                    catch (Exception ex) { _log.Warn($"StopAll清场运行对象失败：{ex.Message}", "EPB"); }
                });

            // CancelAll 的同步前段现在才取得恢复提交锁；上面的安全动作已全部发出。
            // 这里只等待恢复终态和所有权退出，不得再重复 EndBatch 或重复提交 OFF。
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
            var pendingRecoveryTasks = await _recoveryTaskRegistry.DrainThroughEpochAsync(
                    runEpoch,
                    RecoveryOwnershipTakeoverTimeoutMs)
                .ConfigureAwait(false);
            if (pendingRecoveryTasks.Length > 0)
            {
                stopSafetyErrors.Add(
                    "RecoveryTasksPending=" + string.Join(",", pendingRecoveryTasks));
                _log.Warn(
                    "StopAll 等待统一恢复任务表超时；旧 RunEpoch 已撤权，禁止其写入新 Run。" +
                    string.Join(",", pendingRecoveryTasks),
                    "EPB");
            }
            AdvanceStopSafetyProgress(
                stopGeneration,
                StopSafetyStage.ClearRecoveryOwners,
                pendingRecoveryTasks.Length == 0
                    ? "DAQ与软件恢复 owner 已退出"
                    : "恢复 owner 超时，旧 RunEpoch 已隔离");

            // OFF 提交和电源 Disable 已与恢复所有权等待并行。这里只对账实际物理完成，
            // 不再补发第二个 OFF；未在期限内完成由电源关闭证据兜底并保留 Unconfirmed。
            if (stopOffCompletions.Count > 0)
            {
                var allOff = Task.WhenAll(stopOffCompletions.Values.Select(item => item.Task));
                var completed = await Task.WhenAny(
                        allOff,
                        Task.Delay(1000, CancellationToken.None))
                    .ConfigureAwait(false);
                if (completed == allOff)
                {
                    foreach (var telemetry in await allOff.ConfigureAwait(false))
                    {
                        if (telemetry?.Result == true) continue;
                        motorOk = false;
                        motorErrors.Add(
                            $"EPB{telemetry?.Channel ?? 0:D2}:DO物理写失败");
                    }
                }
                else
                {
                    foreach (var pair in stopOffCompletions)
                    {
                        if (pair.Value.Task.Status == TaskStatus.RanToCompletion &&
                            pair.Value.Task.Result?.Result == true)
                            continue;
                        motorOk = false;
                        motorErrors.Add(
                            pair.Value.Task.Status == TaskStatus.RanToCompletion
                                ? $"EPB{pair.Key:D2}:DO物理写失败"
                                : $"EPB{pair.Key:D2}:DO物理完成超时");
                    }
                }
            }
            if (stopOffFallbackTasks.Count > 0)
            {
                var fallbackPairs = stopOffFallbackTasks.ToArray();
                var allFallbacks = Task.WhenAll(fallbackPairs.Select(pair => pair.Value));
                var completed = await Task.WhenAny(
                        allFallbacks,
                        Task.Delay(1000, CancellationToken.None))
                    .ConfigureAwait(false);
                if (completed == allFallbacks)
                {
                    var results = await allFallbacks.ConfigureAwait(false);
                    for (var index = 0; index < results.Length; index++)
                    {
                        if (results[index]) continue;
                        motorOk = false;
                        motorErrors.Add(
                            $"EPB{fallbackPairs[index].Key:D2}:OFF同步安全兜底失败");
                    }
                }
                else
                {
                    foreach (var pair in fallbackPairs)
                    {
                        if (pair.Value.Status == TaskStatus.RanToCompletion && pair.Value.Result)
                            continue;
                        motorOk = false;
                        motorErrors.Add(
                            pair.Value.Status == TaskStatus.RanToCompletion
                                ? $"EPB{pair.Key:D2}:OFF同步安全兜底失败"
                                : $"EPB{pair.Key:D2}:OFF同步安全兜底超时");
                    }
                }
            }

            // 2. 电源 Disable 已经启动；恢复所有者退出后再接管液压释放和压力确认，
            // 避免两个所有者并发操作同一液压组。
            AdvanceStopSafetyProgress(
                stopGeneration,
                StopSafetyStage.ReleaseHydraulics,
                "正在释放液压并确认压力安全");
            var pressureTask = ConfirmPressureSafeForStopAsync(
                context,
                powerTask,
                stopGeneration);
            var pressure = await pressureTask.ConfigureAwait(false);
            var power = await powerTask.ConfigureAwait(false);
            // StopAll 的耐久证明必须建立在一个不再增长的接纳边界上。即使是
            // 人工停止/同进程重新开始，也先停止DAQ；后续预检会建立新generation。
            // 否则只能用旧A验证Raw，却用更新B验证SQLite，造成虚假FullyConfirmed。
            if (ShouldStopAcquisitionBeforeFinalPersistence(context.Source))
            {
                try { _acq.Stop(); }
                catch (Exception ex)
                {
                    _log.Warn($"退出前停止DAQ失败，将继续按已接收边界排空：{ex.Message}", "AI");
                }
            }
            AdvanceStopSafetyProgress(
                stopGeneration,
                StopSafetyStage.StopAcquisition,
                "DAQ 已请求停止，正在冻结最终接纳边界");
            // StopDevice 已等待在途回调退出；此后一次性冻结同一组最终边界，
            // Raw、工程处理和SQLite全部只允许针对这一组值给出闭合证明。
            foreach (var device in new[] { "Dev1", "Dev2" })
            {
                var hasContinuityGap = _acq.TryGetDataContinuityGap(
                        device,
                        out var firstGapSequence,
                        out var lastObservedSequence);
                if (hasContinuityGap)
                    processingDataGaps.Add(
                        $"{device}:FirstGap={firstGapSequence},LastObserved={lastObservedSequence}," +
                        $"AbandonedTail={Math.Max(0, lastObservedSequence - firstGapSequence + 1)}");
                var finalContinuousBoundary = _acq.GetLastProcessRecycleBoundary(device);
                var persistenceSnapshot = _persistence.GetSnapshot(device);
                var existingSuppressionBoundary = persistenceSnapshot.SuppressAfterSequence;
                // 非零 SuppressThrough 才表示仍有活动排除窗口；历史累计 SuppressedCount
                // 在窗口自动关闭后仍保留，不能据此把正常 Stop 的边界错误压回 0。
                var hasExistingSuppressedTail =
                    persistenceSnapshot.SuppressThroughSequence != 0;
                // Stop 可能接管一个尚未完成的 DAQ 恢复。该恢复已经把正式前缀冻结在
                // 更早边界，截止后的批次可能已被明确作为非正式尾段处理；Stop 不能
                // 再以更大的 LastAccepted 要求这些批次“重新出现”。
                stopPersistenceBoundaries[device] = hasExistingSuppressedTail
                    ? Math.Min(finalContinuousBoundary, existingSuppressionBoundary)
                    : finalContinuousBoundary;
                if (hasExistingSuppressedTail &&
                    existingSuppressionBoundary < finalContinuousBoundary)
                    _log.Warn(
                        $"StopAll继承DAQ恢复冻结前缀 Device={device} " +
                        $"RecoveryBoundary={existingSuppressionBoundary} " +
                        $"FinalAccepted={finalContinuousBoundary}",
                        "落盘");
                if (hasContinuityGap)
                {
                    // 仅永久空洞路径需要排除 gap-1 之后不可证明的尾段。正常 Stop 的
                    // 最终边界就是完整 LastAccepted，DAQ 已停止，无需留下额外抑制状态。
                    _persistence.SuppressAfter(
                        device,
                        startedUtc,
                        stopPersistenceBoundaries[device],
                        stopCorrelation,
                        runId,
                        runEpoch);
                }
            }
            if (processingDataGaps.Count > 0)
                _log.Error(
                    "检测到不可重放的DAQ工程/Raw处理空洞；已显式作废故障批次及其后尾段，" +
                    "当前圈不计数，仅允许完成安全断能和进程回收，禁止同进程重新开始。" +
                    string.Join("; ", processingDataGaps),
                    "落盘");
            AdvanceStopSafetyProgress(
                stopGeneration,
                StopSafetyStage.ClosePersistenceBoundary,
                "正在闭合 Raw、SQLite 与最近圈证据边界");
            var rawStorageFlushed = true;
            var rawStorageFlushError = string.Empty;
            var recentCycleExportError = string.Empty;
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
                        await flushRaw(stopPersistenceBoundaries, CancellationToken.None)
                            .ConfigureAwait(false);
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
                    RequiresRecoveredPersistenceStateForStop(context.Source))
                .ConfigureAwait(false);
            foreach (var boundary in persistenceBoundaries.Where(item => item.Closed))
            {
                var evidenceVersion = Math.Max(boundary.Boundary, boundary.Persisted);
                if (evidenceVersion > 0)
                    RecordStopSafetyMaterialProgress(
                        stopGeneration,
                        $"PersistenceBoundary:{boundary.Device}",
                        evidenceVersion,
                        $"持久化边界已推进 Device={boundary.Device};" +
                        $"Boundary={boundary.Boundary};Persisted={boundary.Persisted}");
            }
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
                else
                {
                    var exportChannels = channels
                        .Concat(stopCycles.Keys)
                        .Distinct()
                        .OrderBy(channel => channel)
                        .ToArray();
                    if (exportChannels.Length > 0)
                    {
                        try
                        {
                            var recorder = Recorder ?? throw new InvalidOperationException("Recorder不可用");
                            await Task.Run(() =>
                            {
                                foreach (var channel in exportChannels)
                                {
                                    stopCycles.TryGetValue(channel, out var interruptedCycle);
                                    if (recorder is IStopRecentCycleEvidenceExporter stopExporter)
                                        stopExporter.FlushRecentForStop(channel, 10, interruptedCycle);
                                    else
                                        recorder.FlushRecent(channel, 10);
                                }
                            }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            recentCycleExportError = ex.GetBaseException().Message;
                            persistenceBoundaryConfirmed = false;
                            _log.Error(
                                $"停止时最近10圈证据导出失败，禁止把停止视为完整收尾：{recentCycleExportError}",
                                "落盘",
                                ex);
                        }
                    }
                }
            }
            if (!persistenceBoundaryConfirmed)
                _log.Error(
                    "停止流程未能同时越过Raw发布边界和持久化边界；" +
                    "禁止把本次停止视为可直接重启的完整收尾。",
                    "落盘");
            // Never cancel/dispose the writer while the accepted production prefix is still
            // unresolved. The closing UI remains open and may retry StopAll after the disk
            // recovers; shutting down here would make that retry impossible and could discard
            // the final Raw batch/alarm evidence.
            if (ShouldShutdownPersistenceForStop(context.Source, persistenceBoundaryConfirmed))
                await _persistence.ShutdownAsync(int.MaxValue)
                    .ConfigureAwait(false);

            AdvanceStopSafetyProgress(
                stopGeneration,
                StopSafetyStage.VerifyLogicalQuiescence,
                "正在验证 Timer、Runner、CTS、恢复 owner 与液压代次清场");
            var logicalState = CaptureLogicalQuiescenceSnapshotForStop(context);
            var powerDisposition = GetStopPowerDisposition(
                stopGeneration,
                power.ok);
            var result = new StopSafetyResult
            {
                Source = context.Source,
                CorrelationId = context.CorrelationId ?? string.Empty,
                SafetyTransactionId = stopCorrelation,
                RunId = runId,
                RunEpoch = runEpoch,
                SafetyBoundaryGeneration = stopGeneration,
                MotorOffCommandSucceeded = motorOk,
                PowerOffConfirmed = powerDisposition == PowerShutdownDisposition.ConfirmedOff,
                PowerDisposition = powerDisposition,
                PressureSafeConfirmed = pressure.ok,
                PersistenceBoundaryConfirmed = persistenceBoundaryConfirmed,
                RawStorageFlushed = rawStorageFlushed,
                DataContinuityCompromised = processingDataGaps.Count > 0,
                DataContinuityError = string.Join("; ", processingDataGaps),
                StartedUtc = startedUtc,
                CompletedUtc = DateTime.UtcNow,
                MotorError = string.Join("; ", motorErrors),
                PowerError = power.error,
                PressureError = pressure.error,
                PersistenceError = persistenceBoundaryConfirmed
                    ? string.Empty
                    : string.Join("; ",
                        new[]
                            {
                                rawStorageFlushed ? null : $"RawStorage:{rawStorageFlushError}",
                                string.IsNullOrWhiteSpace(recentCycleExportError)
                                    ? null
                                    : $"RecentCycles:{recentCycleExportError}"
                            }
                            .Concat(persistenceBoundaries
                                .Where(item => !item.Closed)
                                .Select(item =>
                                    $"{item.Device}:RawDrained={item.RawPipelineDrained}," +
                                    $"PersistenceDrained={item.PersistenceQueueDrained}," +
                                    $"Boundary={item.Boundary},Published={item.Published}," +
                                    $"Persisted={item.Persisted},Depth={item.QueueDepth}," +
                                    $"State={item.PersistenceState}"))
                            .Where(item => !string.IsNullOrWhiteSpace(item))),
                LogicalQuiescenceConfirmed = logicalState.IsQuiescent,
                LogicalError = logicalState.IsQuiescent ? string.Empty : logicalState.ToString(),
                LogicalState = logicalState
            };

            lock (_stopSafetyGate)
            {
                if (stopGeneration == Interlocked.Read(ref _stopSafetyGeneration))
                    _lastStopSafetyResult = result.Clone();
            }
            var logText =
                $"StopAll分项结果：CorrelationId={result.CorrelationId}; " +
                $"MotorDO={(result.MotorOffCommandSucceeded ? "Confirmed" : "Unconfirmed")}; " +
                $"Power={(result.PowerOffConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"Pressure={(result.PressureSafeConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"RawStorage={(result.RawStorageFlushed ? "Confirmed" : "Unconfirmed")}; " +
                $"Persistence={(result.PersistenceBoundaryConfirmed ? "Confirmed" : "Unconfirmed")}; " +
                $"DataContinuity={(result.DataContinuityCompromised ? "CompromisedProcessRecycleRequired" : "Confirmed")}; " +
                $"Logical={(result.LogicalQuiescenceConfirmed ? "Confirmed" : "Pending")}; " +
                $"MotorError={result.MotorError}; PowerError={result.PowerError}; " +
                $"PressureError={result.PressureError}; PersistenceError={result.PersistenceError}; " +
                $"DataContinuityError={result.DataContinuityError}; " +
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
            FlushPersistentLog(true);
            return result;
        }

        internal static bool ShouldShutdownPersistenceForStop(
            StopSource source,
            bool persistenceBoundaryConfirmed)
        {
            return persistenceBoundaryConfirmed &&
                   IsFinalExitStopSource(source);
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
            // 授权撤销的内存屏障必须先于后台观察者执行。该专用事件只允许做无I/O、
            // 无等待的RunId标记，确保人工停止不会被并发恢复确认反向重新授权。
            try { RunAuthorizationRevocationBarrier?.Invoke(context); }
            catch (Exception ex)
            {
                _log.Error("同步无人值守授权撤销屏障执行失败。", "EPB", ex);
            }
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

        private async Task<(bool ok, string error)> ConfirmPressureSafeForStopCoreAsync(
            StopContext context,
            Task<(bool ok, string error)> powerOffTask,
            long stopGeneration)
        {
            var hydraulicIds = _cfg.Test.Hydraulics.Select(x => x.Id).Distinct().ToArray();
            Exception lastFailure = null;
            // 最多为每个液压组恢复一次只读采样；仍拿不到新鲜证据就保持禁止启动，
            // 绝不以旧压力或固定延时替代真实安全确认。
            for (var rearmAttempt = 0; rearmAttempt <= hydraulicIds.Length; rearmAttempt++)
            {
                try
                {
                    await Task.WhenAll(hydraulicIds.Select(id =>
                            _hydCoordinator.ForceReleaseAsync(id, $"StopAll:{context.Source}")))
                        .ConfigureAwait(false);
                    return (true, string.Empty);
                }
                catch (HydraulicReleaseTimeoutException ex)
                    when (ex.IsPressureEvidenceUnavailable && rearmAttempt < hydraulicIds.Length)
                {
                    lastFailure = ex;
                    // 复用 StopAll 已启动的唯一电源关闭任务。不得并发第二次 DisableAll，
                    // 否则串口/网络电源驱动的内部锁可能放大退出卡顿。
                    var powerOff = powerOffTask == null
                        ? (ok: false, error: "PowerOffTaskMissing")
                        : await powerOffTask.ConfigureAwait(false);
                    if (!powerOff.ok)
                        return (false,
                            ex.Message + "; PressureSensingRearmBlockedByPower=" + powerOff.error);
                    var channels = SelectPressureEvidenceRearmChannels(
                        ex.HydraulicId,
                        Enumerable.Range(1, 12),
                        channel => _acq.GetDeviceForEpbChannel(channel));
                    if (channels.Length == 0)
                        return (false,
                            ex.Message + "; PressureSensingRearmFailed=NoMappedDaqChannel");

                    try
                    {
                        IO.NI.DaqRecoveryResult[] ready = null;
                        await RecoveryStageDeadline.RunAsync(
                                "StopPressureSensingRearm",
                                StopReleaseHydraulicsStageDeadlineMs,
                                async ct =>
                                {
                                    ready = await _acq.EnsureChannelsReadyAsync(
                                            channels,
                                            RecoveryStageTimeoutMs,
                                            _daqPersistenceRequiredFreshBatches,
                                            (int)_daqPersistenceResumeAgeMs,
                                            ct)
                                        .ConfigureAwait(false);
                                },
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (ready == null || ready.Any(result => !result.Recovered))
                            return (false,
                                ex.Message + "; PressureSensingRearmFailed=" +
                                string.Join("|", ready?.Select(result =>
                                    $"{result.Device}:{result.FailureKind}:{result.FailureReason}") ??
                                    Array.Empty<string>()));

                        var rearmEvidenceVersion = ready
                            .Where(result => result != null)
                            .Select(result => result.LastVerifiedSequence)
                            .Where(sequence => sequence > 0)
                            .DefaultIfEmpty(0)
                            .Max();
                        if (rearmEvidenceVersion > 0)
                            RecordStopSafetyMaterialProgress(
                                stopGeneration,
                                $"DaqPressureRearm:Hydraulic={ex.HydraulicId}",
                                rearmEvidenceVersion,
                                $"DAQ压力取证重建已取得新鲜批次 Hydraulic={ex.HydraulicId};" +
                                $"Channels=[{string.Join(",", channels)}];" +
                                $"Sequence={rearmEvidenceVersion}");
                        _log.Warn(
                            $"StopAll检测到液压组{ex.HydraulicId}压力证据陈旧；所有输出保持断能，" +
                            $"已只恢复DAQ采样 Channels=[{string.Join(",", channels)}]，现在重新确认新鲜压力。",
                            "液压协调");
                    }
                    catch (Exception rearmException)
                    {
                        return (false,
                            ex.Message + "; PressureSensingRearmFailed=" + rearmException.Message);
                    }
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }
            return (false, lastFailure?.Message ?? "PressureSafetyConfirmationFailed");
        }

        internal static int[] SelectPressureEvidenceRearmChannels(
            int hydraulicId,
            IEnumerable<int> candidates,
            Func<int, string> resolveDaqDevice)
        {
            if (hydraulicId <= 0 || resolveDaqDevice == null) return Array.Empty<int>();
            return (candidates ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Where(channel => GetHydraulicGroupForChannel(channel) == hydraulicId)
                .Where(channel => !string.IsNullOrWhiteSpace(resolveDaqDevice(channel)))
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
        }

        private async Task<(bool ok, string error)> ConfirmPowerOffForStopAsync(
            StopContext context,
            CancellationToken callerWaitToken,
            long stopGeneration)
        {
            if (_powerSupply == null)
            {
                _stopPowerDispositionByGeneration[stopGeneration] =
                    PowerShutdownDisposition.CommunicationUnavailableSkipped;
                const string unavailable =
                    "程控电源协调器不可用，退出流程按策略跳过等待；断电未得到真实回读确认。";
                _log.Warn(unavailable, "程控电源");
                return (true, unavailable);
            }
            var results = await _powerSupply.DisableAllForSafetyAsync(
                    $"StopAll Source={context.Source} CorrelationId={context.CorrelationId}",
                    CancellationToken.None)
                .ConfigureAwait(false);
            var failed = results.Where(item => !item.ShutdownSatisfied).ToArray();
            var skipped = results.Where(item =>
                item.Outcome == PowerSafetyDisableOutcome.CommunicationUnavailableSkipped).ToArray();
            _stopPowerDispositionByGeneration[stopGeneration] = failed.Length > 0
                ? PowerShutdownDisposition.Failed
                : skipped.Length > 0
                    ? PowerShutdownDisposition.CommunicationUnavailableSkipped
                    : PowerShutdownDisposition.ConfirmedOff;
            var detail = string.Join("; ", results
                .Where(item => !item.ConfirmedOff)
                .Select(item =>
                    $"Group{item.ElectricalGroupId}:{item.Outcome}:{item.Error}"));
            if (skipped.Length > 0)
                _log.Warn(
                    "程控电源通讯不可用，退出流程按策略不再等待；断电未得到真实回读确认。" + detail,
                    "程控电源");
            return failed.Length == 0 ? (true, detail) : (false, detail);
        }

        private PowerShutdownDisposition GetStopPowerDisposition(
            long stopGeneration,
            bool satisfied)
        {
            return _stopPowerDispositionByGeneration.TryRemove(
                stopGeneration,
                out var disposition)
                ? disposition
                : satisfied
                    ? PowerShutdownDisposition.ConfirmedOff
                    : PowerShutdownDisposition.Failed;
        }

        /// <summary>
        /// Stop 请求的 10/20 秒调用方令牌只代表调用方还愿意等待多久，不能撤销已经
        /// 启动的物理断电。PowerSupplyCoordinator 自身负责串行化和设备级截止；这里
        /// 始终用不可取消令牌启动安全方向 owner 操作。
        /// </summary>
        internal static async Task<(bool ok, string error)> ExecuteStopPowerDisableSafetyAsync(
            Func<CancellationToken, Task> physicalDisable,
            CancellationToken callerWaitToken)
        {
            // 明确读取以说明该令牌属于观察者，不允许传播给物理 OFF。
            var callerAlreadyCancelled = callerWaitToken.IsCancellationRequested;
            _ = callerAlreadyCancelled;
            try
            {
                if (physicalDisable != null)
                    await physicalDisable(CancellationToken.None).ConfigureAwait(false);
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.GetBaseException().Message);
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
                if (_stopSafetyFinalExitTask?.IsCompleted == true)
                    _stopSafetyFinalExitTask = null;
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
                   ageMs <= _epbPowerSupplyOffProofMaxAgeMs;
        }

        internal bool TryConfirmPowerSupplyOffProof(
            int channel,
            out double measuredCurrentA,
            out double ageMs,
            out bool outputEnabled)
        {
            return TryGetFreshPowerSupplyCurrent(
                       channel,
                       out measuredCurrentA,
                       out ageMs,
                       out outputEnabled) &&
                   IsRedundantOffProofSatisfied(
                       doOffSucceeded: true,
                       powerTelemetryFresh: true,
                       measuredCurrentA,
                       _epbPowerSupplyOffProofThresholdA);
        }

        internal static bool IsRedundantOffProofSatisfied(
            bool doOffSucceeded,
            bool powerTelemetryFresh,
            double measuredCurrentA,
            double thresholdA)
        {
            return doOffSucceeded && powerTelemetryFresh && thresholdA > 0 &&
                   !double.IsNaN(measuredCurrentA) &&
                   !double.IsInfinity(measuredCurrentA) &&
                   Math.Abs(measuredCurrentA) <= thresholdA;
        }

        internal EmergencyPowerGroupRegistration RequestElectricalGroupEmergencyShutdown(
            int sourceChannel,
            string reason)
        {
            var groupId = GetElectricalGroupId(sourceChannel);
            if (groupId <= 0)
            {
                ObserveBackgroundTask(Task.Run(() => _log.Error(
                    $"EPB[{sourceChannel}] 请求电源组紧急关闭，但未找到电气组映射。Reason={reason}",
                    "程控电源")), "EmergencyShutdownMissingGroup", sourceChannel);
                return default;
            }
            var members = _cfg.Test.Groups
                .FirstOrDefault(x => x.Id == groupId)?
                .Members
                .Distinct()
                .OrderBy(x => x)
                .ToArray() ?? new[] { sourceChannel };

            var sourceDevice = _acq.GetDeviceForEpbChannel(sourceChannel);
            DaqIncidentContext daqIncident = null;
            var daqStaleReason = !string.IsNullOrWhiteSpace(reason) &&
                                 reason.IndexOf(
                                     "OffCurrentUnverifiableDaqStale",
                                     StringComparison.OrdinalIgnoreCase) >= 0;
            if (daqStaleReason && !string.IsNullOrWhiteSpace(sourceDevice) &&
                !_daqIncidentLatch.TryGet(_activeBatchId, sourceDevice, out daqIncident))
            {
                daqIncident = ObserveDaqIncident(
                    sourceDevice,
                    "OffCurrentUnverifiableDaqStale",
                    reason,
                    DateTime.UtcNow,
                    GetAllDaqDeviceChannels(sourceDevice)).Context;
            }
            var daqDerived = daqStaleReason && daqIncident != null;
            var registration = _emergencyPowerGroupLatch.Register(
                groupId,
                daqDerived ? daqIncident.CorrelationId : Guid.NewGuid(),
                DateTime.UtcNow,
                nonDaqFault: !daqDerived);
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(members);
            // 先只冻结内存身份；recorder 封圈可能等待同步存储锁，必须后移到
            // 整组 OFF 和共享电源 Disable 均已启动之后。
            var cutoffCyclesSealed = true;
            var correlationId = registration.CorrelationId;
            Dictionary<int, string> rejectedOff = null;
            Dictionary<int, Task<(bool ok, string error)>> powerDisableTasks = null;

            ExecuteNonBlockingSafetyIsolationOrder(
                () =>
                {
                    // 先暂停整组，再补抓 Pause 与 Cancel 之间的活动圈；首份身份只允许
                    // 单调合并，不能在 Runner 取消清理后丢失。
                    foreach (var member in members)
                    {
                        try
                        {
                            if (_timers.TryGetValue(member, out var timer))
                                timer.Pause($"ElectricalGroupEmergency:{reason}");
                        }
                        catch { }
                    }
                    foreach (var pair in CaptureSoftwareRecoveryCycles(members))
                    {
                        if (!cutoffCycles.ContainsKey(pair.Key))
                            cutoffCycles[pair.Key] = pair.Value;
                        else if (cutoffCycles[pair.Key] != pair.Value)
                            cutoffCyclesSealed = false;
                    }
                    foreach (var member in members)
                    {
                        try { CancelCyclePauseCts(member); } catch { }
                    }
                },
                () => rejectedOff = SubmitEpbOffHighPriorityBatch(
                    members,
                    "ElectricalGroupEmergencyOffAdmissionRejected",
                    "ElectricalGroupEmergencyOffSubmissionException"),
                () => powerDisableTasks = StartElectricalGroupSafetyDisables(
                    members,
                    $"EPB失效安全联锁 Source={sourceChannel} Reason={reason}",
                    "ElectricalGroupEmergencyPowerDisable"),
                () => ObserveBackgroundTask(Task.Run(async () =>
                {
                if (daqDerived && registration.IsFirst)
                    ObserveDaqIncident(
                        sourceDevice,
                        "OffCurrentUnverifiableDaqStale",
                        reason,
                        DateTime.UtcNow,
                        GetAllDaqDeviceChannels(sourceDevice));
                cutoffCyclesSealed = TrySealSoftwareRecoveryCycleWindows(
                                         cutoffCycles,
                                         cutoffUtc,
                                         $"ElectricalGroupEmergency:{reason}:AfterSafetyActionsStarted") &&
                                     cutoffCyclesSealed;
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
                    // BeginPowerSupplySoftwareRecovery is the sole lifecycle
                    // owner for this path.  It reserves and binds the real
                    // recovery body before publishing Recovering; the
                    // emergency safety worker must not publish a second owner.
                }

                if (powerDisableTasks != null &&
                    powerDisableTasks.TryGetValue(groupId, out var powerDisableTask))
                    await powerDisableTask.ConfigureAwait(false);

                if (!cutoffCyclesSealed)
                    _log.Warn(
                        $"电源组{groupId}紧急联锁圈身份在暂停边界发生变化；" +
                        "保持Recovering并禁止把不确定圈恢复为正式完成。",
                        "落盘");

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
                }), "ElectricalGroupEmergencyShutdown", sourceChannel),
                () => ScheduleRejectedOffFallbacks(
                    rejectedOff,
                    "ElectricalGroupEmergencyImmediateOffFallback"));
            return registration;
        }

        internal static LogicalRecoveryCounts BuildLogicalRecoveryCounts(
            int activeCycleCount,
            params int[] softwareRecoveryCounts)
        {
            return new LogicalRecoveryCounts
            {
                ActiveCycleCount = Math.Max(0, activeCycleCount),
                SoftwareRecoveryCount = (softwareRecoveryCounts ?? Array.Empty<int>())
                    .Sum(count => Math.Max(0, count))
            };
        }

        internal static WatchdogChannelProgressSnapshot ApplyWarningOverlayToWatchdogProgress(
            WatchdogChannelProgressSnapshot progress,
            ChannelWarningOverlayChangedEvent warning)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            progress.WarningActive = warning?.Active == true;
            progress.WarningCode = warning?.WarningCode ?? string.Empty;
            progress.WarningRevision = warning?.Revision ?? 0;
            return progress;
        }

        private LogicalQuiescenceSnapshot CaptureLogicalQuiescenceSnapshot()
        {
            // Recovery contracts, registry leases and channel lifecycle must
            // be sampled with one owner gate.  Monitor is re-entrant for the
            // callers that already hold the incident transaction.
            lock (_recoveryContractGate)
                return CaptureLogicalQuiescenceSnapshotLocked();
        }

        private LogicalQuiescenceSnapshot CaptureLogicalQuiescenceSnapshotLocked()
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
            var recoveryCounts = BuildLogicalRecoveryCounts(
                _currentCycleNumberByChannel.Count,
                _hydraulicSoftwareRecoveryGroups.Count,
                _powerSoftwareRecoveryGroups.Count,
                _affectedGroupResetInProgress.Count,
                _isolatedInfrastructureRecoveryScheduled.Count,
                _timerRuntimeRecoveries.Count,
                _activeCycleLimitRecoveries.Count,
                _alarmCycleFinalizationRetries.Count,
                _formalPersistenceRecoveryPendingCycles.Count);
            var snapshot = new LogicalQuiescenceSnapshot
            {
                BatchLifecycleBusy = _batchLifecycleGate.IsBusy,
                BatchSessionActive = IsBatchSessionActive,
                ActiveBatchId = _activeBatchId,
                TimerActiveCount = _timers.Count,
                TimerCacheCount = _timerCache.Count,
                TimerCount = CountDistinctRuntimeChannels(_timers.Keys, _timerCache.Keys),
                RunnerActiveCount = _runners.Count,
                RunnerCacheCount = _runnerCache.Count,
                RunnerCount = CountDistinctRuntimeChannels(_runners.Keys, _runnerCache.Keys),
                EnergizedChannelCount = Enumerable.Range(1, 12).Count(IsChannelEnergized),
                StopCtsCount = _stopCtsByChannel.Count,
                CycleCtsCount = _cyclePauseCtsByChannel.Count,
                HydraulicParticipantCount = _hydraulicParticipants.Count,
                HydraulicLeaseCount = _hydraulicLeaseByChannel.Count,
                DaqRecoveryCount = _daqAutoRecovery.Count,
                ActiveCycleCount = recoveryCounts.ActiveCycleCount,
                SoftwareRecoveryCount = recoveryCounts.SoftwareRecoveryCount,
                RecoveryOwnerCount = _recoveryOwnership.ActiveCount,
                ActiveRecoveryContractCount = _activeRecoveryContracts.Values.Count(
                    incident => incident != null &&
                                incident.TerminalPublished == 0),
                ActiveRecoveryRegistryLeaseCount = _recoveryTaskRegistry.ActiveCount,
                HydraulicGroups = hydraulicGroups,
                ChannelProgress = Enumerable.Range(1, 12).Select(channel =>
                {
                    var state = _channelRuntimeStateStore.Get(channel);
                    var warning = _channelWarningOverlayStore.Get(channel);
                    _watchdogLastMechanicalCompletedUtcTicks.TryGetValue(channel, out var completedUtc);
                    _watchdogChannelProgressVersion.TryGetValue(channel, out var progressVersion);
                    _watchdogLastProgressUtcTicks.TryGetValue(channel, out var lastProgressUtc);
                    _watchdogProgressKind.TryGetValue(channel, out var progressKind);
                    var completedCount = GetObservedMechanicalCycleCount(channel);
                    _watchdogConsecutiveSoftwareAborts.TryGetValue(channel, out var aborts);
                    _watchdogDoCommandSequence.TryGetValue(channel, out var doSequence);
                    var cutoffGeneration = 0L;
                    var cutoffSequence = 0L;
                    if (_acq != null)
                        _acq.TryGetLastEpbPeakCutoffWatermark(
                            channel,
                            out cutoffGeneration,
                            out cutoffSequence);
                    var progress = new WatchdogChannelProgressSnapshot
                    {
                        Channel = channel,
                        State = state?.State.ToString() ?? ChannelRuntimeState.NotEnabled.ToString(),
                        StateRevision = state?.Revision ?? 0,
                        StateSinceUtcTicks = state?.TimestampUtc.Ticks ?? 0,
                        TimerActive = _timers.ContainsKey(channel) || _timerCache.ContainsKey(channel),
                        RunnerActive = _runners.ContainsKey(channel) || _runnerCache.ContainsKey(channel),
                        Energized = IsChannelEnergized(channel),
                        ProgressVersion = progressVersion,
                        LastProgressUtcTicks = lastProgressUtc,
                        ProgressKind = progressKind ?? string.Empty,
                        LastMechanicalCompletedUtcTicks = completedUtc,
                        MechanicalCompletedCount = completedCount,
                        ConsecutiveSoftwareAbortCount = aborts,
                        DoCommandSequence = doSequence,
                        PeakCutoffGeneration = cutoffGeneration,
                        PeakCutoffSequence = cutoffSequence,
                        RecoveryOwnerKind = state?.RecoveryOwnerKind.ToString() ?? string.Empty,
                        RecoveryOwnerId = state?.RecoveryOwnerId.ToString("N") ?? string.Empty,
                        RecoveryOwnerGeneration = state?.RecoveryOwnerGeneration ?? 0,
                        RecoveryTargetPhase = state?.RecoveryTargetPhase.ToString() ?? string.Empty,
                        SourceStateRevision = state?.SourceStateRevision ?? 0
                    };
                    return ApplyWarningOverlayToWatchdogProgress(progress, warning);
                }).ToArray()
            };
            _recoveryAggregateStore.PublishLogicalSource(snapshot);
            PublishRecoveryOperationalSourcesLocked();
            return snapshot;
        }

        internal static int CountDistinctRuntimeChannels(
            IEnumerable<int> activeChannels,
            IEnumerable<int> cachedChannels)
        {
            return (activeChannels ?? Array.Empty<int>())
                .Concat(cachedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .Count();
        }

        private LogicalQuiescenceSnapshot CaptureLogicalQuiescenceSnapshotForStop(StopContext context)
        {
            var snapshot = CaptureLogicalQuiescenceSnapshot();
            if (!CanIgnoreLifecycleBusyDuringFailingStartCleanup(
                    context,
                    _batchLifecycleGate.ActiveOperationCount,
                    snapshot.BatchSessionActive))
                return snapshot;

            // StartBatch 的 catch 正在持有唯一生命周期租约，并已先 EndBatchSession、
            // 清空执行资源。该租约会在 catch 返回后的 finally 立即释放，不能把它
            // 当成未知后台残留并无条件升级为进程重启。
            snapshot.BatchLifecycleBusy = false;
            _log?.Info(
                "批量启动失败收尾仅剩当前 StartBatch 生命周期栈；按当前拥有者清场证明忽略该瞬时 Busy。",
                "EPB");
            return snapshot;
        }

        internal static bool CanIgnoreLifecycleBusyDuringFailingStartCleanup(
            StopContext context,
            int activeLifecycleOperations,
            bool batchSessionActive)
        {
            if (context == null || batchSessionActive || activeLifecycleOperations != 1)
                return false;
            if (!string.Equals(
                    context.Initiator,
                    nameof(StartBatchSynchronizedWithResultAsync),
                    StringComparison.Ordinal))
                return false;
            return context.Source == StopSource.StartupRollback ||
                   context.Source == StopSource.SystemFault;
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
                   state == BatchPauseState.Stopping ||
                   state == BatchPauseState.PauseHolding;
        }

        internal static bool CanCommitDaqRecoveredHeldTerminal(
            bool holdForBatchPause,
            bool powerOffConfirmed)
        {
            return !holdForBatchPause || powerOffConfirmed;
        }

        private async Task EnsureDaqHeldPowerOffBeforeTerminalAsync(
            string device,
            IEnumerable<int> channels)
        {
            if (_powerSupply == null) return;
            foreach (var groupId in (channels ?? Array.Empty<int>())
                         .Select(GetElectricalGroupId)
                         .Where(id => id > 0)
                         .Distinct()
                         .OrderBy(id => id))
            {
                await RecoveryStageDeadline.RunAsync(
                        "DaqPauseGenerationPowerOff",
                        RecoveryStageTimeoutMs,
                        ct => _powerSupply.DisableGroupAsync(
                            groupId,
                            $"DaqRecoveredHeldForManualPause:{device}",
                            ct),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private static bool ShouldHoldDaqRecoveryForPause(
            DaqAutoRecoveryContext context,
            BatchPauseSnapshot current)
        {
            if (context == null || current == null) return true;
            // A pause that started at any point during this recovery is an
            // irreversible ownership boundary for the transaction.  Even if
            // the UI has already advanced back to Running, the old recovery
            // must stay held and let ResumeBatch perform the fresh DAQ gate.
            return ShouldHoldDaqRecoveredChannelsForBatchPause(
                       context.AdmissionBatchPauseState) ||
                   ShouldHoldDaqRecoveredChannelsForBatchPause(current.State) ||
                   current.Generation != context.AdmissionBatchPauseGeneration;
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
            var runnableChannels = enabled;
            var effectiveStaggerPlan = staggerPlan;

            try
            {
                // P0：任何预释放电机动作前，先按压力组完成实际压力资格。
                // 已确认的物理液压故障只隔离所属压力组；另一独立组继续定位。
                var qualified = new HashSet<int>(enabled);
                foreach (var group in enabled.GroupBy(ch => ch <= 6 ? 1 : 2))
                {
                    var members = group.ToArray();
                    try
                    {
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
                    catch (Exception ex) when (
                        HydraulicGroupCoordinator.ClassifyFault(ex) ==
                        FaultClassification.HardwareConfirmed)
                    {
                        foreach (var member in members)
                        {
                            qualified.Remove(member);
                            failedResults.Add(new StartupPositioningResult
                            {
                                Channel = member,
                                Succeeded = false,
                                Stage = StartupPositioningStage.None,
                                Code = ResolveInfrastructureHardwareStartFailureCode(
                                    member,
                                    "HydraulicHardwareConfirmed"),
                                Reason = AppendInfrastructureDisablePersistenceFailure(
                                    member,
                                    $"HydraulicGroup={group.Key}; {ex.Message}")
                            });
                        }
                        _log.Error(
                            $"液压组{group.Key}启动资格确认硬件故障，已隔离组内所选通道" +
                            $"[{string.Join(",", members)}]；健康独立液压组继续。{ex.Message}",
                            "液压协调",
                            ex);
                    }
                }
                runnableChannels = enabled.Where(qualified.Contains).ToArray();
                if (runnableChannels.Length > 0 && runnableChannels.Length != enabled.Length)
                {
                    effectiveStaggerPlan = ElectricalStaggerPlanner.Build(
                        runnableChannels,
                        _cfg.Test.Groups,
                        PeriodMs);
                    _log.Info(
                        $"启动液压硬件组隔离后已按健康集合重新生成错峰计划。" +
                        $"Channels=[{string.Join(",", runnableChannels)}]",
                        "EPB");
                }

                if (runnableChannels.Length > 0)
                {
                    // 液压资格确认可能耗时数秒。锚点必须在资格确认完成后创建，
                    // 否则 0/800ms 相位均会过期并被同刻放行。
                    var anchorUtc = ElectricalStaggerExecutor.EnsureAnchorInFuture(
                        DateTime.UtcNow.AddMilliseconds(500),
                        DateTime.UtcNow);

                    await ElectricalStaggerExecutor.RunAsync(
                    runnableChannels,
                    effectiveStaggerPlan,
                    anchorUtc,
                    async (ch, ct) =>
                    {
                        var assignment = effectiveStaggerPlan.Get(ch);
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
                        var executionRepairAttempt = 0;
                        var powerReadinessRepairAttempt = 0;
                        while (true)
                        {
                            if (!TryEnsureStartupPositioningExecutionResources(
                                    ch,
                                    runId,
                                    ref concreteRunner,
                                    out var executionRepairFailure))
                            {
                                executionRepairAttempt++;
                                if (ShouldEscalateSoftwareRecovery(executionRepairAttempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningExecutionFramework",
                                        executionRepairAttempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位执行框架连续{executionRepairAttempt}次未恢复。" +
                                            executionRepairFailure),
                                        ch);
                                var repairDelayMs = GetDaqSelfMaintenanceDelayMs(executionRepairAttempt);
                                await RunStartupPositioningRetryIncidentAsync(
                                        ch,
                                        runId,
                                        executionRepairAttempt,
                                        "StartupPositioningExecutionFrameworkRepair",
                                        $"Runner/执行许可校验未通过；{repairDelayMs}ms后修复重试，" +
                                        "不占用机械定位尝试次数。" + executionRepairFailure,
                                        repairDelayMs,
                                        ct,
                                        "StartupPositioningExecutionRepairOff")
                                    .ConfigureAwait(false);
                                continue;
                            }
                            executionRepairAttempt = 0;
                            try
                            {
                                await EnsurePowerSupplyReadyForChannelsAsync(
                                        new[] { ch },
                                        ct)
                                    .ConfigureAwait(false);
                                EnsurePowerSupplyEnergizationPermit(ch);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                powerReadinessRepairAttempt++;
                                if (ShouldEscalateSoftwareRecovery(powerReadinessRepairAttempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningPowerPermit",
                                        powerReadinessRepairAttempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位电源恢复期间执行许可被刷新。"),
                                        ch);
                                var powerPermitDelayMs = GetDaqSelfMaintenanceDelayMs(powerReadinessRepairAttempt);
                                await RunStartupPositioningRetryIncidentAsync(
                                        ch,
                                        runId,
                                        powerReadinessRepairAttempt,
                                        "StartupPositioningPowerPermitRefresh",
                                        $"电源恢复刷新了执行许可；{powerPermitDelayMs}ms后重新建立启动框架，不占用机械尝试次数。",
                                        powerPermitDelayMs,
                                        ct,
                                        "StartupPositioningPowerPermitRefreshOff")
                                    .ConfigureAwait(false);
                                continue;
                            }
                            catch (Exception ex)
                            {
                                powerReadinessRepairAttempt++;
                                if (ShouldEscalateSoftwareRecovery(powerReadinessRepairAttempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningPowerReadiness",
                                        powerReadinessRepairAttempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位电源资格连续未恢复。{ex.Message}",
                                            ex),
                                        ch);
                                var powerReadinessDelayMs = GetDaqSelfMaintenanceDelayMs(powerReadinessRepairAttempt);
                                await RunStartupPositioningRetryIncidentAsync(
                                        ch,
                                        runId,
                                        powerReadinessRepairAttempt,
                                        "StartupPositioningPowerReadinessRepair",
                                        $"电源资格未就绪；{powerReadinessDelayMs}ms后恢复重试，不占用机械尝试次数。{ex.Message}",
                                        powerReadinessDelayMs,
                                        ct,
                                        "StartupPositioningPowerReadinessOff")
                                    .ConfigureAwait(false);
                                continue;
                            }
                            powerReadinessRepairAttempt = 0;
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
                                var exceptionDelayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                                _log.Warn(
                                    $"EPB[{ch}] 启动定位抛出软件异常，自动重试且不标记启动受阻。" +
                                    $"Attempt={attempt} DelayMs={exceptionDelayMs} " +
                                    $"Exception={ex.GetType().Name} Message={ex.Message}",
                                    "EPB");
                                try
                                {
                                    await RunStartupPositioningRetryIncidentAsync(
                                            ch,
                                            runId,
                                            attempt,
                                            "StartupPositioningExceptionSelfHealing",
                                            $"启动定位软件异常，第{attempt}次有界自愈，{exceptionDelayMs}ms后重试。",
                                            exceptionDelayMs,
                                            ct,
                                        "StartupPositioningOutputOffCommandFailed")
                                        .ConfigureAwait(false);
                                }
                                catch (SoftwareSelfHealingRetryException retryEx)
                                {
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningOutputOff",
                                        attempt,
                                        retryEx,
                                        ch);
                                }
                                if (ShouldEscalateSoftwareRecovery(attempt))
                                    throw new SoftwareSelfHealingExhaustedException(
                                        "StartupPositioningException",
                                        attempt,
                                        new SoftwareSelfHealingRetryException(
                                            $"EPB[{ch}] 启动定位连续{attempt}次软件异常。",
                                            ex),
                                        ch);
                                continue;
                            }
                            if (result.Succeeded) break;
                            if (IsStartupPositioningHardwareConfirmed(result))
                            {
                                failedResults.Add(result);
                                await PublishStartupPositioningFailureAsync(result).ConfigureAwait(false);
                                break;
                            }

                            var delayMs = GetDaqSelfMaintenanceDelayMs(attempt);
                            _log.Warn(
                                $"EPB[{ch}] 启动定位软件瞬态自动重试，不标记启动受阻。" +
                                $"Attempt={attempt} DelayMs={delayMs} Code={result.Code} Reason={result.Reason}",
                                "EPB");
                            try
                            {
                                await RunStartupPositioningRetryIncidentAsync(
                                        ch,
                                        runId,
                                        attempt,
                                        "StartupPositioningSelfHealing",
                                        $"启动定位软件瞬态未通过，第{attempt}次有界自愈，{delayMs}ms后重试。",
                                        delayMs,
                                        ct,
                                        "StartupPositioningOutputOffCommandFailed")
                                    .ConfigureAwait(false);
                            }
                            catch (SoftwareSelfHealingRetryException retryEx)
                            {
                                throw new SoftwareSelfHealingExhaustedException(
                                    "StartupPositioningOutputOff",
                                    attempt,
                                    retryEx,
                                    ch);
                            }
                            if (ShouldEscalateSoftwareRecovery(attempt))
                                throw new SoftwareSelfHealingExhaustedException(
                                    "StartupPositioning",
                                    attempt,
                                    new SoftwareSelfHealingRetryException(
                                        $"EPB[{ch}] 启动定位连续{attempt}次软件瞬态未通过。" +
                                        $"Code={result.Code} Reason={result.Reason}"),
                                    ch);
                        }
                    },
                    token).ConfigureAwait(false);
                }
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
