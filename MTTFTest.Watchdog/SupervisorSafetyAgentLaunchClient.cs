using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Formal-mode SafetyAgent launch boundary.  The watchdog session host
    /// presents the exact handoff/permit identity; only the LocalSystem
    /// supervisor owns Process.Start and the durable process identity.
    /// </summary>
    internal static class SupervisorSafetyAgentLaunchClient
    {
        internal static Process Start(
            WatchdogSafetyHandoffReceipt receipt,
            string executable,
            string arguments)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var executablePath = Path.GetFullPath(executable);
            var workingDirectory = Path.GetDirectoryName(executablePath) ??
                                   Environment.CurrentDirectory;
            SupervisorSafetyAgentLaunchRequest request;
            using (var current = Process.GetCurrentProcess())
            {
                request = new SupervisorSafetyAgentLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    SessionId = receipt.SessionId,
                    PermitGeneration = receipt.RelaunchPermitGeneration,
                    PermitId = receipt.RelaunchPermitId,
                    HandoffId = receipt.HandoffId,
                    HandoffNonceSha256 =
                        SupervisorProtocol.ComputeTextSha256(receipt.Nonce),
                    ExecutablePath = executablePath,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(
                        executablePath),
                    Arguments = arguments ?? string.Empty,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                        arguments ?? string.Empty),
                    WorkingDirectory = Path.GetFullPath(workingDirectory)
                };
            }

            SupervisorSafetyAgentLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SupervisorProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.None))
            {
                pipe.Connect(5000);
                using (var writer = new BinaryWriter(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                using (var reader = new BinaryReader(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                {
                    request.WriteTo(writer);
                    response = SupervisorSafetyAgentLaunchResponse.ReadFrom(reader);
                }
            }

            if (response?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !response.Accepted ||
                !string.Equals(response.RequestId, request.RequestId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.ChallengeNonce, request.ChallengeNonce,
                    StringComparison.Ordinal) ||
                response.ProcessId <= 0 || response.ProcessStartUtcTicks <= 0)
                throw new InvalidOperationException(
                    "SupervisorSafetyAgentLaunchRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));

            var process = Process.GetProcessById(response.ProcessId);
            try
            {
                if (process.HasExited ||
                    process.StartTime.ToUniversalTime().Ticks !=
                        response.ProcessStartUtcTicks)
                    throw new InvalidOperationException(
                        "SupervisorSafetyAgentIdentityMismatch");
                return process;
            }
            catch
            {
                try { process.Dispose(); } catch { }
                throw;
            }
        }
    }
}
