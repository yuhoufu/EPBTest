using System;
using System.IO;
using System.Linq;
using MTTFTest.Watchdog.Protocol;
using MTTFTest.SafetyAgent;

namespace AdaptiveControlTests
{
    internal static class IndependentRecoveryTransactionTests
    {
        internal static int RunAll()
        {
            var count = 0;
            var now = DateTime.UtcNow.Ticks;
            IndependentRunIntent Intent() => new IndependentRunIntent
            {
                Revision = 1, ProjectDirectory = @"D:\fixture", DatabasePath = @"D:\fixture\index.db",
                DatabaseCreationUtcTicks = 123, ExecutablePath = @"C:\fixture\MTTFTest.exe",
                ConfigurationSha256 = new string('a', 64), RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1,
                Armed = true, SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 },
                PeriodMs = 15000, StartupBudgetMs = 180000
            };
            void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
            void Reject(Action action, string message)
            {
                var rejected = false;
                try { action(); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, message);
            }
            var intent = Intent();
            intent.SelectedChannels = new[] { 7, 8, 9 };
            Assert(intent.RecoveryChannels().SequenceEqual(new[] { 7, 8, 9 }), "unchecked channels restored");
            intent.PausedChannels = new[] { 7 }; intent.PermanentChannels = new[] { 8 };
            Assert(intent.RecoveryChannels().SequenceEqual(new[] { 9 }), "paused/permanent included");
            intent.ManualStopped = true;
            Reject(() => IndependentRecoveryTransitions.Begin(null, intent, "executor", now), "manual stop ignored");
            intent = Intent(); intent.ManualPaused = true;
            Reject(() => IndependentRecoveryTransitions.Begin(null, intent, "executor", now), "batch pause ignored");
            intent = Intent();
            var tx = IndependentRecoveryTransitions.Begin(null, intent, "executor", now);
            Assert(tx.PhaseDeadlineUtcTicks - now == TimeSpan.FromSeconds(25).Ticks, "cooperative deadline wrong");
            Reject(() => IndependentRecoveryTransitions.Begin(tx, intent, "other", now), "dual owner admitted");
            Reject(() => IndependentRecoveryTransitions.Advance(tx, intent, "executor", tx.Generation,
                IndependentRecoveryPhase.LaunchPending, now, 5000, ""), "safety stage bypassed");
            Reject(() => IndependentRecoveryTransitions.RequireCurrent(tx, intent, "other", tx.Generation), "wrong executor accepted");
            Reject(() => IndependentRecoveryTransitions.RequireCurrent(tx, intent, "executor", tx.Generation + 1), "wrong generation accepted");
            intent.Revision++;
            Reject(() => IndependentRecoveryTransitions.RequireCurrent(tx, intent, "executor", tx.Generation), "old selection authority accepted");
            intent.Revision--;
            intent.SelectedChannels = new[] { 7, 8, 9 };
            Reject(() => IndependentRecoveryTransitions.RequireCurrent(tx, intent, "executor", tx.Generation), "changed targets accepted");
            intent.SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 };
            IndependentRecoveryTransitions.Advance(tx, intent, "executor", tx.Generation,
                IndependentRecoveryPhase.PowerOff, now + TimeSpan.FromSeconds(25).Ticks, 10000, "timeout escalated");
            Assert(tx.Phase == IndependentRecoveryPhase.PowerOff, "timeout did not retain executor ownership");
            tx.Phase = IndependentRecoveryPhase.Cancelled;
            Reject(() => IndependentRecoveryTransitions.Begin(tx, intent, "executor", now + TimeSpan.FromSeconds(59).Ticks), "retry too fast");
            tx = IndependentRecoveryTransitions.Begin(tx, intent, "executor", now + TimeSpan.FromMinutes(1).Ticks);
            tx.Phase = IndependentRecoveryPhase.Cancelled;
            tx = IndependentRecoveryTransitions.Begin(tx, intent, "executor", now + TimeSpan.FromMinutes(2).Ticks);
            tx.Phase = IndependentRecoveryPhase.Cancelled;
            Reject(() => IndependentRecoveryTransitions.Begin(tx, intent, "executor", now + TimeSpan.FromMinutes(3).Ticks), "fourth restart admitted");
            var next = IndependentRecoveryTransitions.Begin(tx, intent, "executor", now + TimeSpan.FromMinutes(31).Ticks);
            Assert(next.Generation > tx.Generation && next.AttemptsUtcTicks.Length == 2, "rolling retry window incorrect");
            Reject(() => IndependentRecoveryTransitions.Begin(tx, intent, "executor", now), "clock rollback admitted");
            var operations = new FakeOperations();
            var saves = 0;
            var engine = new IndependentRecoveryExecutor(operations, _ => saves++, 10000, 15000);
            tx = IndependentRecoveryTransitions.Begin(null, intent, "executor", now);
            engine.Tick(tx, intent, "executor", tx.Generation, now);
            Assert(tx.Phase == IndependentRecoveryPhase.CooperativeStop && operations.Launches == 0, "pending stop launched");
            engine.Tick(tx, intent, "executor", tx.Generation, now + TimeSpan.FromSeconds(25).Ticks);
            Assert(tx.Phase == IndependentRecoveryPhase.PowerOff, "hung main prevented independent escalation");
            operations.Power = IndependentOperationResult.Failed;
            engine.Tick(tx, intent, "executor", tx.Generation, now + TimeSpan.FromSeconds(26).Ticks);
            Assert(tx.Phase == IndependentRecoveryPhase.NeedsAttention && operations.Kills == 0 && operations.Launches == 0,
                "unconfirmed power allowed kill/launch");
            operations = new FakeOperations { Stop = IndependentOperationResult.Completed, Power = IndependentOperationResult.Completed };
            engine = new IndependentRecoveryExecutor(operations, _ => saves++, 10000, 15000);
            tx = IndependentRecoveryTransitions.Begin(null, intent, "executor", now);
            for (var step = 0; step < 5; step++) engine.Tick(tx, intent, "executor", tx.Generation, now + step * TimeSpan.TicksPerSecond);
            Assert(tx.Phase == IndependentRecoveryPhase.Verifying && operations.Launches == 1 && operations.Kills == 1,
                "execution order or launch count incorrect");
            engine = new IndependentRecoveryExecutor(operations, _ => saves++, 10000, 15000);
            engine.Tick(tx, intent, "executor", tx.Generation, now + TimeSpan.FromSeconds(6).Ticks);
            Assert(tx.Phase == IndependentRecoveryPhase.Verifying && operations.Launches == 1, "service restart repeated launch");
            operations.Verified = IndependentOperationResult.Completed;
            engine.Tick(tx, intent, "executor", tx.Generation, now + TimeSpan.FromSeconds(7).Ticks);
            Assert(tx.Phase == IndependentRecoveryPhase.Verified && saves > 0, "verification not durable");
            tx = IndependentRecoveryTransitions.Begin(null, intent, "executor", now);
            intent.ManualStopped = true;
            engine.Tick(tx, intent, "executor", tx.Generation, now);
            Assert(tx.Phase == IndependentRecoveryPhase.Cancelled && operations.Cancelled > 0, "operator revocation ignored");
            var root = Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT");
            if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required");
            var directory = Path.Combine(root, "independent-journal-" + Guid.NewGuid().ToString("N"));
            var journal = new IndependentRecoveryJournal(directory);
            intent = Intent();
            journal.UpdateIntent(0, _ => intent);
            tx = journal.Begin("executor", now);
            var reloaded = new IndependentRecoveryJournal(directory);
            Assert(reloaded.ReadTransaction().RequestId == tx.RequestId, "request lost across journal recreation");
            Reject(() => reloaded.Begin("replacement", now), "persisted owner was bypassed");
            reloaded.UpdateIntent(1, current => { current.SelectedChannels = new[] { 7, 8, 9 }; return current; });
            Reject(() => journal.UpdateIntent(1, current => { current.SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 }; return current; }),
                "stale writer restored manually excluded channels");
            Assert(reloaded.ReadIntent().RecoveryChannels().SequenceEqual(new[] { 7, 8, 9 }), "intent update not durable");
            operations = new FakeOperations();
            engine = new IndependentRecoveryExecutor(operations, reloaded.WriteTransaction, 10000, 15000);
            reloaded.Execute(() => { engine.Tick(reloaded.ReadTransaction(), reloaded.ReadIntent(), "executor", tx.Generation, now); return true; });
            Assert(reloaded.ReadTransaction().Phase == IndependentRecoveryPhase.Cancelled && operations.Launches == 0,
                "durable revision change did not cancel old transaction");
            intent = Intent();
            tx = IndependentRecoveryTransitions.Begin(null, intent, "executor", now);
            var safety = new FakeSafety();
            var runtime = new SafetyRuntimeSnapshot
            {
                SampleRateHz = 2000, SamplesPerChannel = 20, PressureChannels = new[] { "Pressure_1" },
                ReleaseSafePressureBar = new[] { 1d }, PressureSampleMaxAgeMs = 100,
                ReleaseStableMs = 300, ReleaseTimeoutMs = 5000
            };
            Reject(() => IndependentSafetyStages.Execute(tx, directory, runtime, safety), "unsafe worker phase admitted");
            IndependentRecoveryTransitions.Advance(tx, intent, "executor", tx.Generation, IndependentRecoveryPhase.PowerOff,
                now, 10000, "");
            var evidence = IndependentSafetyStages.Execute(tx, directory, runtime, safety);
            Assert(evidence.Confirmed && safety.Calls == "P", "pre-retirement phase touched reserved NI outputs");
            IndependentRecoveryTransitions.Advance(tx, intent, "executor", tx.Generation, IndependentRecoveryPhase.RetireControls,
                now, 10000, "");
            IndependentRecoveryTransitions.Advance(tx, intent, "executor", tx.Generation, IndependentRecoveryPhase.OutputsSafe,
                now, 10000, "");
            safety.Calls = string.Empty; safety.PressureSafe = false;
            evidence = IndependentSafetyStages.Execute(tx, directory, runtime, safety);
            Assert(!evidence.Confirmed && evidence.Detail == "IndependentPressureUnconfirmed" && safety.Calls == "PDAH",
                "partial safety reported as success");
            safety.PressureSafe = true; safety.Calls = string.Empty;
            evidence = IndependentSafetyStages.Execute(tx, directory, runtime, safety);
            Assert(evidence.Confirmed && evidence.RequestId == tx.RequestId && evidence.Generation == tx.Generation &&
                safety.Calls == "PDAH", "independent safety evidence not bound to transaction");
            Console.WriteLine("Independent recovery transaction: " + count + " passed");
            return count;
        }

        private sealed class FakeOperations : IIndependentRecoveryOperations
        {
            internal IndependentOperationResult Stop = IndependentOperationResult.Pending;
            internal IndependentOperationResult Power = IndependentOperationResult.Pending;
            internal IndependentOperationResult Verified = IndependentOperationResult.Pending;
            internal int Kills, Launches, Cancelled;
            public IndependentOperationResult CooperativeStop(IndependentRecoveryTransaction tx) => Stop;
            public IndependentOperationResult ConfirmPowerOff(IndependentRecoveryTransaction tx) => Power;
            public IndependentOperationResult RetireExactControls(IndependentRecoveryTransaction tx) { Kills++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult ConfirmOutputsAndPressure(IndependentRecoveryTransaction tx) => IndependentOperationResult.Completed;
            public IndependentOperationResult LaunchOnce(IndependentRecoveryTransaction tx)
            {
                Launches++; tx.ReplacementPid = 123; tx.ReplacementStartUtcTicks = DateTime.UtcNow.Ticks;
                return IndependentOperationResult.Completed;
            }
            public IndependentOperationResult VerifyActionsAndDatabase(IndependentRecoveryTransaction tx) => Verified;
            public void CancelPendingLaunch(IndependentRecoveryTransaction tx) { Cancelled++; }
        }

        private sealed class FakeSafety : ISafetyHardwareFactory, ISafetyHardware
        {
            internal string Calls = string.Empty;
            internal bool PressureSafe = true;
            public ISafetyHardware Create(string directory, SafetyRuntimeSnapshot runtime) => this;
            public bool ConfirmPowerOff() { Calls += "P"; return true; }
            public bool ConfirmDoOff() { Calls += "D"; return true; }
            public bool ConfirmAoZero() { Calls += "A"; return true; }
            public bool ConfirmPressureSafe() { Calls += "H"; return PressureSafe; }
            public void Dispose() { }
        }
    }
}
