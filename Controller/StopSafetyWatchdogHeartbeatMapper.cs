using MTTFTest.Watchdog.Protocol;

namespace Controller
{
    /// <summary>
    /// Production mapping boundary from one immutable Controller aggregate
    /// snapshot to the wire-level stop projection.  UI and watchdog callers
    /// must pass the already captured aggregate; this mapper never reads live
    /// manager dictionaries or a UI cache.
    /// </summary>
    internal static class StopSafetyWatchdogHeartbeatMapper
    {
        internal static StopSafetyHeartbeatProjection Map(
            WatchdogRecoveryAggregateSnapshot aggregate)
        {
            return Map(aggregate?.StopProgress, aggregate?.Version ?? 0);
        }

        internal static StopSafetyHeartbeatProjection Map(
            StopSafetyProgressSnapshot stop,
            long aggregateVersion = 0)
        {
            if (stop == null)
                return new StopSafetyHeartbeatProjection
                {
                    AggregateVersion = aggregateVersion
                };

            return new StopSafetyHeartbeatProjection
            {
                AggregateVersion = aggregateVersion,
                Active = stop.Active,
                TakeoverRequired = stop.TakeoverRequired,
                TimedOut = stop.TimedOut,
                TerminalReason = stop.TerminalReason ?? string.Empty,
                TransactionId = stop.TransactionId == System.Guid.Empty
                    ? string.Empty
                    : stop.TransactionId.ToString("N"),
                Generation = stop.Generation,
                ProgressVersion = stop.ProgressVersion,
                StageStartedUtc = stop.StageStartedUtc.Ticks,
                StageHardDeadlineUtc = stop.StageHardDeadlineUtc.Ticks,
                StopHardDeadlineUtc = stop.HardDeadlineUtc.Ticks,
                StageNoProgressGraceMs = stop.StageNoProgressGraceMs,
                LastMaterialProgressUtc = stop.LastMaterialProgressUtc.Ticks,
                Stage = stop.Stage.ToString()
            };
        }
    }
}
