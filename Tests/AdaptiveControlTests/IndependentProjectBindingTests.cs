using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentProjectBindingTests
    {
        internal static int RunAll()
        {
            var passed = VerifyManualArmFailureRevocation();
            var fixture = new Fixture();
            foreach (var project in new[] { "B", "C", "A" })
            {
                fixture.Change(project);
                fixture.AssertConsistent(project);
                var registration = fixture.ReadRegistration(fixture.RegistrationPath);
                var state = fixture.Store.Read();
                var intent = fixture.Intent(registration, new[] { 5, 8, 9 });
                intent.Armed = true; intent.ManualStopped = false;
                fixture.Store.ArmManualRun(state.Revision, intent, fixture.Controller, DateTime.UtcNow.Ticks);
                fixture.Store.SetControllerSelection(fixture.Controller, intent.RunId, intent.RunEpoch,
                    8, false, Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
                fixture.Store.SetControllerSelection(fixture.Controller, intent.RunId, intent.RunEpoch,
                    7, true, Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
                Assert(fixture.Store.Read().Intent.SelectedChannels.OrderBy(c => c).SequenceEqual(new[] { 5, 7, 9 }),
                    "channel add/remove was not durable after rebinding");
                fixture.Store.RecordControllerManualStop(fixture.Controller, intent.RunId, intent.RunEpoch,
                    Guid.NewGuid().ToString("N"), DateTime.UtcNow.Ticks);
                passed++;
            }
            Assert(fixture.ServiceTransitions.SequenceEqual(new[] { false, true, false, true, false, true }),
                "binding did not stop and rebuild the service runtime for each project");
            passed++;

            fixture = new Fixture();
            fixture.Change("A", 'c');
            var refreshed = fixture.Store.Read();
            Assert(refreshed.Intent.PermanentChannels.SequenceEqual(new[] { 9 }) &&
                refreshed.Intent.ConfigurationSha256 == new string('c', 64) && !refreshed.Intent.Armed &&
                refreshed.Intent.ManualStopped, "same-project refresh lost exclusion or rearmed old intent");
            passed++;

            foreach (var cut in new[] { "Maintenance", "ServiceStopped", "Prepared", "RegistrationReplaced",
                "StateReplaced", "SetupReplaced", "Committed", "ServiceStarted", "Enabled" })
            {
                fixture = new Fixture();
                Reject(() => fixture.Change("B", 'b', stage =>
                {
                    if (stage == cut) throw new IOException("Injected:" + cut);
                }), "injected transaction interruption was swallowed");
                Assert(fixture.Store.Read().Maintenance && File.Exists(IndependentProjectBinding.PendingPath(fixture.RegistrationPath)),
                    "interruption escaped maintenance or lost its durable retry journal: " + cut);
                Assert(fixture.Store.Read().Intent?.Armed != true, "interrupted migration armed a run");
                IndependentProjectBinding.RecoverPendingCore(fixture.RegistrationPath, fixture.ReadRegistration, fixture.Service);
                var expected = new[] { "Committed", "ServiceStarted", "Enabled" }.Contains(cut) ? "B" : "A";
                fixture.AssertConsistent(expected);
                var beforeRetry = fixture.ServiceTransitions.Count;
                IndependentProjectBinding.RecoverPendingCore(fixture.RegistrationPath, fixture.ReadRegistration, fixture.Service);
                Assert(beforeRetry == fixture.ServiceTransitions.Count, "completed transaction recovery was not idempotent");
                fixture.Change("B");
                fixture.AssertConsistent("B");
                passed++;
            }

            foreach (var blocked in new[] { "Running", "Paused", "Cleanup", "Session" })
            {
                fixture = new Fixture();
                fixture.Store.Update(fixture.Store.Read().Revision, state =>
                {
                    if (blocked == "Running" || blocked == "Paused")
                    {
                        state.Intent.Armed = true; state.Intent.ManualStopped = false;
                        state.Intent.ManualPaused = blocked == "Paused";
                    }
                    if (blocked == "Cleanup")
                    {
                        state.Transaction = IndependentRecoveryTransitions.BeginOperatorStop(null, state.Intent,
                            "fixture", DateTime.UtcNow.Ticks, state.Intent.SelectedChannels);
                        state.SafetyCleanupPending = true;
                    }
                    if (blocked == "Session") state.SessionProcesses = new[] { new IndependentSessionProcess
                    { Process = fixture.Controller, ParentPid = fixture.Controller.Pid,
                        ParentStartUtcTicks = fixture.Controller.StartUtcTicks, Role = "Watchdog" } };
                    return true;
                });
                Reject(() => fixture.Change("B"), "binding admitted " + blocked);
                Assert(fixture.ServiceTransitions.Count == 0 && !File.Exists(IndependentProjectBinding.PendingPath(fixture.RegistrationPath)),
                    "rejected boundary changed installation authority");
                passed++;
            }

            fixture = new Fixture();
            fixture.Change("B");
            var oldDatabase = Path.Combine(fixture.Root, "Projects", "A", "index.db");
            var originalCreation = File.GetCreationTimeUtc(oldDatabase);
            var replacement = oldDatabase + ".replacement";
            File.WriteAllText(replacement, "replacement database with copied creation timestamp");
            File.Replace(replacement, oldDatabase, oldDatabase + ".backup");
            File.SetCreationTimeUtc(oldDatabase, originalCreation);
            Reject(() => fixture.Change("A"), "return to old project accepted a replaced database with copied timestamp");
            fixture.AssertConsistent("B");
            passed++;

            fixture = new Fixture();
            var currentDatabase = fixture.ReadRegistration(fixture.RegistrationPath).DatabasePath;
            File.SetCreationTimeUtc(currentDatabase, File.GetCreationTimeUtc(currentDatabase).AddSeconds(5));
            Reject(() => fixture.Change("A"), "same-project database replacement was silently rebound");
            Assert(fixture.ServiceTransitions.Count == 0, "database rejection stopped the service");
            passed++;

            fixture = new Fixture();
            var candidatePath = fixture.Prepare("B", 'b');
            var foreign = fixture.ReadRegistration(candidatePath);
            foreign.ExecutableSha256 = new string('f', 64);
            BoundedJson.Write(candidatePath, foreign);
            Reject(() => IndependentProjectBinding.CommitPrepared(fixture.RegistrationPath, candidatePath,
                fixture.ReadRegistration, fixture.Service), "project switch changed executable trust identity");
            fixture.AssertConsistent("A");
            passed++;

            fixture = new Fixture();
            var tuned = fixture.ReadRegistration(fixture.RegistrationPath);
            tuned.StartupPositioningBudgetMs = 123000;
            BoundedJson.Write(fixture.RegistrationPath, tuned);
            candidatePath = fixture.Prepare("B", 'b');
            var exported = fixture.ReadRegistration(candidatePath);
            exported.StartupPositioningBudgetMs = 300000;
            BoundedJson.Write(candidatePath, exported);
            Reject(() => IndependentProjectBinding.CommitPrepared(fixture.RegistrationPath, candidatePath,
                fixture.ReadRegistration, fixture.Service), "project refresh silently reset the installed startup budget");
            IndependentProjectBinding.PreserveInstallationSettings(tuned, exported);
            BoundedJson.Write(candidatePath, exported);
            IndependentProjectBinding.CommitPrepared(fixture.RegistrationPath, candidatePath, fixture.ReadRegistration, fixture.Service);
            fixture.AssertConsistent("B");
            Assert(fixture.ReadRegistration(fixture.RegistrationPath).StartupPositioningBudgetMs == 123000,
                "prepared project binding lost the installed startup budget");
            passed++;
            Console.WriteLine("PASS independent project binding " + passed + "/" + passed + "; hardware=NOT_ACCESSED; service=SIMULATED");
            return passed;
        }

        private static int VerifyManualArmFailureRevocation()
        {
            var fixture = new Fixture();
            var config = new GlobalConfig { Test = new TestConfig
            { StoreDir = Path.Combine(fixture.Root, "Projects"), TestName = "A", TestPeriod = 15, TestTarget = 100 } };
            config.Test.GetEpbRecord(9).PermanentAlarmLatched = true;
            var projectConfig = Path.Combine(fixture.Root, "Projects", "A", "Config", "TestConfig.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(projectConfig));
            File.WriteAllText(projectConfig, "<TestConfig><TestPeriod>15</TestPeriod><TestTarget>100</TestTarget></TestConfig>");
            var registration = fixture.ReadRegistration(fixture.RegistrationPath);
            registration.ConfigurationSha256 = UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config);
            var startup = BindStartup(fixture, registration);
            var context = WatchdogRuntime.CreateUiBindingProductionContext(new[] { 9 });
            var active = typeof(WatchdogRuntime).GetField("_activeContext", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = active.GetValue(null);
            try
            {
                active.SetValue(null, context);
                Reject(() => startup.ArmManualRun(config, new[] { 9 }, 0), "all-permanently-excluded run passed post-arm validation");
                var failed = fixture.Store.Read();
                Assert(!failed.Intent.Armed && failed.Intent.ManualStopped, "post-arm validation failure retained durable restart permission");
                Assert(!context.TryRejectBeforeControlAdmission(), "durable arming was incorrectly classified as never admitted");
                startup.RequireManualStopPersistenceCompleted();
            }
            finally { active.SetValue(null, previous); }

            fixture = new Fixture();
            registration = fixture.ReadRegistration(fixture.RegistrationPath);
            var intent = fixture.Intent(registration, new[] { 9 });
            intent.Armed = true; intent.ManualStopped = false; intent.PermanentChannels = new[] { 9 };
            fixture.Store.ArmManualRun(fixture.Store.Read().Revision, intent, fixture.Controller, DateTime.UtcNow.Ticks);
            startup = BindStartup(fixture, registration);
            SetStartupProperty(startup, "RunId", intent.RunId);
            SetStartupProperty(startup, "RunEpoch", intent.RunEpoch);
            var aggregateSeen = false;
            using (var held = new FileStream(Path.Combine(registration.StateDirectory, "independent-project-state.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { startup.ValidateArmedManualRun(intent); }
                catch (AggregateException error)
                {
                    aggregateSeen = error.Message.Contains("IndependentManualStartRevocationUnconfirmed") && error.InnerExceptions.Count == 2;
                }
            }
            Assert(aggregateSeen && fixture.Store.Read().Intent.Armed, "failed durable revocation was hidden or fabricated as complete");
            Reject(startup.RequireManualStopPersistenceCompleted, "failed revocation allowed a fresh manual start");
            return 2;
        }

        private static IndependentRecoveryStartup BindStartup(Fixture fixture, IndependentExecutorRegistration registration)
        {
            var startup = (IndependentRecoveryStartup)Activator.CreateInstance(typeof(IndependentRecoveryStartup), true);
            SetStartupProperty(startup, "Registration", registration);
            SetStartupProperty(startup, "Identity", fixture.Controller);
            typeof(IndependentRecoveryStartup).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(startup, fixture.Store);
            return startup;
        }

        private static void SetStartupProperty(IndependentRecoveryStartup startup, string name, object value)
            => typeof(IndependentRecoveryStartup).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(startup, value);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            catch (InvalidDataException) { return; }
            catch (IOException) { return; }
            throw new Exception(message);
        }

        private sealed class Fixture
        {
            internal readonly string Root, RegistrationPath;
            internal readonly IndependentProjectStateStore Store;
            internal readonly IndependentProcessIdentity Controller;
            internal readonly List<bool> ServiceTransitions = new List<bool>();

            internal Fixture()
            {
                Root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                    "project-binding-" + Guid.NewGuid().ToString("N"));
                var stateDirectory = Path.Combine(Root, "State");
                Directory.CreateDirectory(stateDirectory);
                Directory.CreateDirectory(Path.Combine(Root, "Current"));
                foreach (var project in new[] { "A", "B", "C" })
                {
                    var directory = Path.Combine(Root, "Projects", project);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "index.db"), project + " historical evidence");
                }
                RegistrationPath = Path.Combine(stateDirectory, "registration.json");
                var registration = new IndependentExecutorRegistration
                {
                    InstallationId = Guid.NewGuid().ToString("N"),
                    InteractiveUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value,
                    StateDirectory = stateDirectory, ExecutablePath = Path.Combine(Root, "Current", "MTTFTest.exe"),
                    SafetyExecutablePath = Path.Combine(Root, "Current", "MTTFTest.SafetyAgent.exe"),
                    ExecutableSha256 = new string('a', 64), SafetyExecutableSha256 = new string('a', 64),
                    ConfigurationSha256 = new string('a', 64), ConfigDirectory = Path.Combine(Root, "Config"),
                    ProjectDirectory = Path.Combine(Root, "Projects", "A"),
                    DatabasePath = Path.Combine(Root, "Projects", "A", "index.db"),
                    Files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }
                        .Select(name => new IndependentSafetyConfigFile { Name = name, Sha256 = new string('a', 64) }).ToArray(),
                    Runtime = new SafetyRuntimeSnapshot { SampleRateHz = 2000, SamplesPerChannel = 20,
                        PressureChannels = new[] { "Pressure_1" }, ReleaseSafePressureBar = new[] { 1d },
                        PressureSampleMaxAgeMs = 100, ReleaseStableMs = 300, ReleaseTimeoutMs = 5000 }
                };
                registration.DatabaseCreationUtcTicks = File.GetCreationTimeUtc(registration.DatabasePath).Ticks;
                BoundedJson.Write(RegistrationPath, registration);
                BoundedJson.Write(Path.Combine(Root, "install-setup.json"), new MtEmbTest.IndependentInstallSetup.SetupState
                { SchemaVersion = 1, Stage = "Ready", InteractiveUserSid = registration.InteractiveUserSid,
                    ProjectDirectory = registration.ProjectDirectory, Version = "fixture" });
                using (var process = Process.GetCurrentProcess()) Controller = new IndependentProcessIdentity
                {
                    Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    WindowsSessionId = process.SessionId, ExecutablePath = registration.ExecutablePath,
                    SessionToken = Guid.NewGuid().ToString("N")
                };
                Store = new IndependentProjectStateStore(stateDirectory);
                Store.Update(0, state =>
                {
                    state.Maintenance = false;
                    state.Intent = Intent(registration, new[] { 5, 8, 9 });
                    state.Intent.PermanentChannels = new[] { 9 };
                    state.Controller = Controller;
                    state.RootRunId = state.Intent.RunId;
                    state.RunStartedUtcTicks = DateTime.UtcNow.Ticks;
                    state.StartupDeadlineUtcTicks = state.RunStartedUtcTicks + TimeSpan.FromMilliseconds(state.Intent.StartupBudgetMs).Ticks;
                    return true;
                });
            }

            internal IndependentRunIntent Intent(IndependentExecutorRegistration registration, int[] channels)
                => new IndependentRunIntent
                {
                    Revision = 1, RunId = Guid.NewGuid().ToString("N"), RunEpoch = (Store?.Read()?.Intent?.RunEpoch ?? 0) + 1,
                    ProjectDirectory = registration.ProjectDirectory, DatabasePath = registration.DatabasePath,
                    DatabaseCreationUtcTicks = registration.DatabaseCreationUtcTicks, ExecutablePath = registration.ExecutablePath,
                    ConfigurationSha256 = registration.ConfigurationSha256, SelectedChannels = channels,
                    Armed = false, ManualStopped = true, PeriodMs = 15000, StartupBudgetMs = 60000
                };

            internal IndependentExecutorRegistration ReadRegistration(string path)
            {
                var result = BoundedJson.Read<IndependentExecutorRegistration>(path);
                result.Validate();
                return result;
            }

            internal string Prepare(string project, char hash)
            {
                var next = ReadRegistration(RegistrationPath);
                next.ProjectDirectory = Path.Combine(Root, "Projects", project);
                next.DatabasePath = Path.Combine(next.ProjectDirectory, "index.db");
                next.DatabaseCreationUtcTicks = File.GetCreationTimeUtc(next.DatabasePath).Ticks;
                next.ConfigurationSha256 = new string(hash, 64);
                var path = Path.Combine(next.StateDirectory, "prepared-" + Guid.NewGuid().ToString("N") + ".json");
                BoundedJson.Write(path, next);
                return path;
            }

            internal void Change(string project, char hash = 'b', Action<string> checkpoint = null)
                => IndependentProjectBinding.CommitPrepared(RegistrationPath, Prepare(project, hash), ReadRegistration, Service, checkpoint);

            internal void Service(bool running)
            {
                Assert(Store.Read().Maintenance, "service transition occurred outside maintenance");
                ServiceTransitions.Add(running);
                if (!running) return;
                var registration = ReadRegistration(RegistrationPath);
                using (var lease = IndependentExecutorLease.TryAcquire(registration.StateDirectory, registration.InstallationId))
                    Assert(lease != null, "service enabled before exclusive maintenance lease was released");
                if (Store.Read().Intent != null) registration.RequireBoundIntent(Store.Read().Intent);
            }

            internal void AssertConsistent(string project)
            {
                var registration = ReadRegistration(RegistrationPath);
                var state = Store.Read();
                var setup = BoundedJson.Read<MtEmbTest.IndependentInstallSetup.SetupState>(Path.Combine(Root, "install-setup.json"));
                Assert(registration.ProjectDirectory == Path.Combine(Root, "Projects", project) &&
                    setup.ProjectDirectory == registration.ProjectDirectory && !state.Maintenance &&
                    state.Intent?.Armed != true && state.Ticket == null && state.Transaction == null &&
                    !File.Exists(IndependentProjectBinding.PendingPath(RegistrationPath)), "mixed or active binding after completion");
                if (state.Intent != null) registration.RequireBoundIntent(state.Intent);
                Assert(File.ReadAllText(registration.DatabasePath) == project + " historical evidence", "binding rewrote project data");
            }
        }
    }
}
