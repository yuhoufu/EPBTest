using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal static class SessionAgentLaunchClient
    {
        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        internal static Process Start(DurableLaunchIntentCapability source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var desktopSession = unchecked((int)WTSGetActiveConsoleSessionId());
            if (desktopSession < 0)
                throw new InvalidOperationException("ActiveDesktopSessionUnavailable");
            SessionLaunchCapability capability;
            using (var issuer = Process.GetCurrentProcess())
            {
                capability = new SessionLaunchCapability
                {
                    CapabilityId = source.IntentId,
                    SessionId = source.SessionId,
                    PermitGeneration = source.Generation,
                    PermitId = source.PermitId,
                    DesktopSessionId = desktopSession,
                    ExecutablePath = source.ExecutablePath,
                    ExecutableSha256 = source.ExecutableSha256,
                    Arguments = source.Arguments ?? string.Empty,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                        source.Arguments ?? string.Empty),
                    WorkingDirectory = source.WorkingDirectory,
                    LaunchNonce = Guid.NewGuid().ToString("N"),
                    IssuedUtcTicks = DateTime.UtcNow.Ticks,
                    ExpiresUtcTicks = DateTime.UtcNow.AddSeconds(60).Ticks,
                    IssuerProcessId = issuer.Id,
                    IssuerProcessStartUtcTicks =
                        issuer.StartTime.ToUniversalTime().Ticks
                };
            }
            var seal = SessionAgentProtocol.Seal(capability);
            SessionLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SessionAgentProtocol.PipeName(desktopSession),
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
                    capability.WriteTo(writer, seal);
                    response = SessionLaunchResponse.ReadFrom(reader);
                }
            }
            if (response?.SchemaVersion != 5 || !response.Accepted ||
                !string.Equals(response.CapabilityId, capability.CapabilityId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.LaunchNonce, capability.LaunchNonce,
                    StringComparison.Ordinal) ||
                response.ProcessId <= 0 || response.ProcessStartUtcTicks <= 0)
                throw new InvalidOperationException(
                    "SessionAgentLaunchRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));
            var process = Process.GetProcessById(response.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks !=
                response.ProcessStartUtcTicks)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "SessionAgentProcessIdentityMismatch");
            }
            return process;
        }
    }
}
