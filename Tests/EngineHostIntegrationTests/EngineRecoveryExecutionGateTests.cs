using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineRecoveryExecutionGateTests
    {
        internal static int RunAll()
        {
            StopPreemptsWithoutWaitingForCancellationCallback();
            CompletionDuringFirstOffStillRequiresFinalOff();
            TimeoutDoesNotReleaseOldExecutorOwnership();
            RevisionsAndLaneCapacityAreFenced();
            InitializationIsPreemptedBeforeItsPublication();
            InitializationCannotEnterAfterCommandAdmission();
            Console.WriteLine("PASS 6 engine executor fence tests (isolated, no physical hardware)");
            return 6;
        }

        private static void InitializationIsPreemptedBeforeItsPublication()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var initialization = gate.BeginInitialization(CancellationToken.None))
            using (var stop = gate.Begin(Command(1, RecoveryCommandKind.StopByOperator), CancellationToken.None))
            {
                var offCalls = 0; var released = false;
                var pending = stop.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(++offCalls), CancellationToken.None,
                    (_, token) => { released = true; return Task.CompletedTask; });
                Assert(offCalls == 1 && !pending.IsCompleted && !released, "STOP waited to issue OFF or skipped initialization ownership");
                Assert(!initialization.PublishIfCurrent(() => throw new Exception("late ready publication")), "initialization cleared STOP owner");
                Assert(SpinWait.SpinUntil(() => initialization.Token.IsCancellationRequested, 3000), "initialization not cancelled");
                initialization.Dispose();
                Assert(pending.Wait(3000) && offCalls == 2 && released, "STOP did not join initialization before final OFF/handoff");
                Assert(stop.PublishIfCurrent(() => { }), "STOP publication lost ownership");
            }
        }

        private static void InitializationCannotEnterAfterCommandAdmission()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (gate.BeginInitialization(CancellationToken.None)) { }
            Reject(() => gate.BeginInitialization(CancellationToken.None), "EngineInitializationAlreadyAdmittedOrSuperseded");
            var commanded = new EngineRecoveryExecutionGate();
            using (commanded.Begin(Command(1, RecoveryCommandKind.StopByOperator), CancellationToken.None))
                Reject(() => commanded.BeginInitialization(CancellationToken.None), "EngineInitializationAlreadyAdmittedOrSuperseded");
        }

        private static void StopPreemptsWithoutWaitingForCancellationCallback()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var normal = gate.Begin(Command(1, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None))
            using (normal.Token.Register(() => { entered.Set(); release.Wait(5000); }))
            {
                try
                {
                    using (var stop = gate.Begin(Command(2, RecoveryCommandKind.StopByOperator), CancellationToken.None))
                    {
                        Assert(entered.Wait(3000), "cancel callback was not invoked");
                        var offCalls = 0;
                        var pending = stop.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(++offCalls), CancellationToken.None);
                        Assert(offCalls == 1 && !pending.IsCompleted, "first OFF waited for old cleanup or claimed premature quiescence");
                        Assert(!normal.PublishIfCurrent(() => throw new Exception("stale Running published")), "old publication was not fenced");
                        release.Set(); normal.Dispose();
                        Assert(pending.Wait(3000) && pending.Result == 2, "missing post-cleanup OFF");
                        Assert(stop.PublishIfCurrent(() => { }), "stop publication was rejected");
                    }
                }
                finally { release.Set(); }
            }
        }

        private static void CompletionDuringFirstOffStillRequiresFinalOff()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var normal = gate.Begin(Command(1, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None))
            using (var stop = gate.Begin(Command(2, RecoveryCommandKind.DisableOutputs), CancellationToken.None))
            {
                var calls = 0;
                var result = stop.ExecuteWithQuiescentBoundaryAsync(_ =>
                {
                    if (++calls == 1) normal.Dispose();
                    return Task.FromResult(calls);
                }, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result == 2, "old completion during initial OFF skipped final boundary");
            }
        }

        private static void TimeoutDoesNotReleaseOldExecutorOwnership()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var normal = gate.Begin(Command(1, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None))
            {
                using (var stop = gate.Begin(Command(2, RecoveryCommandKind.StopByOperator), CancellationToken.None))
                using (var deadline = new CancellationTokenSource())
                {
                    var calls = 0;
                    var pending = stop.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(++calls), deadline.Token);
                    deadline.Cancel();
                    var rejected = false;
                    try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                    Assert(rejected && calls == 1 && !stop.InterruptedExecutor.IsCompleted, "timeout fabricated old executor exit");
                }
                Reject(() => gate.Begin(Command(3, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None), "RecoveryExecutorStillOwned");
                Assert(!normal.IsCurrent, "expired stop restored old execution permission");
            }
            using (var next = gate.Begin(Command(3, RecoveryCommandKind.RunPassivePreflight), CancellationToken.None))
                Assert(next.IsCurrent, "actual executor exit did not free lane");
        }

        private static void RevisionsAndLaneCapacityAreFenced()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var first = gate.Begin(Command(1, RecoveryCommandKind.RebuildResource), CancellationToken.None))
            {
                Reject(() => gate.Begin(Command(3, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None), "RecoveryExecutorStillOwned");
                using (var stop = gate.Begin(Command(2, RecoveryCommandKind.StopByOperator), CancellationToken.None))
                {
                    Reject(() => gate.Begin(Command(3, RecoveryCommandKind.DisableOutputs), CancellationToken.None), "RecoveryExecutorStillOwned");
                    Assert(stop.IsCurrent, "rejected command advanced fence");
                }
            }
            Reject(() => gate.Begin(Command(1, RecoveryCommandKind.ResumeFormalRun), CancellationToken.None), "RecoveryCommandRetiredByNewerRevision");
            Reject(() => gate.Begin(Command(2, RecoveryCommandKind.StopByOperator), CancellationToken.None), "RecoveryCommandRetiredByNewerRevision");
            using (var next = gate.Begin(Command(3, RecoveryCommandKind.RunPassivePreflight), CancellationToken.None))
            {
                var calls = 0;
                next.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(++calls), CancellationToken.None).GetAwaiter().GetResult();
                Assert(calls == 1, "normal command executed twice");
            }
        }

        private static RecoveryCommand Command(long sequence, RecoveryCommandKind kind)
        {
            var identity = new RecoveryIdentity { SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1, IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = sequence };
            return new RecoveryCommand { Identity = identity, CommandId = RecoveryProtocolV7.NewId(), OwnerId = RecoveryProtocolV7.NewId(),
                CommandSequence = sequence, Kind = kind, TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(10).Ticks,
                IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(identity, kind, sequence) };
        }

        private static void Reject(Func<IDisposable> action, string reason)
        {
            try { using (action()) { } }
            catch (InvalidOperationException ex) { Assert(ex.Message == reason, "unexpected rejection: " + ex.Message); return; }
            throw new InvalidOperationException("accepted: " + reason);
        }
        private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    }
}
