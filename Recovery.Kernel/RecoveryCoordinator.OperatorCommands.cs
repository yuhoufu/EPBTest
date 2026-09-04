using System;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed partial class RecoveryCoordinator
    {
        public OperatorCommandAdmission QueryOperatorCommand(OperatorCommand command)
        {
            if (command?.IsStructurallyValid() != true) throw new ArgumentException("OperatorCommandInvalid");
            return FindOperatorAdmission(_journal.Load(), command)?.Clone();
        }

        public OperatorCommandAdmission AdmitOperatorCommand(OperatorCommand command, EngineStateSnapshot engine)
        {
            if (command?.IsStructurallyValid() != true) throw new ArgumentException("OperatorCommandInvalid");
            var existing = QueryOperatorCommand(command);
            if (existing != null) return existing;
            if (command.Kind == OperatorCommandKind.Stop)
            {
                var maintenanceStop = TryJoinPressureMaintenanceStop(command);
                if (maintenanceStop != null) return maintenanceStop;
                var joined = TryJoinProjectSwitchStop(command);
                if (joined != null) return joined;
            }
            try
            {
                if (engine?.IsStructurallyValid() != true || engine.SessionId != command.SessionId ||
                    engine.RunId != command.RunId || engine.RunEpoch != command.RunEpoch ||
                    (command.Kind == OperatorCommandKind.Stop ? command.BaseRevision > engine.Revision : engine.Revision != command.BaseRevision))
                    throw new InvalidOperationException("OperatorCommandEngineRevisionConflict");
                if (command.Kind == OperatorCommandKind.Start)
                {
                    if ((!engine.HardwareInitialized && !engine.HardwareRecompositionReady) || engine.OutputsEnergized ||
                        (engine.State != SystemTerminalState.StoppedByOperator && engine.State != SystemTerminalState.SafeIdleAlarmed) ||
                        !string.IsNullOrEmpty(engine.RecoveryIncidentId) || !string.IsNullOrEmpty(engine.RecoveryOwnerId))
                        throw new InvalidOperationException("OperatorStartRequiresReadyStoppedEngine");
                    StartByOperator(command.SessionId, command.RunId, command.RunEpoch,
                        "OperatorStart:" + command.CommandId, command);
                }
                else if (command.Kind == OperatorCommandKind.Stop)
                    StopByOperator(command.SessionId, command.RunId, command.RunEpoch,
                        "OperatorStop:" + command.CommandId, command);
                else if (command.Kind == OperatorCommandKind.CommitConfiguration)
                    AdmitConfiguration(command, engine);
                else if (PressureMaintenanceProtocol.IsOperation(command.Kind))
                    AdmitPressureMaintenance(command, engine);
                else if (command.Kind == OperatorCommandKind.SwitchProject)
                    AdmitProjectSwitch(command, engine);
                else if (command.Kind == OperatorCommandKind.RetryQualification)
                    AdmitQualificationRetry(command, engine);
                else if (ManualBatchCommand.IsOperation(command.Kind))
                    AdmitManualBatchCommand(command, engine);
                else if (AlarmPanelCommand.IsPanelOperation(command.Kind))
                    AdmitPanelCommand(command);
                else
                    throw new InvalidOperationException("OperatorCommandNotEnabled:" + command.Kind);
            }
            catch (InvalidOperationException ex)
            {
                // A failed acknowledgement after a durable commit must not replace the
                // accepted record with a rejection. Re-read the same transaction first.
                existing = QueryOperatorCommand(command);
                if (existing != null) return existing;
                return RejectOperatorCommand(command, ex.Message);
            }
            return QueryOperatorCommand(command) ?? throw new InvalidOperationException("OperatorAdmissionMissingAfterCommit");
        }

        private OperatorCommandAdmission RejectOperatorCommand(OperatorCommand command, string reason)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var existing = FindOperatorAdmission(current, command);
                if (existing != null) return existing.Clone();
                var candidate = current.Clone();
                var admission = NewAdmission(command, current.Revision + 1);
                admission.FailureCode = reason;
                admission.Detail = reason;
                AppendAdmission(candidate, admission);
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return admission.Clone();
            }
            throw new InvalidOperationException("OperatorRejectionJournalConflictLimitExceeded");
        }

        private void AdmitManualBatchCommand(OperatorCommand command, EngineStateSnapshot engine)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                var desired = current.DesiredState;
                if (desired == null || desired.SessionId != command.SessionId || desired.RunId != command.RunId ||
                    desired.RunEpoch != command.RunEpoch || engine.EngineInstanceId != command.ManualBatch.EngineInstanceId || !engine.HardwareInitialized)
                    throw new InvalidOperationException("ManualBatchRunIdentityConflict");
                var candidate = current.Clone();
                var intent = candidate.ActiveIntents.SingleOrDefault();
                var channelOperation = ManualBatchCommand.IsChannelOperation(command.Kind);
                var pause = ManualBatchCommand.IsPause(command.Kind);
                var bit = channelOperation ? 1 << (command.ManualBatch.Channel - 1) : 0;
                if (current.PendingCommand != null || current.ActiveIntents.Length > 1)
                    throw new InvalidOperationException("ManualControlBusy");
                if (intent == null)
                {
                    if (!pause ||
                        (desired.State != SystemTerminalState.Running && desired.State != SystemTerminalState.RunningDegraded) ||
                        engine.State != desired.State || !string.IsNullOrEmpty(engine.RecoveryOwnerId) || !string.IsNullOrEmpty(engine.RecoveryIncidentId) ||
                        !string.IsNullOrEmpty(command.ManualBatch.PauseOwnerId) || !string.IsNullOrEmpty(command.ManualBatch.PauseIncidentId))
                        throw new InvalidOperationException("ManualPauseRequiresHealthyRunningBatch");
                    var now = DateTime.UtcNow.Ticks;
                    intent = new RecoveryIntent
                    {
                        Identity = new RecoveryIdentity { SessionId = command.SessionId, RunId = command.RunId, RunEpoch = command.RunEpoch,
                            IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = current.Revision + 1 },
                        OwnerId = RecoveryProtocolV7.NewId(), Stage = RecoveryStage.OperatorPausePending,
                        DesiredTerminalState = desired.State, ManualBatchEngineInstanceId = engine.EngineInstanceId,
                        OperatorTransaction = command.Clone(), CreatedUtcTicks = now, UpdatedUtcTicks = now
                    };
                    candidate.ActiveIntents = new[] { intent };
                }
                else
                {
                    if ((intent.Stage != RecoveryStage.OperatorPaused && intent.Stage != RecoveryStage.OperatorChannelsHeld) ||
                        engine.State != desired.State ||
                        intent.ManualBatchEngineInstanceId != engine.EngineInstanceId || intent.OwnerId != command.ManualBatch.PauseOwnerId ||
                        intent.Identity.IncidentId != command.ManualBatch.PauseIncidentId || engine.RecoveryOwnerId != intent.OwnerId ||
                        engine.RecoveryIncidentId != intent.Identity.IncidentId || intent.AutomaticReplayForbidden)
                        throw new InvalidOperationException("ManualResumeRequiresExactPausedOwner");
                }
                if (channelOperation)
                {
                    if (intent.Stage == RecoveryStage.OperatorPaused ||
                        (pause ? (engine.ChannelPauseMask & bit) == 0 || (intent.ManualPausedChannelsMask & bit) != 0 :
                            (engine.ChannelResumeMask & bit) == 0 || (intent.ManualPausedChannelsMask & bit) == 0))
                        throw new InvalidOperationException("ManualChannelStateNotEligible");
                    intent.ManualRunningChannelsMask = pause ? engine.ChannelPauseMask & ~bit : engine.ChannelPauseMask | bit;
                }
                else if (!pause && (intent.Stage != RecoveryStage.OperatorPaused ||
                    desired.State != SystemTerminalState.StoppedByOperator || engine.OutputsEnergized))
                    throw new InvalidOperationException("ManualResumeRequiresCompletedBatchPause");
                else if (pause && intent.Stage == RecoveryStage.OperatorPaused)
                    throw new InvalidOperationException("ManualBatchAlreadyPaused");
                intent.OperatorTransaction = command.Clone();
                intent.Stage = pause ? RecoveryStage.OperatorPausePending : RecoveryStage.OperatorResumeChecking;
                intent.Identity.Revision = current.Revision + 1;
                intent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                candidate.PendingCommand = BuildCommand(intent, channelOperation
                    ? (pause ? RecoveryCommandKind.PauseChannelGracefully : RecoveryCommandKind.ResumePausedChannel)
                    : (pause ? RecoveryCommandKind.PauseBatchGracefully : RecoveryCommandKind.ResumePausedBatch),
                    current.Revision + 1, pause ? TimeSpan.FromMinutes(5) : _policy.CommandDeadline);
                AddOperatorAdmission(candidate, command, intent, pause ? "ManualPauseCommitted" : "ManualResumeCommitted");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("ManualBatchJournalConflictLimitExceeded");
        }

        private void AdmitQualificationRetry(OperatorCommand command, EngineStateSnapshot engine)
        {
            var scope = "Channel:" + command.ManualBatch.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                var desired = current.DesiredState;
                var budget = (current.Budgets ?? Array.Empty<RecoveryBudgetState>()).FirstOrDefault(value =>
                    string.Equals(value.ResourceScope, scope, StringComparison.OrdinalIgnoreCase));
                if (desired == null || desired.SessionId != command.SessionId || desired.RunId != command.RunId ||
                    desired.RunEpoch != command.RunEpoch || engine.EngineInstanceId != command.ManualBatch.EngineInstanceId ||
                    engine.State != desired.State || (desired.State != SystemTerminalState.RunningDegraded &&
                    desired.State != SystemTerminalState.SafeIdleAlarmed) || !engine.HardwareInitialized ||
                    !string.IsNullOrEmpty(engine.RecoveryOwnerId) || !string.IsNullOrEmpty(engine.RecoveryIncidentId) ||
                    current.ActiveIntents.Length != 0 || current.PendingCommand != null)
                    throw new InvalidOperationException("QualificationRetryRequiresIdleOwnedRun");
                if (!(current.IsolatedResources ?? Array.Empty<string>()).Contains(scope, StringComparer.OrdinalIgnoreCase) ||
                    !(engine.IsolatedResources ?? Array.Empty<string>()).Contains(scope, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("QualificationRetryRequiresExactChannelIsolation");
                if ((budget?.ActiveQualificationAttempts ?? 0) >= _policy.MaximumConfirmedHardwareQualificationAttempts)
                    throw new InvalidOperationException("QualificationRetryBudgetExhausted");

                var candidate = current.Clone();
                var retryBudget = GetBudget(candidate, scope);
                retryBudget.ActiveQualificationAttempts++;
                var now = DateTime.UtcNow.Ticks;
                var identity = new RecoveryIdentity
                {
                    SessionId = command.SessionId, RunId = command.RunId, RunEpoch = command.RunEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = scope, Generation = 1,
                    Revision = current.Revision + 1
                };
                var remainingIsolations = candidate.IsolatedResources.Count(value =>
                    !string.Equals(value, scope, StringComparison.OrdinalIgnoreCase));
                var intent = new RecoveryIntent
                {
                    Identity = identity, OwnerId = RecoveryProtocolV7.NewId(), Stage = RecoveryStage.AwaitingSafetyProof,
                    DesiredTerminalState = remainingIsolations == 0 ? SystemTerminalState.Running : SystemTerminalState.RunningDegraded,
                    HardwareConfirmed = true, ActiveQualificationAttempts = retryBudget.ActiveQualificationAttempts,
                    OperatorTransaction = command.Clone(), CreatedUtcTicks = now, UpdatedUtcTicks = now
                };
                candidate.ActiveIntents = new[] { intent };
                candidate.DesiredState = Desired(identity, SystemTerminalState.SafeIdleAlarmed,
                    "OperatorQualificationRetrySafetyProofPending:" + scope, current.Revision + 1);
                candidate.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs,
                    current.Revision + 1, _policy.SafetyProofDeadline);
                AddOperatorAdmission(candidate, command, intent, "QualificationRetryCommitted");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("QualificationRetryJournalConflictLimitExceeded");
        }

        public string[] QualificationRetryEligibleScopes()
        {
            var current = _journal.Load();
            if (current.ActiveIntents.Length != 0 || current.PendingCommand != null) return Array.Empty<string>();
            return (current.IsolatedResources ?? Array.Empty<string>()).Where(scope =>
            {
                var parts = scope.Split(':');
                if (parts.Length != 2 || !string.Equals(parts[0], "Channel", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(parts[1], out var channel) || channel < 1 || channel > 12 ||
                    parts[1] != channel.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
                var budget = (current.Budgets ?? Array.Empty<RecoveryBudgetState>()).FirstOrDefault(value =>
                    string.Equals(value.ResourceScope, scope, StringComparison.OrdinalIgnoreCase));
                return (budget?.ActiveQualificationAttempts ?? 0) < _policy.MaximumConfirmedHardwareQualificationAttempts;
            }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void AdmitPanelCommand(OperatorCommand command)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                var desired = current.DesiredState;
                if (desired == null || desired.SessionId != command.SessionId || desired.RunId != command.RunId || desired.RunEpoch != command.RunEpoch)
                    throw new InvalidOperationException("AlarmPanelRunIdentityConflict");
                if (current.OperatorAdmissions.Count(a => a.PanelTransaction != null && !a.ExecutionCompleted) >= 16)
                    throw new InvalidOperationException("AlarmPanelQueueFull");
                var candidate = current.Clone();
                var admission = NewAdmission(command, current.Revision + 1);
                admission.Accepted = true;
                admission.PanelTransaction = command.Clone();
                admission.DesiredState = desired.State;
                admission.Detail = "AlarmPanelCommandAdmitted;NoRecoveryOwnerCreated";
                AppendAdmission(candidate, admission);
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("AlarmPanelJournalConflictLimitExceeded");
        }

        public OperatorCommandAdmission CompletePanelCommand(OperatorCommand command, OperatorExecutionReceipt receipt)
        {
            if (command?.IsStructurallyValid() != true || !AlarmPanelCommand.IsPanelOperation(command.Kind) || receipt?.Matches(command) != true)
                throw new ArgumentException("AlarmPanelReceiptInvalid");
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var existing = FindOperatorAdmission(current, command);
                if (existing?.PanelTransaction == null) throw new InvalidOperationException("AlarmPanelAdmissionMissing");
                if (existing.ExecutionCompleted) return existing.Clone();
                var candidate = current.Clone();
                var admission = FindOperatorAdmission(candidate, command);
                admission.ExecutionCompleted = true;
                admission.ExecutionSucceeded = receipt.Succeeded;
                admission.ExecutionCompletedUtcTicks = receipt.CompletedUtcTicks;
                admission.Detail = receipt.Detail;
                admission.FailureCode = receipt.Succeeded ? string.Empty : "AlarmPanelExecutionFailed";
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return admission.Clone();
            }
            throw new InvalidOperationException("AlarmPanelCompletionConflictLimitExceeded");
        }

        private void AdmitConfiguration(OperatorCommand command, EngineStateSnapshot engine)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if (FindOperatorAdmission(current, command) != null) return;
                ValidateNewOperatorCommand(current, command);
                var desired = current.DesiredState;
                if (desired == null || desired.SessionId != command.SessionId || desired.RunId != command.RunId ||
                    desired.RunEpoch != command.RunEpoch || desired.State != SystemTerminalState.StoppedByOperator ||
                    current.ActiveIntents.Length != 0 || current.PendingCommand != null || engine.OutputsEnergized ||
                    engine.State != SystemTerminalState.StoppedByOperator || (!engine.HardwareInitialized && !engine.HardwareRecompositionReady) ||
                    !string.IsNullOrEmpty(engine.RecoveryIncidentId) || !string.IsNullOrEmpty(engine.RecoveryOwnerId))
                    throw new InvalidOperationException("ConfigurationRequiresCompletedOperatorStop");
                var identity = new RecoveryIdentity
                {
                    SessionId = command.SessionId, RunId = command.RunId, RunEpoch = command.RunEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = current.Revision + 1
                };
                var now = DateTime.UtcNow.Ticks;
                var intent = new RecoveryIntent
                {
                    Identity = identity, OwnerId = RecoveryProtocolV7.NewId(), Stage = RecoveryStage.AwaitingSafetyProof,
                    DesiredTerminalState = SystemTerminalState.StoppedByOperator, OperatorTransaction = command.Clone(),
                    CreatedUtcTicks = now, UpdatedUtcTicks = now
                };
                var candidate = current.Clone();
                candidate.ActiveIntents = new[] { intent };
                candidate.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs, current.Revision + 1, _policy.SafetyProofDeadline);
                AddOperatorAdmission(candidate, command, intent, "ConfigurationTransactionCommitted");
                var result = _journal.CompareExchange(current.Revision, candidate);
                if (result.Conflict) continue;
                if (!result.Committed) throw new InvalidOperationException(result.Reason);
                return;
            }
            throw new InvalidOperationException("ConfigurationJournalConflictLimitExceeded");
        }

        private static OperatorCommandAdmission FindOperatorAdmission(RecoveryKernelJournalDocument document, OperatorCommand command)
        {
            if (command == null) return null; // Retained pure model API, not a production command path.
            var admission = (document.OperatorAdmissions ?? Array.Empty<OperatorCommandAdmission>())
                .FirstOrDefault(value => value.CommandId == command.CommandId);
            if (admission != null && !admission.Matches(command))
                throw new InvalidOperationException("OperatorCommandIdPayloadConflict");
            return admission;
        }

        private static void ValidateNewOperatorCommand(RecoveryKernelJournalDocument document, OperatorCommand command)
        {
            if (command == null) return;
            var now = DateTime.UtcNow.Ticks;
            if (!command.IsStructurallyValid() || command.IssuedUtcTicks <= document.OperatorReplayFloorUtcTicks ||
                command.IssuedUtcTicks > now + TimeSpan.FromSeconds(5).Ticks ||
                command.IssuedUtcTicks < now - TimeSpan.FromMinutes(5).Ticks)
                throw new InvalidOperationException("OperatorCommandExpiredOrRetired");
        }

        private static OperatorCommandAdmission NewAdmission(OperatorCommand command, long revision) => new OperatorCommandAdmission
        {
            CommandId = command.CommandId, Fingerprint = OperatorCommandAdmission.GetFingerprint(command),
            SessionId = command.SessionId, RunId = command.RunId, RunEpoch = command.RunEpoch,
            IssuedUtcTicks = command.IssuedUtcTicks, CommittedRevision = revision
        };

        private static void AddOperatorAdmission(RecoveryKernelJournalDocument document, OperatorCommand command,
            RecoveryIntent intent, string detail)
        {
            if (command == null) return;
            var admission = NewAdmission(command, document.Revision + 1);
            admission.Accepted = true;
            admission.IncidentId = intent.Identity.IncidentId;
            admission.OwnerId = intent.OwnerId;
            admission.DesiredState = intent.DesiredTerminalState;
            admission.Detail = detail;
            if (command.Kind == OperatorCommandKind.CommitConfiguration) admission.ConfigurationTransaction = command.Clone();
            if (PressureMaintenanceProtocol.IsOperation(command.Kind)) admission.MaintenanceTransaction = command.Clone();
            if (command.Kind == OperatorCommandKind.SwitchProject)
            {
                admission.ProjectTransaction = command.Clone();
                admission.DestinationRunId = intent.ProjectSwitch.NextRunId;
                admission.DestinationRunEpoch = intent.ProjectSwitch.NextRunEpoch;
            }
            AppendAdmission(document, admission);
        }

        private static void AppendAdmission(RecoveryKernelJournalDocument document, OperatorCommandAdmission admission)
        {
            var entries = (document.OperatorAdmissions ?? Array.Empty<OperatorCommandAdmission>()).Append(admission).ToArray();
            if (entries.Length > 1024)
            {
                // Pending panel operations remain replayable until their bounded deadline
                // is reconciled; admission pressure must never silently discard execution.
                var retired = entries.Where(a => a.PanelTransaction == null && a.ProjectTransaction == null && a.ConfigurationTransaction == null &&
                    a.MaintenanceTransaction == null || a.ExecutionCompleted).Take(entries.Length - 1024).ToArray();
                if (retired.Length != entries.Length - 1024) throw new InvalidOperationException("OperatorAdmissionCapacityExceeded");
                document.OperatorReplayFloorUtcTicks = Math.Max(document.OperatorReplayFloorUtcTicks,
                    retired.Max(value => value.IssuedUtcTicks));
                var retiredIds = retired.Select(a => a.CommandId).ToArray();
                entries = entries.Where(a => !retiredIds.Contains(a.CommandId)).ToArray();
            }
            document.OperatorAdmissions = entries;
        }

        private static void CompleteConfiguration(RecoveryKernelJournalDocument document, RecoveryIntent intent, bool succeeded, string detail)
        {
            var command = intent.OperatorTransaction;
            if (command?.Kind != OperatorCommandKind.CommitConfiguration) return;
            var admission = FindOperatorAdmission(document, command);
            if (admission == null || admission.ExecutionCompleted) return;
            admission.ConfigurationTransaction = command.Clone();
            admission.ExecutionCompleted = true; admission.ExecutionSucceeded = succeeded;
            admission.ExecutionCompletedUtcTicks = DateTime.UtcNow.Ticks;
            admission.Detail = detail; admission.FailureCode = succeeded ? string.Empty : "ConfigurationExecutionFailed";
        }

        private static RecoveryDecision DecisionFromAdmission(OperatorCommandAdmission admission)
        {
            if (!admission.Accepted) throw new InvalidOperationException(admission.FailureCode);
            return new RecoveryDecision
            {
                Duplicate = true, Reason = admission.Detail,
                Intent = new RecoveryIntent
                {
                    Identity = new RecoveryIdentity
                    {
                        SessionId = admission.SessionId, RunId = admission.RunId, RunEpoch = admission.RunEpoch,
                        IncidentId = admission.IncidentId, ResourceScope = "System", Generation = 1, Revision = admission.CommittedRevision
                    },
                    OwnerId = admission.OwnerId, DesiredTerminalState = admission.DesiredState
                },
                DesiredState = new SystemDesiredState
                {
                    SessionId = admission.SessionId, RunId = admission.RunId, RunEpoch = admission.RunEpoch,
                    Revision = admission.CommittedRevision, State = admission.DesiredState
                }
            };
        }
    }
}
