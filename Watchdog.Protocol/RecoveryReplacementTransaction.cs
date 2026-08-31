using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class RecoveryReplacementTransaction
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public RecoveryReplacementState State { get; set; }
        public long Revision { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public double MonotonicElapsedMs { get; set; }
        public string Detail { get; set; } = string.Empty;

        public bool IsValid()
        {
            Guid parsed;
            return SchemaVersion == 1 &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   PermitGeneration > 0 &&
                   Guid.TryParseExact(PermitId ?? string.Empty, "N", out parsed) &&
                   Enum.IsDefined(typeof(RecoveryReplacementState), State) &&
                   State >= RecoveryReplacementState.Approved &&
                   Revision > 0 && UpdatedUtcTicks > 0 && MonotonicElapsedMs >= 0;
        }
    }

    public sealed class RecoveryReplacementAdvanceResult
    {
        public bool Succeeded { get; internal set; }
        public bool AlreadyApplied { get; internal set; }
        public string Reason { get; internal set; } = string.Empty;
        public RecoveryReplacementTransaction Transaction { get; internal set; }
    }

    /// <summary>
    /// Durable, one-way recovery transaction keyed by Session/Generation/Permit.
    /// Both takeover and application-exit observers submit to this same record.
    /// </summary>
    public static class RecoveryReplacementTransactionStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static RecoveryReplacementAdvanceResult Advance(
            string journalDirectory,
            string sessionId,
            long permitGeneration,
            string permitId,
            RecoveryReplacementState state,
            string detail)
        {
            Guid parsed;
            if (!Guid.TryParseExact(sessionId ?? string.Empty, "N", out parsed) ||
                permitGeneration <= 0 ||
                !Guid.TryParseExact(permitId ?? string.Empty, "N", out parsed) ||
                state < RecoveryReplacementState.Approved ||
                !Enum.IsDefined(typeof(RecoveryReplacementState), state))
                return Failed("ReplacementIdentityInvalid");

            var root = WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory);
            var path = Path.Combine(root, "session-" + sessionId +
                ".replacement-" + permitGeneration + "-" + permitId + ".json");
            var mutexName = "Local\\MTTFTest-RecoveryReplacement-" +
                DurableJsonFileStore.ComputeSha256(Utf8.GetBytes(
                    Path.GetFullPath(path).ToUpperInvariant())).Substring(0, 32);
            using (var mutex = new Mutex(false, mutexName))
            {
                bool held;
                try { held = mutex.WaitOne(5000, false); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return Failed("ReplacementTransactionBusy");
                try
                {
                    var read = DurableJsonFileStore.ReadLatestValid<RecoveryReplacementTransaction>(
                        value => value?.Revision ?? 0,
                        path,
                        path + ".bak");
                    var current = read?.Value;
                    if (current != null)
                    {
                        if (!current.IsValid() ||
                            !string.Equals(current.SessionId, sessionId, StringComparison.Ordinal) ||
                            current.PermitGeneration != permitGeneration ||
                            !string.Equals(current.PermitId, permitId, StringComparison.Ordinal))
                            return Failed("ReplacementTransactionCorruptOrMismatched");
                        if (current.State >= state)
                            return new RecoveryReplacementAdvanceResult
                            {
                                Succeeded = true,
                                AlreadyApplied = true,
                                Reason = "AlreadyApplied",
                                Transaction = current
                            };
                        if ((int)state != (int)current.State + 1)
                            return Failed("ReplacementStageSkipped");
                    }
                    else if (read != null && read.Status != DurableJsonReadStatus.Missing)
                    {
                        return Failed("ReplacementTransactionUnreadable");
                    }
                    else if (state != RecoveryReplacementState.Approved)
                    {
                        return Failed("ReplacementApprovalMissing");
                    }

                    var currentMonotonicMs = Stopwatch.GetTimestamp() * 1000.0 /
                                             Stopwatch.Frequency;
                    var next = new RecoveryReplacementTransaction
                    {
                        SessionId = sessionId,
                        PermitGeneration = permitGeneration,
                        PermitId = permitId,
                        State = state,
                        Revision = (current?.Revision ?? 0) + 1,
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                        MonotonicElapsedMs = Math.Max(
                            (current?.MonotonicElapsedMs ?? -0.001) + 0.001,
                            currentMonotonicMs),
                        Detail = detail ?? string.Empty
                    };
                    DurableJsonFileStore.WriteAtomicWithBackup(
                        path,
                        Utf8.GetBytes(Json.Serialize(next)));
                    var verified = DurableJsonFileStore.ReadLatestValid<RecoveryReplacementTransaction>(
                        value => value?.Revision ?? 0,
                        path,
                        path + ".bak").Value;
                    if (verified == null || !verified.IsValid() ||
                        verified.Revision != next.Revision || verified.State != state)
                        return Failed("ReplacementTransactionReadbackFailed");
                    return new RecoveryReplacementAdvanceResult
                    {
                        Succeeded = true,
                        Reason = "Applied",
                        Transaction = verified
                    };
                }
                catch (Exception ex)
                {
                    return Failed("ReplacementTransactionWriteFailed:" +
                                  ex.GetBaseException().Message);
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        public static bool TryRead(
            string journalDirectory,
            string sessionId,
            long permitGeneration,
            string permitId,
            out RecoveryReplacementTransaction transaction)
        {
            transaction = null;
            try
            {
                var root = WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory);
                var path = Path.Combine(root, "session-" + sessionId +
                    ".replacement-" + permitGeneration + "-" + permitId + ".json");
                var read = DurableJsonFileStore.ReadLatestValid<RecoveryReplacementTransaction>(
                    value => value?.Revision ?? 0,
                    path,
                    path + ".bak");
                transaction = read?.Value;
                return transaction?.IsValid() == true &&
                       string.Equals(transaction.SessionId, sessionId, StringComparison.Ordinal) &&
                       transaction.PermitGeneration == permitGeneration &&
                       string.Equals(transaction.PermitId, permitId, StringComparison.Ordinal);
            }
            catch
            {
                transaction = null;
                return false;
            }
        }

        private static RecoveryReplacementAdvanceResult Failed(string reason) =>
            new RecoveryReplacementAdvanceResult
            {
                Succeeded = false,
                Reason = reason ?? "ReplacementTransactionFailed"
            };
    }
}
