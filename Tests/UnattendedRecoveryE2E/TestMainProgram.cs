using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.UnattendedRecoveryTestMain
{
    internal static class TestMainProgram
    {
        private static long _snapshotRevision;
        private static long _controlProgress;
        private static bool _recovery;
        private static string _runId;
        private static string _sessionId;
        private static string _eventPath;

        [STAThread]
        private static int Main(string[] args)
        {
            string launchFailure;
            if (!MtEmbTest.LaunchCapabilityGate.TryValidate(args, out launchFailure))
            {
                AppendFallback("LaunchRejected", launchFailure);
                return 64;
            }

            WatchdogClientTransportEngine engine = null;
            try
            {
                var intent = RecoveryIntent.Parse(args);
                _recovery = intent != null;
                _sessionId = _recovery
                    ? intent.SessionId
                    : Read(args, SessionAgentProtocol.SessionArgument);
                if (!Guid.TryParseExact(_sessionId, "N", out _))
                    throw new InvalidDataException("E2ESessionIdInvalid");

                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var root = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTest",
                    "UnattendedRecoveryE2E");
                var project = Path.Combine(root, "Project");
                var journal = Path.Combine(project, "WatchdogSessions");
                Directory.CreateDirectory(journal);
                _eventPath = Path.Combine(project, "e2e-main-events.log");
                _runId = Guid.NewGuid().ToString("N");

                var mainPath = Path.GetFullPath(
                    Process.GetCurrentProcess().MainModule.FileName);
                var sidecarPath = Path.Combine(
                    baseDirectory,
                    "MTTFTest.Watchdog.exe");
                var pipeName = _recovery
                    ? intent.PipeName
                    : "MTTFTest.Watchdog." + _sessionId;
                var generation = 1L;

                if (!_recovery)
                {
                    using (var current = Process.GetCurrentProcess())
                    {
                        var bootstrap =
                            DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                                journal,
                                _sessionId,
                                current.Id,
                                current.StartTime.ToUniversalTime().Ticks);
                        if (bootstrap?.Succeeded != true)
                            throw new InvalidOperationException(
                                "E2EStrictAuthorityBootstrapFailed:" +
                                (bootstrap?.Reason ?? "Unavailable"));
                    }
                }

                var options = new WatchdogClientTransportOptions
                {
                    SessionId = _sessionId,
                    PipeName = pipeName,
                    MainExecutablePath = mainPath,
                    SidecarExecutablePath = sidecarPath,
                    JournalDirectory = journal,
                    JournalPolicy = new WatchdogJournalPolicy(),
                    SelectedChannels = new[] { 4 },
                    RecoveryProcess = _recovery,
                    RecoveryAttempt = _recovery ? intent.RecoveryAttempt : 0,
                    SessionGeneration = generation,
                    LaunchPolicy = _recovery
                        ? WatchdogLaunchPolicy.AttachExistingAuthorityOnly
                        : WatchdogLaunchPolicy.LaunchIfPipeUnavailable,
                    AuthoritySeed = _recovery
                        ? new SidecarIdentityStateMachine.AuthoritySnapshot(
                            intent.SidecarProcessId,
                            intent.SidecarProcessStartUtcTicks,
                            _sessionId,
                            intent.SidecarInstanceNonce)
                        : null
                };
                var callbacks = CreateCallbacks(
                    options,
                    intent,
                    generation);
                engine = new WatchdogClientTransportEngine(
                    new SupervisorSidecarProcessLauncher(),
                    new SystemNamedPipeClientFactory());
                engine.BeginSession(options, callbacks);
                var lease = engine.CaptureSnapshot().SessionLease;
                if (lease <= 0)
                    throw new InvalidOperationException("E2ESessionLeaseMissing");
                if (!_recovery)
                    CreateCrashRecoverySeed(
                        project,
                        journal,
                        baseDirectory,
                        mainPath,
                        _sessionId,
                        generation,
                        lease);
                var attach = engine.StartAsync();
                if (!attach.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("E2EWatchdogAttachTimeout");
                var attached = engine.CaptureSnapshot();
                if (!attached.IsAttached)
                    throw new InvalidOperationException(
                        "E2EWatchdogAttachFailed:" +
                        attached.TransportFailureDetail);

                AppendEvent(
                    _recovery ? "RecoveryMainAttached" : "InitialMainAttached",
                    "Session=" + _sessionId + ";Lease=" + lease);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var form = new Form
                {
                    Text = "MTTFTest V2.15 Unattended Recovery E2E Host",
                    Width = 520,
                    Height = 160,
                    StartPosition = FormStartPosition.CenterScreen
                })
                {
                    form.Shown += (sender, eventArgs) =>
                    {
                        engine.TrySendForSession(
                            new WatchdogMessage
                            {
                                Type = WatchdogMessageType.MainUiReady,
                                Reason = "E2ETestHostWindowShown"
                            },
                            _sessionId,
                            generation,
                            lease);
                        AppendEvent("MainUiReady", string.Empty);
                    };
                    Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                AppendEvent("MainFailed", ex.GetBaseException().Message);
                return 2;
            }
            finally
            {
                try { engine?.Dispose(); } catch { }
            }
        }

        private static WatchdogClientTransportCallbacks CreateCallbacks(
            WatchdogClientTransportOptions options,
            RecoveryIntent intent,
            long generation)
        {
            return new WatchdogClientTransportCallbacks
            {
                CreateRunSession = (recovery, attempt) =>
                {
                    using (var process = Process.GetCurrentProcess())
                        return new WatchdogRunSession
                        {
                            SessionId = _sessionId,
                            PipeName = options.PipeName,
                            ExecutablePath = options.MainExecutablePath,
                            ProcessId = process.Id,
                            ProcessStartUtcTicks =
                                process.StartTime.ToUniversalTime().Ticks,
                            RunId = _runId,
                            RunEpoch = 1,
                            RecoveryProcess = _recovery,
                            RecoveryAttempt = _recovery
                                ? Math.Max(1, intent.RecoveryAttempt)
                                : 0,
                            RelaunchGeneration = _recovery
                                ? intent.RelaunchPermitGeneration
                                : 0,
                            RelaunchPermitId = _recovery
                                ? intent.RelaunchPermitId
                                : string.Empty,
                            RelaunchPermitNonce = _recovery
                                ? intent.RelaunchPermitNonce
                                : string.Empty,
                            SelectedChannels = new[] { 4 }
                        };
                },
                CaptureHeartbeat = CaptureHeartbeat,
                RecordEvent = (type, detail) => AppendEvent(type, detail),
                TransportError = (type, detail) =>
                    AppendEvent("TransportError:" + type, detail),
                TransportLost = (type, detail) =>
                    AppendEvent("TransportLost:" + type, detail),
                StopAllRequested = (reason, correlation) =>
                    AppendEvent("StopAllRequested", reason + ";" + correlation),
                ObserveDurableStopMarker = () => false
            };
        }

        private static WatchdogHeartbeat CaptureHeartbeat()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var revision = Interlocked.Increment(ref _snapshotRevision);
                var progress = Interlocked.Increment(ref _controlProgress);
                return new WatchdogHeartbeat
                {
                    PulseSequence = revision,
                    SnapshotRevision = revision,
                    SnapshotCapturedUtcTicks = DateTime.UtcNow.Ticks,
                    SnapshotAgeMs = 0,
                    UiLifecycle = "Visible",
                    ControlProgressVersion = progress,
                    SessionId = _sessionId,
                    ProcessId = process.Id,
                    ProcessStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks,
                    RunId = _runId,
                    RunEpoch = 1,
                    Phase = _recovery ? "RecoveryFirstCycle" : "FormalRun",
                    RunActive = true,
                    ActiveCycleCount = 1,
                    EnabledChannels = new[] { 4 },
                    EligibleChannels = new[] { 4 },
                    RecoveryEligibleChannels = new[] { 4 },
                    RecoveryActive = _recovery,
                    RecoveryStage = _recovery
                        ? "FirstCycleCommitted"
                        : string.Empty,
                    RecoveryProgressVersion = progress,
                    RecoveryBatchCommitGeneration = _recovery ? 1 : 0,
                    RecoveryProcessSource = _recovery
                        ? RecoveryFailurePolicy.RecoveryProcessSource
                        : RecoveryFailurePolicy.InitialProcessSource
                };
            }
        }

        private static void CreateCrashRecoverySeed(
            string project,
            string journal,
            string baseDirectory,
            string mainPath,
            string sessionId,
            long generation,
            long lease)
        {
            var agentPath = Path.Combine(
                baseDirectory,
                "MTTFTest.SafetyAgent.exe");
            var runtime = new SafetyRuntimeSnapshot
            {
                SampleRateHz = 2000,
                SamplesPerChannel = 20,
                PressureChannels = new[] { "Pressure_E2E" },
                ReleaseSafePressureBar = new[] { 5.0 },
                PressureSampleMaxAgeMs = 250,
                ReleaseStableMs = 100,
                ReleaseTimeoutMs = 2000
            };
            runtime.Validate();
            var seedId = Guid.NewGuid().ToString("N");
            var snapshot = WatchdogSafetyConfigSnapshotStore.Create(
                journal,
                seedId,
                Path.Combine(baseDirectory, "Config"),
                Path.Combine(project, "Config"),
                "V2.15.0.0-E2E-TestHost",
                runtime,
                sessionId,
                generation,
                lease,
                0,
                string.Empty,
                SupervisorProtocol.ComputeSha256(mainPath),
                SupervisorProtocol.ComputeSha256(agentPath));
            if (snapshot?.Succeeded != true)
                throw new InvalidOperationException(
                    "E2ESafetySnapshotFailed:" +
                    (snapshot?.Error ?? "Unavailable"));
            WatchdogCrashRecoverySeedStore.WriteThrough(
                journal,
                new WatchdogCrashRecoverySeed
                {
                    SessionId = sessionId,
                    SessionGeneration = generation,
                    SessionLease = lease,
                    SeedId = seedId,
                    Revision = 1,
                    ProjectDirectory = project,
                    MainExecutablePath = mainPath,
                    MainExecutableSha256 =
                        SupervisorProtocol.ComputeSha256(mainPath),
                    SafetyAgentExecutablePath = agentPath,
                    SafetyAgentExecutableSha256 =
                        SupervisorProtocol.ComputeSha256(agentPath),
                    ConfigSnapshotPath = snapshot.ConfigDirectory,
                    ConfigSnapshotManifestPath = snapshot.ManifestPath,
                    ConfigSnapshotManifestSha256 = snapshot.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 2,
                    BuildIdentity = "V2.15.0.0-E2E-TestHost"
                });
            AppendEvent("CrashRecoverySeedCommitted", "Seed=" + seedId);
        }

        private static string Read(string[] args, string name)
        {
            for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                if (string.Equals(args[i], name,
                        StringComparison.OrdinalIgnoreCase))
                    return args[i + 1] ?? string.Empty;
            return string.Empty;
        }

        private static void AppendEvent(string type, string detail)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_eventPath))
                {
                    AppendFallback(type, detail);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_eventPath));
                File.AppendAllText(
                    _eventPath,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                    "|" + type + "|PID=" + Process.GetCurrentProcess().Id +
                    "|Recovery=" + _recovery + "|" +
                    (detail ?? string.Empty) + Environment.NewLine);
            }
            catch { }
        }

        private static void AppendFallback(string type, string detail)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTest",
                    "UnattendedRecoveryE2E");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "e2e-main-bootstrap.log"),
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                    "|" + type + "|" + (detail ?? string.Empty) +
                    Environment.NewLine);
            }
            catch { }
        }

        private sealed class RecoveryIntent
        {
            internal string SessionId;
            internal string PipeName;
            internal int RecoveryAttempt;
            internal int SidecarProcessId;
            internal long SidecarProcessStartUtcTicks;
            internal string SidecarInstanceNonce;
            internal long RelaunchPermitGeneration;
            internal string RelaunchPermitId;
            internal string RelaunchPermitNonce;

            internal static RecoveryIntent Parse(string[] args)
            {
                var session = Read(args, "--watchdog-recover");
                if (string.IsNullOrWhiteSpace(session)) return null;
                int.TryParse(Read(args, "--recovery-attempt"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var attempt);
                int.TryParse(Read(args, "--sidecar-pid"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var sidecarPid);
                long.TryParse(Read(args, "--sidecar-start-ticks"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var sidecarStart);
                long.TryParse(Read(args, "--relaunch-generation"),
                    NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var permitGeneration);
                return new RecoveryIntent
                {
                    SessionId = session,
                    PipeName = Read(args, "--watchdog-pipe"),
                    RecoveryAttempt = Math.Max(1, attempt),
                    SidecarProcessId = sidecarPid,
                    SidecarProcessStartUtcTicks = sidecarStart,
                    SidecarInstanceNonce = Read(args, "--sidecar-instance-nonce"),
                    RelaunchPermitGeneration = permitGeneration,
                    RelaunchPermitId = Read(args, "--relaunch-permit-id"),
                    RelaunchPermitNonce = Read(args, "--relaunch-permit-nonce")
                };
            }
        }
    }
}
