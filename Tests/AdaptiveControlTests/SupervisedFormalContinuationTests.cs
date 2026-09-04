using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class SupervisedFormalContinuationTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Check(ref passed, "V3 资格完成不运行正式圈，只有后续授权执行一次", () =>
            {
                var ticket = new SupervisedFormalContinuation();
                var qualification = Command();
                var version = ticket.BeginQualification(qualification);
                var calls = 0;
                var resume = Resume(qualification);
                Reject(() => ticket.CommitAsync(resume, CancellationToken.None).GetAwaiter().GetResult());
                ticket.CompleteQualification(version, (validate, token) =>
                {
                    validate(); calls++; return Task.FromResult(new[] { 4, 5, 7, 12 });
                });
                Assert(calls == 0, "qualification started formal execution");
                var started = ticket.CommitAsync(resume, CancellationToken.None).GetAwaiter().GetResult();
                Assert(calls == 1 && started.SequenceEqual(new[] { 4, 5, 7, 12 }), "wrong formal channels");
                Reject(() => ticket.CommitAsync(resume, CancellationToken.None).GetAwaiter().GetResult());
                Reject(() => ticket.CompleteQualification(version, (validate, token) => Task.FromResult(new[] { 4 })));
                Assert(calls == 1, "duplicate armed timers");
            });
            Check(ref passed, "V3 正式启动拒绝错误 Owner、Session、Run、Epoch、Incident、Scope、Generation 和旧序号", () =>
            {
                Action<RecoveryCommand>[] corruptions =
                {
                    command => command.OwnerId = RecoveryProtocolV7.NewId(),
                    command => command.Identity.SessionId = RecoveryProtocolV7.NewId(),
                    command => command.Identity.RunId = RecoveryProtocolV7.NewId(),
                    command => command.Identity.RunEpoch++,
                    command => command.Identity.IncidentId = RecoveryProtocolV7.NewId(),
                    command => command.Identity.ResourceScope = "Channel:4",
                    command => command.Identity.Generation++,
                    command => command.Identity.Revision = 1,
                    command => command.CommandSequence = 1,
                    command => command.TargetResource = "Channel:5",
                    command => command.DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(-1).Ticks
                };
                foreach (var corrupt in corruptions)
                {
                    var ticket = new SupervisedFormalContinuation();
                    var qualification = Command();
                    var version = ticket.BeginQualification(qualification);
                    var calls = 0;
                    ticket.CompleteQualification(version, (validate, token) => { calls++; return Task.FromResult(new[] { 4 }); });
                    var resume = Resume(qualification); corrupt(resume); resume.IdempotencyKey = resume.ExpectedIdempotencyKey();
                    Reject(() => ticket.CommitAsync(resume, CancellationToken.None).GetAwaiter().GetResult());
                    Assert(calls == 0, "mismatched authorization executed");
                }
            });
            Check(ref passed, "V3 停止撤销未完成和已完成资格，迟到回调不能复活", () =>
            {
                foreach (var completed in new[] { false, true })
                {
                    var ticket = new SupervisedFormalContinuation(); var qualification = Command();
                    var version = ticket.BeginQualification(qualification);
                    if (completed) ticket.CompleteQualification(version, (validate, token) => Task.FromResult(new[] { 4 }));
                    ticket.Invalidate();
                    Reject(() => ticket.CompleteQualification(version, (validate, token) => Task.FromResult(new[] { 4 })));
                    Reject(() => ticket.CommitAsync(Resume(qualification), CancellationToken.None).GetAwaiter().GetResult());
                    var replacement = new SupervisedFormalContinuation();
                    Reject(() => replacement.CommitAsync(Resume(qualification), CancellationToken.None).GetAwaiter().GetResult());
                }
            });
            Check(ref passed, "V3 正式提交失败或取消不重复执行已消费资格", () =>
            {
                foreach (var canceled in new[] { false, true })
                using (var cancellation = new CancellationTokenSource())
                {
                    var ticket = new SupervisedFormalContinuation(); var qualification = Command();
                    var version = ticket.BeginQualification(qualification);
                    var calls = 0;
                    ticket.CompleteQualification(version, (validate, token) =>
                    { calls++; throw new InvalidOperationException("synthetic power failure"); });
                    if (canceled) cancellation.Cancel();
                    Reject(() => ticket.CommitAsync(Resume(qualification), cancellation.Token).GetAwaiter().GetResult());
                    Reject(() => ticket.CommitAsync(Resume(qualification), CancellationToken.None).GetAwaiter().GetResult());
                    Assert(calls == (canceled ? 0 : 1), "failed continuation was replayed");
                }
            });
            Check(ref passed, "V3 正式提交预检中停止立即阻止后续上电", () =>
            {
                var ticket = new SupervisedFormalContinuation(); var qualification = Command();
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var energized = false;
                ticket.CompleteQualification(ticket.BeginQualification(qualification), async (validate, token) =>
                {
                    entered.SetResult(true); await release.Task.ConfigureAwait(false);
                    validate(); energized = true; return new[] { 4 };
                });
                var commit = ticket.CommitAsync(Resume(qualification), CancellationToken.None);
                Assert(entered.Task.Wait(1000), "executor was not entered");
                ticket.Invalidate(); release.SetResult(true);
                Reject(() => commit.GetAwaiter().GetResult());
                Assert(!energized, "stop did not revoke pending power enable");
            });
            Check(ref passed, "V3 两个并发正式授权只有一个能消费资格", () =>
            {
                var ticket = new SupervisedFormalContinuation(); var qualification = Command();
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                ticket.CompleteQualification(ticket.BeginQualification(qualification), async (validate, token) =>
                { Interlocked.Increment(ref calls); await release.Task.ConfigureAwait(false); validate(); return new[] { 4 }; });
                var first = ticket.CommitAsync(Resume(qualification), CancellationToken.None);
                Reject(() => ticket.CommitAsync(Resume(qualification), CancellationToken.None).GetAwaiter().GetResult());
                release.SetResult(true); first.GetAwaiter().GetResult();
                Assert(calls == 1, "concurrent formal starts");
            });
            Check(ref passed, "V3 DAQ 正式预检要求同代际新增三次有效采集", () =>
            {
                long sequence = 100;
                SupervisedFormalDaqProbe.VerifyAsync(7, () => Snapshot(++sequence), CancellationToken.None).GetAwaiter().GetResult();
                Reject(() => SupervisedFormalDaqProbe.VerifyAsync(7, () => Snapshot(100), CancellationToken.None, 20).GetAwaiter().GetResult());
                Reject(() => SupervisedFormalDaqProbe.VerifyAsync(8, () => Snapshot(100), CancellationToken.None).GetAwaiter().GetResult());
                Reject(() => SupervisedFormalDaqProbe.VerifyAsync(7, () => { var snapshot = Snapshot(100); snapshot.IsFresh = false; return snapshot; },
                    CancellationToken.None).GetAwaiter().GetResult());
            });
            Check(ref passed, "V3 DAQ 预检中替换或取消不能复用资格", () =>
            {
                var reads = 0;
                Reject(() => SupervisedFormalDaqProbe.VerifyAsync(7, () =>
                { var snapshot = Snapshot(100 + reads++); if (reads > 1) snapshot.Generation++; return snapshot; }, CancellationToken.None).GetAwaiter().GetResult());
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel(); reads = 0;
                    Reject(() => SupervisedFormalDaqProbe.VerifyAsync(7, () => { reads++; return Snapshot(100); }, cancellation.Token).GetAwaiter().GetResult());
                    Assert(reads == 0, "canceled read was executed");
                }
            });
            return passed;
        }

        private static DaqFreshnessSnapshot Snapshot(long sequence) => new DaqFreshnessSnapshot
        { Device = "Dev1", Generation = 7, IsFresh = true, LastProcessedSequence = sequence, LastArrivalMonotonicTicks = sequence * 100 };

        private static RecoveryCommand Command()
        {
            var command = new RecoveryCommand
            {
                Identity = new RecoveryIdentity { SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(), RunEpoch = 4,
                    IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 3, Revision = 5 },
                OwnerId = RecoveryProtocolV7.NewId(), CommandId = RecoveryProtocolV7.NewId(), CommandSequence = 1,
                Kind = RecoveryCommandKind.RunQualificationCycle, TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks
            };
            command.IdempotencyKey = command.ExpectedIdempotencyKey(); return command;
        }

        private static RecoveryCommand Resume(RecoveryCommand source)
        {
            var command = new RecoveryCommand { Identity = source.Identity.Clone(), OwnerId = source.OwnerId, CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = source.CommandSequence + 1, Kind = RecoveryCommandKind.ResumeFormalRun, TargetResource = source.TargetResource,
                DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks };
            command.Identity.Revision++;
            command.IdempotencyKey = command.ExpectedIdempotencyKey(); return command;
        }

        private static void Check(ref int passed, string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
        private static void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            catch (OperationCanceledException) { return; }
            catch (TimeoutException) { return; }
            throw new Exception("expected authorization rejection");
        }
    }
}
