using System;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed partial class RecoveryCoordinator
    {
        private void AdmitPressureMaintenance(OperatorCommand command, EngineStateSnapshot engine)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                var request = command.PressureMaintenance;
                if (request.EngineInstanceId != engine.EngineInstanceId)
                    throw new InvalidOperationException("MaintenanceEngineIdentityMismatch");
                RequireDesiredRun(current, command.SessionId, command.RunId, command.RunEpoch);
                var candidate = current.Clone();
                var now = DateTime.UtcNow.Ticks;
                RecoveryIntent intent;
                if (command.Kind == OperatorCommandKind.BeginPressureMaintenance)
                {
                    if (current.DesiredState.State != SystemTerminalState.StoppedByOperator || current.ActiveIntents.Length != 0 ||
                        current.PendingCommand != null || engine.State != SystemTerminalState.StoppedByOperator || engine.OutputsEnergized ||
                        (!engine.HardwareInitialized && !engine.HardwareRecompositionReady) || !string.IsNullOrEmpty(engine.RecoveryOwnerId) ||
                        !string.IsNullOrEmpty(engine.RecoveryIncidentId))
                        throw new InvalidOperationException("MaintenanceRequiresCompletedOperatorStop");
                    var identity = new RecoveryIdentity { SessionId = command.SessionId, RunId = command.RunId, RunEpoch = command.RunEpoch,
                        IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = current.Revision + 1 };
                    var owner = RecoveryProtocolV7.NewId();
                    intent = new RecoveryIntent { Identity = identity, OwnerId = owner, Stage = RecoveryStage.AwaitingSafetyProof,
                        DesiredTerminalState = SystemTerminalState.StoppedByOperator, OperatorTransaction = command.Clone(),
                        CreatedUtcTicks = now, UpdatedUtcTicks = now,
                        PressureMaintenance = new PressureMaintenanceLease
                        {
                            SessionId = identity.SessionId, RunId = identity.RunId, RunEpoch = identity.RunEpoch, IncidentId = identity.IncidentId,
                            OwnerId = owner, EngineInstanceId = request.EngineInstanceId, UiProcessId = request.UiProcessId,
                            UiProcessStartUtcTicks = request.UiProcessStartUtcTicks, HydraulicId = request.HydraulicId,
                            Revision = 1, CreatedUtcTicks = now, LastHeartbeatUtcTicks = now,
                            ExpiresUtcTicks = checked(now + TimeSpan.FromSeconds(PressureMaintenanceProtocol.HeartbeatLeaseSeconds).Ticks),
                            AbsoluteDeadlineUtcTicks = checked(now + TimeSpan.FromSeconds(PressureMaintenanceProtocol.MaximumSessionSeconds).Ticks)
                        } };
                    candidate.ActiveIntents = new[] { intent };
                    candidate.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs, current.Revision + 1, _policy.SafetyProofDeadline);
                }
                else
                {
                    intent = candidate.ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null);
                    var lease = intent?.PressureMaintenance;
                    if (lease == null || !lease.IsLive(now) || request.IncidentId != intent.Identity.IncidentId || request.OwnerId != intent.OwnerId ||
                        request.EngineInstanceId != lease.EngineInstanceId || request.UiProcessId != lease.UiProcessId ||
                        request.UiProcessStartUtcTicks != lease.UiProcessStartUtcTicks || request.HydraulicId != lease.HydraulicId)
                        throw new InvalidOperationException("MaintenanceAuthorityExpiredOrForeign");
                    if (command.Kind == OperatorCommandKind.EndPressureMaintenance)
                    {
                        ClosePressureMaintenance(candidate, intent, SystemTerminalState.StoppedByOperator, "MaintenanceEndedByOperator", command);
                    }
                    else
                    {
                        if (engine.RecoveryOwnerId != intent.OwnerId || engine.RecoveryIncidentId != intent.Identity.IncidentId)
                            throw new InvalidOperationException("MaintenanceEngineOwnerMismatch");
                        var interruptOutput = command.Kind == OperatorCommandKind.StopMaintenanceOutput &&
                            candidate.PendingCommand?.Kind == RecoveryCommandKind.SetMaintenancePressure;
                        if (!interruptOutput && (intent.Stage != RecoveryStage.PressureMaintenanceReady || candidate.PendingCommand != null))
                            throw new InvalidOperationException("MaintenanceExecutorNotReady");
                        if (interruptOutput)
                        {
                            CompleteMaintenanceOperator(candidate, intent, false, "MaintenanceOutputInterruptedByStop");
                            CompletePending(candidate);
                        }
                        intent.OperatorTransaction = command.Clone();
                        intent.Stage = RecoveryStage.PressureMaintenanceExecuting;
                        candidate.PendingCommand = BuildCommand(intent, command.Kind == OperatorCommandKind.SetMaintenancePressure
                            ? RecoveryCommandKind.SetMaintenancePressure : RecoveryCommandKind.StopMaintenanceOutput,
                            current.Revision + 1, _policy.CommandDeadline);
                    }
                }
                AddOperatorAdmission(candidate, command, intent, "MaintenanceOperationCommitted");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("MaintenanceAdmissionConflictLimitExceeded");
        }

        // Caller authenticates the UI identity on the pipe. Heartbeats are not
        // actuating commands and cannot create/replace an owner or reactivate a lease.
        public PressureMaintenanceLease RenewPressureMaintenance(PressureMaintenanceHeartbeat heartbeat, long nowUtcTicks)
        {
            if (heartbeat?.IsStructurallyValid() != true || !EngineUiContract.IsUtcTicks(nowUtcTicks))
                throw new ArgumentException("MaintenanceHeartbeatInvalid");
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var intent = current.ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null);
                var lease = intent?.PressureMaintenance;
                if (!heartbeat.Binds(lease) || !lease.IsLive(nowUtcTicks) ||
                    heartbeat.IssuedUtcTicks > nowUtcTicks || nowUtcTicks - heartbeat.IssuedUtcTicks > TimeSpan.FromSeconds(5).Ticks ||
                    heartbeat.IssuedUtcTicks < lease.LastHeartbeatUtcTicks || heartbeat.Sequence < lease.LastHeartbeatSequence)
                    throw new InvalidOperationException("MaintenanceHeartbeatExpiredOrForeign");
                if (heartbeat.Sequence == lease.LastHeartbeatSequence)
                {
                    if (heartbeat.IssuedUtcTicks != lease.LastHeartbeatUtcTicks)
                        throw new InvalidOperationException("MaintenanceHeartbeatSequenceConflict");
                    return lease.Clone(); // A duplicate never moves the deadline.
                }
                var candidate = current.Clone();
                var renewed = candidate.ActiveIntents.Single().PressureMaintenance;
                if (lease.LastHeartbeatSequence > 0 && heartbeat.IssuedUtcTicks - lease.LastHeartbeatUtcTicks <
                    TimeSpan.FromMilliseconds(PressureMaintenanceProtocol.MinimumHeartbeatIntervalMilliseconds).Ticks)
                    throw new InvalidOperationException("MaintenanceHeartbeatRateLimited");
                renewed.Revision = checked(renewed.Revision + 1);
                renewed.LastHeartbeatSequence = heartbeat.Sequence; renewed.LastHeartbeatUtcTicks = heartbeat.IssuedUtcTicks;
                renewed.ExpiresUtcTicks = Math.Min(renewed.AbsoluteDeadlineUtcTicks,
                    checked(heartbeat.IssuedUtcTicks + TimeSpan.FromSeconds(PressureMaintenanceProtocol.HeartbeatLeaseSeconds).Ticks));
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return renewed.Clone();
            }
            throw new InvalidOperationException("MaintenanceHeartbeatConflictLimitExceeded");
        }

        // The production loop must also call this after Supervisor restart, UI
        // disappearance, Engine replacement and independent watchdog observations.
        public RecoveryDecision ReconcilePressureMaintenance(long nowUtcTicks, string currentEngineInstanceId, bool exactUiProcessAlive,
            bool supervisorReplaced = false)
        {
            if (!EngineUiContract.IsUtcTicks(nowUtcTicks)) throw new ArgumentException("MaintenanceClockInvalid");
            var snapshot = _journal.Load();
            var active = snapshot.ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null);
            if (active == null || active.PressureMaintenance.Revoked) return null;
            if (!supervisorReplaced && active.PressureMaintenance.IsLive(nowUtcTicks) && currentEngineInstanceId == active.PressureMaintenance.EngineInstanceId && exactUiProcessAlive)
                return null;
            return Mutate(active.Identity.IncidentId, (document, intent) =>
            {
                var lease = intent.PressureMaintenance;
                if (lease.Revoked || !supervisorReplaced && lease.IsLive(nowUtcTicks) && currentEngineInstanceId == lease.EngineInstanceId && exactUiProcessAlive)
                    return "MaintenanceLifetimeAlreadyReconciled";
                ClosePressureMaintenance(document, intent, SystemTerminalState.SafeIdleAlarmed,
                    supervisorReplaced ? "MaintenanceSupervisorReplaced" : !exactUiProcessAlive ? "MaintenanceUiLost" : currentEngineInstanceId != lease.EngineInstanceId ?
                        "MaintenanceEngineReplaced" : "MaintenanceLeaseExpired", null);
                return "MaintenanceRevoked;IndependentSafetyPending";
            });
        }

        public OperatorCommandAdmission TryJoinPressureMaintenanceStop(OperatorCommand command)
        {
            if ((command?.Kind != OperatorCommandKind.Stop && command?.Kind != OperatorCommandKind.EndPressureMaintenance) || !command.IsStructurallyValid()) return null;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var intent = current.ActiveIntents.SingleOrDefault(i => i.PressureMaintenance != null);
                if (intent == null) return null;
                var prior = FindOperatorAdmission(current, command); if (prior != null) return prior.Clone();
                if (command.Kind == OperatorCommandKind.EndPressureMaintenance && !command.PressureMaintenance.Binds(intent.PressureMaintenance, command.Kind))
                    return RejectOperatorCommand(command, "MaintenanceEndAuthorityMismatch");
                ValidateNewOperatorCommand(current, command);
                RequireDesiredRun(current, command.SessionId, command.RunId, command.RunEpoch);
                var candidate = current.Clone(); intent = candidate.ActiveIntents.Single();
                if (!intent.PressureMaintenance.Revoked)
                    ClosePressureMaintenance(candidate, intent, SystemTerminalState.StoppedByOperator,
                        command.Kind == OperatorCommandKind.Stop ? "MaintenanceGlobalOperatorStop" : "MaintenancePageEnd", command);
                AddOperatorAdmission(candidate, command, intent, "OperatorStopJoinedMaintenanceOwner");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return FindOperatorAdmission(result.Document, command).Clone();
            }
            throw new InvalidOperationException("MaintenanceStopConflictLimitExceeded");
        }

        private void ClosePressureMaintenance(RecoveryKernelJournalDocument document, RecoveryIntent intent,
            SystemTerminalState exit, string reason, OperatorCommand stopping)
        {
            if (intent.PressureMaintenance.Revoked) return; // Never reset an already-running safety deadline.
            CompleteMaintenanceOperator(document, intent, false, reason);
            CompletePending(document);
            var lease = intent.PressureMaintenance;
            lease.Revoked = true; lease.Revision = checked(lease.Revision + 1); lease.ExitState = exit; lease.ExitReason = reason;
            lease.ExpiresUtcTicks = Math.Min(lease.ExpiresUtcTicks, Math.Max(lease.CreatedUtcTicks, DateTime.UtcNow.Ticks));
            intent.OperatorTransaction = stopping?.Clone();
            intent.Stage = RecoveryStage.PressureMaintenanceStopping;
            intent.AutomaticReplayForbidden = true;
            intent.DesiredTerminalState = exit;
            document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed,
                reason + ";IndependentSafetyPending", document.Revision + 1);
            document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs, document.Revision + 1, _policy.SafetyProofDeadline);
        }

        private static void CompleteMaintenanceOperator(RecoveryKernelJournalDocument document, RecoveryIntent intent, bool succeeded, string detail)
        {
            if (!PressureMaintenanceProtocol.IsOperation(intent.OperatorTransaction?.Kind ?? OperatorCommandKind.None)) return;
            var admission = FindOperatorAdmission(document, intent.OperatorTransaction);
            if (admission == null || admission.ExecutionCompleted) return;
            admission.ExecutionCompleted = true; admission.ExecutionSucceeded = succeeded; admission.ExecutionCompletedUtcTicks = DateTime.UtcNow.Ticks;
            admission.Detail = detail; admission.FailureCode = succeeded ? string.Empty : "MaintenanceExecutionFailed";
        }

        private string AcceptMaintenanceSafetyProof(RecoveryKernelJournalDocument document, RecoveryIntent intent, SafetyProof proof)
        {
            var lease = intent.PressureMaintenance;
            if (!proof.IsComplete)
            { EnterSafeIdle(document, intent, "MaintenanceSafetyProofIncomplete"); return "MaintenanceSafetyProofIncomplete"; }
            if (lease.Revoked)
            {
                CompleteMaintenanceOperator(document, intent, true, lease.ExitReason + ";IndependentSafetyComplete");
                CompleteMaintenanceClosureAdmissions(document, intent, true, lease.ExitReason + ";IndependentSafetyComplete");
                document.DesiredState = Desired(intent.Identity, lease.ExitState, lease.ExitReason, document.Revision + 1);
                document.ActiveIntents = Array.Empty<RecoveryIntent>();
                return "MaintenanceClosed;IndependentSafetyComplete";
            }
            if (!lease.IsLive(DateTime.UtcNow.Ticks))
            { EnterSafeIdle(document, intent, "MaintenanceExpiredBeforePreparation"); return "MaintenanceExpiredBeforePreparation"; }
            intent.Stage = RecoveryStage.PressureMaintenancePreparing;
            document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.PreparePressureMaintenance, document.Revision + 1, _policy.CommandDeadline);
            return "MaintenanceIndependentSafetyComplete;PrepareOnly";
        }

        private static void CompleteMaintenanceClosureAdmissions(RecoveryKernelJournalDocument document, RecoveryIntent intent, bool succeeded, string detail)
        {
            if (intent.PressureMaintenance == null) return;
            // Closing a page can join an already-revoked lease (fault, UI heartbeat
            // loss or global stop). Every joined End must receive a final result.
            foreach (var admission in document.OperatorAdmissions.Where(a => a.Accepted && !a.ExecutionCompleted &&
                a.MaintenanceTransaction?.Kind == OperatorCommandKind.EndPressureMaintenance && a.IncidentId == intent.Identity.IncidentId &&
                a.OwnerId == intent.OwnerId && a.SessionId == intent.Identity.SessionId && a.RunId == intent.Identity.RunId && a.RunEpoch == intent.Identity.RunEpoch))
            {
                admission.ExecutionCompleted = true; admission.ExecutionSucceeded = succeeded; admission.ExecutionCompletedUtcTicks = DateTime.UtcNow.Ticks;
                admission.Detail = detail; admission.FailureCode = succeeded ? string.Empty : "MaintenanceExecutionFailed";
            }
        }

        public RecoveryDecision AcknowledgePressureMaintenance(string incidentId, RecoveryCommandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var snapshot = _journal.Load();
            if (snapshot.CompletedIdempotencyKeys.Contains(receipt.CommandId) && snapshot.CompletedIdempotencyKeys.Contains(receipt.IdempotencyKey))
                return new RecoveryDecision { Duplicate = true, Command = snapshot.PendingCommand, DesiredState = snapshot.DesiredState,
                    Intent = snapshot.ActiveIntents.SingleOrDefault(i => i.Identity.IncidentId == incidentId), Reason = "MaintenanceReceiptAlreadyRetired" };
            return AcknowledgeCommandCore(incidentId, receipt.CommandId, receipt.Succeeded, receipt.Detail, receipt);
        }

        private static void ValidateMaintenanceReceipt(RecoveryIntent intent, RecoveryCommand command, RecoveryCommandReceipt receipt)
        {
            var execution = receipt?.PressureMaintenance; var lease = intent.PressureMaintenance; var now = DateTime.UtcNow.Ticks;
            var output = command.Kind == RecoveryCommandKind.SetMaintenancePressure;
            if (execution?.IsStructurallyValid() != true || receipt.IdempotencyKey != command.IdempotencyKey ||
                receipt.CompletedUtcTicks <= 0 || receipt.CompletedUtcTicks > now || now > command.DeadlineUtcTicks || !lease.IsLive(now) ||
                execution.EngineInstanceId != lease.EngineInstanceId || execution.HydraulicId != lease.HydraulicId ||
                execution.LeaseRevision < command.PressureMaintenance.Revision || execution.LeaseRevision > lease.Revision ||
                execution.LocalLeaseExpiresUtcTicks <= now || execution.LocalLeaseExpiresUtcTicks > lease.ExpiresUtcTicks ||
                execution.OutputActive != output || execution.CommandPressureBar != (output ? command.OperatorTransaction.PressureMaintenance.PressureBar : 0) ||
                receipt.FormalCyclesCompleted != 0 || receipt.QualificationCyclesCompleted != 0 || receipt.InterruptedCycleCounted)
                throw new InvalidOperationException("MaintenanceExecutionReceiptInvalid");
        }
    }
}
