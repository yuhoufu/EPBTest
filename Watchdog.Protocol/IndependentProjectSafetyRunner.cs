using System;
using System.IO;
using System.Security.Principal;

namespace MTTFTest.Watchdog.Protocol
{
    // Production safety operations shared by the service loop. Commands are
    // created only inside the installed, ACL-protected state directory.
    public sealed class IndependentProjectSafetyRunner : IDisposable
    {
        private readonly IndependentExecutorRegistration _registration;
        private readonly IndependentProjectStateStore _store;
        private readonly string _executor;
        private readonly IndependentExecutorLease _lease;
        private readonly object _operationGate = new object();
        private IndependentSafetyWorkerOperation _hardware;
        private IndependentProcessRetirement _retirement;
        private IndependentProcessIdentity _retiringSession;
        private string _request;
        private long _generation;
        private IndependentRecoveryPhase _phase;
        private bool _disposed;
        public string Detail { get; private set; }
        public string ExecutorIdentity => _executor;

        public IndependentProjectSafetyRunner(string registrationPath, string executorIdentity)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("IndependentSafetyRunnerRequiresSystem");
            if (string.IsNullOrWhiteSpace(executorIdentity)) throw new ArgumentException(nameof(executorIdentity));
            _registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            if (executorIdentity != "IndependentExecutor:" + _registration.InstallationId)
                throw new InvalidOperationException("IndependentSafetyRunnerRegisteredExecutorMismatch");
            _store = new IndependentProjectStateStore(_registration.StateDirectory);
            _executor = executorIdentity;
            var leasePath = Path.Combine(_registration.StateDirectory, "independent-executor.lease");
            if (File.Exists(leasePath)) IndependentProtectedFiles.RequireTrustedFile(leasePath);
            _lease = new IndependentExecutorLease(_registration.StateDirectory, _registration.InstallationId);
        }

        public IndependentOperationResult Poll(IndependentRecoveryTransaction expected, long now)
        {
            lock (_operationGate) return PollCore(expected, now);
        }

        private IndependentOperationResult PollCore(IndependentRecoveryTransaction expected, long now)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentProjectSafetyRunner));
            _lease.RequireHeld();
            if (expected == null) throw new ArgumentNullException(nameof(expected));
            IndependentProjectState state;
            try
            {
                state = _store.Read();
                _registration.RequireBoundIntent(state?.Intent);
            }
            catch { Cancel(); throw; }
            var tx = state?.Transaction;
            if (tx == null || !state.SafetyCleanupPending || tx.IsTerminal || tx.ExecutorIdentity != _executor ||
                tx.RequestId != expected.RequestId || tx.Generation != expected.Generation || tx.Phase != expected.Phase)
            {
                Cancel();
                throw new InvalidOperationException("IndependentSafetyRunnerAuthorityChanged");
            }
            if (tx.Phase != IndependentRecoveryPhase.PowerOff && tx.Phase != IndependentRecoveryPhase.RetireControls &&
                tx.Phase != IndependentRecoveryPhase.OutputsSafe)
                throw new InvalidOperationException("IndependentSafetyRunnerPhaseInvalid");
            if (now < tx.LastAttemptUtcTicks || now >= tx.PhaseDeadlineUtcTicks)
            { Cancel(); Detail = "IndependentSafetyRunnerDeadlineExceeded"; return IndependentOperationResult.Failed; }
            if (_request != null && (_request != tx.RequestId || _generation != tx.Generation || _phase != tx.Phase)) Cancel();
            _request = tx.RequestId; _generation = tx.Generation; _phase = tx.Phase;
            try
            {
                if (tx.Phase == IndependentRecoveryPhase.PowerOff)
                {
                    if (state.Controller == null) throw new InvalidDataException("IndependentSafetyControllerIdentityMissing");
                    IndependentExecutionFence.Revoke(_registration.InstallationId, state.Controller);
                    // Retire old recovery helpers before opening their hardware
                    // handles. The old main remains until PSU OFF is confirmed.
                    var child = _registration.SelectSessionCleanupTarget(state, _executor);
                    if (child != null)
                    {
                        if (_retirement == null)
                        {
                            _retiringSession = child.Process;
                            _retirement = new IndependentProcessRetirement(child.Process, child.Process,
                                child.Process.ExecutablePath, (int)Math.Min(10000, Math.Max(1,
                                    TimeSpan.FromTicks(tx.PhaseDeadlineUtcTicks - now).TotalMilliseconds)));
                        }
                        var retired = _retirement.Poll();
                        Detail = "SessionHelper:" + _retirement.Detail;
                        if (retired == IndependentOperationResult.Completed)
                        {
                            _store.AcknowledgeSessionProcessExit(_retiringSession);
                            _retirement.Dispose(); _retirement = null; _retiringSession = null;
                            return IndependentOperationResult.Pending;
                        }
                        return retired;
                    }
                }
                if (tx.Phase == IndependentRecoveryPhase.RetireControls)
                {
                    if (_retirement == null)
                    {
                        if (state.Controller == null) throw new InvalidDataException("IndependentSafetyControllerIdentityMissing");
                        _retirement = new IndependentProcessRetirement(state.Controller, state.Controller,
                            _registration.ExecutablePath, (int)Math.Min(10000, Math.Max(1,
                                TimeSpan.FromTicks(tx.PhaseDeadlineUtcTicks - now).TotalMilliseconds)));
                    }
                    var result = _retirement.Poll();
                    Detail = _retirement.Detail;
                    return result;
                }
                if (_hardware == null)
                {
                    if (tx.Phase == IndependentRecoveryPhase.PowerOff)
                    {
                        if (state.Controller == null) throw new InvalidDataException("IndependentSafetyControllerIdentityMissing");
                        IndependentExecutionFence.Revoke(_registration.InstallationId, state.Controller);
                    }
                    var command = _registration.CreateSafetyCommand(state, _executor, now);
                    var commandPath = Path.Combine(_registration.StateDirectory, "safety-" + command.StageNonce + ".json");
                    if (File.Exists(commandPath)) throw new IOException("IndependentSafetyCommandAlreadyExists");
                    BoundedJson.Write(commandPath, command);
                    _hardware = new IndependentSafetyWorkerOperation(_registration.SafetyExecutablePath,
                        _registration.SafetyExecutableSha256, commandPath);
                }
                var outcome = _hardware.Poll(now);
                Detail = _hardware.Detail;
                return outcome;
            }
            catch (Exception error)
            {
                Cancel();
                var message = "IndependentSafetyRunnerFailed:" + error.GetType().Name + ":" + error.Message;
                Detail = message.Length <= 512 ? message : message.Substring(0, 512);
                return IndependentOperationResult.Failed;
            }
        }

        public void Cancel()
        {
            lock (_operationGate)
            {
                _hardware?.Dispose(); _hardware = null;
                _retirement?.Dispose(); _retirement = null;
                _retiringSession = null;
                _request = null;
            }
        }

        public void Dispose()
        {
            lock (_operationGate)
            {
                if (_disposed) return;
                _disposed = true;
                try { Cancel(); }
                finally { _lease.Dispose(); }
            }
        }
    }
}
