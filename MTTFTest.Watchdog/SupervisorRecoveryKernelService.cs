using System;
using System.Linq;
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
        private readonly AlarmPanelCommandDispatcher _panelDispatcher;
        private readonly RecoveryCommandDispatchPump _commandPump;
        private readonly V3SafetyProofExecutor _safetyProofExecutor;
        private readonly V3EngineProcessReplacer _engineProcessReplacer;
        private readonly Action<string, string> _audit;
        private readonly HashSet<string> _observations =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly object _commandGate = new object();
        private readonly EngineNativeProcessFence _nativeProcessFence = new EngineNativeProcessFence();
        private Task _loop;
        private Task _panelLoop;
        private Task _maintenanceLoop;
        private readonly Func<int, long, string, bool> _authorizeMaintenanceUi;
        private readonly Func<int, long, string, bool> _authorizeMaintenanceEngine;
        private string _lastEngineInstanceId = string.Empty;
        private long _lastPulse;
        private long _lastPulseUtcTicks;
        private bool _engineMissingIncidentRaised;
        private readonly long _startedUtcTicks = DateTime.UtcNow.Ticks;
        private string _rejectedEngineInstanceId = string.Empty;
        private int _started;

        internal SupervisorRecoveryKernelService(Action<string, string> audit,
            Func<int, long, string, bool> authorizeMaintenanceUi,
            Func<int, long, string, bool> authorizeMaintenanceEngine)
        {
            _authorizeMaintenanceUi = authorizeMaintenanceUi ?? throw new ArgumentNullException(nameof(authorizeMaintenanceUi));
            _authorizeMaintenanceEngine = authorizeMaintenanceEngine ?? throw new ArgumentNullException(nameof(authorizeMaintenanceEngine));
            _audit = audit ?? ((eventType, detail) => { });
            _coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal());
            _panelDispatcher = new AlarmPanelCommandDispatcher(_coordinator);
            _safetyProofExecutor = new V3SafetyProofExecutor(_audit);
            _engineProcessReplacer = new V3EngineProcessReplacer(_audit,
                command => _coordinator.Snapshot().PendingCommand?.CommandId == command.CommandId);
            _commandPump = new RecoveryCommandDispatchPump(command => ExecutePendingCommand(command), (command, ex) =>
                _audit("RecoveryCommandTransportFailed", "Command=" + command.CommandId + ";Kind=" + command.Kind +
                    ";AwaitingDurableDeadline;Error=" + ex.GetBaseException().Message));
        }

        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            // A replacement Supervisor never revives a persisted maintenance lease,
            // even if the old UI and EngineHost processes still exist.
            _coordinator.ReconcilePressureMaintenance(DateTime.UtcNow.Ticks, string.Empty, false, true);
            _loop = Task.Run(() => RunAsync(_stop.Token));
            _panelLoop = Task.Run(() => RunPanelCommandsAsync(_stop.Token));
            _maintenanceLoop = Task.Run(() => RunMaintenanceAsync(_stop.Token));
            _audit("RecoveryKernelStarted",
                "Schema=7;Journal=%ProgramData%\\MTTFTest\\RecoveryKernel");
        }

        internal EngineEnvironmentSession ResolveUserInterfaceEnvironment(Action<EngineEnvironmentSession> launchStoppedEngine, string requiredSessionId = null)
        {
            using (var lease = _nativeProcessFence.TryAcquire())
            {
                EngineStateSnapshot observed = null;
                try { observed = EngineHostPipeClient.ReadSnapshot(1000); } catch { }
                var plan = EngineEnvironmentSession.Resolve(_coordinator.Snapshot(), observed, lease != null);
                if (requiredSessionId != null && requiredSessionId != plan.SessionId)
                    throw new InvalidOperationException("SupervisorUiRecoveryDurableSessionMismatch");
                if (plan.ShouldLaunchEngine)
                {
                    if (!V3EngineProcessReplacer.ProveNoLiveEngineHost())
                        throw new InvalidOperationException("EngineEnvironmentHasUnresponsiveProcess;KernelRecoveryRequired");
                    launchStoppedEngine(plan);
                }
                _audit("UserInterfaceEnvironmentResolved", "Session=" + plan.SessionId + ";Run=" + plan.RunId + ";Reason=" + plan.Reason);
                return plan;
            }
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

        private async Task RunPanelCommandsAsync(CancellationToken token)
        {
            // A serial panel timeout cannot hold _commandGate or the safety loop.
            // The durable bounded queue is resumed after Supervisor replacement.
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = _panelDispatcher.ExecuteNext(DateTime.UtcNow.Ticks, command => EngineHostPipeClient.ExecutePanel(command));
                    if (result != null)
                        _audit("AlarmPanelCommandCompleted", "Command=" + result.CommandId + ";Succeeded=" + result.ExecutionSucceeded + ";Detail=" + result.Detail);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Transient lost responses keep the original durable command pending.
                    // Its terminal receipt/deadline is audited once, not on each poll.
                }
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
        }

        internal PressureMaintenanceLease RenewPressureMaintenance(PressureMaintenanceHeartbeat heartbeat)
        {
            if (heartbeat?.IsStructurallyValid() != true ||
                !_authorizeMaintenanceUi(heartbeat.UiProcessId, heartbeat.UiProcessStartUtcTicks, heartbeat.SessionId))
                throw new InvalidOperationException("MaintenanceHeartbeatUiNotAuthorized");
            if (heartbeat.EngineInstanceId != Volatile.Read(ref _lastEngineInstanceId))
                throw new InvalidOperationException("MaintenanceHeartbeatEngineNotCurrent");
            return _coordinator.RenewPressureMaintenance(heartbeat, DateTime.UtcNow.Ticks);
        }

        private async Task RunMaintenanceAsync(CancellationToken token)
        {
            string sentHash = null;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var lease = _coordinator.Snapshot().ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null)?.PressureMaintenance;
                    if (lease != null)
                    {
                        var uiAlive = false;
                        try { uiAlive = _authorizeMaintenanceUi(lease.UiProcessId, lease.UiProcessStartUtcTicks, lease.SessionId); } catch { }
                        var revoked = _coordinator.ReconcilePressureMaintenance(DateTime.UtcNow.Ticks,
                            Volatile.Read(ref _lastEngineInstanceId), uiAlive);
                        if (revoked != null)
                        {
                            _audit("PressureMaintenanceRevoked", "Incident=" + lease.IncidentId + ";Reason=" + revoked.Reason);
                            ExecutePendingCommandIfNeeded();
                        }
                        lease = _coordinator.Snapshot().ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null)?.PressureMaintenance;
                        if (lease != null && lease.ComputeSha256() != sentHash)
                        {
                            // Separate memory-only endpoint: no hardware I/O, command
                            // gate, process replacement or safety proof in this exchange.
                            await EngineHostPipeClient.SendMaintenanceLeaseAsync(lease,
                                (pid, started) => _authorizeMaintenanceEngine(pid, started, lease.SessionId), token).ConfigureAwait(false);
                            sentHash = lease.ComputeSha256();
                        }
                    }
                    else sentHash = null;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // The lease and command deadlines remain authoritative. A failed
                    // publication cannot extend local authority or flood the audit log.
                }
                try { await Task.Delay(100, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        private void ObserveEngineState()
        {
            EngineStateSnapshot snapshot;
            try { snapshot = EngineHostPipeClient.ReadSnapshot(1000); }
            catch
            {
                // Source release and destination admission are one bounded,
                // already-safe transaction. The deadline reconciler below still
                // runs; this expected gap must not create a competing fault owner.
                if (EngineEnvironmentSession.IsExpectedProjectHandoffGap(_coordinator.Snapshot(), DateTime.UtcNow.Ticks)) return;
                if (DateTime.UtcNow.Ticks - (_lastPulseUtcTicks > 0 ? _lastPulseUtcTicks : _startedUtcTicks) > TimeSpan.FromSeconds(3).Ticks)
                {
                    PublishEngineHostMissingObservation();
                }
                return;
            }
            var desired = _coordinator.Snapshot().DesiredState;
            if (desired != null && (desired.SessionId != snapshot.SessionId || desired.RunId != snapshot.RunId || desired.RunEpoch != snapshot.RunEpoch))
            {
                if (_rejectedEngineInstanceId != snapshot.EngineInstanceId)
                {
                    _rejectedEngineInstanceId = snapshot.EngineInstanceId;
                    _audit("ForeignEngineIdentityRejected", "ExpectedRun=" + desired.RunId + ";ObservedRun=" + snapshot.RunId);
                }
                return;
            }
            _engineMissingIncidentRaised = false;
            if (!string.Equals(_lastEngineInstanceId, snapshot.EngineInstanceId,
                    StringComparison.Ordinal))
            {
                _audit("EngineHostObserved",
                    "Instance=" + snapshot.EngineInstanceId + ";Session=" + snapshot.SessionId +
                    ";Run=" + snapshot.RunId + ";Epoch=" + snapshot.RunEpoch);
                _lastEngineInstanceId = snapshot.EngineInstanceId;
                // Pulse sequence is monotonic only within one EngineHost instance.
                // Keeping the old high-water mark makes a replacement appear stale.
                _lastPulse = 0;
                _lastPulseUtcTicks = 0;
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
            var durable = _coordinator.Snapshot();
            var desired = durable.DesiredState;
            if (_engineMissingIncidentRaised || desired == null || durable.ActiveIntents.Length != 0 ||
                (desired.State != SystemTerminalState.Running && desired.State != SystemTerminalState.RunningDegraded) ||
                !V3EngineProcessReplacer.ProveNoLiveEngineHost())
                return;
            var observation = new FaultObservation
            {
                ObservationId = RecoveryProtocolV7.NewId(),
                Identity = new RecoveryIdentity
                {
                    SessionId = desired.SessionId,
                    RunId = desired.RunId,
                    RunEpoch = desired.RunEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(),
                    ResourceScope = "EngineHost",
                    Generation = 1,
                    Revision = Math.Max(1, durable.Revision)
                },
                ResourceKind = ResourceKind.EngineHost,
                ResourceId = "EngineHost",
                FaultCode = "EngineHostProcessExited",
                ObservableProperty = "PulseMissingAndExactProcessAbsent",
                Detail = "Supervisor proved no live EngineHost for the durable running session, including after Supervisor replacement.",
                Severity = FaultSeverity.RecoveryRequired,
                ScopeProven = true,
                HardwareConfirmed = false,
                OutputOffConfirmed = false,
                PressureSafe = false,
                DataBoundaryClosed = false,
                SafetyChainHealthy = true,
                ObservedUtcTicks = DateTime.UtcNow.Ticks
            };
            lock (_observations) _observations.Add(observation.ObservationId);
            var decision = _coordinator.Observe(observation);
            _engineMissingIncidentRaised = true;
            _audit("EngineHostExitRecoveryIntentCommitted",
                "Incident=" + decision.Intent.Identity.IncidentId +
                ";Owner=" + decision.Intent.OwnerId +
                ";Command=" + decision.Command?.Kind);
        }

        private void ExecutePendingCommandIfNeeded()
        {
            _commandPump.TryDispatch(_coordinator.Snapshot().PendingCommand);
        }

        private RecoveryDecision ExecutePendingCommand(RecoveryCommand command)
        {
            if (_coordinator.Snapshot().PendingCommand?.CommandId != command.CommandId) return null;
            if (command.Kind == RecoveryCommandKind.EnsureUserInterface)
            {
                return _coordinator.AcknowledgeCommand(
                    command.Identity.IncidentId,
                    command.CommandId,
                    true,
                    "UserInterfaceMonitorOwnsReplacement");
            }

            RecoveryCommandReceipt receipt;
            if (command.Kind == RecoveryCommandKind.ReplaceEngineHost ||
                command.Kind == RecoveryCommandKind.ActivateLastKnownGood || command.Kind == RecoveryCommandKind.ActivateProjectSwitch)
            {
                using (var lease = _nativeProcessFence.AcquireUntil(command.DeadlineUtcTicks))
                receipt = _engineProcessReplacer.Replace(
                    command,
                    command.Kind == RecoveryCommandKind.ActivateLastKnownGood);
            }
            else
            {
            var timeout = command.Kind == RecoveryCommandKind.PauseBatchGracefully || command.Kind == RecoveryCommandKind.PauseChannelGracefully ? 300000 :
                command.Kind == RecoveryCommandKind.RunQualificationCycle || command.Kind == RecoveryCommandKind.CommitTestConfiguration ||
                command.Kind == RecoveryCommandKind.PrepareProjectSwitch || command.Kind == RecoveryCommandKind.AbortProjectSwitch ||
                command.Kind == RecoveryCommandKind.ResumePausedBatch || command.Kind == RecoveryCommandKind.ResumePausedChannel
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
                    Detail = "EngineHostAbsent;NativeHandlesClosedByProcessExit;" +
                             "IndependentHardwareProofAndDataReconciliationRequired",
                    DataBoundaryClosed = false,
                    ExecutionAuthorizationRevoked = true,
                    CompletedUtcTicks = DateTime.UtcNow.Ticks
                };
            }
            }
            // A Stop/fault can replace the pending transaction while this worker
            // is in hardware I/O. Its late receipt must not advance the new owner.
            if (_coordinator.Snapshot().PendingCommand?.CommandId != command.CommandId)
            {
                _audit("SupersededRecoveryReceiptIgnored", "Command=" + command.CommandId);
                return null;
            }
            _audit("RecoveryCommandExecuted",
                "Incident=" + command.Identity.IncidentId +
                ";Command=" + command.Kind +
                ";Succeeded=" + receipt.Succeeded +
                ";Detail=" + receipt.Detail);

            if (command.Kind == RecoveryCommandKind.DisableOutputs)
            {
                var proof = ExecuteIndependentSafetyProof(command, receipt);
                return _coordinator.AcceptSafetyProof(proof);
            }
            if (command.Kind == RecoveryCommandKind.StopByOperator ||
                command.Kind == RecoveryCommandKind.EnterSafeIdle || command.Kind == RecoveryCommandKind.CommitTestConfiguration)
            {
                var proof = ExecuteIndependentSafetyProof(command, receipt);
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
            if (command.Kind == RecoveryCommandKind.PauseBatchGracefully)
                return _coordinator.AcknowledgeCommand(command.Identity.IncidentId, command.CommandId,
                    receipt.Succeeded && receipt.OutputsOff && receipt.PressureSafe && receipt.DataBoundaryClosed,
                    receipt.Detail + ";ManualPauseIsNotIndependentSafetyProof");
            if (command.Kind == RecoveryCommandKind.PauseChannelGracefully || command.Kind == RecoveryCommandKind.ResumePausedChannel)
                return _coordinator.AcknowledgeManualChannelCommand(command.Identity.IncidentId, receipt);
            if (command.Kind == RecoveryCommandKind.PrepareProjectSwitch || command.Kind == RecoveryCommandKind.ActivateProjectSwitch)
                return _coordinator.AcknowledgeProjectSwitch(command.Identity.IncidentId, receipt);
            if (PressureMaintenanceProtocol.IsExecution(command.Kind))
                return _coordinator.AcknowledgePressureMaintenance(command.Identity.IncidentId, receipt);
            return _coordinator.AcknowledgeCommand(
                command.Identity.IncidentId,
                command.CommandId,
                receipt.Succeeded,
                receipt.Detail);
        }

        internal EngineUiKernelState ReadUiState(string sessionId)
        {
            var document = _coordinator.Snapshot();
            var desired = document.DesiredState;
            if (desired == null || desired.SessionId != sessionId) return new EngineUiKernelState();
            var manual = document.ActiveIntents?.SingleOrDefault();
            if (manual != null && manual.Stage != RecoveryStage.OperatorPausePending && manual.Stage != RecoveryStage.OperatorPaused &&
                manual.Stage != RecoveryStage.OperatorResumeChecking && manual.Stage != RecoveryStage.OperatorChannelsHeld) manual = null;
            return new EngineUiKernelState
            {
                Available = true, SessionId = desired.SessionId, RunId = desired.RunId, RunEpoch = desired.RunEpoch,
                Revision = document.Revision, CapturedUtcTicks = DateTime.UtcNow.Ticks, DesiredState = desired.State,
                ActiveIncidentCount = document.ActiveIntents?.Length ?? 0, CommandPending = document.PendingCommand != null,
                OperatorStage = manual?.Stage ?? RecoveryStage.None,
                ManualBatchEngineInstanceId = manual?.ManualBatchEngineInstanceId ?? string.Empty,
                ManualBatchIncidentId = manual?.Identity.IncidentId ?? string.Empty, ManualBatchOwnerId = manual?.OwnerId ?? string.Empty,
                ManualPausedChannelsMask = manual?.ManualPausedChannelsMask ?? 0,
                IsolatedResources = (document.IsolatedResources ?? Array.Empty<string>()).ToArray(),
                QualificationRetryEligibleScopes = _coordinator.QualificationRetryEligibleScopes(),
                PressureMaintenance = document.ActiveIntents?.SingleOrDefault(i => i.PressureMaintenance != null)?.PressureMaintenance.Clone(),
                PressureMaintenanceStage = document.ActiveIntents?.SingleOrDefault(i => i.PressureMaintenance != null)?.Stage ?? RecoveryStage.None,
                Reason = desired.Reason
            };
        }

        internal bool CanObserveInitialEngine(EngineStateSnapshot snapshot)
        {
            var document = _coordinator.Snapshot();
            return document.DesiredState == null && document.ActiveIntents.Length == 0 && document.PendingCommand == null &&
                snapshot?.IsStructurallyValid() == true && snapshot.State == SystemTerminalState.SafeIdleAlarmed &&
                !snapshot.OutputsEnergized && string.IsNullOrEmpty(snapshot.RecoveryOwnerId) && string.IsNullOrEmpty(snapshot.RecoveryIncidentId);
        }

        private SafetyProof ExecuteIndependentSafetyProof(RecoveryCommand command, RecoveryCommandReceipt receipt)
        {
            using (var lease = _nativeProcessFence.AcquireUntil(command.DeadlineUtcTicks))
                return _safetyProofExecutor.Execute(command, receipt,
                    TimeSpan.FromTicks(Math.Max(1, Math.Min(TimeSpan.FromSeconds(30).Ticks, command.DeadlineUtcTicks - DateTime.UtcNow.Ticks))));
        }

        internal OperatorCommandAdmission SubmitOperatorCommand(OperatorCommand command, bool queryOnly = false)
        {
            if (command?.IsStructurallyValid() != true)
                throw new ArgumentException("OperatorCommandInvalid", nameof(command));
            if (queryOnly) return _coordinator.QueryOperatorCommand(command);
            if (AlarmPanelCommand.IsPanelOperation(command.Kind))
            {
                var existing = _coordinator.QueryOperatorCommand(command);
                if (existing != null) return existing;
                return _coordinator.AdmitOperatorCommand(command, EngineHostPipeClient.ReadPanelSnapshot());
            }
            if (command.Kind != OperatorCommandKind.Stop &&
                command.Kind != OperatorCommandKind.Start && command.Kind != OperatorCommandKind.CommitConfiguration &&
                command.Kind != OperatorCommandKind.SwitchProject && !ManualBatchCommand.IsOperation(command.Kind) && !PressureMaintenanceProtocol.IsOperation(command.Kind))
                throw new InvalidOperationException(
                    "OperatorCommandNotYetEnabled:" + command.Kind);
            if (command.Kind == OperatorCommandKind.Stop || command.Kind == OperatorCommandKind.EndPressureMaintenance)
            {
                var existing = _coordinator.QueryOperatorCommand(command);
                if (existing != null) return existing;
                var maintenanceStop = _coordinator.TryJoinPressureMaintenanceStop(command);
                if (maintenanceStop != null)
                {
                    if (maintenanceStop.Accepted) ExecutePendingCommandIfNeeded();
                    return maintenanceStop;
                }
                var joined = _coordinator.TryJoinProjectSwitchStop(command);
                if (joined != null) return joined;
                var admission = _coordinator.AdmitOperatorCommand(command, EngineHostPipeClient.ReadSnapshot(3000));
                // Do not wait for the observation loop (which may be in a failed
                // read) to notice an operator's already-durable stop intent.
                if (admission.Accepted) ExecutePendingCommandIfNeeded();
                return admission;
            }
            lock (_commandGate)
            {
                var existing = _coordinator.QueryOperatorCommand(command);
                if (existing != null) return existing;
                var snapshot = EngineHostPipeClient.ReadSnapshot(3000);
                var admission = _coordinator.AdmitOperatorCommand(command, snapshot);
                // Receipt and intent are now one durable CAS. The normal kernel loop
                // executes the pending command; this response never claims stop proof.
                _audit("OperatorTransactionAdmitted", "Command=" + command.CommandId +
                    ";Accepted=" + admission.Accepted + ";Incident=" + admission.IncidentId +
                    ";Owner=" + admission.OwnerId + ";Reason=" + admission.Detail);
                return admission;
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
            try { _panelLoop?.Wait(TimeSpan.FromSeconds(12)); } catch { }
            try { _maintenanceLoop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { _commandPump.StopAsync(5000).GetAwaiter().GetResult(); } catch { }
            _stop.Dispose();
        }
    }
}
