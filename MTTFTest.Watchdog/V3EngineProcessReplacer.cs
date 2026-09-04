using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Exact EngineHost process replacement owned by the LocalSystem
    /// Supervisor.  It is called only after Recovery Kernel has persisted an
    /// intent and an independent safety proof has completed.
    /// </summary>
    internal sealed class V3EngineProcessReplacer
    {
        private sealed class LaunchBase
        {
            internal Process LiveProcess { get; set; }
            internal int DesktopSessionId { get; set; }
            internal string ExecutablePath { get; set; }
            internal int OldProcessId { get; set; }
            internal long OldProcessStartUtcTicks { get; set; }
        }

        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer();
        private readonly Action<string, string> _audit;
        private readonly Func<RecoveryCommand, bool> _isCurrent;

        internal V3EngineProcessReplacer(Action<string, string> audit, Func<RecoveryCommand, bool> isCurrent = null)
        {
            _audit = audit ?? ((eventType, detail) => { });
            _isCurrent = isCurrent;
        }

        internal RecoveryCommandReceipt Replace(
            RecoveryCommand command,
            bool activateLastKnownGood)
        {
            var receipt = new RecoveryCommandReceipt
            {
                CommandId = command?.CommandId ?? string.Empty,
                IdempotencyKey = command?.IdempotencyKey ?? string.Empty,
                CompletedUtcTicks = DateTime.UtcNow.Ticks
            };
            try
            {
                if (command?.IsStructurallyValid() != true)
                    throw new InvalidDataException("EngineReplacementCommandInvalid");
                var projectSwitch = command.Kind == RecoveryCommandKind.ActivateProjectSwitch;
                if (projectSwitch && activateLastKnownGood) throw new InvalidOperationException("ProjectSwitchCannotImplicitlyChangePackage");
                EnsureCurrent(command);
                EngineStateSnapshot snapshot = null;
                var observedProcessId = 0; long observedStart = 0;
                if (projectSwitch)
                {
                    try { snapshot = ReadProjectSnapshot(command, out observedProcessId, out observedStart); }
                    catch { if (!ProveNoLiveEngineHost()) throw; }
                    if (snapshot != null && !ProjectEngineHandoffPolicy.IsSource(command, snapshot) && !ProjectEngineHandoffPolicy.IsDestination(command, snapshot))
                        throw new InvalidDataException("ProjectReplacementSourceOrDestinationInvalid");
                }
                else
                {
                    try { snapshot = EngineHostPipeClient.ReadSnapshot(3000); }
                    catch { }
                }
                if (!projectSwitch && snapshot != null &&
                    (!string.Equals(snapshot.SessionId,
                         command.Identity.SessionId, StringComparison.Ordinal) ||
                     !string.Equals(snapshot.RunId,
                         command.Identity.RunId, StringComparison.Ordinal) ||
                     snapshot.RunEpoch != command.Identity.RunEpoch))
                    throw new InvalidDataException("EngineReplacementIdentityMismatch");

                var launchBase = ResolveLaunchBase(command.Identity.SessionId);
                try
                {
                    var oldProcess = launchBase.LiveProcess;
                    var oldProcessId = launchBase.OldProcessId;
                    var oldStartTicks = launchBase.OldProcessStartUtcTicks;
                    if (projectSwitch && oldProcess != null &&
                        (snapshot == null || oldProcessId != observedProcessId || oldStartTicks != observedStart))
                        throw new InvalidDataException("ProjectReplacementExactProcessMismatch");
                    if (projectSwitch && ProjectEngineHandoffPolicy.IsDestination(command, snapshot))
                    {
                        EnsureCurrent(command);
                        return ProjectEngineHandoffPolicy.DestinationReceipt(command, snapshot);
                    }
                    var desktopSessionId = launchBase.DesktopSessionId;
                    var currentExecutable = launchBase.ExecutablePath;
                    var executable = currentExecutable;
                    var slotReason = "CurrentPackage";
                    if (activateLastKnownGood &&
                        !PackageSlotDescriptorStore.TryActivateLastKnownGood(
                            currentExecutable, out executable, out slotReason))
                        throw new InvalidDataException(slotReason);
                    if (!File.Exists(executable))
                        throw new FileNotFoundException(
                            "EngineReplacementExecutableMissing", executable);

                    if (oldProcess != null)
                    {
                        EnsureCurrent(command);
                        oldProcess.Kill();
                        if (!oldProcess.WaitForExit(30000))
                            throw new TimeoutException("OldEngineHostExitTimeout");
                        try
                        {
                            using (var check = Process.GetProcessById(oldProcessId))
                                if (!check.HasExited &&
                                    check.StartTime.ToUniversalTime().Ticks == oldStartTicks)
                                    throw new InvalidOperationException(
                                        "OldEngineHostIdentityStillAlive");
                        }
                        catch (ArgumentException) { }
                    }

                    var capabilityId = RecoveryProtocolV7.NewId();
                    var nonce = RecoveryProtocolV7.NewId();
                    var baseArguments = "--session " + command.Identity.SessionId +
                                        " --run " + command.Identity.RunId +
                                        " --epoch " + command.Identity.RunEpoch;
                    if (projectSwitch) baseArguments += EngineProjectActivation.FromCommand(command).ToArguments();
                    var arguments = SessionAgentLaunchClient.AppendLaunchProof(
                        baseArguments, capabilityId, nonce,
                        command.Identity.SessionId);
                    SessionLaunchCapability capability;
                    using (var supervisor = Process.GetCurrentProcess())
                    {
                        capability = new SessionLaunchCapability
                        {
                            ProcessRole = ProcessRole.EngineHost,
                            CapabilityId = capabilityId,
                            SessionId = command.Identity.SessionId,
                            PermitGeneration = Math.Max(
                                1, command.CommandSequence),
                            PermitId = RecoveryProtocolV7.NewId(),
                            DesktopSessionId = desktopSessionId,
                            ExecutablePath = executable,
                            ExecutableSha256 = SupervisorProtocol.ComputeSha256(
                                executable),
                            Arguments = arguments,
                            ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                                arguments),
                            WorkingDirectory = Path.GetDirectoryName(executable) ??
                                               Environment.CurrentDirectory,
                            LaunchNonce = nonce,
                            IssuedUtcTicks = DateTime.UtcNow.Ticks,
                            ExpiresUtcTicks = Math.Min(DateTime.UtcNow.AddSeconds(60).Ticks, command.DeadlineUtcTicks),
                            IssuerProcessId = supervisor.Id,
                            IssuerProcessStartUtcTicks =
                                supervisor.StartTime.ToUniversalTime().Ticks
                        };
                    }
                    EnsureCurrent(command);
                    using (var replacement = SessionAgentLaunchClient.Start(capability))
                    {
                        var deadline = new DateTime(Math.Min(command.DeadlineUtcTicks,
                            DateTime.UtcNow.AddSeconds(projectSwitch ? 180 : 30).Ticks), DateTimeKind.Utc);
                        Exception last = null;
                        EngineStateSnapshot admitted = null;
                        while (DateTime.UtcNow <= deadline)
                        {
                            EnsureCurrent(command);
                            if (replacement.HasExited)
                                throw new InvalidOperationException(
                                    "ReplacementEngineHostExited:" +
                                    replacement.ExitCode);
                            try
                            {
                                if (projectSwitch)
                                {
                                    admitted = ReadProjectSnapshot(command, out var replacementId, out var replacementStarted);
                                    if (replacementId == replacement.Id && replacementStarted == replacement.StartTime.ToUniversalTime().Ticks &&
                                        ProjectEngineHandoffPolicy.IsDestination(command, admitted)) break;
                                    admitted = null;
                                    Thread.Sleep(100);
                                    continue;
                                }
                                admitted = EngineHostPipeClient.ReadSnapshot(1000);
                                if (admitted.HardwareInitialized &&
                                    (snapshot == null ||
                                     !string.Equals(admitted.EngineInstanceId,
                                         snapshot.EngineInstanceId,
                                         StringComparison.Ordinal)) &&
                                    string.Equals(admitted.SessionId,
                                        command.Identity.SessionId,
                                        StringComparison.Ordinal) &&
                                    string.Equals(admitted.RunId,
                                        command.Identity.RunId,
                                        StringComparison.Ordinal) &&
                                    admitted.RunEpoch == command.Identity.RunEpoch &&
                                    admitted.State == SystemTerminalState.SafeIdleAlarmed)
                                    break;
                            }
                            catch (Exception ex) { last = ex; }
                            Thread.Sleep(100);
                        }
                        if (projectSwitch)
                        {
                            EnsureCurrent(command);
                            receipt = ProjectEngineHandoffPolicy.DestinationReceipt(command, admitted);
                            _audit("ProjectEngineHostActivated", "Command=" + command.ProjectSwitch.OperatorCommandId + ";PID=" + replacement.Id);
                            return receipt;
                        }
                        if (admitted?.HardwareInitialized != true ||
                            (snapshot != null &&
                             string.Equals(admitted.EngineInstanceId,
                                 snapshot.EngineInstanceId,
                                 StringComparison.Ordinal)))
                            throw new TimeoutException(
                                "ReplacementEngineHostAdmissionTimeout:" +
                                (last?.GetBaseException().Message ?? "NoSnapshot"));
                        receipt.Succeeded = true;
                        receipt.OutputsOff = true;
                        receipt.PressureSafe = true;
                        receipt.DataBoundaryClosed = true;
                        receipt.ExecutionAuthorizationRevoked = true;
                        receipt.Detail =
                            (activateLastKnownGood
                                ? "LastKnownGoodEngineHostAdmitted:"
                                : "CurrentEngineHostReplaced:") +
                            slotReason + ";OldPID=" + oldProcessId +
                            ";NewPID=" + replacement.Id +
                            ";Instance=" + admitted.EngineInstanceId;
                        _audit("EngineHostReplacementCompleted", receipt.Detail);
                    }
                }
                finally
                {
                    try { launchBase.LiveProcess?.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                receipt.Succeeded = false;
                receipt.Detail = "EngineHostReplacementFailed:" +
                                 ex.GetBaseException().Message;
                _audit("EngineHostReplacementFailed", receipt.Detail);
            }
            receipt.CompletedUtcTicks = DateTime.UtcNow.Ticks;
            return receipt;
        }

        private void EnsureCurrent(RecoveryCommand command)
        {
            if (command.Kind == RecoveryCommandKind.ActivateProjectSwitch && _isCurrent == null ||
                _isCurrent != null && !_isCurrent(command) || command.DeadlineUtcTicks <= DateTime.UtcNow.Ticks)
                throw new InvalidOperationException("EngineReplacementCommandSupersededOrExpired");
        }

        private static EngineStateSnapshot ReadProjectSnapshot(RecoveryCommand command, out int processId, out long startTicks) =>
            EngineHostPipeClient.ReadBoundSnapshot(command.Identity.SessionId,
                (id, start) => SupervisorServiceRuntime.IsExactSessionAgentRoleProcess(id, start, command.Identity.SessionId, ProcessRole.EngineHost),
                out processId, out startTicks);

        internal static bool ProveNoLiveEngineHost()
        {
            var processes = Process.GetProcessesByName("MTTFTest.EngineHost");
            try { return processes.All(process => !IsExactLiveProcess(process)); }
            finally
            {
                foreach (var process in processes)
                    try { process.Dispose(); } catch { }
            }
        }

        private static LaunchBase ResolveLaunchBase(string sessionId)
        {
            var live = Process.GetProcessesByName("MTTFTest.EngineHost")
                .Where(IsExactLiveProcess)
                .ToArray();
            if (live.Length > 1)
            {
                foreach (var process in live) process.Dispose();
                throw new InvalidDataException(
                    "EngineReplacementProcessCardinality=" + live.Length);
            }
            if (live.Length == 1)
            {
                var process = live[0];
                return new LaunchBase
                {
                    LiveProcess = process,
                    DesktopSessionId = process.SessionId,
                    ExecutablePath = Path.GetFullPath(
                        process.MainModule.FileName),
                    OldProcessId = process.Id,
                    OldProcessStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks
                };
            }

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest", "SessionAgent", "capabilities");
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    "EngineLaunchCapabilityDirectoryMissing");
            foreach (var path in Directory.GetFiles(
                         root, "capability-*.json.capability", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var capability = Json.Deserialize<SessionLaunchCapability>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (capability?.IsStructurallyValid() != true ||
                        capability.ProcessRole != ProcessRole.EngineHost ||
                        !string.Equals(capability.SessionId, sessionId,
                            StringComparison.Ordinal) ||
                        !File.Exists(capability.ExecutablePath))
                        continue;
                    var recordPath = path.Substring(
                        0, path.Length - ".capability".Length);
                    var record = Json.Deserialize<SessionLaunchConsumptionRecord>(
                        File.ReadAllText(recordPath, Encoding.UTF8));
                    if (record == null ||
                        !string.Equals(record.State, "Started",
                            StringComparison.Ordinal) ||
                        !string.Equals(record.CapabilityId,
                            capability.CapabilityId, StringComparison.Ordinal) ||
                        !SessionAgentProtocol.VerifySeal(
                            capability,
                            Convert.FromBase64String(
                                record.CapabilitySealBase64 ?? string.Empty)))
                        continue;
                    return new LaunchBase
                    {
                        DesktopSessionId = capability.DesktopSessionId,
                        ExecutablePath = Path.GetFullPath(
                            capability.ExecutablePath),
                        OldProcessId = record.ProcessId,
                        OldProcessStartUtcTicks = record.ProcessStartUtcTicks
                    };
                }
                catch { }
            }
            throw new InvalidDataException(
                "DurableEngineLaunchCapabilityUnavailable");
        }

        private static bool IsExactLiveProcess(Process process)
        {
            try
            {
                return process != null && !process.HasExited &&
                       string.Equals(
                           Path.GetFileName(process.MainModule.FileName),
                           "MTTFTest.EngineHost.exe",
                           StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                try { process?.Dispose(); } catch { }
                return false;
            }
        }
    }
}
