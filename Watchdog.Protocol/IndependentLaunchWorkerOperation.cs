using System;
using System.IO;
using System.Security.Principal;

namespace MTTFTest.Watchdog.Protocol
{
    public static class IndependentLaunchObservation
    {
        public static IndependentOperationResult Evaluate(IndependentProjectState state,
            string requestId, long generation, string nonce, long now)
        {
            if (state == null) return IndependentOperationResult.Failed;
            state.Validate();
            var tx = state.Transaction;
            var ticket = state.Ticket;
            if (tx == null || ticket == null || tx.RequestId != requestId || tx.Generation != generation ||
                ticket.Nonce != nonce || ticket.Revoked || state.Maintenance || state.SafetyCleanupPending ||
                state.Intent == null || state.Intent.RecoveryChannels().Length == 0 ||
                tx.RunId != state.Intent.RunId || tx.RunEpoch != state.Intent.RunEpoch ||
                tx.IntentRevision != state.Intent.Revision || now < tx.LastAttemptUtcTicks || now >= tx.PhaseDeadlineUtcTicks ||
                (tx.Phase != IndependentRecoveryPhase.LaunchPending && tx.Phase != IndependentRecoveryPhase.Verifying))
                return IndependentOperationResult.Failed;
            if (ticket.Consumer == null) return now >= ticket.ExpiresUtcTicks ? IndependentOperationResult.Failed : IndependentOperationResult.Pending;
            return ticket.DispatchStartedUtcTicks > 0 && ticket.Consumer.Pid == tx.ReplacementPid &&
                ticket.Consumer.StartUtcTicks == tx.ReplacementStartUtcTicks &&
                ticket.Consumer.WindowsSessionId == ticket.WindowsSessionId &&
                string.Equals(ticket.Consumer.ExecutablePath, state.Intent.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? IndependentOperationResult.Completed : IndependentOperationResult.Failed;
        }
    }

    public sealed class IndependentLaunchWorkerOperation : IDisposable
    {
        private readonly IndependentProjectStateStore _store;
        private readonly IndependentExecutorRegistration _registration;
        private readonly string _requestId, _nonce;
        private readonly long _generation;
        private readonly object _gate = new object();
        private IndependentBoundedWorker _worker;
        private bool _disposed;
        public string Detail { get; private set; }

        public IndependentLaunchWorkerOperation(string registrationPath, string nonce)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("IndependentLaunchOperationRequiresSystem");
            _registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            _store = new IndependentProjectStateStore(_registration.StateDirectory);
            var state = _store.Read();
            _registration.RequireBoundIntent(state?.Intent);
            _requestId = state.Transaction?.RequestId;
            _generation = state.Transaction?.Generation ?? 0;
            _nonce = nonce;
            var now = DateTime.UtcNow.Ticks;
            if (IndependentLaunchObservation.Evaluate(state, _requestId, _generation, nonce, now) == IndependentOperationResult.Failed ||
                state.Transaction.ExecutorIdentity != "IndependentExecutor:" + _registration.InstallationId)
                throw new InvalidOperationException("IndependentLaunchOperationNotAuthorized");
            if (state.Ticket.DispatchStartedUtcTicks != 0 || state.Ticket.Consumer != null)
            {
                Detail = "ResumingDispatchObservationWithoutRelaunch";
                return;
            }
            var deadline = (int)Math.Min(10000, Math.Max(1,
                TimeSpan.FromTicks(Math.Min(state.Ticket.ExpiresUtcTicks, state.Transaction.PhaseDeadlineUtcTicks) - now).TotalMilliseconds));
            _worker = new IndependentBoundedWorker(_registration.SafetyExecutablePath,
                "--independent-launch \"" + Path.GetFullPath(registrationPath) + "\" " + nonce,
                Path.GetDirectoryName(_registration.SafetyExecutablePath), deadline);
            Detail = "BoundedInteractiveDispatchWorkerStarted";
        }

        public IndependentOperationResult Poll(long now)
        {
            lock (_gate) return PollCore(now);
        }

        private IndependentOperationResult PollCore(long now)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentLaunchWorkerOperation));
            try
            {
                var state = _store.Read();
                _registration.RequireBoundIntent(state?.Intent);
                var observation = IndependentLaunchObservation.Evaluate(state, _requestId, _generation, _nonce, now);
                if (observation == IndependentOperationResult.Failed)
                {
                    CancelWorker(); Detail = "LaunchAuthorityRevokedOrDeadlineExceeded";
                    return observation;
                }
                if (_worker != null)
                {
                    var workerState = _worker.Poll();
                    if (workerState == IndependentWorkerState.Running) return IndependentOperationResult.Pending;
                    Detail = "DispatchWorkerExited:" + workerState + ":" + _worker.ExitCode;
                    CancelWorker();
                    // The worker may have committed dispatch after the first
                    // read. Do not classify its earlier snapshot as failure.
                    state = _store.Read();
                    _registration.RequireBoundIntent(state?.Intent);
                    observation = IndependentLaunchObservation.Evaluate(state, _requestId, _generation, _nonce, now);
                    if (observation == IndependentOperationResult.Failed) return observation;
                    if (state.Ticket.DispatchStartedUtcTicks == 0)
                    { Detail = "DispatchWorkerExitedBeforeDurableDispatch"; return IndependentOperationResult.Failed; }
                }
                // A zero exit code or Task Scheduler instance ID is never
                // enough. An ambiguous dispatch waits for the same ticket.
                Detail = observation == IndependentOperationResult.Completed
                    ? "ReplacementDurablyConsumedTicket;BusinessVerificationPending"
                    : "WaitingForReplacementTicketConsumption";
                return observation;
            }
            catch (Exception error)
            {
                CancelWorker();
                var message = "LaunchObservationFailed:" + error.GetType().Name + ":" + error.Message;
                Detail = message.Length <= 512 ? message : message.Substring(0, 512);
                return IndependentOperationResult.Failed;
            }
        }

        private void CancelWorker() { _worker?.Dispose(); _worker = null; }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; CancelWorker(); }
        }
    }
}
