using System;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed partial class RecoveryCoordinator
    {
        private void AdmitProjectSwitch(OperatorCommand command, EngineStateSnapshot engine)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                RequireDesiredRun(current, command.SessionId, command.RunId, command.RunEpoch);
                if (current.DesiredState?.State != SystemTerminalState.StoppedByOperator ||
                    current.PendingCommand != null || current.ActiveIntents.Length != 0 ||
                    engine.State != SystemTerminalState.StoppedByOperator || engine.OutputsEnergized ||
                    (!engine.HardwareInitialized && !engine.HardwareRecompositionReady) ||
                    !string.IsNullOrEmpty(engine.RecoveryOwnerId) || !string.IsNullOrEmpty(engine.RecoveryIncidentId) ||
                    engine.EngineInstanceId != command.ProjectSwitch.EngineInstanceId || command.RunEpoch == long.MaxValue)
                    throw new InvalidOperationException("ProjectSwitchRequiresCompletedOperatorStop");
                var now = DateTime.UtcNow.Ticks;
                var intent = new RecoveryIntent
                {
                    Identity = new RecoveryIdentity { SessionId = command.SessionId, RunId = command.RunId,
                        RunEpoch = command.RunEpoch, IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System",
                        Generation = 1, Revision = current.Revision + 1 },
                    OwnerId = RecoveryProtocolV7.NewId(), Stage = RecoveryStage.AwaitingSafetyProof,
                    DesiredTerminalState = SystemTerminalState.StoppedByOperator, OperatorTransaction = command.Clone(),
                    CreatedUtcTicks = now, UpdatedUtcTicks = now
                };
                var request = command.ProjectSwitch;
                intent.ProjectSwitch = new ProjectSwitchPlan
                {
                    OperatorCommandId = command.CommandId, OwnerId = intent.OwnerId, SourceIdentity = intent.Identity.Clone(),
                    SourceEngineInstanceId = request.EngineInstanceId, NextRunId = RecoveryProtocolV7.NewId(),
                    NextRunEpoch = command.RunEpoch + 1, BaseSelectionRevision = request.BaseSelectionRevision,
                    SourceProjectFileSha256 = request.SourceProjectFileSha256, TargetConfigurationPath = request.TargetConfigurationPath,
                    TargetProjectFileSha256 = request.TargetProjectFileSha256, BaseConfigurationRevision = request.BaseConfigurationRevision,
                    Creation = request.Creation?.Clone(),
                    Reset = request.Reset?.Clone(),
                    BaseConfigurationSha256 = request.BaseConfigurationSha256, IsolatedResources = current.IsolatedResources.ToArray()
                };
                var candidate = current.Clone();
                candidate.ActiveIntents = new[] { intent };
                candidate.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs, current.Revision + 1, _policy.SafetyProofDeadline);
                AddOperatorAdmission(candidate, command, intent, "ProjectSwitchAdmitted;SourceRunRetainedUntilPrepared");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("ProjectSwitchAdmissionConflictLimitExceeded");
        }

        public RecoveryDecision AcknowledgeProjectSwitch(string incidentId, RecoveryCommandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            return AcknowledgeCommandCore(incidentId, receipt.CommandId, receipt.Succeeded, receipt.Detail, receipt);
        }

        private static void ValidateProjectSwitchReceipt(RecoveryCommand command, RecoveryCommandReceipt receipt)
        {
            if (receipt?.Succeeded != true || receipt.SchemaVersion != EngineHostProtocol.SchemaVersion || receipt.ProjectSwitch?.Matches(command) != true ||
                receipt.CommandId != command.CommandId || receipt.IdempotencyKey != command.IdempotencyKey ||
                receipt.CompletedUtcTicks <= 0 || receipt.CompletedUtcTicks > DateTime.UtcNow.Ticks ||
                DateTime.UtcNow.Ticks > command.DeadlineUtcTicks ||
                !receipt.DataBoundaryClosed || !receipt.ExecutionAuthorizationRevoked || receipt.InterruptedCycleCounted)
                throw new InvalidOperationException("ProjectSwitchBoundReceiptRequired");
        }

        private void AdvanceProjectSwitch(RecoveryKernelJournalDocument document, RecoveryIntent intent,
            RecoveryCommand command, RecoveryCommandReceipt receipt)
        {
            ValidateProjectSwitchReceipt(command, receipt);
            if (command.Kind == RecoveryCommandKind.PrepareProjectSwitch)
            {
                intent.ProjectSwitchPreparedSha256 = receipt.ProjectSwitch.PreparedDocumentSha256;
                // The new desired identity and its sole command/Owner commit together.
                // No process may infer this transition from files or UI selection alone.
                intent.Identity.RunId = intent.ProjectSwitch.NextRunId;
                intent.Identity.RunEpoch = intent.ProjectSwitch.NextRunEpoch;
                intent.Stage = RecoveryStage.ProjectSwitchActivating;
                document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed,
                    "ProjectSwitchPrepared;DestinationActivationPending", document.Revision + 1);
                document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.ActivateProjectSwitch,
                    document.Revision + 1, _policy.CommandDeadline);
            }
            else
            {
                intent.Stage = RecoveryStage.ProjectSwitchStopping;
                document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.StopByOperator,
                    document.Revision + 1, _policy.SafetyProofDeadline);
            }
        }

        private void FailProjectSwitch(RecoveryKernelJournalDocument document, RecoveryIntent intent, string reason)
        {
            if (string.IsNullOrEmpty(intent.ProjectSwitchFailure)) intent.ProjectSwitchFailure = reason;
            intent.AutomaticReplayForbidden = true;
            intent.Stage = RecoveryStage.AwaitingSafetyProof;
            document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed,
                "ProjectSwitchFailed;SafetyBoundaryPending:" + reason, document.Revision + 1);
            document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs,
                document.Revision + 1, _policy.SafetyProofDeadline);
        }

        private static void CompleteProjectSwitch(RecoveryKernelJournalDocument document, RecoveryIntent intent, bool stopped, string reason)
        {
            if (intent.ProjectSwitch == null) return;
            var admission = FindOperatorAdmission(document, intent.OperatorTransaction);
            if (admission == null || admission.ExecutionCompleted) return;
            admission.ExecutionCompleted = true;
            admission.ExecutionCompletedUtcTicks = DateTime.UtcNow.Ticks;
            admission.ExecutionSucceeded = stopped && intent.ProjectSwitch.MatchesDestination(intent.Identity) &&
                string.IsNullOrEmpty(intent.ProjectSwitchFailure) && !intent.ProjectSwitchStopRequested;
            admission.FailureCode = admission.ExecutionSucceeded ? string.Empty :
                !string.IsNullOrEmpty(intent.ProjectSwitchFailure) ? intent.ProjectSwitchFailure :
                intent.ProjectSwitchStopRequested ? "ProjectSwitchStoppedByOperator" : "ProjectSwitchIncomplete";
            admission.Detail = admission.ExecutionSucceeded ? "ProjectSwitched;Stopped;NoAutomaticStart" : reason;
        }

        // A project handoff deliberately has a period with no EngineHost pipe.
        // A stop joins only its durable source/destination identity; it never
        // needs to invent a snapshot or a second recovery owner during that gap.
        public OperatorCommandAdmission TryJoinProjectSwitchStop(OperatorCommand command)
        {
            if (command?.IsStructurallyValid() != true || command.Kind != OperatorCommandKind.Stop)
                throw new ArgumentException("ProjectSwitchStopCommandInvalid");
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var current = _journal.Load();
                var previous = FindOperatorAdmission(current, command);
                if (previous != null) return previous.Clone();
                var project = current.ActiveIntents.SingleOrDefault(value => value.ProjectSwitch != null);
                if (project == null) return null;
                try
                {
                    if (JoinProjectSwitchStop(current, project, command.SessionId, command.RunId, command.RunEpoch, command) != null)
                        return QueryOperatorCommand(command);
                }
                catch (InvalidOperationException ex) { return RejectOperatorCommand(command, ex.Message); }
            }
            throw new InvalidOperationException("ProjectSwitchStopJournalBusy");
        }

        private RecoveryDecision JoinProjectSwitchStop(RecoveryKernelJournalDocument current, RecoveryIntent project,
            string sessionId, string runId, long runEpoch, OperatorCommand command)
        {
            var source = project.ProjectSwitch.SourceIdentity;
            if (sessionId != source.SessionId || !((runId == source.RunId && runEpoch == source.RunEpoch) ||
                (runId == project.ProjectSwitch.NextRunId && runEpoch == project.ProjectSwitch.NextRunEpoch)))
                throw new InvalidOperationException("ProjectSwitchStopRunConflict");
            var candidate = current.Clone();
            var intent = candidate.ActiveIntents.Single();
            // Both projects are already stopped. Joining never cancels an in-flight
            // durable activation halfway or creates a second Owner. No start is issued.
            intent.ProjectSwitchStopRequested = true;
            intent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            AddOperatorAdmission(candidate, command, intent, "ProjectSwitchStopJoined;NoAutomaticStart");
            var result = _journal.CompareExchange(current.Revision, candidate);
            if (result.Conflict) return null;
            if (!result.Committed) throw new InvalidOperationException(result.Reason);
            return new RecoveryDecision { Intent = result.Document.ActiveIntents.Single(), Command = result.Document.PendingCommand,
                DesiredState = result.Document.DesiredState, Reason = "ProjectSwitchStopJoined" };
        }
    }
}
