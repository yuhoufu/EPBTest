using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal static class EngineLaunchCapabilityGate
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static bool Validate(string[] args, out string failure)
        {
            failure = string.Empty;
            try
            {
                var capabilityId = Read(args, SessionAgentProtocol.CapabilityArgument);
                var nonce = Read(args, SessionAgentProtocol.NonceArgument);
                var sessionId = Read(args, SessionAgentProtocol.SessionArgument);
                if (!int.TryParse(Read(args, SessionAgentProtocol.SchemaArgument), out var schema) ||
                    schema != SessionAgentProtocol.SchemaVersion ||
                    !RecoveryProtocolV7.IsGuid(capabilityId) ||
                    !RecoveryProtocolV7.IsGuid(sessionId) ||
                    !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(nonce))
                    return Reject("CapabilityArgumentsInvalid", out failure);
                var recordPath = SessionAgentProtocol.ConsumptionPath(capabilityId);
                SessionLaunchConsumptionRecord record = null;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow <= deadline)
                {
                    if (File.Exists(recordPath))
                    {
                        try
                        {
                            record = Json.Deserialize<SessionLaunchConsumptionRecord>(
                                File.ReadAllText(recordPath, Encoding.UTF8));
                        }
                        catch (IOException) { }
                        if (record != null && string.Equals(
                                record.State, "Started", StringComparison.Ordinal))
                            break;
                    }
                    Thread.Sleep(25);
                }
                var canonicalPath = recordPath + ".capability";
                if (record == null || !File.Exists(canonicalPath))
                    return Reject("CapabilityConsumptionMissing", out failure);
                var canonical = Json.Deserialize<SessionLaunchCapability>(
                    File.ReadAllText(canonicalPath, Encoding.UTF8));
                byte[] seal;
                try { seal = Convert.FromBase64String(record.CapabilitySealBase64 ?? string.Empty); }
                catch (FormatException)
                {
                    return Reject("CapabilitySealEncodingInvalid", out failure);
                }
                if (canonical?.IsStructurallyValid() != true ||
                    canonical.ProcessRole != ProcessRole.EngineHost ||
                    record.ProcessRole != ProcessRole.EngineHost ||
                    !SessionAgentProtocol.VerifySeal(canonical, seal))
                    return Reject("EngineRoleCapabilityInvalid", out failure);
                using (var process = Process.GetCurrentProcess())
                {
                    var executable = Path.GetFullPath(process.MainModule.FileName);
                    if (record.ProcessId != process.Id ||
                        record.ProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks ||
                        canonical.DesktopSessionId != process.SessionId ||
                        !string.Equals(canonical.CapabilityId, capabilityId, StringComparison.Ordinal) ||
                        !string.Equals(canonical.SessionId, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(canonical.LaunchNonce, nonce, StringComparison.Ordinal) ||
                        !string.Equals(record.CapabilityId, capabilityId, StringComparison.Ordinal) ||
                        !string.Equals(record.SessionId, sessionId, StringComparison.Ordinal) ||
                        !string.Equals(record.LaunchNonce, nonce, StringComparison.Ordinal) ||
                        !string.Equals(Path.GetFullPath(canonical.ExecutablePath), executable,
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(SupervisorProtocol.ComputeSha256(executable),
                            canonical.ExecutableSha256, StringComparison.Ordinal) ||
                        DateTime.UtcNow.Ticks > canonical.ExpiresUtcTicks +
                            TimeSpan.FromSeconds(5).Ticks)
                        return Reject("EngineCapabilityBindingMismatch", out failure);
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = "CapabilityValidationFailed:" + ex.GetBaseException().Message;
                return false;
            }
        }

        private static string Read(string[] args, string name)
        {
            for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1]?.Trim('"') ?? string.Empty;
            return string.Empty;
        }

        private static bool Reject(string reason, out string failure)
        {
            failure = reason;
            return false;
        }
    }
}
