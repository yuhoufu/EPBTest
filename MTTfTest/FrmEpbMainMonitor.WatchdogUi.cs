using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;
using MtEmbTest;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : IWinFormsWatchdogStopSafetyPort
    {
        private readonly TaskCompletionSource<bool> _watchdogControllerReady =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _watchdogUiHandlerGate = new object();
        private WatchdogRuntime.RuntimeStopAllHandlerLease _watchdogUiHandlerLease;
        private RuntimeTransportSessionContext _watchdogUiContext;
        private RuntimeValidatedAttachIdentity _watchdogUiIdentity;
        private long _watchdogUiPipelineGeneration;

        internal bool WatchdogUiHandlerBound =>
            _watchdogUiHandlerLease?.IsBound == true &&
            _watchdogUiHandlerLease.IsActive;

        internal void MarkWatchdogControllerReadyIfInitialized()
        {
            if (_epb != null && _cfg?.Test != null && IsHandleCreated)
                _watchdogControllerReady.TrySetResult(true);
        }

        internal async Task<bool> WaitUntilWatchdogControllerReadyAsync(
            int timeoutMilliseconds = 30000)
        {
            if (_epb != null && _cfg?.Test != null && IsHandleCreated)
                return true;
            var completed = await Task.WhenAny(
                    _watchdogControllerReady.Task,
                    Task.Delay(Math.Max(1, timeoutMilliseconds)))
                .ConfigureAwait(true);
            return completed == _watchdogControllerReady.Task &&
                   _epb != null && _cfg?.Test != null && IsHandleCreated;
        }

        internal Func<WatchdogStopAllOfferEnvelope, Task> WatchdogSafetyHandler =>
            HandleWatchdogStopAllEnvelopeAsync;

        internal WatchdogRuntime.RuntimeStopAllHandlerLease WatchdogSafetyHandlerLease
        {
            get { lock (_watchdogUiHandlerGate) return _watchdogUiHandlerLease; }
        }

        internal void CommitWatchdogSafetyHandler(
            WinFormsWatchdogUiBindingReceipt receipt)
        {
            if (receipt == null || !receipt.Accepted || !receipt.Ready ||
                receipt.HandlerLease == null) return;
            WatchdogRuntime.RuntimeStopAllHandlerLease previous;
            lock (_watchdogUiHandlerGate)
            {
                previous = _watchdogUiHandlerLease;
                _watchdogUiHandlerLease = receipt.HandlerLease;
                _watchdogUiContext = receipt.Context;
                _watchdogUiIdentity = receipt.Context?.ValidatedAttachIdentity;
                _watchdogUiPipelineGeneration = receipt.PipelineGeneration;
            }
            // Swap is atomic under the gate; disposal is deliberately outside
            // it because a registration may complete asynchronously.
            if (previous != null && !ReferenceEquals(previous, receipt.HandlerLease))
            {
                try { previous.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Registers the monitor's typed safety handler after Main_Frm has
        /// bound its stable WinForms post target.  The binding completion and
        /// every identity are checked before the receipt becomes Ready.
        /// </summary>
        internal async Task<WinFormsWatchdogUiBindingReceipt>
            BindWatchdogSafetyHandlerAsync(
                RuntimeTransportSessionContext context,
                RuntimeSafetyTargetLease targetLease,
                RuntimeValidatedAttachIdentity expectedIdentity)
        {
            if (context == null || targetLease == null || expectedIdentity == null)
                return WinFormsWatchdogUiBindingReceipt.Rejected("UiBindingArgumentsMissing");
            if (_epb == null || _cfg == null)
                return WinFormsWatchdogUiBindingReceipt.Rejected("ControllerNotReady");
            var receipt = await WinFormsWatchdogUiBindingCore.ExecuteAsync(
                    context, expectedIdentity, targetLease.TargetId, targetLease.Target,
                    targetLease.TransferOwnership,
                    "FrmEpbMainMonitor.WatchdogSafety",
                    HandleWatchdogStopAllEnvelopeAsync,
                    MainWatchdogUiLifecycleAdapter.SelectExistingHandlerForContext(
                        WatchdogSafetyHandlerLease, context,
                        "FrmEpbMainMonitor.WatchdogSafety"), true)
                .ConfigureAwait(true);
            if (receipt != null && receipt.Accepted && receipt.Ready)
                CommitWatchdogSafetyHandler(receipt);
            return receipt;
        }

        internal void ReleaseWatchdogSafetyHandler()
        {
            WatchdogRuntime.RuntimeStopAllHandlerLease handler;
            lock (_watchdogUiHandlerGate)
            {
                handler = _watchdogUiHandlerLease;
                _watchdogUiHandlerLease = null;
                _watchdogUiContext = null;
                _watchdogUiIdentity = null;
                _watchdogUiPipelineGeneration = 0;
            }
            try { handler?.Dispose(); } catch { }
        }

        internal Task<WinFormsWatchdogUiBindingReceipt> BindWatchdogUiAfterAttachAsync()
        {
            var main = MdiParent as Main_Frm;
            return main == null
                ? Task.FromResult(WinFormsWatchdogUiBindingReceipt.Rejected("MainFormMissing"))
                : main.BindWatchdogUiProductionAsync(this);
        }

        private async Task HandleWatchdogStopAllEnvelopeAsync(
            WatchdogStopAllOfferEnvelope envelope)
        {
            await WinFormsWatchdogStopHandlerCore.HandleAsync(
                    this, envelope, _watchdogUiContext, _watchdogUiIdentity,
                    _watchdogUiPipelineGeneration, true)
                .ConfigureAwait(true);
        }

        WatchdogTakeoverHandoffReceipt
            IWinFormsWatchdogStopSafetyPort.PrepareWatchdogTakeoverHandoff(
                string sessionId,
                string correlationId)
        {
            var receipt = UnattendedRunCheckpointStore.PrepareWatchdogTakeoverHandoff(
                sessionId,
                correlationId);
            if (receipt.Prepared) PrepareForWatchdogRetryExit();
            return receipt;
        }

        Task<StopSafetyResult> IWinFormsWatchdogStopSafetyPort.PrepareForWatchdogTakeoverAsync(
            StopContext context)
        {
            // Watchdog 已拥有后续拉起；这里只执行真实安全边界，禁止再进入会
            // 清理恢复身份的同进程 FreshRestart 事务。
            return _epb.StopAllAsync(context, CancellationToken.None);
        }

        void IWinFormsWatchdogStopSafetyPort.NotifyStopCompleted(
            WatchdogStopSummary summary, string reason)
        {
            WatchdogRuntime.NotifyStopCompleted(summary, reason);
        }

        void IWinFormsWatchdogStopSafetyPort.RequestWatchdogOwnedExit(
            string reason,
            RuntimeShutdownIntent shutdownIntent)
        {
            (MdiParent as Main_Frm)?.RequestWatchdogOwnedExit(
                reason,
                shutdownIntent);
        }
    }
}
