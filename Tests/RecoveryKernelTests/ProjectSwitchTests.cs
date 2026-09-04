using System;
using System.Linq;
using System.IO;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        private static RecoveryCoordinator ProjectSwitchFixture(out MemoryJournal journal, out OperatorCommand command, out EngineStateSnapshot engine)
        {
            journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var stop = Operator(out engine, OperatorCommandKind.Stop);
            engine.HardwareInitialized = true;
            var admission = coordinator.AdmitOperatorCommand(stop, engine);
            var pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(admission.IncidentId, pending.CommandId, true, "stopped");
            var document = journal.Load();
            document.IsolatedResources = new[] { "Channel:6", "ElectricalGroup:4" };
            document.Budgets = new[] { new RecoveryBudgetState { ResourceScope = "System", LocalRebuildAttempts = 2,
                CurrentEngineReplacementAttempts = 1, LastKnownGoodAttempts = 1 } };
            Assert(journal.CompareExchange(document.Revision, document).Committed, "fixture journal setup");
            command = stop.Clone();
            command.CommandId = RecoveryProtocolV7.NewId(); command.Kind = OperatorCommandKind.SwitchProject;
            command.ProjectSwitch = new ProjectSwitchRequest { EngineInstanceId = engine.EngineInstanceId,
                BaseSelectionRevision = 5, BaseConfigurationRevision = 12, BaseConfigurationSha256 = new string('a', 64),
                SourceProjectFileSha256 = new string('b', 64), TargetConfigurationPath = @"D:\IsolatedProject\Target\Config\TestConfig.xml",
                TargetProjectFileSha256 = new string('c', 64) };
            command.PayloadSha256 = command.ProjectSwitch.ComputeSha256();
            return coordinator;
        }

        private static RecoveryCommandReceipt ProjectReceipt(RecoveryCommand command) => new RecoveryCommandReceipt
        {
            CommandId = command.CommandId, IdempotencyKey = command.IdempotencyKey, Succeeded = true,
            OutputsOff = true, DataBoundaryClosed = true, ExecutionAuthorizationRevoked = true,
            CompletedUtcTicks = DateTime.UtcNow.Ticks,
            ProjectSwitch = new ProjectSwitchReceipt { PlanSha256 = command.ProjectSwitch.ComputeSha256(),
                Identity = command.Identity.Clone(), PreparedDocumentSha256 = command.Kind == RecoveryCommandKind.PrepareProjectSwitch
                    ? new string('d', 64) : command.ProjectSwitchPreparedSha256,
                EngineInstanceId = command.Kind == RecoveryCommandKind.PrepareProjectSwitch ? command.ProjectSwitch.SourceEngineInstanceId : RecoveryProtocolV7.NewId(),
                SelectionCommitted = command.Kind == RecoveryCommandKind.ActivateProjectSwitch }
        };

        private static void AssertProjectDocument(RecoveryKernelJournalDocument doc)
        {
            Assert(doc.DesiredState.IsStructurallyValid() && doc.ActiveIntents.All(i => i.IsStructurallyValid()) &&
                (doc.PendingCommand == null || doc.PendingCommand.IsStructurallyValid()) && doc.OperatorAdmissions.All(a => a.IsStructurallyValid()), "invalid durable project document");
            Assert(doc.IsolatedResources.SequenceEqual(new[] { "Channel:6", "ElectricalGroup:4" }) &&
                doc.Budgets.Single().LocalRebuildAttempts == 2 && doc.Budgets.Single().CurrentEngineReplacementAttempts == 1 &&
                doc.Budgets.Single().LastKnownGoodAttempts == 1, "project switch cleared isolation/budget");
        }

        private static void ProjectSwitchRoundTrip()
        {
            ProjectSwitchRoundTrip(false);
        }

        private static void ConfigureCreation(OperatorCommand command)
        {
            command.ProjectSwitch.TargetProjectFileSha256 = string.Empty;
            command.ProjectSwitch.Creation = new ProjectCreationRequest { Configuration = new EngineTestConfiguration
            {
                TestName = "Target", StoreDir = @"D:\IsolatedProject", Owner = "CreationOperator", TestPeriod = 15, TestTarget = 200000,
                Channels = Enumerable.Range(1, 12).Select(channel => new EngineRunnerConfiguration { Channel = channel, TargetTotalCount = 200000 }).ToArray(),
                Hydraulics = new[] { new EngineHydraulicSetting { Id = 1 }, new EngineHydraulicSetting { Id = 2 } }
            } };
            command.PayloadSha256 = command.ProjectSwitch.ComputeSha256();
            Assert(command.IsStructurallyValid(), "new project command fixture invalid");
        }

        private static void ConfigureReset(OperatorCommand command)
        {
            ConfigureCreation(command);
            command.ProjectSwitch.Reset = new ProjectResetRequest { Configuration = command.ProjectSwitch.Creation.Configuration.Clone() };
            command.ProjectSwitch.Creation = null;
            command.ProjectSwitch.TargetProjectFileSha256 = command.ProjectSwitch.SourceProjectFileSha256;
            command.PayloadSha256 = command.ProjectSwitch.ComputeSha256();
            Assert(command.IsStructurallyValid(), "reset command fixture invalid");
        }

        private static void ProjectSwitchRoundTrip(bool creation, bool reset = false)
        {
            var coordinator = ProjectSwitchFixture(out var journal, out var command, out var engine);
            if (creation) ConfigureCreation(command);
            if (reset) ConfigureReset(command);
            var admission = coordinator.AdmitOperatorCommand(command, engine);
            Assert(admission.Accepted && !admission.ExecutionCompleted, "project not admitted");
            var doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            var owner = doc.ActiveIntents.Single().OwnerId;
            Assert(doc.DesiredState.RunId == command.RunId && doc.PendingCommand.Kind == RecoveryCommandKind.DisableOutputs, "changed project before OFF");
            coordinator.AcceptSafetyProof(Proof(doc.PendingCommand.Identity.Clone()));
            doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            var preparedCommand = doc.PendingCommand;
            if (creation || reset)
            {
                var settings = preparedCommand.ProjectSwitch.Creation?.Configuration ?? preparedCommand.ProjectSwitch.Reset.Configuration;
                Assert(settings.Owner == "CreationOperator" && settings.ComputeSha256() ==
                    (command.ProjectSwitch.Creation?.Configuration ?? command.ProjectSwitch.Reset.Configuration).ComputeSha256(), "fresh-run metadata lost from durable plan");
                var mutated = command.Clone();
                (mutated.ProjectSwitch.Creation?.Configuration ?? mutated.ProjectSwitch.Reset.Configuration).TestTarget++;
                Assert(!mutated.IsStructurallyValid(), "creation payload mutation not detected");
                var invalidRejected = false;
                try { coordinator.AdmitOperatorCommand(mutated, engine); }
                catch (ArgumentException ex) { invalidRejected = ex.Message == "OperatorCommandInvalid"; }
                Assert(invalidRejected && coordinator.Snapshot().PendingCommand.CommandId == preparedCommand.CommandId,
                    "invalid creation changed durable work");
                mutated.PayloadSha256 = mutated.ProjectSwitch.ComputeSha256();
                var conflictRejected = false;
                try { coordinator.AdmitOperatorCommand(mutated, engine); }
                catch (InvalidOperationException ex) { conflictRejected = ex.Message == "OperatorCommandIdPayloadConflict"; }
                Assert(conflictRejected && coordinator.Snapshot().PendingCommand.CommandId == preparedCommand.CommandId,
                    "reused creation ID admitted a different valid payload");
            }
            var prepared = ProjectReceipt(preparedCommand);
            var rejected = false;
            try { coordinator.AcknowledgeCommand(admission.IncidentId, preparedCommand.CommandId, true, "string success"); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "generic success bypassed durable preparation receipt");
            coordinator.AcknowledgeProjectSwitch(admission.IncidentId, prepared);
            coordinator = new RecoveryCoordinator(journal);
            doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            Assert(doc.DesiredState.RunId == admission.DestinationRunId && doc.DesiredState.RunEpoch == command.RunEpoch + 1 &&
                doc.ActiveIntents.Single().OwnerId == owner && doc.PendingCommand.Kind == RecoveryCommandKind.ActivateProjectSwitch, "destination not atomic/same owner");
            var activeCommandId = doc.PendingCommand.CommandId;
            Assert(EngineEnvironmentSession.IsExpectedProjectHandoffGap(doc, DateTime.UtcNow.Ticks), "safe project gap becomes a competing missing-engine fault");
            Assert(!EngineEnvironmentSession.IsExpectedProjectHandoffGap(doc, doc.PendingCommand.DeadlineUtcTicks), "project gap bypasses phase deadline");
            var mismatchedGap = doc.Clone(); mismatchedGap.ActiveIntents[0].OwnerId = RecoveryProtocolV7.NewId();
            Assert(!EngineEnvironmentSession.IsExpectedProjectHandoffGap(mismatchedGap, DateTime.UtcNow.Ticks), "unbound owner suppresses missing-engine fault");
            coordinator.AcknowledgeProjectSwitch(admission.IncidentId, prepared);
            Assert(coordinator.Snapshot().PendingCommand.CommandId == activeCommandId, "duplicate prepared changed activation");
            coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(doc.PendingCommand));
            AssertKind(coordinator, RecoveryCommandKind.StopByOperator);
            Assert(!coordinator.QueryOperatorCommand(command).ExecutionCompleted, "activation bypassed final stop");
            doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            coordinator.AcknowledgeCommand(admission.IncidentId, doc.PendingCommand.CommandId, true, "independent final stop complete");
            doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            Assert(doc.ActiveIntents.Length == 0 && doc.PendingCommand == null && doc.DesiredState.State == SystemTerminalState.StoppedByOperator &&
                coordinator.QueryOperatorCommand(command).ExecutionSucceeded, "project did not finish stopped");
            Assert(coordinator.AdmitOperatorCommand(command, engine).ExecutionSucceeded, "old command replay started new switch");
        }

        private static void ProjectSwitchAdmissionFences()
        {
            foreach (var scenario in new[] { "running", "energized", "unavailable", "wrong-engine", "owner", "epoch-overflow" })
            {
                var coordinator = ProjectSwitchFixture(out _, out var command, out var engine);
                if (scenario == "running") engine.State = SystemTerminalState.Running;
                if (scenario == "energized") engine.OutputsEnergized = true;
                if (scenario == "unavailable") engine.HardwareInitialized = false;
                if (scenario == "wrong-engine") command.ProjectSwitch.EngineInstanceId = RecoveryProtocolV7.NewId();
                if (scenario == "owner") engine.RecoveryOwnerId = RecoveryProtocolV7.NewId();
                if (scenario == "epoch-overflow") { command.RunEpoch = long.MaxValue; engine.RunEpoch = long.MaxValue; }
                command.PayloadSha256 = command.ProjectSwitch.ComputeSha256();
                Assert(!coordinator.AdmitOperatorCommand(command, engine).Accepted, "unsafe project admission " + scenario);
                Assert(coordinator.Snapshot().PendingCommand == null, "rejected switch produced command");
            }
            var valid = ProjectSwitchFixture(out _, out var request, out var snapshot);
            var accepted = valid.AdmitOperatorCommand(request, snapshot);
            var second = request.Clone(); second.CommandId = RecoveryProtocolV7.NewId();
            Assert(!valid.AdmitOperatorCommand(second, snapshot).Accepted && valid.Snapshot().ActiveIntents.Single().OwnerId == accepted.OwnerId,
                "second switch obtained owner");
            var tampered = request.Clone(); tampered.ProjectSwitch.TargetProjectFileSha256 = new string('e', 64);
            Assert(!tampered.IsStructurallyValid(), "payload mutation not detected");
        }

        private static void ProjectSwitchReceiptFences()
        {
            foreach (var scenario in new[] { "schema", "plan", "engine", "identity", "key", "counted", "boundary", "future", "empty-archive" })
            {
                var coordinator = ProjectSwitchFixture(out _, out var request, out var engine);
                var accepted = coordinator.AdmitOperatorCommand(request, engine);
                coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
                var command = coordinator.Snapshot().PendingCommand;
                var receipt = ProjectReceipt(command);
                if (scenario == "schema") receipt.SchemaVersion--;
                if (scenario == "plan") receipt.ProjectSwitch.PlanSha256 = new string('e', 64);
                if (scenario == "engine") receipt.ProjectSwitch.EngineInstanceId = RecoveryProtocolV7.NewId();
                if (scenario == "identity") receipt.ProjectSwitch.Identity.RunEpoch++;
                if (scenario == "key") receipt.IdempotencyKey = new string('f', 64);
                if (scenario == "counted") receipt.InterruptedCycleCounted = true;
                if (scenario == "boundary") receipt.DataBoundaryClosed = false;
                if (scenario == "future") receipt.CompletedUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks;
                if (scenario == "empty-archive") receipt.ProjectSwitch.PreparedDocumentSha256 = string.Empty;
                var rejected = false;
                try { coordinator.AcknowledgeProjectSwitch(accepted.IncidentId, receipt); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && coordinator.Snapshot().PendingCommand.CommandId == command.CommandId &&
                    coordinator.Snapshot().DesiredState.RunId == request.RunId, "invalid receipt crossed run boundary " + scenario);
            }
        }

        private static void ProjectSwitchStopJoinsOwner()
        {
            var coordinator = ProjectSwitchFixture(out _, out var request, out var engine);
            var admission = coordinator.AdmitOperatorCommand(request, engine);
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
            coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(coordinator.Snapshot().PendingCommand));
            var pending = coordinator.Snapshot().PendingCommand;
            var stop = request.Clone(); stop.CommandId = RecoveryProtocolV7.NewId(); stop.Kind = OperatorCommandKind.Stop; stop.ProjectSwitch = null;
            stop.PayloadSha256 = SupervisorProtocol.ComputeTextSha256("stop");
            Assert(coordinator.AdmitOperatorCommand(stop, null).Accepted, "old displayed source cannot stop handoff without EngineHost pipe");
            var foreign = stop.Clone(); foreign.CommandId = RecoveryProtocolV7.NewId(); foreign.RunId = RecoveryProtocolV7.NewId();
            Assert(!coordinator.TryJoinProjectSwitchStop(foreign).Accepted, "foreign run stop joined a project owner");
            var destinationStop = stop.Clone(); destinationStop.CommandId = RecoveryProtocolV7.NewId();
            destinationStop.RunId = pending.Identity.RunId; destinationStop.RunEpoch = pending.Identity.RunEpoch;
            Assert(coordinator.TryJoinProjectSwitchStop(destinationStop).Accepted, "destination run stop needs an invented EngineHost snapshot");
            var doc = coordinator.Snapshot(); AssertProjectDocument(doc);
            Assert(doc.PendingCommand.CommandId == pending.CommandId && doc.ActiveIntents.Single().OwnerId == admission.OwnerId &&
                doc.ActiveIntents.Single().ProjectSwitchStopRequested, "stop replaced transaction owner/activation");
            coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(pending));
            coordinator.AcknowledgeCommand(admission.IncidentId, coordinator.Snapshot().PendingCommand.CommandId, true, "stopped");
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.StoppedByOperator &&
                coordinator.QueryOperatorCommand(request).ExecutionCompleted && !coordinator.QueryOperatorCommand(request).ExecutionSucceeded,
                "operator stop lost or auto-started");
        }

        private static void ProjectSwitchFailureAndDeadlines()
        {
            ProjectSwitchFailureAndDeadlines(false);
        }

        private static void ProjectSwitchFailureAndDeadlines(bool reset)
        {
            foreach (var phase in new[] { "proof", "prepare", "activate", "abort", "final-stop" })
            {
                var coordinator = ProjectSwitchFixture(out var journal, out var request, out var engine);
                if (reset) ConfigureReset(request);
                var admission = coordinator.AdmitOperatorCommand(request, engine);
                if (phase != "proof") coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
                if (phase == "activate" || phase == "final-stop")
                    coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(coordinator.Snapshot().PendingCommand));
                if (phase == "final-stop") coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(coordinator.Snapshot().PendingCommand));
                if (phase == "abort")
                {
                    var pending = coordinator.Snapshot().PendingCommand;
                    coordinator.AcknowledgeCommand(admission.IncidentId, pending.CommandId, false, "prepare unavailable");
                    coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
                    AssertKind(coordinator, RecoveryCommandKind.AbortProjectSwitch);
                }
                var expired = coordinator.Snapshot().PendingCommand;
                coordinator.ReconcileExpiredCommand(expired.DeadlineUtcTicks + 1);
                coordinator = new RecoveryCoordinator(journal);
                for (var i = 0; i < 4 && coordinator.Snapshot().PendingCommand != null; i++)
                {
                    var pending = coordinator.Snapshot().PendingCommand;
                    if (pending.Kind == RecoveryCommandKind.DisableOutputs) coordinator.AcceptSafetyProof(Proof(pending.Identity.Clone()));
                    else if (pending.Kind == RecoveryCommandKind.AbortProjectSwitch || pending.Kind == RecoveryCommandKind.StopByOperator)
                        coordinator.AcknowledgeCommand(admission.IncidentId, pending.CommandId, true, "closed safely");
                    else throw new InvalidOperationException("failure scheduled active work " + pending.Kind);
                }
                var doc = coordinator.Snapshot(); AssertProjectDocument(doc);
                Assert(doc.PendingCommand == null && doc.ActiveIntents.Length == 0 &&
                    coordinator.QueryOperatorCommand(request).ExecutionCompleted && !coordinator.QueryOperatorCommand(request).ExecutionSucceeded,
                    "project failure did not converge " + phase);
                Assert(doc.DesiredState.RunId == (phase == "activate" || phase == "final-stop" ? admission.DestinationRunId : request.RunId),
                    "silent run rollback/advance on failure " + phase);
            }
        }

        private static void ProjectSwitchFaultKeepsOwnerAndDeadline()
        {
            var coordinator = ProjectSwitchFixture(out _, out var request, out var engine);
            var admission = coordinator.AdmitOperatorCommand(request, engine);
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
            var identity = coordinator.Snapshot().PendingCommand.Identity.Clone(); identity.IncidentId = RecoveryProtocolV7.NewId();
            coordinator.Observe(Observation(identity, ResourceKind.Channel, "Channel:4", true));
            var pending = coordinator.Snapshot().PendingCommand;
            for (var index = 0; index < 100; index++)
            {
                identity.IncidentId = RecoveryProtocolV7.NewId();
                coordinator.Observe(Observation(identity, ResourceKind.Channel, "Channel:4", true));
            }
            Assert(coordinator.Snapshot().PendingCommand.CommandId == pending.CommandId &&
                coordinator.Snapshot().ActiveIntents.Single().OwnerId == admission.OwnerId, "fault storm renewed deadline/owner");
            coordinator.AcceptSafetyProof(Proof(pending.Identity.Clone()));
            AssertKind(coordinator, RecoveryCommandKind.AbortProjectSwitch);
        }

        private static void ProjectSwitchDurableReload()
        {
            ProjectSwitchDurableReload(false);
        }

        private static void ProjectSwitchDurableReload(bool creation, bool reset = false)
        {
            var initial = ProjectSwitchFixture(out _, out var request, out var engine);
            if (creation) ConfigureCreation(request);
            if (reset) ConfigureReset(request);
            var admission = initial.AdmitOperatorCommand(request, engine);
            var directory = Path.Combine(Path.GetTempPath(), "EPB-ProjectKernel-" + Guid.NewGuid().ToString("N"));
            try
            {
                {
                    var durable = new FileRecoveryKernelJournal(directory);
                    var doc = initial.Snapshot();
                    while (durable.Load().Revision < doc.Revision - 1)
                    {
                        var empty = durable.Load();
                        Assert(durable.CompareExchange(empty.Revision, empty).Committed, "fixture revision advance failed");
                    }
                    Assert(durable.CompareExchange(doc.Revision - 1, doc).Committed, "durable initial import failed");
                    var coordinator = new RecoveryCoordinator(durable);
                    coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity.Clone()));
                    coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(coordinator.Snapshot().PendingCommand));
                    var tampered = coordinator.Snapshot();
                    tampered.PendingCommand.ProjectSwitch.IsolatedResources = new[] { "System" };
                    tampered.PendingCommand.IdempotencyKey = tampered.PendingCommand.ExpectedIdempotencyKey();
                    var rejected = false;
                    try { durable.CompareExchange(tampered.Revision, tampered); }
                    catch (InvalidDataException ex) { rejected = ex.Message == "RecoveryJournalProjectTransactionBindingInvalid"; }
                    Assert(rejected && durable.Load().Revision == tampered.Revision,
                        "journal accepted command/intent plan mismatch or changed revision on rejection");
                }
                {
                    var durable = new FileRecoveryKernelJournal(directory);
                    var coordinator = new RecoveryCoordinator(durable);
                    AssertProjectDocument(coordinator.Snapshot());
                    AssertKind(coordinator, RecoveryCommandKind.ActivateProjectSwitch);
                    if (creation) Assert(coordinator.Snapshot().PendingCommand.ProjectSwitch.Creation.Configuration.Owner == "CreationOperator",
                        "creation metadata lost on DPAPI journal reload");
                    if (reset) Assert(coordinator.Snapshot().PendingCommand.ProjectSwitch.Reset.Configuration.Owner == "CreationOperator",
                        "reset metadata lost on DPAPI journal reload");
                    coordinator.AcknowledgeProjectSwitch(admission.IncidentId, ProjectReceipt(coordinator.Snapshot().PendingCommand));
                    coordinator.AcknowledgeCommand(admission.IncidentId, coordinator.Snapshot().PendingCommand.CommandId, true, "stopped");
                    Assert(coordinator.QueryOperatorCommand(request).ExecutionSucceeded, "durable result lost");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
