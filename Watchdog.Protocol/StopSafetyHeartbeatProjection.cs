using System;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// The single stop-safety view consumed by the sidecar.  It is a value
    /// projection of the Controller's immutable heartbeat evidence; callers
    /// must not infer takeover from UI text or from StopAllActive alone.
    /// </summary>
    public sealed class StopSafetyHeartbeatProjection
    {
        public bool Active { get; set; }
        public bool TakeoverRequired { get; set; }
        public bool TimedOut { get; set; }
        public string TerminalReason { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public long Generation { get; set; }
        public long ProgressVersion { get; set; }
        /// <summary>
        /// Aggregate revision from which all stop fields were mapped.  It is
        /// diagnostic/order evidence only; transaction/generation remain the
        /// takeover identity.
        /// </summary>
        public long AggregateVersion { get; set; }
        /// <summary>
        /// Validated main-process identity used by the sidecar ordering gate.
        /// Stop progress from a different PID/start pair is never merged into
        /// the current attachment, even when its generation is numerically
        /// newer or equal.
        /// </summary>
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long AttachEpoch { get; set; }
        public long StageStartedUtc { get; set; }
        public long StageHardDeadlineUtc { get; set; }
        /// <summary>Whole StopAll transaction escape deadline (UTC ticks).</summary>
        public long StopHardDeadlineUtc { get; set; }
        public int StageNoProgressGraceMs { get; set; }
        public long LastMaterialProgressUtc { get; set; }
        public string Stage { get; set; } = string.Empty;

        public StopSafetyHeartbeatProjection Clone()
        {
            return (StopSafetyHeartbeatProjection)MemberwiseClone();
        }

        public static StopSafetyHeartbeatProjection FromHeartbeat(
            WatchdogHeartbeat heartbeat)
        {
            if (heartbeat == null)
                return new StopSafetyHeartbeatProjection();

            return new StopSafetyHeartbeatProjection
            {
                Active = heartbeat.StopAllActive,
                TakeoverRequired = heartbeat.StopTakeoverRequired,
                TimedOut = heartbeat.StopTimedOut,
                TerminalReason = heartbeat.StopTerminalReason ?? string.Empty,
                TransactionId = heartbeat.StopTransactionId ?? string.Empty,
                Generation = heartbeat.StopGeneration,
                ProgressVersion = heartbeat.StopProgressVersion,
                AggregateVersion = heartbeat.RecoveryAggregateSnapshotVersion,
                ProcessId = heartbeat.ProcessId,
                ProcessStartUtcTicks = heartbeat.ProcessStartUtcTicks,
                AttachEpoch = heartbeat.AttachEpoch,
                StageStartedUtc = heartbeat.StopStageStartedUtc,
                StageHardDeadlineUtc = heartbeat.StopStageHardDeadlineUtc > 0
                    ? heartbeat.StopStageHardDeadlineUtc
                    : heartbeat.StageHardDeadlineUtc,
                StopHardDeadlineUtc = heartbeat.StopHardDeadlineUtc,
                StageNoProgressGraceMs = heartbeat.StopStageNoProgressGraceMs > 0
                    ? heartbeat.StopStageNoProgressGraceMs
                    : heartbeat.StageNoProgressGraceMs,
                LastMaterialProgressUtc = heartbeat.StopLastMaterialProgressUtc > 0
                    ? heartbeat.StopLastMaterialProgressUtc
                    : heartbeat.LastMaterialProgressUtc,
                Stage = heartbeat.StopStage ?? string.Empty
            };
        }
    }
}
