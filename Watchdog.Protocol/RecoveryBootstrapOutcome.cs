using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum RecoveryBootstrapOutcome
    {
        RecoveryAccepted = 1,
        RejectedNoWork = 2,
        StartupFailed = 3,
        RecoveryUiAttached = 4
    }

    public sealed class RecoveryBootstrapReceipt
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public int ChildProcessId { get; set; }
        public long ChildProcessStartUtcTicks { get; set; }
        public long CheckpointRevision { get; set; }
        public RecoveryBootstrapOutcome Outcome { get; set; }
        public string Reason { get; set; } = string.Empty;
        public long WrittenUtcTicks { get; set; }
        public string HmacSha256 { get; set; } = string.Empty;
    }

    public static class RecoveryBootstrapOutcomeStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static string GetPath(string projectDirectory, string sessionId, long generation)
        {
            return Path.Combine(
                WatchdogJournalPaths.ValidateProjectDirectory(projectDirectory),
                "session-" + WatchdogJournalPaths.SafeName(sessionId) +
                ".bootstrap-outcome-" + generation.ToString(CultureInfo.InvariantCulture) + ".json");
        }

        public static void WriteThrough(
            string projectDirectory,
            RecoveryBootstrapReceipt receipt,
            string permitNonce)
        {
            ValidateIdentity(receipt, permitNonce);
            receipt.WrittenUtcTicks = DateTime.UtcNow.Ticks;
            receipt.HmacSha256 = ComputeHmac(receipt, permitNonce);
            AtomicWrite(GetPath(projectDirectory, receipt.SessionId, receipt.PermitGeneration),
                Serializer.Serialize(receipt));
        }

        public static bool TryReadVerified(
            string projectDirectory,
            string sessionId,
            long generation,
            string permitId,
            string permitNonce,
            out RecoveryBootstrapReceipt receipt)
        {
            receipt = null;
            try
            {
                var path = GetPath(projectDirectory, sessionId, generation);
                if (!File.Exists(path)) return false;
                string json;
                using (var stream = new FileStream(
                           path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    json = reader.ReadToEnd();
                var value = Serializer.Deserialize<RecoveryBootstrapReceipt>(json);
                ValidateIdentity(value, permitNonce);
                if (!string.Equals(value.SessionId, sessionId, StringComparison.Ordinal) ||
                    value.PermitGeneration != generation ||
                    !string.Equals(value.PermitId, permitId, StringComparison.Ordinal) ||
                    !FixedEquals(value.HmacSha256, ComputeHmac(value, permitNonce)))
                    return false;
                receipt = value;
                return true;
            }
            catch { return false; }
        }

        private static void ValidateIdentity(RecoveryBootstrapReceipt receipt, string permitNonce)
        {
            if (receipt == null || receipt.SchemaVersion != 1 ||
                string.IsNullOrWhiteSpace(receipt.SessionId) ||
                receipt.PermitGeneration <= 0 || string.IsNullOrWhiteSpace(receipt.PermitId) ||
                receipt.ChildProcessId <= 0 || receipt.ChildProcessStartUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(permitNonce))
                throw new InvalidOperationException("Recovery bootstrap receipt identity is incomplete.");
        }

        private static string ComputeHmac(RecoveryBootstrapReceipt value, string nonce)
        {
            var canonical = string.Join("|", new[]
            {
                value.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                value.SessionId ?? string.Empty,
                value.PermitGeneration.ToString(CultureInfo.InvariantCulture),
                value.PermitId ?? string.Empty,
                value.ChildProcessId.ToString(CultureInfo.InvariantCulture),
                value.ChildProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
                value.CheckpointRevision.ToString(CultureInfo.InvariantCulture),
                ((int)value.Outcome).ToString(CultureInfo.InvariantCulture),
                value.Reason ?? string.Empty,
                value.WrittenUtcTicks.ToString(CultureInfo.InvariantCulture)
            });
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(nonce)))
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty);
        }

        private static bool FixedEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var diff = 0;
            for (var index = 0; index < left.Length; index++) diff |= left[index] ^ right[index];
            return diff == 0;
        }

        private static void AtomicWrite(string path, string payload)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, payload, new UTF8Encoding(false));
                using (var stream = new FileStream(
                           temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
