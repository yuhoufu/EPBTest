using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using DataOperation;
using MTEmbTest;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    internal sealed class WinFormsWatchdogUiBindingReceipt
    {
        internal bool Accepted { get; }
        internal bool Ready { get; }
        internal string Reason { get; }
        internal RuntimeTransportSessionContext Context { get; }
        internal RuntimeSafetyTargetLease TargetLease { get; }
        internal WatchdogRuntime.RuntimeStopAllHandlerLease HandlerLease { get; }
        internal long PipelineGeneration { get; }
        internal string SessionId { get; }
        internal long SessionGeneration { get; }
        internal long SessionLease { get; }
        internal long AuthorityGeneration { get; }
        internal long AttachedConnectionGeneration { get; }
        internal long AttachEpoch { get; }
        internal int AuthorityProcessId { get; }
        internal long AuthorityProcessStartUtcTicks { get; }
        internal string AuthorityInstanceNonceHash { get; }
        internal string ExactKey { get; }

        private WinFormsWatchdogUiBindingReceipt(
            bool accepted,
            bool ready,
            string reason,
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease,
            long pipelineGeneration,
            RuntimeValidatedAttachIdentity identity)
        {
            Accepted = accepted;
            Ready = ready;
            Reason = reason ?? string.Empty;
            Context = context;
            TargetLease = targetLease;
            HandlerLease = handlerLease;
            PipelineGeneration = pipelineGeneration;
            SessionId = identity?.SessionId ?? context?.SessionId ?? string.Empty;
            SessionGeneration = identity?.SessionGeneration ?? context?.SessionGeneration ?? 0;
            SessionLease = identity?.SessionLease ?? context?.SessionLease ?? 0;
            AuthorityGeneration = identity?.AuthorityGeneration ?? 0;
            AttachedConnectionGeneration = identity?.AttachedConnectionGeneration ?? 0;
            AttachEpoch = identity?.AttachEpoch ?? 0;
            AuthorityProcessId = identity?.AuthorityProcessId ?? 0;
            AuthorityProcessStartUtcTicks = identity?.AuthorityProcessStartUtcTicks ?? 0;
            AuthorityInstanceNonceHash = identity?.AuthorityInstanceNonceHash ?? string.Empty;
            ExactKey = identity?.ExactKey ?? string.Empty;
        }

        internal static WinFormsWatchdogUiBindingReceipt Rejected(string reason)
        {
            return new WinFormsWatchdogUiBindingReceipt(
                false, false, reason, null, null, null, 0, null);
        }

        internal static WinFormsWatchdogUiBindingReceipt AcceptedReady(
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease,
            long pipelineGeneration,
            RuntimeValidatedAttachIdentity identity)
        {
            return new WinFormsWatchdogUiBindingReceipt(
                true, true, string.Empty, context, targetLease, handlerLease,
                pipelineGeneration, identity);
        }
    }

    public partial class Main_Frm
    {
        // All watchdog UI ownership, binding, retention, and terminal release
        // state lives in this one production adapter.  The form only provides
        // the stable WinForms owner and delegates to it.
        private readonly MainWatchdogUiLifecycleAdapter _watchdogUiAdapter;
        private int _watchdogOwnedExitRequested;
        private int _watchdogAllowClose;

        internal WinFormsWatchdogPostTarget WatchdogPostTarget =>
            _watchdogUiAdapter?.PostTarget;

        internal RuntimeSafetyTargetLease WatchdogTargetLease =>
            _watchdogUiAdapter?.TargetLease;

        internal bool WatchdogUiReadyNotified =>
            _watchdogUiAdapter?.UiReadyNotified == true;

        internal int WatchdogUiResourceReleaseCount =>
            _watchdogUiAdapter?.ResourceReleaseCount ?? 0;

        internal bool WatchdogUiHasResources =>
            _watchdogUiAdapter?.HasResources == true;

        internal Task<WinFormsWatchdogUiBindingReceipt>
            BindWatchdogUiProductionAsync(FrmEpbMainMonitor monitor)
        {
            return _watchdogUiAdapter.BindExactAsync(monitor);
        }

        internal Task<WinFormsWatchdogUiBindingReceipt>
            BindWatchdogUiProductionAsync(
                string targetId,
                string subscriberId,
                Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            return _watchdogUiAdapter.BindExactAsync(targetId, subscriberId, handler);
        }

        internal void NotifyMainUiReadyOnce(string reason)
        {
            _watchdogUiAdapter.NotifyMainUiReadyOnce(reason);
        }

        internal bool HandleWatchdogMainFormClosing(FormClosingEventArgs e)
        {
            if (Volatile.Read(ref _watchdogAllowClose) != 0) return false;
            var composite = WatchdogRuntime.CaptureTransportSnapshot();
            var retained = WatchdogRuntime.CaptureRetainedShutdown();
            var decision = WinFormsWatchdogUiCloseCoordinator.Evaluate(
                composite, retained, _watchdogUiAdapter.HasResources);
            if (!decision.HasActiveEvidence) return false;
            e.Cancel = true;
            RequestWatchdogOwnedExit("MainFormClosing:" + decision.Reason);
            return true;
        }

        internal void RequestWatchdogOwnedExit(string reason)
        {
            Interlocked.Exchange(ref _watchdogOwnedExitRequested, 1);
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => RequestWatchdogOwnedExit(reason))); }
                catch { }
                return;
            }
            BeginWatchdogClose(reason ?? "WatchdogOwnedExit");
        }

        internal Task<RuntimeShutdownReceipt> ShutdownWatchdogSessionAndReleaseUiAsync(
            string reason)
        {
            return _watchdogUiAdapter.ShutdownAndReleaseAsync(reason);
        }

        private void BeginWatchdogClose(string reason)
        {
            _ = ShutdownWatchdogSessionAndReleaseUiAsync(reason).ContinueWith(task =>
            {
                var receipt = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                if (receipt == null || !receipt.IsTerminal)
                {
                    Interlocked.Exchange(ref _watchdogOwnedExitRequested, 0);
                    ProjectLogHub.Write(ProjectLogLevel.Warning,
                        "Watchdog主窗体退出保留资源未完成；窗口继续保持可见，等待下一次安全收口。",
                        "独立看门狗");
                    return;
                }
                if (IsDisposed || Disposing) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        Interlocked.Exchange(ref _watchdogAllowClose, 1);
                        Close();
                    }));
                }
                catch { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        internal bool ReleaseWatchdogUiResources(RuntimeShutdownReceipt receipt)
        {
            return _watchdogUiAdapter.ReleaseAfterTerminal(receipt);
        }

        internal bool PublishWatchdogUiResourcesForProduction(
            RuntimeTransportSessionContext context,
            WinFormsWatchdogPostTarget target,
            RuntimeSafetyTargetLease targetLease)
        {
            return _watchdogUiAdapter.PublishForProduction(context, target, targetLease);
        }

    }
}
