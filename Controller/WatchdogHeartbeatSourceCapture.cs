using System;

namespace Controller
{
    /// <summary>
    /// The production heartbeat entry point captures the controller recovery
    /// snapshot exactly once.  Keeping this tiny boundary outside the WinForms
    /// type makes the one-read/no-fallback contract deterministic to test.
    /// </summary>
    internal sealed class WatchdogHeartbeatSourceCapture
    {
        private WatchdogHeartbeatSourceCapture(
            WatchdogRecoverySnapshot snapshot)
        {
            Snapshot = snapshot;
            Aggregate = snapshot?.Aggregate;
            AggregateVersion = Aggregate?.Version ?? 0;
        }

        internal WatchdogRecoverySnapshot Snapshot { get; }
        internal WatchdogRecoveryAggregateSnapshot Aggregate { get; }
        internal long AggregateVersion { get; }

        internal static WatchdogHeartbeatSourceCapture CaptureOnce(
            Func<WatchdogRecoverySnapshot> source)
        {
            // Do not retry a null/legacy aggregate here.  The caller must
            // build a fail-closed heartbeat from this one captured object.
            return new WatchdogHeartbeatSourceCapture(source?.Invoke());
        }
    }
}
