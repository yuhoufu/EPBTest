using System;
using System.IO;
using System.Linq;
using System.Text;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        private sealed class MaintenanceFixture : IDisposable
        {
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "MTTFTest.MaintenanceTests." + Guid.NewGuid().ToString("N"));
            internal readonly IRecoveryKernelJournal Journal;
            internal RecoveryCoordinator Coordinator;
            internal EngineStateSnapshot Engine;
            internal readonly OperatorCommand Stop;
            internal OperatorCommand Begin;
            internal MaintenanceFixture(bool durable = false)
            {
                Journal = durable ? (IRecoveryKernelJournal)new FileRecoveryKernelJournal(Root) : new MemoryJournal();
                Coordinator = new RecoveryCoordinator(Journal);
                Stop = Operator(out Engine, OperatorCommandKind.Stop); Engine.HardwareInitialized = true;
                Engine.FormalCyclesSinceRecovery = 37;
                var admitted = Coordinator.AdmitOperatorCommand(Stop, Engine);
                Coordinator.AcknowledgeCommand(admitted.IncidentId, Pending.CommandId, true, "isolated model stop");
                var seed = Journal.Load(); seed.IsolatedResources = new[] { "Channel:6" };
                seed.Budgets = new[] { new RecoveryBudgetState { ResourceScope = "System", CurrentEngineReplacementAttempts = 1 } };
                Assert(Journal.CompareExchange(seed.Revision, seed).Committed, "maintenance seed failed");
                Begin = Stop.Clone(); Begin.CommandId = RecoveryProtocolV7.NewId(); Begin.Kind = OperatorCommandKind.BeginPressureMaintenance;
                Begin.PressureMaintenance = new PressureMaintenanceCommand { EngineInstanceId = Engine.EngineInstanceId,
                    UiProcessId = 1234, UiProcessStartUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks, HydraulicId = 1 };
                Begin.PayloadSha256 = Begin.PressureMaintenance.ComputeSha256();
            }
            internal RecoveryCommand Pending => Coordinator.Snapshot().PendingCommand;
            internal PressureMaintenanceLease Lease => Coordinator.Snapshot().ActiveIntents.Single().PressureMaintenance;
            internal OperatorCommandAdmission Admit()
            {
                var admitted = Coordinator.AdmitOperatorCommand(Begin, Engine);
                Assert(admitted.Accepted, "maintenance not admitted:" + admitted.Detail);
                Engine.RecoveryIncidentId = admitted.IncidentId; Engine.RecoveryOwnerId = admitted.OwnerId;
                return admitted;
            }
            internal void Prepare()
            {
                Admit(); Coordinator.AcceptSafetyProof(Proof(Pending.Identity));
                Assert(Pending.Kind == RecoveryCommandKind.PreparePressureMaintenance, "preparation skipped safety proof");
                Ack();
            }
            internal OperatorCommand Command(OperatorCommandKind kind, double pressure = 0)
            {
                var lease = Lease; var command = Begin.Clone(); command.CommandId = RecoveryProtocolV7.NewId();
                command.IssuedUtcTicks = DateTime.UtcNow.Ticks; command.Kind = kind;
                command.PressureMaintenance.IncidentId = lease.IncidentId; command.PressureMaintenance.OwnerId = lease.OwnerId;
                command.PressureMaintenance.PressureBar = pressure;
                command.PayloadSha256 = command.PressureMaintenance.ComputeSha256(); return command;
            }
            internal RecoveryCommandReceipt Receipt()
            {
                var pending = Pending; var lease = Lease;
                var output = pending.Kind == RecoveryCommandKind.SetMaintenancePressure;
                return new RecoveryCommandReceipt { CommandId = pending.CommandId, IdempotencyKey = pending.IdempotencyKey,
                    Succeeded = true, CompletedUtcTicks = DateTime.UtcNow.Ticks, Detail = "isolated executor result, no hardware",
                    PressureMaintenance = new PressureMaintenanceExecutionReceipt { EngineInstanceId = lease.EngineInstanceId,
                        LeaseRevision = lease.Revision, LocalLeaseExpiresUtcTicks = lease.ExpiresUtcTicks, HydraulicId = lease.HydraulicId,
                        OutputActive = output, CommandPressureBar = output ? pending.OperatorTransaction.PressureMaintenance.PressureBar : 0,
                        Voltage = output ? 2.5 : 0 } };
            }
            internal void Ack()
            {
                var result = Receipt(); Coordinator.AcknowledgePressureMaintenance(Pending.Identity.IncidentId, result);
                Engine.OutputsEnergized = result.PressureMaintenance.OutputActive;
            }
            internal PressureMaintenanceHeartbeat Heartbeat(long sequence, long now) => new PressureMaintenanceHeartbeat
            {
                SessionId = Lease.SessionId, RunId = Lease.RunId, RunEpoch = Lease.RunEpoch, IncidentId = Lease.IncidentId,
                OwnerId = Lease.OwnerId, EngineInstanceId = Lease.EngineInstanceId, UiProcessId = Lease.UiProcessId,
                UiProcessStartUtcTicks = Lease.UiProcessStartUtcTicks, Generation = Lease.Generation, Sequence = sequence, IssuedUtcTicks = now
            };
            internal void AssertUnchangedHistory()
            {
                var state = Coordinator.Snapshot();
                Assert(state.IsolatedResources.SequenceEqual(new[] { "Channel:6" }) && state.Budgets.Single().CurrentEngineReplacementAttempts == 1 &&
                    Engine.FormalCyclesSinceRecovery == 37, "maintenance changed isolation, recovery budget or historical count");
            }
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }

        private static void MaintenanceRoundTrip(bool durable)
        {
            using (var f = new MaintenanceFixture(durable))
            {
                f.Prepare(); var owner = f.Lease.OwnerId; var incident = f.Lease.IncidentId;
                Assert(f.Pending == null && f.Coordinator.Snapshot().ActiveIntents.Single().Stage == RecoveryStage.PressureMaintenanceReady,
                    "maintenance not held after prepare");
                Assert(f.Coordinator.QueryOperatorCommand(f.Begin).ExecutionSucceeded, "begin final receipt missing");
                f.Coordinator = new RecoveryCoordinator(durable ? new FileRecoveryKernelJournal(f.Root) : f.Journal);
                var output = f.Command(OperatorCommandKind.SetMaintenancePressure, 30);
                Assert(f.Coordinator.AdmitOperatorCommand(output, f.Engine).Accepted, "output rejected");
                var pending = f.Pending;
                Assert(pending.IsStructurallyValid() && pending.Kind == RecoveryCommandKind.SetMaintenancePressure && pending.OwnerId == owner,
                    "output changed owner or contract");
                f.Ack();
                var stopped = f.Command(OperatorCommandKind.StopMaintenanceOutput);
                Assert(f.Coordinator.AdmitOperatorCommand(stopped, f.Engine).Accepted, "output stop rejected"); f.Ack();
                Assert(f.Lease.IsLive(DateTime.UtcNow.Ticks) && !f.Engine.OutputsEnergized, "output stop destroyed maintenance session");
                var end = f.Command(OperatorCommandKind.EndPressureMaintenance);
                Assert(f.Coordinator.AdmitOperatorCommand(end, f.Engine).Accepted, "end rejected");
                Assert(f.Lease.Revoked && f.Pending.Kind == RecoveryCommandKind.DisableOutputs && f.Pending.OwnerId == owner,
                    "end did not revoke before safety command");
                Assert(!f.Coordinator.QueryOperatorCommand(end).ExecutionCompleted, "end claimed safety before proof");
                f.Coordinator.AcceptSafetyProof(Proof(f.Pending.Identity));
                var state = f.Coordinator.Snapshot();
                Assert(state.ActiveIntents.Length == 0 && state.PendingCommand == null && state.DesiredState.State == SystemTerminalState.StoppedByOperator,
                    "maintenance left orphan state or resumed formal work");
                Assert(f.Coordinator.AdmitOperatorCommand(output, null).OwnerId == owner && f.Coordinator.QueryOperatorCommand(end).ExecutionSucceeded &&
                    f.Coordinator.QueryOperatorCommand(end).IncidentId == incident, "replay changed final result");
                f.AssertUnchangedHistory();
            }
        }

        private static void MaintenancePageClosure(bool safe)
        {
            foreach (var outcome in safe ? new[] { "complete" } : new[] { "incomplete", "deadline" }) using (var f = new MaintenanceFixture(true))
            {
                f.Prepare(); var first = f.Command(OperatorCommandKind.EndPressureMaintenance);
                var admitted = f.Coordinator.TryJoinPressureMaintenanceStop(first);
                Assert(admitted.Accepted && !admitted.ExecutionCompleted && f.Lease.Revoked, "end bypassed owner or claimed immediate safety");
                var pending = f.Pending; var second = f.Command(OperatorCommandKind.EndPressureMaintenance);
                var joined = f.Coordinator.TryJoinPressureMaintenanceStop(second);
                Assert(joined.Accepted && joined.OwnerId == admitted.OwnerId && f.Pending.CommandId == pending.CommandId &&
                    f.Pending.DeadlineUtcTicks == pending.DeadlineUtcTicks, "second end reset safety deadline");
                var wrong = second.Clone(); wrong.CommandId = RecoveryProtocolV7.NewId(); wrong.PressureMaintenance.OwnerId = RecoveryProtocolV7.NewId();
                wrong.PayloadSha256 = wrong.PressureMaintenance.ComputeSha256();
                Assert(!f.Coordinator.TryJoinPressureMaintenanceStop(wrong).Accepted && f.Pending.CommandId == pending.CommandId, "wrong owner ended session");
                f.Coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal(f.Root));
                Assert(!f.Coordinator.QueryOperatorCommand(first).ExecutionCompleted && !f.Coordinator.QueryOperatorCommand(second).ExecutionCompleted,
                    "journal reload fabricated completion");
                if (outcome == "deadline") f.Coordinator.ReconcileExpiredCommand(pending.DeadlineUtcTicks + 1);
                else
                {
                    var proof = Proof(pending.Identity); if (!safe) proof.PressureSafe = false;
                    f.Coordinator.AcceptSafetyProof(proof);
                }
                foreach (var command in new[] { first, second })
                {
                    var result = f.Coordinator.QueryOperatorCommand(command);
                    Assert(result.ExecutionCompleted && result.ExecutionSucceeded == safe && result.OwnerId == admitted.OwnerId,
                        "joined page end has no final receipt");
                    Assert(f.Coordinator.AdmitOperatorCommand(command, null).ExecutionSucceeded == safe, "replay changed final page result");
                }
                Assert(f.Coordinator.Snapshot().ActiveIntents.Length == 0 && f.Coordinator.Snapshot().DesiredState.State ==
                    (safe ? SystemTerminalState.StoppedByOperator : SystemTerminalState.SafeIdleAlarmed), "page closure orphaned owner");
                f.AssertUnchangedHistory();
            }
        }

        private static void MaintenanceRequiresStopAndProof()
        {
            using (var f = new MaintenanceFixture())
            {
                f.Engine.State = SystemTerminalState.Running;
                Assert(!f.Coordinator.AdmitOperatorCommand(f.Begin, f.Engine).Accepted, "running maintenance accepted");
                f.Engine.State = SystemTerminalState.StoppedByOperator; f.Begin.CommandId = RecoveryProtocolV7.NewId(); f.Admit();
                var early = f.Command(OperatorCommandKind.SetMaintenancePressure, 10);
                Assert(!f.Coordinator.AdmitOperatorCommand(early, f.Engine).Accepted, "output before safety accepted");
                var wrong = Proof(f.Pending.Identity.Clone()); wrong.Identity.Generation++;
                ExpectMaintenanceFailure(() => f.Coordinator.AcceptSafetyProof(wrong));
                f.Coordinator.AcceptSafetyProof(Proof(f.Pending.Identity));
                ExpectMaintenanceFailure(() => f.Coordinator.AcknowledgeCommand(f.Pending.Identity.IncidentId, f.Pending.CommandId, true, "missing receipt"));
                var receipt = f.Receipt(); receipt.FormalCyclesCompleted = 1;
                ExpectMaintenanceFailure(() => f.Coordinator.AcknowledgePressureMaintenance(f.Pending.Identity.IncidentId, receipt));
                receipt = f.Receipt(); receipt.PressureMaintenance.EngineInstanceId = RecoveryProtocolV7.NewId();
                ExpectMaintenanceFailure(() => f.Coordinator.AcknowledgePressureMaintenance(f.Pending.Identity.IncidentId, receipt));
                f.Ack();
                foreach (var mutation in new Action<PressureMaintenanceCommand>[] { p => p.UiProcessId++, p => p.UiProcessStartUtcTicks++,
                    p => p.OwnerId = RecoveryProtocolV7.NewId(), p => p.EngineInstanceId = RecoveryProtocolV7.NewId(), p => p.HydraulicId = 2 })
                {
                    var command = f.Command(OperatorCommandKind.SetMaintenancePressure, 20); mutation(command.PressureMaintenance);
                    command.PayloadSha256 = command.PressureMaintenance.ComputeSha256();
                    Assert(!f.Coordinator.AdmitOperatorCommand(command, f.Engine).Accepted, "foreign maintenance authority accepted");
                }
                f.AssertUnchangedHistory();
            }
        }

        private static void MaintenanceHeartbeatLimits()
        {
            using (var f = new MaintenanceFixture(true))
            {
                f.Prepare(); var pending = f.Command(OperatorCommandKind.SetMaintenancePressure, 20);
                Assert(f.Coordinator.AdmitOperatorCommand(pending, f.Engine).Accepted, "output rejected");
                var commandKey = f.Pending.IdempotencyKey;
                var now = DateTime.UtcNow.Ticks; var heartbeat = f.Heartbeat(1, now);
                var renewed = f.Coordinator.RenewPressureMaintenance(heartbeat, now);
                Assert(renewed.Revision == 2 && f.Pending.IdempotencyKey == commandKey, "heartbeat rewrote in-flight actuation");
                var revision = f.Coordinator.Snapshot().Revision;
                Assert(f.Coordinator.RenewPressureMaintenance(heartbeat, now + 1).ExpiresUtcTicks == renewed.ExpiresUtcTicks &&
                    f.Coordinator.Snapshot().Revision == revision, "duplicate heartbeat extended lease");
                heartbeat.UiProcessId++; ExpectMaintenanceFailure(() => f.Coordinator.RenewPressureMaintenance(heartbeat, now));
                heartbeat = f.Heartbeat(1, now + 1); ExpectMaintenanceFailure(() => f.Coordinator.RenewPressureMaintenance(heartbeat, now + 1));
                var rateRevision = f.Coordinator.Snapshot().Revision;
                ExpectMaintenanceFailure(() => f.Coordinator.RenewPressureMaintenance(f.Heartbeat(2, now + 1), now + 1));
                Assert(f.Coordinator.Snapshot().Revision == rateRevision, "heartbeat flood wrote journal");
                var absolute = f.Lease.AbsoluteDeadlineUtcTicks;
                long sequence = 2;
                // Model time is injected only into the pure coordinator; no real waiting or output.
                for (var tick = now + TimeSpan.FromSeconds(5).Ticks; tick < absolute; tick += TimeSpan.FromSeconds(5).Ticks)
                    f.Coordinator.RenewPressureMaintenance(f.Heartbeat(sequence++, tick), tick);
                ExpectMaintenanceFailure(() => f.Coordinator.RenewPressureMaintenance(f.Heartbeat(sequence, absolute), absolute));
                f.Coordinator.ReconcilePressureMaintenance(absolute, f.Engine.EngineInstanceId, true);
                Assert(f.Lease.Revoked && f.Pending.Kind == RecoveryCommandKind.DisableOutputs, "absolute lease did not revoke");
                f.Coordinator.AcceptSafetyProof(Proof(f.Pending.Identity));
                Assert(f.Coordinator.Snapshot().DesiredState.State == SystemTerminalState.SafeIdleAlarmed, "expired lease resumed work");
            }
        }

        private static void MaintenanceInterruptions()
        {
            foreach (var interruption in new[] { "ui", "engine", "supervisor", "lease", "fault", "global-stop", "output-stop", "failure", "deadline" })
                using (var f = new MaintenanceFixture())
                {
                    f.Prepare(); var owner = f.Lease.OwnerId;
                    var output = f.Command(OperatorCommandKind.SetMaintenancePressure, 35);
                    f.Coordinator.AdmitOperatorCommand(output, f.Engine);
                    var late = f.Receipt(); var incident = f.Lease.IncidentId;
                    if (interruption == "global-stop")
                    {
                        var stop = f.Stop.Clone(); stop.CommandId = RecoveryProtocolV7.NewId();
                        Assert(f.Coordinator.AdmitOperatorCommand(stop, null).OwnerId == owner, "global stop allocated another owner");
                    }
                    else if (interruption == "output-stop")
                    {
                        var stop = f.Command(OperatorCommandKind.StopMaintenanceOutput);
                        Assert(f.Coordinator.AdmitOperatorCommand(stop, f.Engine).Accepted && EngineHostProtocol.IsPrioritySafetyCommand(f.Pending.Kind),
                            "output stop cannot preempt pending output");
                    }
                    else if (interruption == "failure") f.Coordinator.AcknowledgeCommand(incident, f.Pending.CommandId, false, "unknown executor error");
                    else if (interruption == "deadline") f.Coordinator.ReconcileExpiredCommand(f.Pending.DeadlineUtcTicks + 1);
                    else if (interruption == "fault")
                    {
                        var observation = Observation(f.Pending.Identity.Clone(), ResourceKind.System, "System", false);
                        observation.FaultCode = "Unknown"; observation.ObservableProperty = "PreviouslyUnseenCalibrationProperty";
                        observation.Identity.IncidentId = RecoveryProtocolV7.NewId(); f.Coordinator.Observe(observation);
                        var revision = f.Coordinator.Snapshot().Revision;
                        f.Coordinator.Observe(observation);
                        Assert(f.Coordinator.Snapshot().Revision == revision, "repeated fault rewrote maintenance safety deadline");
                    }
                    else f.Coordinator.ReconcilePressureMaintenance(interruption == "lease" ? f.Lease.ExpiresUtcTicks : DateTime.UtcNow.Ticks,
                        interruption == "engine" ? RecoveryProtocolV7.NewId() : f.Engine.EngineInstanceId, interruption != "ui", interruption == "supervisor");
                    if (interruption == "supervisor")
                        Assert(f.Lease.ExitReason == "MaintenanceSupervisorReplaced", "Supervisor replacement lost the actual revocation reason");
                    Assert(f.Pending.OwnerId == owner && f.Pending.CommandId != late.CommandId, "interruption changed owner or retained old command");
                    Assert(f.Coordinator.QueryOperatorCommand(output).ExecutionCompleted && !f.Coordinator.QueryOperatorCommand(output).ExecutionSucceeded,
                        "interrupted output missing failure result");
                    var revisionAfterStop = f.Coordinator.Snapshot().Revision;
                    // A retired callback may be acknowledged as duplicate, but cannot change the pending safety work.
                    f.Coordinator.AcknowledgePressureMaintenance(incident, late);
                    Assert(f.Pending.CommandId != late.CommandId && f.Coordinator.Snapshot().Revision == revisionAfterStop,
                        "late output displaced stop or caused journal churn");
                    if (interruption == "output-stop") { f.Ack(); Assert(!f.Lease.Revoked, "stop output ended whole session"); }
                    else
                    {
                        f.Coordinator.AcceptSafetyProof(Proof(f.Pending.Identity));
                        Assert(f.Coordinator.Snapshot().ActiveIntents.Length == 0 && f.Coordinator.Snapshot().DesiredState.State ==
                            (interruption == "global-stop" ? SystemTerminalState.StoppedByOperator : SystemTerminalState.SafeIdleAlarmed), "interruption did not converge");
                    }
                    f.AssertUnchangedHistory();
                }
        }

        private static void MaintenanceProtocolAndJournalBinding()
        {
            using (var f = new MaintenanceFixture(true))
            {
                var request = new SupervisorOperatorCommandRequest { Command = f.Begin, RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId() };
                using (var stream = new MemoryStream())
                {
                    request.WriteTo(new BinaryWriter(stream, Encoding.UTF8, true)); stream.Position = 0;
                    var reader = new BinaryReader(stream, Encoding.UTF8, true);
                    var decoded = SupervisorOperatorCommandRequest.ReadBodyFrom(reader, reader.ReadString());
                    Assert(decoded.Command.IsStructurallyValid() && decoded.Command.PayloadSha256 == f.Begin.PayloadSha256, "maintenance wire lost payload");
                    decoded.Command.PressureMaintenance.UiProcessId++; Assert(!decoded.Command.IsStructurallyValid(), "maintenance wire allowed payload mutation");
                }
                f.Admit();
                var candidate = f.Journal.Load(); candidate.ActiveIntents[0].PressureMaintenance.OwnerId = RecoveryProtocolV7.NewId();
                ExpectMaintenanceFailure(() => f.Journal.CompareExchange(candidate.Revision, candidate));
                candidate = f.Journal.Load(); candidate.ActiveIntents = Array.Empty<RecoveryIntent>();
                ExpectMaintenanceFailure(() => f.Journal.CompareExchange(candidate.Revision, candidate));
                candidate = f.Journal.Load(); candidate.PendingCommand.PressureMaintenance.UiProcessId++;
                candidate.PendingCommand.IdempotencyKey = candidate.PendingCommand.ExpectedIdempotencyKey();
                ExpectMaintenanceFailure(() => f.Journal.CompareExchange(candidate.Revision, candidate));
                var proof = Proof(f.Pending.Identity); proof.PressureSafe = false;
                f.Coordinator.AcceptSafetyProof(proof);
                Assert(f.Coordinator.Snapshot().ActiveIntents.Length == 0 && f.Coordinator.Snapshot().DesiredState.State == SystemTerminalState.SafeIdleAlarmed &&
                    !f.Coordinator.QueryOperatorCommand(f.Begin).ExecutionSucceeded, "incomplete proof left unowned maintenance");
            }
        }

        private static void ExpectMaintenanceFailure(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } catch (InvalidDataException) { return; }
            throw new Exception("Expected maintenance authority rejection");
        }
    }
}
