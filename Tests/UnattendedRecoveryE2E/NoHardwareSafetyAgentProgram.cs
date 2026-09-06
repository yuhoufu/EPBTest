using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.UnattendedRecoveryNoHardwareSafetyAgent
{
    internal static class NoHardwareSafetyAgentProgram
    {
        private static int Main(string[] args)
        {
            try
            {
                var authorityId = Read(args, "--authority-id");
                var authorityPath = Path.GetFullPath(
                    Read(args, "--authority-receipt"));
                if (!long.TryParse(
                        Read(args, "--authority-revision"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var initialRevision))
                    throw new InvalidDataException("E2EAuthorityRevisionInvalid");
                var initialSha = Read(args, "--authority-sha256");
                var stateDirectory = Path.GetDirectoryName(authorityPath);
                if (!string.Equals(
                        SupervisorSafetyAuthorityStore.GetPath(
                            stateDirectory,
                            authorityId),
                        authorityPath,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("E2EAuthorityPathMismatch");

                SupervisorSafetyAuthorityRecord authority;
                string failure;
                if (!SupervisorSafetyAuthorityStore.TryRead(
                        stateDirectory,
                        authorityId,
                        out authority,
                        out failure))
                    throw new InvalidDataException(
                        "E2EAuthorityReadFailed:" + failure);
                Append(authority.Receipt.ProjectDirectory,
                    "SafetyAgentStarted", authorityId);

                if (authority.Receipt.Stage <
                    WatchdogSafetyStage.DoOffConfirmed)
                {
                    var motorOff = authority.Receipt;
                    motorOff.Revision++;
                    motorOff.State = WatchdogSafetyHandoffState.WorkerStarted;
                    motorOff.Stage = WatchdogSafetyStage.DoOffConfirmed;
                    motorOff.MotorsOff = true;
                    motorOff.StageUtcTicks = DateTime.UtcNow.Ticks;
                    motorOff.UpdatedUtcTicks = motorOff.StageUtcTicks;
                    motorOff.Detail = "E2ENoHardwareMotorOffConfirmed";
                    authority = SupervisorSafetyAuthorityStore.Advance(
                        stateDirectory,
                        authorityId,
                        initialRevision,
                        initialSha,
                        motorOff);
                    Append(motorOff.ProjectDirectory,
                        "MotorOffConfirmed", authorityId);
                }

                // Deliberate kill window. The Supervisor/sidecar must restart
                // this independent test agent and resume the same authority.
                Thread.Sleep(5000);

                if (!SupervisorSafetyAuthorityStore.TryRead(
                        stateDirectory,
                        authorityId,
                        out authority,
                        out failure))
                    throw new InvalidDataException(
                        "E2EAuthorityRereadFailed:" + failure);
                if (!authority.Receipt.IsSafetyCompleted)
                {
                    var completed = authority.Receipt;
                    completed.Revision++;
                    completed.State = WatchdogSafetyHandoffState.Completed;
                    completed.Stage = WatchdogSafetyStage.Completed;
                    completed.MotorsOff = true;
                    completed.PowerOff = true;
                    completed.PressureSafe = true;
                    completed.LogicalQuiescent = true;
                    completed.HardwareResourcesReleased = true;
                    completed.ExecutionAuthorizationRevoked = true;
                    completed.CallbacksIsolated = true;
                    completed.DataAuditState = completed.PersistenceDrained
                        ? WatchdogDataAuditState.Drained
                        : WatchdogDataAuditState.CrashRepairRequired;
                    completed.FailureCode = string.Empty;
                    completed.FailureDomain = RecoveryFailureDomain.None;
                    completed.StageUtcTicks = DateTime.UtcNow.Ticks;
                    completed.UpdatedUtcTicks = completed.StageUtcTicks;
                    completed.Detail =
                        "E2ENoHardwareSafetyProofCompleted";
                    SupervisorSafetyAuthorityStore.Advance(
                        stateDirectory,
                        authorityId,
                        initialRevision,
                        initialSha,
                        completed);
                    Append(completed.ProjectDirectory,
                        "SafetyProofCompleted", authorityId);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    var root = Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.CommonApplicationData),
                        "MTTFTest",
                        "UnattendedRecoveryE2E");
                    Directory.CreateDirectory(root);
                    File.AppendAllText(
                        Path.Combine(root, "e2e-safety-agent-error.log"),
                        DateTime.UtcNow.ToString("O") + "|" +
                        ex.GetBaseException().Message + Environment.NewLine);
                }
                catch { }
                return 10;
            }
        }

        private static string Read(string[] args, string name)
        {
            for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                if (string.Equals(args[i], name,
                        StringComparison.OrdinalIgnoreCase))
                    return args[i + 1] ?? string.Empty;
            return string.Empty;
        }

        private static void Append(
            string projectDirectory,
            string eventType,
            string authorityId)
        {
            try
            {
                var root = WatchdogJournalPaths.ValidateProjectDirectory(
                    projectDirectory);
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "e2e-safety-agent-events.log"),
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                    "|" + eventType + "|PID=" +
                    Process.GetCurrentProcess().Id + "|Authority=" +
                    authorityId + Environment.NewLine);
            }
            catch { }
        }
    }
}
