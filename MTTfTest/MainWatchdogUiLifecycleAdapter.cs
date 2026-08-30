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
    /// <summary>
    /// The single Main-owned watchdog UI lifecycle.  It owns the resource
    /// tuple (context/target/target lease/handler lease), the close attempt,
    /// and the terminal release gate.  Main_Frm is only a thin WinForms
    /// facade over this adapter; no second UI ownership state is allowed.
    /// </summary>
    internal sealed class MainWatchdogUiLifecycleAdapter : IDisposable
    {
        private readonly Main_Frm _owner;
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _bindGate = new SemaphoreSlim(1, 1);
        private readonly WinFormsWatchdogUiResourceOwner _resourceOwner =
            new WinFormsWatchdogUiResourceOwner();

        private WinFormsWatchdogPostTarget _postTarget;
        private RuntimeSafetyTargetLease _targetLease;
        private RuntimeTransportSessionContext _context;
        private WatchdogRuntime.RuntimeStopAllHandlerLease _handlerLease;
        private Task<RuntimeShutdownReceipt> _closeTask;
        private int _readyNotified;
        private int _disposed;

        internal MainWatchdogUiLifecycleAdapter(Main_Frm owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        internal WinFormsWatchdogPostTarget PostTarget
        {
            get { lock (_gate) return _postTarget; }
        }

        internal RuntimeSafetyTargetLease TargetLease
        {
            get { lock (_gate) return _targetLease; }
        }

        internal bool HasResources
        {
            get
            {
                lock (_gate)
                    return _context != null || _targetLease != null ||
                           _postTarget != null || _handlerLease != null ||
                           _resourceOwner.HasResources;
            }
        }

        internal bool UiReadyNotified => Volatile.Read(ref _readyNotified) != 0;

        internal int ResourceReleaseCount => _resourceOwner.ReleaseCount;

        internal async Task<WinFormsWatchdogUiBindingReceipt> BindExactAsync(
            FrmEpbMainMonitor monitor)
        {
            if (monitor == null)
                return WinFormsWatchdogUiBindingReceipt.Rejected("MonitorMissing");
            return await BindExactAsync(
                    "Main_Frm.WatchdogSafety",
                    "FrmEpbMainMonitor.WatchdogSafety",
                    monitor.WatchdogSafetyHandler,
                    monitor)
                .ConfigureAwait(true);
        }

        internal Task<WinFormsWatchdogUiBindingReceipt> BindExactAsync(
            string targetId,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            return BindExactAsync(targetId, subscriberId, handler, null);
        }

        private async Task<WinFormsWatchdogUiBindingReceipt> BindExactAsync(
            string targetId,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler,
            FrmEpbMainMonitor monitor)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return WinFormsWatchdogUiBindingReceipt.Rejected("UiAdapterDisposed");

            await _bindGate.WaitAsync().ConfigureAwait(true);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return WinFormsWatchdogUiBindingReceipt.Rejected("UiAdapterDisposed");
                if (_owner.IsDisposed || _owner.Disposing || !_owner.IsHandleCreated)
                    return WinFormsWatchdogUiBindingReceipt.Rejected("MainControlUnavailable");

                var composite = WatchdogRuntime.CaptureTransportSnapshot();
                if (!WatchdogRuntime.IsExactAttachedSnapshot(composite))
                    return WinFormsWatchdogUiBindingReceipt.Rejected("ExactAttachedRequired");
                var context = composite.Context;
                var identity = context?.ValidatedAttachIdentity;
                if (context == null || identity == null)
                    return WinFormsWatchdogUiBindingReceipt.Rejected("ValidatedIdentityMissing");

                WinFormsWatchdogPostTarget target;
                bool targetCreatedForAttempt = false;
                lock (_gate)
                {
                    if (_context != null && !ReferenceEquals(_context, context))
                        return WinFormsWatchdogUiBindingReceipt.Rejected(
                            "PreviousUiBindingActive");
                    if (_postTarget == null || _postTarget.IsDisposed)
                    {
                        target = new WinFormsWatchdogPostTarget(_owner);
                        targetCreatedForAttempt = true;
                    }
                    else
                    {
                        target = _postTarget;
                    }
                }

                var targetWasAlreadyBound = context.SafetyTargetLease != null &&
                    context.SafetyTargetLease.IsActive;

                // A monitor can retain a lease from a previous context or a
                // completed registration.  Passing that lease into the
                // transaction would let the transaction make a decision on
                // stale ownership.  Only an active lease for this exact
                // context and subscriber is eligible for reuse; all other
                // cases deliberately take the normal registration path.
                var existingHandler = SelectExistingHandlerForContext(
                    monitor?.WatchdogSafetyHandlerLease, context,
                    "FrmEpbMainMonitor.WatchdogSafety");

                var handlerReceipt = await WinFormsWatchdogUiBindingCore.ExecuteAsync(
                        context, identity, targetId, target, false, subscriberId,
                        handler, existingHandler, true)
                    .ConfigureAwait(true);
                if (handlerReceipt == null || !handlerReceipt.Accepted ||
                    !handlerReceipt.Ready)
                {
                    if (handlerReceipt?.HandlerLease != null)
                        handlerReceipt.HandlerLease.Dispose();
                    if (targetCreatedForAttempt)
                        target.Dispose();
                    return handlerReceipt ??
                           WinFormsWatchdogUiBindingReceipt.Rejected(
                               "HandlerBindRejected");
                }

                // Dispose may race the BindingCompletion continuation.  Do
                // not publish a half tuple after the await; release only the
                // leases/target created by this attempt, outside the gate.
                bool disposedAfterAwait;
                lock (_gate) disposedAfterAwait = Volatile.Read(ref _disposed) != 0;
                if (disposedAfterAwait)
                {
                    handlerReceipt.HandlerLease.Dispose();
                    if (!targetWasAlreadyBound)
                        handlerReceipt.TargetLease.ReleaseUnpublished();
                    if (targetCreatedForAttempt)
                        target.Dispose();
                    return WinFormsWatchdogUiBindingReceipt.Rejected(
                        "UiAdapterDisposed");
                }

                monitor?.CommitWatchdogSafetyHandler(handlerReceipt);

                var after = WatchdogRuntime.CaptureTransportSnapshot();
                if (!WatchdogRuntime.IsExactAttachedSnapshot(after) ||
                    !ReferenceEquals(after.Context, context) ||
                    context.PipelineState != RuntimeCallbackPipelineState.Ready ||
                    context.PipelineGeneration != handlerReceipt.PipelineGeneration ||
                    context.ValidatedAttachIdentity == null ||
                    context.ValidatedAttachIdentity.ExactKey != identity.ExactKey)
                {
                    monitor?.ReleaseWatchdogSafetyHandler();
                    handlerReceipt.HandlerLease.Dispose();
                    if (!targetWasAlreadyBound)
                        handlerReceipt.TargetLease.ReleaseUnpublished();
                    if (targetCreatedForAttempt)
                        target.Dispose();
                    return WinFormsWatchdogUiBindingReceipt.Rejected(
                        "UiBindingIdentityChanged");
                }

                if (!Publish(context, target, handlerReceipt.TargetLease,
                        handlerReceipt.HandlerLease))
                {
                    monitor?.ReleaseWatchdogSafetyHandler();
                    handlerReceipt.HandlerLease.Dispose();
                    if (!targetWasAlreadyBound)
                        handlerReceipt.TargetLease.ReleaseUnpublished();
                    if (targetCreatedForAttempt)
                        target.Dispose();
                    return WinFormsWatchdogUiBindingReceipt.Rejected(
                        "UiResourceOwnerConflict");
                }
                NotifyMainUiReadyOnce("MainUiWatchdogPipelineReady");
                return WinFormsWatchdogUiBindingReceipt.AcceptedReady(
                    context, handlerReceipt.TargetLease, handlerReceipt.HandlerLease,
                    handlerReceipt.PipelineGeneration, identity);
            }
            finally
            {
                _bindGate.Release();
            }
        }

        /// <summary>
        /// Selects a handler lease only when it is still an active lease for
        /// the exact runtime context and subscriber.  This helper is used by
        /// both Main's binding transaction and the monitor's direct binding
        /// entry, so a stale monitor-style lease cannot be smuggled into a
        /// new session as an existing handler.
        /// </summary>
        internal static WatchdogRuntime.RuntimeStopAllHandlerLease
            SelectExistingHandlerForContext(
                WatchdogRuntime.RuntimeStopAllHandlerLease existingHandler,
                RuntimeTransportSessionContext context, string subscriberId)
        {
            if (existingHandler == null || !existingHandler.IsActive ||
                context == null ||
                !ReferenceEquals(existingHandler.Context, context) ||
                !string.Equals(existingHandler.SubscriberId, subscriberId,
                    StringComparison.Ordinal))
                return null;
            return existingHandler;
        }

        private bool Publish(
            RuntimeTransportSessionContext context,
            WinFormsWatchdogPostTarget target,
            RuntimeSafetyTargetLease targetLease,
            WatchdogRuntime.RuntimeStopAllHandlerLease handlerLease)
        {
            if (context == null || target == null || targetLease == null ||
                handlerLease == null)
                return false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;
                if (_context != null && !ReferenceEquals(_context, context))
                    return false;
                if (_targetLease != null && !ReferenceEquals(_targetLease, targetLease))
                    return false;
                if (!_resourceOwner.Publish(context, targetLease, target))
                    return false;
                _context = context;
                _postTarget = target;
                _targetLease = targetLease;
                _handlerLease = handlerLease;
            }
            return true;
        }

        internal bool PublishForProduction(
            RuntimeTransportSessionContext context,
            WinFormsWatchdogPostTarget target,
            RuntimeSafetyTargetLease targetLease)
        {
            if (context == null || target == null || targetLease == null)
                return false;
            // This seam uses the same atomic adapter publication as the real
            // binding path; it never publishes the resource owner first.
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                if (_context != null && !ReferenceEquals(_context, context)) return false;
                if (_targetLease != null && !ReferenceEquals(_targetLease, targetLease))
                    return false;
                if (!_resourceOwner.Publish(context, targetLease, target))
                    return false;
                _context = context;
                _postTarget = target;
                _targetLease = targetLease;
                return true;
            }
        }

        internal void NotifyMainUiReadyOnce(string reason)
        {
            if (Interlocked.Exchange(ref _readyNotified, 1) != 0) return;
            WatchdogRuntime.NotifyMainUiReady(reason ?? "MainUiWatchdogPipelineReady");
        }

        internal async Task<RuntimeShutdownReceipt> ShutdownAndReleaseAsync(
            string reason,
            RuntimeShutdownIntent shutdownIntent = RuntimeShutdownIntent.SessionClose)
        {
            RuntimeShutdownReceipt receipt;
            var deadlineUtc = DateTime.UtcNow.AddSeconds(15);
            do
            {
                Task<RuntimeShutdownReceipt> shutdown;
                lock (_gate) shutdown = EnsureShutdownTaskLocked(shutdownIntent);
                try
                {
                    receipt = await shutdown.ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    ProjectLogHub.Write(ProjectLogLevel.Error,
                        "Watchdog主窗体收口异常（" + (reason ?? "unknown") + "）：" +
                        ex.GetBaseException().Message, "独立看门狗", ex);
                    return null;
                }

                if (receipt != null && receipt.Disposition == RuntimeShutdownDisposition.Terminal)
                {
                    ReleaseAfterTerminal(receipt);
                    return receipt;
                }
                // Exact identity mismatch is deliberately sticky inside the
                // retention coordinator.  Replaying the same receipt for the
                // remainder of the 15-second retry window cannot make progress
                // and only makes repeated close clicks look unresponsive.
                if (receipt?.IsStickyBlockingFailure == true)
                {
                    LogBlockedClose(reason, receipt, sticky: true);
                    return receipt;
                }
                // Journal/archive cleanup is non-safety work only after the
                // receipt proves SafeExitAllowed.  Retry it for a bounded
                // window so ordinary transient I/O still reaches Terminal;
                // only then detach the retained cleanup from the UI process.
                if (receipt != null &&
                    receipt.Disposition == RuntimeShutdownDisposition.DetachedRetained &&
                    DateTime.UtcNow >= deadlineUtc)
                {
                    ReleaseAfterTerminal(receipt);
                    return receipt;
                }
                if (DateTime.UtcNow < deadlineUtc)
                    await Task.Delay(1000).ConfigureAwait(true);
            }
            while (DateTime.UtcNow < deadlineUtc);

            if (receipt == null || !receipt.IsCloseAuthorized)
                LogBlockedClose(reason, receipt, sticky: false);
            return receipt;
        }

        private void LogBlockedClose(
            string reason,
            RuntimeShutdownReceipt receipt,
            bool sticky)
        {
            RuntimeTransportSessionContext context;
            lock (_gate) context = _context;
            var identity = WatchdogRuntime.DescribeSessionCloseIdentity(context);
            ProjectLogHub.Write(
                ProjectLogLevel.Warning,
                "Watchdog会话尚未达到终态（" + (reason ?? "unknown") +
                "），关闭被安全阻止。Disposition=" +
                (receipt?.Disposition.ToString() ?? "NoReceipt") +
                "; Reason=" + (receipt?.TerminalReason ?? "unknown") +
                "; Sticky=" + sticky +
                "; PipelineTerminal=" + (receipt?.PipelineTerminal ?? false) +
                "; JournalFlush=" + (receipt?.JournalFlushCompleted ?? false) +
                "; JournalDisposed=" + (receipt?.JournalDisposed ?? false) +
                "; " + identity,
                "独立看门狗");
        }

        private Task<RuntimeShutdownReceipt> EnsureShutdownTaskLocked(
            RuntimeShutdownIntent shutdownIntent)
        {
            if (_closeTask != null && !_closeTask.IsCompleted) return _closeTask;
            _closeTask = Task.Run(() =>
            {
                try
                {
                    return WatchdogRuntime.ShutdownRuntimeWithReceipt(
                        shutdownIntent);
                }
                catch (Exception ex)
                {
                    ProjectLogHub.Write(ProjectLogLevel.Error,
                        "Watchdog主窗体退出收口异常：" + ex.GetBaseException().Message,
                        "独立看门狗", ex);
                    return null;
                }
            });
            return _closeTask;
        }

        internal bool ReleaseAfterTerminal(RuntimeShutdownReceipt receipt)
        {
            WatchdogRuntime.RuntimeStopAllHandlerLease handler;
            lock (_gate)
            {
                if (!_resourceOwner.ReleaseAfterTerminal(receipt)) return false;
                handler = _handlerLease;
                _handlerLease = null;
                _targetLease = null;
                _postTarget = null;
                _context = null;
                Interlocked.Exchange(ref _readyNotified, 0);
            }
            try { handler?.Dispose(); } catch { }
            return true;
        }

        internal bool IsExactResourceContext(RuntimeTransportSessionContext context)
        {
            lock (_gate) return ReferenceEquals(_context, context);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Disposal only closes admission.  Active handler/target leases
            // remain owned until a matching terminal receipt arrives; the
            // BindingCompletion continuation can therefore safely re-check
            // _disposed and clean up its own unpublished attempt.
        }
    }
}
