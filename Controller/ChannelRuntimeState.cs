using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller
{
    public enum ChannelRuntimeState
    {
        NotEnabled = 0,
        Starting = 1,
        Learning = 2,
        Running = 3,
        WarningRunning = 4,
        Paused = 5,
        AlarmStopped = 6,
        InterlockStopped = 7,
        ManualStopped = 8,
        Completed = 9,
        StartBlocked = 10,
        Recovering = 11,
        SystemFault = 12,
        PausePending = 13,
        ResumeChecking = 14,
        Qualification = 15,
        /// <summary>自身输出已关闭，正在等待同一全局正式槽的其它参与者安全收口。</summary>
        WaitingForSlotBarrier = 16
    }

    /// <summary>
    ///     Recovering 不是一个可以仅靠 ReasonCode 推断的普通显示状态。每一次恢复都必须有
    ///     明确、可校验的所有者；Watchdog 与孤儿恢复审计只认这里的结构化所有权。
    /// </summary>
    public enum RecoveryOwnerKind
    {
        None = 0,
        BatchStartup = 1,
        BatchLearning = 2,
        BatchQualification = 3,
        FormalTimer = 4,
        DaqRecovery = 5,
        PowerRecovery = 6,
        HydraulicGroupRecovery = 7,
        AffectedGroupRecovery = 8,
        Watchdog = 9,
        Unknown = 10
    }

    public enum RecoveryTargetPhase
    {
        None = 0,
        Startup = 1,
        Learning = 2,
        Qualification = 3,
        Formal = 4,
        TerminalCleanup = 5
    }

    public enum RecoveryExitDisposition
    {
        RejoinedRunning = 0,
        HeldForManualPause = 1,
        SafeIdleFault = 2,
        Superseded = 3,
        CancelledByStop = 4
    }

    /// <summary>
    ///     One recovery incident has exactly one monotonic progress line.  Stage
    ///     budgets are diagnostic only; <see cref="RecoveryContractSnapshot.HardDeadlineUtc"/>
    ///     is the sole cancellation authority.
    /// </summary>
    public enum RecoveryIncidentStage
    {
        Detected = 0,
        SafetyOffSubmitting = 10,
        SafetyOffConfirmed = 20,
        DaqRebuilding = 30,
        FreshnessConfirmed = 40,
        PersistenceClosed = 50,
        Terminal = 60
    }

    public enum SafetyOffTargetKind
    {
        DigitalOutput = 0,
        PowerSupplyGroup = 1
    }

    public enum SafetyOffEvidenceStatus
    {
        Submitted = 0,
        ConfirmedOff = 1,
        Rejected = 2,
        Failed = 3,
        TimedOut = 4,
        Superseded = 5
    }

    /// <summary>
    ///     Immutable physical OFF evidence. Queue admission is represented by
    ///     Submitted; only ConfirmedOff may close a safety boundary.
    /// </summary>
    public sealed class SafetyOffReceipt
    {
        public Guid CommandId { get; set; }
        public Guid CorrelationId { get; set; }
        public SafetyOffTargetKind TargetKind { get; set; }
        public int TargetId { get; set; }
        public long RunEpoch { get; set; }
        public long OperationGeneration { get; set; }
        public DateTime SubmittedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public DateTime HardwareObservedUtc { get; set; }
        public SafetyOffEvidenceStatus Status { get; set; }
        public string EvidenceSource { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;

        public bool ConfirmedOff => Status == SafetyOffEvidenceStatus.ConfirmedOff;

        public SafetyOffReceipt Clone() => (SafetyOffReceipt)MemberwiseClone();
    }

    public enum CycleAttemptClosureDisposition
    {
        Unknown = 0,
        Committed = 1,
        Aborted = 2,
        Alarmed = 3
    }

    public sealed class CycleAttemptClosureReceipt
    {
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public string Device { get; set; } = string.Empty;
        public int Channel { get; set; }
        public long FormalSlot { get; set; } = -1;
        public long AttemptId { get; set; }
        public int Cycle { get; set; }
        public CycleAttemptClosureDisposition Disposition { get; set; }
        public long PersistenceVersion { get; set; }
        public bool Durable { get; set; }
        public string DurabilityEvidence { get; set; } = string.Empty;
        public DateTime CapturedUtc { get; set; }
    }

    public static class RecoveryOwnershipPolicy
    {
        public static bool HasExplicitOwner(ChannelRuntimeStateChangedEvent state)
        {
            return state != null &&
                   state.State == ChannelRuntimeState.Recovering &&
                   state.RecoveryOwnerKind != RecoveryOwnerKind.None &&
                   state.RecoveryOwnerKind != RecoveryOwnerKind.Unknown &&
                   state.RecoveryOwnerId != Guid.Empty &&
                   state.RecoveryOwnerGeneration > 0 &&
                   state.RecoveryTargetPhase != RecoveryTargetPhase.None;
        }

        public static bool IsOwnerCurrent(ChannelRuntimeStateChangedEvent state)
        {
            return HasExplicitOwner(state) &&
                   state.RunEpoch > 0 &&
                   state.RecoveryOwnerGeneration == state.RunEpoch;
        }

        public static bool MatchesExplicitOwner(
            ChannelRuntimeStateChangedEvent state,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            Guid ownerId,
            long ownerGeneration)
        {
            return HasExplicitOwner(state) &&
                   state.RecoveryOwnerKind == ownerKind &&
                   state.RecoveryTargetPhase == targetPhase &&
                   state.RecoveryOwnerId == ownerId &&
                   state.RecoveryOwnerGeneration == ownerGeneration;
        }
    }

    public enum BatchPauseState
    {
        Idle = 0,
        Running = 1,
        PausePending = 2,
        Paused = 3,
        ResumeChecking = 4,
        Qualification = 5,
        Stopping = 6,
        // Append-only for persisted/UI compatibility. The physical safety
        // boundary is complete, but DAQ health evidence is still pending.
        PauseHolding = 7
    }

    /// <summary>
    ///     当前进程一旦由 StopAll 撤销再次启动授权，所有界面入口必须共享同一闭锁语义。
    ///     该策略不决定普通批次状态下按钮是否可用，只负责覆盖“必须重启”的终态。
    /// </summary>
    public static class ProcessRestartUiPolicy
    {
        public const string LockedStartButtonText = "需重启软件";

        public static bool CanStartInProcess(bool requiresProcessRestart)
        {
            return !requiresProcessRestart;
        }

        public static string GetOperatorMessage(bool stopTimedOut)
        {
            return stopTimedOut
                ? "停止试验超过安全截止：本进程已永久禁止再次开始。Watchdog 安全接管状态以回执为准，人工停止后不会自动续跑。"
                : "停止试验已安全收口：本进程已永久禁止再次开始，请关闭软件后重新启动，软件将保持待机。";
        }
    }

    public sealed class BatchPauseStateChangedEvent
    {
        /// <summary>
        /// Monotonic pause-intent generation.  PausePending through the
        /// matching resume share one generation, allowing recovery workers to
        /// reject a late running-state rejoin from an older pause boundary.
        /// </summary>
        public long Generation { get; set; }
        public BatchPauseState State { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public DateTime TimestampUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public Guid CommandId { get; set; }
    }

    public enum ManualPauseStage
    {
        None = 0,
        CurrentCycleDrain = 1,
        PhysicalOffConfirm = 2,
        PersistenceDrain = 3,
        Completed = 4,
        SafetyFault = 5,
        Resumed = 6
    }

    /// <summary>供主界面与独立 Watchdog 共同使用的人工暂停权威进展。</summary>
    public sealed class ManualPauseProgressSnapshot
    {
        public bool Active { get; set; }
        public ManualPauseStage Stage { get; set; }
        public long ProgressVersion { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime StageStartedUtc { get; set; }
        public DateTime HardDeadlineUtc { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public int[] EnergizedChannels { get; set; } = Array.Empty<int>();
        public bool SafetyFault { get; set; }
        public string Detail { get; set; } = string.Empty;
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public Guid PauseCommandId { get; set; }
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }

        public ManualPauseProgressSnapshot Clone() => new ManualPauseProgressSnapshot
        {
            Active = Active,
            Stage = Stage,
            ProgressVersion = ProgressVersion,
            StartedUtc = StartedUtc,
            StageStartedUtc = StageStartedUtc,
            HardDeadlineUtc = HardDeadlineUtc,
            Channels = Channels?.ToArray() ?? Array.Empty<int>(),
            EnergizedChannels = EnergizedChannels?.ToArray() ?? Array.Empty<int>(),
            SafetyFault = SafetyFault,
            Detail = Detail ?? string.Empty,
            RunId = RunId,
            RunEpoch = RunEpoch,
            PauseCommandId = PauseCommandId,
            MotorsOff = MotorsOff,
            PowerOff = PowerOff,
            PressureSafe = PressureSafe,
            PersistenceDrained = PersistenceDrained
        };
    }

    public sealed class ChannelRuntimeStateChangedEvent
    {
        /// <summary>
        ///     控制层为同一通道发布的单调递增版本号。
        ///     UI 必须用它丢弃迟到的异步消息，不能让旧停机状态覆盖新运行状态。
        /// </summary>
        public long Revision { get; set; }
        public int Channel { get; set; }
        public ChannelRuntimeState State { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string ReasonText { get; set; } = string.Empty;
        public int? SourceChannel { get; set; }
        public int[] AffectedChannels { get; set; } = Array.Empty<int>();
        public DateTime TimestampUtc { get; set; }
        public Guid CorrelationId { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public long PauseGeneration { get; set; }
        public Guid PauseCommandId { get; set; }
        public bool Enabled { get; set; }
        public bool FormalPhaseCommitted { get; set; }
        public bool TimerActive { get; set; }
        public bool RunnerActive { get; set; }
        public bool Energized { get; set; }
        public bool PermanentAlarmLatched { get; set; }
        public string PermanentAlarmCode { get; set; } = string.Empty;
        public DateTime? PermanentAlarmUtc { get; set; }
        public int ConsecutivePeriodOverrunCount { get; set; }
        public RecoveryOwnerKind RecoveryOwnerKind { get; set; }
        public Guid RecoveryOwnerId { get; set; }
        public long RecoveryOwnerGeneration { get; set; }
        public RecoveryTargetPhase RecoveryTargetPhase { get; set; }
        /// <summary>发布本状态前的生命周期 Revision，用于对同一恢复事故做稳定去重。</summary>
        public long SourceStateRevision { get; set; }

        public ChannelRuntimeStateChangedEvent Clone()
        {
            return new ChannelRuntimeStateChangedEvent
            {
                Revision = Revision,
                Channel = Channel,
                State = State,
                ReasonCode = ReasonCode,
                ReasonText = ReasonText,
                SourceChannel = SourceChannel,
                AffectedChannels = AffectedChannels?.ToArray() ?? Array.Empty<int>(),
                TimestampUtc = TimestampUtc,
                CorrelationId = CorrelationId,
                RunId = RunId,
                RunEpoch = RunEpoch,
                PauseGeneration = PauseGeneration,
                PauseCommandId = PauseCommandId,
                Enabled = Enabled,
                FormalPhaseCommitted = FormalPhaseCommitted,
                TimerActive = TimerActive,
                RunnerActive = RunnerActive,
                Energized = Energized,
                PermanentAlarmLatched = PermanentAlarmLatched,
                PermanentAlarmCode = PermanentAlarmCode,
                PermanentAlarmUtc = PermanentAlarmUtc,
                ConsecutivePeriodOverrunCount = ConsecutivePeriodOverrunCount,
                RecoveryOwnerKind = RecoveryOwnerKind,
                RecoveryOwnerId = RecoveryOwnerId,
                RecoveryOwnerGeneration = RecoveryOwnerGeneration,
                RecoveryTargetPhase = RecoveryTargetPhase,
                SourceStateRevision = SourceStateRevision
            };
        }

        public bool IsNewerThan(ChannelRuntimeStateChangedEvent current)
        {
            if (current == null) return true;
            if (Channel != current.Channel) return false;

            // 新版控制层始终提供 Revision。保留时间比较只用于兼容进程内尚未带版本号的旧事件。
            if (Revision > 0 || current.Revision > 0)
                return Revision > current.Revision;
            return TimestampUtc > current.TimestampUtc;
        }
    }

    /// <summary>
    ///     通道预警覆盖层。它与 ChannelRuntimeStateChangedEvent 使用不同存储和 Revision，
    ///     因而快速负载/诊断预警绝不会改写 Learning/Qualification/Recovering 的恢复所有权。
    /// </summary>
    public sealed class ChannelWarningOverlayChangedEvent
    {
        public long Revision { get; set; }
        public int Channel { get; set; }
        public bool Active { get; set; }
        public string WarningCode { get; set; } = string.Empty;
        public string WarningText { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public Guid CorrelationId { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        /// <summary>预警首次激活时所属的圈尝试；用于阻止同圈结果提前清除。</summary>
        public long RaisedAttemptId { get; set; }
        public int RaisedCycleNumber { get; set; }
        public DateTime RaisedUtc { get; set; }
        /// <summary>仅在 Active=false 的恢复事件中赋值，保留完整审计链。</summary>
        public long ClearedAttemptId { get; set; }
        public int ClearedCycleNumber { get; set; }
        public string ClearReason { get; set; } = string.Empty;

        public ChannelWarningOverlayChangedEvent Clone()
        {
            return (ChannelWarningOverlayChangedEvent)MemberwiseClone();
        }

        public bool IsNewerThan(ChannelWarningOverlayChangedEvent current)
        {
            if (current == null) return true;
            if (Channel != current.Channel) return false;
            if (Revision > 0 || current.Revision > 0)
                return Revision > current.Revision;
            return TimestampUtc > current.TimestampUtc;
        }
    }

    internal sealed class ChannelWarningOverlayStore
    {
        private readonly ConcurrentDictionary<int, ChannelWarningOverlayChangedEvent> _warnings = new();

        internal ChannelWarningOverlayChangedEvent Publish(ChannelWarningOverlayChangedEvent next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (next.Channel < 1 || next.Channel > 12)
                throw new ArgumentOutOfRangeException(nameof(next.Channel));

            next.TimestampUtc = next.TimestampUtc == default ? DateTime.UtcNow : next.TimestampUtc;
            next.CorrelationId = next.CorrelationId == Guid.Empty ? Guid.NewGuid() : next.CorrelationId;
            if (next.Active && next.RaisedUtc == default)
                next.RaisedUtc = next.TimestampUtc;
            var candidate = next.Clone();
            return _warnings.AddOrUpdate(
                    candidate.Channel,
                    _ => CloneWithRevision(candidate, 1),
                    (_, current) => CloneWithRevision(candidate, current.Revision + 1))
                .Clone();
        }

        internal ChannelWarningOverlayChangedEvent Clear(
            int channel,
            Guid runId,
            long runEpoch,
            Guid correlationId = default,
            string clearReason = "LifecycleReset")
        {
            var current = Get(channel);
            return Publish(new ChannelWarningOverlayChangedEvent
            {
                Channel = channel,
                Active = false,
                WarningCode = current?.WarningCode ?? string.Empty,
                WarningText = current?.WarningText ?? string.Empty,
                RunId = runId,
                RunEpoch = runEpoch,
                CorrelationId = correlationId == Guid.Empty
                    ? current?.CorrelationId ?? Guid.Empty
                    : correlationId,
                TimestampUtc = DateTime.UtcNow,
                RaisedAttemptId = current?.RaisedAttemptId ?? 0,
                RaisedCycleNumber = current?.RaisedCycleNumber ?? 0,
                RaisedUtc = current?.RaisedUtc ?? default,
                ClearReason = clearReason ?? string.Empty
            });
        }

        /// <summary>
        ///     只清除调用方刚刚读取并验证过的那一条预警。Revision/运行身份/代码任一变化，
        ///     都表示有更新的事实到达，旧圈的恢复提交不得覆盖它。
        /// </summary>
        internal bool TryClearIfCurrent(
            int channel,
            Guid expectedRunId,
            long expectedRunEpoch,
            long expectedRevision,
            string expectedWarningCode,
            long recoveredAttemptId,
            int recoveredCycleNumber,
            Guid correlationId,
            string clearReason,
            out ChannelWarningOverlayChangedEvent cleared)
        {
            cleared = null;
            if (channel < 1 || channel > 12) return false;
            if (!_warnings.TryGetValue(channel, out var current) || !current.Active) return false;
            if (current.Revision != expectedRevision ||
                current.RunId != expectedRunId ||
                current.RunEpoch != expectedRunEpoch ||
                !string.Equals(
                    current.WarningCode ?? string.Empty,
                    expectedWarningCode ?? string.Empty,
                    StringComparison.Ordinal))
                return false;

            var candidate = current.Clone();
            candidate.Active = false;
            candidate.Revision = current.Revision + 1;
            candidate.TimestampUtc = DateTime.UtcNow;
            candidate.CorrelationId = correlationId == Guid.Empty
                ? current.CorrelationId
                : correlationId;
            candidate.ClearedAttemptId = recoveredAttemptId;
            candidate.ClearedCycleNumber = recoveredCycleNumber;
            candidate.ClearReason = clearReason ?? string.Empty;
            if (!_warnings.TryUpdate(channel, candidate, current)) return false;

            cleared = candidate.Clone();
            return true;
        }

        internal ChannelWarningOverlayChangedEvent Get(int channel)
        {
            return _warnings.TryGetValue(channel, out var warning) ? warning.Clone() : null;
        }

        internal IReadOnlyList<ChannelWarningOverlayChangedEvent> Snapshot()
        {
            return _warnings.Values.Select(x => x.Clone()).OrderBy(x => x.Channel).ToArray();
        }

        private static ChannelWarningOverlayChangedEvent CloneWithRevision(
            ChannelWarningOverlayChangedEvent warning,
            long revision)
        {
            var clone = warning.Clone();
            clone.Revision = revision;
            return clone;
        }
    }

    internal sealed class ChannelRuntimeStateStore
    {
        private readonly ConcurrentDictionary<int, ChannelRuntimeStateChangedEvent> _states = new();

        internal ChannelRuntimeStateChangedEvent Publish(
            ChannelRuntimeStateChangedEvent next,
            bool allowTerminalReset = false,
            bool allowSystemFaultReset = false)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (next.Channel < 1 || next.Channel > 12)
                throw new ArgumentOutOfRangeException(nameof(next.Channel));

            next.TimestampUtc = next.TimestampUtc == default ? DateTime.UtcNow : next.TimestampUtc;
            next.CorrelationId = next.CorrelationId == Guid.Empty ? Guid.NewGuid() : next.CorrelationId;
            next.AffectedChannels = (next.AffectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var candidate = next.Clone();

            return _states.AddOrUpdate(
                    candidate.Channel,
                    _ => CloneWithRevision(candidate, 1),
                    (_, current) => IsLatchedStop(current.State) &&
                                    !allowTerminalReset &&
                                    !(allowSystemFaultReset &&
                                      current.State == ChannelRuntimeState.SystemFault)
                        ? current
                        : CloneWithRevision(candidate, current.Revision + 1))
                .Clone();
        }

        private static ChannelRuntimeStateChangedEvent CloneWithRevision(
            ChannelRuntimeStateChangedEvent state,
            long revision)
        {
            var clone = state.Clone();
            clone.Revision = revision;
            return clone;
        }

        internal ChannelRuntimeStateChangedEvent Get(int channel)
        {
            return _states.TryGetValue(channel, out var state) ? state.Clone() : null;
        }

        internal bool TryRepairRecoveryOwnerIfCurrent(
            int channel,
            long expectedRevision,
            Guid expectedRunId,
            long expectedRunEpoch,
            Guid expectedIncidentId,
            RecoveryTaskRegistry.RecoveryTaskLeaseSnapshot lease,
            out ChannelRuntimeStateChangedEvent repaired)
        {
            repaired = null;
            if (lease == null || channel < 1 || channel > 12) return false;
            if (!_states.TryGetValue(channel, out var current) ||
                current.State != ChannelRuntimeState.Recovering ||
                current.Revision != expectedRevision ||
                current.RunId != expectedRunId ||
                current.RunEpoch != expectedRunEpoch ||
                current.CorrelationId != expectedIncidentId)
                return false;
            var candidate = current.Clone();
            candidate.Revision = current.Revision + 1;
            candidate.TimestampUtc = DateTime.UtcNow;
            candidate.RecoveryOwnerKind = lease.OwnerKind;
            candidate.RecoveryTargetPhase = lease.TargetPhase;
            candidate.RecoveryOwnerId = lease.OwnerId;
            candidate.RecoveryOwnerGeneration = lease.RunEpoch;
            if (!_states.TryUpdate(channel, candidate, current)) return false;
            repaired = candidate.Clone();
            return true;
        }

        internal IReadOnlyList<ChannelRuntimeStateChangedEvent> Snapshot()
        {
            return _states.Values.Select(x => x.Clone()).OrderBy(x => x.Channel).ToArray();
        }

        internal static bool IsLatchedStop(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.AlarmStopped ||
                   state == ChannelRuntimeState.InterlockStopped ||
                   state == ChannelRuntimeState.SystemFault ||
                   state == ChannelRuntimeState.StartBlocked;
        }
    }

    public sealed class LogicalQuiescenceSnapshot
    {
        /// <summary>
        /// Monotonic revision of this logical source only.  Aggregate Version
        /// may advance when DAQ/Stop/ownership sources publish and therefore
        /// cannot prove that channel progress itself was refreshed.
        /// </summary>
        public long SourceVersion { get; set; }
        /// <summary>UTC DateTime ticks at the logical-source commit point.</summary>
        public long CapturedUtcTicks { get; set; }
        public bool BatchLifecycleBusy { get; set; }
        public bool BatchSessionActive { get; set; }
        public Guid ActiveBatchId { get; set; }
        public int TimerActiveCount { get; set; }
        public int TimerCacheCount { get; set; }
        public int TimerCount { get; set; }
        public int RunnerActiveCount { get; set; }
        public int RunnerCacheCount { get; set; }
        public int RunnerCount { get; set; }
        public int EnergizedChannelCount { get; set; }
        public int StopCtsCount { get; set; }
        public int CycleCtsCount { get; set; }
        public int HydraulicParticipantCount { get; set; }
        public int HydraulicLeaseCount { get; set; }
        public int DaqRecoveryCount { get; set; }
        /// <summary>正常执行中的机械圈；仅用于残留诊断，绝不代表软件恢复。</summary>
        public int ActiveCycleCount { get; set; }
        public int SoftwareRecoveryCount { get; set; }
        public int RecoveryOwnerCount { get; set; }
        /// <summary>
        /// Recovery contracts and registry leases are part of logical
        /// quiescence.  A completed worker task is still non-quiescent until
        /// its terminal state has been published and the lease is closed.
        /// </summary>
        public int ActiveRecoveryContractCount { get; set; }
        public int ActiveRecoveryRegistryLeaseCount { get; set; }
        public HydraulicGenerationSnapshot[] HydraulicGroups { get; set; } =
            Array.Empty<HydraulicGenerationSnapshot>();
        public WatchdogChannelProgressSnapshot[] ChannelProgress { get; set; } =
            Array.Empty<WatchdogChannelProgressSnapshot>();

        public LogicalQuiescenceSnapshot Clone()
        {
            return new LogicalQuiescenceSnapshot
            {
                SourceVersion = SourceVersion,
                CapturedUtcTicks = CapturedUtcTicks,
                BatchLifecycleBusy = BatchLifecycleBusy,
                BatchSessionActive = BatchSessionActive,
                ActiveBatchId = ActiveBatchId,
                TimerActiveCount = TimerActiveCount,
                TimerCacheCount = TimerCacheCount,
                TimerCount = TimerCount,
                RunnerActiveCount = RunnerActiveCount,
                RunnerCacheCount = RunnerCacheCount,
                RunnerCount = RunnerCount,
                EnergizedChannelCount = EnergizedChannelCount,
                StopCtsCount = StopCtsCount,
                CycleCtsCount = CycleCtsCount,
                HydraulicParticipantCount = HydraulicParticipantCount,
                HydraulicLeaseCount = HydraulicLeaseCount,
                DaqRecoveryCount = DaqRecoveryCount,
                ActiveCycleCount = ActiveCycleCount,
                SoftwareRecoveryCount = SoftwareRecoveryCount,
                RecoveryOwnerCount = RecoveryOwnerCount,
                ActiveRecoveryContractCount = ActiveRecoveryContractCount,
                ActiveRecoveryRegistryLeaseCount = ActiveRecoveryRegistryLeaseCount,
                HydraulicGroups = (HydraulicGroups ?? Array.Empty<HydraulicGenerationSnapshot>())
                    .Select(group => group == null
                        ? null
                        : new HydraulicGenerationSnapshot
                        {
                            HydraulicId = group.HydraulicId,
                            CoordinatorEpoch = group.CoordinatorEpoch,
                            AdmissionOpen = group.AdmissionOpen,
                            ActiveGenerationCount = group.ActiveGenerationCount,
                            ActiveOperationCount = group.ActiveOperationCount,
                            ActiveLeaseCount = group.ActiveLeaseCount,
                            GenerationGateAvailable = group.GenerationGateAvailable,
                            RebuildCount = group.RebuildCount,
                            ActiveGenerationKeys = (group.ActiveGenerationKeys ?? Array.Empty<string>()).ToArray(),
                            PendingMembers = (group.PendingMembers ?? Array.Empty<int>()).ToArray()
                        })
                    .Where(group => group != null)
                    .ToArray(),
                ChannelProgress = (ChannelProgress ?? Array.Empty<WatchdogChannelProgressSnapshot>())
                    .Select(progress => progress?.Clone())
                    .Where(progress => progress != null)
                    .ToArray()
            };
        }

        public bool IsQuiescent => !BatchLifecycleBusy &&
                                   !BatchSessionActive &&
                                   ActiveBatchId == Guid.Empty &&
                                   TimerCount == 0 && RunnerCount == 0 &&
                                   EnergizedChannelCount == 0 &&
                                   StopCtsCount == 0 && CycleCtsCount == 0 &&
                                   HydraulicParticipantCount == 0 &&
                                   HydraulicLeaseCount == 0 &&
                                   DaqRecoveryCount == 0 && ActiveCycleCount == 0 &&
                                   SoftwareRecoveryCount == 0 &&
                                   RecoveryOwnerCount == 0 &&
                                   ActiveRecoveryContractCount == 0 &&
                                   ActiveRecoveryRegistryLeaseCount == 0 &&
                                   (HydraulicGroups ?? Array.Empty<HydraulicGenerationSnapshot>())
                                   .All(group => group.IsHealthyForFreshStart);

        public override string ToString()
        {
            return $"LifecycleBusy={BatchLifecycleBusy} Session={BatchSessionActive} " +
                   $"Batch={ActiveBatchId:N} " +
                   $"Timers={TimerCount}(Active={TimerActiveCount},Cache={TimerCacheCount}) " +
                   $"Runners={RunnerCount}(Active={RunnerActiveCount},Cache={RunnerCacheCount}) " +
                   $"Energized={EnergizedChannelCount} " +
                   $"StopCts={StopCtsCount} CycleCts={CycleCtsCount} " +
                   $"Participants={HydraulicParticipantCount} Leases={HydraulicLeaseCount} " +
                   $"DaqRecovery={DaqRecoveryCount} ActiveCycles={ActiveCycleCount} " +
                   $"SoftwareRecovery={SoftwareRecoveryCount} " +
                   $"RecoveryOwners={RecoveryOwnerCount} " +
                   $"Contracts={ActiveRecoveryContractCount} " +
                   $"RegistryLeases={ActiveRecoveryRegistryLeaseCount} " +
                   $"Hydraulics=[{string.Join(" | ", (HydraulicGroups ?? Array.Empty<HydraulicGenerationSnapshot>()).Select(x => x.ToString()))}]";
        }
    }

    internal sealed class LogicalRecoveryCounts
    {
        internal int ActiveCycleCount { get; set; }
        internal int SoftwareRecoveryCount { get; set; }
    }

    public sealed class WatchdogChannelProgressSnapshot
    {
        public int Channel { get; set; }
        public string State { get; set; } = string.Empty;
        public long StateRevision { get; set; }
        public long StateSinceUtcTicks { get; set; }
        public bool TimerActive { get; set; }
        public bool RunnerActive { get; set; }
        public bool Energized { get; set; }
        /// <summary>Per-channel logical progress revision; unrelated aggregate commits never change it.</summary>
        public long ProgressVersion { get; set; }
        /// <summary>UTC ticks of the last mechanically meaningful progress event.</summary>
        public long LastProgressUtcTicks { get; set; }
        public string ProgressKind { get; set; } = string.Empty;
        public long LastMechanicalCompletedUtcTicks { get; set; }
        public long MechanicalCompletedCount { get; set; }
        public int ConsecutiveSoftwareAbortCount { get; set; }
        public long DoCommandSequence { get; set; }
        public long PeakCutoffGeneration { get; set; }
        public long PeakCutoffSequence { get; set; }
        public string RecoveryOwnerKind { get; set; } = string.Empty;
        public string RecoveryOwnerId { get; set; } = string.Empty;
        public long RecoveryOwnerGeneration { get; set; }
        public string RecoveryTargetPhase { get; set; } = string.Empty;
        public long SourceStateRevision { get; set; }
        public bool WarningActive { get; set; }
        public string WarningCode { get; set; } = string.Empty;
        public long WarningRevision { get; set; }

        internal WatchdogChannelProgressSnapshot Clone()
        {
            return (WatchdogChannelProgressSnapshot)MemberwiseClone();
        }
    }

    public sealed class WatchdogDeviceStorageSnapshot
    {
        public string Device { get; set; } = string.Empty;
        public long CallbackGapCount { get; set; }
        public int Generation { get; set; }
        public long FrozenBoundary { get; set; }
        public long Persisted { get; set; }
        public long Head { get; set; }
        public long InFlight { get; set; }
        public int QueueDepth { get; set; }
        public string PersistenceState { get; set; } = string.Empty;
    }

    public sealed class WatchdogStorageSnapshot
    {
        public WatchdogDeviceStorageSnapshot Dev1 { get; set; }
        public WatchdogDeviceStorageSnapshot Dev2 { get; set; }
    }

    /// <summary>
    /// Immutable infrastructure/recovery source committed by the Controller
    /// before a watchdog heartbeat is captured.  This is deliberately kept
    /// separate from ownership, DAQ, Stop and logical sources so each producer
    /// has one explicit publication boundary in RecoveryAggregateStore.
    /// </summary>
    public sealed class InfrastructureRecoverySource
    {
        public bool ActiveRecovery { get; set; }
        public long RecoveryHardDeadlineUtcTicks { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string RecoveryIncident { get; set; } = string.Empty;
        public string RecoveryContext { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public string Stage { get; set; } = string.Empty;
        public int StageOrdinal { get; set; }
        public long ProgressVersion { get; set; }
        public long StageStartedUtcTicks { get; set; }
        public long HardDeadlineUtcTicks { get; set; }
        public DateTime StartedUtc { get; set; }
        public int[] ExpectedChannels { get; set; } = Array.Empty<int>();
        public int[] ExpectedRecoveryChannels { get; set; } = Array.Empty<int>();
        public int[] RecoveringChannels { get; set; } = Array.Empty<int>();
        public int[] OrphanRecoveryChannels { get; set; } = Array.Empty<int>();
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        public IReadOnlyDictionary<int, string> PermanentAlarmReasons { get; set; } =
            new Dictionary<int, string>();

        internal InfrastructureRecoverySource Clone()
        {
            return new InfrastructureRecoverySource
            {
                ActiveRecovery = ActiveRecovery,
                RecoveryHardDeadlineUtcTicks = RecoveryHardDeadlineUtcTicks,
                RunId = RunId,
                RunEpoch = RunEpoch,
                IncidentId = IncidentId ?? string.Empty,
                RecoveryIncident = RecoveryIncident ?? string.Empty,
                RecoveryContext = RecoveryContext ?? string.Empty,
                Device = Device ?? string.Empty,
                CorrelationId = CorrelationId,
                Stage = Stage ?? string.Empty,
                StageOrdinal = StageOrdinal,
                ProgressVersion = ProgressVersion,
                StageStartedUtcTicks = StageStartedUtcTicks,
                HardDeadlineUtcTicks = HardDeadlineUtcTicks,
                StartedUtc = StartedUtc,
                ExpectedChannels = (ExpectedChannels ?? Array.Empty<int>()).ToArray(),
                ExpectedRecoveryChannels =
                    (ExpectedRecoveryChannels ?? Array.Empty<int>()).ToArray(),
                RecoveringChannels = (RecoveringChannels ?? Array.Empty<int>()).ToArray(),
                OrphanRecoveryChannels =
                    (OrphanRecoveryChannels ?? Array.Empty<int>()).ToArray(),
                PermanentAlarmedChannels =
                    (PermanentAlarmedChannels ?? Array.Empty<int>()).ToArray(),
                PermanentAlarmReasons = (PermanentAlarmReasons ??
                                         new Dictionary<int, string>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty)
            };
        }
    }

    /// <summary>Immutable power-safety source for the aggregate heartbeat.</summary>
    public sealed class PowerRecoverySource
    {
        public bool PowerDisablePending { get; set; }
        public bool OutputsConfirmedOff { get; set; }
        public bool PowerOffUnconfirmed { get; set; }
        public long PowerDisableSinceUtcTicks { get; set; }
        public int[] PowerDisablePendingGroups { get; set; } = Array.Empty<int>();
        public DateTime? PowerDisableSinceUtc { get; set; }

        internal PowerRecoverySource Clone()
        {
            return new PowerRecoverySource
            {
                PowerDisablePending = PowerDisablePending,
                OutputsConfirmedOff = OutputsConfirmedOff,
                PowerOffUnconfirmed = PowerOffUnconfirmed,
                PowerDisableSinceUtcTicks = PowerDisableSinceUtcTicks,
                PowerDisablePendingGroups =
                    (PowerDisablePendingGroups ?? Array.Empty<int>()).ToArray(),
                PowerDisableSinceUtc = PowerDisableSinceUtc
            };
        }
    }

    /// <summary>Immutable timer/orphan source for the aggregate heartbeat.</summary>
    public sealed class TimerRecoverySource
    {
        public bool OrphanPaused { get; set; }
        public long PauseSinceUtcTicks { get; set; }
        public int[] OrphanPausedChannels { get; set; } = Array.Empty<int>();

        internal TimerRecoverySource Clone()
        {
            return new TimerRecoverySource
            {
                OrphanPaused = OrphanPaused,
                PauseSinceUtcTicks = PauseSinceUtcTicks,
                OrphanPausedChannels =
                    (OrphanPausedChannels ?? Array.Empty<int>()).ToArray()
            };
        }
    }

    /// <summary>
    /// Controller 权威恢复快照。独立 Watchdog 只消费此快照，不从 UI 文本或通道
    /// AlarmStopped 状态推断永久性、恢复阶段或电源关闭进度。
    /// </summary>
    public sealed class WatchdogRecoverySnapshot
    {
        public bool ActiveRecovery { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        public bool OutputsConfirmedOff { get; set; }
        public bool PowerOffUnconfirmed { get; set; }
        public long RecoveryHardDeadlineUtcTicks { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string RecoveryIncident { get; set; } = string.Empty;
        public string RecoveryContext { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public string Stage { get; set; } = string.Empty;
        public int StageOrdinal { get; set; }
        public long ProgressVersion { get; set; }
        public long StageStartedUtcTicks { get; set; }
        public long HardDeadlineUtcTicks { get; set; }
        public DateTime StartedUtc { get; set; }
        public long PauseSinceUtcTicks { get; set; }
        public long PowerDisableSinceUtcTicks { get; set; }
        public int[] ExpectedChannels { get; set; } = Array.Empty<int>();
        public int[] ExpectedRecoveryChannels { get; set; } = Array.Empty<int>();
        public int[] OrphanPausedChannels { get; set; } = Array.Empty<int>();
        public int[] RecoveringChannels { get; set; } = Array.Empty<int>();
        public int[] OrphanRecoveryChannels { get; set; } = Array.Empty<int>();
        public int[] PowerDisablePendingGroups { get; set; } = Array.Empty<int>();
        public DateTime? PowerDisableSinceUtc { get; set; }
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        public IReadOnlyDictionary<int, string> PermanentAlarmReasons { get; set; } =
            new Dictionary<int, string>();

        /// <summary>
        /// All recovery evidence captured by EpbManager under one
        /// _recoveryContractGate critical section.  Consumers must prefer this
        /// aggregate over combining channel/UI, contract and progress reads
        /// from separate calls.
        /// </summary>
        public WatchdogRecoveryAggregateSnapshot Aggregate { get; internal set; }

        internal WatchdogRecoverySnapshot CloneForHeartbeat()
        {
            return new WatchdogRecoverySnapshot
            {
                ActiveRecovery = ActiveRecovery,
                OrphanPaused = OrphanPaused,
                PowerDisablePending = PowerDisablePending,
                OutputsConfirmedOff = OutputsConfirmedOff,
                PowerOffUnconfirmed = PowerOffUnconfirmed,
                RecoveryHardDeadlineUtcTicks = RecoveryHardDeadlineUtcTicks,
                RunId = RunId,
                RunEpoch = RunEpoch,
                IncidentId = IncidentId,
                RecoveryIncident = RecoveryIncident,
                RecoveryContext = RecoveryContext,
                Device = Device,
                CorrelationId = CorrelationId,
                Stage = Stage,
                StageOrdinal = StageOrdinal,
                ProgressVersion = ProgressVersion,
                StageStartedUtcTicks = StageStartedUtcTicks,
                HardDeadlineUtcTicks = HardDeadlineUtcTicks,
                StartedUtc = StartedUtc,
                PauseSinceUtcTicks = PauseSinceUtcTicks,
                PowerDisableSinceUtcTicks = PowerDisableSinceUtcTicks,
                ExpectedChannels = (ExpectedChannels ?? Array.Empty<int>()).ToArray(),
                ExpectedRecoveryChannels = (ExpectedRecoveryChannels ?? Array.Empty<int>()).ToArray(),
                OrphanPausedChannels = (OrphanPausedChannels ?? Array.Empty<int>()).ToArray(),
                RecoveringChannels = (RecoveringChannels ?? Array.Empty<int>()).ToArray(),
                OrphanRecoveryChannels = (OrphanRecoveryChannels ?? Array.Empty<int>()).ToArray(),
                PowerDisablePendingGroups = (PowerDisablePendingGroups ?? Array.Empty<int>()).ToArray(),
                PowerDisableSinceUtc = PowerDisableSinceUtc,
                PermanentAlarmedChannels = (PermanentAlarmedChannels ?? Array.Empty<int>()).ToArray(),
                PermanentAlarmReasons = (PermanentAlarmReasons ??
                                         new Dictionary<int, string>())
                    .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty),
                Aggregate = Aggregate
            };
        }
    }

    /// <summary>
    /// Immutable recovery evidence supplied to the watchdog heartbeat.  Each
    /// nested object is cloned at construction; no live controller dictionary
    /// or Task reference escapes the aggregate.
    /// </summary>
    public sealed class WatchdogRecoveryAggregateSnapshot
    {
        internal WatchdogRecoveryAggregateSnapshot(
            long version,
            DateTime capturedUtc,
            IEnumerable<ChannelRuntimeStateChangedEvent> channelStates,
            IEnumerable<RecoveryContractSnapshot> contracts,
            IEnumerable<WatchdogRecoveryLeaseSnapshot> registryLeases,
            int daqRecoveryCount,
            string daqStage,
            long daqProgressVersion,
            DateTime daqStageStartedUtc,
            DateTime daqHardDeadlineUtc,
            LogicalQuiescenceSnapshot logical,
            StopSafetyProgressSnapshot stopProgress,
            string retainedCommittedStage = "",
            long retainedCommittedProgressVersion = 0,
            DateTime retainedCommittedStageStartedUtc = default,
            DateTime retainedCommittedHardDeadlineUtc = default,
            string retainedTerminalStage = "",
            long retainedTerminalProgressVersion = 0,
            bool isStable = true,
            long retainedCommittedAggregateVersion = 0,
            long retainedTerminalAggregateVersion = 0,
            InfrastructureRecoverySource infrastructure = null,
            PowerRecoverySource power = null,
            TimerRecoverySource timer = null)
        {
            Version = version;
            CapturedUtc = capturedUtc;
            ChannelStates = Array.AsReadOnly((channelStates ?? Array.Empty<ChannelRuntimeStateChangedEvent>())
                .Select(state => state?.Clone())
                .Where(state => state != null)
                .OrderBy(state => state.Channel)
                .ToArray());
            Contracts = Array.AsReadOnly((contracts ?? Array.Empty<RecoveryContractSnapshot>())
                .Select(contract => contract?.Clone())
                .Where(contract => contract != null)
                .OrderBy(contract => contract.IncidentId)
                .ToArray());
            RegistryLeases = Array.AsReadOnly((registryLeases ?? Array.Empty<WatchdogRecoveryLeaseSnapshot>())
                .Select(lease => lease?.Clone())
                .Where(lease => lease != null)
                .OrderBy(lease => lease.Id)
                .ToArray());
            DaqRecoveryCount = Math.Max(0, daqRecoveryCount);
            DaqStage = daqStage ?? string.Empty;
            DaqProgressVersion = daqProgressVersion;
            DaqStageStartedUtc = daqStageStartedUtc;
            DaqHardDeadlineUtc = daqHardDeadlineUtc;
            RetainedCommittedStage = retainedCommittedStage ?? string.Empty;
            RetainedCommittedProgressVersion = retainedCommittedProgressVersion;
            RetainedCommittedStageStartedUtc = retainedCommittedStageStartedUtc;
            RetainedCommittedHardDeadlineUtc = retainedCommittedHardDeadlineUtc;
            RetainedTerminalStage = retainedTerminalStage ?? string.Empty;
            RetainedTerminalProgressVersion = retainedTerminalProgressVersion;
            RetainedCommittedAggregateVersion = retainedCommittedAggregateVersion;
            RetainedTerminalAggregateVersion = retainedTerminalAggregateVersion;
            IsStable = isStable;
            Logical = logical == null ? null : logical.Clone();
            StopProgress = stopProgress?.Clone();
            Infrastructure = (infrastructure ?? new InfrastructureRecoverySource()).Clone();
            Power = (power ?? new PowerRecoverySource()).Clone();
            Timer = (timer ?? new TimerRecoverySource()).Clone();
        }

        public long Version { get; }
        public DateTime CapturedUtc { get; }
        public IReadOnlyList<ChannelRuntimeStateChangedEvent> ChannelStates { get; }
        public IReadOnlyList<RecoveryContractSnapshot> Contracts { get; }
        public IReadOnlyList<WatchdogRecoveryLeaseSnapshot> RegistryLeases { get; }
        public int DaqRecoveryCount { get; }
        public string DaqStage { get; }
        public long DaqProgressVersion { get; }
        public DateTime DaqStageStartedUtc { get; }
        public DateTime DaqHardDeadlineUtc { get; }
        /// <summary>
        /// Separate retained evidence for the successful Committed point and
        /// the later Terminal point.  They must not be collapsed into one
        /// phase, otherwise a watchdog cannot distinguish a committed rejoin
        /// from an abrupt owner disappearance.
        /// </summary>
        public string RetainedCommittedStage { get; }
        public long RetainedCommittedProgressVersion { get; }
        public DateTime RetainedCommittedStageStartedUtc { get; }
        public DateTime RetainedCommittedHardDeadlineUtc { get; }
        public string RetainedTerminalStage { get; }
        public long RetainedTerminalProgressVersion { get; }
        public long RetainedCommittedAggregateVersion { get; }
        public long RetainedTerminalAggregateVersion { get; }
        public bool IsStable { get; }
        public LogicalQuiescenceSnapshot Logical { get; }
        public StopSafetyProgressSnapshot StopProgress { get; }
        public InfrastructureRecoverySource Infrastructure { get; }
        public PowerRecoverySource Power { get; }
        public TimerRecoverySource Timer { get; }
    }

    public sealed class WatchdogRecoveryLeaseSnapshot
    {
        internal WatchdogRecoveryLeaseSnapshot(
            long id,
            string operation,
            long runEpoch,
            IEnumerable<int> channels,
            bool isBound,
            bool isTerminal)
        {
            Id = id;
            Operation = operation ?? string.Empty;
            RunEpoch = runEpoch;
            Channels = Array.AsReadOnly(
                (channels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray());
            IsBound = isBound;
            IsTerminal = isTerminal;
        }

        public long Id { get; }
        public string Operation { get; }
        public long RunEpoch { get; }
        public IReadOnlyList<int> Channels { get; }
        public bool IsBound { get; }
        public bool IsTerminal { get; }

        internal WatchdogRecoveryLeaseSnapshot Clone() =>
            new WatchdogRecoveryLeaseSnapshot(
                Id,
                Operation,
                RunEpoch,
                Channels,
                IsBound,
                IsTerminal);
    }

    /// <summary>
    /// Immutable identity of one recovery incident.  The contract is created
    /// before the worker Task and before Recovering is published; callers must
    /// keep the same owner/generation for the entire incident and publish a
    /// terminal state before releasing it.
    /// </summary>
    public sealed class RecoveryContractSnapshot
    {
        internal RecoveryContractSnapshot(
            Guid incidentId,
            Guid runId,
            long runEpoch,
            Guid ownerId,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            string operation,
            DateTime startedUtc,
            DateTime hardDeadlineUtc,
            IEnumerable<int> channels)
            : this(
                incidentId,
                runId,
                runEpoch,
                ownerId,
                ownerKind,
                targetPhase,
                operation,
                startedUtc,
                hardDeadlineUtc,
                channels,
                channels)
        {
        }

        internal RecoveryContractSnapshot(
            Guid incidentId,
            Guid runId,
            long runEpoch,
            Guid ownerId,
            RecoveryOwnerKind ownerKind,
            RecoveryTargetPhase targetPhase,
            string operation,
            DateTime startedUtc,
            DateTime hardDeadlineUtc,
            IEnumerable<int> ownedChannels,
            IEnumerable<int> safetyAffectedChannels)
        {
            IncidentId = incidentId;
            RunId = runId;
            RunEpoch = runEpoch;
            OwnerId = ownerId;
            OwnerKind = ownerKind;
            TargetPhase = targetPhase;
            Operation = operation ?? string.Empty;
            StartedUtc = startedUtc;
            HardDeadlineUtc = hardDeadlineUtc;
            OwnedChannels = Array.AsReadOnly(
                (ownedChannels ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray());
            SafetyAffectedChannels = Array.AsReadOnly(
                (safetyAffectedChannels ?? Array.Empty<int>())
                .Concat(OwnedChannels)
                .Distinct()
                .OrderBy(x => x)
                .ToArray());
        }

        public Guid IncidentId { get; }
        public Guid RunId { get; }
        public long RunEpoch { get; }
        public Guid OwnerId { get; }
        public RecoveryOwnerKind OwnerKind { get; }
        public RecoveryTargetPhase TargetPhase { get; }
        public string Operation { get; }
        public DateTime StartedUtc { get; }
        public DateTime HardDeadlineUtc { get; }
        /// <summary>
        /// Logical channels that own Recovering/terminal lifecycle state.
        /// Kept as Channels for compatibility with existing observers.
        /// </summary>
        public IReadOnlyList<int> Channels => OwnedChannels;
        public IReadOnlyList<int> OwnedChannels { get; }
        /// <summary>Physical cohort that must be commanded OFF as one safety unit.</summary>
        public IReadOnlyList<int> SafetyAffectedChannels { get; }

        public RecoveryContractSnapshot Clone() => new RecoveryContractSnapshot(
            IncidentId,
            RunId,
            RunEpoch,
            OwnerId,
            OwnerKind,
            TargetPhase,
            Operation,
            StartedUtc,
            HardDeadlineUtc,
            OwnedChannels,
            SafetyAffectedChannels);
    }

    public enum StopSafetyStage
    {
        None = 0,
        /// <summary>Admission fence plus immediate DO OFF / PSU Disable submission.</summary>
        AdmitAndSubmitSafety = 5,
        FreezeActiveWork = 10,
        RevokeExecutionAuthorization = 20,
        SubmitPhysicalOff = 30,
        StartPowerDisable = 40,
        ClearTimerAndRunner = 50,
        ClearRecoveryOwners = 60,
        // StopAll releases the hydraulic owner and confirms pressure before it
        // freezes the DAQ/persistence boundary.  The numeric order is part of
        // the monotonic watchdog contract; keeping StopAcquisition below
        // ReleaseHydraulics silently discarded the real transition.
        ReleaseHydraulics = 70,
        StopAcquisition = 80,
        ClosePersistenceBoundary = 90,
        VerifyLogicalQuiescence = 100,
        Completed = 110,
        TimedOut = 120
    }

    public enum StopSafetyOutcome
    {
        Unknown = 0,
        CompletedSafe = 1,
        SafeButRestartRequired = 2,
        PhysicalSafetyUnconfirmed = 3,
        Failed = 4
    }

    public sealed class StopSafetyProgressSnapshot
    {
        public Guid TransactionId { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        /// <summary>Controller StopAll generation owning this snapshot.</summary>
        public long Generation { get; set; }
        public long ProgressVersion { get; set; }
        public StopSafetyStage Stage { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime StageStartedUtc { get; set; }
        /// <summary>整个 StopAll 事务的 45 秒逃逸截止；阶段截止不能覆盖它。</summary>
        public DateTime HardDeadlineUtc { get; set; }
        /// <summary>当前阶段独立硬截止；不同阶段不得共用一个固定五秒门限。</summary>
        public DateTime StageHardDeadlineUtc { get; set; }
        /// <summary>
        /// 当前阶段在硬截止之后仍允许等待真实材料进展的宽限；它不是阶段硬截止，
        /// 也不能被同阶段的普通心跳或诊断刷新重置。
        /// </summary>
        public int StageNoProgressGraceMs { get; set; }
        /// <summary>最近一次由压力/DAQ/持久化等真实材料事件推进的 UTC 时间。</summary>
        public DateTime LastMaterialProgressUtc { get; set; }
        /// <summary>
        /// 最近一次被 Controller 接受的、带来源身份的材料证据版本。它只由
        /// 单调 sequence/boundary 推进，不能由 Detail 文本或心跳刷新产生。
        /// </summary>
        public long MaterialEvidenceVersion { get; set; }
        public bool Active { get; set; }
        public bool PhysicalOffSubmitted { get; set; }
        public bool PowerDisableStarted { get; set; }
        public bool PhysicalSafe { get; set; }
        /// <summary>
        ///     The stop transaction crossed its takeover boundary.  This is a
        ///     sticky safety fact, not a UI-derived timeout label; once set it
        ///     remains visible in the retained aggregate snapshot.
        /// </summary>
        public bool TakeoverRequired { get; set; }
        /// <summary>True only for the terminal hard-deadline outcome.</summary>
        public bool TimedOut { get; set; }
        /// <summary>Stable machine-readable terminal reason for the sidecar.</summary>
        public string TerminalReason { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public StopSafetyProgressSnapshot Clone()
        {
            return (StopSafetyProgressSnapshot)MemberwiseClone();
        }
    }

    public enum PowerShutdownDisposition
    {
        Unknown = 0,
        ConfirmedOff = 1,
        NotRequiredNoActiveTrial = 2,
        CommunicationUnavailableSkipped = 3,
        Failed = 4
    }

    public sealed class StopSafetyResult
    {
        public StopSafetyOutcome Outcome { get; set; } = StopSafetyOutcome.Unknown;
        public StopSafetyStage LastStage { get; set; } = StopSafetyStage.None;
        public bool TimedOut { get; set; }
        public bool RequiresProcessRestart { get; set; }
        // A completed physical stop may precede the cancelled startup task's
        // logical tail. Only that same transaction may resolve this provisional result.
        public bool LogicalCleanupPending { get; set; }
        public bool PhysicalOffSubmitted { get; set; }
        public bool PowerDisableStarted { get; set; }
        public string StageError { get; set; } = string.Empty;
        public StopSource Source { get; set; } = StopSource.UnknownLegacy;
        public string CorrelationId { get; set; } = string.Empty;
        public Guid SafetyTransactionId { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public long SafetyBoundaryGeneration { get; set; }
        public bool MotorOffCommandSucceeded { get; set; }
        public bool PowerOffConfirmed { get; set; }
        public PowerShutdownDisposition PowerDisposition { get; set; }
        public bool PressureSafeConfirmed { get; set; }
        public bool PersistenceBoundaryConfirmed { get; set; }
        public bool RawStorageFlushed { get; set; }
        public bool DataContinuityCompromised { get; set; }
        public string DataContinuityError { get; set; } = string.Empty;
        public bool ReusedPreviousResult { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public string MotorError { get; set; } = string.Empty;
        public string PowerError { get; set; } = string.Empty;
        public string PressureError { get; set; } = string.Empty;
        public string PersistenceError { get; set; } = string.Empty;
        public bool LogicalQuiescenceConfirmed { get; set; }
        public string LogicalError { get; set; } = string.Empty;
        public LogicalQuiescenceSnapshot LogicalState { get; set; }

        public bool PowerShutdownSatisfied => PowerOffConfirmed ||
                                              PowerDisposition == PowerShutdownDisposition.NotRequiredNoActiveTrial ||
                                              PowerDisposition == PowerShutdownDisposition.CommunicationUnavailableSkipped;
        public bool CanReleaseAcquisition => MotorOffCommandSucceeded && PowerShutdownSatisfied;
        /// <summary>
        ///     Application exit is stricter than releasing acquisition hardware during a
        ///     controlled stop. The process must remain alive while the accepted Raw/SQLite
        ///     prefix is still being retried; otherwise disposing the persistence worker can
        ///     discard the final batch or alarm evidence.
        /// </summary>
        public bool CanCloseApplication => CanReleaseAcquisition && PersistenceBoundaryConfirmed;
        public bool PhysicalSafetyConfirmed => MotorOffCommandSucceeded && PowerOffConfirmed && PressureSafeConfirmed;
        public bool FullyConfirmed => PhysicalSafetyConfirmed && PersistenceBoundaryConfirmed;
        public bool CanRestartInProcess => PhysicalSafetyConfirmed &&
                                           !RequiresProcessRestart && !TimedOut &&
                                           Outcome != StopSafetyOutcome.PhysicalSafetyUnconfirmed &&
                                           FullyConfirmed && LogicalQuiescenceConfirmed &&
                                           !DataContinuityCompromised;

        internal bool TryCompleteLogicalCleanup(Guid transactionId, long generation, bool hardRestartRequired)
        {
            if (!LogicalCleanupPending || !LogicalQuiescenceConfirmed || !FullyConfirmed ||
                DataContinuityCompromised || TimedOut || hardRestartRequired ||
                SafetyTransactionId == Guid.Empty || SafetyTransactionId != transactionId ||
                SafetyBoundaryGeneration != generation || LastStage != StopSafetyStage.Completed)
                return false;
            LogicalCleanupPending = false;
            RequiresProcessRestart = false;
            Outcome = StopSafetyOutcome.CompletedSafe;
            return true;
        }

        public StopSafetyResult Clone(bool reused = false)
        {
            return new StopSafetyResult
            {
                Outcome = Outcome,
                LastStage = LastStage,
                TimedOut = TimedOut,
                RequiresProcessRestart = RequiresProcessRestart,
                LogicalCleanupPending = LogicalCleanupPending,
                PhysicalOffSubmitted = PhysicalOffSubmitted,
                PowerDisableStarted = PowerDisableStarted,
                StageError = StageError,
                Source = Source,
                CorrelationId = CorrelationId,
                SafetyTransactionId = SafetyTransactionId,
                RunId = RunId,
                RunEpoch = RunEpoch,
                SafetyBoundaryGeneration = SafetyBoundaryGeneration,
                MotorOffCommandSucceeded = MotorOffCommandSucceeded,
                PowerOffConfirmed = PowerOffConfirmed,
                PowerDisposition = PowerDisposition,
                PressureSafeConfirmed = PressureSafeConfirmed,
                PersistenceBoundaryConfirmed = PersistenceBoundaryConfirmed,
                RawStorageFlushed = RawStorageFlushed,
                DataContinuityCompromised = DataContinuityCompromised,
                DataContinuityError = DataContinuityError,
                ReusedPreviousResult = reused,
                StartedUtc = StartedUtc,
                CompletedUtc = CompletedUtc,
                MotorError = MotorError,
                PowerError = PowerError,
                PressureError = PressureError,
                PersistenceError = PersistenceError,
                LogicalQuiescenceConfirmed = LogicalQuiescenceConfirmed,
                LogicalError = LogicalError,
                LogicalState = LogicalState
            };
        }
    }

    /// <summary>
    ///     Request-scoped stop evidence.  Historical transaction results are
    ///     referenced for audit only and are never returned as this request's
    ///     current physical outcome.
    /// </summary>
    public sealed class StopRequestReceipt
    {
        public Guid RequestId { get; set; }
        public StopSource Source { get; set; }
        public DateTime RequestedUtc { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public Guid PhysicalTransactionId { get; set; }
        public bool JoinedActiveTransaction { get; set; }
        public Guid PreviousTransactionId { get; set; }
        public StopSafetyOutcome PreviousOutcome { get; set; }
        public StopSafetyResult Result { get; set; }

        public StopRequestReceipt Clone() => new StopRequestReceipt
        {
            RequestId = RequestId,
            Source = Source,
            RequestedUtc = RequestedUtc,
            StartedUtc = StartedUtc,
            CompletedUtc = CompletedUtc,
            PhysicalTransactionId = PhysicalTransactionId,
            JoinedActiveTransaction = JoinedActiveTransaction,
            PreviousTransactionId = PreviousTransactionId,
            PreviousOutcome = PreviousOutcome,
            Result = Result?.Clone()
        };
    }
}
