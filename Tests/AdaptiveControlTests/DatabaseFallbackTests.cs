using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.FallbackGuard;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class DatabaseFallbackTests
    {
        internal static void RunProcessIntegration(string executable)
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required"), "database-process-" + Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "WatchdogSessions"); Directory.CreateDirectory(directory);
            var path = Path.Combine(root, "index.db");
            using (var db = new SQLiteConnection("Data Source=" + path + ";Pooling=False;Journal Mode=Wal"))
            {
                db.Open(); using (var command = db.CreateCommand())
                { command.CommandText = "CREATE TABLE epb_cycles(epb_id INTEGER,cycle_number INTEGER,status TEXT,end_time TEXT);" +
                    "INSERT INTO epb_cycles VALUES(4,100,'completed','2026-09-13');"; command.ExecuteNonQuery(); }
            }
            var session = Guid.NewGuid().ToString("N"); var run = Guid.NewGuid().ToString("N");
            var store = new FallbackLedgerStore(directory, session); store.Initialize(run, 1, "fixture");
            var settingsPath = Path.Combine(directory, "fallback-settings-" + session + ".json");
            var observationPath = Path.Combine(directory, "fallback-observation-" + session + ".json");
            BoundedJson.Write(settingsPath, new { SchemaVersion = 1, Enabled = true, Active = true });
            BoundedJson.Write(observationPath, new FallbackObservation { SessionId = session, InstallationId = "fixture",
                SourceInstanceId = "fixture", Sequence = 1, CapturedUtc = DateTime.UtcNow.ToString("O"),
                Heartbeat = new WatchdogHeartbeat { RunId = run, RunEpoch = 1, RunActive = true, Phase = "Formal",
                    ExpectedCyclePeriodMs = 15000, RecoveryEligibleChannels = new[] { 4 }, ChannelProgress = Array.Empty<WatchdogChannelProgress>() } });
            var requests = 0;
            using (var cancellation = new CancellationTokenSource())
            using (var guard = new Process())
            {
                var server = Task.Run(() => FallbackControlPipe.ServeAsync(session, command =>
                {
                    Assert(command.Action == "Request" && command.RunId == run && command.RunEpoch == 1, "incorrect recovery request");
                    Interlocked.Increment(ref requests);
                    return new FallbackCommandResult { Status = "Completed", Ledger = store.Read() };
                }, cancellation.Token));
                guard.StartInfo = new ProcessStartInfo(Path.GetFullPath(executable),
                    "--run --project-directory \"" + directory + "\" --session-id " + session)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                guard.Start();
                var output = guard.StandardOutput.ReadToEndAsync(); var error = guard.StandardError.ReadToEndAsync();
                try
                {
                    var watch = Stopwatch.StartNew();
                    var intentPath = Path.Combine(directory, "fallback-database-intent-" + session + ".json");
                    while (!File.Exists(intentPath) && watch.ElapsedMilliseconds < 10000 && !guard.HasExited) Thread.Sleep(100);
                    Assert(File.Exists(intentPath), "real isolated reader could not bind database");
                    // The independent observer must continue after all original
                    // observation files disappear. The only live server is this
                    // request recorder; it cannot control hardware or launch a trial.
                    File.Delete(observationPath);
                    while (Volatile.Read(ref requests) == 0 && watch.ElapsedMilliseconds < 125000 && !guard.HasExited)
                    { guard.Refresh(); Assert(guard.PrivateMemorySize64 <= 128L * 1024 * 1024, "guard exceeded memory cap"); Thread.Sleep(100); }
                    Assert(Volatile.Read(ref requests) > 0, "DB stall did not request takeover without original observation");
                    Assert(watch.ElapsedMilliseconds >= 90000, "stall budget bypassed");
                    store.Stop();
                    Thread.Sleep(1500);
                    var afterStop = Volatile.Read(ref requests);
                    Thread.Sleep(5500);
                    Assert(Volatile.Read(ref requests) == afterStop, "manual stop did not suppress requests");
                    BoundedJson.Write(settingsPath, new { SchemaVersion = 1, Enabled = false, Active = true });
                    Assert(guard.WaitForExit(5000) && guard.ExitCode == 0, "disabled guard failed to exit");
                    Console.WriteLine("PASS real DB worker: missing original observation, timed stall request, manual stop, independent exit; " + root);
                }
                finally
                {
                    if (!guard.HasExited) { guard.Kill(); Assert(guard.WaitForExit(5000), "guard cleanup failed"); }
                    cancellation.Cancel(); Assert(server.Wait(5000), "fixture pipe cleanup failed");
                    File.WriteAllText(Path.Combine(root, "guard.log"), output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                }
            }
        }
        internal static int RunAll()
        {
            Run("数据库单路/部分/全部停滞及短暂空窗", StallScopes);
            Run("数据库失败不等于停滞、停止与旧数据隔离", UnknownAndStop);
            Run("数据库恢复需全部通道三圈和稳定观察", RecoveryEvidence);
            Run("SQLite并发写入、未提交及原行完成可见性", ConcurrentReader);
            return 4;
        }

        private static void Run(string name, Action action)
        { action(); Console.WriteLine("PASS " + name); }
        private static void Assert(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }
        private static DatabaseWatchIntent Intent() => new DatabaseWatchIntent
        { SessionId = "session", RunId = "run", RunEpoch = 1, DatabasePath = "db", DatabaseCreationUtcTicks = 1,
            PeriodMs = 15000, Channels = new[] { 4, 5, 7 } };
        private static DatabaseProgressSnapshot Snapshot(params long[] cycles) => new DatabaseProgressSnapshot
        { DatabasePath = "db", CreationUtcTicks = 1, Channels = new[] { 4, 5, 7 }.Select((c, i) =>
            new DatabaseLaneProgress { Channel = c, Cycle = cycles[i],
                RecentCompletedCycles = new[] { cycles[i], cycles[i] - 1, cycles[i] - 2 } }).ToArray() };

        private static void StallScopes()
        {
            for (int stalledCount = 1; stalledCount <= 3; stalledCount++)
            {
                var monitor = new DatabaseStallMonitor();
                var intent = Intent();
                monitor.Observe(intent, Snapshot(100, 100, 100), 0);
                Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 30000), "short gap triggered restart");
                for (int round = 0; round < 3; round++)
                {
                    var cycles = Enumerable.Range(0, 3).Select(i => i < stalledCount ? 100L : 110L + round).ToArray();
                    var stalled = monitor.Observe(intent, Snapshot(cycles), 90000 + round * 5000);
                    Assert(stalled == (round == 2), "three successful observations required");
                }
                Assert(monitor.StalledChannels.SequenceEqual(intent.Channels.Take(stalledCount)), "wrong stalled scope");
            }
        }

        private static void UnknownAndStop()
        {
            var monitor = new DatabaseStallMonitor(); var intent = Intent();
            monitor.Observe(intent, Snapshot(100, 100, 100), 0);
            monitor.Observe(intent, Snapshot(100, 100, 100), 90000);
            monitor.Unreadable();
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 95000), "failure was counted as evidence");
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 95000), "duplicate observation counted");
            intent.ManualStopped = true;
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 150000), "manual stop restarted");
            intent.ManualStopped = false; intent.RunEpoch++;
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 160000), "old run stall reused");
            var rejected = false;
            try { monitor.Observe(intent, Snapshot(99, 100, 100), 165000); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "count regression not rejected");
            rejected = false;
            var replaced = Snapshot(100, 100, 100); replaced.CreationUtcTicks++;
            try { monitor.Observe(intent, replaced, 170000); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "database replacement accepted");
        }

        private static void RecoveryEvidence()
        {
            var monitor = new DatabaseStallMonitor(); var intent = Intent();
            monitor.BeginVerification(10, 123);
            monitor.Observe(intent, Snapshot(100, 100, 100), 0);
            monitor.Observe(intent, Snapshot(103, 103, 102), 45000);
            Assert(!monitor.RecoveryVerified, "partial recovery reported as success");
            monitor.Observe(intent, Snapshot(103, 103, 103), 50000);
            Assert(!monitor.RecoveryVerified, "stable observation omitted");
            monitor.Observe(intent, Snapshot(105, 105, 105), 80000);
            Assert(monitor.RecoveryVerified, "all lanes committed but verification failed");
            monitor.BeginVerification(11, 456);
            Assert(!monitor.RecoveryVerified, "replacement process inherited old proof");
            monitor.Unreadable();
            Assert(!monitor.RecoveryVerified, "failed database read retained success");
        }

        private static void ConcurrentReader()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Codex")),
                "database-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "index.db");
            using (var writer = new SQLiteConnection("Data Source=" + path + ";Pooling=False;Journal Mode=Wal"))
            {
                writer.Open();
                Action<string> execute = sql => { using (var cmd = writer.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); } };
                execute("CREATE TABLE epb_cycles(epb_id INTEGER,cycle_number INTEGER,status TEXT,end_time TEXT);" +
                    "CREATE INDEX idx ON epb_cycles(epb_id,cycle_number);" +
                    "INSERT INTO epb_cycles VALUES(4,10,'completed','2026-09-13T00:00:00');" +
                    "INSERT INTO epb_cycles VALUES(4,11,'running',NULL);");
                Assert(DatabaseProgressReader.Read(path, new[] { 4 }).Channels[0].Cycle == 10, "running row counted");
                execute("BEGIN IMMEDIATE; UPDATE epb_cycles SET status='completed',end_time='2026-09-13T00:00:15' WHERE cycle_number=11;");
                Assert(DatabaseProgressReader.Read(path, new[] { 4 }).Channels[0].Cycle == 10, "uncommitted data visible");
                execute("COMMIT;");
                Assert(DatabaseProgressReader.Read(path, new[] { 4 }).Channels[0].Cycle == 11, "completed update missed");
                execute("INSERT INTO epb_cycles VALUES(4,12,'completed','2026-09-13T00:00:30');");
                Assert(DatabaseProgressReader.Read(path, new[] { 4 }).Channels[0].Cycle == 12, "reader blocked subsequent writer");
            }
            // Preserve this small synthetic database as a reproducible test artifact.
            Console.WriteLine("Database test evidence: " + path);
        }
    }
}
