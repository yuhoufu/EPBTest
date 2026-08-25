using System;
using System.Linq;

namespace Controller
{
    /// <summary>
    /// Small production projection used by the UI heartbeat.  It accepts only
    /// the already committed immutable aggregate; a missing aggregate is an
    /// unavailable source and therefore produces an empty, fail-closed state
    /// set.  There is intentionally no legacy/UI dictionary parameter.
    /// </summary>
    internal static class RecoveryHeartbeatAggregateSource
    {
        internal static ChannelRuntimeStateChangedEvent[] CaptureChannelStates(
            WatchdogRecoveryAggregateSnapshot aggregate)
        {
            return (aggregate?.ChannelStates ??
                    Array.Empty<ChannelRuntimeStateChangedEvent>())
                .Where(state => state != null)
                .Select(state => state.Clone())
                .ToArray();
        }

        internal static long CaptureVersion(
            WatchdogRecoveryAggregateSnapshot aggregate)
        {
            return aggregate?.Version ?? 0;
        }
    }
}
