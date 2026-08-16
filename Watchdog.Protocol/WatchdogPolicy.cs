using System;
using System.Collections.Generic;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogLifecyclePolicy
    {
        public static bool IsTerminalMessage(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.RunStopped, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RunCompleted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ApplicationClosing, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ShutdownExpected, StringComparison.Ordinal);
        }

        public static bool IsRetryableFailure(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.BatchStartFailed, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RecoveryAttemptFailed, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Pure takeover decision helpers.  The process host supplies evidence and
    /// performs no policy inference from UI text or persistence counters.
    /// </summary>
    public static class WatchdogTakeoverPolicy
    {
        public static double SelectFormalProgressTimeoutSeconds(int expectedCyclePeriodMs)
        {
            var periodSeconds = Math.Max(1, expectedCyclePeriodMs) / 1000.0;
            return Math.Min(3600, Math.Max(90, periodSeconds * 4 + 30));
        }

        public static bool ShouldTakeover(
            bool sessionRevoked,
            bool manualStopRequested,
            bool alreadyTakingOver,
            bool processAlive,
            double heartbeatAgeSeconds,
            bool recoveryActive,
            bool orphanPaused,
            bool powerDisablePending,
            double stageAgeSeconds,
            bool hasRecoveryEligibleChannels,
            bool stopAllActive = false,
            bool logicalResidue = false,
            bool inconsistentRecoveryEvidence = false,
            bool formalProgressStalled = false)
        {
            if (sessionRevoked || alreadyTakingOver)
                return false;
            if (!processAlive || heartbeatAgeSeconds >= 5)
                return true;
            if (stopAllActive && stageAgeSeconds >= 5)
                return true;
            if (logicalResidue && stageAgeSeconds >= 5)
                return true;
            if (inconsistentRecoveryEvidence)
                return true;
            if (formalProgressStalled && hasRecoveryEligibleChannels)
                return true;
            if (!hasRecoveryEligibleChannels && !manualStopRequested)
                return false;
            if (orphanPaused && stageAgeSeconds >= 5)
                return true;
            if (powerDisablePending && stageAgeSeconds >= 5)
                return true;
            return recoveryActive && stageAgeSeconds >= 15;
        }
    }

    public static class RecoveryProgressSignature
    {
        public static string Build(
            IEnumerable<string> stageStates,
            int daqRecoveryCount,
            int softwareRecoveryCount,
            int recoveryOwnerCount,
            int stageOrdinal,
            string recoveryIncident,
            string recoveryContext,
            bool orphanPaused,
            bool powerDisablePending)
        {
            var states = string.Join("|", (stageStates ?? Enumerable.Empty<string>())
                .Where(value => value != null)
                .OrderBy(value => value, StringComparer.Ordinal));
            return string.Format(
                "{0}|D={1}|S={2}|O={3}|Stage={4}|Incident={5}|Context={6}|Orphan={7}|PowerPending={8}",
                states,
                daqRecoveryCount,
                softwareRecoveryCount,
                recoveryOwnerCount,
                stageOrdinal,
                recoveryIncident ?? string.Empty,
                recoveryContext ?? string.Empty,
                orphanPaused,
                powerDisablePending);
        }
    }

    public static class SessionRevocationPolicy
    {
        public static bool IsRevoked(bool markerExists, bool manualStopRequested, bool explicitLifecycleEnd)
        {
            // ManualStopIntent keeps the sidecar's kill authority alive until
            // StopCompleted or the 15-second manual deadline. It is not a
            // session revocation marker in protocol v2.
            return markerExists || explicitLifecycleEnd;
        }
    }

    public static class WatchdogProcessIdentityPolicy
    {
        public static bool Matches(int expectedPid, long expectedStartTicks, int actualPid, long actualStartTicks)
        {
            return expectedPid > 0 && expectedStartTicks > 0 &&
                   expectedPid == actualPid && expectedStartTicks == actualStartTicks;
        }

        public static bool CanKillOldProcess(
            bool sessionRevoked,
            bool manualStopRequested,
            bool currentIdentityMatches)
        {
            return !sessionRevoked && currentIdentityMatches;
        }
    }
}
