using System;
using System.Collections.Generic;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    public sealed class RecoveryKernelPolicy
    {
        public int MaximumLocalRebuilds { get; set; } = 2;
        public int MaximumCurrentEngineReplacements { get; set; } = 2;
        public int MaximumLastKnownGoodActivations { get; set; } = 1;
        public int MaximumConfirmedHardwareQualificationAttempts { get; set; } = 1;
        public TimeSpan CommandDeadline { get; set; } = TimeSpan.FromSeconds(180);
        public TimeSpan SafetyProofDeadline { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class RecoveryDecision
    {
        public RecoveryIntent Intent { get; set; }
        public RecoveryCommand Command { get; set; }
        public SystemDesiredState DesiredState { get; set; }
        public bool Duplicate { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pure, single-writer coordinator.  Every public mutation is a durable
    /// compare-and-swap.  The intent and Owner are committed before the first
    /// safety or recovery command becomes observable.
    /// </summary>
    public sealed partial class RecoveryCoordinator
    {
        private readonly IRecoveryKernelJournal _journal;
        private readonly RecoveryResourceGraph _graph;
        private readonly RecoveryKernelPolicy _policy;

        public RecoveryCoordinator(
            IRecoveryKernelJournal journal,
            RecoveryResourceGraph graph = null,
            RecoveryKernelPolicy policy = null)
        {
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _graph = graph ?? RecoveryResourceGraph.CreateDefaultEpbGraph();
            _policy = policy ?? new RecoveryKernelPolicy();
        }

        public static RecoveryIdentity CreateBootstrapIdentity(
            string sessionId,
            long runEpoch,
            string resourceScope = "System")
        {
            if (!RecoveryProtocolV7.IsGuid(sessionId) || runEpoch <= 0)
                throw new ArgumentException("BootstrapIdentityInvalid");
            return new RecoveryIdentity
            {
                SessionId = sessionId,
                RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = runEpoch,
                IncidentId = RecoveryProtocolV7.NewId(),
                ResourceScope = resourceScope,
                Generation = 1,
                Revision = 1
            };
        }

        public RecoveryKernelJournalDocument Snapshot()
        {
            return _journal.Load();
        }

        public bool AdmitSafeIdleEngine(EngineStateSnapshot snapshot)
        {
            if (snapshot?.IsStructurallyValid() != true ||
                !snapshot.HardwareInitialized ||
                snapshot.State != SystemTerminalState.SafeIdleAlarmed)
                return false;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                // Discovery is not a project/run-switch transaction. Even a new
                // healthy process cannot erase a durable owner, budget or session.
                if (current.DesiredState != null || current.ActiveIntents.Length != 0 || current.PendingCommand != null)
                    return false;
                var candidate = current.Clone();
                candidate.ActiveIntents = Array.Empty<RecoveryIntent>();
                candidate.PendingCommand = null;
                candidate.IsolatedResources = (snapshot.IsolatedResources ?? Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                candidate.DesiredState = new SystemDesiredState
                {
                    SessionId = snapshot.SessionId,
                    RunId = snapshot.RunId,
                    RunEpoch = snapshot.RunEpoch,
                    Revision = current.Revision + 1,
                    State = SystemTerminalState.SafeIdleAlarmed,
                    Reason = "EngineHostSafeIdleAdmitted",
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed)
                    throw new InvalidOperationException(commit.Reason);
                return true;
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        private static void RequireDesiredRun(RecoveryKernelJournalDocument document, string sessionId, string runId, long runEpoch)
        {
            var desired = document.DesiredState;
            if (desired != null && (desired.SessionId != sessionId || desired.RunId != runId || desired.RunEpoch != runEpoch))
                throw new InvalidOperationException("RecoverySessionChangeRequiresExplicitTransaction");
        }

        public RecoveryDecision Observe(FaultObservation observation)
        {
            if (observation?.IsStructurallyValid() != true)
                throw new ArgumentException("FaultObservationInvalid", nameof(observation));
            var scope = _graph.Resolve(observation);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                RequireDesiredRun(current, observation.Identity.SessionId, observation.Identity.RunId, observation.Identity.RunEpoch);
                var existing = (current.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                    .FirstOrDefault(item => string.Equals(
                        item.Identity.IncidentId,
                        observation.Identity.IncidentId,
                        StringComparison.Ordinal));
                if (existing != null)
                    return new RecoveryDecision
                    {
                        Intent = RecoveryKernelJournalDocument.CloneIntent(existing),
                        Command = RecoveryKernelJournalDocument.CloneCommand(current.PendingCommand),
                        DesiredState = current.DesiredState,
                        Duplicate = true,
                        Reason = "ObservationAlreadyOwned"
                    };

                // The journal intentionally exposes one executable command at
                // a time. A second incident must never overwrite the first
                // owner's PendingCommand. Same-scope observations are folded;
                // a different scope invalidates the earlier minimal-domain
                // proof and atomically expands that owner to System.
                existing = (current.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                    .FirstOrDefault();
                if (existing != null)
                {
                    if (existing.ProjectSwitch != null)
                    {
                        if (!string.IsNullOrEmpty(existing.ProjectSwitchFailure))
                            return new RecoveryDecision { Intent = existing, Command = current.PendingCommand,
                                DesiredState = current.DesiredState, Duplicate = true, Reason = "ProjectSwitchFailureAlreadyOwned" };
                        var interrupted = current.Clone();
                        var project = interrupted.ActiveIntents.Single();
                        CompletePending(interrupted);
                        FailProjectSwitch(interrupted, project, "ProjectSwitchInterruptedByFault:" + observation.ObservationId);
                        var result = _journal.CompareExchange(current.Revision, interrupted);
                        if (result.Conflict) continue;
                        if (!result.Committed) throw new InvalidOperationException(result.Reason);
                        return new RecoveryDecision { Intent = result.Document.ActiveIntents.Single(), Command = result.Document.PendingCommand,
                            DesiredState = result.Document.DesiredState, Reason = "ProjectSwitchInterruptedByFault" };
                    }
                    if (existing.PressureMaintenance != null)
                    {
                        if (existing.PressureMaintenance.Revoked)
                            return new RecoveryDecision { Duplicate = true, Intent = existing, Command = current.PendingCommand,
                                DesiredState = current.DesiredState, Reason = "MaintenanceAlreadyClosing" };
                        var interrupted = current.Clone();
                        ClosePressureMaintenance(interrupted, interrupted.ActiveIntents.Single(), SystemTerminalState.SafeIdleAlarmed,
                            "MaintenanceInterruptedByFault:" + observation.ObservationId, null);
                        var result = _journal.CompareExchange(current.Revision, interrupted);
                        if (result.Conflict) continue;
                        if (!result.Committed) throw new InvalidOperationException(result.Reason);
                        return new RecoveryDecision { Intent = result.Document.ActiveIntents.Single(), Command = result.Document.PendingCommand,
                            DesiredState = result.Document.DesiredState, Reason = "MaintenanceInterruptedByFault" };
                    }
                    if (existing.OperatorTransaction != null)
                    {
                        var interrupted = current.Clone();
                        var interruptedIntent = interrupted.ActiveIntents.First();
                        CompleteConfiguration(interrupted, interruptedIntent, false, "ConfigurationInterruptedByFault");
                        interruptedIntent.OperatorTransaction = null;
                        interruptedIntent.AutomaticReplayForbidden = true;
                        interruptedIntent.Stage = RecoveryStage.AwaitingSafetyProof;
                        interruptedIntent.DesiredTerminalState = SystemTerminalState.SafeIdleAlarmed;
                        interruptedIntent.Identity.Generation++;
                        interruptedIntent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                        interrupted.PendingCommand = BuildCommand(interruptedIntent, RecoveryCommandKind.DisableOutputs,
                            current.Revision + 1, _policy.SafetyProofDeadline);
                        var result = _journal.CompareExchange(current.Revision, interrupted);
                        if (result.Conflict) continue;
                        if (!result.Committed) throw new InvalidOperationException(result.Reason);
                        return new RecoveryDecision { Intent = result.Document.ActiveIntents.First(), Command = result.Document.PendingCommand,
                            DesiredState = result.Document.DesiredState, Reason = existing.OperatorTransaction.Kind == OperatorCommandKind.CommitConfiguration
                                ? "ConfigurationInterruptedByFault" : "ManualControlInterruptedByFault" };
                    }
                    if (string.Equals(existing.Identity.ResourceScope,
                            scope.Scope, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.Identity.ResourceScope,
                            "System", StringComparison.OrdinalIgnoreCase))
                    {
                        if ((observation.HardwareConfirmed && !existing.HardwareConfirmed) ||
                            (!observation.SafetyChainHealthy &&
                             !existing.AutomaticReplayForbidden))
                        {
                            var folded = current.Clone();
                            var foldedIntent = folded.ActiveIntents.First();
                            foldedIntent.HardwareConfirmed |=
                                observation.HardwareConfirmed;
                            foldedIntent.AutomaticReplayForbidden |=
                                !observation.SafetyChainHealthy;
                            foldedIntent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                            foldedIntent.Identity.Revision = current.Revision + 1;
                            var foldedCommit = _journal.CompareExchange(
                                current.Revision, folded);
                            if (foldedCommit.Conflict) continue;
                            if (!foldedCommit.Committed)
                                throw new InvalidOperationException(
                                    foldedCommit.Reason);
                            return new RecoveryDecision
                            {
                                Intent = FindIntent(
                                    foldedCommit.Document,
                                    foldedIntent.Identity.IncidentId),
                                Command = foldedCommit.Document.PendingCommand,
                                DesiredState = foldedCommit.Document.DesiredState,
                                Duplicate = true,
                                Reason = "FaultFactsFoldedIntoExistingOwner"
                            };
                        }
                        return new RecoveryDecision
                        {
                            Intent = RecoveryKernelJournalDocument.CloneIntent(existing),
                            Command = RecoveryKernelJournalDocument.CloneCommand(current.PendingCommand),
                            DesiredState = current.DesiredState,
                            Duplicate = true,
                            Reason = "FaultFoldedIntoExistingOwner"
                        };
                    }

                    var expanded = current.Clone();
                    var expandedIntent = expanded.ActiveIntents.First();
                    expandedIntent.Identity.ResourceScope = "System";
                    expandedIntent.Identity.Generation++;
                    expandedIntent.Identity.Revision = current.Revision + 1;
                    expandedIntent.Stage = RecoveryStage.AwaitingSafetyProof;
                    expandedIntent.DesiredTerminalState = SystemTerminalState.Running;
                    expandedIntent.AutomaticReplayForbidden |=
                        !observation.SafetyChainHealthy;
                    expandedIntent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    expanded.DesiredState = Desired(
                        expandedIntent.Identity,
                        SystemTerminalState.SafeIdleAlarmed,
                        "ConcurrentFaultScopeExpandedToSystem",
                        current.Revision + 1);
                    expanded.PendingCommand = BuildCommand(
                        expandedIntent,
                        RecoveryCommandKind.DisableOutputs,
                        current.Revision + 1,
                        _policy.SafetyProofDeadline);
                    var expandedCommit = _journal.CompareExchange(
                        current.Revision, expanded);
                    if (expandedCommit.Conflict) continue;
                    if (!expandedCommit.Committed)
                        throw new InvalidOperationException(expandedCommit.Reason);
                    return new RecoveryDecision
                    {
                        Intent = FindIntent(
                            expandedCommit.Document,
                            expandedIntent.Identity.IncidentId),
                        Command = expandedCommit.Document.PendingCommand,
                        DesiredState = expandedCommit.Document.DesiredState,
                        Duplicate = true,
                        Reason = "ConcurrentFaultScopeExpandedToSystem"
                    };
                }

                var now = DateTime.UtcNow.Ticks;
                var identity = observation.Identity.Clone();
                identity.ResourceScope = scope.Scope;
                identity.Generation = Math.Max(1, identity.Generation);
                identity.Revision = Math.Max(1, current.Revision + 1);
                var budget = GetBudget(current, scope.Scope);
                var intent = new RecoveryIntent
                {
                    Identity = identity,
                    OwnerId = RecoveryProtocolV7.NewId(),
                    Stage = observation.ResourceKind == ResourceKind.UserInterface
                        ? RecoveryStage.LocalRebuild
                        : RecoveryStage.AwaitingSafetyProof,
                    DesiredTerminalState = observation.ResourceKind == ResourceKind.UserInterface
                        ? current.DesiredState?.State ?? SystemTerminalState.Running
                        : observation.ResourceKind == ResourceKind.EngineHost
                        ? SystemTerminalState.Running
                        : scope.IsSystemWide
                        ? SystemTerminalState.Running
                        : SystemTerminalState.RunningDegraded,
                    HardwareConfirmed = observation.HardwareConfirmed,
                    AutomaticReplayForbidden = !observation.SafetyChainHealthy,
                    LocalRebuildAttempts = budget.LocalRebuildAttempts,
                    CurrentEngineReplacementAttempts = budget.CurrentEngineReplacementAttempts,
                    LastKnownGoodAttempts = budget.LastKnownGoodAttempts,
                    ActiveQualificationAttempts = budget.ActiveQualificationAttempts,
                    RequiresEngineReplacement =
                        observation.ResourceKind == ResourceKind.EngineHost,
                    CreatedUtcTicks = now,
                    UpdatedUtcTicks = now
                };
                var candidate = current.Clone();
                candidate.ActiveIntents = (candidate.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                    .Concat(new[] { intent }).ToArray();
                candidate.DesiredState = observation.ResourceKind == ResourceKind.UserInterface
                    ? Desired(identity, intent.DesiredTerminalState,
                        "UserInterfaceRecoveryDoesNotAffectEngine", current.Revision + 1)
                    : Desired(identity, SystemTerminalState.SafeIdleAlarmed,
                        "RecoveryIntentCommitted:" + observation.FaultCode, current.Revision + 1);
                candidate.PendingCommand = BuildCommand(
                    intent,
                    observation.ResourceKind == ResourceKind.UserInterface
                        ? RecoveryCommandKind.EnsureUserInterface
                        : RecoveryCommandKind.DisableOutputs,
                    current.Revision + 1,
                    observation.ResourceKind == ResourceKind.UserInterface
                        ? TimeSpan.FromSeconds(60)
                        : _policy.SafetyProofDeadline);
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed)
                    throw new InvalidOperationException(commit.Reason);
                return new RecoveryDecision
                {
                    Intent = FindIntent(commit.Document, identity.IncidentId),
                    Command = commit.Document.PendingCommand,
                    DesiredState = commit.Document.DesiredState,
                    Reason = scope.Reason
                };
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        public RecoveryDecision AcceptSafetyProof(SafetyProof proof)
        {
            if (proof?.IsStructurallyValid() != true)
                throw new ArgumentException("SafetyProofInvalid", nameof(proof));
            return Mutate(proof.Identity.IncidentId, (document, intent) =>
            {
                if (intent.Stage != RecoveryStage.AwaitingSafetyProof && intent.Stage != RecoveryStage.PressureMaintenanceStopping)
                    return "SafetyProofDuplicateOrOutOfOrder";
                if (document.PendingCommand?.Identity.ToCanonicalString() != proof.Identity.ToCanonicalString() ||
                    proof.CapturedUtcTicks > DateTime.UtcNow.Ticks || proof.ValidUntilUtcTicks <= DateTime.UtcNow.Ticks)
                    throw new InvalidOperationException("SafetyProofBindingInvalid");
                CompletePending(document);
                if (intent.ProjectSwitch != null)
                {
                    if (!proof.IsComplete)
                    {
                        EnterSafeIdle(document, intent, "ProjectSwitchSafetyProofIncomplete");
                        return "ProjectSwitchSafetyProofIncomplete";
                    }
                    if (!string.IsNullOrEmpty(intent.ProjectSwitchFailure))
                    {
                        if (intent.ProjectSwitch.MatchesSource(intent.Identity))
                        {
                            intent.Stage = RecoveryStage.ProjectSwitchAborting;
                            document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.AbortProjectSwitch,
                                document.Revision + 1, _policy.CommandDeadline);
                        }
                        else EnterSafeIdle(document, intent, "ProjectSwitchDestinationFailed;NoSourceFallback");
                        return "ProjectSwitchFailureSafetyBoundaryComplete";
                    }
                    intent.Stage = RecoveryStage.ProjectSwitchPreparing;
                    document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.PrepareProjectSwitch,
                        document.Revision + 1, _policy.CommandDeadline);
                    return "ProjectSwitchSourceSafetyProofComplete";
                }
                if (intent.PressureMaintenance != null) return AcceptMaintenanceSafetyProof(document, intent, proof);
                if (!proof.IsComplete || intent.AutomaticReplayForbidden)
                {
                    EnterSafeIdle(document, intent, "SafetyProofIncomplete");
                    return "SafetyProofIncomplete";
                }
                if (intent.CommandAfterSafetyProof != RecoveryCommandKind.None)
                {
                    var next = intent.CommandAfterSafetyProof;
                    intent.CommandAfterSafetyProof = RecoveryCommandKind.None;
                    intent.Stage = next == RecoveryCommandKind.RebuildResource ? RecoveryStage.LocalRebuild :
                        next == RecoveryCommandKind.ReplaceEngineHost ? RecoveryStage.ReplaceCurrentEngine :
                        next == RecoveryCommandKind.ActivateLastKnownGood ? RecoveryStage.ActivateLastKnownGood : RecoveryStage.SafeIdleAlarmed;
                    document.PendingCommand = BuildCommand(intent, next, document.Revision + 1, _policy.CommandDeadline);
                    return "TimedOutExecutorSafetyBoundaryComplete";
                }
                if (intent.OperatorTransaction?.Kind == OperatorCommandKind.CommitConfiguration)
                {
                    intent.Stage = RecoveryStage.LocalRebuild;
                    document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.CommitTestConfiguration,
                        document.Revision + 1, _policy.CommandDeadline);
                    return "ConfigurationSafetyProofComplete";
                }
                if (intent.OperatorTransaction?.Kind == OperatorCommandKind.RetryQualification)
                {
                    intent.Stage = RecoveryStage.PassivePreflight;
                    document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.RunPassivePreflight,
                        document.Revision + 1, _policy.CommandDeadline);
                    return "OperatorQualificationRetryPassivePreflight";
                }
                if (intent.IsOperatorStart)
                {
                    intent.Stage = RecoveryStage.PassivePreflight;
                    document.PendingCommand = BuildCommand(
                        intent,
                        RecoveryCommandKind.RunPassivePreflight,
                        document.Revision + 1,
                        _policy.CommandDeadline);
                    return "OperatorStartPassivePreflight";
                }
                if (intent.RequiresEngineReplacement)
                {
                    intent.Stage = RecoveryStage.ReplaceCurrentEngine;
                    document.PendingCommand = BuildCommand(
                        intent,
                        RecoveryCommandKind.ReplaceEngineHost,
                        document.Revision + 1,
                        _policy.CommandDeadline);
                    return "EngineHostReplacementScheduled";
                }
                var budget = GetBudget(document, intent.Identity.ResourceScope);
                if (intent.HardwareConfirmed && !document.IsolatedResources.Contains(intent.Identity.ResourceScope, StringComparer.OrdinalIgnoreCase))
                {
                    if (budget.ActiveQualificationAttempts >=
                        _policy.MaximumConfirmedHardwareQualificationAttempts)
                    {
                        Isolate(document, intent, "ConfirmedHardwareQualificationExhausted");
                        return "ConfirmedHardwareIsolated";
                    }
                    budget.ActiveQualificationAttempts++;
                    CopyBudget(intent, budget);
                    intent.Stage = RecoveryStage.PassivePreflight;
                    document.PendingCommand = BuildCommand(
                        intent,
                        RecoveryCommandKind.RunPassivePreflight,
                        document.Revision + 1,
                        _policy.CommandDeadline);
                    return "ConfirmedHardwarePassivePreflight";
                }
                intent.Stage = RecoveryStage.LocalRebuild;
                document.PendingCommand = BuildCommand(
                    intent,
                    RecoveryCommandKind.RebuildResource,
                    document.Revision + 1,
                    _policy.CommandDeadline);
                return "LocalRebuildScheduled";
            });
        }

        public RecoveryDecision StartByOperator(
            string sessionId,
            string runId,
            long runEpoch,
            string reason, OperatorCommand operatorCommand = null)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var duplicate = FindOperatorAdmission(current, operatorCommand);
                if (duplicate != null) return DecisionFromAdmission(duplicate);
                ValidateNewOperatorCommand(current, operatorCommand);
                RequireDesiredRun(current, sessionId, runId, runEpoch);
                if ((current.ActiveIntents ?? Array.Empty<RecoveryIntent>()).Length > 0 ||
                    current.PendingCommand != null)
                    throw new InvalidOperationException("OperatorStartRecoveryAlreadyOwned");
                if (current.DesiredState != null &&
                    (current.DesiredState.State == SystemTerminalState.Running ||
                     current.DesiredState.State == SystemTerminalState.RunningDegraded))
                    throw new InvalidOperationException("OperatorStartAlreadyRunning");

                var identity = new RecoveryIdentity
                {
                    SessionId = sessionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(),
                    ResourceScope = "System",
                    Generation = 1,
                    Revision = current.Revision + 1
                };
                if (!identity.IsStructurallyValid())
                    throw new ArgumentException("OperatorStartIdentityInvalid");
                var now = DateTime.UtcNow.Ticks;
                var intent = new RecoveryIntent
                {
                    Identity = identity,
                    OwnerId = RecoveryProtocolV7.NewId(),
                    Stage = RecoveryStage.AwaitingSafetyProof,
                    DesiredTerminalState = SystemTerminalState.Running,
                    IsOperatorStart = true,
                    CreatedUtcTicks = now,
                    UpdatedUtcTicks = now
                };
                var candidate = current.Clone();
                candidate.ActiveIntents = new[] { intent };
                candidate.DesiredState = Desired(
                    identity,
                    SystemTerminalState.SafeIdleAlarmed,
                    string.IsNullOrWhiteSpace(reason)
                        ? "OperatorStartSafetyProofPending"
                        : reason,
                    current.Revision + 1);
                candidate.PendingCommand = BuildCommand(
                    intent,
                    RecoveryCommandKind.DisableOutputs,
                    current.Revision + 1,
                    _policy.SafetyProofDeadline);
                AddOperatorAdmission(candidate, operatorCommand, intent, "OperatorStartCommitted");
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed)
                    throw new InvalidOperationException(commit.Reason);
                return new RecoveryDecision
                {
                    Intent = FindIntent(commit.Document, identity.IncidentId),
                    Command = commit.Document.PendingCommand,
                    DesiredState = commit.Document.DesiredState,
                    Reason = "OperatorStartCommitted"
                };
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        public RecoveryDecision AcknowledgeCommand(
            string incidentId,
            string commandId,
            bool succeeded,
            string detail)
            => AcknowledgeCommandCore(incidentId, commandId, succeeded, detail, null);

        public RecoveryDecision AcknowledgeManualChannelCommand(string incidentId, RecoveryCommandReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            return AcknowledgeCommandCore(incidentId, receipt.CommandId, receipt.Succeeded, receipt.Detail, receipt);
        }

        private RecoveryDecision AcknowledgeCommandCore(string incidentId, string commandId, bool succeeded, string detail,
            RecoveryCommandReceipt manualReceipt)
        {
            if (!RecoveryProtocolV7.IsGuid(incidentId) ||
                !RecoveryProtocolV7.IsGuid(commandId))
                throw new ArgumentException("RecoveryCommandReceiptIdentityInvalid");
            return Mutate(incidentId, (document, intent) =>
            {
                var command = document.PendingCommand;
                if (command == null || !string.Equals(
                        command.CommandId, commandId, StringComparison.Ordinal))
                {
                    if ((document.CompletedIdempotencyKeys ?? Array.Empty<string>())
                        .Any(value => string.Equals(value, commandId, StringComparison.Ordinal)))
                        return "CommandReceiptDuplicate";
                    throw new InvalidOperationException("RecoveryCommandReceiptOutOfOrder");
                }
                if (succeeded && command.Kind == RecoveryCommandKind.RunQualificationCycle)
                    throw new InvalidOperationException("QualificationReceiptRequired");
                if (succeeded && PressureMaintenanceProtocol.IsExecution(command.Kind))
                {
                    ValidateMaintenanceReceipt(intent, command, manualReceipt);
                    CompletePending(document);
                    CompleteMaintenanceOperator(document, intent, true, detail);
                    intent.OperatorTransaction = null;
                    intent.Stage = RecoveryStage.PressureMaintenanceReady;
                    return "MaintenanceExecutionComplete;LeaseStillOwned";
                }
                if (succeeded && (command.Kind == RecoveryCommandKind.PrepareProjectSwitch || command.Kind == RecoveryCommandKind.ActivateProjectSwitch))
                {
                    ValidateProjectSwitchReceipt(command, manualReceipt);
                    CompletePending(document);
                    AdvanceProjectSwitch(document, intent, command, manualReceipt);
                    return "ProjectSwitchStageCommitted";
                }
                if (succeeded && (command.Kind == RecoveryCommandKind.PauseChannelGracefully || command.Kind == RecoveryCommandKind.ResumePausedChannel))
                {
                    var state = manualReceipt?.ManualChannels;
                    var bit = 1 << (command.OperatorTransaction.ManualBatch.Channel - 1);
                    var expectedHolds = command.Kind == RecoveryCommandKind.PauseChannelGracefully
                        ? intent.ManualPausedChannelsMask | bit : intent.ManualPausedChannelsMask & ~bit;
                    if (state?.IsStructurallyValid() != true || manualReceipt.IdempotencyKey != command.IdempotencyKey ||
                        state.EngineInstanceId != intent.ManualBatchEngineInstanceId || state.ResumeMask != expectedHolds ||
                        manualReceipt.CompletedUtcTicks <= 0 || manualReceipt.CompletedUtcTicks > DateTime.UtcNow.Ticks ||
                        DateTime.UtcNow.Ticks > command.DeadlineUtcTicks ||
                        command.Kind == RecoveryCommandKind.PauseChannelGracefully && (!manualReceipt.DataBoundaryClosed || (state.PauseMask & bit) != 0))
                        throw new InvalidOperationException("ManualChannelReceiptRequiredOrInvalid");
                    // Peers can finish their target while this channel drains.
                    // Use executor completion facts, not admission-time membership.
                    intent.ManualRunningChannelsMask = state.PauseMask;
                }
                CompletePending(document);
                if (succeeded)
                {
                    AdvanceSuccess(document, intent, command.Kind, detail);
                    return "RecoveryStageAdvanced";
                }
                AdvanceFailure(document, intent, command.Kind, detail);
                if (command.Kind == RecoveryCommandKind.RunQualificationCycle || command.Kind == RecoveryCommandKind.ResumeFormalRun)
                    RequireExecutorSafetyBoundary(document, intent, "ActiveQualificationOrFormalCommitFailed:" + command.Kind);
                return "RecoveryStageEscalated";
            });
        }

        /// <summary>
        /// Turns a lost reply, dead executor, or communication partition into
        /// a durable failure transition after the command deadline. This
        /// prevents a permanent Recovering intermediate state.
        /// </summary>
        public RecoveryDecision ReconcileExpiredCommand(long nowUtcTicks)
        {
            var snapshot = _journal.Load();
            var pending = snapshot.PendingCommand;
            if (pending == null || pending.DeadlineUtcTicks > nowUtcTicks)
                return null;
            var commandId = pending.CommandId;
            var commandKind = pending.Kind;
            return Mutate(pending.Identity.IncidentId, (document, intent) =>
            {
                var current = document.PendingCommand;
                if (current == null ||
                    !string.Equals(current.CommandId, commandId,
                        StringComparison.Ordinal) ||
                    current.DeadlineUtcTicks > nowUtcTicks)
                    return "RecoveryDeadlineAlreadyReconciled";
                CompletePending(document);
                if (commandKind == RecoveryCommandKind.DisableOutputs ||
                    commandKind == RecoveryCommandKind.SealActiveCycle ||
                    commandKind == RecoveryCommandKind.EnterSafeIdle || commandKind == RecoveryCommandKind.StopByOperator)
                {
                    EnterSafeIdle(document, intent,
                        "SafetyProofDeadlineExceeded:" + commandKind);
                    return "SafetyProofDeadlineExceeded";
                }
                AdvanceFailure(document, intent, commandKind,
                    "CommandDeadlineExceeded");
                if (intent.ProjectSwitch == null && commandKind != RecoveryCommandKind.EnsureUserInterface &&
                    !EngineHostProtocol.IsPrioritySafetyCommand(document.PendingCommand?.Kind ?? RecoveryCommandKind.None))
                {
                    // A lost reply is not proof that the executor stopped. Preserve
                    // the budget decision, but fence/join it through priority OFF
                    // before dispatching any successor (or declaring safe idle).
                    RequireExecutorSafetyBoundary(document, intent, "TimedOutExecutorSafetyBoundaryPending:" + commandKind);
                }
                return "RecoveryCommandDeadlineExceeded:" + commandKind;
            });
        }

        public RecoveryDecision AcceptQualification(QualificationReceipt receipt)
        {
            if (receipt?.IsStructurallyValid() != true)
                throw new ArgumentException("QualificationReceiptInvalid", nameof(receipt));
            return Mutate(receipt.Identity.IncidentId, (document, intent) =>
            {
                var pending = document.PendingCommand;
                if (pending?.Kind != RecoveryCommandKind.RunQualificationCycle ||
                    pending.Identity.ToCanonicalString() != receipt.Identity.ToCanonicalString() ||
                    receipt.CapturedUtcTicks > DateTime.UtcNow.Ticks || DateTime.UtcNow.Ticks > pending.DeadlineUtcTicks)
                    throw new InvalidOperationException("QualificationReceiptBindingInvalid");
                CompletePending(document);
                if (receipt.InterruptedCycleCounted)
                {
                    EnterSafeIdle(document, intent, "InterruptedCycleWasCounted");
                    return "QualificationRejectedInterruptedCycleCounted";
                }
                if (receipt.QualificationCyclesCompleted < 2)
                {
                    intent.Stage = RecoveryStage.Qualification;
                    document.PendingCommand = BuildCommand(
                        intent,
                        RecoveryCommandKind.RunQualificationCycle,
                        document.Revision + 1,
                        _policy.CommandDeadline);
                    return "QualificationIncomplete";
                }
                intent.Stage = RecoveryStage.FormalValidation;
                document.PendingCommand = BuildCommand(
                    intent,
                    RecoveryCommandKind.ResumeFormalRun,
                    document.Revision + 1,
                    _policy.CommandDeadline);
                if (receipt.IsBudgetResetQualified(DateTime.UtcNow.Ticks))
                    ResetBudget(document, intent.Identity.ResourceScope);
                return "QualificationAccepted";
            });
        }

        private void RequireExecutorSafetyBoundary(RecoveryKernelJournalDocument document, RecoveryIntent intent, string reason)
        {
            intent.CommandAfterSafetyProof = document.PendingCommand?.Kind ?? RecoveryCommandKind.None;
            intent.AutomaticReplayForbidden |= intent.CommandAfterSafetyProof == RecoveryCommandKind.None;
            intent.OperatorTransaction = null;
            intent.IsOperatorStart = false;
            intent.Stage = RecoveryStage.AwaitingSafetyProof;
            intent.Identity.Generation++;
            intent.Identity.Revision = document.Revision + 1;
            document.ActiveIntents = new[] { intent };
            document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed, reason, document.Revision + 1);
            document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs,
                document.Revision + 1, _policy.SafetyProofDeadline);
        }

        public RecoveryDecision StopByOperator(
            string sessionId,
            string runId,
            long runEpoch,
            string reason, OperatorCommand operatorCommand = null)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var duplicate = FindOperatorAdmission(current, operatorCommand);
                if (duplicate != null) return DecisionFromAdmission(duplicate);
                ValidateNewOperatorCommand(current, operatorCommand);
                var projectSwitch = current.ActiveIntents.SingleOrDefault(item => item.ProjectSwitch != null);
                if (projectSwitch != null)
                {
                    var joined = JoinProjectSwitchStop(current, projectSwitch, sessionId, runId, runEpoch, operatorCommand);
                    if (joined != null) return joined;
                    continue;
                }
                RequireDesiredRun(current, sessionId, runId, runEpoch);
                var candidate = current.Clone();
                foreach (var previousIntent in candidate.ActiveIntents)
                    CompleteConfiguration(candidate, previousIntent, false, "ConfigurationInterruptedByOperatorStop");
                var identity = new RecoveryIdentity
                {
                    SessionId = sessionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    IncidentId = RecoveryProtocolV7.NewId(),
                    ResourceScope = "System",
                    Generation = 1,
                    Revision = current.Revision + 1
                };
                if (!identity.IsStructurallyValid())
                    throw new ArgumentException("OperatorStopIdentityInvalid");
                var now = DateTime.UtcNow.Ticks;
                var intent = new RecoveryIntent
                {
                    Identity = identity,
                    OwnerId = RecoveryProtocolV7.NewId(),
                    Stage = RecoveryStage.SafeIdleAlarmed,
                    DesiredTerminalState = SystemTerminalState.StoppedByOperator,
                    AutomaticReplayForbidden = true,
                    CreatedUtcTicks = now,
                    UpdatedUtcTicks = now
                };
                candidate.ActiveIntents = new[] { intent };
                candidate.DesiredState = new SystemDesiredState
                {
                    SessionId = sessionId,
                    RunId = runId,
                    RunEpoch = runEpoch,
                    Revision = current.Revision + 1,
                    State = SystemTerminalState.SafeIdleAlarmed,
                    Reason = string.IsNullOrWhiteSpace(reason)
                        ? "OperatorStopPending"
                        : reason,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                candidate.PendingCommand = BuildCommand(
                    intent,
                    RecoveryCommandKind.StopByOperator,
                    current.Revision + 1,
                    TimeSpan.FromSeconds(30));
                AddOperatorAdmission(candidate, operatorCommand, intent, "OperatorStopCommitted");
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed) throw new InvalidOperationException(commit.Reason);
                return new RecoveryDecision
                {
                    Intent = FindIntent(commit.Document, identity.IncidentId),
                    Command = commit.Document.PendingCommand,
                    DesiredState = commit.Document.DesiredState,
                    Reason = "OperatorStopCommitted"
                };
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        /// <summary>
        /// Resets recovery budgets only after the EngineHost has durably
        /// reported two qualification cycles, five formal cycles and ten
        /// continuous stable minutes. A system-wide stable observation is a
        /// stronger predicate than resetting only the last failed scope.
        /// </summary>
        public bool ObserveStableProgress(
            string sessionId,
            string runId,
            long runEpoch,
            int qualificationCyclesCompleted,
            int formalCyclesCompleted,
            long stableSinceUtcTicks,
            long nowUtcTicks)
        {
            if (!RecoveryProtocolV7.IsGuid(sessionId) ||
                !RecoveryProtocolV7.IsGuid(runId) || runEpoch <= 0 ||
                qualificationCyclesCompleted < 2 || formalCyclesCompleted < 5 ||
                stableSinceUtcTicks <= 0 || nowUtcTicks <= stableSinceUtcTicks ||
                nowUtcTicks - stableSinceUtcTicks < TimeSpan.FromMinutes(10).Ticks)
                return false;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                if ((current.ActiveIntents ?? Array.Empty<RecoveryIntent>()).Length > 0 ||
                    current.PendingCommand != null ||
                    current.DesiredState == null ||
                    !string.Equals(current.DesiredState.SessionId, sessionId,
                        StringComparison.Ordinal) ||
                    !string.Equals(current.DesiredState.RunId, runId,
                        StringComparison.Ordinal) ||
                    current.DesiredState.RunEpoch != runEpoch ||
                    (current.DesiredState.State != SystemTerminalState.Running &&
                     current.DesiredState.State != SystemTerminalState.RunningDegraded))
                    return false;
                var candidate = current.Clone();
                var budgets = candidate.Budgets ?? Array.Empty<RecoveryBudgetState>();
                if (!budgets.Any(value =>
                        value.LocalRebuildAttempts > 0 ||
                        value.CurrentEngineReplacementAttempts > 0 ||
                        value.LastKnownGoodAttempts > 0 ||
                        value.ActiveQualificationAttempts > 0))
                    return false;
                foreach (var budget in budgets)
                {
                    budget.LocalRebuildAttempts = 0;
                    budget.CurrentEngineReplacementAttempts = 0;
                    budget.LastKnownGoodAttempts = 0;
                    budget.ActiveQualificationAttempts = 0;
                    budget.StableSinceUtcTicks = stableSinceUtcTicks;
                }
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed)
                    throw new InvalidOperationException(commit.Reason);
                return true;
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        private RecoveryDecision Mutate(
            string incidentId,
            Func<RecoveryKernelJournalDocument, RecoveryIntent, string> mutation)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var current = _journal.Load();
                var candidate = current.Clone();
                var intent = FindIntent(candidate, incidentId);
                if (intent == null)
                    throw new InvalidOperationException("RecoveryIncidentNotOwned");
                var reason = mutation(candidate, intent);
                intent.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                intent.Identity.Revision = current.Revision + 1;
                SynchronizeBudget(candidate, intent);
                var commit = _journal.CompareExchange(current.Revision, candidate);
                if (commit.Conflict) continue;
                if (!commit.Committed) throw new InvalidOperationException(commit.Reason);
                return new RecoveryDecision
                {
                    Intent = FindIntent(commit.Document, incidentId),
                    Command = commit.Document.PendingCommand,
                    DesiredState = commit.Document.DesiredState,
                    Reason = reason
                };
            }
            throw new InvalidOperationException("RecoveryJournalConflictLimitExceeded");
        }

        private void AdvanceSuccess(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent,
            RecoveryCommandKind kind,
            string detail)
        {
            switch (kind)
            {
                case RecoveryCommandKind.PauseChannelGracefully:
                case RecoveryCommandKind.ResumePausedChannel:
                    var bit = 1 << (intent.OperatorTransaction.ManualBatch.Channel - 1);
                    intent.ManualPausedChannelsMask = kind == RecoveryCommandKind.PauseChannelGracefully
                        ? intent.ManualPausedChannelsMask | bit : intent.ManualPausedChannelsMask & ~bit;
                    intent.Stage = intent.ManualPausedChannelsMask == 0 ? RecoveryStage.Completed : RecoveryStage.OperatorChannelsHeld;
                    document.DesiredState = Desired(intent.Identity, intent.ManualPausedChannelsMask == 0 ? intent.DesiredTerminalState :
                        intent.ManualRunningChannelsMask == 0 ? SystemTerminalState.StoppedByOperator : SystemTerminalState.RunningDegraded,
                        "ManualChannelBoundaryCommitted", document.Revision + 1);
                    if (intent.ManualPausedChannelsMask == 0) document.ActiveIntents = Array.Empty<RecoveryIntent>();
                    break;
                case RecoveryCommandKind.PauseBatchGracefully:
                    intent.Stage = RecoveryStage.OperatorPaused;
                    document.DesiredState = Desired(intent.Identity, SystemTerminalState.StoppedByOperator,
                        "PausedByOperator;SameEngineContinuationOnly", document.Revision + 1);
                    break;
                case RecoveryCommandKind.ResumePausedBatch:
                    intent.Stage = RecoveryStage.Completed;
                    document.DesiredState = Desired(intent.Identity, intent.DesiredTerminalState, "ManualBatchResumed", document.Revision + 1);
                    document.ActiveIntents = Array.Empty<RecoveryIntent>();
                    break;
                case RecoveryCommandKind.RebuildResource:
                case RecoveryCommandKind.ReplaceEngineHost:
                case RecoveryCommandKind.ActivateLastKnownGood:
                case RecoveryCommandKind.RunPassivePreflight:
                    intent.Stage = kind == RecoveryCommandKind.RunPassivePreflight
                        ? RecoveryStage.Qualification
                        : RecoveryStage.PassivePreflight;
                    document.PendingCommand = BuildCommand(
                        intent,
                        kind == RecoveryCommandKind.RunPassivePreflight
                            ? RecoveryCommandKind.RunQualificationCycle
                            : RecoveryCommandKind.RunPassivePreflight,
                        document.Revision + 1,
                        _policy.CommandDeadline);
                    break;
                case RecoveryCommandKind.RunQualificationCycle:
                    intent.Stage = RecoveryStage.Qualification;
                    break;
                case RecoveryCommandKind.ResumeFormalRun:
                    intent.Stage = RecoveryStage.Completed;
                    if (intent.OperatorTransaction?.Kind == OperatorCommandKind.RetryQualification)
                        document.IsolatedResources = (document.IsolatedResources ?? Array.Empty<string>())
                            .Where(value => !string.Equals(value, intent.Identity.ResourceScope, StringComparison.OrdinalIgnoreCase)).ToArray();
                    document.DesiredState = Desired(
                        intent.Identity,
                        intent.DesiredTerminalState,
                        string.IsNullOrWhiteSpace(detail)
                            ? "RecoveryCompleted"
                            : detail,
                        document.Revision + 1);
                    document.ActiveIntents = (document.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                        .Where(item => !string.Equals(
                            item.Identity.IncidentId,
                            intent.Identity.IncidentId,
                            StringComparison.Ordinal)).ToArray();
                    break;
                case RecoveryCommandKind.IsolateResource:
                    FinishIsolation(document, intent, detail);
                    break;
                case RecoveryCommandKind.EnterSafeIdle:
                    EnterSafeIdle(document, intent, detail);
                    break;
                case RecoveryCommandKind.StopByOperator:
                case RecoveryCommandKind.CommitTestConfiguration:
                    CompleteConfiguration(document, intent, true, detail);
                    CompleteProjectSwitch(document, intent, true, "ProjectSwitchStopped");
                    document.DesiredState = Desired(
                        intent.Identity,
                        SystemTerminalState.StoppedByOperator,
                        "StoppedByOperator",
                        document.Revision + 1);
                    document.ActiveIntents = Array.Empty<RecoveryIntent>();
                    break;
                case RecoveryCommandKind.AbortProjectSwitch:
                    intent.Stage = RecoveryStage.ProjectSwitchStopping;
                    document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.StopByOperator,
                        document.Revision + 1, _policy.SafetyProofDeadline);
                    break;
                case RecoveryCommandKind.EnsureUserInterface:
                    intent.Stage = RecoveryStage.Completed;
                    document.DesiredState = Desired(
                        intent.Identity,
                        intent.DesiredTerminalState,
                        string.IsNullOrWhiteSpace(detail)
                            ? "UserInterfaceRestored"
                            : detail,
                        document.Revision + 1);
                    document.ActiveIntents = (document.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                        .Where(item => !string.Equals(
                            item.Identity.IncidentId,
                            intent.Identity.IncidentId,
                            StringComparison.Ordinal)).ToArray();
                    break;
                default:
                    throw new InvalidOperationException("RecoveryCommandSuccessStageInvalid:" + kind);
            }
        }

        private void AdvanceFailure(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent,
            RecoveryCommandKind kind,
            string detail)
        {
            if (intent.PressureMaintenance != null)
            {
                if (intent.PressureMaintenance.Revoked || kind == RecoveryCommandKind.DisableOutputs)
                    EnterSafeIdle(document, intent, "MaintenanceSafetyOrCloseFailed:" + detail);
                else ClosePressureMaintenance(document, intent, SystemTerminalState.SafeIdleAlarmed, "MaintenanceExecutorFailed:" + detail, null);
                return;
            }
            if (intent.ProjectSwitch != null)
            {
                if (kind == RecoveryCommandKind.DisableOutputs || kind == RecoveryCommandKind.AbortProjectSwitch || kind == RecoveryCommandKind.StopByOperator)
                    EnterSafeIdle(document, intent, "ProjectSwitchSafetyOrAbortFailed:" + detail);
                else FailProjectSwitch(document, intent, detail);
                return;
            }
            if (intent.OperatorTransaction?.Kind == OperatorCommandKind.RetryQualification)
            {
                intent.OperatorTransaction = null;
                Isolate(document, intent, "OperatorQualificationRetryFailed:" + kind + ":" + (detail ?? string.Empty));
                return;
            }
            var budget = GetBudget(document, intent.Identity.ResourceScope);
            budget.LastFailureUtcTicks = DateTime.UtcNow.Ticks;
            switch (kind)
            {
                case RecoveryCommandKind.PauseBatchGracefully:
                case RecoveryCommandKind.ResumePausedBatch:
                case RecoveryCommandKind.PauseChannelGracefully:
                case RecoveryCommandKind.ResumePausedChannel:
                    intent.AutomaticReplayForbidden = true;
                    intent.OperatorTransaction = null;
                    intent.Stage = RecoveryStage.AwaitingSafetyProof;
                    document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed,
                        "ManualBatchFailed;SafetyBoundaryPending:" + detail, document.Revision + 1);
                    document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.DisableOutputs,
                        document.Revision + 1, _policy.SafetyProofDeadline);
                    break;
                case RecoveryCommandKind.RebuildResource:
                    budget.LocalRebuildAttempts++;
                    if (budget.LocalRebuildAttempts < _policy.MaximumLocalRebuilds)
                    {
                        intent.Stage = RecoveryStage.LocalRebuild;
                        document.PendingCommand = BuildCommand(intent,
                            RecoveryCommandKind.RebuildResource,
                            document.Revision + 1,
                            _policy.CommandDeadline);
                    }
                    else
                    {
                        intent.Stage = RecoveryStage.ReplaceCurrentEngine;
                        document.PendingCommand = BuildCommand(intent,
                            RecoveryCommandKind.ReplaceEngineHost,
                            document.Revision + 1,
                            _policy.CommandDeadline);
                    }
                    break;
                case RecoveryCommandKind.ReplaceEngineHost:
                    budget.CurrentEngineReplacementAttempts++;
                    if (budget.CurrentEngineReplacementAttempts <
                        _policy.MaximumCurrentEngineReplacements)
                        document.PendingCommand = BuildCommand(intent,
                            RecoveryCommandKind.ReplaceEngineHost,
                            document.Revision + 1,
                            _policy.CommandDeadline);
                    else
                    {
                        intent.Stage = RecoveryStage.ActivateLastKnownGood;
                        document.PendingCommand = BuildCommand(intent,
                            RecoveryCommandKind.ActivateLastKnownGood,
                            document.Revision + 1,
                            _policy.CommandDeadline);
                    }
                    break;
                case RecoveryCommandKind.ActivateLastKnownGood:
                    budget.LastKnownGoodAttempts++;
                    EnterSafeIdle(document, intent,
                        "LastKnownGoodFailed:" + (detail ?? string.Empty));
                    break;
                case RecoveryCommandKind.RunPassivePreflight:
                case RecoveryCommandKind.RunQualificationCycle:
                    if (string.Equals(detail, "NoRemainingFormalWork",
                            StringComparison.Ordinal))
                    {
                        EnterSafeIdle(document, intent, detail);
                        break;
                    }
                    if (intent.HardwareConfirmed && !document.IsolatedResources.Contains(intent.Identity.ResourceScope, StringComparer.OrdinalIgnoreCase))
                        Isolate(document, intent,
                            "ConfirmedHardwareQualificationFailed:" + (detail ?? string.Empty));
                    else
                    {
                        intent.Stage = RecoveryStage.ReplaceCurrentEngine;
                        document.PendingCommand = BuildCommand(intent,
                            RecoveryCommandKind.ReplaceEngineHost,
                            document.Revision + 1,
                            _policy.CommandDeadline);
                    }
                    break;
                default:
                    EnterSafeIdle(document, intent,
                        "RecoveryCommandFailed:" + kind + ":" + (detail ?? string.Empty));
                    break;
            }
            CopyBudget(intent, budget);
        }

        private static void CompletePending(RecoveryKernelJournalDocument document)
        {
            if (document.PendingCommand == null) return;
            document.CompletedIdempotencyKeys =
                (document.CompletedIdempotencyKeys ?? Array.Empty<string>())
                .Concat(new[]
                {
                    document.PendingCommand.CommandId,
                    document.PendingCommand.IdempotencyKey
                })
                .Distinct(StringComparer.Ordinal)
                .Reverse()
                .Take(4096)
                .Reverse()
                .ToArray();
            document.PendingCommand = null;
        }

        private static void Isolate(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent,
            string reason)
        {
            intent.Stage = RecoveryStage.SafeIdleAlarmed;
            document.PendingCommand = BuildCommandStatic(
                intent,
                RecoveryCommandKind.IsolateResource,
                document.Revision + 1,
                TimeSpan.FromSeconds(30));
            document.DesiredState = Desired(
                intent.Identity,
                string.Equals(intent.Identity.ResourceScope, "System", StringComparison.OrdinalIgnoreCase)
                    ? SystemTerminalState.SafeIdleAlarmed
                    : SystemTerminalState.RunningDegraded,
                reason,
                document.Revision + 1);
        }

        private void FinishIsolation(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent,
            string detail)
        {
            document.IsolatedResources = (document.IsolatedResources ?? Array.Empty<string>())
                .Concat(new[] { intent.Identity.ResourceScope })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!string.Equals(intent.Identity.ResourceScope, "System", StringComparison.OrdinalIgnoreCase))
            {
                // Isolation is not evidence of a running healthy domain. Keep the
                // same durable Owner through passive checks, qualification and formal admission.
                intent.DesiredTerminalState = SystemTerminalState.RunningDegraded;
                intent.Stage = RecoveryStage.PassivePreflight;
                document.PendingCommand = BuildCommand(intent, RecoveryCommandKind.RunPassivePreflight,
                    document.Revision + 1, _policy.CommandDeadline);
                document.DesiredState = Desired(intent.Identity, SystemTerminalState.SafeIdleAlarmed,
                    "IsolationCommitted;HealthyDomainsAwaitingQualification", document.Revision + 1);
                return;
            }
            document.DesiredState = Desired(
                intent.Identity,
                string.Equals(intent.Identity.ResourceScope, "System", StringComparison.OrdinalIgnoreCase)
                    ? SystemTerminalState.SafeIdleAlarmed
                    : SystemTerminalState.RunningDegraded,
                string.IsNullOrWhiteSpace(detail) ? "ResourceIsolated" : detail,
                document.Revision + 1);
            document.ActiveIntents = (document.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                .Where(item => !string.Equals(item.Identity.IncidentId,
                    intent.Identity.IncidentId, StringComparison.Ordinal)).ToArray();
        }

        private static void EnterSafeIdle(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent,
            string reason)
        {
            CompleteProjectSwitch(document, intent, false, reason);
            CompleteConfiguration(document, intent, false, reason);
            CompleteMaintenanceOperator(document, intent, false, reason);
            CompleteMaintenanceClosureAdmissions(document, intent, false, reason);
            intent.Stage = RecoveryStage.SafeIdleAlarmed;
            document.PendingCommand = null;
            document.DesiredState = Desired(
                intent.Identity,
                SystemTerminalState.SafeIdleAlarmed,
                string.IsNullOrWhiteSpace(reason) ? "RecoveryExhausted" : reason,
                document.Revision + 1);
            document.ActiveIntents = (document.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                .Where(item => !string.Equals(item.Identity.IncidentId,
                    intent.Identity.IncidentId, StringComparison.Ordinal)).ToArray();
        }

        private RecoveryCommand BuildCommand(
            RecoveryIntent intent,
            RecoveryCommandKind kind,
            long sequence,
            TimeSpan deadline)
        {
            return BuildCommandStatic(intent, kind, sequence, deadline);
        }

        private static RecoveryCommand BuildCommandStatic(
            RecoveryIntent intent,
            RecoveryCommandKind kind,
            long sequence,
            TimeSpan deadline)
        {
            var identity = intent.Identity.Clone();
            identity.Revision = Math.Max(1, sequence);
            var command = new RecoveryCommand
            {
                Identity = identity,
                OwnerId = intent.OwnerId,
                CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = Math.Max(1, sequence),
                Kind = kind,
                OperatorTransaction = intent.OperatorTransaction?.Clone(),
                ProjectSwitch = intent.ProjectSwitch?.Clone(),
                ProjectSwitchPreparedSha256 = intent.ProjectSwitchPreparedSha256,
                PressureMaintenance = intent.PressureMaintenance?.Clone(),
                TargetResource = intent.OperatorTransaction != null && ManualBatchCommand.IsChannelOperation(intent.OperatorTransaction.Kind)
                    ? "Channel:" + intent.OperatorTransaction.ManualBatch.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture) : identity.ResourceScope,
                DeadlineUtcTicks = DateTime.UtcNow.Add(deadline).Ticks
            };
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            return command;
        }

        private static SystemDesiredState Desired(
            RecoveryIdentity identity,
            SystemTerminalState state,
            string reason,
            long revision)
        {
            return new SystemDesiredState
            {
                SessionId = identity.SessionId,
                RunId = identity.RunId,
                RunEpoch = identity.RunEpoch,
                Revision = Math.Max(1, revision),
                State = state,
                Reason = string.IsNullOrWhiteSpace(reason) ? state.ToString() : reason,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static RecoveryIntent FindIntent(
            RecoveryKernelJournalDocument document,
            string incidentId)
        {
            return (document?.ActiveIntents ?? Array.Empty<RecoveryIntent>())
                .FirstOrDefault(item => string.Equals(
                    item.Identity.IncidentId,
                    incidentId,
                    StringComparison.Ordinal));
        }

        private static RecoveryBudgetState GetBudget(
            RecoveryKernelJournalDocument document,
            string scope)
        {
            var budgets = (document.Budgets ?? Array.Empty<RecoveryBudgetState>()).ToList();
            var budget = budgets.FirstOrDefault(item => string.Equals(
                item.ResourceScope, scope, StringComparison.OrdinalIgnoreCase));
            if (budget != null) return budget;
            budget = new RecoveryBudgetState { ResourceScope = scope };
            budgets.Add(budget);
            document.Budgets = budgets.ToArray();
            return budget;
        }

        private static void SynchronizeBudget(
            RecoveryKernelJournalDocument document,
            RecoveryIntent intent)
        {
            var budget = GetBudget(document, intent.Identity.ResourceScope);
            CopyBudget(intent, budget);
        }

        private static void CopyBudget(
            RecoveryIntent intent,
            RecoveryBudgetState budget)
        {
            intent.LocalRebuildAttempts = budget.LocalRebuildAttempts;
            intent.CurrentEngineReplacementAttempts = budget.CurrentEngineReplacementAttempts;
            intent.LastKnownGoodAttempts = budget.LastKnownGoodAttempts;
            intent.ActiveQualificationAttempts = budget.ActiveQualificationAttempts;
        }

        private static void ResetBudget(
            RecoveryKernelJournalDocument document,
            string scope)
        {
            var budget = GetBudget(document, scope);
            budget.LocalRebuildAttempts = 0;
            budget.CurrentEngineReplacementAttempts = 0;
            budget.LastKnownGoodAttempts = 0;
            budget.ActiveQualificationAttempts = 0;
            budget.StableSinceUtcTicks = DateTime.UtcNow.Ticks;
        }
    }
}
