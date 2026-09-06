using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog.Client
{
    /// <summary>
    /// Production launch path: the main process registers the session with the
    /// LocalSystem supervisor and never creates its own watchdog helper.
    /// Engineering packages without the formal-mode marker retain a bounded
    /// compatibility fallback so local development does not require SCM.
    /// </summary>
    public sealed class SupervisorSidecarProcessLauncher : ISidecarProcessLauncher
    {
        public const string FormalModeMarkerName = "MTTFTest.UnattendedMode.required";
        private readonly SystemSidecarProcessLauncher _engineeringFallback =
            new SystemSidecarProcessLauncher();

        public async Task<SidecarProcessLaunchResult> LaunchAsync(
            SidecarProcessLaunchRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            try
            {
                return await LaunchThroughSupervisorAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch when (!IsFormalModeRequired(request.ExecutablePath))
            {
                return await _engineeringFallback.LaunchAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public static bool IsFormalModeRequired(string executablePath)
        {
            var directory = Path.GetDirectoryName(executablePath ?? string.Empty);
            return !string.IsNullOrWhiteSpace(directory) &&
                   File.Exists(Path.Combine(directory, FormalModeMarkerName));
        }

        private static async Task<SidecarProcessLaunchResult> LaunchThroughSupervisorAsync(
            SidecarProcessLaunchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executablePath = Path.GetFullPath(request.ExecutablePath);
            var workingDirectory = Path.GetFullPath(
                request.WorkingDirectory ?? Environment.CurrentDirectory);
            SupervisorSessionLaunchRequest message;
            using (var current = Process.GetCurrentProcess())
            {
                message = new SupervisorSessionLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    ExecutablePath = executablePath,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(
                        executablePath),
                    Arguments = request.Arguments ?? string.Empty,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                        request.Arguments ?? string.Empty),
                    WorkingDirectory = workingDirectory
                };
            }

            SupervisorSessionLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SupervisorProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.Asynchronous))
            {
                var connect = Task.Run(() => pipe.Connect(1500), cancellationToken);
                await connect.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                using (var deadline = new PipeExchangeDeadline(pipe, 10000))
                using (var writer = new BinaryWriter(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                using (var reader = new BinaryReader(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                {
                    message.WriteTo(writer);
                    response = SupervisorSessionLaunchResponse.ReadFrom(reader);
                }
            }

            if (response == null ||
                response.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !string.Equals(response.RequestId, message.RequestId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.ChallengeNonce, message.ChallengeNonce,
                    StringComparison.Ordinal) ||
                !response.Accepted || response.ProcessId <= 0 ||
                response.ProcessStartUtcTicks <= 0)
                throw new InvalidOperationException(
                    "SupervisorLaunchRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));

            cancellationToken.ThrowIfCancellationRequested();
            var authorizedNonce = SupervisorProtocol.ReadSessionHostBinding(response.Detail);
            var process = Process.GetProcessById(response.ProcessId);
            try
            {
                if (process.HasExited ||
                    process.StartTime.ToUniversalTime().Ticks !=
                        response.ProcessStartUtcTicks)
                    throw new InvalidOperationException(
                        "SupervisorLaunchProcessIdentityMismatch");
                return new SidecarProcessLaunchResult
                {
                    Process = process,
                    Owner = new SidecarProcessHandleOwner(process),
                    AuthorizedInstanceNonce = authorizedNonce
                };
            }
            catch
            {
                try { process.Dispose(); } catch { }
                throw;
            }
        }
    }
}
