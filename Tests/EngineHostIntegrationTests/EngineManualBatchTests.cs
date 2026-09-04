using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineManualBatchTests
    {
        internal static int RunAll()
        {
            PauseDrainsBeforeTicketAndPreservesCounts();
            FaultInvalidatesPendingPauseAndResume();
            ReplacementCannotInheritContinuation();
            ChannelTicketsAndBatchInteroperate();
            ChannelFailureRetiresAllContinuation();
            Console.WriteLine("PASS 5 manual batch/channel executor tests (isolated, no hardware)"); return 5;
        }

        private static void PauseDrainsBeforeTicketAndPreservesCounts()
        {
            var executor = new EngineManualBatchContinuation();
            var pause = PauseCommand();
            var cycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var flush = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var flushing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long formal = 21508, mechanical = 21510;
            var pending = executor.PauseAsync(pause, async _ => { await cycle.Task; formal++; mechanical++; },
                async _ => { flushing.SetResult(true); await flush.Task; }, CancellationToken.None);
            Assert(!executor.IsPaused && !pending.IsCompleted && formal == 21508, "pause reset count or skipped current cycle");
            cycle.SetResult(true);
            Assert(flushing.Task.Wait(3000) && !executor.IsPaused && !pending.IsCompleted, "pause skipped durable drain");
            flush.SetResult(true); Assert(pending.Wait(3000) && executor.IsPaused, "pause ticket missing after drain");
            var resume = ResumeCommand(pause);
            var calls = 0;
            executor.ResumeAsync(resume, _ => { calls++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!executor.IsPaused && calls == 1 && formal == 21509 && mechanical == 21511, "resume changed historical counts");
            Reject(() => executor.ResumeAsync(resume, _ => throw new Exception("duplicate resume"), _ => Task.CompletedTask, CancellationToken.None));
        }

        private static void FaultInvalidatesPendingPauseAndResume()
        {
            var executor = new EngineManualBatchContinuation(); var pause = PauseCommand();
            var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = executor.PauseAsync(pause, _ => finish.Task, _ => throw new Exception("faulted pause persisted as healthy"), CancellationToken.None);
            executor.Invalidate(); finish.SetResult(true); Reject(() => pending);
            Assert(!executor.IsPaused, "faulted pause retained continuation");
            executor.PauseAsync(pause, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Reject(() => executor.ResumeAsync(ResumeCommand(pause), _ => { executor.Invalidate(); return Task.CompletedTask; },
                _ => throw new Exception("faulted resume persisted as running"), CancellationToken.None));
            Assert(!executor.IsPaused, "faulted resume retained ticket");
        }

        private static void ReplacementCannotInheritContinuation()
        {
            var executor = new EngineManualBatchContinuation(); var pause = PauseCommand();
            executor.PauseAsync(pause, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            var resume = ResumeCommand(pause);
            Reject(() => new EngineManualBatchContinuation().ResumeAsync(resume, _ => throw new Exception("replacement energized"),
                _ => Task.CompletedTask, CancellationToken.None));
            executor.Invalidate();
            Reject(() => executor.ResumeAsync(resume, _ => throw new Exception("stop ticket reused"), _ => Task.CompletedTask, CancellationToken.None));
        }

        internal static RecoveryCommand ForChannel(RecoveryCommand command, int channel, bool pause)
        {
            command.Kind = pause ? RecoveryCommandKind.PauseChannelGracefully : RecoveryCommandKind.ResumePausedChannel;
            command.TargetResource = "Channel:" + channel;
            command.OperatorTransaction.Kind = pause ? OperatorCommandKind.PauseChannel : OperatorCommandKind.ResumeChannel;
            command.OperatorTransaction.ManualBatch.Channel = channel;
            command.OperatorTransaction.PayloadSha256 = command.OperatorTransaction.ManualBatch.ComputeSha256();
            command.IdempotencyKey = command.ExpectedIdempotencyKey(); return command;
        }

        private static void ChannelTicketsAndBatchInteroperate()
        {
            var executor = new EngineManualBatchContinuation(); var pause4 = ForChannel(PauseCommand(), 4, true);
            executor.PauseAsync(pause4, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Assert(executor.ChannelMask == 8 && !executor.IsPaused, "channel pause converted to global pause");
            var pause12 = ForChannel(ResumeCommand(pause4), 12, true);
            executor.PauseAsync(pause12, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            var resume4 = ForChannel(ResumeCommand(pause4), 4, false);
            executor.ResumeAsync(resume4, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Assert(executor.ChannelMask == 2048, "resuming channel 4 changed channel 12");
            Reject(() => executor.ResumeAsync(resume4, _ => throw new Exception("duplicate"), _ => Task.CompletedTask, CancellationToken.None));
            var whole = ResumeCommand(pause12); whole.Kind = RecoveryCommandKind.PauseBatchGracefully;
            whole.OperatorTransaction.Kind = OperatorCommandKind.Pause; whole.OperatorTransaction.ManualBatch.Channel = 0;
            whole.OperatorTransaction.PayloadSha256 = whole.OperatorTransaction.ManualBatch.ComputeSha256(); whole.IdempotencyKey = whole.ExpectedIdempotencyKey();
            executor.PauseAsync(whole, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Assert(executor.IsPaused && executor.ChannelMask == 0, "batch pause lost continuation state");
            executor.ResumeAsync(ResumeCommand(whole), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!executor.IsPaused && executor.ChannelMask == 0, "batch resume retained channel tickets");
        }

        private static void ChannelFailureRetiresAllContinuation()
        {
            var executor = new EngineManualBatchContinuation(); var pause = ForChannel(PauseCommand(), 4, true);
            executor.PauseAsync(pause, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
            var next = ForChannel(ResumeCommand(pause), 7, true);
            Reject(() => executor.PauseAsync(next, _ => Task.CompletedTask, _ => throw new InvalidOperationException("disk boundary failed"), CancellationToken.None));
            Assert(executor.ChannelMask == 0, "failed persistence left reusable channel ticket");
            Reject(() => executor.ResumeAsync(ForChannel(ResumeCommand(pause), 4, false), _ => throw new Exception("must not energize"), _ => Task.CompletedTask, CancellationToken.None));
        }

        internal static RecoveryCommand PauseCommand()
        {
            var identity = new RecoveryIdentity { SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1,
                IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = 1 };
            var payload = new ManualBatchCommand { EngineInstanceId = RecoveryProtocolV7.NewId() };
            var command = new RecoveryCommand { Identity = identity, OwnerId = RecoveryProtocolV7.NewId(), CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = 1, Kind = RecoveryCommandKind.PauseBatchGracefully, TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks,
                OperatorTransaction = new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = identity.SessionId, RunId = identity.RunId,
                    RunEpoch = 1, BaseRevision = 1, IssuedUtcTicks = DateTime.UtcNow.Ticks, Kind = OperatorCommandKind.Pause,
                    ManualBatch = payload, PayloadSha256 = payload.ComputeSha256() } };
            command.IdempotencyKey = command.ExpectedIdempotencyKey(); return command;
        }

        internal static RecoveryCommand ResumeCommand(RecoveryCommand pause)
        {
            var command = new RecoveryCommand { Identity = pause.Identity.Clone(), OwnerId = pause.OwnerId,
                CommandId = RecoveryProtocolV7.NewId(), CommandSequence = pause.CommandSequence + 1, Kind = RecoveryCommandKind.ResumePausedBatch,
                TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks, OperatorTransaction = pause.OperatorTransaction.Clone() };
            command.Identity.Revision++; command.OperatorTransaction.CommandId = RecoveryProtocolV7.NewId();
            command.OperatorTransaction.Kind = OperatorCommandKind.Resume;
            command.OperatorTransaction.ManualBatch.PauseOwnerId = pause.OwnerId;
            command.OperatorTransaction.ManualBatch.PauseIncidentId = pause.Identity.IncidentId;
            command.OperatorTransaction.PayloadSha256 = command.OperatorTransaction.ManualBatch.ComputeSha256();
            command.IdempotencyKey = command.ExpectedIdempotencyKey(); return command;
        }

        private static void Reject(Func<Task> action)
        {
            try { action().GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("retired manual batch operation was accepted");
        }
        private static void Assert(bool value, string reason) { if (!value) throw new Exception(reason); }
    }
}
