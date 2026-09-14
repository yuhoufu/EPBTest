using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentProjectStateTests
    {
        private static string Exe => Process.GetCurrentProcess().MainModule.FileName;
        private static string Hash => new string('a', 64);
        private static int _count;
        private static void Assert(bool value, string reason)
        { if (!value) throw new Exception(reason); _count++; }
        private static void Reject(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException) { _count++; return; }
            throw new Exception(reason);
        }
        private static IndependentProcessIdentity Identity()
        {
            using (var p = Process.GetCurrentProcess()) return new IndependentProcessIdentity
            { Pid = p.Id, StartUtcTicks = p.StartTime.ToUniversalTime().Ticks,
                WindowsSessionId = p.SessionId, ExecutablePath = Exe, SessionToken = Guid.NewGuid().ToString("N") };
        }
        private static IndependentRunIntent Intent(string root) => new IndependentRunIntent
        {
            Revision = 1, ProjectDirectory = root, DatabasePath = Path.Combine(root, "index.db"),
            DatabaseCreationUtcTicks = 1, ExecutablePath = Exe, ConfigurationSha256 = Hash,
            RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, Armed = true,
            SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 }, PeriodMs = 15000, StartupBudgetMs = 180000
        };
        private static IndependentProjectStateStore Fixture(string root, long now)
        {
            var store = new IndependentProjectStateStore(root);
            store.Update(0, state => { state.Maintenance = false; return true; });
            var old = Identity(); old.Pid += 100000; // an identity fixture, never sent to OS termination
            store.ArmManualRun(1, Intent(root), old, now);
            return store;
        }
        private static IndependentLaunchTicket Ready(IndependentProjectStateStore store, long now)
        {
            var tx = store.BeginRecovery(store.Read().Revision, "executor", now);
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
            {
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId,
                    phase, true, now, 30000, "SimulatedStageConfirmed");
            }
            return store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash,
                Process.GetCurrentProcess().SessionId, now);
        }

        internal static int ConsumeChild(string root, string nonce, long revision)
        {
            try
            {
                new IndependentProjectStateStore(root).ConsumeLaunchTicket(revision, nonce, Identity(), Hash, DateTime.UtcNow.Ticks);
                return 0;
            }
            catch (InvalidOperationException) { return 23; }
        }

        internal static int RunAll()
        {
            _count = 0;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                Path.GetTempPath(), "independent-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var now = DateTime.UtcNow.Ticks;
            var store = Fixture(Path.Combine(root, "revocation"), now);
            var tx = store.BeginRecovery(store.Read().Revision, "executor", now);
            Assert(store.Read().SafetyCleanupPending, "begin lost safety cleanup obligation");
            Reject(() => store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash, 1, now),
                "issued ticket before safety stages");
            store.UpdateOperatorIntent(store.Read().Revision, "OperatorStop", "manual", now,
                intent => { intent.ManualStopped = true; intent.Armed = false; });
            var stopped = store.Read();
            Assert(stopped.SafetyCleanupPending && stopped.Intent.RecoveryChannels().Length == 0,
                "operator stop discarded safety cleanup or retained restart authority");
            Reject(() => store.ArmManualRun(stopped.Revision, Intent(root), Identity(), now), "started while cleanup pending");
            Reject(() => store.CompleteSafetyStage(stopped.Revision, "other", tx.Generation, tx.RequestId,
                tx.Phase, true, now, 30000, "wrong executor"), "foreign cleanup receipt admitted");
            foreach (var stage in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId,
                    stage, true, now, 30000, "SimulatedCleanupAfterManualStop");
            Assert(!store.Read().SafetyCleanupPending && store.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled,
                "revocation prevented safety cleanup or authorized restart");

            var lateStore = Fixture(Path.Combine(root, "late-safety"), now);
            var lateTx = lateStore.BeginRecovery(lateStore.Read().Revision, "executor", now);
            lateStore.CompleteSafetyStage(lateStore.Read().Revision, "executor", lateTx.Generation, lateTx.RequestId,
                IndependentRecoveryPhase.CooperativeStop, false, now + TimeSpan.FromSeconds(25).Ticks, 1000, "CooperativeTimeout");
            Assert(lateStore.Read().Transaction.Phase == IndependentRecoveryPhase.PowerOff,
                "cooperative timeout did not escalate independently");
            lateStore.CompleteSafetyStage(lateStore.Read().Revision, "executor", lateTx.Generation, lateTx.RequestId,
                IndependentRecoveryPhase.PowerOff, true, now + TimeSpan.FromSeconds(27).Ticks, 30000, "LateReceipt");
            Assert(lateStore.Read().SafetyCleanupPending &&
                lateStore.Read().Transaction.Phase == IndependentRecoveryPhase.NeedsAttention,
                "late safety success cleared cleanup obligation");

            store = Fixture(Path.Combine(root, "ticket"), now);
            var ticket = Ready(store, now);
            var revision = store.Read().Revision;
            var sessionZero = Identity(); sessionZero.WindowsSessionId = 0;
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, sessionZero, Hash, now),
                "service session consumed interactive startup ticket");
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), new string('b', 64), now),
                "wrong executable consumed ticket");
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), Hash,
                now + TimeSpan.FromMinutes(1).Ticks), "expired ticket consumed");
            var consumer = Identity();
            store.ConsumeLaunchTicket(revision, ticket.Nonce, consumer, Hash, now);
            var recovered = store.Read();
            Assert(recovered.Ticket.Consumer.Matches(consumer) && recovered.Transaction.ReplacementPid == consumer.Pid,
                "ticket consumption and PID binding not atomic");
            Reject(() => store.ConsumeLaunchTicket(recovered.Revision, ticket.Nonce, Identity(), Hash, now), "ticket replay admitted");
            var run = Guid.NewGuid().ToString("N");
            store.CommitReplacementRun(recovered.Revision, consumer, run, 2, now);
            recovered = store.Read();
            Assert(recovered.Intent.RunId == run && recovered.Transaction.RunId == run &&
                recovered.Intent.Revision == recovered.Transaction.IntentRevision && recovered.Controller.Matches(consumer),
                "replacement left mixed run identities");
            Reject(() => store.CommitReplacementRun(recovered.Revision, consumer, Guid.NewGuid().ToString("N"), 3, now),
                "bootstrap committed a second run using old ticket");

            store = Fixture(Path.Combine(root, "stop-race"), now);
            ticket = Ready(store, now); revision = store.Read().Revision;
            store.UpdateOperatorIntent(revision, "OperatorSelection", "exclude", now,
                intent => intent.SelectedChannels = new[] { 7, 8, 9 });
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), Hash, now), "stale writer restored selection");
            Reject(() => store.ConsumeLaunchTicket(store.Read().Revision, ticket.Nonce, Identity(), Hash, now), "revoked ticket accepted");
            Assert(store.Read().Intent.RecoveryChannels().SequenceEqual(new[] { 7, 8, 9 }), "excluded channels returned");
            var selectionAudit = store.Read().Audit.Last();
            Assert(selectionAudit.Before.SequenceEqual(new[] { 4, 5, 7, 8, 9, 12 }) &&
                selectionAudit.After.SequenceEqual(new[] { 7, 8, 9 }), "selection audit lost before/after values");

            store = Fixture(Path.Combine(root, "after-consume-stop"), now);
            ticket = Ready(store, now); consumer = Identity();
            store.ConsumeLaunchTicket(store.Read().Revision, ticket.Nonce, consumer, Hash, now);
            store.UpdateOperatorIntent(store.Read().Revision, "OperatorStop", "stop after consume", now,
                intent => intent.ManualStopped = true);
            Reject(() => store.CommitReplacementRun(store.Read().Revision, consumer, Guid.NewGuid().ToString("N"), 2, now),
                "stop between ticket consumption and startup was ignored");

            store = Fixture(Path.Combine(root, "isolation"), now);
            store.UpdateOperatorIntent(store.Read().Revision, "PermanentFault", "isolated", now,
                intent => intent.PermanentChannels = new[] { 4 });
            store.ArmManualRun(store.Read().Revision, Intent(Path.Combine(root, "isolation")), Identity(), now);
            Assert(!store.Read().Intent.RecoveryChannels().Contains(4), "ordinary restart cleared permanent isolation");
            for (var i = 0; i < 80; i++)
                store.UpdateOperatorIntent(store.Read().Revision, "OperatorPause", new string('中', 512), now,
                    intent => intent.ManualPaused = !intent.ManualPaused);
            Assert(store.Read().Audit.Length == 32 && new FileInfo(Path.Combine(root, "isolation", "independent-project-state.json")).Length < 65536,
                "audit overflow blocked future stop transactions");

            var raceRoot = Path.Combine(root, "process-race");
            store = Fixture(raceRoot, DateTime.UtcNow.Ticks); ticket = Ready(store, DateTime.UtcNow.Ticks);
            revision = store.Read().Revision;
            var args = "--state-consume \"" + raceRoot + "\" " + ticket.Nonce + " " + revision;
            using (var first = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            using (var second = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    if (!first.WaitForExit(10000) || !second.WaitForExit(10000)) throw new Exception("consumer deadline exceeded");
                    Assert(new[] { first.ExitCode, second.ExitCode }.OrderBy(c => c).SequenceEqual(new[] { 0, 23 }),
                        "two processes consumed one permission");
                    var onDisk = new IndependentProjectStateStore(raceRoot).Read();
                    Assert(onDisk.Ticket.Consumer.Pid == (first.ExitCode == 0 ? first.Id : second.Id), "winner binding not durable");
                    Reject(() => store.ConsumeLaunchTicket(onDisk.Revision, ticket.Nonce, Identity(), Hash, DateTime.UtcNow.Ticks),
                        "dead consumer automatically resurrected its spent permission");
                }
                finally
                {
                    if (!first.HasExited) { first.Kill(); first.WaitForExit(3000); }
                    if (!second.HasExited) { second.Kill(); second.WaitForExit(3000); }
                }
            }
            var corrupt = Path.Combine(raceRoot, "independent-project-state.json");
            var text = File.ReadAllText(corrupt);
            File.WriteAllText(corrupt, text.Replace("\"SafetyCleanupPending\":false,", ""));
            var rejected = false;
            try { store.Read(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "missing safety flag silently defaulted to false");
            var safetyIntent = Intent(root);
            var safetyTx = IndependentRecoveryTransitions.Begin(null, safetyIntent, "executor", now);
            IndependentRecoveryTransitions.Advance(safetyTx, safetyIntent, "executor", safetyTx.Generation,
                IndependentRecoveryPhase.PowerOff, now, 30000, "fixture");
            var command = new IndependentSafetyWorkerCommand
            {
                StageNonce = Guid.NewGuid().ToString("N"), Transaction = safetyTx,
                IssuedUtcTicks = now, DeadlineUtcTicks = now + TimeSpan.FromSeconds(30).Ticks,
                ConfigDirectory = root,
                Files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }
                    .Select(n => new IndependentSafetyConfigFile { Name = n, Sha256 = Hash }).ToArray(),
                Runtime = new SafetyRuntimeSnapshot { SampleRateHz = 2000, SamplesPerChannel = 20,
                    PressureChannels = new[] { "Pressure_1" }, ReleaseSafePressureBar = new[] { 1d },
                    PressureSampleMaxAgeMs = 100, ReleaseStableMs = 300, ReleaseTimeoutMs = 5000 }
            };
            command.Validate(now);
            var receipt = new IndependentSafetyWorkerReceipt
            {
                StageNonce = command.StageNonce, CommandSha256 = Hash, RequestId = safetyTx.RequestId,
                Generation = safetyTx.Generation, Phase = safetyTx.Phase, Confirmed = true,
                WorkerPid = 123, WorkerStartUtcTicks = now, CompletedUtcTicks = now + 1
            };
            Assert(receipt.MatchesCurrent(command, Hash, 123, now, now + 2), "matching safety worker receipt rejected");
            Assert(!receipt.MatchesCurrent(command, Hash, 124, now, now + 2), "wrong worker accepted");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now - 1, now + 2), "reused PID accepted");
            Assert(!receipt.MatchesCurrent(command, new string('b', 64), 123, now, now + 2), "wrong sealed command accepted");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now, command.DeadlineUtcTicks), "late receipt accepted");
            receipt.StageNonce = Guid.NewGuid().ToString("N");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now, now + 2), "previous stage nonce accepted");
            command.Files[0].Name = "..\\AIConfig.xml";
            var invalidManifest = false;
            try { command.Validate(now); } catch (InvalidDataException) { invalidManifest = true; }
            Assert(invalidManifest, "config traversal accepted");
            Console.WriteLine("PASS independent project state " + _count + "/" + _count);
            return _count;
        }
    }
}
