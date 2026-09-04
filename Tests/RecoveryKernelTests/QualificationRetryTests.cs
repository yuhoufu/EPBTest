using System;
using System.IO;
using System.Linq;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        private static void QualificationRetryRoundTrip()
        {
            var coordinator = SeedIsolatedChannel(4, 0, out var engine);
            var command = QualificationRetryCommand(engine, 4);
            Assert(command.IsStructurallyValid(), "retry command invalid");
            Assert(QualificationRetryWireRoundTrip(command).ManualBatch.Channel == 4, "wire lost retry target");
            var admitted = coordinator.AdmitOperatorCommand(command, engine);
            var first = coordinator.Snapshot();
            Assert(admitted.Accepted && first.ActiveIntents.Single().OwnerId == admitted.OwnerId &&
                first.PendingCommand.Kind == RecoveryCommandKind.DisableOutputs && first.PendingCommand.TargetResource == "Channel:4" &&
                first.Budgets.Single().ActiveQualificationAttempts == 1, "retry did not atomically commit owner safety command and budget");
            Assert(coordinator.AdmitOperatorCommand(command, engine).OwnerId == admitted.OwnerId,
                "duplicate retry created a second owner");

            coordinator.AcceptSafetyProof(Proof(first.PendingCommand.Identity));
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            var pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "isolated channel passive checks passed");
            AssertKind(coordinator, RecoveryCommandKind.RunQualificationCycle);
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = pending.Identity.Clone(), ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2, FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks, InterruptedCycleCounted = false
            });
            AssertKind(coordinator, RecoveryCommandKind.ResumeFormalRun);
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true,
                "qualified channel and healthy peers formally authorized");
            var finished = coordinator.Snapshot();
            Assert(finished.ActiveIntents.Length == 0 && finished.PendingCommand == null &&
                finished.DesiredState.State == SystemTerminalState.Running &&
                !finished.IsolatedResources.Contains("Channel:4", StringComparer.OrdinalIgnoreCase) &&
                finished.Budgets.Single().ActiveQualificationAttempts == 1,
                "successful retry did not reintegrate exactly one resource or released budget early");
        }

        private static void QualificationRetryFailureIsPermanent()
        {
            var coordinator = SeedIsolatedChannel(4, 0, out var engine);
            coordinator.AdmitOperatorCommand(QualificationRetryCommand(engine, 4), engine);
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity));
            var pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "preflight passed");
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, false, "qualification property failed");
            var barrier = coordinator.Snapshot();
            Assert(barrier.PendingCommand.Kind == RecoveryCommandKind.DisableOutputs &&
                barrier.ActiveIntents.Single().CommandAfterSafetyProof == RecoveryCommandKind.IsolateResource &&
                barrier.ActiveIntents.Single().OperatorTransaction == null,
                "failed active qualification skipped its new safety boundary or retained replay command");
            coordinator.AcceptSafetyProof(Proof(barrier.PendingCommand.Identity));
            AssertKind(coordinator, RecoveryCommandKind.IsolateResource);
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "isolation reasserted");
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, false, "NoRemainingFormalWork");
            var finished = coordinator.Snapshot();
            Assert(finished.DesiredState.State == SystemTerminalState.SafeIdleAlarmed && finished.ActiveIntents.Length == 0 &&
                finished.IsolatedResources.Contains("Channel:4") && coordinator.QualificationRetryEligibleScopes().Length == 0,
                "failed retry was not permanently isolated and bounded");
        }

        private static void QualificationRetryAdmissionFences()
        {
            var exhausted = SeedIsolatedChannel(4, 1, out var engine);
            Assert(!exhausted.AdmitOperatorCommand(QualificationRetryCommand(engine, 4), engine).Accepted &&
                exhausted.Snapshot().ActiveIntents.Length == 0, "exhausted retry budget accepted");

            var exact = SeedIsolatedChannel(4, 0, out engine);
            var foreign = QualificationRetryCommand(engine, 4);
            foreign.ManualBatch.EngineInstanceId = RecoveryProtocolV7.NewId();
            foreign.PayloadSha256 = foreign.ManualBatch.ComputeSha256();
            Assert(!exact.AdmitOperatorCommand(foreign, engine).Accepted, "foreign EngineHost requested qualification");

            var journal = new MemoryJournal();
            var desired = SeedDocument(engine, "Power:2", 0);
            journal.CompareExchange(0, desired);
            var shared = new RecoveryCoordinator(journal);
            Assert(shared.QualificationRetryEligibleScopes().Length == 0 &&
                !shared.AdmitOperatorCommand(QualificationRetryCommand(engine, 4), engine).Accepted,
                "channel button split a shared isolation scope");
        }

        private static RecoveryCoordinator SeedIsolatedChannel(int channel, int qualificationAttempts, out EngineStateSnapshot engine)
        {
            var command = Operator(out engine, OperatorCommandKind.Stop);
            engine.State = SystemTerminalState.SafeIdleAlarmed;
            engine.IsolatedResources = new[] { "Channel:" + channel };
            var journal = new MemoryJournal();
            journal.CompareExchange(0, SeedDocument(engine, "Channel:" + channel, qualificationAttempts));
            return new RecoveryCoordinator(journal);
        }

        private static RecoveryKernelJournalDocument SeedDocument(EngineStateSnapshot engine, string scope, int attempts)
        {
            return new RecoveryKernelJournalDocument
            {
                Revision = 0, UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                DesiredState = new SystemDesiredState
                {
                    SessionId = engine.SessionId, RunId = engine.RunId, RunEpoch = engine.RunEpoch,
                    Revision = 1, State = SystemTerminalState.SafeIdleAlarmed,
                    Reason = "isolated fixture", UpdatedUtcTicks = DateTime.UtcNow.Ticks
                },
                IsolatedResources = new[] { scope },
                Budgets = new[] { new RecoveryBudgetState { ResourceScope = scope, ActiveQualificationAttempts = attempts } }
            };
        }

        private static OperatorCommand QualificationRetryCommand(EngineStateSnapshot engine, int channel)
        {
            var payload = new ManualBatchCommand { EngineInstanceId = engine.EngineInstanceId, Channel = channel };
            return new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = engine.SessionId, RunId = engine.RunId,
                RunEpoch = engine.RunEpoch, BaseRevision = engine.Revision, Kind = OperatorCommandKind.RetryQualification,
                ManualBatch = payload, PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static OperatorCommand QualificationRetryWireRoundTrip(OperatorCommand command)
        {
            var request = new SupervisorOperatorCommandRequest
            {
                RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                RequesterProcessId = 1, RequesterProcessStartUtcTicks = 1, Command = command
            };
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) request.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                    return SupervisorOperatorCommandRequest.ReadBodyFrom(reader, SupervisorProtocol.ReadRequestMagic(reader)).Command;
            }
        }
    }
}
