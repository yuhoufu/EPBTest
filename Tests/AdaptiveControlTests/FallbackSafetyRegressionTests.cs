using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class FallbackSafetyRegressionTests
    {
        internal static int RunAll()
        {
            int passed = 0, failed = 0;
            Action<string, Action> run = (name, action) =>
            {
                try { action(); passed++; Console.WriteLine("PASS " + name); }
                catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
            };
            run("F1 actual host failed admission releases busy", FailedHostAdmissionReleasesBusy);
            run("F3 pre-admission fault does not publish ownerless recovery count", HydraulicAdmissionFailureHasNoPublishedCount);
            run("F4 safety-only has no recovery permit", SafetyOnlyDoesNotNeedPermit);
            run("R1 EPB4未重入不得清理已重入EPB5，旧finally不改新代", GroupReleasePreservesRejoinedPeer);
            run("F2 connected peer does not read", () => StalledPipe(0));
            run("F2 connected peer does not respond", () => StalledPipe(1));
            run("F2 peer sends incomplete frame", () => StalledPipe(2));
            if (failed > 0) throw new InvalidOperationException($"FallbackSafety: {passed} passed, {failed} failed");
            return passed;
        }

        private static void FailedHostAdmissionReleasesBusy()
        {
            // No host constructor: it creates UI/journal infrastructure. Only
            // admission is invoked, with failure persistence/notification also
            // unavailable. No worker or production pipe can be started here.
            var host = (WatchdogHost)FormatterServices.GetUninitializedObject(typeof(WatchdogHost));
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var session = Guid.NewGuid().ToString("N");
            typeof(WatchdogHost).GetField("_args", flags).SetValue(host,
                new WatchdogArguments { SessionId = session, JournalDirectory = "" });
            var busy = typeof(WatchdogHost).GetField("_safetyHandoffStarted", flags);
            for (int i = 0; i < 2; i++)
            {
                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = 1, SessionId = session, SessionGeneration = 1,
                    SessionLease = 1, Revision = 1, HandoffId = Guid.NewGuid().ToString("N"),
                    Nonce = Guid.NewGuid().ToString("N"), State = WatchdogSafetyHandoffState.Requested
                };
                Assert(receipt.IsValidFor(session), "fixture must pass receipt admission");
                try { typeof(WatchdogHost).GetMethod("BeginSafetyHandoff", flags).Invoke(host, new object[] { receipt }); }
                catch (TargetInvocationException) { /* injected missing notification/persistence */ }
                Assert((int)busy.GetValue(host) == 0, "failed admission retained busy ownership");
            }
        }

        private static void GroupReleasePreservesRejoinedPeer()
        {
            var type = typeof(Controller.EpbManager);
            var manager = (Controller.EpbManager)FormatterServices.GetUninitializedObject(type);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var store = new Controller.ChannelRuntimeStateStore();
            type.GetField("_channelRuntimeStateStore", flags).SetValue(manager, store);
            var run = Guid.NewGuid(); var incident = Guid.NewGuid(); var owner = Guid.NewGuid();
            var contract = new Controller.RecoveryContractSnapshot(incident, run, 1, owner,
                Controller.RecoveryOwnerKind.HydraulicGroupRecovery, Controller.RecoveryTargetPhase.Formal,
                "test", DateTime.UtcNow, DateTime.UtcNow.AddSeconds(30), new[] { 4, 5 });
            store.Publish(new Controller.ChannelRuntimeStateChangedEvent
            { Channel = 4, State = Controller.ChannelRuntimeState.Recovering, RunId = run, RunEpoch = 1,
                CorrelationId = incident, RecoveryOwnerId = owner, Enabled = true, TimestampUtc = DateTime.UtcNow });
            store.Publish(new Controller.ChannelRuntimeStateChangedEvent
            { Channel = 5, State = Controller.ChannelRuntimeState.Running, RunId = run, RunEpoch = 1,
                CorrelationId = incident, Enabled = true, TimestampUtc = DateTime.UtcNow });
            var method = type.GetMethod("CommitRecoveryIncidentStateForRelease", flags);
            method.Invoke(manager, new object[] { contract, "test", "test" });
            Assert(store.Get(5).State == Controller.ChannelRuntimeState.Running, "peer EPB5 lost its rejoined state");
            Assert(store.Get(4).State == Controller.ChannelRuntimeState.StartBlocked, "unrejoined EPB4 has no terminal");
            var nextRun = Guid.NewGuid();
            store.Publish(new Controller.ChannelRuntimeStateChangedEvent
            { Channel = 4, State = Controller.ChannelRuntimeState.Recovering, RunId = nextRun, RunEpoch = 2,
                CorrelationId = Guid.NewGuid(), RecoveryOwnerId = Guid.NewGuid(), Enabled = true, TimestampUtc = DateTime.UtcNow },
                allowTerminalReset: true, allowSystemFaultReset: true);
            method.Invoke(manager, new object[] { contract, "test", "test" });
            Assert(store.Get(4).RunId == nextRun && store.Get(4).State == Controller.ChannelRuntimeState.Recovering,
                "late old finally overwrote replacement run");
        }

        private static void HydraulicAdmissionFailureHasNoPublishedCount()
        {
            var type = typeof(Controller.EpbManager);
            var manager = (Controller.EpbManager)FormatterServices.GetUninitializedObject(type);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var groups = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            type.GetField("_hydraulicSoftwareRecoveryGroups", flags).SetValue(manager, groups);
            var fault = new Controller.ControlFault("test", "injected pre-admission failure", default,
                new[] { 1 }, 1, DateTime.UtcNow, Guid.NewGuid());
            try { type.GetMethod("BeginHydraulicSoftwareRecovery", flags).Invoke(manager, new object[] { fault }); }
            catch (TargetInvocationException) { }
            Assert(groups.Count == 0, "pre-admission exception left software count without structured owner");
        }

        private static void SafetyOnlyDoesNotNeedPermit()
        {
            var request = new SupervisorSafetyAgentLaunchRequest
            {
                RequestId = Guid.NewGuid().ToString("N"), ChallengeNonce = Guid.NewGuid().ToString("N"),
                RequesterProcessId = 1, RequesterProcessStartUtcTicks = 1,
                SessionId = Guid.NewGuid().ToString("N"), HandoffId = Guid.NewGuid().ToString("N"),
                HandoffNonceSha256 = new string('a', 64), ExecutablePath = "safety.exe",
                ExecutableSha256 = new string('b', 64), Arguments = "", WorkingDirectory = "C:\\",
                ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256("")
            };
            typeof(SupervisorSafetyAgentLaunchRequest).GetProperty("SafetyOnly")?.SetValue(request, true);
            Assert(request.IsStructurallyValid(), "safety-only incorrectly requires permit");
            request.PermitGeneration = 1; request.PermitId = Guid.NewGuid().ToString("N");
            Assert(!request.IsStructurallyValid(), "safety-only must not carry a resume grant");
        }

        private static void StalledPipe(int mode)
        {
            var name = "EPB-Fallback-Test-" + Guid.NewGuid().ToString("N");
            using (var release = new ManualResetEventSlim())
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256))
            {
                var peer = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    if (mode != 0)
                    {
                        var input = new byte[1]; await server.ReadAsync(input, 0, 1);
                        if (mode == 2) { await server.WriteAsync(new byte[] { 1, 2 }, 0, 2); }
                    }
                    release.Wait(4000);
                });
                var watch = Stopwatch.StartNew();
                try
                {
                    try
                    {
                        DeadlinePipeExchange.Execute(name, 250,
                            writer => { if (mode == 0) writer.Write(new byte[1024 * 1024]); else writer.Write((byte)1); },
                            reader => reader.ReadInt64());
                        throw new Exception("stalled pipe unexpectedly succeeded");
                    }
                    catch (TimeoutException) { }
                    Assert(watch.ElapsedMilliseconds < 3000, "total deadline did not bound caller");
                }
                finally { release.Set(); server.Dispose(); Assert(peer.Wait(3000), "peer leaked"); }
            }
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
