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
        private Task _acceptLoop;
        private SupervisorP0AlarmHardwareOwner _p0AlarmOwner;
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
            RestorePersistedSessions();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
            WriteAudit("SupervisorStarted", "Schema=5;Pipe=" + SupervisorProtocol.PipeName);
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

        private void RestorePersistedSessions()
        {
            foreach (var path in Directory.GetFiles(
                         StateDirectory,
                         "session-*.launch.json",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var record = Json.Deserialize<SupervisorLaunchRecord>(
                        File.ReadAllText(path, Encoding.UTF8));
                    ValidateStoredRecord(record);
                    var owned = _sessions.GetOrAdd(
                        record.SessionId,
                        id => new SupervisorOwnedSession(id));
                    var identity = owned.Restore(record, StateDirectory);
                    WriteAudit(
                        "SessionRestored",
                        $"Session={record.SessionId};PID={identity.ProcessId};" +
                        $"StartUtcTicks={identity.ProcessStartUtcTicks}");
                }
                catch (Exception ex)
                {
                    WriteAudit(
                        "SessionRestoreRejected",
                        Path.GetFileName(path) + ":" + ex.GetBaseException().Message);
                }
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
            var match = SessionPattern.Match(record.Arguments ?? string.Empty);
            if (!match.Success || !string.Equals(
                    match.Groups["id"].Value,
                    record.SessionId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PersistedSupervisorSessionMismatch");
            if (!Directory.Exists(Path.GetFullPath(record.WorkingDirectory)))
                throw new InvalidDataException("PersistedSupervisorWorkingDirectoryMissing");
            var formalMarker = Path.Combine(
                Path.GetDirectoryName(executable) ?? string.Empty,
                "MTTFTest.UnattendedMode.required");
            if (File.Exists(formalMarker))
            {
                if (!PackageSlotDescriptorStore.TryValidateSlot(
                        Path.GetDirectoryName(executable),
                        "Current",
                        out var slot,
                        out var slotReason) ||
                    !string.Equals(
                        slot.ConfigSha256,
                        record.ConfigurationIdentity,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "PersistedSupervisorPackageIdentityMismatch:" + slotReason);
            }
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
                                FailureCode = "SupervisorSafetyRequestRejected",
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

        private SupervisorSessionLaunchResponse RegisterOrGetSession(
            SupervisorSessionLaunchRequest request)
        {
            ValidateRequest(
                request,
                out var sessionId,
                out var mainExecutablePath,
                out var projectDirectory,
                out var configurationIdentity);
            var owned = _sessions.GetOrAdd(
                sessionId,
                _ => new SupervisorOwnedSession(sessionId));
            var identity = owned.RegisterOrGet(
                request,
                StateDirectory,
                mainExecutablePath,
                projectDirectory,
                configurationIdentity);
            WriteAudit(
                "SessionRegistered",
                $"Session={sessionId};PID={identity.ProcessId};" +
                $"StartUtcTicks={identity.ProcessStartUtcTicks};" +
                $"RequestId={request.RequestId}");
            return new SupervisorSessionLaunchResponse
            {
                RequestId = request.RequestId,
                ChallengeNonce = request.ChallengeNonce,
                Accepted = true,
                ProcessId = identity.ProcessId,
                ProcessStartUtcTicks = identity.ProcessStartUtcTicks,
                Detail = "SupervisorOwnedSessionHost"
            };
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
            var formalMarker = Path.Combine(
                Path.GetDirectoryName(executable) ?? string.Empty,
                "MTTFTest.UnattendedMode.required");
            if (File.Exists(formalMarker))
            {
                PackageSlotDescriptor slot;
                string slotReason;
                if (!PackageSlotDescriptorStore.TryValidateSlot(
                        Path.GetDirectoryName(executable),
                        "Current",
                        out slot,
                        out slotReason))
                    throw new InvalidDataException(
                        "SupervisorPackageSlotUnproven:" + slotReason);
                configurationIdentity = slot.ConfigSha256;
            }
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

            if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                    projectDirectory,
                    request.SessionId,
                    out receipt) ||
                receipt.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                receipt.State < WatchdogSafetyHandoffState.Accepted ||
                receipt.IsTerminal ||
                !string.Equals(receipt.HandoffId, request.HandoffId,
                    StringComparison.Ordinal) ||
                !string.Equals(receipt.Nonce, argumentNonce,
                    StringComparison.Ordinal) ||
                receipt.RelaunchPermitGeneration != request.PermitGeneration ||
                !string.Equals(receipt.RelaunchPermitId, request.PermitId,
                    StringComparison.Ordinal) ||
                !string.Equals(receipt.SafetyAgentExecutablePath, executable,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.SafetyAgentExecutableSha256,
                    request.ExecutableSha256, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffIdentityMismatch");

            var formalMarker = Path.Combine(
                serviceDirectory,
                "MTTFTest.UnattendedMode.required");
            if (File.Exists(formalMarker) &&
                !PackageSlotDescriptorStore.TryValidateSlot(
                    serviceDirectory,
                    "Current",
                    out _,
                    out var slotReason))
                throw new InvalidDataException(
                    "SupervisorSafetyPackageSlotUnproven:" + slotReason);
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

        public void Dispose()
        {
            try { _stop.Cancel(); } catch { }
            try { _acceptLoop?.Wait(3000); } catch { }
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
                    if (IsCurrentProcessAlive())
                    {
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
                        SchemaVersion = 5,
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
                    if (record?.SchemaVersion != 5 ||
                        !string.Equals(record.SessionId, _sessionId,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(record.ExecutableSha256,
                            request.ExecutableSha256, StringComparison.Ordinal) ||
                        !string.Equals(record.ArgumentsSha256,
                            request.ArgumentsSha256, StringComparison.Ordinal) ||
                        record.ProcessId <= 0 || record.ProcessStartUtcTicks <= 0)
                        return;
                    TryAttachRecordProcess(record);
                }
                catch { }
            }

            private void TryAttachRecordProcess(SupervisorLaunchRecord record)
            {
                if (record == null || record.ProcessId <= 0 ||
                    record.ProcessStartUtcTicks <= 0)
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
                var configPath = Path.Combine(
                    executableDirectory ?? string.Empty,
                    "Config",
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
                        Arguments = request.Arguments,
                        ArgumentsSha256 = request.ArgumentsSha256,
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
                        !string.Equals(record.ArgumentsSha256,
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
            public string ExecutablePath { get; set; }
            public string ExecutableSha256 { get; set; }
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
