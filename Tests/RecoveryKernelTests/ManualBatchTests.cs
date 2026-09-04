using System;
using System.IO;
using System.Linq;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        private static RecoveryCoordinator RunningBatch(IRecoveryKernelJournal journal, out EngineStateSnapshot engine)
        {
            var coordinator = new RecoveryCoordinator(journal);
            var start = Operator(out engine, OperatorCommandKind.Start); engine.HardwareInitialized = true;
            coordinator.AdmitOperatorCommand(start, engine);
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity));
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(command.Identity.IncidentId, command.CommandId, true, "preflight");
            coordinator.AcceptQualification(new QualificationReceipt { Identity = coordinator.Snapshot().PendingCommand.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(), QualificationCyclesCompleted = 2, CapturedUtcTicks = DateTime.UtcNow.Ticks });
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(command.Identity.IncidentId, command.CommandId, true, "running");
            engine.State = SystemTerminalState.Running; engine.OutputsEnergized = true; engine.Revision = 5;
            return coordinator;
        }

        private static OperatorCommand ManualCommand(EngineStateSnapshot engine, OperatorCommandKind kind)
        {
            var payload = new ManualBatchCommand { EngineInstanceId = engine.EngineInstanceId,
                PauseIncidentId = engine.RecoveryIncidentId, PauseOwnerId = engine.RecoveryOwnerId };
            return new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = engine.SessionId, RunId = engine.RunId,
                RunEpoch = engine.RunEpoch, BaseRevision = engine.Revision, Kind = kind, ManualBatch = payload,
                PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
        }

        private static void FinishManualPause(RecoveryCoordinator coordinator, EngineStateSnapshot engine)
        {
            var pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "current cycle finished and outputs off");
            engine.State = SystemTerminalState.StoppedByOperator; engine.OutputsEnergized = false; engine.Revision++;
            engine.RecoveryOwnerId = pending.OwnerId; engine.RecoveryIncidentId = pending.Identity.IncidentId;
        }

        private static OperatorCommand ChannelCommand(EngineStateSnapshot engine, int channel, bool pause)
        {
            var command = ManualCommand(engine, pause ? OperatorCommandKind.PauseChannel : OperatorCommandKind.ResumeChannel);
            command.ManualBatch.Channel = channel; command.PayloadSha256 = command.ManualBatch.ComputeSha256();
            return command;
        }

        private static void FinishChannelCommand(RecoveryCoordinator coordinator, EngineStateSnapshot engine)
        {
            var command = coordinator.Snapshot().PendingCommand;
            var bit = 1 << (command.OperatorTransaction.ManualBatch.Channel - 1);
            var pause = command.Kind == RecoveryCommandKind.PauseChannelGracefully;
            engine.ChannelPauseMask = pause ? engine.ChannelPauseMask & ~bit : engine.ChannelPauseMask | bit;
            engine.ChannelResumeMask = pause ? engine.ChannelResumeMask | bit : engine.ChannelResumeMask & ~bit;
            coordinator.AcknowledgeManualChannelCommand(command.Identity.IncidentId, new RecoveryCommandReceipt
            {
                CommandId = command.CommandId, IdempotencyKey = command.IdempotencyKey, Succeeded = true,
                DataBoundaryClosed = true, CompletedUtcTicks = DateTime.UtcNow.Ticks,
                ManualChannels = new ManualChannelState { EngineInstanceId = engine.EngineInstanceId,
                    PauseMask = engine.ChannelPauseMask, ResumeMask = engine.ChannelResumeMask }
            });
            engine.State = coordinator.Snapshot().DesiredState.State; engine.Revision++;
            var owner = coordinator.Snapshot().ActiveIntents.SingleOrDefault();
            engine.RecoveryOwnerId = owner?.OwnerId ?? string.Empty; engine.RecoveryIncidentId = owner?.Identity.IncidentId ?? string.Empty;
        }

        private static void ManualChannelsShareOwner()
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.ManualChannels." + RecoveryProtocolV7.NewId());
            try
            {
                var coordinator = RunningBatch(new FileRecoveryKernelJournal(root, false), out var engine); engine.ChannelPauseMask = 4095;
                var pause = ChannelCommand(engine, 4, true);
                var first = coordinator.AdmitOperatorCommand(pause, engine);
                Assert(first.Accepted && coordinator.Snapshot().PendingCommand.TargetResource == "Channel:4" &&
                    coordinator.Snapshot().PendingCommand.IsStructurallyValid(), "channel target not bound");
                Assert(coordinator.AdmitOperatorCommand(pause, engine).OwnerId == first.OwnerId, "duplicate created owner");
                FinishChannelCommand(coordinator, engine);
                coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false));
                Assert(coordinator.Snapshot().ActiveIntents.Single().ManualPausedChannelsMask == 8 && engine.State == SystemTerminalState.RunningDegraded,
                    "journal lost channel hold or stopped healthy peers");
                Assert(coordinator.AdmitOperatorCommand(ChannelCommand(engine, 12, true), engine).OwnerId == first.OwnerId, "second channel created owner");
                FinishChannelCommand(coordinator, engine);
                Assert(coordinator.AdmitOperatorCommand(ChannelCommand(engine, 4, false), engine).Accepted, "channel resume rejected");
                FinishChannelCommand(coordinator, engine);
                Assert(engine.ChannelResumeMask == 2048 && engine.RecoveryOwnerId == first.OwnerId, "resume released other channel hold");
                Assert(coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Pause), engine).Accepted, "batch pause cannot absorb channel holds");
                FinishManualPause(coordinator, engine);
                Assert(coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Resume), engine).Accepted, "batch resume rejected");
                var pending = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "whole batch resumed");
                Assert(coordinator.Snapshot().ActiveIntents.Length == 0 && coordinator.Snapshot().DesiredState.State == SystemTerminalState.Running,
                    "batch continuation retained manual channel owner");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void ManualChannelsRejectUnsafeContinuation()
        {
            var coordinator = RunningBatch(new MemoryJournal(), out var engine); engine.ChannelPauseMask = 8 | 16;
            Assert(!coordinator.AdmitOperatorCommand(ChannelCommand(engine, 3, true), engine).Accepted, "paused disabled target");
            Assert(coordinator.AdmitOperatorCommand(ChannelCommand(engine, 4, true), engine).Accepted, "eligible target rejected");
            var missingReceiptRejected = false;
            var pending = coordinator.Snapshot().PendingCommand;
            try { coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "untyped receipt"); }
            catch (InvalidOperationException) { missingReceiptRejected = true; }
            Assert(missingReceiptRejected, "channel pause accepted absent completion facts");
            engine.ChannelPauseMask &= ~16; // Healthy peer finished during the target channel's drain.
            FinishChannelCommand(coordinator, engine);
            Assert(engine.State == SystemTerminalState.StoppedByOperator, "all manual channels held still claims running");
            var payload = ChannelCommand(engine, 4, false);
            var foreign = payload.Clone(); foreign.ManualBatch.Channel = 5;
            Assert(!foreign.IsStructurallyValid(), "channel not included in payload hash");
            engine.EngineInstanceId = RecoveryProtocolV7.NewId();
            Assert(!coordinator.AdmitOperatorCommand(payload, engine).Accepted, "replacement reused old channel ticket");
            engine.EngineInstanceId = payload.ManualBatch.EngineInstanceId;
            var fault = coordinator.Snapshot().ActiveIntents.Single().Identity.Clone(); fault.IncidentId = RecoveryProtocolV7.NewId();
            coordinator.Observe(Observation(fault, ResourceKind.System, "System", false));
            Assert(!coordinator.AdmitOperatorCommand(ChannelCommand(engine, 4, false), engine).Accepted &&
                coordinator.Snapshot().PendingCommand.Kind == RecoveryCommandKind.DisableOutputs, "fault kept channel continuation");
        }

        private static void ManualBatchDurableRoundTrip()
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.ManualBatch." + RecoveryProtocolV7.NewId());
            try
            {
                var coordinator = RunningBatch(new FileRecoveryKernelJournal(root, false), out var engine);
                var pause = ManualCommand(engine, OperatorCommandKind.Pause);
                var first = coordinator.AdmitOperatorCommand(pause, engine);
                Assert(first.Accepted && coordinator.Snapshot().ActiveIntents.Single().Stage == RecoveryStage.OperatorPausePending, "pause lacked durable owner");
                Assert(coordinator.AdmitOperatorCommand(pause, engine).OwnerId == first.OwnerId, "duplicate pause changed owner");
                FinishManualPause(coordinator, engine);
                coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false));
                var held = coordinator.Snapshot();
                Assert(held.PendingCommand == null && held.ActiveIntents.Single().Stage == RecoveryStage.OperatorPaused &&
                    held.DesiredState.State == SystemTerminalState.StoppedByOperator && held.ActiveIntents.Single().ManualBatchEngineInstanceId == engine.EngineInstanceId,
                    "reload lost explicit operator pause");
                var resume = ManualCommand(engine, OperatorCommandKind.Resume);
                var resumed = coordinator.AdmitOperatorCommand(resume, engine);
                Assert(resumed.Accepted && resumed.OwnerId == first.OwnerId && resumed.IncidentId == first.IncidentId, "resume created second owner");
                Assert(coordinator.Snapshot().PendingCommand.Kind == RecoveryCommandKind.ResumePausedBatch, "resume restarted or reinitialized batch");
                var pending = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "same batch resumed");
                Assert(coordinator.Snapshot().ActiveIntents.Length == 0 && coordinator.Snapshot().DesiredState.State == SystemTerminalState.Running,
                    "manual continuation leaked owner");
                Assert(coordinator.QueryOperatorCommand(resume).Accepted, "original command query was lost");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void ManualBatchIdentityFence()
        {
            var coordinator = RunningBatch(new MemoryJournal(), out var engine);
            coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Pause), engine);
            FinishManualPause(coordinator, engine);
            var old = ManualCommand(engine, OperatorCommandKind.Resume);
            engine.EngineInstanceId = RecoveryProtocolV7.NewId();
            Assert(!coordinator.AdmitOperatorCommand(old, engine).Accepted, "old engine command resumed replacement");
            var replacement = ManualCommand(engine, OperatorCommandKind.Resume);
            Assert(!coordinator.AdmitOperatorCommand(replacement, engine).Accepted, "replacement inherited in-process pause ticket");
            engine.EngineInstanceId = old.ManualBatch.EngineInstanceId;
            var wrongOwner = ManualCommand(engine, OperatorCommandKind.Resume); wrongOwner.ManualBatch.PauseOwnerId = RecoveryProtocolV7.NewId();
            wrongOwner.PayloadSha256 = wrongOwner.ManualBatch.ComputeSha256();
            Assert(!coordinator.AdmitOperatorCommand(wrongOwner, engine).Accepted, "foreign pause owner resumed");
            Assert(coordinator.Snapshot().ActiveIntents.Single().Stage == RecoveryStage.OperatorPaused, "rejection released pause ownership");
        }

        private static void ManualBatchFaultInvalidation()
        {
            var coordinator = RunningBatch(new MemoryJournal(), out var engine);
            coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Pause), engine);
            FinishManualPause(coordinator, engine);
            var resume = ManualCommand(engine, OperatorCommandKind.Resume);
            var fault = coordinator.Snapshot().ActiveIntents.Single().Identity.Clone(); fault.IncidentId = RecoveryProtocolV7.NewId();
            coordinator.Observe(Observation(fault, ResourceKind.System, "System", false));
            Assert(!coordinator.AdmitOperatorCommand(resume, engine).Accepted, "fault retained manual resume authority");
            var pending = coordinator.Snapshot().PendingCommand;
            Assert(pending.Kind == RecoveryCommandKind.DisableOutputs, "fault did not schedule safety command");
            coordinator.AcceptSafetyProof(Proof(pending.Identity));
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.SafeIdleAlarmed && coordinator.Snapshot().PendingCommand == null,
                "paused fault automatically resumed");
        }

        private static void ManualBatchStopAndDeadline()
        {
            var coordinator = RunningBatch(new MemoryJournal(), out var engine);
            coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Pause), engine);
            var pause = coordinator.Snapshot().PendingCommand;
            coordinator.ReconcileExpiredCommand(pause.DeadlineUtcTicks + 1);
            Assert(coordinator.Snapshot().PendingCommand.Kind == RecoveryCommandKind.DisableOutputs &&
                coordinator.Snapshot().ActiveIntents.Single().AutomaticReplayForbidden, "pause timeout did not fence execution");
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity));
            Assert(coordinator.Snapshot().ActiveIntents.Length == 0, "expired pause stayed unowned recovering");
            coordinator = RunningBatch(new MemoryJournal(), out engine);
            coordinator.AdmitOperatorCommand(ManualCommand(engine, OperatorCommandKind.Pause), engine);
            pause = coordinator.Snapshot().PendingCommand;
            var stopped = coordinator.StopByOperator(engine.SessionId, engine.RunId, engine.RunEpoch, "operator stop");
            var rejected = false;
            try { coordinator.AcknowledgeCommand(pause.Identity.IncidentId, pause.CommandId, true, "late paused"); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && coordinator.Snapshot().PendingCommand.CommandId == stopped.Command.CommandId, "late pause replaced operator stop");
        }
    }
}
