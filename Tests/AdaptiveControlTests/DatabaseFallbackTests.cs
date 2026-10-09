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
                    Assert(!output.GetAwaiter().GetResult().Contains("DatabaseUnreadable"),
                        "healthy database worker was reported unreadable; inspect guard.log");
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
            Run("人工暂停与通道选择变更不继承旧停滞及旧恢复证明", SelectionAndPauseInvalidateEvidence);
            Run("数据库恢复需全部通道三圈和稳定观察", RecoveryEvidence);
            Run("SQLite并发写入、未提交及原行完成可见性", ConcurrentReader);
            Run("独立恢复逐通道核验新机械事实与三条正式采样记录", RecoveryMechanicalEvidence);
            Run("独立兜底仅接受当前请求的新安全回执", IndependentSafetyReceipt);
            Run("独立接管隔离原Watchdog的延迟启动", IndependentRetirement);
            Run("无Watchdog端点的独立恢复编排与六通道推进验收", IndependentOrchestration);
            Run("独立请求等待中人工停止或心跳消失能够有界收口", IndependentRequestCancellation);
            return 10;
        }

        private static void IndependentRequestCancellation()
        {
            foreach (var manualStop in new[] { true, false })
            {
                var directory = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                    "independent-cancel-" + Guid.NewGuid().ToString("N"));
                var utc = DateTime.UtcNow.Ticks;
                var status = new IndependentFallbackStatus { ProcessId = 17036, ProcessStartUtcTicks = 123,
                    RunId = Guid.NewGuid().ToString("N"), RunEpoch = 3, Armed = true, UpdatedUtcTicks = utc,
                    SessionId = Guid.NewGuid().ToString("N"), Heartbeat = new WatchdogHeartbeat { Phase = "Formal" } };
                BoundedJson.Write(IndependentFallbackProtocol.StatusPath(directory), status);
                using (var recovery = new IndependentRecovery((d, q, r, s) =>
                    throw new InvalidOperationException("unexpected restart"), utcTicks: () => utc))
                {
                    recovery.Observe(directory, true, true);
                    Assert(recovery.IsPending, "request was not created");
                    if (manualStop)
                    {
                        status.Armed = false;
                        BoundedJson.Write(IndependentFallbackProtocol.StatusPath(directory), status);
                    }
                    else
                    {
                        File.Delete(IndependentFallbackProtocol.StatusPath(directory));
                        utc += TimeSpan.FromSeconds(91).Ticks;
                    }
                    recovery.Observe(directory, true, true);
                    Assert(!recovery.IsPending, "request retained ownership after stop/timeout");
                }
            }
        }

        private static void IndependentOrchestration()
        {
            var directory = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "independent-orchestration-" + Guid.NewGuid().ToString("N"), "WatchdogSessions");
            Directory.CreateDirectory(directory);
            var utc = DateTime.UtcNow.Ticks;
            long clock = Stopwatch.Frequency * 10;
            var channels = new[] { 4, 5, 7, 8, 9, 12 };
            var status = new IndependentFallbackStatus { ProcessId = 17036, ProcessStartUtcTicks = 123,
                SessionId = Guid.NewGuid().ToString("N"), RunId = Guid.NewGuid().ToString("N"), RunEpoch = 3,
                Armed = true, UpdatedUtcTicks = utc, Channels = channels, Heartbeat = new WatchdogHeartbeat { Phase = "Formal" } };
            var store = new FallbackLedgerStore(directory, status.SessionId);
            store.Initialize(Guid.NewGuid().ToString("N"), 1, "blocked-original");
            store.PrepareOriginalLaunch("blocked-original", "old");
            store.BindOriginalLaunch("blocked-original", "old", 17036, 123);
            var restarts = 0;
            var cycle = 100L;
            var freezeLast = true;
            using (var recovery = new IndependentRecovery((d, q, r, s) =>
            {
                restarts++;
                Assert(r.Ready && store.Read().IndependentRecoveryRequestId == q.Id, "restart before safety/fence");
                return Tuple.Create(20000, 456L);
            }, (path, selected) => new DatabaseProgressSnapshot { DatabasePath = path, CreationUtcTicks = 1,
                Channels = selected.Select(c => new DatabaseLaneProgress { Channel = c,
                    Cycle = freezeLast && c == 12 ? 100 : cycle,
                    RecentCompletedCycles = freezeLast && c == 12 ? new long[] { 100, 99, 98 } :
                        new long[] { cycle, cycle - 1, cycle - 2 } }).ToArray() }, () => clock, () => utc))
            {
                BoundedJson.Write(IndependentFallbackProtocol.StatusPath(directory), status);
                recovery.Observe(directory, true, true);
                var request = BoundedJson.Read<IndependentFallbackRequest>(IndependentFallbackProtocol.RequestPath(directory));
                recovery.Observe(directory, true, true);
                Assert(restarts == 0, "missing safety response launched process");
                BoundedJson.Write(IndependentFallbackProtocol.ReceiptPath(directory), new IndependentFallbackReceipt
                { RequestId = request.Id, RunId = request.RunId, ProcessId = 17036, ProcessStartUtcTicks = 123,
                    Ready = true, Nonce = Guid.NewGuid().ToString("N"), UpdatedUtcTicks = utc });
                recovery.Observe(directory, true, true);
                Assert(restarts == 1, "blocked original prevented independent restart");
                status.ProcessId = 20000; status.ProcessStartUtcTicks = 456;
                status.RunId = Guid.NewGuid().ToString("N"); status.RunEpoch = 4;
                for (int step = 1; step <= 12; step++)
                {
                    utc += TimeSpan.FromSeconds(5).Ticks; clock += Stopwatch.Frequency * 5; cycle++;
                    status.UpdatedUtcTicks = utc;
                    status.Heartbeat = new WatchdogHeartbeat { ProcessId = 20000, ProcessStartUtcTicks = 456,
                        RunId = status.RunId, RunEpoch = 4, Sequence = step, ExpectedCyclePeriodMs = 1000,
                        RecoveryEligibleChannels = channels,
                        ChannelProgress = channels.Select(c => new WatchdogChannelProgress { Channel = c, State = "Running",
                            FormalCommitRunEpoch = 4, FormalCommitIdentity = "commit" + step,
                            FormalCommitSequence = cycle, DoCommandSequence = cycle, MechanicalCompletedCount = cycle }).ToArray() };
                    BoundedJson.Write(IndependentFallbackProtocol.StatusPath(directory), status);
                    recovery.Observe(directory, true, true);
                    var result = BoundedJson.Read<System.Collections.Generic.Dictionary<string, object>>(
                        Path.Combine(directory, "fallback-independent-status.json"));
                    if (step <= 6) Assert(!(bool)result["RecoveryVerified"], "five moving lanes hid stalled EPB12");
                    if (step == 6) freezeLast = false;
                    if (step == 12) Assert((bool)result["RecoveryVerified"], "six moving lanes did not verify recovery");
                }
                Assert(restarts == 1, "repeated observations launched duplicate replacements");
            }
        }

        private static void IndependentSafetyReceipt()
        {
            var now = DateTime.UtcNow.Ticks;
            var status = new IndependentFallbackStatus { ProcessId = 17036, ProcessStartUtcTicks = now - 100000000,
                RunId = Guid.NewGuid().ToString("N"), RunEpoch = 3, Armed = true, UpdatedUtcTicks = now };
            var request = new IndependentFallbackRequest { Id = Guid.NewGuid().ToString("N"), RunId = status.RunId,
                RunEpoch = 3, ProcessId = status.ProcessId, ProcessStartUtcTicks = status.ProcessStartUtcTicks,
                RequestedUtcTicks = now - TimeSpan.FromSeconds(2).Ticks };
            var receipt = new IndependentFallbackReceipt { RequestId = request.Id, RunId = status.RunId,
                ProcessId = status.ProcessId, ProcessStartUtcTicks = status.ProcessStartUtcTicks,
                Nonce = Guid.NewGuid().ToString("N"), Ready = true, UpdatedUtcTicks = now };
            Assert(IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "fresh safety was rejected");
            status.Armed = false;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "manual stop permitted restart");
            status.Armed = true; receipt.Ready = false;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "failed drain permitted kill");
            receipt.Ready = true; receipt.ProcessStartUtcTicks++;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "PID reuse accepted");
            receipt.ProcessStartUtcTicks--; receipt.RequestId = Guid.NewGuid().ToString("N");
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "old transaction accepted");
            receipt.RequestId = request.Id; receipt.UpdatedUtcTicks = request.RequestedUtcTicks - 1;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "startup safety receipt reused");
            receipt.UpdatedUtcTicks = now; status.RunEpoch++;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now), "new run closed by old receipt");
            status.RunEpoch--;
            Assert(!IndependentFallbackProtocol.CanTerminate(request, receipt, status, now + TimeSpan.FromMinutes(2).Ticks),
                "expired handoff authorized termination");
        }

        private static void IndependentRetirement()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "independent-fence-" + Guid.NewGuid().ToString("N"));
            var store = new FallbackLedgerStore(root, Guid.NewGuid().ToString("N"));
            var ledger = store.Initialize(Guid.NewGuid().ToString("N"), 1, "original");
            store.PrepareOriginalLaunch("original", "old-launch");
            store.BindOriginalLaunch("original", "old-launch", 17036, 123);
            store.RetireForIndependentRecovery(Guid.NewGuid().ToString("N"), 17036, 123);
            ledger = store.Read();
            store.CompleteOriginalLaunch(ledger.Revision, 17036, 123);
            var rejected = false;
            try { store.PrepareOriginalLaunch("original", "late-launch"); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "late original launch escaped independent fence");
            ledger = store.Read(); rejected = false;
            try { store.Request(ledger.Revision, Guid.NewGuid().ToString("N"), "old-guard"); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "old watchdog request escaped independent fence");
            Assert(!ledger.ManualStopped, "independent takeover impersonated manual stop");
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

        private static void SelectionAndPauseInvalidateEvidence()
        {
            var monitor = new DatabaseStallMonitor(); var intent = Intent();
            monitor.Observe(intent, Snapshot(100, 100, 100), 0);
            monitor.Observe(intent, Snapshot(100, 100, 100), 90000);
            monitor.Observe(intent, Snapshot(100, 100, 100), 95000);
            intent.ManualStopped = true;
            monitor.Observe(intent, Snapshot(100, 100, 100), 100000);
            intent.ManualStopped = false;
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 1000000), "pause time counted as stall");
            Assert(!monitor.Observe(intent, Snapshot(100, 100, 100), 1005000), "old confirmations reused");
            monitor.BeginVerification(10, 123);
            monitor.Observe(intent, Snapshot(100, 100, 100), 1010000);
            monitor.Observe(intent, Snapshot(103, 103, 103), 1055000);
            monitor.Observe(intent, Snapshot(105, 105, 105), 1085000);
            Assert(monitor.RecoveryVerified, "verification fixture did not progress");
            intent.Channels = new[] { 7 };
            monitor.Observe(intent, Snapshot(105, 105, 105), 1090000);
            Assert(!monitor.RecoveryVerified, "changed target set inherited success");
            intent.Channels = new[] { 4, 5, 7 };
            Assert(!monitor.Observe(intent, Snapshot(105, 105, 105), 2000000), "re-enabled target inherited old stall");
        }

        private static void RecoveryMechanicalEvidence()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required"),
                "recovery-database-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "index.db");
            using (var writer = new SQLiteConnection("Data Source=" + path + ";Pooling=False;Journal Mode=Wal"))
            {
                writer.Open();
                Action<string> sql = text => { using (var cmd = writer.CreateCommand())
                    { cmd.CommandText = text; cmd.ExecuteNonQuery(); } };
                sql("CREATE TABLE epb_cycles(id INTEGER PRIMARY KEY AUTOINCREMENT, epb_id INTEGER," +
                    "cycle_number INTEGER,status TEXT,end_time TEXT,mechanical_completed INTEGER," +
                    "mechanical_completed_at TEXT,sample_count INTEGER,UNIQUE(epb_id,cycle_number));");
                Action<int, int, string, int, int> insert = (channel, cycle, status, mechanical, samples) =>
                    sql("INSERT INTO epb_cycles(epb_id,cycle_number,status,end_time,mechanical_completed," +
                        "mechanical_completed_at,sample_count) VALUES(" + channel + "," + cycle + ",'" + status +
                        "','2026-09-14T12:00:00+08:00'," + mechanical + ",'2026-09-14T12:00:00+08:00'," + samples + ");");
                foreach (var channel in new[] { 7, 8 })
                {
                    insert(channel, 100, "completed", 1, 100);
                    insert(channel, 101, "running", 0, 0);
                }
                var baseline = RecoveryDatabaseEvidence.Read(path, new[] { 7, 8 });
                var completionIntent = new MTTFTest.Watchdog.Protocol.IndependentRunIntent
                {
                    Revision = 1, ProjectDirectory = Path.GetDirectoryName(path), DatabasePath = path,
                    DatabaseCreationUtcTicks = baseline.CreationUtcTicks, ExecutablePath = Path.Combine(Path.GetDirectoryName(path), "main.exe"),
                    ConfigurationSha256 = new string('a', 64), RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1,
                    SelectedChannels = new[] { 7, 8 }, Armed = true, PeriodMs = 15000, StartupBudgetMs = 180000,
                    MechanicalTargets = baseline.Channels.Select(lane => new MTTFTest.Watchdog.Protocol.IndependentMechanicalTarget
                    { Channel = lane.Channel, TotalCount = lane.MechanicalCompletedCount + (lane.Channel == 8 ? 1 : 0) }).ToArray()
                };
                Assert(RecoveryDatabaseEvidence.CompletedTargets(baseline, completionIntent).SequenceEqual(new[] { 7 }),
                    "committed target completion was not distinguished from pending lane");
                Assert(RecoveryDatabaseEvidence.UnverifiedChannels(baseline, baseline).Length == 2,
                    "target completion weakened three-formal-cycle recovery verification");
                Func<int[]> pending = () => RecoveryDatabaseEvidence.UnverifiedChannels(baseline,
                    RecoveryDatabaseEvidence.Read(path, new[] { 7, 8 }));
                Assert(pending().Length == 2, "old rows proved recovery");
                sql("UPDATE epb_cycles SET status='completed',mechanical_completed=1,sample_count=100 WHERE cycle_number=101;");
                foreach (var channel in new[] { 7, 8 })
                    for (var cycle = 102; cycle <= 103; cycle++) insert(channel, cycle, "completed", 1, 100);
                Assert(pending().Length == 2, "old allocated row counted as new action");
                insert(7, 104, "completed", 1, 100);
                Assert(pending().SequenceEqual(new[] { 8 }), "healthy channel masked stalled peer");
                insert(8, 104, "completed", 0, 100);
                insert(8, 105, "completed", 1, 0);
                insert(8, -1, "learning_completed", 1, 100);
                Assert(pending().SequenceEqual(new[] { 8 }), "missing mechanical/samples or learning counted");
                sql("BEGIN IMMEDIATE;");
                insert(8, 106, "completed", 1, 100);
                Assert(pending().SequenceEqual(new[] { 8 }), "uncommitted completion visible");
                sql("COMMIT;");
                Assert(pending().Length == 0, "three committed new formal mechanical records not accepted");
                var current = RecoveryDatabaseEvidence.Read(path, new[] { 7, 8 });
                current.CreationUtcTicks++;
                var rejected = false;
                try { RecoveryDatabaseEvidence.UnverifiedChannels(baseline, current); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "replaced database inherited evidence");
                current = RecoveryDatabaseEvidence.Read(path, new[] { 7 });
                rejected = false;
                try { RecoveryDatabaseEvidence.UnverifiedChannels(baseline, current); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "subset silently dropped a recovery target");
                Console.WriteLine("Recovery database evidence: " + path);
            }
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
