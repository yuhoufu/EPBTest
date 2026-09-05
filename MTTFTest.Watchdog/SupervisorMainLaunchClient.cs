using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Both the interactive first launch and an owned Sidecar recovery ask the
    /// LocalSystem Supervisor to issue a one-shot capability and deliver it
    /// through the logged-on user's SessionAgent.  This client never starts
    /// MTTFTest.exe or signs a SessionLaunchCapability itself.
    /// </summary>
    internal static class SupervisorMainLaunchClient
    {
        internal static int Run(string[] args)
        {
            var directory = AppDomain.CurrentDomain.BaseDirectory;
            var executable = Read(args, "--main-executable");
            if (string.IsNullOrWhiteSpace(executable))
                executable = Path.Combine(directory, "MTTFTest.exe");
            executable = Path.GetFullPath(executable);
            var workingDirectory = Path.GetDirectoryName(executable) ?? directory;
            var mainArguments = Read(args, "--main-arguments") ?? string.Empty;

            SupervisorMainLaunchRequest request;
            using (var current = Process.GetCurrentProcess())
            {
                request = new SupervisorMainLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    DesktopSessionId = current.SessionId,
                    ExecutablePath = executable,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(executable),
                    Arguments = mainArguments,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(mainArguments),
                    WorkingDirectory = workingDirectory
                };
            }

            SendAndValidate(request, string.Empty);
            return 0;
        }

        internal static Process Start(DurableLaunchIntentCapability source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            SupervisorMainLaunchRequest request;
            using (var current = Process.GetCurrentProcess())
            {
                request = new SupervisorMainLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    DesktopSessionId =
                        SessionAgentProtocol.RegisteredDesktopSessionId,
                    ExecutablePath = source.ExecutablePath,
                    ExecutableSha256 = source.ExecutableSha256,
                    Arguments = source.Arguments ?? string.Empty,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                        source.Arguments),
                    WorkingDirectory = source.WorkingDirectory,
                    IsRecoveryLaunch = true,
                    RecoveryCapabilityId = source.IntentId,
                    RecoverySessionId = source.SessionId,
                    RecoveryPermitGeneration = source.Generation,
                    RecoveryPermitId = source.PermitId,
                    RecoveryAuthorityRevision = source.AuthorityRevision,
                    RecoveryAuthoritySha256 = source.AuthoritySha256
                };
            }
            var response = SendAndValidate(request, source.IntentId);
            var process = Process.GetProcessById(response.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks !=
                response.ProcessStartUtcTicks)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "SupervisorMainLaunchProcessIdentityMismatch");
            }
            return process;
        }

        private static SupervisorMainLaunchResponse SendAndValidate(
            SupervisorMainLaunchRequest request,
            string expectedCapabilityId)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException(
                    "SupervisorMainLaunchRequestInvalid");
            SupervisorMainLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SupervisorProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.None))
            {
                pipe.Connect(5000);
                using (var writer = new BinaryWriter(pipe, new UTF8Encoding(false), true))
                using (var reader = new BinaryReader(pipe, new UTF8Encoding(false), true))
                {
                    request.WriteTo(writer);
                    response = SupervisorMainLaunchResponse.ReadFrom(reader);
                }
            }

            if (response?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !response.Accepted || response.ProcessId <= 0 ||
                response.ProcessStartUtcTicks <= 0 ||
                !string.Equals(response.RequestId, request.RequestId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.ChallengeNonce, request.ChallengeNonce,
                    StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(expectedCapabilityId) &&
                 !string.Equals(response.CapabilityId, expectedCapabilityId,
                     StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    "SupervisorMainLaunchRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));
            return response;
        }

        private static string Read(string[] args, string name)
        {
            for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return string.Empty;
        }
    }
}
