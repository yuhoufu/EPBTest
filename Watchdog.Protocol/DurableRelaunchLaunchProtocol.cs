using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Frozen, durable launch description.  It is deliberately data-only;
    /// process creation is possible only after the authority has persisted
    /// the corresponding LaunchIntent record and returned a capability.
    /// </summary>
    public sealed class DurableLaunchIntent
    {
        public string SessionId { get; set; }
        public string SessionNonce { get; set; }
        public long Generation { get; set; }
        public string PermitId { get; set; }
        public string PermitNonce { get; set; }
        public string IntentId { get; set; }
        public string ExecutablePath { get; set; }
        public string ExecutableSha256 { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string LaunchOptionsCanonical { get; set; }
        public string LaunchSpecSha256 { get; set; }

        public DurableLaunchIntent Clone() => new DurableLaunchIntent
        {
            SessionId = SessionId, SessionNonce = SessionNonce,
            Generation = Generation, PermitId = PermitId, PermitNonce = PermitNonce,
            IntentId = IntentId, ExecutablePath = ExecutablePath,
            ExecutableSha256 = ExecutableSha256, Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            LaunchOptionsCanonical = LaunchOptionsCanonical,
            LaunchSpecSha256 = LaunchSpecSha256
        };
    }

    /// <summary>
    /// One-shot authorization returned only after a durable LaunchIntent
    /// read-back.  The constructor is internal so callers cannot forge a
    /// capability from a DTO or a legacy permit.
    /// </summary>
    public sealed class DurableLaunchIntentCapability
    {
        internal DurableLaunchIntentCapability(DurableLaunchIntent intent, long authorityRevision, string authoritySha256)
        {
            Intent = intent?.Clone() ?? throw new ArgumentNullException(nameof(intent));
            AuthorityRevision = authorityRevision;
            AuthoritySha256 = authoritySha256;
        }

        internal DurableLaunchIntent Intent { get; }
        public string SessionId => Intent.SessionId;
        public string SessionNonce => Intent.SessionNonce;
        public long Generation => Intent.Generation;
        public string PermitId => Intent.PermitId;
        public string PermitNonce => Intent.PermitNonce;
        public string IntentId => Intent.IntentId;
        public string ExecutablePath => Intent.ExecutablePath;
        public string ExecutableSha256 => Intent.ExecutableSha256;
        public string Arguments => Intent.Arguments;
        public string WorkingDirectory => Intent.WorkingDirectory;
        public string LaunchOptionsCanonical => Intent.LaunchOptionsCanonical;
        public string LaunchSpecSha256 => Intent.LaunchSpecSha256;
        public long AuthorityRevision { get; }
        public string AuthoritySha256 { get; }

        internal DurableLaunchIntent CloneIntent() => Intent.Clone();
    }

    public enum DurableAuthorityTransitionStatus
    {
        Committed = 0,
        Busy = 1,
        Conflict = 2,
        Invalid = 3,
        Blocked = 4,
        Unproven = 5,
        WriteFailed = 6
    }

    public sealed class DurableAuthorityTransitionResult
    {
        public DurableAuthorityTransitionStatus Status { get; internal set; }
        public bool Succeeded => Status == DurableAuthorityTransitionStatus.Committed;
        public bool Durable => Succeeded;
        public string Reason { get; internal set; }
        public DurableRelaunchAuthorityRecord Record { get; internal set; }
        public string Sha256 { get; internal set; }
        public DurableLaunchIntentCapability Capability { get; internal set; }
        public long AuthorityRevision => Record?.AuthorityRevision ?? 0;

        internal DurableAuthorityTransitionResult Clone() => new DurableAuthorityTransitionResult
        {
            Status = Status, Reason = Reason, Record = Record?.Clone(), Sha256 = Sha256,
            Capability = Capability
        };
    }

    /// <summary>Stable launch-spec hashing shared by authority and launcher.</summary>
    public static class DurableLaunchCanonical
    {
        public const string RequiredOptionsCanonical =
            "UseShellExecute=false;CreateNoWindow=true";

        public static string Sha256(DurableLaunchIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            var canonical = string.Join("|", new[]
            {
                Field("session", intent.SessionId),
                Field("nonce", intent.SessionNonce),
                Field("generation", intent.Generation.ToString(CultureInfo.InvariantCulture)),
                Field("permit", intent.PermitId),
                Field("permitNonce", intent.PermitNonce),
                Field("intent", intent.IntentId),
                Field("exe", intent.ExecutablePath),
                Field("exeSha", intent.ExecutableSha256),
                Field("args", intent.Arguments),
                Field("cwd", intent.WorkingDirectory),
                Field("options", intent.LaunchOptionsCanonical)
            });
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty).ToUpperInvariant();
        }

        public static bool TryValidate(DurableLaunchIntent intent, out string reason)
        {
            reason = null;
            if (intent == null) { reason = "IntentMissing"; return false; }
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(intent.SessionId)) { reason = "SessionNotN"; return false; }
            if (string.IsNullOrWhiteSpace(intent.SessionNonce) || !IsToken(intent.SessionNonce)) { reason = "SessionNonceMissing"; return false; }
            if (intent.Generation <= 0 || string.IsNullOrWhiteSpace(intent.PermitId) || string.IsNullOrWhiteSpace(intent.PermitNonce) ||
                string.IsNullOrWhiteSpace(intent.IntentId)) { reason = "PermitIdentityMissing"; return false; }
            if (string.IsNullOrWhiteSpace(intent.ExecutablePath) || !Path.IsPathRooted(intent.ExecutablePath)) { reason = "ExecutablePathInvalid"; return false; }
            if (!IsCanonicalPath(intent.ExecutablePath)) { reason = "ExecutablePathNotCanonical"; return false; }
            if (!RecoveryFailureReceipt.IsSha256(intent.ExecutableSha256)) { reason = "ExecutableHashInvalid"; return false; }
            if (string.IsNullOrWhiteSpace(intent.WorkingDirectory) || !Path.IsPathRooted(intent.WorkingDirectory)) { reason = "WorkingDirectoryInvalid"; return false; }
            if (!IsCanonicalPath(intent.WorkingDirectory)) { reason = "WorkingDirectoryNotCanonical"; return false; }
            if (!string.Equals(intent.LaunchOptionsCanonical, RequiredOptionsCanonical, StringComparison.Ordinal) ||
                !RecoveryFailureReceipt.IsSha256(intent.LaunchSpecSha256)) { reason = "LaunchSpecInvalid"; return false; }
            string calculated;
            try { calculated = Sha256(intent); } catch { reason = "LaunchSpecHashFailed"; return false; }
            if (!string.Equals(calculated, intent.LaunchSpecSha256, StringComparison.Ordinal)) { reason = "LaunchSpecHashMismatch"; return false; }
            return true;
        }

        private static bool IsToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
                   value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        private static bool IsCanonicalPath(string value)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(value),
                    value,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Field(string key, string value)
        {
            value = value ?? string.Empty;
            return key + "=" + value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }
    }
}
