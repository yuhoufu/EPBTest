using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    /// <summary>
    /// Deterministic production seam for the callback pipeline.  The seam
    /// owns an isolated RuntimeTransportSessionContext and delegates every
    /// operation to the same bind/register/advance/publish/close methods used
    /// by WatchdogRuntime.  It deliberately never installs a process-global
    /// active context and contains no transport or lifecycle algorithm.
    /// </summary>
    internal sealed class RuntimeCallbackPipelineTestSession : IDisposable
    {
        private readonly RuntimeTransportSessionContext _context;
        private readonly object _closeGate = new object();
        private RuntimeShutdownReceipt _closeReceipt;
        private int _disposed;

        internal RuntimeCallbackPipelineTestSession(
            int[] selectedChannels = null,
            RuntimeJournalMode journalMode = RuntimeJournalMode.CreateNewInitial)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var state = new RuntimeTransportSessionState(() => new WatchdogHeartbeat());
            _context = new RuntimeTransportSessionContext(
                sessionId,
                "test-pipeline-" + sessionId,
                string.Empty,
                string.Empty,
                string.Empty,
                new WatchdogJournalPolicy(),
                selectedChannels ?? new[] { 4, 10 },
                false,
                0,
                1,
                0,
                string.Empty,
                string.Empty,
                journalMode,
                null,
                state,
                null,
                new RuntimeCallbackIngressGate());
            _context.BindSessionLease(1);
        }

        internal RuntimeTransportSessionContext Context => _context;
        internal long PipelineGeneration => _context.PipelineGeneration;

        internal RuntimeSafetyTargetLease BindTarget(
            string targetId, IWatchdogCallbackPostTarget target)
        {
            return WatchdogRuntime.BindStopAllSafetyTarget(_context, targetId, target, false);
        }

        internal WatchdogRuntime.RuntimeStopAllHandlerLease RegisterHandler(
            RuntimeSafetyTargetLease targetLease, string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            return WatchdogRuntime.RegisterStopAllHandler(
                _context, targetLease, subscriberId, handler);
        }

        internal bool Attach()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var identity = new RuntimeValidatedAttachIdentity(
                    _context.SessionId,
                    _context.SessionGeneration,
                    _context.SessionLease,
                    1,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    new string('b', 32));
                return WatchdogRuntime.ActivateCallbackPipelineExact(_context, identity);
            }
        }

        internal Task<bool> AttachAsync() => Task.Run(Attach);

        internal void SetAfterActivateAndTakeBeforeReadyProbe(
            Action<RuntimeTransportSessionContext> probe)
        {
            _context.AfterActivateAndTakeBeforeReadyProbe = probe;
        }

        internal bool PublishPipe(string reason, string correlation, string requestId = null,
            string eventType = "TestPipeStopAll")
        {
            return WatchdogRuntime.PublishStopAllIngressForContext(
                _context, reason, correlation, requestId ?? correlation,
                WatchdogStopAllSourceFlags.Pipe, eventType);
        }

        internal bool PublishDurable(string reason, string correlation, string requestId = null,
            string eventType = "TestDurableStopAll")
        {
            return WatchdogRuntime.PublishStopAllIngressForContext(
                _context, reason, correlation, requestId ?? correlation,
                WatchdogStopAllSourceFlags.Durable, eventType);
        }

        internal void Advance() => WatchdogRuntime.TryAdvancePipelineForContext(_context);

        internal RuntimeCallbackPipelineSnapshot Capture() =>
            new RuntimeCallbackPipelineSnapshot(_context);

        internal WatchdogStopAllCoordinatorSnapshot CaptureCoordinator()
        {
            var scope = _context.ScopeLease;
            var coordinator = _context.StopAllCoordinator;
            return scope == null || coordinator == null ? null : coordinator.Capture(scope);
        }

        internal RuntimeIngressAdmissionReservation ReserveAdmission(
            WatchdogStopAllSourceFlags source)
        {
            return _context.IngressGate.TryReserveAdmission(source);
        }

        internal RuntimeIngressAdmissionReservation[] ReserveBothAdmissions()
        {
            return new[]
            {
                ReserveAdmission(WatchdogStopAllSourceFlags.Pipe),
                ReserveAdmission(WatchdogStopAllSourceFlags.Durable)
            };
        }

        internal RuntimeIngressAdmissionReservation AliasAdmission(
            RuntimeIngressAdmissionReservation reservation)
        {
            if (reservation == null) return null;
            return new RuntimeIngressAdmissionReservation(
                _context.IngressGate, reservation.Source, reservation.Request,
                reservation.ReservationId, reservation.DrainToken);
        }

        internal WatchdogStopAllCloseReceipt BeginCoordinatorClose()
        {
            var scope = _context.ScopeLease;
            var coordinator = _context.StopAllCoordinator;
            return scope == null || coordinator == null
                ? null : coordinator.BeginClose(scope);
        }

        internal RuntimeShutdownReceipt Abandon(string reason,
            TimeSpan? budget = null)
        {
            lock (_closeGate)
            {
                // A fixture abandon is an explicit test cleanup operation,
                // never a production terminal outcome.  If normal Close
                // already cached a receipt, return a non-terminal view rather
                // than reusing that terminal evidence as the abandon result.
                if (_closeReceipt != null)
                {
                    if (_closeReceipt.TestAbandoned || _closeReceipt.IsTerminal)
                        return _closeReceipt.AsTestAbandoned();
                    // A production FailClosed receipt intentionally retains
                    // its evidence.  Fixture abandonment may now use the
                    // same shared cleanup transaction to release test-owned
                    // workers, while keeping the returned receipt explicitly
                    // non-terminal/non-evidence-resolved.
                    _closeReceipt = WatchdogRuntime.CleanupCallbackPipelineResources(
                        _context, budget ?? TimeSpan.FromSeconds(5), reason ?? "FixtureAbandon",
                        null, true);
                    return _closeReceipt;
                }
                _closeReceipt = WatchdogRuntime.CleanupCallbackPipelineResources(
                    _context, budget ?? TimeSpan.FromSeconds(5), reason ?? "FixtureAbandon",
                    null, true);
                return _closeReceipt;
            }
        }

        internal RuntimeShutdownReceipt Close(ShutdownReceipt engineReceipt = null)
        {
            lock (_closeGate)
            {
                if (_closeReceipt != null) return _closeReceipt;
                if (engineReceipt == null) engineReceipt = CreateSuccessfulEngineReceipt();
                var pipelineReceipt = WatchdogRuntime.CloseCallbackPipelineForContext(_context, engineReceipt);
                // This deterministic seam has no journal object.  Mark that
                // boundary explicitly instead of teaching the production
                // RuntimeReceipt constructor to infer completion from null
                // receipts.
                _closeReceipt = _context.Journal == null && pipelineReceipt != null &&
                    pipelineReceipt.PipelineTerminal
                    ? pipelineReceipt.WithRetention(
                        pipelineReceipt.RetentionVersion, pipelineReceipt.Retained,
                        true, true, pipelineReceipt.PreviousRuntimeShutdownIncomplete)
                    : pipelineReceipt;
                return _closeReceipt;
            }
        }

        private ShutdownReceipt CreateSuccessfulEngineReceipt()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var termination = new WorkerTerminationState(
                    _context.SessionLease,
                    DateTime.UtcNow.Ticks,
                    true, true, true, true, true, true, true, true, true, true,
                    0,
                    0,
                    true);
                return new ShutdownReceipt(
                    _context.SessionLease,
                    DateTime.UtcNow.Ticks,
                    termination,
                    true,
                    true);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Abandon("FixtureDispose"); } catch { }
        }
    }
}
