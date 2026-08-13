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
        Qualification = 15
    }

    public enum BatchPauseState
    {
        Idle = 0,
        Running = 1,
        PausePending = 2,
        Paused = 3,
        ResumeChecking = 4,
        Qualification = 5,
        Stopping = 6
    }

    public sealed class BatchPauseStateChangedEvent
    {
        public BatchPauseState State { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public DateTime TimestampUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
        public Guid RunId { get; set; }
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
        public bool Enabled { get; set; }
        public bool FormalPhaseCommitted { get; set; }
        public bool TimerActive { get; set; }
        public bool RunnerActive { get; set; }
        public bool Energized { get; set; }

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
                Enabled = Enabled,
                FormalPhaseCommitted = FormalPhaseCommitted,
                TimerActive = TimerActive,
                RunnerActive = RunnerActive,
                Energized = Energized
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
        public bool BatchLifecycleBusy { get; set; }
        public bool BatchSessionActive { get; set; }
        public Guid ActiveBatchId { get; set; }
        public int TimerCount { get; set; }
        public int RunnerCount { get; set; }
        public int StopCtsCount { get; set; }
        public int CycleCtsCount { get; set; }
        public int HydraulicParticipantCount { get; set; }
        public int HydraulicLeaseCount { get; set; }
        public int DaqRecoveryCount { get; set; }
        public int SoftwareRecoveryCount { get; set; }
        public int RecoveryOwnerCount { get; set; }
        public HydraulicGenerationSnapshot[] HydraulicGroups { get; set; } =
            Array.Empty<HydraulicGenerationSnapshot>();

        public bool IsQuiescent => !BatchLifecycleBusy &&
                                   !BatchSessionActive &&
                                   ActiveBatchId == Guid.Empty &&
                                   TimerCount == 0 && RunnerCount == 0 &&
                                   StopCtsCount == 0 && CycleCtsCount == 0 &&
                                   HydraulicParticipantCount == 0 &&
                                   HydraulicLeaseCount == 0 &&
                                   DaqRecoveryCount == 0 && SoftwareRecoveryCount == 0 &&
                                   RecoveryOwnerCount == 0 &&
                                   (HydraulicGroups ?? Array.Empty<HydraulicGenerationSnapshot>())
                                   .All(group => group.IsHealthyForFreshStart);

        public override string ToString()
        {
            return $"LifecycleBusy={BatchLifecycleBusy} Session={BatchSessionActive} " +
                   $"Batch={ActiveBatchId:N} Timers={TimerCount} Runners={RunnerCount} " +
                   $"StopCts={StopCtsCount} CycleCts={CycleCtsCount} " +
                   $"Participants={HydraulicParticipantCount} Leases={HydraulicLeaseCount} " +
                   $"DaqRecovery={DaqRecoveryCount} SoftwareRecovery={SoftwareRecoveryCount} " +
                   $"RecoveryOwners={RecoveryOwnerCount} " +
                   $"Hydraulics=[{string.Join(" | ", (HydraulicGroups ?? Array.Empty<HydraulicGenerationSnapshot>()).Select(x => x.ToString()))}]";
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
    /// Controller 权威恢复快照。独立 Watchdog 只消费此快照，不从 UI 文本或通道
    /// AlarmStopped 状态推断永久性、恢复阶段或电源关闭进度。
    /// </summary>
    public sealed class WatchdogRecoverySnapshot
    {
        public bool ActiveRecovery { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string RecoveryIncident { get; set; } = string.Empty;
        public string RecoveryContext { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public string Stage { get; set; } = string.Empty;
        public int StageOrdinal { get; set; }
        public DateTime StartedUtc { get; set; }
        public long PauseSinceUtcTicks { get; set; }
        public long PowerDisableSinceUtcTicks { get; set; }
        public int[] ExpectedChannels { get; set; } = Array.Empty<int>();
        public int[] ExpectedRecoveryChannels { get; set; } = Array.Empty<int>();
        public int[] OrphanPausedChannels { get; set; } = Array.Empty<int>();
        public int[] PowerDisablePendingGroups { get; set; } = Array.Empty<int>();
        public DateTime? PowerDisableSinceUtc { get; set; }
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        public IReadOnlyDictionary<int, string> PermanentAlarmReasons { get; set; } =
            new Dictionary<int, string>();
    }

    public sealed class StopSafetyResult
    {
        public StopSource Source { get; set; } = StopSource.UnknownLegacy;
        public string CorrelationId { get; set; } = string.Empty;
        public Guid RunId { get; set; }
        public bool MotorOffCommandSucceeded { get; set; }
        public bool PowerOffConfirmed { get; set; }
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

        public bool CanReleaseAcquisition => MotorOffCommandSucceeded && PowerOffConfirmed;
        /// <summary>
        ///     Application exit is stricter than releasing acquisition hardware during a
        ///     controlled stop. The process must remain alive while the accepted Raw/SQLite
        ///     prefix is still being retried; otherwise disposing the persistence worker can
        ///     discard the final batch or alarm evidence.
        /// </summary>
        public bool CanCloseApplication => CanReleaseAcquisition && PersistenceBoundaryConfirmed;
        public bool PhysicalSafetyConfirmed => CanReleaseAcquisition && PressureSafeConfirmed;
        public bool FullyConfirmed => PhysicalSafetyConfirmed && PersistenceBoundaryConfirmed;
        public bool CanRestartInProcess => FullyConfirmed && LogicalQuiescenceConfirmed &&
                                           !DataContinuityCompromised;

        public StopSafetyResult Clone(bool reused = false)
        {
            return new StopSafetyResult
            {
                Source = Source,
                CorrelationId = CorrelationId,
                RunId = RunId,
                MotorOffCommandSucceeded = MotorOffCommandSucceeded,
                PowerOffConfirmed = PowerOffConfirmed,
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
}
