using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    // Test controller for the real SYSTEM service/interactive task integration.
    // Writes simulated cycles to real SQLite; never creates hardware objects.
    internal static class ServiceControllerSimulation
    {
        internal static void Prepare(string root, string userSid)
        {
            root = Path.GetFullPath(root);
            if (!Path.GetFileName(root).StartsWith("service-controller-simulation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Isolated service fixture required");
            IndependentProtectedFiles.RequireTrustedDirectory(root);
            if (Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Fixture must be empty");
            string executable;
            using (var process = Process.GetCurrentProcess()) executable = process.MainModule.FileName;
            var config = Path.Combine(root, "Config"); Directory.CreateDirectory(config);
            var stateDirectory = Path.Combine(root, "State"); Directory.CreateDirectory(stateDirectory);
            var files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }.Select(name =>
            {
                var path = Path.Combine(config, name); File.WriteAllText(path, "<simulation-only/>");
                return new IndependentSafetyConfigFile { Name = name, Sha256 = SupervisorProtocol.ComputeSha256(path) };
            }).ToArray();
            File.WriteAllText(Path.Combine(config, "SIMULATED-HARDWARE-ONLY.txt"), "success");
            File.WriteAllText(Path.Combine(root, "SERVICE-CONTROLLER-SIMULATION-ONLY.txt"), "SIMULATED_NO_HARDWARE");
            var database = Path.Combine(root, "index.db"); SQLiteConnection.CreateFile(database);
            using (var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = database, Pooling = false }.ConnectionString))
            {
                db.Open(); using (var sql = db.CreateCommand())
                {
                    sql.CommandText = "CREATE TABLE epb_cycles(id INTEGER PRIMARY KEY AUTOINCREMENT,epb_id INTEGER,cycle_number INTEGER," +
                        "status TEXT,end_time TEXT,mechanical_completed INTEGER,mechanical_completed_at TEXT,sample_count INTEGER,UNIQUE(epb_id,cycle_number))";
                    sql.ExecuteNonQuery();
                }
            }
            TicketDatabaseSimulationTests.CommitCycle(root, new[] { 4, 5, 7, 8, 9, 12 }, 100);
            var registration = new IndependentExecutorRegistration
            {
                InstallationId = Guid.NewGuid().ToString("N"), InteractiveUserSid = userSid,
                ProjectDirectory = root, DatabasePath = database, DatabaseCreationUtcTicks = File.GetCreationTimeUtc(database).Ticks,
                StateDirectory = stateDirectory, ConfigDirectory = config, ExecutablePath = executable, SafetyExecutablePath = executable,
                ExecutableSha256 = SupervisorProtocol.ComputeSha256(executable), SafetyExecutableSha256 = SupervisorProtocol.ComputeSha256(executable),
                ConfigurationSha256 = new string('c', 64), Files = files, StartupPositioningBudgetMs = 5000,
                Runtime = new SafetyRuntimeSnapshot
                {
                    SampleRateHz = 2000, SamplesPerChannel = 20, PressureChannels = new[] { "Pressure_1" },
                    ReleaseSafePressureBar = new[] { 1d }, PressureSampleMaxAgeMs = 100, ReleaseStableMs = 300, ReleaseTimeoutMs = 5000
                }
            };
            registration.Validate();
            BoundedJson.Write(Path.Combine(stateDirectory, "registration.json"), registration);
            new IndependentProjectStateStore(stateDirectory).Update(0, state => true);
        }

        internal static void ArmExitedController(string registrationPath, int interactiveSession)
        {
            var registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            if (!Path.GetFileName(registration.ProjectDirectory).StartsWith("service-controller-simulation-", StringComparison.Ordinal) || interactiveSession <= 0)
                throw new InvalidOperationException("Isolated interactive simulation required");
            var store = new IndependentProjectStateStore(registration.StateDirectory);
            var state = store.Read();
            var intent = new IndependentRunIntent
            {
                Revision = 1, RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, Armed = true,
                ProjectDirectory = registration.ProjectDirectory, DatabasePath = registration.DatabasePath,
                DatabaseCreationUtcTicks = registration.DatabaseCreationUtcTicks, ExecutablePath = registration.ExecutablePath,
                ConfigurationSha256 = registration.ConfigurationSha256, PeriodMs = 1000, StartupBudgetMs = 5000,
                SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 }, PausedChannels = new[] { 4 },
                PermanentChannels = new[] { 5 }, CompletedChannels = new[] { 12 }
            };
            // Synthetic absent identity; this case does not prove termination
            // of a live hung controller. No unrelated PID may be killed.
            var old = new IndependentProcessIdentity { Pid = int.MaxValue - 5, StartUtcTicks = 1,
                WindowsSessionId = interactiveSession, ExecutablePath = registration.ExecutablePath, SessionToken = Guid.NewGuid().ToString("N") };
            store.ArmManualRun(state.Revision, intent, old, DateTime.UtcNow.Ticks);
        }

        internal static int RunStalledController(string registrationPath, string scenario)
        {
            var healthy = scenario == "single" ? new[] { 7, 8 } : scenario == "partial" ? new[] { 7 } :
                scenario == "all" ? Array.Empty<int>() : scenario == "channel-pause" ? new[] { 8 } :
                throw new ArgumentException("Unknown stall scenario");
            var registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            var root = registration.ProjectDirectory;
            if (!Path.GetFileName(root).StartsWith("service-controller-simulation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Isolated service fixture required");
            var marker = Path.Combine(root, "SERVICE-CONTROLLER-SIMULATION-ONLY.txt");
            IndependentProtectedFiles.RequireTrustedFile(marker);
            if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != "SIMULATED_NO_HARDWARE")
                throw new InvalidDataException("Service simulation marker invalid");
            using (var user = WindowsIdentity.GetCurrent())
                if (user.IsSystem || user.User.Value != registration.InteractiveUserSid)
                    throw new UnauthorizedAccessException("Interactive simulation account required");
            IndependentProcessIdentity identity;
            using (var process = Process.GetCurrentProcess()) identity = new IndependentProcessIdentity
            {
                Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                SessionToken = Guid.NewGuid().ToString("N")
            };
            // The owner is recorded before arming; independent test supervision
            // can identify this process even if subsequent initialization fails.
            BoundedJson.Write(Path.Combine(root, "old-controller-owner.json"), identity);
            var store = new IndependentProjectStateStore(registration.StateDirectory);
            var intent = new IndependentRunIntent
            {
                Revision = 1, RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, Armed = true,
                ProjectDirectory = root, DatabasePath = registration.DatabasePath,
                DatabaseCreationUtcTicks = registration.DatabaseCreationUtcTicks, ExecutablePath = registration.ExecutablePath,
                ConfigurationSha256 = registration.ConfigurationSha256, PeriodMs = 1000, StartupBudgetMs = 5000,
                SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 }, PausedChannels = new[] { 4 },
                PermanentChannels = new[] { 5 }, CompletedChannels = new[] { 12 }
            };
            store.ArmManualRun(store.Read().Revision, intent, identity, DateTime.UtcNow.Ticks);
            IndependentExecutionFence.AttachCurrent(registration.InstallationId, identity);
            if (scenario == "channel-pause")
            {
                store.SetControllerChannelPause(identity, intent.RunId, intent.RunEpoch, 7, true,
                    Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
                BoundedJson.Write(Path.Combine(root, "channel-pause-state.json"), store.Read());
            }
            var elapsed = Stopwatch.StartNew(); var cycle = 100; var fenced = false;
            try
            {
                // Deliberately ignore cooperative stop requests. Healthy lanes
                // continue formal commits until the independent fence revokes
                // output authority; then remain alive for exact forced retirement.
                while (elapsed.ElapsedMilliseconds < 250000 && !File.Exists(Path.Combine(root, "finish-simulation.txt")))
                {
                    if (!fenced)
                    {
                        try { IndependentExecutionFence.RequireCurrentAuthority(); }
                        catch (Exception error)
                        {
                            fenced = true;
                            BoundedJson.Write(Path.Combine(root, "old-controller-fenced.json"), new
                            { pid = identity.Pid, elapsedMs = elapsed.ElapsedMilliseconds, reason = error.Message });
                        }
                    }
                    if (!fenced && healthy.Length > 0)
                        TicketDatabaseSimulationTests.CommitCycle(root, healthy, ++cycle);
                    Thread.Sleep(1000);
                }
                throw new TimeoutException("Stalled controller was not independently retired");
            }
            finally
            {
                var state = store.Read();
                if (identity.Matches(state.Controller))
                    store.RecordControllerManualStop(identity, intent.RunId, intent.RunEpoch, Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
            }
        }

        internal static int Resume(string registrationPath, string nonce)
        {
            var registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            var root = registration.ProjectDirectory;
            if (!Path.GetFileName(root).StartsWith("service-controller-simulation-", StringComparison.Ordinal) ||
                registration.DatabasePath != Path.Combine(root, "index.db"))
                throw new InvalidOperationException("Service simulation project required");
            var marker = Path.Combine(root, "SERVICE-CONTROLLER-SIMULATION-ONLY.txt");
            IndependentProtectedFiles.RequireTrustedFile(marker);
            if (new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != "SIMULATED_NO_HARDWARE")
                throw new InvalidDataException("Service simulation marker invalid");
            using (var user = WindowsIdentity.GetCurrent())
                if (user.IsSystem || user.User.Value != registration.InteractiveUserSid)
                    throw new UnauthorizedAccessException("Interactive simulation account required");
            IndependentProcessIdentity identity;
            using (var process = Process.GetCurrentProcess()) identity = new IndependentProcessIdentity
            {
                Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                SessionToken = Guid.NewGuid().ToString("N")
            };
            var store = new IndependentProjectStateStore(registration.StateDirectory);
            var before = store.Read(); registration.RequireBoundIntent(before.Intent);
            store.ConsumeLaunchTicket(before.Revision, nonce, identity,
                SupervisorProtocol.ComputeSha256(identity.ExecutablePath), DateTime.UtcNow.Ticks);
            var runId = Guid.NewGuid().ToString("N"); var epoch = checked(before.Intent.RunEpoch + 1);
            IndependentExecutionFence.AttachCurrent(registration.InstallationId, identity);
            store.CommitReplacementRun(store.Read().Revision, identity, runId, epoch, DateTime.UtcNow.Ticks);
            BoundedJson.Write(Path.Combine(root, "replacement-owner.json"), identity);
            try
            {
                for (var index = 0; index < 3; index++)
                {
                    IndependentExecutionFence.RequireCurrentAuthority();
                    var state = store.Read();
                    if (!identity.Matches(state.Controller) || state.Intent.RunId != runId || state.Intent.RunEpoch != epoch ||
                        state.Maintenance || state.Ticket.Revoked || state.Intent.RecoveryChannels().Length == 0)
                        throw new InvalidOperationException("Simulation authority revoked");
                    foreach (var channel in state.Intent.RecoveryChannels())
                    {
                        int next;
                        using (var db = new SQLiteConnection(new SQLiteConnectionStringBuilder
                        { DataSource = registration.DatabasePath, FailIfMissing = true, Pooling = false, DefaultTimeout = 1 }.ConnectionString))
                        {
                            db.Open(); using (var query = db.CreateCommand())
                            {
                                query.CommandText = "SELECT COALESCE(MAX(cycle_number),0)+1 FROM epb_cycles WHERE epb_id=@channel";
                                query.Parameters.AddWithValue("@channel", channel);
                                next = Convert.ToInt32(query.ExecuteScalar());
                            }
                        }
                        TicketDatabaseSimulationTests.CommitCycle(root, new[] { channel }, next);
                    }
                    Thread.Sleep((int)Math.Min(1000, state.Intent.PeriodMs));
                }
                File.WriteAllText(Path.Combine(root, "three-formal-records.txt"), "SIMULATED_ACTIONS_REAL_SQLITE");
                var wait = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "finish-simulation.txt")))
                {
                    if (wait.ElapsedMilliseconds >= 30000) throw new TimeoutException("Simulation completion acknowledgement missing");
                    IndependentExecutionFence.RequireCurrentAuthority(); Thread.Sleep(50);
                }
                return 0;
            }
            finally
            {
                // Explicit test completion/timeout stops this isolated run so
                // disposal cannot inadvertently start another service recovery.
                store.RecordControllerManualStop(identity, runId, epoch, Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
            }
        }
    }
}
