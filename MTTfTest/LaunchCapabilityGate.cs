using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    /// <summary>
    /// Production admission gate for MTTFTest.exe.  Only an exact process
    /// identity already recorded by SessionAgent from a Supervisor-issued,
    /// one-shot schema 6 capability may continue into configuration or UI.
    /// </summary>
    internal static class LaunchCapabilityGate
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer();

        internal static bool ValidateOrReject(string[] args)
        {
            string failure;
            if (TryValidate(args, out failure)) return true;
            WriteRejected(failure);
            try
            {
                MessageBox.Show(
                    "V2.15.0.0 正式版仅允许由 Supervisor 服务启动。\r\n" +
                    "请使用桌面“MT EPB 试验系统 V2.15”快捷方式。" +
                    "\r\n\r\n拒绝原因：" + failure,
                    "启动已拒绝",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { }
            return false;
        }

        internal static bool TryValidate(string[] args, out string failure)
        {
            failure = string.Empty;
            try
            {
                var capabilityId = Read(args, SessionAgentProtocol.CapabilityArgument);
                var launchNonce = Read(args, SessionAgentProtocol.NonceArgument);
                var sessionId = Read(args, SessionAgentProtocol.SessionArgument);
                var schemaText = Read(args, SessionAgentProtocol.SchemaArgument);
                int schema;
                if (!int.TryParse(schemaText, out schema) ||
                    schema != SessionAgentProtocol.SchemaVersion)
                    return Reject("LaunchCapabilitySchemaMismatch", out failure);
                Guid parsed;
                if (!Guid.TryParseExact(capabilityId, "N", out parsed))
                    return Reject("LaunchCapabilityIdMissing", out failure);
                if (!WatchdogProcessIdentityPolicy.IsValidChallengeNonce(launchNonce))
                    return Reject("LaunchCapabilityNonceMissing", out failure);
                if (!Guid.TryParseExact(sessionId, "N", out parsed))
                    return Reject("LaunchCapabilitySessionMissing", out failure);

                var path = SessionAgentProtocol.ConsumptionPath(capabilityId);
                SessionLaunchConsumptionRecord record = null;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow <= deadline)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            record = Json.Deserialize<SessionLaunchConsumptionRecord>(
                                File.ReadAllText(path, Encoding.UTF8));
                        }
                        catch (IOException) { }
                        if (record != null &&
                            string.Equals(record.State, "Started",
                                StringComparison.Ordinal))
                            break;
                    }
                    Thread.Sleep(25);
                }
                if (record == null)
                    return Reject("LaunchCapabilityConsumptionMissing", out failure);
                if (record.SchemaVersion != SessionAgentProtocol.SchemaVersion ||
                    !string.Equals(record.State, "Started", StringComparison.Ordinal))
                    return Reject("LaunchCapabilityConsumptionStateInvalid", out failure);

                byte[] seal;
                try { seal = Convert.FromBase64String(record.CapabilitySealBase64 ?? string.Empty); }
                catch (FormatException)
                {
                    return Reject("LaunchCapabilitySealEncodingInvalid", out failure);
                }
                // The canonical capability includes fields not duplicated into
                // the consumption index.  Read it from the sealed payload is
                // deliberately impossible without the original object, so the
                // record also persists the complete canonical source below.
                var canonicalPath = path + ".capability";
                if (!File.Exists(canonicalPath))
                    return Reject("LaunchCapabilityCanonicalMissing", out failure);
                SessionLaunchCapability canonical;
                try
                {
                    canonical = Json.Deserialize<SessionLaunchCapability>(
                        File.ReadAllText(canonicalPath, Encoding.UTF8));
                }
                catch (Exception)
                {
                    return Reject("LaunchCapabilityCanonicalReadFailed", out failure);
                }
                if (canonical?.IsStructurallyValid() != true ||
                    !SessionAgentProtocol.VerifySeal(canonical, seal))
                    return Reject("LaunchCapabilitySealInvalid", out failure);

                using (var current = Process.GetCurrentProcess())
                {
                    var executable = Path.GetFullPath(current.MainModule.FileName);
                    var startTicks = current.StartTime.ToUniversalTime().Ticks;
                    return ValidateBoundCapability(
                        canonical,
                        record,
                        capabilityId,
                        launchNonce,
                        sessionId,
                        current.Id,
                        startTicks,
                        current.SessionId,
                        executable,
                        DateTime.UtcNow.Ticks,
                        out failure);
                }
            }
            catch (Exception ex)
            {
                failure = "LaunchCapabilityValidationFailed:" +
                          ex.GetBaseException().Message;
                return false;
            }
        }

        internal static bool ValidateBoundCapability(
            SessionLaunchCapability canonical,
            SessionLaunchConsumptionRecord record,
            string capabilityId,
            string launchNonce,
            string sessionId,
            int processId,
            long processStartUtcTicks,
            int desktopSessionId,
            string executablePath,
            long nowUtcTicks,
            out string failure)
        {
            failure = string.Empty;
            if (canonical?.IsStructurallyValid() != true || record == null ||
                record.SchemaVersion != SessionAgentProtocol.SchemaVersion ||
                !string.Equals(record.State, "Started", StringComparison.Ordinal))
                return Reject("LaunchCapabilityConsumptionStateInvalid", out failure);
            if (record.ProcessId != processId ||
                record.ProcessStartUtcTicks != processStartUtcTicks)
                return Reject("LaunchCapabilityProcessIdentityMismatch", out failure);
            var executable = Path.GetFullPath(executablePath ?? string.Empty);
            if (!string.Equals(
                    Path.GetFullPath(canonical.ExecutablePath),
                    executable,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    SupervisorProtocol.ComputeSha256(executable),
                    canonical.ExecutableSha256,
                    StringComparison.Ordinal))
                return Reject("LaunchCapabilityExecutableMismatch", out failure);
            if (canonical.DesktopSessionId != desktopSessionId)
                return Reject("LaunchCapabilityDesktopMismatch", out failure);
            if (!string.Equals(canonical.CapabilityId, capabilityId,
                    StringComparison.Ordinal) ||
                !string.Equals(canonical.LaunchNonce, launchNonce,
                    StringComparison.Ordinal) ||
                !string.Equals(canonical.SessionId, sessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(record.CapabilityId, capabilityId,
                    StringComparison.Ordinal) ||
                !string.Equals(record.LaunchNonce, launchNonce,
                    StringComparison.Ordinal) ||
                !string.Equals(record.SessionId, sessionId,
                    StringComparison.Ordinal) ||
                record.PermitGeneration != canonical.PermitGeneration ||
                !string.Equals(record.PermitId, canonical.PermitId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetFullPath(record.ExecutablePath ?? string.Empty),
                    Path.GetFullPath(canonical.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.ExecutableSha256,
                    canonical.ExecutableSha256, StringComparison.Ordinal) ||
                !string.Equals(record.ArgumentsSha256,
                    canonical.ArgumentsSha256, StringComparison.Ordinal))
                return Reject("LaunchCapabilityBindingMismatch", out failure);
            if (nowUtcTicks >
                canonical.ExpiresUtcTicks + TimeSpan.FromSeconds(5).Ticks)
                return Reject("LaunchCapabilityExpired", out failure);
            return true;
        }

        private static string Read(string[] args, string name)
        {
            for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1] ?? string.Empty;
            return string.Empty;
        }

        private static bool Reject(string reason, out string failure)
        {
            failure = reason;
            return false;
        }

        private static void WriteRejected(string failure)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTest",
                    "LaunchAudit");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "main-launch-rejected.log"),
                    DateTime.UtcNow.ToString("O") + " " +
                    (failure ?? "Unknown") + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

}
