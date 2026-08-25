using System;
using System.Threading.Tasks;
using MtEmbTest;
using MTTFTest.Watchdog.Client;

namespace MTEmbTest
{
    /// <summary>
    /// The single production WinForms binding transaction.  Main_Frm and the
    /// monitor must use this component for the exact sequence:
    /// capture identity, reserve the one target, register the typed handler,
    /// await BindingCompletion, then publish Ready only after a final exact
    /// identity check.  The isolated UI acceptance seam calls this same
    /// component with requireGlobalAttached=false; it does not copy the
    /// transaction algorithm.
    /// </summary>
    internal sealed class WinFormsWatchdogUiBindingTransaction
    {
        internal async Task<WinFormsWatchdogUiBindingReceipt> ExecuteAsync(
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity expectedIdentity,
            string targetId,
            IWatchdogCallbackPostTarget target,
            bool transferOwnership,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler,
            WatchdogRuntime.RuntimeStopAllHandlerLease existingHandler,
            bool requireGlobalAttached)
        {
            if (context == null) return WinFormsWatchdogUiBindingReceipt.Rejected("RuntimeContextMissing");
            if (expectedIdentity == null)
                return WinFormsWatchdogUiBindingReceipt.Rejected("ValidatedIdentityMissing");
            if (!WatchdogRuntime.IsUiBindingIdentityCurrent(
                    context, expectedIdentity, requireGlobalAttached))
                return WinFormsWatchdogUiBindingReceipt.Rejected("ExactAttachedRequired");

            var targetWasAlreadyBound = context.SafetyTargetLease != null &&
                context.SafetyTargetLease.IsActive;
            var targetLease = WatchdogRuntime.BindStopAllSafetyTarget(
                context, targetId, target, transferOwnership);
            if (targetLease == null || !targetLease.Accepted)
                return WinFormsWatchdogUiBindingReceipt.Rejected(
                    targetLease?.RejectionReason ?? "SafetyTargetBindRejected");

            var handlerLease = existingHandler;
            if (handlerLease != null &&
                (!ReferenceEquals(handlerLease.Context, context) ||
                 !string.Equals(handlerLease.SubscriberId, subscriberId,
                     StringComparison.Ordinal)))
            {
                CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                return WinFormsWatchdogUiBindingReceipt.Rejected("ExistingHandlerIdentityMismatch");
            }
            var createdHandlerLease = false;
            if (handlerLease == null || !handlerLease.IsActive)
            {
                if (handler == null)
                {
                    CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                    return WinFormsWatchdogUiBindingReceipt.Rejected("StopHandlerMissing");
                }
                try
                {
                    handlerLease = WatchdogRuntime.RegisterStopAllHandler(
                        context, targetLease, subscriberId, handler);
                    createdHandlerLease = handlerLease != null;
                }
                catch
                {
                    CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                    return WinFormsWatchdogUiBindingReceipt.Rejected("HandlerReservationFault");
                }
            }
            if (handlerLease == null || !handlerLease.Accepted)
            {
                if (createdHandlerLease)
                    CleanupHandler(handlerLease);
                CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                return WinFormsWatchdogUiBindingReceipt.Rejected(
                    handlerLease?.RejectionReason ?? "HandlerReservationRejected");
            }

            try
            {
                var registration = await handlerLease.BindingCompletion
                    .ConfigureAwait(true);
                var ready = registration != null && handlerLease.IsBound &&
                    handlerLease.IsActive &&
                    context.PipelineState == RuntimeCallbackPipelineState.Ready &&
                    WatchdogRuntime.IsUiBindingIdentityCurrent(
                        context, expectedIdentity, requireGlobalAttached);
                if (!ready)
                {
                    if (createdHandlerLease) CleanupHandler(handlerLease);
                    CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                    return WinFormsWatchdogUiBindingReceipt.Rejected(
                        "UiBindingIdentityChanged");
                }
                return WinFormsWatchdogUiBindingReceipt.AcceptedReady(
                    context, targetLease, handlerLease,
                    context.PipelineGeneration, expectedIdentity);
            }
            catch
            {
                if (createdHandlerLease) CleanupHandler(handlerLease);
                CleanupUnpublishedTarget(targetLease, targetWasAlreadyBound);
                return WinFormsWatchdogUiBindingReceipt.Rejected("BindingCompletionFault");
            }
        }

        private static void CleanupHandler(
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease)
        {
            try { handlerLease?.Dispose(); } catch { }
        }

        private static void CleanupUnpublishedTarget(
            RuntimeSafetyTargetLease targetLease, bool targetWasAlreadyBound)
        {
            if (targetLease == null || targetWasAlreadyBound) return;
            try { targetLease.ReleaseUnpublished(); } catch { }
            // The transaction only disposes a target when it was explicitly
            // given ownership.  ReleaseUnpublished removes the exact lease
            // from the context; this second step closes an owned WinForms
            // target without ever disposing an already-published target.
            try { targetLease.DisposeOwnedTarget(); } catch { }
        }
    }

}
