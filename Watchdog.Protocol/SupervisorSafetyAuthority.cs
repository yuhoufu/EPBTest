using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Supervisor-owned schema 6 safety authority.  This is the only receipt
    /// that can authorize SafetyAgent execution.  Project receipts are
    /// evidence mirrors and are never read by this store.
    /// </summary>
    public sealed class SupervisorSafetyAuthorityRecord
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string AuthorityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public string ProjectDirectory { get; set; } = string.Empty;
        public long InitialReceiptRevision { get; set; }
        public string InitialReceiptCanonicalSha256 { get; set; } = string.Empty;
        public long ReceiptRevision { get; set; }
        public string ReceiptCanonicalSha256 { get; set; } = string.Empty;
        public WatchdogSafetyHandoffReceipt Receipt { get; set; }
        public long UpdatedUtcTicks { get; set; }

        public bool IsValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(AuthorityId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   InitialReceiptRevision > 0 && ReceiptRevision >= InitialReceiptRevision &&
                   RecoveryFailureReceipt.IsSha256(InitialReceiptCanonicalSha256) &&
                   RecoveryFailureReceipt.IsSha256(ReceiptCanonicalSha256) &&
                   Receipt != null && Receipt.IsValidFor(SessionId) &&
                   Receipt.SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Receipt.Revision == ReceiptRevision &&
                   string.Equals(Receipt.HandoffId, HandoffId, StringComparison.Ordinal) &&
                   string.Equals(
                       SupervisorSafetyAuthorityStore.ComputeReceiptSha256(Receipt),
                       ReceiptCanonicalSha256,
                       StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(ProjectDirectory);
        }
    }

    public sealed class SupervisorSafetyAuthorityToken
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string AuthorityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public long ReceiptRevision { get; set; }
        public string ReceiptCanonicalSha256 { get; set; } = string.Empty;

        public bool IsValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(AuthorityId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   PermitGeneration > 0 &&
                   Guid.TryParseExact(PermitId ?? string.Empty, "N", out parsed) &&
                   ReceiptRevision > 0 &&
                   RecoveryFailureReceipt.IsSha256(ReceiptCanonicalSha256);
        }
    }

    public static class SupervisorOriginalProcessIdentityPolicy
    {
        public static bool Matches(
            int registeredProcessId,
            long registeredProcessStartUtcTicks,
            int claimedProcessId,
            long claimedProcessStartUtcTicks)
        {
            return registeredProcessId > 0 &&
                   registeredProcessStartUtcTicks > 0 &&
                   claimedProcessId == registeredProcessId &&
                   claimedProcessStartUtcTicks ==
                       registeredProcessStartUtcTicks;
        }
    }

    public static class SupervisorSafetyAuthorityStore
    {
        private static JavaScriptSerializer Json => new JavaScriptSerializer();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static string ComputeAuthorityId(
            string sessionId,
            string handoffId,
            long permitGeneration,
            string permitId)
        {
            var hash = SupervisorProtocol.ComputeTextSha256(
                (sessionId ?? string.Empty) + "|" +
                (handoffId ?? string.Empty) + "|" +
                permitGeneration + "|" + (permitId ?? string.Empty));
            return hash.Substring(0, 32).ToLowerInvariant();
        }

        public static string GetPath(string stateDirectory, string authorityId)
        {
            Guid parsed;
            if (!Guid.TryParseExact(authorityId ?? string.Empty, "N", out parsed))
                throw new InvalidDataException("SupervisorSafetyAuthorityIdInvalid");
            var root = Path.GetFullPath(stateDirectory ?? string.Empty);
            return Path.Combine(root, "safety-authority-" + authorityId + ".v6.json");
        }

        public static string SerializeReceipt(WatchdogSafetyHandoffReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            // Reflection property order in .NET Framework is not a wire format.
            // Named-property lookups can warm that cache in a different order in each process.
            var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var property in typeof(WatchdogSafetyHandoffReceipt).GetProperties())
                if (property.CanRead && !Attribute.IsDefined(property, typeof(ScriptIgnoreAttribute)))
                    values.Add(property.Name, property.GetValue(receipt));
            return Json.Serialize(values);
        }

        public static string ComputeReceiptSha256(WatchdogSafetyHandoffReceipt receipt)
        {
            return SupervisorProtocol.ComputeTextSha256(SerializeReceipt(receipt));
        }

        public static SupervisorSafetyAuthorityRecord CreateOrRead(
            string stateDirectory,
            string projectDirectory,
            WatchdogSafetyHandoffReceipt receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            var authorityId = ComputeAuthorityId(
                receipt.SessionId,
                receipt.HandoffId,
                receipt.RelaunchPermitGeneration,
                receipt.RelaunchPermitId);
            var path = GetPath(stateDirectory, authorityId);
            using (var mutex = OpenAuthorityMutex(authorityId))
            {
                bool held;
                try { held = mutex.WaitOne(5000, false); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("SupervisorSafetyAuthorityBusy");
                try
                {
                    SupervisorSafetyAuthorityRecord existing;
                    string failure;
                    if (TryReadPath(path, out existing, out failure))
                    {
                        if (!SameInitialIdentity(existing, receipt))
                            throw new InvalidDataException(
                                "SupervisorSafetyAuthorityExistingIdentityMismatch");
                        return existing;
                    }
                    if (File.Exists(path))
                    {
                        if (!CanRecreateAfterCorruption(receipt))
                            throw new InvalidDataException(
                                "SupervisorSafetyAuthorityCorruptSafeIdleAlarmed:" + failure);
                        Quarantine(path);
                        Quarantine(path + ".bak");
                    }

                    var canonical = ComputeReceiptSha256(receipt);
                    var record = new SupervisorSafetyAuthorityRecord
                    {
                        AuthorityId = authorityId,
                        SessionId = receipt.SessionId,
                        HandoffId = receipt.HandoffId,
                        ProjectDirectory = Path.GetFullPath(projectDirectory),
                        InitialReceiptRevision = receipt.Revision,
                        InitialReceiptCanonicalSha256 = canonical,
                        ReceiptRevision = receipt.Revision,
                        ReceiptCanonicalSha256 = canonical,
                        Receipt = Clone(receipt),
                        UpdatedUtcTicks = DateTime.UtcNow.Ticks
                    };
                    Write(path, record);
                    MirrorEvidence(record);
                    return record;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        public static bool TryRead(
            string stateDirectory,
            string authorityId,
            out SupervisorSafetyAuthorityRecord record,
            out string failure)
        {
            record = null;
            failure = string.Empty;
            try
            {
                return TryReadPath(GetPath(stateDirectory, authorityId), out record, out failure);
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                return false;
            }
        }

        public static SupervisorSafetyAuthorityRecord Advance(
            string stateDirectory,
            string authorityId,
            long initialRevision,
            string initialCanonicalSha256,
            WatchdogSafetyHandoffReceipt next)
        {
            var path = GetPath(stateDirectory, authorityId);
            using (var mutex = OpenAuthorityMutex(authorityId))
            {
                bool held;
                try { held = mutex.WaitOne(5000, false); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("SupervisorSafetyAuthorityBusy");
                try
                {
                    SupervisorSafetyAuthorityRecord current;
                    string failure;
                    if (!TryReadPath(path, out current, out failure))
                        throw new InvalidDataException(
                            "SupervisorSafetyAuthorityReadFailed:" + failure);
                    if (current.InitialReceiptRevision != initialRevision)
                        throw new InvalidDataException(
                            "SupervisorSafetyAuthorityInitialRevisionMismatch");
                    if (!string.Equals(
                            current.InitialReceiptCanonicalSha256,
                            initialCanonicalSha256,
                            StringComparison.Ordinal))
                        throw new InvalidDataException(
                            "SupervisorSafetyAuthorityInitialCanonicalHashMismatch");
                    ValidateTransition(current.Receipt, next);
                    current.Receipt = Clone(next);
                    current.ReceiptRevision = next.Revision;
                    current.ReceiptCanonicalSha256 = ComputeReceiptSha256(next);
                    current.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    Write(path, current);
                    MirrorEvidence(current);
                    return current;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static void ValidateTransition(
            WatchdogSafetyHandoffReceipt previous,
            WatchdogSafetyHandoffReceipt next)
        {
            if (previous == null || next == null || !next.IsValidFor(previous.SessionId))
                throw new InvalidDataException("SupervisorSafetyAuthorityReceiptInvalid");
            if (next.SchemaVersion != SupervisorProtocol.SchemaVersion)
                throw new InvalidDataException("SupervisorSafetyAuthoritySchemaMismatch");
            var identityDifferences = DescribeIdentityDifferences(previous, next);
            if (!string.IsNullOrEmpty(identityDifferences))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityImmutableIdentityMismatch: " + identityDifferences);
            if (next.Revision <= previous.Revision || next.Stage < previous.Stage ||
                next.State < previous.State || previous.IsTerminal)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityRevisionOrStateMismatch");
        }

        public static string DescribeIdentityDifferences(
            WatchdogSafetyHandoffReceipt expected, WatchdogSafetyHandoffReceipt actual)
        {
            var fields = new[] { "SessionId", "SessionGeneration", "SessionLease", "HandoffId", "Nonce",
                "StopSafetyTransactionId", "RunId", "RunEpoch", "RelaunchDisposition",
                "RelaunchPermitGeneration", "RelaunchPermitId", "RelaunchPermitNonceSha256",
                "ProjectDirectory", "MainExecutablePath", "MainExecutableSha256",
                "SafetyAgentExecutablePath", "SafetyAgentExecutableSha256",
                "ConfigSnapshotPath", "ConfigSnapshotManifestPath", "ConfigSnapshotManifestSha256" };
            // Field names identify the failed binding without exposing the capability nonce.
            return string.Join(",", fields.Where(name =>
            {
                var property = typeof(WatchdogSafetyHandoffReceipt).GetProperty(name);
                return !Equals(property.GetValue(expected), property.GetValue(actual));
            }));
        }

        private static bool TryReadPath(
            string path,
            out SupervisorSafetyAuthorityRecord record,
            out string failure)
        {
            record = null;
            failure = string.Empty;
            try
            {
                if (!File.Exists(path))
                {
                    failure = "AuthorityMissing";
                    return false;
                }
                record = Json.Deserialize<SupervisorSafetyAuthorityRecord>(
                    File.ReadAllText(path, Utf8));
                if (record?.IsValid() != true)
                {
                    failure = "AuthorityInvalid";
                    if (record?.Receipt == null) failure += ":ReceiptMissing";
                    else
                    {
                        var actual = ComputeReceiptSha256(record.Receipt);
                        if (!string.Equals(actual, record.ReceiptCanonicalSha256, StringComparison.Ordinal))
                            failure += ":ReceiptCanonicalSha256Mismatch Expected=" + record.ReceiptCanonicalSha256 + " Actual=" + actual;
                        else
                            failure += ":Contract Schema=" + record.SchemaVersion +
                                " Session=" + record.SessionId + " Handoff=" + record.HandoffId +
                                " InitialRevision=" + record.InitialReceiptRevision +
                                " RecordRevision=" + record.ReceiptRevision + " ReceiptRevision=" + record.Receipt.Revision +
                                " ReceiptContractValid=" + record.Receipt.IsValidFor(record.SessionId);
                    }
                    record = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                record = null;
                return false;
            }
        }

        private static void Write(string path, SupervisorSafetyAuthorityRecord record)
        {
            if (record?.IsValid() != true)
                throw new InvalidDataException("SupervisorSafetyAuthorityWriteInvalid");
            DurableJsonFileStore.WriteAtomicWithBackup(
                path,
                Utf8.GetBytes(Json.Serialize(record)));
            SupervisorSafetyAuthorityRecord verified;
            string failure;
            if (!TryReadPath(path, out verified, out failure) ||
                !string.Equals(verified.ReceiptCanonicalSha256,
                    record.ReceiptCanonicalSha256, StringComparison.Ordinal))
                throw new IOException(
                    "SupervisorSafetyAuthorityWriteReadbackFailed:" + failure);
        }

        private static void MirrorEvidence(SupervisorSafetyAuthorityRecord record)
        {
            // Compatibility evidence for the sidecar and incident archive only.
            // It is deliberately overwritten from the Supervisor authority and
            // never participates in revision arbitration or authorization.
            var path = WatchdogJournalPaths.ProjectSafetyHandoffPath(
                record.ProjectDirectory,
                record.SessionId);
            DurableJsonFileStore.WriteAtomicWithBackup(
                path,
                Utf8.GetBytes(Json.Serialize(record.Receipt)));
            var evidenceDirectory = Path.Combine(
                record.ProjectDirectory,
                "SupervisorEvidence");
            var authorityEvidencePath = Path.Combine(
                evidenceDirectory,
                "safety-authority-" + record.AuthorityId + ".v6.json");
            DurableJsonFileStore.WriteAtomicWithBackup(
                authorityEvidencePath,
                Utf8.GetBytes(Json.Serialize(record)));
        }

        private static bool SameInitialIdentity(
            SupervisorSafetyAuthorityRecord record,
            WatchdogSafetyHandoffReceipt receipt)
        {
            return record != null && receipt != null &&
                   string.Equals(record.SessionId, receipt.SessionId, StringComparison.Ordinal) &&
                   string.Equals(record.HandoffId, receipt.HandoffId, StringComparison.Ordinal) &&
                   record.Receipt.RelaunchPermitGeneration ==
                       receipt.RelaunchPermitGeneration &&
                   string.Equals(record.Receipt.RelaunchPermitId,
                       receipt.RelaunchPermitId, StringComparison.Ordinal) &&
                   string.Equals(record.Receipt.Nonce, receipt.Nonce,
                       StringComparison.Ordinal);
        }

        private static bool CanRecreateAfterCorruption(
            WatchdogSafetyHandoffReceipt receipt)
        {
            return receipt != null && receipt.CrashRecovery &&
                   receipt.OldProcessExitProven && receipt.OldProcessId > 0 &&
                   receipt.OldProcessStartUtcTicks > 0 &&
                   receipt.OldProcessExitEvidenceOwner ==
                       WatchdogSafetyEvidenceOwner.SupervisorService &&
                   (receipt.OldProcessObservation ==
                        DurableRelaunchProcessObservation.Dead ||
                    receipt.OldProcessObservation ==
                        DurableRelaunchProcessObservation.IdentityMismatch);
        }

        private static void Quarantine(string path)
        {
            if (!File.Exists(path)) return;
            var target = path + ".corrupt-" +
                         DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                         Guid.NewGuid().ToString("N");
            File.Move(path, target);
        }

        private static string MutexName(string authorityId)
        {
            return "Global\\MTTFTest.SupervisorSafetyAuthority.V216." + authorityId;
        }

        private static Mutex OpenAuthorityMutex(string authorityId)
        {
            var name = MutexName(authorityId);
            const MutexRights usage = MutexRights.Synchronize | MutexRights.Modify;
            try { return Mutex.OpenExisting(name, usage); }
            catch (WaitHandleCannotBeOpenedException) { }
            var security = new MutexSecurity();
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(sid, null), MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), usage, AccessControlType.Allow));
            try { return new Mutex(false, name, out _, security); }
            catch (UnauthorizedAccessException) { return Mutex.OpenExisting(name, usage); }
        }

        private static WatchdogSafetyHandoffReceipt Clone(
            WatchdogSafetyHandoffReceipt value)
        {
            return Json.Deserialize<WatchdogSafetyHandoffReceipt>(
                Json.Serialize(value));
        }
    }
}
