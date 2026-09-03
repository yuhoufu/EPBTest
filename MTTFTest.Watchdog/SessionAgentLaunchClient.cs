using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal static class SessionAgentLaunchClient
    {
        internal static Process Start(DurableLaunchIntentCapability source)
        {
            return SupervisorMainLaunchClient.Start(source);
        }

        internal static Process Start(SessionLaunchCapability capability)
        {
            if (capability?.IsStructurallyValid() != true)
                throw new InvalidDataException("LaunchCapabilityInvalid");
            var seal = SessionAgentProtocol.Seal(capability);
            SessionLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SessionAgentProtocol.PipeName(capability.DesktopSessionId),
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
            if (response?.SchemaVersion != SessionAgentProtocol.SchemaVersion || !response.Accepted ||
                response.ProcessRole != capability.ProcessRole ||
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

        internal static string AppendLaunchProof(
            string arguments,
            string capabilityId,
            string launchNonce,
            string sessionId)
        {
            var prefix = string.IsNullOrWhiteSpace(arguments)
                ? string.Empty
                : arguments.Trim() + " ";
            return prefix +
                   SessionAgentProtocol.CapabilityArgument + " " + Quote(capabilityId) + " " +
                   SessionAgentProtocol.NonceArgument + " " + Quote(launchNonce) + " " +
                   SessionAgentProtocol.SchemaArgument + " " +
                   SessionAgentProtocol.SchemaVersion + " " +
                   SessionAgentProtocol.SessionArgument + " " + Quote(sessionId);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
