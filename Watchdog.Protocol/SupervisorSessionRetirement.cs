using System;

namespace MTTFTest.Watchdog.Protocol
{
    public static class SupervisorSessionRetirement
    {
        // A terminal host must not be relaunched just because its process ended.
        // Incomplete independent safety is resumed; missing proof is blocked,
        // never converted into a successful physical stop.
        public static string Decide(string sessionId, WatchdogSessionManifest manifest,
            WatchdogClosingTombstone closing, WatchdogSafetyHandoffReceipt handoff)
        {
            if (manifest == null || !string.Equals(sessionId, manifest.SessionId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(manifest.TerminalUtc) || string.IsNullOrWhiteSpace(manifest.TerminalState))
                return "Active";
            if (handoff != null && string.Equals(handoff.SessionId, sessionId, StringComparison.Ordinal))
            {
                if (!handoff.IsTerminal) return "Recovering";
                if (!handoff.IsSafetyCompleted) return "Blocked";
            }
            if (closing != null && string.Equals(closing.SessionId, sessionId, StringComparison.Ordinal) &&
                closing.State == WatchdogClosingTombstoneState.Closing &&
                !closing.IsSafetyTerminal && handoff?.IsSafetyCompleted != true)
                return "Blocked";
            return "Retired";
        }
        public static bool SuppressesRestart(string state) =>
            string.Equals(state, "Retired", StringComparison.Ordinal) ||
            string.Equals(state, "Blocked", StringComparison.Ordinal);
    }
}
