using System;
using System.Diagnostics;
using System.IO;
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
            catch when (!IsFormalModeRequired(request.ExecutablePath) && !IsIndependentModeRequired())
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

        private static bool IsIndependentModeRequired()
        {
            // A bound installation cannot evade a Supervisor rejection through
            // the engineering fallback, even if its legacy marker is absent.
            // Invalid binding exceptions in this filter also deny fallback.
            using (var current = Process.GetCurrentProcess())
                return IndependentInstallationBinding.Resolve(current.MainModule.FileName) != null;
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

            var response = await ExchangeSupervisorLaunchAsync(
                message, cancellationToken, SupervisorProtocol.PipeName).ConfigureAwait(false);

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
                    Owner = new SidecarProcessHandleOwner(process)
                };
            }
            catch
            {
                try { process.Dispose(); } catch { }
                throw;
            }
        }

        internal static Task<SupervisorSessionLaunchResponse> ExchangeSupervisorLaunchAsync(
            SupervisorSessionLaunchRequest message, CancellationToken cancellation, string pipeName)
        {
            // Bound connect, write and the complete reply together. Merely
            // timing out Connect leaves a connected-but-stalled Supervisor
            // holding a startup worker and its shutdown receipt indefinitely.
            return Task.Run(() => DeadlinePipeExchange.Execute(pipeName, 3000,
                writer => message.WriteTo(writer), SupervisorSessionLaunchResponse.ReadFrom,
                cancellation), cancellation);
        }
    }
}
