using System;

namespace MTEmbTest
{
    internal enum WinFormsWatchdogUiEntryKind
    {
        Normal = 0,
        Recovery = 1,
        StartIdle = 2
    }

    internal sealed class WinFormsWatchdogUiEntryDecision
    {
        internal bool Allowed { get; }
        internal bool AttachExistingAuthorityOnly { get; }
        internal string Reason { get; }

        internal WinFormsWatchdogUiEntryDecision(
            bool allowed, bool attachExistingAuthorityOnly, string reason)
        {
            Allowed = allowed;
            AttachExistingAuthorityOnly = attachExistingAuthorityOnly;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// One production entry policy for normal start, recovery and safe-idle.
    /// Batch start is never allowed to proceed on a merely existing context;
    /// the exact Attached and Ready gates are required first.  Recovery and
    /// safe-idle are explicitly AttachOnly and therefore cannot authorize a
    /// new Sidecar launch.
    /// </summary>
    internal static class WinFormsWatchdogUiEntryPolicy
    {
        internal static WinFormsWatchdogUiEntryDecision Evaluate(
            WinFormsWatchdogUiEntryKind kind,
            bool exactAttached,
            bool bindingReady)
        {
            var attachOnly = kind != WinFormsWatchdogUiEntryKind.Normal;
            if (!exactAttached)
                return new WinFormsWatchdogUiEntryDecision(
                    false, attachOnly, "ExactAttachedRequired");
            if (!bindingReady)
                return new WinFormsWatchdogUiEntryDecision(
                    false, attachOnly, "UiBindingReadyRequired");
            return new WinFormsWatchdogUiEntryDecision(true, attachOnly, string.Empty);
        }

        internal static WinFormsWatchdogUiEntryDecision EvaluateRecoveryIntent(
            WatchdogRecoveryIntent intent,
            bool exactAttached)
        {
            if (intent == null)
                return new WinFormsWatchdogUiEntryDecision(
                    false, true, "RecoveryIntentMissing");
            if (string.IsNullOrWhiteSpace(intent.SessionId) ||
                string.IsNullOrWhiteSpace(intent.PipeName) ||
                intent.SidecarProcessId <= 0 ||
                intent.SidecarProcessStartUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(intent.SidecarInstanceNonce))
                return new WinFormsWatchdogUiEntryDecision(
                    false, true, "RecoveryAuthorityIdentityIncomplete");
            if (!exactAttached)
                return new WinFormsWatchdogUiEntryDecision(
                    false, true, "ExactAttachedRequired");
            return new WinFormsWatchdogUiEntryDecision(true, true, string.Empty);
        }
    }
}
