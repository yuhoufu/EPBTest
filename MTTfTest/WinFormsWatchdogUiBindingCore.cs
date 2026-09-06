using System;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;
using MtEmbTest;

namespace MTEmbTest
{
    /// <summary>
    /// Shared production transaction used by Main_Frm and
    /// FrmEpbMainMonitor.  It is intentionally a thin adapter over the
    /// runtime's bind/register/BindingCompletion gates; no test-only identity
    /// or lifecycle algorithm is duplicated here.
    /// </summary>
    internal static class WinFormsWatchdogUiBindingCore
    {
        private static readonly WinFormsWatchdogUiBindingTransaction Transaction =
            new WinFormsWatchdogUiBindingTransaction();

        internal static Task<WinFormsWatchdogUiBindingReceipt> ExecuteAsync(
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
            return Transaction.ExecuteAsync(
                context, expectedIdentity, targetId, target, transferOwnership,
                subscriberId, handler, existingHandler, requireGlobalAttached);
        }

        internal static RuntimeSafetyTargetLease BindTarget(
            RuntimeTransportSessionContext context,
            string targetId,
            IWatchdogCallbackPostTarget target,
            bool transferOwnership)
        {
            return WatchdogRuntime.BindStopAllSafetyTarget(
                context, targetId, target, transferOwnership);
        }

        internal static WatchdogRuntime.RuntimeStopAllHandlerLease RegisterHandler(
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            return WatchdogRuntime.RegisterStopAllHandler(
                context, targetLease, subscriberId, handler);
        }

        internal static async Task<bool> AwaitReadyAsync(
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity expectedIdentity,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease)
        {
            if (context == null || expectedIdentity == null ||
                handlerLease == null || !handlerLease.Accepted)
                return false;
            try
            {
                var registration = await handlerLease.BindingCompletion
                    .ConfigureAwait(true);
                return registration != null && handlerLease.IsBound &&
                       handlerLease.IsActive &&
                       context.PipelineState == RuntimeCallbackPipelineState.Ready &&
                       context.ValidatedAttachIdentity != null &&
                       context.ValidatedAttachIdentity.ExactKey == expectedIdentity.ExactKey;
            }
            catch
            {
                return false;
            }
        }
    }
}
