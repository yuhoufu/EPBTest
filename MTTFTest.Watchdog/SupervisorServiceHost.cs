using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.IO.Ports;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class SupervisorWindowsService : ServiceBase
    {
        private SupervisorServiceRuntime _runtime;

        internal SupervisorWindowsService()
        {
            ServiceName = "MTTFTestSupervisor";
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            _runtime = new SupervisorServiceRuntime();
            _runtime.Start();
        }

        protected override void OnStop()
        {
            _runtime?.Dispose();
            _runtime = null;
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }
    }

    internal static class SupervisorServiceHost
    {
        internal static int RunService()
        {
            ServiceBase.Run(new SupervisorWindowsService());
            return 0;
        }

        internal static int RunConsole()
        {
            using (var runtime = new SupervisorServiceRuntime())
            using (var stopped = new ManualResetEventSlim(false))
            {
                ConsoleCancelEventHandler handler = (sender, args) =>
                {
                    args.Cancel = true;
                    stopped.Set();
                };
                Console.CancelKeyPress += handler;
                try
                {
                    runtime.Start();
                    stopped.Wait();
                    return 0;
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }
        }
    }

    internal sealed class SupervisorServiceRuntime : IDisposable
    {
        private static readonly Regex SessionPattern = new Regex(
            @"(?:^|\s)--session\s+(?:\""(?<id>[0-9a-fA-F]{32})\""|(?<id>[0-9a-fA-F]{32}))(?:\s|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, SupervisorOwnedSession> _sessions =
            new ConcurrentDictionary<string, SupervisorOwnedSession>(
                StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SupervisorOwnedSafetyAgent>
            _safetyAgents =
                new ConcurrentDictionary<string, SupervisorOwnedSafetyAgent>(
                    StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task> _uiRoleMonitors =
            new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private readonly object _mainLaunchGate = new object();
        private Task _acceptLoop;
        private SupervisorP0AlarmHardwareOwner _p0AlarmOwner;
        private SupervisorRecoveryKernelService _recoveryKernel;
        private int _started;

        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Directory.CreateDirectory(StateDirectory);
            HardenStateDirectoryAcl();
            string executableDirectory;
            using (var current = Process.GetCurrentProcess())
                executableDirectory = Path.GetDirectoryName(
                    current.MainModule.FileName);
            _p0AlarmOwner = new SupervisorP0AlarmHardwareOwner(
                executableDirectory,
                StateDirectory);
            _recoveryKernel = new SupervisorRecoveryKernelService(WriteAudit);
            _recoveryKernel.Start();
            AuditIgnoredLegacySessionRecords();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
            WriteAudit(
                "SupervisorStarted",
                "Schema=" + SupervisorProtocol.SchemaVersion +
                ";Pipe=" + SupervisorProtocol.PipeName);
        }

        private static string StateDirectory => Path.Combine(
            Environment.GetFolderPath(
                Environment.UserInteractive
                    ? Environment.SpecialFolder.LocalApplicationData
                    : Environment.SpecialFolder.CommonApplicationData),
            "MTTFTest",
            Environment.UserInteractive ? "SupervisorConsole" : "Supervisor",
            "sessions");

        private static void HardenStateDirectoryAcl()
        {
            if (Environment.UserInteractive) return;
            try
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                Directory.SetAccessControl(StateDirectory, security);
            }
            catch (UnauthorizedAccessException ex) when (Environment.UserInteractive)
            {
                WriteAudit("SupervisorAclHardeningSkippedInConsole", ex.Message);
            }
        }

        private void AuditIgnoredLegacySessionRecords()
        {
            foreach (var path in Directory.GetFiles(
                         StateDirectory,
                         "session-*.launch.json",
                         SearchOption.TopDirectoryOnly))
            {
                // Do not delete the V2 evidence during migration, but never
                // turn it back into executable authority.  A formal V3
                // package can only launch EngineHost/UI role capabilities.
                WriteAudit(
                    "LegacySessionRecordIgnored",
                    Path.GetFileName(path) + ":LegacySessionHostDisabledInV3");
            }
        }

        private static void ValidateStoredRecord(SupervisorLaunchRecord record)
        {
            if (record?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !Guid.TryParseExact(record.SessionId ?? string.Empty, "N", out _) ||
                string.IsNullOrWhiteSpace(record.ExecutablePath) ||
                !IsSha256(record.ExecutableSha256) ||
                !string.Equals(
                    SupervisorProtocol.ComputeTextSha256(record.Arguments),
                    record.ArgumentsSha256,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(record.WorkingDirectory) ||
                string.IsNullOrWhiteSpace(record.MainExecutablePath) ||
                !IsSha256(record.MainExecutableSha256) ||
                record.MainProcessId <= 0 ||
                record.MainProcessStartUtcTicks <= 0 ||
                record.MainDesktopSessionId < 0 ||
                string.IsNullOrWhiteSpace(record.ProjectDirectory) ||
                string.IsNullOrWhiteSpace(record.RegistrationRunId) ||
                string.IsNullOrWhiteSpace(record.ConfigurationIdentity))
                throw new InvalidDataException("PersistedSupervisorRecordInvalid");
            var executable = Path.GetFullPath(record.ExecutablePath);
            using (var current = Process.GetCurrentProcess())
            {
                if (!string.Equals(
                        executable,
                        Path.GetFullPath(current.MainModule.FileName),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PersistedSupervisorExecutableMismatch");
            }
            if (!File.Exists(executable) ||
                !string.Equals(
                    SupervisorProtocol.ComputeSha256(executable),
                    record.ExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("PersistedSupervisorHashMismatch");
            var mainExecutable = Path.GetFullPath(record.MainExecutablePath);
            if (!File.Exists(mainExecutable) ||
                !string.Equals(
                    SupervisorProtocol.ComputeSha256(mainExecutable),
                    record.MainExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("PersistedSupervisorMainHashMismatch");
            WatchdogJournalPaths.ValidateProjectDirectory(record.ProjectDirectory);
            if (!string.Equals(
                    Path.GetFullPath(ReadLaunchArgument(
                        record.Arguments,
                        "--executable")),
                    mainExecutable,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    WatchdogJournalPaths.ValidateProjectDirectory(
                        ReadLaunchArgument(
                            record.Arguments,
                            "--journal-directory")),
                    WatchdogJournalPaths.ValidateProjectDirectory(
                        record.ProjectDirectory),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PersistedSupervisorArgumentIdentityMismatch");
            if (!int.TryParse(
                    ReadLaunchArgument(record.Arguments, "--parent-pid"),
                    out var parentPid) || parentPid != record.MainProcessId ||
                !long.TryParse(
                    ReadLaunchArgument(record.Arguments, "--parent-start-ticks"),
                    out var parentStartTicks) ||
                parentStartTicks != record.MainProcessStartUtcTicks)
                throw new InvalidDataException(
                    "PersistedSupervisorMainProcessIdentityMismatch");
            var match = SessionPattern.Match(record.Arguments ?? string.Empty);
            if (!match.Success || !string.Equals(
                    match.Groups["id"].Value,
                    record.SessionId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PersistedSupervisorSessionMismatch");
            if (!Directory.Exists(Path.GetFullPath(record.WorkingDirectory)))
                throw new InvalidDataException("PersistedSupervisorWorkingDirectoryMissing");
            // 版本和安装目录内容由操作人员负责。持久会话只校验实际进程、
            // 参数和项目身份，不再依赖包槽、DPAPI 或配置文件哈希。
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var accepted = pipe;
                    pipe = null;
                    _ = Task.Run(
                        () => HandleConnection(accepted),
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    WriteAudit("SupervisorAcceptFailed", ex.GetBaseException().Message);
                    try { await Task.Delay(1000, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private static NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
            return new NamedPipeServerStream(
                SupervisorProtocol.PipeName,
                PipeDirection.InOut,
                8,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024,
                security);
        }

        private void HandleConnection(NamedPipeServerStream pipe)
        {
            using (pipe)
            using (var reader = new BinaryReader(
                       pipe,
                       new UTF8Encoding(false),
                       true))
            using (var writer = new BinaryWriter(
                       pipe,
                       new UTF8Encoding(false),
                       true))
            {
                try
                {
                    var magic = SupervisorProtocol.ReadRequestMagic(reader);
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.MainLaunchRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorMainLaunchRequest request = null;
                        SupervisorMainLaunchResponse response;
                        try
                        {
                            request = SupervisorMainLaunchRequest.ReadBodyFrom(
                                reader,
                                magic);
                            response = LaunchMainThroughSessionAgent(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorMainLaunchResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = "SupervisorMainLaunchRejected",
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.OperatorCommandRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorOperatorCommandRequest request = null;
                        SupervisorOperatorCommandResponse response;
                        try
                        {
                            request = SupervisorOperatorCommandRequest.ReadBodyFrom(
                                reader, magic);
                            response = ApplyOperatorCommand(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorOperatorCommandResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = "SupervisorOperatorCommandRejected",
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.RequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorSessionLaunchRequest request = null;
                        SupervisorSessionLaunchResponse response;
                        try
                        {
                            request = SupervisorSessionLaunchRequest.ReadBodyFrom(
                                reader,
                                magic);
                            response = RegisterOrGetSession(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorSessionLaunchResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = "SupervisorRequestRejected",
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.SafetyHandoffBeginRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorSafetyHandoffBeginRequest request = null;
                        SupervisorSafetyHandoffBeginResponse response;
                        try
                        {
                            request = SupervisorSafetyHandoffBeginRequest.ReadBodyFrom(
                                reader,
                                magic);
                            response = BeginSafetyHandoff(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorSafetyHandoffBeginResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = SafetyFailureCode(
                                    ex,
                                    "SupervisorSafetyHandoffBeginRejected"),
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.SafetyAuthorityReadRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorSafetyAuthorityReadRequest request = null;
                        SupervisorSafetyAuthorityReadResponse response;
                        try
                        {
                            request = SupervisorSafetyAuthorityReadRequest
                                .ReadBodyFrom(reader, magic);
                            response = ReadSafetyAuthority(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorSafetyAuthorityReadResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = SafetyFailureCode(
                                    ex,
                                    "SupervisorSafetyAuthorityReadRejected"),
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.SafetyAgentRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorSafetyAgentLaunchRequest request = null;
                        SupervisorSafetyAgentLaunchResponse response;
                        try
                        {
                            request = SupervisorSafetyAgentLaunchRequest.ReadBodyFrom(
                                reader,
                                magic);
                            response = RegisterOrGetSafetyAgent(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorSafetyAgentLaunchResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = SafetyFailureCode(
                                    ex,
                                    "SupervisorSafetyRequestRejected"),
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    if (string.Equals(
                            magic,
                            SupervisorProtocol.P0AlarmRequestMagic,
                            StringComparison.Ordinal))
                    {
                        SupervisorP0AlarmRequest request = null;
                        SupervisorP0AlarmResponse response;
                        try
                        {
                            request = SupervisorP0AlarmRequest.ReadBodyFrom(
                                reader,
                                magic);
                            response = ApplyP0AlarmRequest(request);
                        }
                        catch (Exception ex)
                        {
                            response = new SupervisorP0AlarmResponse
                            {
                                RequestId = request?.RequestId ?? string.Empty,
                                ChallengeNonce = request?.ChallengeNonce ?? string.Empty,
                                Accepted = false,
                                FailureCode = "SupervisorP0AlarmRequestRejected",
                                Detail = ex.GetBaseException().Message
                            };
                        }
                        response.WriteTo(writer);
                        return;
                    }
                    throw new InvalidDataException("SupervisorRequestMagicMismatch");
                }
                catch (Exception ex)
                {
                    WriteAudit(
                        "SupervisorProtocolRejected",
                        ex.GetBaseException().Message);
                }
            }
        }

        private SupervisorMainLaunchResponse LaunchMainThroughSessionAgent(
            SupervisorMainLaunchRequest request)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorMainLaunchRequestInvalid");
            string launcherPath;
            int launcherSession;
            int targetDesktopSessionId;
            bool requesterIsInteractiveSupervisorLauncher;
            SupervisorOwnedSession ownedRequester;
            using (var requester = Process.GetProcessById(request.RequesterProcessId))
            using (var current = Process.GetCurrentProcess())
            {
                if (requester.HasExited ||
                    requester.StartTime.ToUniversalTime().Ticks !=
                        request.RequesterProcessStartUtcTicks)
                    throw new InvalidDataException(
                        "SupervisorMainLaunchRequesterIdentityMismatch");
                launcherPath = Path.GetFullPath(requester.MainModule.FileName);
                launcherSession = requester.SessionId;
                ownedRequester = _sessions.Values.FirstOrDefault(session =>
                    session.MatchesProcess(
                        request.RequesterProcessId,
                        request.RequesterProcessStartUtcTicks));
                requesterIsInteractiveSupervisorLauncher =
                    ownedRequester == null && string.Equals(
                        launcherPath,
                        Path.GetFullPath(current.MainModule.FileName),
                        StringComparison.OrdinalIgnoreCase);
                if (!requesterIsInteractiveSupervisorLauncher &&
                    ownedRequester == null)
                    throw new InvalidDataException(
                        "SupervisorMainLaunchRequesterExecutableMismatch");
            }
            if (ownedRequester == null)
            {
                if (request.IsRecoveryLaunch)
                    throw new InvalidDataException(
                        "SupervisorMainLaunchUnexpectedRecoveryBinding");
                if (launcherSession != request.DesktopSessionId)
                    throw new InvalidDataException(
                        "SupervisorMainLaunchDesktopSessionMismatch");
                targetDesktopSessionId = request.DesktopSessionId;
            }
            else
            {
                if (!request.IsRecoveryLaunch)
                    throw new InvalidDataException(
                        "SupervisorMainRecoveryBindingMissing");
                if (request.DesktopSessionId !=
                    SessionAgentProtocol.RegisteredDesktopSessionId)
                    throw new InvalidDataException(
                        "SupervisorMainRecoveryDesktopSessionMismatch");
                if (!ownedRequester.MatchesSessionId(
                        request.RecoverySessionId))
                    throw new InvalidDataException(
                        "SupervisorMainRecoverySessionMismatch");
                targetDesktopSessionId =
                    ownedRequester.RegisteredDesktopSessionId;
                if (targetDesktopSessionId < 0)
                    throw new InvalidDataException(
                        "SupervisorMainRecoveryDesktopSessionMissing");
            }

            var mainExecutable = Path.GetFullPath(request.ExecutablePath);
            var launcherDirectory = Path.GetDirectoryName(launcherPath);
            if (ownedRequester != null &&
                !ownedRequester.MatchesMainExecutable(
                    mainExecutable,
                    request.ExecutableSha256))
                throw new InvalidDataException(
                    "SupervisorMainRelaunchExecutableMismatch");
            if (!string.Equals(
                    Path.GetDirectoryName(mainExecutable),
                    launcherDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(mainExecutable),
                    "MTTFTest.exe",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "SupervisorMainLaunchExecutablePathMismatch");
            if (!File.Exists(Path.Combine(
                    launcherDirectory,
                    "MTTFTest.UnattendedMode.required")))
                throw new InvalidDataException(
                    "SupervisorMainLaunchFormalModeMarkerMissing");
            if (!string.Equals(
                    SupervisorProtocol.ComputeSha256(mainExecutable),
                    request.ExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorMainLaunchExecutableHashMismatch");

            lock (_mainLaunchGate)
            {
                var capabilityId = request.IsRecoveryLaunch
                    ? request.RecoveryCapabilityId
                    : Guid.NewGuid().ToString("N");
                var sessionId = request.IsRecoveryLaunch
                    ? request.RecoverySessionId
                    : string.Empty;
                var permitGeneration = request.IsRecoveryLaunch
                    ? request.RecoveryPermitGeneration
                    : 1;
                var permitId = request.IsRecoveryLaunch
                    ? request.RecoveryPermitId
                    : Guid.NewGuid().ToString("N");
                string engineRunId;
                long engineRunEpoch;
                if (request.IsRecoveryLaunch)
                {
                    var engine = EngineHostPipeClient.ReadSnapshot(3000);
                    if (!string.Equals(engine.SessionId, sessionId,
                            StringComparison.Ordinal) || engine.RunEpoch <= 0)
                        throw new InvalidDataException(
                            "SupervisorUiRecoveryEngineIdentityMismatch");
                    engineRunId = engine.RunId;
                    engineRunEpoch = engine.RunEpoch;
                }
                else
                {
                    EngineStateSnapshot existingEngine = null;
                    try
                    {
                        existingEngine = EngineHostPipeClient.ReadSnapshot(3000);
                    }
                    catch (Exception ex)
                    {
                        WriteAudit(
                            "SupervisorInitialUiEngineProbeUnavailable",
                            ex.GetBaseException().Message);
                    }
                    if (TryBindExistingEngineHostForUserInterface(
                            existingEngine,
                            out sessionId,
                            out engineRunId,
                            out engineRunEpoch))
                    {
                        WriteAudit(
                            "SupervisorInitialUiReusedEngineHost",
                            $"Session={sessionId};Run={engineRunId};" +
                            $"Epoch={engineRunEpoch};" +
                            $"State={existingEngine.State};" +
                            $"HardwareInitialized={existingEngine.HardwareInitialized}");
                    }
                    else
                    {
                        sessionId = Guid.NewGuid().ToString("N");
                        engineRunId = Guid.NewGuid().ToString("N");
                        engineRunEpoch = 1;
                        try
                        {
                            LaunchEngineHostThroughSessionAgent(
                                launcherDirectory,
                                targetDesktopSessionId,
                                sessionId,
                                engineRunId,
                                engineRunEpoch);
                        }
                        catch (InvalidOperationException ex) when (
                            ex.Message.IndexOf(
                                "ProcessRoleAlreadyRunning:EngineHost",
                                StringComparison.Ordinal) >= 0)
                        {
                            existingEngine = WaitForExistingEngineHostForUserInterface(5000);
                            if (!TryBindExistingEngineHostForUserInterface(
                                    existingEngine,
                                    out sessionId,
                                    out engineRunId,
                                    out engineRunEpoch))
                                throw;
                            WriteAudit(
                                "SupervisorInitialUiReusedRacingEngineHost",
                                $"Session={sessionId};Run={engineRunId};" +
                                $"Epoch={engineRunEpoch};State={existingEngine.State}");
                        }
                    }
                }
                var launchNonce = Guid.NewGuid().ToString("N");
                var uiBaseArguments = (request.Arguments ?? string.Empty) +
                                      " --engine-run " + engineRunId +
                                      " --engine-epoch " + engineRunEpoch;
                var effectiveArguments = SessionAgentLaunchClient.AppendLaunchProof(
                    uiBaseArguments,
                    capabilityId,
                    launchNonce,
                    sessionId);
                SessionLaunchCapability capability;
                using (var current = Process.GetCurrentProcess())
                {
                    capability = new SessionLaunchCapability
                    {
                        ProcessRole = ProcessRole.UserInterface,
                        CapabilityId = capabilityId,
                        SessionId = sessionId,
                        PermitGeneration = permitGeneration,
                        PermitId = permitId,
                        DesktopSessionId = targetDesktopSessionId,
                        ExecutablePath = mainExecutable,
                        ExecutableSha256 = request.ExecutableSha256,
                        Arguments = effectiveArguments,
                        ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                            effectiveArguments),
                        WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
                        LaunchNonce = launchNonce,
                        IssuedUtcTicks = DateTime.UtcNow.Ticks,
                        ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks,
                        IssuerProcessId = current.Id,
                        IssuerProcessStartUtcTicks =
                            current.StartTime.ToUniversalTime().Ticks
                    };
                }
                using (var launched = SessionAgentLaunchClient.Start(capability))
                {
                    var startTicks = launched.StartTime.ToUniversalTime().Ticks;
                    WriteAudit(
                        "SupervisorMainLaunchCapabilityConsumed",
                        $"Kind={(request.IsRecoveryLaunch ? "Recovery" : "Initial")};" +
                        $"Capability={capabilityId};Session={sessionId};" +
                        $"PermitGeneration={permitGeneration};Permit={permitId};" +
                        $"DesktopSession={targetDesktopSessionId};" +
                        (request.IsRecoveryLaunch
                            ? $"AuthorityRevision={request.RecoveryAuthorityRevision};" +
                              $"AuthoritySha256={request.RecoveryAuthoritySha256};"
                            : string.Empty) +
                        $"PID={launched.Id};StartUtcTicks={startTicks};" +
                        $"ExecutableSha256={request.ExecutableSha256}");
                    StartUserInterfaceMonitor(
                        sessionId,
                        engineRunId,
                        engineRunEpoch,
                        targetDesktopSessionId,
                        mainExecutable,
                        request.ExecutableSha256,
                        uiBaseArguments,
                        Path.GetFullPath(request.WorkingDirectory),
                        permitGeneration,
                        launched.Id,
                        startTicks);
                    return new SupervisorMainLaunchResponse
                    {
                        RequestId = request.RequestId,
                        ChallengeNonce = request.ChallengeNonce,
                        Accepted = true,
                        CapabilityId = capabilityId,
                        ProcessId = launched.Id,
                        ProcessStartUtcTicks = startTicks,
                        Detail = request.IsRecoveryLaunch
                            ? "SupervisorRecoveryCapabilitySessionAgentLaunch"
                            : "SupervisorCapabilitySessionAgentLaunch"
                    };
                }
            }
        }

        internal static bool TryBindExistingEngineHostForUserInterface(
            EngineStateSnapshot snapshot,
            out string sessionId,
            out string runId,
            out long runEpoch)
        {
            sessionId = string.Empty;
            runId = string.Empty;
            runEpoch = 0;
            if (snapshot?.IsStructurallyValid() != true)
                return false;
            sessionId = snapshot.SessionId;
            runId = snapshot.RunId;
            runEpoch = snapshot.RunEpoch;
            return true;
        }

        private static EngineStateSnapshot WaitForExistingEngineHostForUserInterface(
            int timeoutMilliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            Exception last = null;
            do
            {
                try
                {
                    return EngineHostPipeClient.ReadSnapshot(1000);
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
            } while (DateTime.UtcNow <= deadline);
            throw new InvalidOperationException(
                "ExistingEngineHostAdmissionTimedOut:" +
                (last?.GetBaseException().Message ?? "NoSnapshot"),
                last);
        }

        private void StartUserInterfaceMonitor(
            string sessionId,
            string runId,
            long runEpoch,
            int desktopSessionId,
            string executablePath,
            string executableSha256,
            string baseArguments,
            string workingDirectory,
            long permitGeneration,
            int processId,
            long processStartUtcTicks)
        {
            var task = Task.Run(() => MonitorUserInterfaceAsync(
                sessionId,
                runId,
                runEpoch,
                desktopSessionId,
                executablePath,
                executableSha256,
                baseArguments,
                workingDirectory,
                permitGeneration,
                processId,
                processStartUtcTicks,
                _stop.Token));
            _uiRoleMonitors.AddOrUpdate(sessionId, task, (key, prior) => task);
        }

        private async Task MonitorUserInterfaceAsync(
            string sessionId,
            string runId,
            long runEpoch,
            int desktopSessionId,
            string executablePath,
            string executableSha256,
            string baseArguments,
            string workingDirectory,
            long permitGeneration,
            int processId,
            long processStartUtcTicks,
            CancellationToken token)
        {
            try
            {
                var failures = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (var process = Process.GetProcessById(processId))
                        {
                            if (process.StartTime.ToUniversalTime().Ticks != processStartUtcTicks)
                                throw new InvalidDataException("UiProcessIdentityChanged");
                            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                        }
                    }
                    catch (ArgumentException) { }
                    if (token.IsCancellationRequested) return;
                    if (IsApprovedUserInterfaceExit(
                            sessionId, runId, runEpoch, processId,
                            processStartUtcTicks))
                    {
                        WriteAudit("UserInterfaceApprovedExit",
                            "Session=" + sessionId + ";PID=" + processId);
                        return;
                    }
                    failures++;
                    if (failures > 5)
                    {
                        WriteAudit("UserInterfaceRestartBudgetExhausted",
                            "Session=" + sessionId + ";Run=" + runId);
                        return;
                    }
                    var delaySeconds = 1 << (failures - 1);
                    WriteAudit("UserInterfaceAbnormalExit",
                        "Session=" + sessionId + ";PID=" + processId +
                        ";RestartAttempt=" + failures + ";DelaySeconds=" + delaySeconds +
                        ";EngineContinues=true");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token)
                        .ConfigureAwait(false);
                    try
                    {
                        var launched = LaunchUserInterfaceRole(
                            sessionId,
                            desktopSessionId,
                            executablePath,
                            executableSha256,
                            baseArguments,
                            workingDirectory,
                            permitGeneration + failures);
                        processId = launched.Item1;
                        processStartUtcTicks = launched.Item2;
                        WriteAudit("UserInterfaceRestarted",
                            "Session=" + sessionId + ";PID=" + processId +
                            ";Attempt=" + failures);
                    }
                    catch (Exception ex)
                    {
                        WriteAudit("UserInterfaceRestartFailed",
                            "Session=" + sessionId + ";Attempt=" + failures +
                            ";" + ex.GetBaseException().Message);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                WriteAudit("UserInterfaceMonitorFailed",
                    "Session=" + sessionId + ";" + ex.GetBaseException().Message);
            }
            finally
            {
                _uiRoleMonitors.TryRemove(sessionId, out _);
            }
        }

        private static bool IsApprovedUserInterfaceExit(
            string sessionId,
            string runId,
            long runEpoch,
            int processId,
            long processStartUtcTicks)
        {
            if (!UserInterfaceExitReceiptStore.TryReadExact(
                    sessionId, runId, runEpoch, processId,
                    processStartUtcTicks, out var receipt))
                return false;
            try
            {
                var engine = EngineHostPipeClient.ReadSnapshot(3000);
                return string.Equals(engine.SessionId, sessionId, StringComparison.Ordinal) &&
                       string.Equals(engine.RunId, runId, StringComparison.Ordinal) &&
                       engine.RunEpoch == runEpoch &&
                       (engine.State == SystemTerminalState.SafeIdleAlarmed ||
                        engine.State == SystemTerminalState.StoppedByOperator) &&
                       string.IsNullOrEmpty(engine.RecoveryIncidentId) &&
                       receipt.LastObservedState == engine.State;
            }
            catch { return false; }
        }

        private static Tuple<int, long> LaunchUserInterfaceRole(
            string sessionId,
            int desktopSessionId,
            string executablePath,
            string executableSha256,
            string baseArguments,
            string workingDirectory,
            long permitGeneration)
        {
            var capabilityId = Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N");
            var arguments = SessionAgentLaunchClient.AppendLaunchProof(
                baseArguments, capabilityId, nonce, sessionId);
            SessionLaunchCapability capability;
            using (var current = Process.GetCurrentProcess())
                capability = new SessionLaunchCapability
                {
                    ProcessRole = ProcessRole.UserInterface,
                    CapabilityId = capabilityId,
                    SessionId = sessionId,
                    PermitGeneration = permitGeneration,
                    PermitId = Guid.NewGuid().ToString("N"),
                    DesktopSessionId = desktopSessionId,
                    ExecutablePath = executablePath,
                    ExecutableSha256 = executableSha256,
                    Arguments = arguments,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(arguments),
                    WorkingDirectory = workingDirectory,
                    LaunchNonce = nonce,
                    IssuedUtcTicks = DateTime.UtcNow.Ticks,
                    ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks,
                    IssuerProcessId = current.Id,
                    IssuerProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks
                };
            using (var process = SessionAgentLaunchClient.Start(capability))
                return Tuple.Create(
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks);
        }

        private void LaunchEngineHostThroughSessionAgent(
            string executableDirectory,
            int desktopSessionId,
            string sessionId,
            string runId,
            long runEpoch)
        {
            var executable = Path.GetFullPath(Path.Combine(
                executableDirectory, "MTTFTest.EngineHost.exe"));
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "SupervisorEngineHostExecutableMissing", executable);
            var executableSha256 = SupervisorProtocol.ComputeSha256(executable);
            var capabilityId = Guid.NewGuid().ToString("N");
            var permitId = Guid.NewGuid().ToString("N");
            var launchNonce = Guid.NewGuid().ToString("N");
            var arguments = SessionAgentLaunchClient.AppendLaunchProof(
                "--session " + sessionId + " --run " + runId +
                " --epoch " + runEpoch,
                capabilityId,
                launchNonce,
                sessionId);
            SessionLaunchCapability capability;
            using (var current = Process.GetCurrentProcess())
            {
                capability = new SessionLaunchCapability
                {
                    ProcessRole = ProcessRole.EngineHost,
                    CapabilityId = capabilityId,
                    SessionId = sessionId,
                    PermitGeneration = 1,
                    PermitId = permitId,
                    DesktopSessionId = desktopSessionId,
                    ExecutablePath = executable,
                    ExecutableSha256 = executableSha256,
                    Arguments = arguments,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(arguments),
                    WorkingDirectory = executableDirectory,
                    LaunchNonce = launchNonce,
                    IssuedUtcTicks = DateTime.UtcNow.Ticks,
                    ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks,
                    IssuerProcessId = current.Id,
                    IssuerProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks
                };
            }
            using (var process = SessionAgentLaunchClient.Start(capability))
            {
                var processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                var deadline = DateTime.UtcNow.AddSeconds(30);
                EngineStateSnapshot snapshot = null;
                Exception last = null;
                while (DateTime.UtcNow <= deadline)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException(
                            "EngineHostExitedDuringSafeIdleAdmission:" + process.ExitCode);
                    try
                    {
                        snapshot = EngineHostPipeClient.ReadSnapshot(1000);
                        if (snapshot.IsStructurallyValid() &&
                            string.Equals(snapshot.SessionId, sessionId,
                                StringComparison.Ordinal) &&
                            string.Equals(snapshot.RunId, runId,
                                StringComparison.Ordinal) &&
                            snapshot.RunEpoch == runEpoch)
                            break;
                    }
                    catch (Exception ex) { last = ex; }
                    Thread.Sleep(100);
                }
                if (snapshot?.IsStructurallyValid() != true ||
                    !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal) ||
                    !string.Equals(snapshot.RunId, runId, StringComparison.Ordinal) ||
                    snapshot.RunEpoch != runEpoch)
                    throw new InvalidOperationException(
                        "EngineHostSafeIdleAdmissionTimedOut:" +
                        (last?.GetBaseException().Message ?? snapshot?.State.ToString() ?? "NoSnapshot"));
                WriteAudit(
                    "SupervisorEngineHostLaunchCapabilityConsumed",
                    "Capability=" + capabilityId + ";Session=" + sessionId +
                    ";Run=" + runId + ";Epoch=" + runEpoch +
                    ";HardwareInitialized=" + snapshot.HardwareInitialized +
                    ";PID=" + process.Id +
                    ";StartUtcTicks=" + processStartUtcTicks +
                    ";ExecutableSha256=" + executableSha256 +
                    ";SafeIdle=true");
            }
        }

        private SupervisorP0AlarmResponse ApplyP0AlarmRequest(
            SupervisorP0AlarmRequest request)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorP0AlarmRequestInvalid");
            var exactOwnedRequester = _sessions.Values.Any(
                session => session.MatchesProcess(
                    request.RequesterProcessId,
                    request.RequesterProcessStartUtcTicks));
            if (!exactOwnedRequester)
                throw new InvalidDataException(
                    "SupervisorP0AlarmRequesterNotOwnedSession");
            if (_p0AlarmOwner == null)
                throw new InvalidOperationException(
                    "SupervisorP0AlarmOwnerUnavailable");
            _p0AlarmOwner.Apply(request);
            WriteAudit(
                "P0AlarmRequestApplied",
                $"Action={request.Action};Event={request.EventId};" +
                $"Code={request.Code};PID={request.RequesterProcessId}");
            return new SupervisorP0AlarmResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                Detail = "DurableP0AlarmDemandAccepted"
            };
        }

        private SupervisorOperatorCommandResponse ApplyOperatorCommand(
            SupervisorOperatorCommandRequest request)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorOperatorCommandInvalid");
            if (!IsExactSessionAgentRoleProcess(
                    request.RequesterProcessId,
                    request.RequesterProcessStartUtcTicks,
                    request.Command.SessionId,
                    ProcessRole.UserInterface))
                throw new InvalidDataException(
                    "SupervisorOperatorRequesterNotAuthorizedUi");
            if (_recoveryKernel == null)
                throw new InvalidOperationException("RecoveryKernelUnavailable");
            var decision = _recoveryKernel.SubmitOperatorCommand(request.Command);
            return new SupervisorOperatorCommandResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                IncidentId = decision.Intent?.Identity?.IncidentId ?? string.Empty,
                OwnerId = decision.Intent?.OwnerId ?? string.Empty,
                DesiredState = decision.DesiredState?.State ??
                               SystemTerminalState.SafeIdleAlarmed,
                Detail = decision.Reason
            };
        }

        private static bool IsExactSessionAgentRoleProcess(
            int processId,
            long processStartUtcTicks,
            string sessionId,
            ProcessRole role)
        {
            var root = Path.GetDirectoryName(
                SessionAgentProtocol.ConsumptionPath(Guid.Empty.ToString("N")));
            if (!Directory.Exists(root)) return false;
            foreach (var path in Directory.GetFiles(
                         root, "capability-*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var record = Json.Deserialize<SessionLaunchConsumptionRecord>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (record == null ||
                        record.SchemaVersion != SessionAgentProtocol.SchemaVersion ||
                        record.ProcessRole != role ||
                        record.ProcessId != processId ||
                        record.ProcessStartUtcTicks != processStartUtcTicks ||
                        !string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(record.State, "Started", StringComparison.Ordinal))
                        continue;
                    var canonical = Json.Deserialize<SessionLaunchCapability>(
                        File.ReadAllText(path + ".capability", Encoding.UTF8));
                    var seal = Convert.FromBase64String(
                        record.CapabilitySealBase64 ?? string.Empty);
                    if (canonical?.ProcessRole != role ||
                        !SessionAgentProtocol.VerifySeal(canonical, seal))
                        continue;
                    using (var process = Process.GetProcessById(processId))
                        return !process.HasExited &&
                               process.StartTime.ToUniversalTime().Ticks ==
                               processStartUtcTicks;
                }
                catch { }
            }
            return false;
        }

        private SupervisorSafetyAgentLaunchResponse RegisterOrGetSafetyAgent(
            SupervisorSafetyAgentLaunchRequest request)
        {
            ValidateSafetyAgentRequest(
                request,
                out var receipt,
                out var projectDirectory);
            var key = request.SessionId + "-" +
                      request.PermitGeneration.ToString() + "-" +
                      request.PermitId + "-" + request.HandoffId;
            var owned = _safetyAgents.GetOrAdd(
                key,
                _ => new SupervisorOwnedSafetyAgent(key));
            var identity = owned.RegisterOrGet(
                request,
                receipt,
                projectDirectory,
                StateDirectory);
            WriteAudit(
                "SafetyAgentRegistered",
                $"Session={request.SessionId};Permit={request.PermitGeneration}/" +
                $"{request.PermitId};Handoff={request.HandoffId};" +
                $"PID={identity.ProcessId};StartUtcTicks=" +
                identity.ProcessStartUtcTicks);
            WriteProjectAudit(
                projectDirectory,
                "SafetyAgentRegistered",
                $"Session={request.SessionId};Authority={request.AuthorityId};" +
                $"PID={identity.ProcessId};StartUtcTicks={identity.ProcessStartUtcTicks}");
            return new SupervisorSafetyAgentLaunchResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                ProcessId = identity.ProcessId,
                ProcessStartUtcTicks = identity.ProcessStartUtcTicks,
                Detail = "SupervisorOwnedSafetyAgent"
            };
        }

        private SupervisorSafetyHandoffBeginResponse BeginSafetyHandoff(
            SupervisorSafetyHandoffBeginRequest request)
        {
            if (request == null)
                throw new InvalidDataException("SupervisorSafetyHandoffRequestMissing");
            if (request.SchemaVersion != SupervisorProtocol.SchemaVersion)
                throw new InvalidDataException("SupervisorSafetyHandoffSchemaMismatch");
            if (request.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorSafetyHandoffRequestInvalid");
            if (!_sessions.TryGetValue(request.SessionId, out var session) ||
                !session.MatchesProcess(
                    request.RequesterProcessId,
                    request.RequesterProcessStartUtcTicks))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffRequesterIdentityMismatch");

            WatchdogSafetyHandoffReceipt receipt;
            try
            {
                receipt = Json.Deserialize<WatchdogSafetyHandoffReceipt>(
                    request.ReceiptJson);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffReceiptReadFailed",
                    ex);
            }
            if (receipt == null)
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffReceiptReadFailed");
            if (receipt.SchemaVersion != SupervisorProtocol.SchemaVersion)
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffReceiptSchemaMismatch");
            if (!receipt.IsValidFor(request.SessionId))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffReceiptInvalid");
            if (receipt.State < WatchdogSafetyHandoffState.Accepted ||
                receipt.IsTerminal)
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffReceiptStateMismatch");
            if (!string.Equals(receipt.HandoffId, request.HandoffId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffIdMismatch");
            var projectDirectory = WatchdogJournalPaths.ValidateProjectDirectory(
                request.ProjectDirectory);
            if (!string.Equals(
                    Path.GetFullPath(receipt.ProjectDirectory),
                    Path.GetFullPath(projectDirectory),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffPathMismatch");
            if (!session.MatchesProjectRoot(projectDirectory))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffRegisteredPathMismatch");
            if (!session.MatchesMainExecutable(
                    receipt.MainExecutablePath,
                    receipt.MainExecutableSha256))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffRegisteredExecutableMismatch");
            if (!string.Equals(
                    SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt),
                    request.ReceiptCanonicalSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffCanonicalHashMismatch");
            if (receipt.CrashRecovery)
            {
                if (!session.MatchesMainProcessIdentity(
                        receipt.OldProcessId,
                        receipt.OldProcessStartUtcTicks))
                    throw new InvalidDataException(
                        "SupervisorSafetyHandoffOldProcessIdentityMismatch");
                if (!IsOldProcessExitIdentityProven(receipt))
                    throw new InvalidDataException(
                        "SupervisorSafetyHandoffOldProcessExitUnproven");
            }

            var authority = SupervisorSafetyAuthorityStore.CreateOrRead(
                StateDirectory,
                projectDirectory,
                receipt);
            WriteAudit(
                "SafetyAuthorityCommitted",
                $"Session={receipt.SessionId};Handoff={receipt.HandoffId};" +
                $"Authority={authority.AuthorityId};Revision=" +
                $"{authority.InitialReceiptRevision};CanonicalSha256=" +
                authority.InitialReceiptCanonicalSha256);
            WriteProjectAudit(
                projectDirectory,
                "SafetyAuthorityCommitted",
                $"Session={receipt.SessionId};Handoff={receipt.HandoffId};" +
                $"Authority={authority.AuthorityId};Revision=" +
                $"{authority.InitialReceiptRevision};CanonicalSha256=" +
                authority.InitialReceiptCanonicalSha256);
            CopyAuthorityEvidence(
                SupervisorSafetyAuthorityStore.GetPath(
                    StateDirectory,
                    authority.AuthorityId),
                projectDirectory,
                authority.AuthorityId);
            return new SupervisorSafetyHandoffBeginResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                AuthorityId = authority.AuthorityId,
                SessionId = authority.SessionId,
                HandoffId = authority.HandoffId,
                PermitGeneration = authority.Receipt.RelaunchPermitGeneration,
                PermitId = authority.Receipt.RelaunchPermitId,
                ReceiptRevision = authority.InitialReceiptRevision,
                ReceiptCanonicalSha256 =
                    authority.InitialReceiptCanonicalSha256,
                Detail = "SupervisorSafetyAuthorityCommitted"
            };
        }

        private SupervisorSafetyAuthorityReadResponse ReadSafetyAuthority(
            SupervisorSafetyAuthorityReadRequest request)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadRequestInvalid");
            if (!_sessions.TryGetValue(request.SessionId, out var session) ||
                !session.MatchesProcess(
                    request.RequesterProcessId,
                    request.RequesterProcessStartUtcTicks))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadRequesterIdentityMismatch");

            SupervisorSafetyAuthorityRecord authority;
            string failure;
            if (!SupervisorSafetyAuthorityStore.TryRead(
                    StateDirectory,
                    request.AuthorityId,
                    out authority,
                    out failure))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadFailed:" + failure);
            if (authority.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                authority.Receipt?.SchemaVersion != SupervisorProtocol.SchemaVersion)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadSchemaMismatch");
            if (!string.Equals(authority.AuthorityId, request.AuthorityId,
                    StringComparison.Ordinal) ||
                !string.Equals(authority.SessionId, request.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(authority.HandoffId, request.HandoffId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadHandoffIdMismatch");
            if (authority.Receipt.RelaunchPermitGeneration !=
                    request.PermitGeneration ||
                !string.Equals(authority.Receipt.RelaunchPermitId,
                    request.PermitId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadPermitMismatch");
            if (authority.InitialReceiptRevision !=
                    request.InitialReceiptRevision)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadRevisionMismatch");
            if (!string.Equals(
                    authority.InitialReceiptCanonicalSha256,
                    request.InitialReceiptCanonicalSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadCanonicalHashMismatch");
            if (!session.MatchesProjectRoot(authority.ProjectDirectory))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadPathMismatch");

            var receiptJson =
                SupervisorSafetyAuthorityStore.SerializeReceipt(
                    authority.Receipt);
            var canonical = SupervisorProtocol.ComputeTextSha256(receiptJson);
            if (!string.Equals(
                    authority.ReceiptCanonicalSha256,
                    canonical,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadCurrentHashMismatch");
            return new SupervisorSafetyAuthorityReadResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                AuthorityId = authority.AuthorityId,
                SessionId = authority.SessionId,
                HandoffId = authority.HandoffId,
                ReceiptRevision = authority.ReceiptRevision,
                ReceiptCanonicalSha256 = canonical,
                ReceiptJson = receiptJson,
                Detail = "SupervisorSafetyAuthorityRead"
            };
        }

        private static bool IsOldProcessExitIdentityProven(
            WatchdogSafetyHandoffReceipt receipt)
        {
            if (!receipt.OldProcessExitProven || receipt.OldProcessId <= 0 ||
                receipt.OldProcessStartUtcTicks <= 0 ||
                receipt.OldProcessExitEvidenceOwner !=
                    WatchdogSafetyEvidenceOwner.SupervisorService)
                return false;
            try
            {
                using (var process = Process.GetProcessById(receipt.OldProcessId))
                    return process.HasExited ||
                           process.StartTime.ToUniversalTime().Ticks !=
                               receipt.OldProcessStartUtcTicks;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static string SafetyFailureCode(
            Exception exception,
            string fallback)
        {
            var message = exception?.GetBaseException().Message ?? string.Empty;
            var separator = message.IndexOf(':');
            var code = separator < 0 ? message : message.Substring(0, separator);
            return code.StartsWith("SupervisorSafety", StringComparison.Ordinal)
                ? code
                : fallback;
        }

        private SupervisorSessionLaunchResponse RegisterOrGetSession(
            SupervisorSessionLaunchRequest request)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorRequestInvalid");
            WriteAudit(
                "LegacySessionRegistrationRejected",
                "RequestId=" + request.RequestId +
                ";Reason=LegacySessionHostDisabledInV3");
            throw new InvalidOperationException(
                "LegacySessionHostDisabledInV3");
        }

        private static void ValidateRequest(
            SupervisorSessionLaunchRequest request,
            out string sessionId,
            out string mainExecutablePath,
            out string projectDirectory,
            out string configurationIdentity)
        {
            sessionId = string.Empty;
            mainExecutablePath = string.Empty;
            projectDirectory = string.Empty;
            configurationIdentity = "UNCONTROLLED_PACKAGE";
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorRequestInvalid");
            using (var requester = Process.GetProcessById(request.RequesterProcessId))
            {
                if (requester.HasExited || requester.StartTime.ToUniversalTime().Ticks !=
                    request.RequesterProcessStartUtcTicks)
                    throw new InvalidDataException("RequesterProcessIdentityMismatch");
                mainExecutablePath = Path.GetFullPath(requester.MainModule.FileName);
            }
            var executable = Path.GetFullPath(request.ExecutablePath);
            using (var current = Process.GetCurrentProcess())
            {
                var serviceExecutable = Path.GetFullPath(current.MainModule.FileName);
                if (!string.Equals(executable, serviceExecutable,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SupervisorExecutablePathMismatch");
            }
            if (!string.Equals(
                    SupervisorProtocol.ComputeSha256(executable),
                    request.ExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("SupervisorExecutableHashMismatch");
            if (!int.TryParse(
                    ReadLaunchArgument(request.Arguments, "--parent-pid"),
                    out var parentPid) || parentPid != request.RequesterProcessId ||
                !long.TryParse(
                    ReadLaunchArgument(request.Arguments, "--parent-start-ticks"),
                    out var parentStartTicks) ||
                parentStartTicks != request.RequesterProcessStartUtcTicks)
                throw new InvalidDataException("SupervisorParentIdentityMismatch");
            var registeredMain = Path.GetFullPath(
                ReadLaunchArgument(request.Arguments, "--executable"));
            if (!string.Equals(
                    registeredMain,
                    mainExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SupervisorMainExecutableMismatch");
            projectDirectory = WatchdogJournalPaths.ValidateProjectDirectory(
                ReadLaunchArgument(request.Arguments, "--journal-directory"));
            var expectedWorkingDirectory = Path.GetDirectoryName(executable) ??
                                           string.Empty;
            if (!string.Equals(
                    Path.GetFullPath(request.WorkingDirectory),
                    Path.GetFullPath(expectedWorkingDirectory),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SupervisorWorkingDirectoryMismatch");
            // TestConfig.xml 是运行时可写的项目模板，不能同时作为不可变包身份。
            // Supervisor 不再对版本、槽描述或目录文件哈希作启动裁决。
            configurationIdentity = "OPERATOR_MANAGED";
            var match = SessionPattern.Match(request.Arguments ?? string.Empty);
            if (!match.Success ||
                !Guid.TryParseExact(match.Groups["id"].Value, "N", out var parsed))
                throw new InvalidDataException("SupervisorSessionIdMissing");
            sessionId = parsed.ToString("N");
        }

        private void ValidateSafetyAgentRequest(
            SupervisorSafetyAgentLaunchRequest request,
            out WatchdogSafetyHandoffReceipt receipt,
            out string projectDirectory)
        {
            receipt = null;
            projectDirectory = string.Empty;
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("SupervisorSafetyRequestInvalid");
            if (!_sessions.TryGetValue(request.SessionId, out var session) ||
                !session.MatchesProcess(
                    request.RequesterProcessId,
                    request.RequesterProcessStartUtcTicks))
                throw new InvalidDataException(
                    "SupervisorSafetyRequesterNotOwnedSession");

            var executable = Path.GetFullPath(request.ExecutablePath);
            string serviceDirectory;
            using (var current = Process.GetCurrentProcess())
                serviceDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(current.MainModule.FileName));
            if (!string.Equals(
                    Path.GetDirectoryName(executable),
                    serviceDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileName(executable),
                    "MTTFTest.SafetyAgent.exe",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "SupervisorSafetyExecutablePathMismatch");
            if (!File.Exists(executable) ||
                !string.Equals(
                    SupervisorProtocol.ComputeSha256(executable),
                    request.ExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyExecutableHashMismatch");
            if (!string.Equals(
                    Path.GetFullPath(request.WorkingDirectory),
                    Path.GetFullPath(serviceDirectory),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "SupervisorSafetyWorkingDirectoryMismatch");

            var argumentSession = ReadLaunchArgument(
                request.Arguments,
                "--session-id");
            var argumentHandoff = ReadLaunchArgument(
                request.Arguments,
                "--handoff-id");
            var argumentNonce = ReadLaunchArgument(
                request.Arguments,
                "--handoff-nonce");
            projectDirectory = WatchdogJournalPaths.ValidateProjectDirectory(
                ReadLaunchArgument(
                    request.Arguments,
                    "--journal-directory"));
            if (!string.Equals(argumentSession, request.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(argumentHandoff, request.HandoffId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    SupervisorProtocol.ComputeTextSha256(argumentNonce),
                    request.HandoffNonceSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyArgumentIdentityMismatch");

            SupervisorSafetyAuthorityRecord authority;
            string authorityFailure;
            if (!SupervisorSafetyAuthorityStore.TryRead(
                    StateDirectory,
                    request.AuthorityId,
                    out authority,
                    out authorityFailure))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadFailed:" + authorityFailure);
            if (authority.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                authority.Receipt?.SchemaVersion != SupervisorProtocol.SchemaVersion)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthoritySchemaMismatch");
            if (authority.Receipt.State < WatchdogSafetyHandoffState.Accepted ||
                authority.Receipt.IsTerminal)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityStateMismatch");
            if (!string.Equals(authority.SessionId, request.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(authority.HandoffId, request.HandoffId,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityHandoffIdMismatch");
            if (!string.Equals(authority.Receipt.Nonce, argumentNonce,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    SupervisorProtocol.ComputeTextSha256(argumentNonce),
                    request.HandoffNonceSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityNonceMismatch");
            if (authority.Receipt.RelaunchPermitGeneration !=
                    request.PermitGeneration ||
                !string.Equals(authority.Receipt.RelaunchPermitId,
                    request.PermitId, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityPermitMismatch");
            // The authority is bound to the project root, while the
            // SafetyAgent command line intentionally receives the
            // WatchdogSessions journal directory.  Compare their canonical
            // project roots; an exact path comparison rejects every valid
            // installed handoff.
            if (!AreEquivalentProjectRoots(
                    authority.ProjectDirectory,
                    projectDirectory) ||
                !AreEquivalentProjectRoots(
                    authority.Receipt.ProjectDirectory,
                    projectDirectory) ||
                !session.MatchesProjectRoot(projectDirectory))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityPathMismatch");
            if (!string.Equals(authority.Receipt.SafetyAgentExecutablePath,
                    executable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(authority.Receipt.SafetyAgentExecutableSha256,
                    request.ExecutableSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityExecutableHashMismatch");
            if (authority.InitialReceiptRevision !=
                request.AuthorityReceiptRevision)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityRevisionMismatch");
            if (!string.Equals(authority.InitialReceiptCanonicalSha256,
                    request.AuthorityReceiptCanonicalSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityCanonicalHashMismatch");
            receipt = authority.Receipt;

            // SafetyAgent 仍绑定当前 Supervisor 会话、进程和一次性交接凭证；
            // 不再额外绑定版本包槽，避免可写配置导致安全停机本身无法执行。
        }

        private static string ReadLaunchArgument(string arguments, string name)
        {
            var pattern = "(?:^|\\s)" + Regex.Escape(name) +
                          "\\s+(?:\"(?<quoted>[^\"]*)\"|(?<plain>\\S+))";
            var match = Regex.Match(
                arguments ?? string.Empty,
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new InvalidDataException("SupervisorArgumentMissing:" + name);
            return match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value;
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Regex.IsMatch(
                       value,
                       "^[0-9a-fA-F]{64}$",
                       RegexOptions.CultureInvariant);
        }

        internal static bool AreEquivalentProjectRoots(
            string firstDirectory,
            string secondDirectory)
        {
            return string.Equals(
                ResolveProjectRoot(firstDirectory),
                ResolveProjectRoot(secondDirectory),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveProjectRoot(string directory)
        {
            var full = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            return string.Equals(
                    Path.GetFileName(full),
                    "WatchdogSessions",
                    StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(full)?.FullName ?? full
                : full;
        }

        private static void WriteAudit(string eventType, string detail)
        {
            try
            {
                var directory = Path.GetDirectoryName(StateDirectory);
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "supervisor-audit.log"),
                    DateTime.UtcNow.ToString("O") + " " + eventType + " " +
                    (detail ?? string.Empty) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void WriteProjectAudit(
            string projectDirectory,
            string eventType,
            string detail)
        {
            try
            {
                var root = WatchdogJournalPaths.ValidateProjectDirectory(
                    projectDirectory);
                var directory = Path.Combine(root, "SupervisorEvidence");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "supervisor-audit.jsonl"),
                    Json.Serialize(new
                    {
                        SchemaVersion = SupervisorProtocol.SchemaVersion,
                        Utc = DateTime.UtcNow.ToString("O"),
                        EventType = eventType ?? string.Empty,
                        Detail = detail ?? string.Empty
                    }) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void CopyAuthorityEvidence(
            string authorityPath,
            string projectDirectory,
            string authorityId)
        {
            try
            {
                var root = WatchdogJournalPaths.ValidateProjectDirectory(
                    projectDirectory);
                var directory = Path.Combine(root, "SupervisorEvidence");
                Directory.CreateDirectory(directory);
                File.Copy(
                    authorityPath,
                    Path.Combine(
                        directory,
                        "safety-authority-" + authorityId + ".v7.json"),
                    true);
            }
            catch { }
        }

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _acceptLoop?.Wait(3000); } catch { }
            try { _recoveryKernel?.Dispose(); } catch { }
            _recoveryKernel = null;
            foreach (var session in _sessions.Values)
                try { session.Dispose(); } catch { }
            _sessions.Clear();
            foreach (var agent in _safetyAgents.Values)
                try { agent.Dispose(); } catch { }
            _safetyAgents.Clear();
            try { _p0AlarmOwner?.Dispose(); } catch { }
            _p0AlarmOwner = null;
            _stop.Dispose();
        }

        private sealed class SupervisorOwnedSession : IDisposable
        {
            private readonly object _gate = new object();
            private readonly string _sessionId;
            private readonly CancellationTokenSource _monitorStop =
                new CancellationTokenSource();
            private Process _process;
            private long _processStartUtcTicks;
            private int _mainProcessId;
            private long _mainProcessStartUtcTicks;
            private int _mainDesktopSessionId =
                SessionAgentProtocol.RegisteredDesktopSessionId;
            private string _mainExecutablePath;
            private string _mainExecutableSha256;
            private string _projectDirectory;
            private Task _monitorTask;
            private string _stateDirectory;

            internal SupervisorOwnedSession(string sessionId)
            {
                _sessionId = sessionId;
            }

            internal SupervisorProcessIdentity RegisterOrGet(
                SupervisorSessionLaunchRequest request,
                string stateDirectory,
                string mainExecutablePath,
                string projectDirectory,
                string configurationIdentity)
            {
                lock (_gate)
                {
                    var mainDesktopSessionId = GetExactProcessSessionId(
                        request.RequesterProcessId,
                        request.RequesterProcessStartUtcTicks);
                    if (IsCurrentProcessAlive())
                    {
                        if (!MatchesMainProcessIdentityUnsafe(
                                request.RequesterProcessId,
                                request.RequesterProcessStartUtcTicks))
                            throw new InvalidDataException(
                                "SupervisorOwnedSessionMainProcessIdentityMismatch");
                        if (_mainDesktopSessionId != mainDesktopSessionId)
                            throw new InvalidDataException(
                                "SupervisorOwnedSessionMainDesktopSessionMismatch");
                        EnsureMonitor(stateDirectory);
                        return CurrentIdentity();
                    }
                    TryAttachPersistedProcess(stateDirectory, request);
                    if (IsCurrentProcessAlive())
                    {
                        EnsureMonitor(stateDirectory);
                        return CurrentIdentity();
                    }

                    var record = new SupervisorLaunchRecord
                    {
                        SchemaVersion = SupervisorProtocol.SchemaVersion,
                        SessionId = _sessionId,
                        RequestId = request.RequestId,
                        ChallengeNonceSha256 =
                            SupervisorProtocol.ComputeTextSha256(
                                request.ChallengeNonce),
                        ExecutablePath = request.ExecutablePath,
                        ExecutableSha256 = request.ExecutableSha256,
                        MainExecutablePath = mainExecutablePath,
                        MainExecutableSha256 =
                            SupervisorProtocol.ComputeSha256(mainExecutablePath),
                        MainProcessId = request.RequesterProcessId,
                        MainProcessStartUtcTicks =
                            request.RequesterProcessStartUtcTicks,
                        MainDesktopSessionId = mainDesktopSessionId,
                        ProjectDirectory = projectDirectory,
                        RegistrationRunId = "PENDING_FIRST_HEARTBEAT",
                        ConfigurationIdentity = configurationIdentity,
                        Arguments = request.Arguments,
                        ArgumentsSha256 = request.ArgumentsSha256,
                        WorkingDirectory = request.WorkingDirectory,
                        State = "LaunchIntent",
                        Revision = DateTime.UtcNow.Ticks
                    };
                    WriteRecord(stateDirectory, record);
                    StartFromRecord(record, stateDirectory);
                    EnsureMonitor(stateDirectory);
                    return CurrentIdentity();
                }
            }

            internal SupervisorProcessIdentity Restore(
                SupervisorLaunchRecord record,
                string stateDirectory)
            {
                lock (_gate)
                {
                    if (!string.Equals(
                            record?.SessionId,
                            _sessionId,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "PersistedSupervisorOwnedSessionMismatch");
                    TryAttachRecordProcess(record);
                    if (!IsCurrentProcessAlive())
                        StartFromRecord(record, stateDirectory);
                    EnsureMonitor(stateDirectory);
                    return CurrentIdentity();
                }
            }

            private void EnsureMonitor(string stateDirectory)
            {
                _stateDirectory = stateDirectory;
                if (_monitorTask != null) return;
                _monitorTask = Task.Run(
                    () => MonitorLoopAsync(_monitorStop.Token),
                    CancellationToken.None);
            }

            private async Task MonitorLoopAsync(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        lock (_gate)
                        {
                            if (cancellationToken.IsCancellationRequested ||
                                IsCurrentProcessAlive())
                                continue;
                            try { _process?.Dispose(); } catch { }
                            _process = null;
                            _processStartUtcTicks = 0;
                            var path = RecordPath(_stateDirectory, _sessionId);
                            var record = Json.Deserialize<SupervisorLaunchRecord>(
                                File.ReadAllText(path, Encoding.UTF8));
                            ValidateStoredRecord(record);
                            StartFromRecord(record, _stateDirectory);
                            WriteAudit(
                                "SessionHostRestarted",
                                $"Session={_sessionId};PID={_process.Id};" +
                                $"StartUtcTicks={_processStartUtcTicks}");
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteAudit(
                            "SessionHostRestartFailed",
                            $"Session={_sessionId};" +
                            ex.GetBaseException().Message);
                        try
                        {
                            await Task.Delay(5000, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }

            private void TryAttachPersistedProcess(
                string stateDirectory,
                SupervisorSessionLaunchRequest request)
            {
                try
                {
                    var path = RecordPath(stateDirectory, _sessionId);
                    if (!File.Exists(path)) return;
                    var record = Json.Deserialize<SupervisorLaunchRecord>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (record?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                        !string.Equals(record.SessionId, _sessionId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(record.ExecutableSha256,
                            request.ExecutableSha256, StringComparison.Ordinal) ||
                        !string.Equals(record.ArgumentsSha256,
                            request.ArgumentsSha256, StringComparison.Ordinal) ||
                        record.MainDesktopSessionId < 0 ||
                        record.ProcessId <= 0 || record.ProcessStartUtcTicks <= 0)
                        return;
                    TryAttachRecordProcess(record);
                }
                catch { }
            }

            private void TryAttachRecordProcess(SupervisorLaunchRecord record)
            {
                if (record == null || record.ProcessId <= 0 ||
                    record.ProcessStartUtcTicks <= 0 ||
                    record.MainDesktopSessionId < 0)
                    return;
                var process = Process.GetProcessById(record.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks !=
                    record.ProcessStartUtcTicks)
                {
                    process.Dispose();
                    return;
                }
                _process = process;
                _processStartUtcTicks = record.ProcessStartUtcTicks;
                _mainProcessId = record.MainProcessId;
                _mainProcessStartUtcTicks = record.MainProcessStartUtcTicks;
                _mainDesktopSessionId = record.MainDesktopSessionId;
                _mainExecutablePath = record.MainExecutablePath;
                _mainExecutableSha256 = record.MainExecutableSha256;
                _projectDirectory = record.ProjectDirectory;
            }

            private void StartFromRecord(
                SupervisorLaunchRecord record,
                string stateDirectory)
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = record.ExecutablePath,
                    Arguments = "--session-host " + record.Arguments,
                    WorkingDirectory = record.WorkingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process == null)
                    throw new InvalidOperationException(
                        "SupervisorSessionHostStartReturnedNull");
                _process = process;
                _processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                _mainProcessId = record.MainProcessId;
                _mainProcessStartUtcTicks = record.MainProcessStartUtcTicks;
                _mainDesktopSessionId = record.MainDesktopSessionId;
                _mainExecutablePath = record.MainExecutablePath;
                _mainExecutableSha256 = record.MainExecutableSha256;
                _projectDirectory = record.ProjectDirectory;
                record.State = "Started";
                record.ProcessId = process.Id;
                record.ProcessStartUtcTicks = _processStartUtcTicks;
                record.Revision = Math.Max(
                    record.Revision + 1,
                    DateTime.UtcNow.Ticks);
                WriteRecord(stateDirectory, record);
            }

            private bool IsCurrentProcessAlive()
            {
                try
                {
                    return _process != null && !_process.HasExited &&
                           _process.StartTime.ToUniversalTime().Ticks ==
                               _processStartUtcTicks;
                }
                catch { return false; }
            }

            internal bool MatchesProcess(int processId, long startUtcTicks)
            {
                lock (_gate)
                {
                    return processId > 0 && startUtcTicks > 0 &&
                           IsCurrentProcessAlive() &&
                           _process.Id == processId &&
                           _processStartUtcTicks == startUtcTicks;
                }
            }

            internal bool MatchesSessionId(string sessionId)
            {
                return string.Equals(
                    _sessionId,
                    sessionId,
                    StringComparison.OrdinalIgnoreCase);
            }

            internal int RegisteredDesktopSessionId
            {
                get
                {
                    lock (_gate)
                        return _mainDesktopSessionId;
                }
            }

            internal bool MatchesMainProcessIdentity(
                int processId,
                long startUtcTicks)
            {
                lock (_gate)
                    return MatchesMainProcessIdentityUnsafe(
                        processId,
                        startUtcTicks);
            }

            private bool MatchesMainProcessIdentityUnsafe(
                int processId,
                long startUtcTicks)
            {
                return SupervisorOriginalProcessIdentityPolicy.Matches(
                    _mainProcessId,
                    _mainProcessStartUtcTicks,
                    processId,
                    startUtcTicks);
            }

            private static int GetExactProcessSessionId(
                int processId,
                long processStartUtcTicks)
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (process.HasExited ||
                        process.StartTime.ToUniversalTime().Ticks !=
                            processStartUtcTicks)
                        throw new InvalidDataException(
                            "SupervisorMainDesktopSessionIdentityMismatch");
                    return process.SessionId;
                }
            }

            internal bool MatchesProjectRoot(string projectDirectory)
            {
                lock (_gate)
                {
                    if (string.IsNullOrWhiteSpace(_projectDirectory) ||
                        string.IsNullOrWhiteSpace(projectDirectory))
                        return false;
                    return string.Equals(
                        ResolveProjectRoot(_projectDirectory),
                        ResolveProjectRoot(projectDirectory),
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            internal bool MatchesMainExecutable(
                string executablePath,
                string executableSha256)
            {
                lock (_gate)
                {
                    return !string.IsNullOrWhiteSpace(_mainExecutablePath) &&
                           !string.IsNullOrWhiteSpace(_mainExecutableSha256) &&
                           string.Equals(
                               Path.GetFullPath(_mainExecutablePath),
                               Path.GetFullPath(executablePath ?? string.Empty),
                               StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(
                               _mainExecutableSha256,
                               executableSha256,
                               StringComparison.Ordinal);
                }
            }

            private static string ResolveProjectRoot(string directory)
            {
                var full = WatchdogJournalPaths.ValidateProjectDirectory(directory);
                return string.Equals(
                        Path.GetFileName(full),
                        "WatchdogSessions",
                        StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(full)?.FullName ?? full
                    : full;
            }

            private SupervisorProcessIdentity CurrentIdentity()
            {
                return new SupervisorProcessIdentity
                {
                    ProcessId = _process.Id,
                    ProcessStartUtcTicks = _processStartUtcTicks
                };
            }

            private static void WriteRecord(
                string stateDirectory,
                SupervisorLaunchRecord record)
            {
                Directory.CreateDirectory(stateDirectory);
                var path = RecordPath(stateDirectory, record.SessionId);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(
                        temporary,
                        Json.Serialize(record),
                        new UTF8Encoding(false));
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.Read))
                        stream.Flush(true);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch { }
                }
            }

            private static string RecordPath(string directory, string sessionId)
            {
                return Path.Combine(
                    directory,
                    "session-" + WatchdogJournalPaths.SafeName(sessionId) +
                    ".launch.json");
            }

            public void Dispose()
            {
                try { _monitorStop.Cancel(); } catch { }
                try { _monitorTask?.Wait(3000); } catch { }
                lock (_gate)
                {
                    try { _process?.Dispose(); } catch { }
                    _process = null;
                }
                _monitorStop.Dispose();
            }
        }

        private sealed class SupervisorProcessIdentity
        {
            internal int ProcessId;
            internal long ProcessStartUtcTicks;
        }

        /// <summary>
        /// The only formal-mode owner of the global P0 beacon/buzzer demand.
        /// The demand is durable before serial I/O and is replayed after a
        /// service restart.  Hardware delivery is retried without weakening
        /// any recovery gate; clear/mute are distinct operations.
        /// </summary>
        private sealed class SupervisorP0AlarmHardwareOwner : IDisposable
        {
            private readonly object _gate = new object();
            private readonly string _statePath;
            private readonly string _auditPath;
            private readonly P0AlarmHardwareConfiguration _configuration;
            private readonly AutoResetEvent _changed = new AutoResetEvent(false);
            private readonly CancellationTokenSource _stop =
                new CancellationTokenSource();
            private readonly Task _worker;
            private SupervisorP0AlarmState _state;

            internal SupervisorP0AlarmHardwareOwner(
                string executableDirectory,
                string stateDirectory)
            {
                Directory.CreateDirectory(stateDirectory);
                _statePath = Path.Combine(
                    stateDirectory,
                    "p0-alarm-state.v5.json");
                _auditPath = Path.Combine(
                    stateDirectory,
                    "p0-alarm-hardware-audit.log");
                var formal = File.Exists(Path.Combine(
                    executableDirectory ?? string.Empty,
                    "MTTFTest.UnattendedMode.required"));
                var configPath = WatchdogRuntimeConfigPaths.GetPath(
                    executableDirectory,
                    "AlarmConfig.xml");
                _configuration = File.Exists(configPath)
                    ? P0AlarmHardwareConfiguration.Load(configPath)
                    : formal
                        ? throw new FileNotFoundException(
                            "FormalP0AlarmConfigMissing",
                            configPath)
                        : null;
                _state = ReadState() ?? new SupervisorP0AlarmState
                {
                    SchemaVersion = SupervisorProtocol.SchemaVersion,
                    Revision = 1,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };
                _worker = Task.Run(() => WorkerLoop(_stop.Token));
                _changed.Set();
            }

            internal void Apply(SupervisorP0AlarmRequest request)
            {
                lock (_gate)
                {
                    switch (request.Action)
                    {
                        case SupervisorP0AlarmAction.Latch:
                            _state.Latched = true;
                            _state.BuzzerMuted = false;
                            _state.EventId = request.EventId;
                            _state.Code = request.Code;
                            _state.Detail = request.Detail;
                            break;
                        case SupervisorP0AlarmAction.ClearTransient:
                            _state.Latched = false;
                            _state.BuzzerMuted = false;
                            _state.EventId = request.EventId;
                            _state.Code = request.Code;
                            _state.Detail = request.Detail;
                            break;
                        case SupervisorP0AlarmAction.MuteBuzzer:
                            if (_state.Latched) _state.BuzzerMuted = true;
                            _state.EventId = request.EventId;
                            _state.Code = request.Code;
                            _state.Detail = request.Detail;
                            break;
                        default:
                            throw new InvalidDataException(
                                "SupervisorP0AlarmActionInvalid");
                    }
                    _state.SchemaVersion = SupervisorProtocol.SchemaVersion;
                    _state.Revision = Math.Max(
                        _state.Revision + 1,
                        DateTime.UtcNow.Ticks);
                    _state.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    WriteState(_state);
                }
                _changed.Set();
            }

            private void WorkerLoop(CancellationToken cancellationToken)
            {
                long appliedRevision = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    SupervisorP0AlarmState snapshot;
                    lock (_gate)
                        snapshot = _state.Clone();
                    var shouldDrive = snapshot.Revision != appliedRevision ||
                                      snapshot.Latched;
                    if (shouldDrive)
                    {
                        try
                        {
                            if (_configuration == null)
                                throw new InvalidOperationException(
                                    "P0AlarmHardwareOwnerEngineeringModeDisabled");
                            _configuration.Drive(
                                snapshot.Latched,
                                snapshot.BuzzerMuted);
                            appliedRevision = snapshot.Revision;
                            AppendAudit(
                                "P0AlarmHardwareApplied",
                                snapshot,
                                string.Empty);
                        }
                        catch (Exception ex)
                        {
                            AppendAudit(
                                "P0AlarmHardwareRetryPending",
                                snapshot,
                                ex.GetBaseException().Message);
                        }
                    }
                    var waitMs = snapshot.Latched ? 30000 : Timeout.Infinite;
                    WaitHandle.WaitAny(
                        new[] { _changed, cancellationToken.WaitHandle },
                        waitMs);
                }
            }

            private SupervisorP0AlarmState ReadState()
            {
                try
                {
                    if (!File.Exists(_statePath)) return null;
                    var value = Json.Deserialize<SupervisorP0AlarmState>(
                        File.ReadAllText(_statePath, Encoding.UTF8));
                    return value?.SchemaVersion == SupervisorProtocol.SchemaVersion &&
                           value.Revision > 0
                        ? value
                        : null;
                }
                catch
                {
                    return null;
                }
            }

            private void WriteState(SupervisorP0AlarmState value)
            {
                var temporary = _statePath + ".tmp-" +
                                Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(
                        temporary,
                        Json.Serialize(value),
                        new UTF8Encoding(false));
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.Read))
                        stream.Flush(true);
                    if (File.Exists(_statePath))
                        File.Replace(temporary, _statePath, null);
                    else
                        File.Move(temporary, _statePath);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch { }
                }
            }

            private void AppendAudit(
                string eventType,
                SupervisorP0AlarmState state,
                string detail)
            {
                try
                {
                    File.AppendAllText(
                        _auditPath,
                        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                        " " + eventType + " Latched=" + state.Latched +
                        ";Muted=" + state.BuzzerMuted +
                        ";Revision=" + state.Revision +
                        ";Code=" + (state.Code ?? string.Empty) +
                        ";Detail=" + (detail ?? string.Empty) +
                        Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { }
            }

            public void Dispose()
            {
                try { _stop.Cancel(); } catch { }
                _changed.Set();
                try { _worker?.Wait(3000); } catch { }
                _changed.Dispose();
                _stop.Dispose();
            }
        }

        private sealed class SupervisorP0AlarmState
        {
            public int SchemaVersion { get; set; }
            public bool Latched { get; set; }
            public bool BuzzerMuted { get; set; }
            public string EventId { get; set; }
            public string Code { get; set; }
            public string Detail { get; set; }
            public long Revision { get; set; }
            public long UpdatedUtcTicks { get; set; }

            internal SupervisorP0AlarmState Clone()
            {
                return new SupervisorP0AlarmState
                {
                    SchemaVersion = SchemaVersion,
                    Latched = Latched,
                    BuzzerMuted = BuzzerMuted,
                    EventId = EventId,
                    Code = Code,
                    Detail = Detail,
                    Revision = Revision,
                    UpdatedUtcTicks = UpdatedUtcTicks
                };
            }
        }

        private sealed class P0AlarmHardwareConfiguration
        {
            private string PortName { get; set; }
            private int Baud { get; set; }
            private int DataBits { get; set; }
            private Parity Parity { get; set; }
            private StopBits StopBits { get; set; }
            private int TimeoutMs { get; set; }
            private int Retry { get; set; }
            private List<P0AlarmCommand> AllOff { get; set; }
            private List<P0AlarmCommand> LightsOn { get; set; }
            private P0AlarmCommand BuzzerOn { get; set; }
            private P0AlarmCommand BuzzerOff { get; set; }

            internal static P0AlarmHardwareConfiguration Load(string path)
            {
                var root = XDocument.Load(path).Root ??
                           throw new InvalidDataException("AlarmConfigRootMissing");
                var serial = root.Element("Serial") ??
                             throw new InvalidDataException("AlarmSerialMissing");
                var mappings = root.Element("Mappings") ??
                               throw new InvalidDataException("AlarmMappingsMissing");
                var commands = root.Element("Commands") ??
                               throw new InvalidDataException("AlarmCommandsMissing");
                var single = commands.Element("SingleCoil")?.Elements("Cmd")
                    .Select(element => new
                    {
                        Device = (int?)element.Attribute("DeviceId") ?? -1,
                        Line = (int?)element.Attribute("Line") ?? -1,
                        On = (string)element.Attribute("OnHex") ?? string.Empty,
                        Off = (string)element.Attribute("OffHex") ?? string.Empty
                    })
                    .ToDictionary(
                        item => item.Device.ToString(CultureInfo.InvariantCulture) +
                                ":" + item.Line.ToString(CultureInfo.InvariantCulture),
                        item => item,
                        StringComparer.Ordinal) ??
                    throw new InvalidDataException("AlarmSingleCoilMissing");
                string Key(XElement element) =>
                    ((int?)element.Attribute("DeviceId") ?? -1)
                        .ToString(CultureInfo.InvariantCulture) + ":" +
                    ((int?)element.Attribute("Line") ?? -1)
                        .ToString(CultureInfo.InvariantCulture);
                var lightCommands = new List<P0AlarmCommand>();
                foreach (var mapping in mappings.Elements("Epb"))
                {
                    if (!single.TryGetValue(Key(mapping), out var command))
                        throw new InvalidDataException(
                            "AlarmLightCommandMissing:" + Key(mapping));
                    lightCommands.Add(new P0AlarmCommand(command.On, true));
                }
                var buzzer = mappings.Element("Buzzer") ??
                             throw new InvalidDataException("AlarmBuzzerMappingMissing");
                if (!single.TryGetValue(Key(buzzer), out var buzzerCommand))
                    throw new InvalidDataException("AlarmBuzzerCommandMissing");
                var behavior = root.Element("Behavior");
                Enum.TryParse((string)serial.Attribute("Parity") ?? "None",
                    true, out Parity parity);
                var stopText = (string)serial.Attribute("StopBits") ?? "1";
                var stopBits = stopText == "1" ? StopBits.One :
                    stopText == "2" ? StopBits.Two : StopBits.One;
                return new P0AlarmHardwareConfiguration
                {
                    PortName = (string)serial.Attribute("Port") ?? string.Empty,
                    Baud = (int?)serial.Attribute("Baud") ?? 115200,
                    DataBits = (int?)serial.Attribute("DataBits") ?? 8,
                    Parity = parity,
                    StopBits = stopBits,
                    TimeoutMs = (int?)behavior?.Attribute("TimeoutMs") ?? 200,
                    Retry = (int?)behavior?.Attribute("Retry") ?? 2,
                    AllOff = commands.Element("AllOff")?.Elements("Device")
                        .Select(element => new P0AlarmCommand(
                            (string)element.Attribute("Hex") ?? string.Empty,
                            (bool?)element.Attribute("ExpectResponse") ?? true))
                        .ToList() ?? new List<P0AlarmCommand>(),
                    LightsOn = lightCommands,
                    BuzzerOn = new P0AlarmCommand(buzzerCommand.On, true),
                    BuzzerOff = new P0AlarmCommand(buzzerCommand.Off, true)
                };
            }

            internal void Drive(bool latched, bool buzzerMuted)
            {
                if (string.IsNullOrWhiteSpace(PortName))
                    throw new InvalidDataException("AlarmSerialPortMissing");
                using (var port = new SerialPort(
                           PortName,
                           Baud,
                           Parity,
                           DataBits,
                           StopBits)
                {
                    ReadTimeout = Math.Max(50, TimeoutMs),
                    WriteTimeout = Math.Max(50, TimeoutMs)
                })
                {
                    port.Open();
                    if (!latched)
                    {
                        foreach (var command in AllOff) Send(port, command);
                        return;
                    }
                    foreach (var command in LightsOn) Send(port, command);
                    Send(port, buzzerMuted ? BuzzerOff : BuzzerOn);
                }
            }

            private void Send(SerialPort port, P0AlarmCommand command)
            {
                var bytes = ParseHex(command.Hex);
                Exception last = null;
                for (var attempt = 0; attempt <= Math.Max(0, Retry); attempt++)
                {
                    try
                    {
                        try { port.DiscardInBuffer(); } catch { }
                        port.Write(bytes, 0, bytes.Length);
                        if (command.ExpectResponse)
                        {
                            try
                            {
                                var buffer = new byte[256];
                                port.Read(buffer, 0, buffer.Length);
                            }
                            catch (System.TimeoutException) { }
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Thread.Sleep(30);
                    }
                }
                throw new IOException(
                    "P0AlarmSerialCommandFailed",
                    last);
            }

            private static byte[] ParseHex(string hex)
            {
                var parts = (hex ?? string.Empty).Split(
                    new[] { ' ', '\t', '\r', '\n', '-' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    throw new InvalidDataException("P0AlarmCommandEmpty");
                return parts.Select(value => byte.Parse(
                        value,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture))
                    .ToArray();
            }
        }

        private sealed class P0AlarmCommand
        {
            internal P0AlarmCommand(string hex, bool expectResponse)
            {
                Hex = hex;
                ExpectResponse = expectResponse;
            }

            internal string Hex { get; }
            internal bool ExpectResponse { get; }
        }

        private sealed class SupervisorOwnedSafetyAgent : IDisposable
        {
            private readonly object _gate = new object();
            private readonly string _key;
            private Process _process;
            private long _processStartUtcTicks;

            internal SupervisorOwnedSafetyAgent(string key)
            {
                _key = key;
            }

            internal SupervisorProcessIdentity RegisterOrGet(
                SupervisorSafetyAgentLaunchRequest request,
                WatchdogSafetyHandoffReceipt receipt,
                string projectDirectory,
                string stateDirectory)
            {
                lock (_gate)
                {
                    if (IsAlive()) return CurrentIdentity();
                    TryAttachPersisted(request, stateDirectory);
                    if (IsAlive()) return CurrentIdentity();

                    var authorityPath = SupervisorSafetyAuthorityStore.GetPath(
                        stateDirectory,
                        request.AuthorityId);
                    var authoritativeArguments =
                        (request.Arguments ?? string.Empty) +
                        " --authority-id " + QuoteArgument(request.AuthorityId) +
                        " --authority-receipt " + QuoteArgument(authorityPath) +
                        " --authority-revision " +
                        request.AuthorityReceiptRevision.ToString(
                            CultureInfo.InvariantCulture) +
                        " --authority-sha256 " +
                        QuoteArgument(request.AuthorityReceiptCanonicalSha256);
                    var record = new SupervisorSafetyAgentRecord
                    {
                        SchemaVersion = SupervisorProtocol.SchemaVersion,
                        Key = _key,
                        SessionId = request.SessionId,
                        PermitGeneration = request.PermitGeneration,
                        PermitId = request.PermitId,
                        HandoffId = request.HandoffId,
                        HandoffNonceSha256 = request.HandoffNonceSha256,
                        ExecutablePath = request.ExecutablePath,
                        ExecutableSha256 = request.ExecutableSha256,
                        AuthorityId = request.AuthorityId,
                        AuthorityReceiptRevision =
                            request.AuthorityReceiptRevision,
                        AuthorityReceiptCanonicalSha256 =
                            request.AuthorityReceiptCanonicalSha256,
                        BaseArgumentsSha256 = request.ArgumentsSha256,
                        Arguments = authoritativeArguments,
                        ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                            authoritativeArguments),
                        WorkingDirectory = request.WorkingDirectory,
                        ProjectDirectory = projectDirectory,
                        ReceiptRevisionAtLaunch = receipt.Revision,
                        State = "LaunchIntent",
                        Revision = DateTime.UtcNow.Ticks
                    };
                    WriteSafetyRecord(stateDirectory, record);
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = record.ExecutablePath,
                        Arguments = record.Arguments,
                        WorkingDirectory = record.WorkingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (process == null)
                        throw new InvalidOperationException(
                            "SupervisorSafetyAgentStartReturnedNull");
                    _process = process;
                    _processStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks;
                    record.ProcessId = process.Id;
                    record.ProcessStartUtcTicks = _processStartUtcTicks;
                    record.State = "Started";
                    record.Revision = Math.Max(
                        record.Revision + 1,
                        DateTime.UtcNow.Ticks);
                    WriteSafetyRecord(stateDirectory, record);
                    return CurrentIdentity();
                }
            }

            private void TryAttachPersisted(
                SupervisorSafetyAgentLaunchRequest request,
                string stateDirectory)
            {
                try
                {
                    var path = SafetyRecordPath(stateDirectory, _key);
                    if (!File.Exists(path)) return;
                    var record = Json.Deserialize<SupervisorSafetyAgentRecord>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (record?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                        !string.Equals(record.Key, _key, StringComparison.Ordinal) ||
                        !string.Equals(record.SessionId, request.SessionId,
                            StringComparison.Ordinal) ||
                        record.PermitGeneration != request.PermitGeneration ||
                        !string.Equals(record.PermitId, request.PermitId,
                            StringComparison.Ordinal) ||
                        !string.Equals(record.HandoffId, request.HandoffId,
                            StringComparison.Ordinal) ||
                        !string.Equals(record.HandoffNonceSha256,
                            request.HandoffNonceSha256, StringComparison.Ordinal) ||
                        !string.Equals(record.ExecutableSha256,
                            request.ExecutableSha256, StringComparison.Ordinal) ||
                        !string.Equals(record.AuthorityId,
                            request.AuthorityId, StringComparison.Ordinal) ||
                        record.AuthorityReceiptRevision !=
                            request.AuthorityReceiptRevision ||
                        !string.Equals(record.AuthorityReceiptCanonicalSha256,
                            request.AuthorityReceiptCanonicalSha256,
                            StringComparison.Ordinal) ||
                        !string.Equals(record.BaseArgumentsSha256,
                            request.ArgumentsSha256, StringComparison.Ordinal) ||
                        record.ProcessId <= 0 || record.ProcessStartUtcTicks <= 0)
                        return;
                    var process = Process.GetProcessById(record.ProcessId);
                    if (process.HasExited ||
                        process.StartTime.ToUniversalTime().Ticks !=
                            record.ProcessStartUtcTicks)
                    {
                        process.Dispose();
                        return;
                    }
                    _process = process;
                    _processStartUtcTicks = record.ProcessStartUtcTicks;
                }
                catch { }
            }

            private bool IsAlive()
            {
                try
                {
                    return _process != null && !_process.HasExited &&
                           _process.StartTime.ToUniversalTime().Ticks ==
                               _processStartUtcTicks;
                }
                catch { return false; }
            }

            private SupervisorProcessIdentity CurrentIdentity()
            {
                return new SupervisorProcessIdentity
                {
                    ProcessId = _process.Id,
                    ProcessStartUtcTicks = _processStartUtcTicks
                };
            }

            private static void WriteSafetyRecord(
                string stateDirectory,
                SupervisorSafetyAgentRecord record)
            {
                var path = SafetyRecordPath(stateDirectory, record.Key);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(
                        temporary,
                        Json.Serialize(record),
                        new UTF8Encoding(false));
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.Read))
                        stream.Flush(true);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch { }
                }
            }

            private static string SafetyRecordPath(
                string stateDirectory,
                string key)
            {
                var safe = SupervisorProtocol.ComputeTextSha256(key)
                    .Substring(0, 32);
                return Path.Combine(
                    stateDirectory,
                    "safety-" + safe + ".launch.json");
            }

            private static string QuoteArgument(string value)
            {
                return "\"" + (value ?? string.Empty)
                    .Replace("\"", "\\\"") + "\"";
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    try { _process?.Dispose(); } catch { }
                    _process = null;
                }
            }
        }

        private sealed class SupervisorLaunchRecord
        {
            public int SchemaVersion { get; set; }
            public string SessionId { get; set; }
            public string RequestId { get; set; }
            public string ChallengeNonceSha256 { get; set; }
            public string ExecutablePath { get; set; }
            public string ExecutableSha256 { get; set; }
            public string MainExecutablePath { get; set; }
            public string MainExecutableSha256 { get; set; }
            public int MainProcessId { get; set; }
            public long MainProcessStartUtcTicks { get; set; }
            public int MainDesktopSessionId { get; set; } =
                SessionAgentProtocol.RegisteredDesktopSessionId;
            public string ProjectDirectory { get; set; }
            public string RegistrationRunId { get; set; }
            public string ConfigurationIdentity { get; set; }
            public string Arguments { get; set; }
            public string ArgumentsSha256 { get; set; }
            public string WorkingDirectory { get; set; }
            public string State { get; set; }
            public int ProcessId { get; set; }
            public long ProcessStartUtcTicks { get; set; }
            public long Revision { get; set; }
        }

        private sealed class SupervisorSafetyAgentRecord
        {
            public int SchemaVersion { get; set; }
            public string Key { get; set; }
            public string SessionId { get; set; }
            public long PermitGeneration { get; set; }
            public string PermitId { get; set; }
            public string HandoffId { get; set; }
            public string HandoffNonceSha256 { get; set; }
            public string AuthorityId { get; set; }
            public long AuthorityReceiptRevision { get; set; }
            public string AuthorityReceiptCanonicalSha256 { get; set; }
            public string ExecutablePath { get; set; }
            public string ExecutableSha256 { get; set; }
            public string BaseArgumentsSha256 { get; set; }
            public string Arguments { get; set; }
            public string ArgumentsSha256 { get; set; }
            public string WorkingDirectory { get; set; }
            public string ProjectDirectory { get; set; }
            public long ReceiptRevisionAtLaunch { get; set; }
            public string State { get; set; }
            public int ProcessId { get; set; }
            public long ProcessStartUtcTicks { get; set; }
            public long Revision { get; set; }
        }
    }
}
