using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MTTFTest.FallbackGuard;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    // Test-only actor: no hardware libraries, no production bootstrap shortcut.
    // Safety phases below are simulated; database commits and child processes are real.
    internal static class TicketDatabaseSimulationTests
    {
        private const string Marker = "TICKET-DATABASE-SIMULATION-ONLY";
        private static readonly int[] All = { 4, 5, 7, 8, 9, 12 };
        // Runs the production durable pump, replacing only the external hardware
        // operations and launcher with this explicitly simulated test actor.
        private sealed class PumpOperations : IIndependentProjectRuntimeOperations
        {
            internal IndependentProjectStateStore Store;
            internal string DatabasePath;
            internal RecoveryDatabaseSnapshot Baseline;
            internal Func<bool> ReplacementAlive;
            internal int[] Stages = new int[4];
            public IndependentOperationResult CooperativeStop(IndependentRecoveryTransaction tx)
            { Stages[0]++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult ConfirmPowerOff(IndependentRecoveryTransaction tx)
            { Stages[1]++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult RetireExactControls(IndependentRecoveryTransaction tx)
            { Stages[2]++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult ConfirmOutputsAndPressure(IndependentRecoveryTransaction tx)
            { Stages[3]++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult LaunchOnce(IndependentRecoveryTransaction tx)
            {
                var state = Store.Read();
                return state.Ticket?.Consumer != null && state.Ticket.Consumer.Matches(state.Controller)
                    ? IndependentOperationResult.Completed : IndependentOperationResult.Pending;
            }
            public IndependentOperationResult VerifyActionsAndDatabase(IndependentRecoveryTransaction tx)
            {
                if (ReplacementAlive?.Invoke() != true) return IndependentOperationResult.Failed;
                var pending = RecoveryDatabaseEvidence.UnverifiedChannels(Baseline,
                    RecoveryDatabaseEvidence.Read(DatabasePath, All));
                return pending.Intersect(tx.Channels).Any()
                    ? IndependentOperationResult.Pending : IndependentOperationResult.Completed;
            }
            public void CancelPendingLaunch(IndependentRecoveryTransaction tx) { }
            public void CancelStageWorkers() { } // no hardware worker exists in this fixture
        }
        private static string Exe => Process.GetCurrentProcess().MainModule.FileName;
        private static IndependentProcessIdentity Identity()
        {
            using (var p = Process.GetCurrentProcess()) return new IndependentProcessIdentity
            { Pid = p.Id, StartUtcTicks = p.StartTime.ToUniversalTime().Ticks, WindowsSessionId = p.SessionId,
                ExecutablePath = Exe, SessionToken = Guid.NewGuid().ToString("N") };
        }
        private static SQLiteConnection Open(string root)
        {
            var connection = new SQLiteConnection(new SQLiteConnectionStringBuilder
            { DataSource = Path.Combine(root, "index.db"), Pooling = false, FailIfMissing = true, DefaultTimeout = 1 }.ConnectionString);
            connection.Open(); return connection;
        }
        internal static void CommitCycle(string root, int[] channels, int cycle)
        {
            using (var db = Open(root))
            using (var tx = db.BeginTransaction())
            {
                foreach (var channel in channels)
                using (var command = db.CreateCommand())
                {
                    command.Transaction = tx;
                    command.CommandText = "INSERT INTO epb_cycles(epb_id,cycle_number,status,end_time,mechanical_completed,mechanical_completed_at,sample_count) " +
                        "VALUES(@channel,@cycle,'completed',@time,1,@time,100)";
                    command.Parameters.AddWithValue("@channel", channel);
                    command.Parameters.AddWithValue("@cycle", cycle);
                    command.Parameters.AddWithValue("@time", DateTimeOffset.UtcNow.ToString("O"));
                    command.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        private static void Wait(string file, Func<bool> alive)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(file))
            {
                if (watch.ElapsedMilliseconds > 10000 || !alive()) throw new TimeoutException("Simulation rendezvous failed: " + file);
                Thread.Sleep(20);
            }
        }
        internal static int Resume(string root, string nonce)
        {
            root = Path.GetFullPath(root);
            if (!Path.GetFileName(root).StartsWith("ticket-db-simulation-", StringComparison.Ordinal) ||
                File.ReadAllText(Path.Combine(root, Marker)) != Marker) throw new InvalidOperationException("Simulation fixture required");
            var store = new IndependentProjectStateStore(root);
            var before = store.Read();
            if (before.Intent.ProjectDirectory != root || before.Intent.DatabasePath != Path.Combine(root, "index.db"))
                throw new InvalidOperationException("Simulation project identity mismatch");
            var identity = Identity();
            try { store.ConsumeLaunchTicket(before.Revision, nonce, identity, SupervisorProtocol.ComputeSha256(Exe), DateTime.UtcNow.Ticks); }
            catch (InvalidOperationException) { return 23; }
            store.CommitReplacementRun(store.Read().Revision, identity, Guid.NewGuid().ToString("N"), before.Intent.RunEpoch + 1, DateTime.UtcNow.Ticks);
            for (var cycle = 101; cycle <= 103; cycle++)
            {
                if (cycle == 103) Wait(Path.Combine(root, "continue"), () => true);
                var state = store.Read();
                if (state.Ticket.Revoked || !identity.Matches(state.Controller) || state.Intent.RecoveryChannels().Length == 0)
                    throw new InvalidOperationException("Simulation authorization revoked");
                CommitCycle(root, state.Intent.RecoveryChannels(), cycle);
                if (cycle == 102) File.WriteAllText(Path.Combine(root, "two-records"), "committed");
            }
            File.WriteAllText(Path.Combine(root, "three-records"), "committed");
            Wait(Path.Combine(root, "finish"), () => true);
            return 0;
        }
        internal static int Run()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required"), "ticket-db-simulation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, Marker), Marker);
            BoundedJson.Write(Path.Combine(root, "parent-owner.json"), Identity());
            var dbPath = Path.Combine(root, "index.db"); SQLiteConnection.CreateFile(dbPath);
            using (var db = Open(root)) using (var sql = db.CreateCommand())
            {
                sql.CommandText = "CREATE TABLE epb_cycles(id INTEGER PRIMARY KEY AUTOINCREMENT,epb_id INTEGER,cycle_number INTEGER," +
                    "status TEXT,end_time TEXT,mechanical_completed INTEGER,mechanical_completed_at TEXT,sample_count INTEGER,UNIQUE(epb_id,cycle_number))";
                sql.ExecuteNonQuery();
            }
            CommitCycle(root, All, 100);
            var baseline = RecoveryDatabaseEvidence.Read(dbPath, All);
            var store = new IndependentProjectStateStore(root);
            store.Update(0, state => { state.Maintenance = false; return true; });
            var old = Identity(); old.Pid += 100000; // simulated old controller; never sent to OS termination
            var intent = new IndependentRunIntent
            { Revision = 1, ProjectDirectory = root, DatabasePath = dbPath, DatabaseCreationUtcTicks = baseline.CreationUtcTicks,
                ExecutablePath = Exe, ConfigurationSha256 = new string('c', 64), RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1,
                SelectedChannels = All, PausedChannels = new[] { 4 }, PermanentChannels = new[] { 5 }, CompletedChannels = new[] { 12 },
                Armed = true, PeriodMs = 1000, StartupBudgetMs = 30000 };
            store.ArmManualRun(1, intent, old, DateTime.UtcNow.Ticks);
            var tx = store.BeginRecovery(store.Read().Revision, "simulation", DateTime.UtcNow.Ticks);
            var operations = new PumpOperations { Store = store, DatabasePath = dbPath, Baseline = baseline };
            var pump = new IndependentProjectRecoveryPump(store, operations, "simulation", 30000, 30000);
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
            {
                if (store.Read().Transaction.Phase != phase) throw new InvalidOperationException("Pump safety stage order changed");
                pump.Tick(DateTime.UtcNow.Ticks);
            }
            if (store.Read().Transaction.Phase != IndependentRecoveryPhase.LaunchPending || operations.Stages.Any(n => n != 1))
                throw new InvalidOperationException("Pump did not execute each simulated safety stage exactly once");
            var ticket = store.IssueLaunchTicket(store.Read().Revision, "simulation", tx.Generation,
                SupervisorProtocol.ComputeSha256(Exe), Process.GetCurrentProcess().SessionId, DateTime.UtcNow.Ticks);
            store.MarkLaunchDispatched(store.Read().Revision, ticket.Nonce, "simulation", DateTime.UtcNow.Ticks);
            var arguments = "--simulation-resume \"" + root + "\" " + ticket.Nonce;
            var count = 0;
            Action<bool, string> check = (value, message) => { if (!value) throw new InvalidOperationException(message); count++; };
            using (var worker = new IndependentBoundedWorker(Exe, arguments, root, 25000, 256, (pid, started) =>
                BoundedJson.Write(Path.Combine(root, "child-owner.json"), new
                { pid, startUtcTicks = started, executable = Exe, purpose = "SimulationResume" })))
            {
                operations.ReplacementAlive = () => worker.Poll() == IndependentWorkerState.Running;
                Wait(Path.Combine(root, "two-records"), () => worker.Poll() == IndependentWorkerState.Running);
                var state = store.Read();
                check(state.Controller.Pid == worker.ProcessId && state.Controller.StartUtcTicks == worker.StartUtcTicks && state.Intent.RunEpoch == 2,
                    "Ticket did not bind the actual new process and run");
                var two = RecoveryDatabaseEvidence.Read(dbPath, All);
                check(RecoveryDatabaseEvidence.UnverifiedChannels(baseline, two).Intersect(new[] { 7, 8, 9 }).Count() == 3,
                    "Two records incorrectly proved recovery");
                pump.Tick(DateTime.UtcNow.Ticks);
                pump.Tick(DateTime.UtcNow.Ticks);
                check(store.Read().Transaction.Phase == IndependentRecoveryPhase.Verifying,
                    "Production pump reported success before three formal records");
                File.WriteAllText(Path.Combine(root, "continue"), "continue");
                Wait(Path.Combine(root, "three-records"), () => worker.Poll() == IndependentWorkerState.Running);
                var three = RecoveryDatabaseEvidence.Read(dbPath, All);
                check(RecoveryDatabaseEvidence.UnverifiedChannels(baseline, three).SequenceEqual(new[] { 4, 5, 12 }),
                    "Three formal commits did not prove exactly the authorized lanes");
                check(three.Channels.Where(lane => new[] { 4, 5, 12 }.Contains(lane.Channel)).All(lane => lane.MechanicalCompletedCount == 1),
                    "Excluded lane advanced");
                check(worker.Poll() == IndependentWorkerState.Running, "Replacement exited before verification");
                // Reconstruct the production pump to exercise durable phase resume.
                pump = new IndependentProjectRecoveryPump(store, operations, "simulation", 30000, 30000);
                pump.Tick(DateTime.UtcNow.Ticks);
                check(store.Read().Transaction.Phase == IndependentRecoveryPhase.Verified &&
                    store.Read().Transaction.Detail.Contains("Channels=7,8,9"),
                    "Production pump did not verify the actual replacement targets after restart");
                var observation = IndependentExecutorObservation.Capture(Guid.NewGuid().ToString("N"),
                    store.Read(), DateTime.UtcNow.Ticks, "SimulationVerification");
                BoundedJson.Write(Path.Combine(root, "executor-observation.json"), observation);
                observation = BoundedJson.Read<IndependentExecutorObservation>(Path.Combine(root, "executor-observation.json"));
                check(observation.SchemaVersion == 2 && observation.ControllerPid == worker.ProcessId &&
                    observation.ControllerStartUtcTicks == worker.StartUtcTicks && observation.RunEpoch == 2 &&
                    observation.RunId == store.Read().Intent.RunId && observation.BindingState == "BoundToDurableRun" &&
                    observation.TransactionPhase == "Verified" && observation.LastTransactionVerified,
                    "Executor observation lost the replacement binding or confused its transaction");
                pump.Tick(DateTime.UtcNow.Ticks);
                check(operations.Stages.All(n => n == 1), "Terminal pump repeated a simulated safety stage");
                File.WriteAllText(Path.Combine(root, "finish"), "finish");
                var watch = Stopwatch.StartNew();
                while (worker.Poll() == IndependentWorkerState.Running && watch.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                check(worker.Poll() == IndependentWorkerState.Completed && worker.ExitCode == 0, "Replacement did not finish cleanly");
            }
            var replayBaseline = RecoveryDatabaseEvidence.Read(dbPath, All);
            using (var replay = new IndependentBoundedWorker(Exe, arguments, root, 5000, 256, (pid, started) =>
                BoundedJson.Write(Path.Combine(root, "replay-owner.json"), new
                { pid, startUtcTicks = started, executable = Exe, purpose = "ConsumedTicketReplay" })))
            {
                while (replay.Poll() == IndependentWorkerState.Running) Thread.Sleep(20);
                check(replay.ExitCode == 23, "Consumed ticket replay accepted");
            }
            var afterReplay = RecoveryDatabaseEvidence.Read(dbPath, All);
            check(replayBaseline.Channels.Zip(afterReplay.Channels, (before, after) =>
                before.LastAllocatedRowId == after.LastAllocatedRowId && before.MechanicalCompletedCount == after.MechanicalCompletedCount).All(value => value),
                "Rejected replay changed committed records");
            Console.WriteLine("SIMULATION evidence: " + root);
            return count;
        }
    }
}
