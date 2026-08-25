using System;

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
