using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// The only V3 recovery decision owner.  EngineHost publishes facts and
    /// executes commands; this LocalSystem service owns durable intent/budget.
    /// Independent SafetyAgent proof is deliberately required before the
    /// coordinator may schedule a rebuild or resume.
    /// </summary>
    internal sealed class SupervisorRecoveryKernelService : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly RecoveryCoordinator _coordinator;
        private readonly V3SafetyProofExecutor _safetyProofExecutor;
        private readonly V3EngineProcessReplacer _engineProcessReplacer;
        private readonly Action<string, string> _audit;
        private readonly HashSet<string> _observations =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly object _commandGate = new object();
        private Task _loop;
        private string _lastExecutedCommandId = string.Empty;
        private string _lastEngineInstanceId = string.Empty;
        private long _lastPulse;
        private long _lastPulseUtcTicks;
        private EngineStateSnapshot _lastEngineSnapshot;
        private bool _engineMissingIncidentRaised;
        private int _started;

        internal SupervisorRecoveryKernelService(Action<string, string> audit)
        {
            _audit = audit ?? ((eventType, detail) => { });
            _coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal());
            _safetyProofExecutor = new V3SafetyProofExecutor(_audit);
            _engineProcessReplacer = new V3EngineProcessReplacer(_audit);
        }

        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _loop = Task.Run(() => RunAsync(_stop.Token));
            _audit("RecoveryKernelStarted",
                "Schema=7;Journal=%ProgramData%\\MTTFTest\\RecoveryKernel");
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ObserveEngineState();
                    ObserveLatestFault();
                    var expired = _coordinator.ReconcileExpiredCommand(
                        DateTime.UtcNow.Ticks);
                    if (expired != null)
                        _audit("RecoveryCommandDeadlineReconciled",
                            "Reason=" + expired.Reason +
                            ";Next=" + expired.Command?.Kind +
                            ";State=" + expired.DesiredState?.State);
                    ExecutePendingCommandIfNeeded();
                }
                catch (Exception ex)
                {
                    _audit("RecoveryKernelIterationFailed", ex.GetBaseException().Message);
                }
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }

        private void ObserveEngineState()
        {
            EngineStateSnapshot snapshot;
            try { snapshot = EngineHostPipeClient.ReadSnapshot(1000); }
            catch
            {
                if (_lastPulseUtcTicks > 0 &&
                    DateTime.UtcNow.Ticks - _lastPulseUtcTicks > TimeSpan.FromSeconds(3).Ticks)
                {
                    _audit("EngineHostPulseMissing",
                        "LastEngine=" + _lastEngineInstanceId + ";LastPulse=" + _lastPulse);
                    PublishEngineHostMissingObservation();
                }
                return;
            }
            _lastEngineSnapshot = snapshot;
            _engineMissingIncidentRaised = false;
            if (!string.Equals(_lastEngineInstanceId, snapshot.EngineInstanceId,
                    StringComparison.Ordinal))
            {
                _audit("EngineHostObserved",
                    "Instance=" + snapshot.EngineInstanceId + ";Session=" + snapshot.SessionId +
                    ";Run=" + snapshot.RunId + ";Epoch=" + snapshot.RunEpoch);
                _lastEngineInstanceId = snapshot.EngineInstanceId;
            }
            if (_coordinator.AdmitSafeIdleEngine(snapshot))
                _audit("EngineHostSafeIdleAdmitted",
                    "Session=" + snapshot.SessionId +
                    ";Run=" + snapshot.RunId +
                    ";Epoch=" + snapshot.RunEpoch);
            if (snapshot.PulseSequence > _lastPulse)
            {
                _lastPulse = snapshot.PulseSequence;
                _lastPulseUtcTicks = DateTime.UtcNow.Ticks;
            }
            if ((snapshot.State == SystemTerminalState.Running ||
                 snapshot.State == SystemTerminalState.RunningDegraded) &&
                _coordinator.ObserveStableProgress(
                    snapshot.SessionId,
                    snapshot.RunId,
                    snapshot.RunEpoch,
                    snapshot.QualificationCyclesCompleted,
                    snapshot.FormalCyclesSinceRecovery,
                    snapshot.StableSinceUtcTicks,
                    DateTime.UtcNow.Ticks))
                _audit("RecoveryBudgetResetAfterStableRun",
                    "Session=" + snapshot.SessionId +
                    ";Run=" + snapshot.RunId +
                    ";Qualification=" + snapshot.QualificationCyclesCompleted +
                    ";Formal=" + snapshot.FormalCyclesSinceRecovery);
        }

        private void ObserveLatestFault()
        {
            FaultObservation observation;
            try { observation = EngineHostPipeClient.ReadFault(1000); }
            catch { return; }
            if (observation?.IsStructurallyValid() != true) return;
            lock (_observations)
                if (!_observations.Add(observation.ObservationId)) return;
            var decision = _coordinator.Observe(observation);
            _audit("RecoveryIntentCommitted",
                "Incident=" + decision.Intent.Identity.IncidentId +
                ";Owner=" + decision.Intent.OwnerId +
                ";Scope=" + decision.Intent.Identity.ResourceScope +
                ";Command=" + decision.Command?.Kind +
                ";Reason=" + decision.Reason);
        }

        private void PublishEngineHostMissingObservation()
        {
            if (_engineMissingIncidentRaised || _lastEngineSnapshot == null ||
                !V3EngineProcessReplacer.ProveNoLiveEngineHost())
                return;
            _engineMissingIncidentRaised = true;
            var snapshot = _lastEngineSnapshot;
            var observation = new FaultObservation
            {
                ObservationId = RecoveryProtocolV7.NewId(),
                Identity = new RecoveryIdentity
                {
                    SessionId = snapshot.SessionId,
                    RunId = snapshot.RunId,
                    RunEpoch = snapshot.RunEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(),
                    ResourceScope = "EngineHost",
                    Generation = 1,
                    Revision = Math.Max(1, snapshot.Revision)
                },
                ResourceKind = ResourceKind.EngineHost,
                ResourceId = "EngineHost",
                FaultCode = "EngineHostProcessExited",
                ObservableProperty = "PulseMissingAndExactProcessAbsent",
                Detail = "Supervisor proved that the previously observed EngineHost identity no longer exists.",
                Severity = FaultSeverity.RecoveryRequired,
                ScopeProven = true,
                HardwareConfirmed = false,
                OutputOffConfirmed = false,
                PressureSafe = false,
                DataBoundaryClosed = true,
                SafetyChainHealthy = true,
                ObservedUtcTicks = DateTime.UtcNow.Ticks
            };
            lock (_observations) _observations.Add(observation.ObservationId);
            var decision = _coordinator.Observe(observation);
            _audit("EngineHostExitRecoveryIntentCommitted",
                "Incident=" + decision.Intent.Identity.IncidentId +
                ";Owner=" + decision.Intent.OwnerId +
                ";Command=" + decision.Command?.Kind);
        }

        private void ExecutePendingCommandIfNeeded()
        {
            var command = _coordinator.Snapshot().PendingCommand;
            if (command?.IsStructurallyValid() != true ||
                string.Equals(command.CommandId, _lastExecutedCommandId,
                    StringComparison.Ordinal))
                return;
            lock (_commandGate)
            {
                if (string.Equals(command.CommandId, _lastExecutedCommandId,
                        StringComparison.Ordinal))
                    return;
                ExecutePendingCommand(command);
            }
        }

        private RecoveryDecision ExecutePendingCommand(RecoveryCommand command)
        {
            if (command.Kind == RecoveryCommandKind.EnsureUserInterface)
            {
                _lastExecutedCommandId = command.CommandId;
                return _coordinator.AcknowledgeCommand(
                    command.Identity.IncidentId,
                    command.CommandId,
                    true,
                    "UserInterfaceMonitorOwnsReplacement");
            }

            RecoveryCommandReceipt receipt;
            if (command.Kind == RecoveryCommandKind.ReplaceEngineHost ||
                command.Kind == RecoveryCommandKind.ActivateLastKnownGood)
            {
                receipt = _engineProcessReplacer.Replace(
                    command,
                    command.Kind == RecoveryCommandKind.ActivateLastKnownGood);
            }
            else
            {
            var timeout = command.Kind == RecoveryCommandKind.RunQualificationCycle
                ? 180000
                : command.Kind == RecoveryCommandKind.RunPassivePreflight ||
                  command.Kind == RecoveryCommandKind.RebuildResource ||
                  command.Kind == RecoveryCommandKind.ResumeFormalRun ||
                  command.Kind == RecoveryCommandKind.IsolateResource
                    ? 60000
                    : 30000;
            try
            {
                receipt = EngineHostPipeClient.Execute(command, timeout);
            }
            catch when (command.Kind == RecoveryCommandKind.DisableOutputs &&
                        V3EngineProcessReplacer.ProveNoLiveEngineHost())
            {
                receipt = new RecoveryCommandReceipt
                {
                    CommandId = command.CommandId,
                    IdempotencyKey = command.IdempotencyKey,
                    Succeeded = true,
                    Detail = "EngineHostAbsent;DataWriterClosedByProcessExit;" +
                             "IndependentHardwareProofRequired",
                    DataBoundaryClosed = true,
                    ExecutionAuthorizationRevoked = true,
                    CompletedUtcTicks = DateTime.UtcNow.Ticks
                };
            }
            }
            _lastExecutedCommandId = command.CommandId;
            _audit("RecoveryCommandExecuted",
                "Incident=" + command.Identity.IncidentId +
                ";Command=" + command.Kind +
                ";Succeeded=" + receipt.Succeeded +
                ";Detail=" + receipt.Detail);

            if (command.Kind == RecoveryCommandKind.DisableOutputs)
            {
                var proof = _safetyProofExecutor.Execute(
                    command, receipt, TimeSpan.FromSeconds(30));
                return _coordinator.AcceptSafetyProof(proof);
            }
            if (command.Kind == RecoveryCommandKind.StopByOperator ||
                command.Kind == RecoveryCommandKind.EnterSafeIdle)
            {
                var proof = _safetyProofExecutor.Execute(
                    command, receipt, TimeSpan.FromSeconds(30));
                return _coordinator.AcknowledgeCommand(
                    command.Identity.IncidentId,
                    command.CommandId,
                    receipt.Succeeded && proof.IsComplete,
                    receipt.Detail + ";IndependentSafetyProof=" + proof.IsComplete);
            }
            if (command.Kind == RecoveryCommandKind.RunQualificationCycle &&
                receipt.Succeeded)
            {
                return _coordinator.AcceptQualification(new QualificationReceipt
                {
                    Identity = command.Identity.Clone(),
                    ReceiptId = RecoveryProtocolV7.NewId(),
                    QualificationCyclesCompleted =
                        receipt.QualificationCyclesCompleted,
                    FormalCyclesCompleted = receipt.FormalCyclesCompleted,
                    StableSinceUtcTicks = receipt.StableSinceUtcTicks,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks,
                    InterruptedCycleCounted = receipt.InterruptedCycleCounted
                });
            }
            return _coordinator.AcknowledgeCommand(
                command.Identity.IncidentId,
                command.CommandId,
                receipt.Succeeded,
                receipt.Detail);
        }

        internal RecoveryDecision SubmitOperatorCommand(OperatorCommand command)
        {
            if (command?.IsStructurallyValid() != true)
                throw new ArgumentException("OperatorCommandInvalid", nameof(command));
            if (command.Kind != OperatorCommandKind.Stop &&
                command.Kind != OperatorCommandKind.Start)
                throw new InvalidOperationException(
                    "OperatorCommandNotYetEnabled:" + command.Kind);
            lock (_commandGate)
            {
                var snapshot = EngineHostPipeClient.ReadSnapshot(3000);
                if (!string.Equals(snapshot.SessionId, command.SessionId,
                        StringComparison.Ordinal) ||
                    !string.Equals(snapshot.RunId, command.RunId,
                        StringComparison.Ordinal) ||
                    snapshot.RunEpoch != command.RunEpoch ||
                    snapshot.Revision != command.BaseRevision)
                    throw new InvalidOperationException(
                        "OperatorCommandEngineRevisionConflict");
                if (command.Kind == OperatorCommandKind.Start)
                {
                    var started = _coordinator.StartByOperator(
                        command.SessionId,
                        command.RunId,
                        command.RunEpoch,
                        "OperatorStart:" + command.CommandId);
                    _audit("OperatorStartTransactionCommitted",
                        "Incident=" + started.Intent.Identity.IncidentId +
                        ";Owner=" + started.Intent.OwnerId +
                        ";Command=" + started.Command?.Kind);
                    return started;
                }
                var decision = _coordinator.StopByOperator(
                    command.SessionId,
                    command.RunId,
                    command.RunEpoch,
                    "OperatorStop:" + command.CommandId);
                var completed = ExecutePendingCommand(decision.Command);
                _audit("OperatorStopTransactionCompleted",
                    "Incident=" + decision.Intent.Identity.IncidentId +
                    ";Owner=" + decision.Intent.OwnerId +
                    ";State=" + completed.DesiredState?.State);
                return completed;
            }
        }

        internal RecoveryDecision SubmitSafetyProof(SafetyProof proof)
        {
            var decision = _coordinator.AcceptSafetyProof(proof);
            _audit("RecoverySafetyProofAccepted",
                "Incident=" + proof.Identity.IncidentId +
                ";Complete=" + proof.IsComplete +
                ";Next=" + decision.Command?.Kind);
            return decision;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _stop.Dispose();
        }
    }
}
