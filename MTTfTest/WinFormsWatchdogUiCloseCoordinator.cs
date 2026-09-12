using System;
using System.Threading.Tasks;

namespace MTEmbTest
{
    /// <summary>
    /// Shared FormClosing decision core.  Main_Frm and the production UI
    /// acceptance seam use the same evidence rule: a stable runtime,
    /// retained shutdown owner, or any Main-owned UI binding keeps the
    /// message pump alive until the retention coordinator publishes a
    /// terminal receipt.
    /// </summary>
    internal sealed class WinFormsWatchdogUiCloseDecision
    {
        internal bool HasActiveEvidence { get; }
        internal bool KeepMessagePump { get; }
        internal bool ReleaseOnlyAfterTerminal { get; }
        internal string Reason { get; }

        internal WinFormsWatchdogUiCloseDecision(
            bool hasActiveEvidence,
            bool keepMessagePump,
            bool releaseOnlyAfterTerminal,
            string reason)
        {
            HasActiveEvidence = hasActiveEvidence;
            KeepMessagePump = keepMessagePump;
            ReleaseOnlyAfterTerminal = releaseOnlyAfterTerminal;
            Reason = reason ?? string.Empty;
        }
    }

    internal static class WinFormsWatchdogUiCloseCoordinator
    {
        // Caller serializes admission on the UI thread / exit gate. A completed
        // attempt is not a close receipt; retries must run authorization again.
        internal static bool TryStartCloseAttempt(ref Task current, Func<Task> prepareAndAuthorize)
        {
            if (current != null && !current.IsCompleted) return false;
            current = prepareAndAuthorize();
            return true;
        }

        internal static bool ShouldCoordinateMainClose(
            bool hasActiveEvidence,
            int mdiChildCount)
        {
            // An MDI child is itself an application-owned resource.  After a
            // completed manual stop the watchdog runtime can already be
            // released while the monitor window is still alive; that close
            // must still join the single-flight application-exit coordinator.
            return hasActiveEvidence || mdiChildCount > 0;
        }

        internal static bool ShouldApplyLegacyMdiGuard(
            bool watchdogCloseAuthorized,
            int mdiChildCount)
        {
            return !watchdogCloseAuthorized && mdiChildCount > 0;
        }

        internal static WinFormsWatchdogUiCloseDecision Evaluate(
            RuntimeTransportSnapshot composite,
            RuntimeShutdownRetentionOwner retained,
            bool hasUiResources)
        {
            var hasRuntime = composite != null &&
                (composite.Context != null || composite.Engine?.SessionActive == true);
            var hasRetained = retained != null;
            var hasEvidence = hasRuntime || hasRetained || hasUiResources;
            if (!hasEvidence)
                return new WinFormsWatchdogUiCloseDecision(
                    false, false, false, "NoRuntimeOrUiEvidence");
            return new WinFormsWatchdogUiCloseDecision(
                true, true, true,
                hasRetained ? "RetainedShutdownInProgress" :
                (hasRuntime ? "RuntimeShutdownInProgress" : "UiBindingInProgress"));
        }
    }
}
