using System;
using System.Collections.Generic;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Pure takeover decision helpers.  The process host supplies evidence and
    /// performs no policy inference from UI text or persistence counters.
    /// </summary>
    public static class WatchdogTakeoverPolicy
    {
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
            bool hasRecoveryEligibleChannels)
        {
            if (sessionRevoked || manualStopRequested || alreadyTakingOver)
                return false;
            if (!processAlive || heartbeatAgeSeconds >= 5)
                return true;
            if (!hasRecoveryEligibleChannels)
                return false;
            if (orphanPaused && stageAgeSeconds >= 5)
                return true;
            if (powerDisablePending && stageAgeSeconds >= 5)
                return true;
            return recoveryActive && stageAgeSeconds >= 60;
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
            return markerExists || manualStopRequested || explicitLifecycleEnd;
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
            return !sessionRevoked && !manualStopRequested && currentIdentityMatches;
        }
    }
}
