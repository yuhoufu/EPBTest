using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// The only dispositions that may cross the watchdog recovery-failure
    /// boundary.  Keep this list deliberately small: prose is diagnostic
    /// data, never a decision.
    /// </summary>
    public enum RecoveryFailureDisposition
    {
        Superseded = 0,
        RelaunchApproved = 1,
        RelaunchAlreadyPending = 2,
        CircuitOpen = 3,
        IdentityConflict = 4
    }

    public static class RecoveryFailureDispositions
    {
        public const string Superseded = "Superseded";
        public const string RelaunchApproved = "RelaunchApproved";
        public const string RelaunchAlreadyPending = "RelaunchAlreadyPending";
        public const string CircuitOpen = "CircuitOpen";
        public const string IdentityConflict = "IdentityConflict";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Superseded, StringComparison.Ordinal) ||
                   string.Equals(value, RelaunchApproved, StringComparison.Ordinal) ||
                   string.Equals(value, RelaunchAlreadyPending, StringComparison.Ordinal) ||
                   string.Equals(value, CircuitOpen, StringComparison.Ordinal) ||
                   string.Equals(value, IdentityConflict, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Exact v4 wire receipt for a recovery-attempt failure.  It intentionally
    /// carries no permit nonce: nonce is launch transport evidence and must not
    /// be reflected back as durable failure authority.
    /// </summary>
    public sealed class RecoveryFailureReceipt
    {
        public string RequestCorrelationId { get; set; }
        public string RequestPayloadSha256 { get; set; }
        public string FailureCode { get; set; }
        public string FailureFingerprint { get; set; }
        public string Disposition { get; set; }
        public bool Durable { get; set; }
        public bool PermitClosed { get; set; }
        public bool FailureRegistered { get; set; }
        public bool CircuitOpen { get; set; }
        public int ConsecutiveCount { get; set; }
        public long RelaunchPermitGeneration { get; set; }
        public long DecisionSequence { get; set; }
        public long DecisionUtcTicks { get; set; }
        public string DetailCode { get; set; }

        public RecoveryFailureReceipt Clone() => new RecoveryFailureReceipt
        {
            RequestCorrelationId = RequestCorrelationId,
            RequestPayloadSha256 = RequestPayloadSha256,
            FailureCode = FailureCode,
            FailureFingerprint = FailureFingerprint,
            Disposition = Disposition,
            Durable = Durable,
            PermitClosed = PermitClosed,
            FailureRegistered = FailureRegistered,
            CircuitOpen = CircuitOpen,
            ConsecutiveCount = ConsecutiveCount,
            RelaunchPermitGeneration = RelaunchPermitGeneration,
            DecisionSequence = DecisionSequence,
            DecisionUtcTicks = DecisionUtcTicks,
            DetailCode = DetailCode
        };

        public bool IsDecisionValid()
        {
            return IsValidCorrelation(RequestCorrelationId) &&
                   IsSha256(RequestPayloadSha256) &&
                   !string.IsNullOrWhiteSpace(FailureCode) &&
                   !string.IsNullOrWhiteSpace(FailureFingerprint) &&
                   RecoveryFailureDispositions.IsDefined(Disposition) &&
                   ConsecutiveCount >= 0 &&
                   RelaunchPermitGeneration >= 0 &&
                   DecisionSequence > 0 &&
                   DecisionUtcTicks > 0 &&
                   !string.IsNullOrWhiteSpace(DetailCode);
        }

        internal static bool IsValidCorrelation(string value)
        {
            Guid parsed;
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Length == 32 &&
                   Guid.TryParseExact(value, "N", out parsed) &&
                   string.Equals(parsed.ToString("N"), value, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            return value.All(c => (c >= '0' && c <= '9') ||
                                  (c >= 'A' && c <= 'F') ||
                                  (c >= 'a' && c <= 'f'));
        }
    }

    /// <summary>
    /// Stable fields used to identify one recovery failure operation.  No
    /// free-text detail is accepted by the canonical digest.
    /// </summary>
    public sealed class RecoveryFailureOperation
    {
        public string OperationId { get; set; }
        public string SessionId { get; set; }
        public string SessionNonce { get; set; }
        public int SidecarProcessId { get; set; }
        public long SidecarProcessStartUtcTicks { get; set; }
        public long ConnectionGeneration { get; set; }
        public long RecoveryAttemptGeneration { get; set; }
        public string RequestCorrelationId { get; set; }
        public string RequestPayloadSha256 { get; set; }
        public string FailureCode { get; set; }
        public string FailureFingerprint { get; set; }
        public bool Permanent { get; set; }
        public string DetailCode { get; set; }
        /// <summary>
        /// Diagnostic-only prose.  These fields are intentionally excluded
        /// from RecoveryFailureCanonical and are never used for replay or
        /// circuit decisions.
        /// </summary>
        public string FailureReason { get; set; }
        public string DiagnosticProse { get; set; }
        public string ExceptionText { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public int MaximumProcessRelaunches { get; set; }

        public RecoveryFailureOperation Clone() => (RecoveryFailureOperation)MemberwiseClone();

        public string CanonicalSha256() => RecoveryFailureCanonical.Sha256(this);
    }

    /// <summary>
    /// Canonical, culture-independent SHA-256 for a recovery failure request.
    /// Length framing prevents concatenation ambiguity and ordinal UTF-8
    /// encoding makes replay/deduplication stable across machines.
    /// </summary>
    public static class RecoveryFailureCanonical
    {
        public static string Sha256(RecoveryFailureOperation operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            var canonical = string.Join("|", new[]
            {
                Field("session", operation.SessionId),
                Field("nonce", operation.SessionNonce),
                Field("pid", operation.SidecarProcessId.ToString(CultureInfo.InvariantCulture)),
                Field("start", operation.SidecarProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture)),
                Field("connection", operation.ConnectionGeneration.ToString(CultureInfo.InvariantCulture)),
                Field("attempt", operation.RecoveryAttemptGeneration.ToString(CultureInfo.InvariantCulture)),
                Field("operation", operation.OperationId),
                Field("correlation", NormalizeGuid(operation.RequestCorrelationId)),
                Field("payload", NormalizeHash(operation.RequestPayloadSha256)),
                Field("code", operation.FailureCode),
                Field("fingerprint", operation.FailureFingerprint),
                Field("permanent", operation.Permanent ? "1" : "0"),
                Field("detailcode", operation.DetailCode),
                Field("run", operation.RunId),
                Field("runepoch", operation.RunEpoch.ToString(CultureInfo.InvariantCulture)),
                Field("stage", operation.RecoveryStage),
                Field("progress", operation.RecoveryProgressToken),
                Field("source", operation.RecoveryProcessSource),
                Field("group", operation.DeviceOrChannelGroup),
                Field("budget", operation.MaximumProcessRelaunches.ToString(CultureInfo.InvariantCulture))
            });
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToUpperInvariant();
            }
        }

        public static bool TryValidate(RecoveryFailureOperation operation, out string reason)
        {
            reason = null;
            if (operation == null) { reason = "OperationMissing"; return false; }
            if (string.IsNullOrWhiteSpace(operation.SessionId)) { reason = "SessionMissing"; return false; }
            if (!RecoveryFailureReceipt.IsValidCorrelation(operation.RequestCorrelationId)) { reason = "CorrelationNotN"; return false; }
            if (!RecoveryFailureReceipt.IsSha256(operation.RequestPayloadSha256)) { reason = "PayloadHashInvalid"; return false; }
            if (string.IsNullOrWhiteSpace(operation.FailureCode)) { reason = "FailureCodeMissing"; return false; }
            if (string.IsNullOrWhiteSpace(operation.FailureFingerprint)) { reason = "FingerprintMissing"; return false; }
            if (operation.SidecarProcessId < 0 || operation.SidecarProcessStartUtcTicks < 0 ||
                operation.ConnectionGeneration < 0 || operation.RecoveryAttemptGeneration < 0 ||
                operation.MaximumProcessRelaunches < 0)
            { reason = "IdentityNegative"; return false; }
            return true;
        }

        private static string Field(string key, string value)
        {
            value = value ?? string.Empty;
            return key + "=" + value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
        }

        private static string NormalizeGuid(string value)
        {
            Guid parsed;
            if (!Guid.TryParseExact(value ?? string.Empty, "N", out parsed))
                return (value ?? string.Empty).Trim().ToUpperInvariant();
            return parsed.ToString("N").ToUpperInvariant();
        }

        private static string NormalizeHash(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    }
}
