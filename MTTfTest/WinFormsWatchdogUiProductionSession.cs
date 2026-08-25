using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;
using MtEmbTest;

namespace MTEmbTest
{
    /// <summary>
    /// A narrow production UI seam.  It owns the real WinForms post target
    /// and delegates target binding, handler registration, attachment,
    /// ingress, and cleanup to the same WatchdogRuntime production methods
    /// used by Main_Frm/FrmEpbMainMonitor.  It contains no lifecycle or
    /// identity algorithm of its own.
    /// </summary>
    internal sealed class WinFormsWatchdogUiProductionSession : IDisposable
    {
        private sealed class UiRetentionTransportPort : IRuntimeTransportShutdownPort
        {
            private readonly RuntimeTransportSessionContext _context;
            private readonly Func<ShutdownReceipt> _shutdown;

            internal UiRetentionTransportPort(
                RuntimeTransportSessionContext context,
                Func<ShutdownReceipt> shutdown = null)
            {
                _context = context ?? throw new ArgumentNullException(nameof(context));
                _shutdown = shutdown;
            }

            public ShutdownReceipt ShutdownWithReceipt()
            {
                if (_shutdown != null)
                    return _shutdown();
                // The UI seam has no sidecar to close.  This is the explicit
                // transport boundary used by the production retention
                // coordinator; the exact context lease is retained so the
                // coordinator still exercises its identity gate.
                return WatchdogRuntime.CreateUiBindingProductionTransportReceipt(
                    _context.SessionLease);
            }
        }

        private sealed class UiRetentionOwnershipPort : IRuntimeShutdownOwnershipPort
        {
            private readonly RuntimeTransportSessionContext _context;
            private int _detached;

            internal UiRetentionOwnershipPort(RuntimeTransportSessionContext context)
            {
                _context = context ?? throw new ArgumentNullException(nameof(context));
            }

            public RuntimeTransportSessionContext CaptureActive()
            {
                return Volatile.Read(ref _detached) == 0 ? _context : null;
            }

            public bool TryDetachActive(RuntimeTransportSessionContext context)
            {
                if (!ReferenceEquals(context, _context)) return false;
                return Interlocked.CompareExchange(ref _detached, 1, 0) == 0;
            }
        }

        private sealed class UiRetentionSessionPort : IRuntimeShutdownSessionPort
        {
            public RuntimeShutdownMarkOutcome TryMarkSessionClosing(
                RuntimeTransportSessionContext context)
            {
                // The isolated seam does not own the process-wide Engine
                // session.  NoEngineSession is an explicit, safe outcome;
                // the coordinator still runs the real transport/pipeline
                // close and exact retention phases.
                return RuntimeShutdownMarkOutcome.NoEngineSession;
            }
        }

        private sealed class UiRetentionPipelinePort : IRuntimeShutdownPipelinePort
        {
            private TimeSpan _budget = TimeSpan.FromSeconds(5);

            internal TimeSpan Budget
            {
                get { return _budget; }
                set { _budget = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value; }
            }

            public RuntimeShutdownReceipt Close(
                RuntimeTransportSessionContext context,
                ShutdownReceipt engineReceipt)
            {
                return WatchdogRuntime.CleanupCallbackPipelineResources(
                    context, _budget, string.Empty, engineReceipt);
            }
        }

        private sealed class UiRetentionJournalPort : IRuntimeShutdownJournalPort
        {
            public void Record(RuntimeTransportSessionContext context,
                string eventType, string detail)
            {
                // CreateUiBindingProductionContext deliberately has no
                // authority journal.  The no-op is therefore the explicit
                // journal boundary, not an inferred terminal shortcut.
            }

            public bool Flush(RuntimeTransportSessionContext context) { return true; }

            public bool Dispose(RuntimeTransportSessionContext context) { return true; }
        }

        private readonly RuntimeTransportSessionContext _context;
        private readonly RuntimeValidatedAttachIdentity _engineIdentity;
        private readonly WatchdogClientTransportEngine _engine;
        private readonly WinFormsWatchdogPostTarget _target;
        private readonly UiRetentionPipelinePort _retentionPipeline;
        private readonly RuntimeShutdownRetentionCoordinator _retentionCoordinator;
        private RuntimeSafetyTargetLease _targetLease;
        private WatchdogRuntime.RuntimeStopAllHandlerLease _handlerLease;
        private int _disposed;

        internal WinFormsWatchdogUiProductionSession(Control owner)
            : this(owner, null)
        {
        }

        internal WinFormsWatchdogUiProductionSession(
            Control owner,
            WatchdogClientTransportEngine engine)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            _engine = engine;
            var engineSnapshot = engine?.CaptureSnapshot();
            _context = engineSnapshot == null
                ? WatchdogRuntime.CreateUiBindingProductionContext()
                : WatchdogRuntime.CreateUiBindingProductionContext(engineSnapshot);
            _engineIdentity = engineSnapshot == null
                ? null
                : WatchdogRuntime.CreateUiBindingProductionIdentity(_context, engineSnapshot);
            _target = new WinFormsWatchdogPostTarget(owner);
            _retentionPipeline = new UiRetentionPipelinePort();
            _retentionCoordinator = new RuntimeShutdownRetentionCoordinator(
                new UiRetentionTransportPort(
                    _context,
                    engine == null ? null : new Func<ShutdownReceipt>(
                        () =>
                        {
                            try
                            {
                                WatchdogRuntime.SendApplicationClosingForShutdown(engine);
                            }
                            catch { }
                            return engine.ShutdownWithReceipt();
                        })),
                new UiRetentionOwnershipPort(_context),
                new UiRetentionSessionPort(),
                _retentionPipeline,
                new UiRetentionJournalPort());
        }

        internal RuntimeTransportSessionContext Context => _context;
        internal WinFormsWatchdogPostTarget Target => _target;
        internal RuntimeSafetyTargetLease TargetLease => _targetLease;
        internal WatchdogRuntime.RuntimeStopAllHandlerLease HandlerLease => _handlerLease;
        internal WatchdogStopAllScopeLease ScopeLease => Context.ScopeLease;
        internal RuntimeShutdownRetentionCoordinator RetentionCoordinator =>
            _retentionCoordinator;

        internal Task HandleStopEnvelopeAsync(
            WatchdogStopAllOfferEnvelope envelope,
            IWinFormsWatchdogStopSafetyPort port)
        {
            return WinFormsWatchdogStopHandlerCore.HandleAsync(
                port, envelope, Context, Context.ValidatedAttachIdentity,
                Context.PipelineGeneration, false);
        }

        internal WinFormsWatchdogUiCloseDecision CaptureCloseDecision()
        {
            var composite = new RuntimeTransportSnapshot(
                Context, null,
                Context.PipelineState == RuntimeCallbackPipelineState.Closing,
                Context, Context, true);
            var hasUiResources = _targetLease != null || !_target.IsDisposed;
            return WinFormsWatchdogUiCloseCoordinator.Evaluate(
                composite, _retentionCoordinator.Capture(), hasUiResources);
        }

        /// <summary>
        /// Simulates the transport's already validated Attached identity,
        /// without activating the callback pipeline.  The identity is still
        /// frozen by the production context gate; the UI bind transaction is
        /// then required to take it from Unbound to Ready.
        /// </summary>
        internal bool PublishValidatedAttachedBeforeBind()
        {
            var identity = WatchdogRuntime.CreateUiBindingProductionIdentity(
                Context, new string('a', 32));
            var accepted = Context.TryBindValidatedAttachIdentity(identity);
            // Exercise the same initial Activate attempt made by the
            // transport lifecycle: with no target/handler it must remain
            // Unbound.  The shared binding transaction below will then
            // self-advance it to Ready after registration.
            var activated = WatchdogRuntime.ActivateCallbackPipelineExact(Context, identity);
            return accepted && !activated;
        }

        /// <summary>
        /// Drives the complete production binding transaction used by
        /// Main_Frm: frozen exact identity, target bind, typed handler
        /// registration, BindingCompletion, and final Ready validation.
        /// </summary>
        internal async Task<WinFormsWatchdogUiBindingReceipt> BindReadyAsync(
            string targetId,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            var identity = Context.ValidatedAttachIdentity ?? _engineIdentity ??
                WatchdogRuntime.CreateUiBindingProductionIdentity(
                    Context, new string('c', 32));
            if (Context.ValidatedAttachIdentity == null &&
                !Context.TryBindValidatedAttachIdentity(identity))
                return WinFormsWatchdogUiBindingReceipt.Rejected(
                    "ValidatedAttachIdentityRejected");
            var receipt = await WinFormsWatchdogUiBindingCore.ExecuteAsync(
                    Context, identity, targetId, Target, false, subscriberId,
                    handler, null, false)
                .ConfigureAwait(true);
            if (receipt != null && receipt.Accepted)
            {
                _targetLease = receipt.TargetLease;
                _handlerLease = receipt.HandlerLease;
            }
            return receipt;
        }

        internal RuntimeSafetyTargetLease BindTarget(string targetId = "Main_Frm.WatchdogSafety")
        {
            _targetLease = WinFormsWatchdogUiBindingCore.BindTarget(
                Context, targetId, _target, false);
            return _targetLease;
        }

        internal WatchdogRuntime.RuntimeStopAllHandlerLease RegisterHandler(
            Func<WatchdogStopAllOfferEnvelope, Task> handler,
            string subscriberId = "FrmEpbMainMonitor.WatchdogSafety")
        {
            _handlerLease = WinFormsWatchdogUiBindingCore.RegisterHandler(
                Context, _targetLease, subscriberId, handler);
            return _handlerLease;
        }

        internal bool Attach()
        {
            var identity = Context.ValidatedAttachIdentity ??
                WatchdogRuntime.CreateUiBindingProductionIdentity(
                    Context, new string('b', 32));
            return WatchdogRuntime.ActivateCallbackPipelineExact(Context, identity);
        }

        internal bool PublishPipe(string reason, string correlation, string requestId = null)
        {
            return WatchdogRuntime.PublishStopAllIngressForContext(
                Context, reason, correlation, requestId ?? correlation,
                WatchdogStopAllSourceFlags.Pipe, "UiProductionPipeStopAll");
        }

        internal bool PublishDurable(string reason, string correlation, string requestId = null)
        {
            return WatchdogRuntime.PublishStopAllIngressForContext(
                Context, reason, correlation, requestId ?? correlation,
                WatchdogStopAllSourceFlags.Durable, "UiProductionDurableStopAll");
        }

        internal RuntimeCallbackPipelineSnapshot Capture() =>
            new RuntimeCallbackPipelineSnapshot(Context);

        internal RuntimeShutdownReceipt Close(TimeSpan? budget = null)
        {
            _retentionPipeline.Budget = budget ?? TimeSpan.FromSeconds(5);
            // All UI-seam closes use the same production process-retention
            // coordinator as Main_Frm.  In particular, a first nonterminal
            // close retains the owner and a retry advances that same owner;
            // it never constructs a fresh Engine or pipeline transaction.
            return _retentionCoordinator.ShutdownOrRetry();
        }

        internal RuntimeShutdownReceipt Abandon(string reason)
        {
            _retentionPipeline.Budget = TimeSpan.FromSeconds(5);
            var receipt = _retentionCoordinator.ShutdownOrRetry();
            return receipt?.AsTestAbandoned();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _handlerLease?.Dispose(); } catch { }
            try { Abandon("FixtureDispose"); } catch { }
            try { _target.Dispose(); } catch { }
        }
    }
}
