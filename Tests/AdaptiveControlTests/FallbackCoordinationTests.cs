using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class FallbackCoordinationTests
    {
        internal static int RunAll()
        {
            var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fallback-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            int passed = 0;
            try
            {
                Action<string, Action<FallbackLedgerStore, FallbackLedger>> run = (name, test) =>
                {
                    var store = new FallbackLedgerStore(directory, Guid.NewGuid().ToString("N"));
                    var state = store.Initialize(Guid.NewGuid().ToString("N"), 1, "original");
                    test(store, state); passed++; Console.WriteLine("PASS " + name);
                };
                run("Fallback cannot acquire without Yield", (s, v) => Reject(() => s.Acquire(v.Revision, "external", "command")));
                run("Fallback two requesters compete through CAS", (s, v) =>
                {
                    var accepted = 0;
                    Parallel.For(0, 16, i => { try { s.Request(v.Revision, Guid.NewGuid().ToString("N"), "external" + i); System.Threading.Interlocked.Increment(ref accepted); } catch (InvalidOperationException) { } });
                    Assert(accepted == 1, "multiple requesters won");
                });
                run("Fallback cannot yield a prepared original launch", (s, v) =>
                { s.PrepareOriginalLaunch("original", "intent"); Reject(() => s.Request(s.Read().Revision, Guid.NewGuid().ToString("N"), "external")); });
                run("Fallback old delayed launch rejected after acquisition", (s, v) =>
                { Acquire(s, v); Reject(() => s.PrepareOriginalLaunch("original", "late")); });
                run("Fallback stale revision rejected", (s, v) =>
                { s.Request(v.Revision, Guid.NewGuid().ToString("N"), "external"); Reject(() => s.Yield(v.Revision, "original")); });
                run("Fallback manual stop dominates acquire", (s, v) =>
                { v = s.Request(v.Revision, Guid.NewGuid().ToString("N"), "external"); v = s.Yield(v.Revision, "original"); s.Stop(); Reject(() => s.Acquire(s.Read().Revision, "external", v.CommandId)); });
                run("Fallback manual stop dominates energization authorization", (s, v) =>
                { v = Acquire(s, v); s.Stop(); Reject(() => s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId)); });
                run("Fallback repeated start cannot cross prepare twice", (s, v) =>
                { v = Acquire(s, v); s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId); Reject(() => s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId)); });
                run("Fallback lost launch response survives store restart", (s, v) =>
                { v = Acquire(s, v); s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId); var reloaded = new FallbackLedgerStore(directory, v.SessionId); Assert(reloaded.Read().Phase == "LaunchPrepared", "lost command disappeared"); Reject(() => reloaded.Return(reloaded.Read().Revision, "external", "original", true)); });
                run("Fallback crashed owner cannot be replaced by lease expiry", (s, v) =>
                { v = Acquire(s, v); Reject(() => s.Acquire(v.Revision, "replacement", v.CommandId)); });
                run("Fallback acquisition can return before action", (s, v) =>
                { v = Acquire(s, v); var returned = s.Return(v.Revision, "external", "original", false); Assert(returned.FenceGeneration > v.FenceGeneration && returned.Owner == "Original", "generation did not advance"); Reject(() => s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId)); });
                run("Fallback PID binding required before verification", (s, v) =>
                { v = Acquire(s, v); Reject(() => s.BindLaunch(v.FenceGeneration, "external", v.CommandId, 5, 10)); });
                run("Fallback saving or unverified operation cannot return", (s, v) =>
                { v = Acquire(s, v); s.PrepareLaunch(v.FenceGeneration, "external", v.CommandId); v = s.BindLaunch(v.FenceGeneration, "external", v.CommandId, 5, 10); Reject(() => s.Return(v.Revision, "external", "original", false)); });
                run("Fallback old Run cannot reinitialize authority", (s, v) => Reject(() => s.Initialize(Guid.NewGuid().ToString("N"), 2, "other")));
                run("Fallback admitted new run clears old stop and fences delayed command", (s, v) =>
                {
                    var old = v;
                    v = s.Stop();
                    var next = s.BindActiveRun(v.Revision, "original", Guid.NewGuid().ToString("N"), 2);
                    Assert(!next.ManualStopped && next.FenceGeneration > old.FenceGeneration && next.RunId != old.RunId,
                        "new run inherited old stop");
                    Reject(() => s.Request(old.Revision, Guid.NewGuid().ToString("N"), "late"));
                    Reject(() => s.BindActiveRun(next.Revision, "original", next.RunId, 1));
                });
                run("Fallback new run cannot erase an outstanding launch", (s, v) =>
                {
                    s.PrepareOriginalLaunch("original", "intent");
                    Reject(() => s.BindActiveRun(s.Read().Revision, "original", Guid.NewGuid().ToString("N"), 2));
                });
                run("Fallback corrupt ledger is not overwritten", (s, v) =>
                { var path = Path.Combine(directory, "fallback-" + v.SessionId + ".json"); File.WriteAllText(path, "{"); Reject(() => s.Initialize(v.RunId, v.RunEpoch, "original")); Assert(File.ReadAllText(path) == "{", "corrupt evidence overwritten"); });
                run("Fallback unknown schema fails closed", (s, v) =>
                { v.SchemaVersion = 99; BoundedJson.Write(Path.Combine(directory, "fallback-" + v.SessionId + ".json"), v); Reject(() => s.Read()); });
                run("Fallback snapshot size is bounded", (s, v) =>
                { v.Detail = new string('x', 65537); Reject(() => BoundedJson.Write(Path.Combine(directory, "oversize.json"), v)); });
                run("Fallback one moving channel cannot verify another stopped channel", VerifyEachChannel);
                return passed;
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
        private static void VerifyEachChannel(FallbackLedgerStore store, FallbackLedger value)
        {
            value = Acquire(store, value); store.PrepareLaunch(value.FenceGeneration, "external", value.CommandId);
            value = store.BindLaunch(value.FenceGeneration, "external", value.CommandId, 5, 10);
            var verifier = new FallbackProgressVerifier();
            var heartbeat = new WatchdogHeartbeat { ProcessId = 5, ProcessStartUtcTicks = 10, RunId = value.RunId,
                RunEpoch = 2, RecoveryEligibleChannels = new[] { 1, 2 }, ExpectedCyclePeriodMs = 1000,
                ChannelProgress = new[] { 1, 2 }.Select(ch => new WatchdogChannelProgress { Channel = ch, State = "Running",
                    FormalCommitRunEpoch = 2, FormalCommitIdentity = "commit", FormalCommitSequence = 1, DoCommandSequence = 1, MechanicalCompletedCount = 1 }).ToArray() };
            for (int i = 1; i <= 10; i++)
            { heartbeat.Sequence = i; heartbeat.ChannelProgress[0].FormalCommitSequence++; heartbeat.ChannelProgress[0].DoCommandSequence++; heartbeat.ChannelProgress[0].MechanicalCompletedCount++;
                Assert(!verifier.Observe(heartbeat, value, i * System.Diagnostics.Stopwatch.Frequency), "stalled second channel accepted"); }
            heartbeat.ChannelProgress[1].FormalCommitSequence = 5; heartbeat.ChannelProgress[1].DoCommandSequence = 5; heartbeat.ChannelProgress[1].MechanicalCompletedCount = 5;
            heartbeat.Sequence++; Assert(!verifier.Observe(heartbeat, value, 11 * System.Diagnostics.Stopwatch.Frequency), "stable observation skipped");
            heartbeat.Sequence++; Assert(verifier.Observe(heartbeat, value, 13 * System.Diagnostics.Stopwatch.Frequency), "verified channels not accepted");
        }
        private static FallbackLedger Acquire(FallbackLedgerStore store, FallbackLedger value)
        { value = store.Request(value.Revision, Guid.NewGuid().ToString("N"), "external"); value = store.Yield(value.Revision, "original"); return store.Acquire(value.Revision, "external", value.CommandId); }
        private static void Reject(Action action)
        { try { action(); } catch (Exception) { return; } throw new Exception("operation should have been rejected"); }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
