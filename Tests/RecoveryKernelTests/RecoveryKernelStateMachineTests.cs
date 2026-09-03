using System;
using System.Collections.Generic;
using System.Linq;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static class RecoveryKernelStateMachineTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("schema7 contracts bind complete transaction identity", ProtocolContracts, ref passed);
            Run("intent and owner commit before command", IntentBeforeCommand, ref passed);
            Run("unknown scope expands to system", UnknownScopeExpandsToSystem, ref passed);
            Run("duplicate observation retains one owner", DuplicateObservationIsIdempotent, ref passed);
            Run("recovery budget is 2 local 2 current 1 LKG", FixedRecoveryLadder, ref passed);
            Run("confirmed hardware gets one active qualification", ConfirmedHardwareSingleQualification, ref passed);
            Run("UI exit does not interrupt EngineHost", UiExitIsIndependent, ref passed);
            Run("I0026 startup permit failure is recoverable work", I0026StartupFailureRegression, ref passed);
            Run("stage deadline converges instead of staying recovering", StageDeadlineConverges, ref passed);
            Run("concurrent resource faults expand to system owner", ConcurrentScopesExpandToSystem, ref passed);
            Run("operator stop commits owner before stop command", OperatorStopIsTransactional, ref passed);
            Run("operator start requires proof preflight and qualification", OperatorStartIsTransactional, ref passed);
            Run("EngineHost exit bypasses local rebuild and replaces process", EngineHostExitSchedulesReplacement, ref passed);
            Run("recovery budget resets only after stable formal work", StableRunResetsBudget, ref passed);
            Run("10000 generated unknown faults converge", GeneratedUnknownFaultsConverge, ref passed);
            return passed;
        }

        private static void ProtocolContracts()
        {
            var identity = Identity("Channel:4");
            Assert(identity.IsStructurallyValid(), "identity invalid");
            var observation = Observation(identity, ResourceKind.Channel, "Channel:4", true);
            Assert(observation.IsStructurallyValid(), "observation invalid");
            var intent = new RecoveryIntent
            {
                Identity = identity,
                OwnerId = RecoveryProtocolV7.NewId(),
                Stage = RecoveryStage.IntentCommitted,
                DesiredTerminalState = SystemTerminalState.RunningDegraded,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
            Assert(intent.IsStructurallyValid(), "intent invalid");
            var command = new RecoveryCommand
            {
                Identity = identity,
                OwnerId = intent.OwnerId,
                CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = 1,
                Kind = RecoveryCommandKind.DisableOutputs,
                TargetResource = "Channel:4",
                DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks
            };
            command.IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(
                identity, command.Kind, command.CommandSequence);
            Assert(command.IsStructurallyValid(), "command invalid");
        }

        private static void IntentBeforeCommand()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var decision = coordinator.Observe(Observation(
                Identity("Channel:4"), ResourceKind.Channel, "Channel:4", true));
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.Revision == 1, "first durable revision missing");
            Assert(snapshot.ActiveIntents.Length == 1, "intent not durable");
            Assert(snapshot.PendingCommand != null, "command not created");
            Assert(snapshot.ActiveIntents[0].OwnerId == snapshot.PendingCommand.OwnerId,
                "command not bound to owner");
            Assert(decision.Intent.OwnerId == snapshot.ActiveIntents[0].OwnerId,
                "returned owner differs from durable owner");
        }

        private static void UnknownScopeExpandsToSystem()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var observation = Observation(
                Identity("Channel:4"), ResourceKind.Channel, "NeverSeenResource", false);
            var decision = coordinator.Observe(observation);
            Assert(decision.Intent.Identity.ResourceScope == "System", "scope did not expand");
            Assert(decision.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "unknown safety scope did not stay de-energized");
        }

        private static void DuplicateObservationIsIdempotent()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var observation = Observation(
                Identity("Power:4"), ResourceKind.ElectricalGroup, "Power:4", true);
            var first = coordinator.Observe(observation);
            var second = coordinator.Observe(observation);
            Assert(second.Duplicate, "duplicate was not detected");
            Assert(first.Intent.OwnerId == second.Intent.OwnerId, "owner changed");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1, "duplicate owner created");
        }

        private static void FixedRecoveryLadder()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "local-1");
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "local-2");
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "current-1");
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "current-2");
            AssertKind(coordinator, RecoveryCommandKind.ActivateLastKnownGood);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "lkg-1");
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.PendingCommand == null, "recovery storm continued");
            Assert(snapshot.ActiveIntents.Length == 0, "terminal owner leaked");
            Assert(snapshot.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "exhausted recovery did not converge safe");
        }

        private static void ConfirmedHardwareSingleQualification()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var identity = Identity("Channel:4");
            var observation = Observation(identity, ResourceKind.Channel, "Channel:4", true);
            observation.HardwareConfirmed = true;
            coordinator.Observe(observation);
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, false,
                "passive preflight failed");
            AssertKind(coordinator, RecoveryCommandKind.IsolateResource);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, true,
                "Channel:4 permanently isolated");
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.IsolatedResources.Contains("Channel:4"), "hardware scope not isolated");
            Assert(snapshot.DesiredState.State == SystemTerminalState.RunningDegraded,
                "independent channels were not allowed to continue");
        }

        private static void UiExitIsIndependent()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var identity = Identity("UserInterface");
            var observation = Observation(
                identity, ResourceKind.UserInterface, "UserInterface", true);
            observation.FaultCode = "ProcessExited";
            observation.ObservableProperty = "UiPulseMissing";
            var decision = coordinator.Observe(observation);
            Assert(decision.Command.Kind == RecoveryCommandKind.EnsureUserInterface,
                "UI exit requested hardware recovery");
            Assert(decision.DesiredState.State == SystemTerminalState.Running,
                "UI exit changed engine desired state");
            coordinator.AcknowledgeCommand(
                identity.IncidentId, decision.Command.CommandId, true, "UI restarted");
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.Running,
                "engine was interrupted by UI replacement");
        }

        private static void I0026StartupFailureRegression()
        {
            var sessionId = RecoveryProtocolV7.NewId();
            var identity = RecoveryCoordinator.CreateBootstrapIdentity(sessionId, 1, "Power:4");
            Assert(RecoveryProtocolV7.IsGuid(identity.RunId),
                "pre-formal start has no RunId");
            var observation = Observation(
                identity, ResourceKind.ElectricalGroup, "Power:4", true);
            observation.FaultCode = "StartupPositioningException";
            observation.ObservableProperty =
                "ChannelExecutionPermitMissing+SOUR:CURR?Timeout";
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var decision = coordinator.Observe(observation);
            Assert(decision.Reason != "RejectedNoWork", "startup was discarded as no work");
            Assert(decision.Intent.OwnerId.Length == 32, "startup incident has no owner");
            coordinator.AcceptSafetyProof(Proof(identity));
            for (var index = 0; index < 2; index++)
            {
                var failed = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(
                    identity.IncidentId, failed.CommandId, false, "lease/permit state corrupt");
            }
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            var replacement = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, replacement.CommandId, true, "fresh EngineHost");
            var preflight = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, preflight.CommandId, true, "passive safe");
            var qualification = coordinator.Snapshot().PendingCommand;
            Assert(qualification.Kind == RecoveryCommandKind.RunQualificationCycle,
                "qualification was skipped");
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                InterruptedCycleCounted = false
            });
            var resume = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, resume.CommandId, true, "startup recovered");
            var terminal = coordinator.Snapshot();
            Assert(terminal.ActiveIntents.Length == 0, "startup owner leaked");
            Assert(terminal.DesiredState.State == SystemTerminalState.RunningDegraded,
                "I0026 sequence did not recover");
        }

        private static void StageDeadlineConverges()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            var decision = coordinator.Observe(Observation(
                identity, ResourceKind.Channel, "Channel:4", true));
            var expired = coordinator.ReconcileExpiredCommand(
                decision.Command.DeadlineUtcTicks + 1);
            Assert(expired != null, "expired safety stage was ignored");
            var terminal = coordinator.Snapshot();
            Assert(terminal.PendingCommand == null,
                "expired safety stage retained a pending command");
            Assert(terminal.ActiveIntents.Length == 0,
                "expired safety stage retained an owner");
            Assert(terminal.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "expired safety stage did not converge to SafeIdleAlarmed");
        }

        private static void ConcurrentScopesExpandToSystem()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var firstIdentity = Identity("Channel:4");
            var first = coordinator.Observe(Observation(
                firstIdentity, ResourceKind.Channel, "Channel:4", true));
            var firstCommandId = first.Command.CommandId;
            var secondIdentity = Identity("DAQ:Dev2");
            var second = coordinator.Observe(Observation(
                secondIdentity, ResourceKind.DaqDevice, "DAQ:Dev2", true));
            Assert(second.Duplicate, "concurrent fault created a second owner");
            Assert(second.Intent.Identity.ResourceScope == "System",
                "concurrent independent scopes were not expanded to System");
            Assert(second.Command.Kind == RecoveryCommandKind.DisableOutputs,
                "scope expansion did not restart the all-domain safety gate");
            Assert(second.Command.TargetResource == "System",
                "expanded safety command retained a partial target");
            Assert(second.Command.CommandId != firstCommandId,
                "scope expansion reused an obsolete partial-domain command");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1,
                "concurrent faults produced multiple recovery owners");
        }

        private static void GeneratedUnknownFaultsConverge()
        {
            var random = new Random(0x300000);
            for (var sample = 0; sample < 10000; sample++)
            {
                var coordinator = new RecoveryCoordinator(new MemoryJournal());
                var proven = random.Next(0, 3) != 0;
                var identity = Identity(proven ? "Channel:4" : "System");
                var observation = Observation(
                    identity,
                    proven ? ResourceKind.Channel : ResourceKind.System,
                    proven ? "Channel:4" : "Unknown:" + sample,
                    proven);
                observation.FaultCode = "Unknown";
                observation.ObservableProperty = "GeneratedProperty:" + random.Next();
                coordinator.Observe(observation);
                var proof = Proof(identity);
                if (random.Next(0, 4) == 0) proof.PressureSafe = false;
                coordinator.AcceptSafetyProof(proof);
                var steps = 0;
                while (coordinator.Snapshot().ActiveIntents.Length > 0 && steps++ < 16)
                {
                    var snapshot = coordinator.Snapshot();
                    if (snapshot.PendingCommand == null)
                    {
                        var active = snapshot.ActiveIntents[0];
                        if (active.Stage == RecoveryStage.Qualification)
                            coordinator.AcceptQualification(new QualificationReceipt
                            {
                                Identity = active.Identity,
                                ReceiptId = RecoveryProtocolV7.NewId(),
                                QualificationCyclesCompleted = 2,
                                FormalCyclesCompleted = 0,
                                CapturedUtcTicks = DateTime.UtcNow.Ticks
                            });
                        else break;
                        continue;
                    }
                    var command = snapshot.PendingCommand;
                    var succeed = command.Kind == RecoveryCommandKind.RunPassivePreflight ||
                                  command.Kind == RecoveryCommandKind.RunQualificationCycle ||
                                  command.Kind == RecoveryCommandKind.ResumeFormalRun
                        ? random.Next(0, 2) == 0
                        : false;
                    coordinator.AcknowledgeCommand(
                        identity.IncidentId, command.CommandId, succeed, "generated");
                }
                var terminal = coordinator.Snapshot();
                Assert(terminal.ActiveIntents.Length == 0,
                    "generated sequence retained owner at sample " + sample);
                Assert(IsLegalTerminal(terminal.DesiredState?.State ?? SystemTerminalState.Unknown),
                    "illegal terminal at sample " + sample);
            }
        }

        private static void OperatorStopIsTransactional()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var sessionId = RecoveryProtocolV7.NewId();
            var runId = RecoveryProtocolV7.NewId();
            var decision = coordinator.StopByOperator(
                sessionId, runId, 1, "OperatorStopTest");
            var pending = coordinator.Snapshot();
            Assert(decision.Intent != null && decision.Command != null,
                "operator stop owner or command missing");
            Assert(pending.ActiveIntents.Length == 1,
                "operator stop intent was not durable");
            Assert(pending.PendingCommand.Kind == RecoveryCommandKind.StopByOperator,
                "operator stop command missing");
            Assert(pending.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "stop declared terminal before hardware receipt");
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                decision.Command.CommandId,
                true,
                "outputs off");
            var terminal = coordinator.Snapshot();
            Assert(terminal.DesiredState.State == SystemTerminalState.StoppedByOperator,
                "operator stop did not reach terminal after receipt");
            Assert(terminal.ActiveIntents.Length == 0,
                "operator stop owner leaked");
        }

        private static void OperatorStartIsTransactional()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var decision = coordinator.StartByOperator(
                RecoveryProtocolV7.NewId(),
                RecoveryProtocolV7.NewId(),
                1,
                "OperatorStartTest");
            Assert(decision.Command.Kind == RecoveryCommandKind.DisableOutputs,
                "operator start did not begin at safety gate");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1,
                "operator start owner was not durable");
            coordinator.AcceptSafetyProof(Proof(decision.Intent.Identity));
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                command.CommandId,
                true,
                "passive safe");
            AssertKind(coordinator, RecoveryCommandKind.RunQualificationCycle);
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = decision.Intent.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            });
            AssertKind(coordinator, RecoveryCommandKind.ResumeFormalRun);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                command.CommandId,
                true,
                "formal run active");
            var terminal = coordinator.Snapshot();
            Assert(terminal.ActiveIntents.Length == 0,
                "operator start owner leaked");
            Assert(terminal.DesiredState.State == SystemTerminalState.Running,
                "operator start did not converge Running");
        }

        private static void EngineHostExitSchedulesReplacement()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("EngineHost");
            var decision = coordinator.Observe(Observation(
                identity, ResourceKind.EngineHost, "EngineHost", true));
            Assert(decision.Intent.RequiresEngineReplacement,
                "EngineHost process fault was treated as resource rebuild");
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            var replacement = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId,
                replacement.CommandId,
                true,
                "replacement admitted safe idle");
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
        }

        private static void StableRunResetsBudget()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false,
                "first local rebuild failed");
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "second local rebuild succeeded");
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "passive safe");
            var identity = coordinator.Snapshot().ActiveIntents[0].Identity;
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            });
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "formal running");
            var before = coordinator.Snapshot();
            Assert(before.Budgets.Any(value => value.LocalRebuildAttempts == 1),
                "test did not consume recovery budget");
            var stableSince = DateTime.UtcNow.AddMinutes(-11).Ticks;
            Assert(coordinator.ObserveStableProgress(
                    identity.SessionId,
                    identity.RunId,
                    identity.RunEpoch,
                    2,
                    5,
                    stableSince,
                    DateTime.UtcNow.Ticks),
                "qualified stable run did not reset budget");
            Assert(coordinator.Snapshot().Budgets.All(value =>
                    value.LocalRebuildAttempts == 0 &&
                    value.CurrentEngineReplacementAttempts == 0 &&
                    value.LastKnownGoodAttempts == 0 &&
                    value.ActiveQualificationAttempts == 0),
                "recovery budget did not reset atomically");
        }

        private static RecoveryCoordinator StartRecoverableIncident(out string incident)
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            incident = identity.IncidentId;
            coordinator.Observe(Observation(
                identity, ResourceKind.Channel, "Channel:4", true));
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
            return coordinator;
        }

        private static RecoveryIdentity Identity(string scope)
        {
            return new RecoveryIdentity
            {
                SessionId = RecoveryProtocolV7.NewId(),
                RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1,
                IncidentId = RecoveryProtocolV7.NewId(),
                ResourceScope = scope,
                Generation = 1,
                Revision = 1
            };
        }

        private static FaultObservation Observation(
            RecoveryIdentity identity,
            ResourceKind kind,
            string resource,
            bool scopeProven)
        {
            return new FaultObservation
            {
                ObservationId = RecoveryProtocolV7.NewId(),
                Identity = identity,
                ResourceKind = kind,
                ResourceId = resource,
                FaultCode = "Unknown",
                ObservableProperty = "InvariantChanged",
                Severity = FaultSeverity.RecoveryRequired,
                ScopeProven = scopeProven,
                OutputOffConfirmed = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                SafetyChainHealthy = true,
                ObservedUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static SafetyProof Proof(RecoveryIdentity identity)
        {
            return new SafetyProof
            {
                Identity = identity,
                ProofId = RecoveryProtocolV7.NewId(),
                SafetyAgentInstanceId = RecoveryProtocolV7.NewId(),
                OutputsOff = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                OldProcessIsolated = true,
                SafetyChainHealthy = true,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                ValidUntilUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks
            };
        }

        private static bool IsLegalTerminal(SystemTerminalState state)
        {
            return state == SystemTerminalState.Running ||
                   state == SystemTerminalState.RunningDegraded ||
                   state == SystemTerminalState.SafeIdleAlarmed ||
                   state == SystemTerminalState.StoppedByOperator;
        }

        private static void AssertKind(
            RecoveryCoordinator coordinator,
            RecoveryCommandKind expected)
        {
            Assert(coordinator.Snapshot().PendingCommand?.Kind == expected,
                "expected command " + expected + " but got " +
                coordinator.Snapshot().PendingCommand?.Kind);
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class MemoryJournal : IRecoveryKernelJournal
        {
            private readonly object _gate = new object();
            private RecoveryKernelJournalDocument _document =
                new RecoveryKernelJournalDocument
                {
                    Revision = 0,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };

            public RecoveryKernelJournalDocument Load()
            {
                lock (_gate) return _document.Clone();
            }

            public RecoveryJournalCommitResult CompareExchange(
                long expectedRevision,
                RecoveryKernelJournalDocument candidate)
            {
                lock (_gate)
                {
                    if (_document.Revision != expectedRevision)
                        return new RecoveryJournalCommitResult
                        {
                            Conflict = true,
                            Reason = "conflict",
                            Document = _document.Clone()
                        };
                    _document = candidate.Clone();
                    _document.Revision = expectedRevision + 1;
                    _document.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    return new RecoveryJournalCommitResult
                    {
                        Committed = true,
                        Reason = "committed",
                        Document = _document.Clone()
                    };
                }
            }
        }
    }
}
