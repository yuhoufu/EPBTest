using System;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// The one host-side stop monitor.  It owns projection ordering and the
    /// once-only sink gate; WatchdogHost supplies only validated identity,
    /// policy decisions and the real StopAll request sink.
    /// </summary>
    internal sealed class WatchdogHostStopMonitor
    {
        private readonly HostStopSafetySupervisor _supervisor;

        internal WatchdogHostStopMonitor(string sessionId)
        {
            _supervisor = new HostStopSafetySupervisor(sessionId);
        }

        internal StopSafetyHeartbeatProjection Snapshot => _supervisor.Snapshot;

        internal double NoProgressSeconds(DateTime utcNow) =>
            _supervisor.NoProgressSeconds(utcNow);

        internal int DispatchCount => _supervisor.DispatchCount;

        internal void Observe(StopSafetyHeartbeatProjection projection) =>
            _supervisor.Observe(projection);

        internal void NotifyValidatedAttached(
            string sessionId,
            string transactionId,
            long generation,
            string attachmentIdentity = null,
            int processId = 0,
            long processStartUtcTicks = 0,
            long attachEpoch = 0) =>
            _supervisor.NotifyValidatedAttached(
                sessionId,
                transactionId,
                generation,
                attachmentIdentity,
                processId,
                processStartUtcTicks,
                attachEpoch);

        internal bool EvaluateAndDispatch(Action<string> sink) =>
            _supervisor.EvaluateAndDispatch(sink);

        internal bool DispatchPolicyDecision(string reason, Action<string> sink) =>
            _supervisor.TryDispatchReason(reason, sink);

        /// <summary>
        /// Evaluates one stop-safety tick using the production watchdog policy
        /// and dispatches through the supervisor's once-per-transaction gate.
        /// This is the only host-side entry point for an active StopAll
        /// deadline; callers must not duplicate the policy in a monitor loop.
        /// </summary>
        internal bool EvaluateTick(
            DateTime nowUtc,
            bool processAlive,
            double heartbeatAgeSeconds,
            bool sessionRevoked,
            bool manualStopRequested,
            bool alreadyTakingOver,
            bool recoveryActive = false,
            bool orphanPaused = false,
            bool powerDisablePending = false,
            bool hasRecoveryEligibleChannels = false,
            bool manualPauseActive = false,
            bool manualPauseUnsafe = false,
            bool hardwareSafeIdle = false,
            Action<string> sink = null)
        {
            var projection = Snapshot;
            // Never consume the supervisor's once-only gate for a heartbeat
            // that does not identify an actual StopAll transaction.  Liveness
            // loss during ordinary running/recovery is owned by the broader
            // watchdog policy; this monitor is strictly stop-scoped.
            var hasStopTransaction =
                (projection.Active || projection.TakeoverRequired || projection.TimedOut) &&
                !string.IsNullOrWhiteSpace(projection.TransactionId) &&
                projection.Generation > 0;
            if (!hasStopTransaction)
                return false;
            var stageAgeSeconds = projection.StageStartedUtc > 0
                ? Math.Max(0, (nowUtc.Ticks - projection.StageStartedUtc) /
                    (double)TimeSpan.TicksPerSecond)
                : NoProgressSeconds(nowUtc);
            var noProgressSeconds = NoProgressSeconds(nowUtc);
            var nowTicks = nowUtc.Ticks;

            var shouldTakeover = WatchdogTakeoverPolicy.ShouldTakeover(
                sessionRevoked,
                manualStopRequested,
                alreadyTakingOver,
                processAlive,
                heartbeatAgeSeconds,
                recoveryActive,
                orphanPaused,
                powerDisablePending,
                stageAgeSeconds,
                hasRecoveryEligibleChannels,
                stopAllActive: projection.Active,
                logicalResidue: false,
                inconsistentRecoveryEvidence: false,
                formalProgressStalled: false,
                manualPauseActive: manualPauseActive,
                manualPauseUnsafe: manualPauseUnsafe,
                nowUtcTicks: nowTicks,
                stopStageHardDeadlineUtcTicks: projection.StageHardDeadlineUtc,
                stopNoProgressSeconds: noProgressSeconds,
                stopStageNoProgressGraceMs: projection.StageNoProgressGraceMs,
                stopHardDeadlineUtcTicks: projection.StopHardDeadlineUtc);

            // A terminal safety fact is sticky even after Active is cleared.
            // The policy still supplies the normal liveness/deadline decision;
            // this branch only carries the already-published authoritative
            // terminal fact through the same once gate.
            if ((projection.TakeoverRequired || projection.TimedOut) &&
                !sessionRevoked && !alreadyTakingOver &&
                !(hardwareSafeIdle && !projection.Active))
                shouldTakeover = true;
            if (!shouldTakeover || sink == null)
                return false;

            var reason = BuildReason(
                projection,
                processAlive,
                heartbeatAgeSeconds,
                nowTicks,
                noProgressSeconds);
            return _supervisor.TryDispatchReason(reason, sink);
        }

        private static string BuildReason(
            StopSafetyHeartbeatProjection projection,
            bool processAlive,
            double heartbeatAgeSeconds,
            long nowTicks,
            double noProgressSeconds)
        {
            if (!processAlive)
                return "StopSafetyProcessNotAlive";
            if (heartbeatAgeSeconds >= 5)
                return "StopSafetyHeartbeatUnresponsive";
            if (!string.IsNullOrWhiteSpace(projection.TerminalReason))
                return projection.TerminalReason;
            if (projection.StageHardDeadlineUtc > 0 &&
                nowTicks >= projection.StageHardDeadlineUtc)
            {
                return "StopStageDeadlineExceeded:" +
                    (string.IsNullOrWhiteSpace(projection.Stage)
                        ? "Unknown"
                        : projection.Stage) + ";NoMaterialProgressMs=" +
                    (noProgressSeconds * 1000).ToString("F0",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    ";GraceMs=" + projection.StageNoProgressGraceMs;
            }
            return projection.TimedOut
                ? "StopSafetyTimedOut"
                : "StopSafetyPolicyTakeover";
        }
    }
}
