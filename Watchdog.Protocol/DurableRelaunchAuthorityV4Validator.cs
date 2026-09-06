using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Validator/migrator for the authority namespace.  The older
    /// WatchdogJournalV4Validator remains a legacy journal validator and is
    /// never used by the strict authority store.
    /// </summary>
    public static class DurableRelaunchAuthorityV4Validator
    {
        public const string RequiredRecordKind = "DurableRelaunchAuthority";
        public const int RequiredFormatRevision = 2;
        public const int LegacyFormatRevision = 1;

        public static DurableRelaunchAuthorityRecord CreatePristine(string sessionId, int budget = 3, string marker = "schema4-session-bootstrap-v1")
        {
            return new DurableRelaunchAuthorityRecord
            {
                SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion, RecordKind = RequiredRecordKind, RecordFormatRevision = RequiredFormatRevision,
                AuthorityRevision = 0, SessionId = sessionId, State = DurableRelaunchPermitState.None,
                MaximumProcessRelaunches = Math.Max(1, budget), BootstrapMarker = marker
            };
        }

        public static DurableRelaunchAuthorityRecord CreateBlocked(string sessionId, string reason, DurableRelaunchAuthorityRecord source = null)
        {
            var record = source?.Clone() ?? new DurableRelaunchAuthorityRecord();
            record.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion; record.RecordKind = RequiredRecordKind; record.RecordFormatRevision = RequiredFormatRevision;
            record.SessionId = sessionId; record.State = DurableRelaunchPermitState.Blocked; record.CircuitOpen = true;
            record.DetailCode = (reason ?? "AuthorityBlocked").Trim();
            if (!string.IsNullOrEmpty(record.LastFailureCanonicalSha256))
                record.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
            return record;
        }

        public static bool TryValidateRecord(DurableRelaunchAuthorityRecord record, string expectedSessionId, out string reason)
        {
            reason = null;
            if (record == null) { reason = "RecordMissing"; return false; }
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(expectedSessionId)) { reason = "ExpectedSessionNotN"; return false; }
            if (record.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion) { reason = "SchemaMismatch"; return false; }
            if (!string.Equals(record.RecordKind, RequiredRecordKind, StringComparison.Ordinal)) { reason = "RecordKindMismatch"; return false; }
            if (record.RecordFormatRevision != RequiredFormatRevision) { reason = "RecordFormatRevisionMismatch"; return false; }
            if (!string.Equals(record.SessionId, expectedSessionId, StringComparison.Ordinal)) { reason = "SessionMismatch"; return false; }
            if (record.AuthorityRevision < 0 || record.Generation < 0 || record.ConsecutiveFailures < 0 || record.MaximumProcessRelaunches <= 0 || record.MaximumProcessRelaunches > 1000000 ||
                record.ProcessId < 0 || record.ProcessStartUtcTicks < 0 || record.ConnectionGeneration < 0 || record.RecoveryAttemptGeneration < 0 ||
                record.RunEpoch < 0 || record.LastFailureRunEpoch < 0 ||
                record.RecoveryCommitGeneration < 0 || record.LastFailureDecisionSequence < 0 || record.LastFailureDecisionUtcTicks < 0)
            { reason = "NegativeAuthorityField"; return false; }
            if (!HasProof(record)) { reason = "BootstrapProofMissing"; return false; }
            if (!Enum.IsDefined(typeof(DurableRelaunchPermitState), record.State)) { reason = "StateInvalid"; return false; }
            // State is a closed wire contract.  Keep required and forbidden
            // fields together in one switch so a newly added state cannot
            // accidentally inherit a permissive scattered condition.
            if (!ValidateState(record, expectedSessionId, out reason)) return false;
            if (!RecoveryFailureDispositions.IsDefined(record.LastFailureDisposition) &&
                !string.IsNullOrEmpty(record.LastFailureDisposition))
            { reason = "DispositionInvalid"; return false; }
            var hasCanonical = !string.IsNullOrEmpty(record.LastFailureCanonicalSha256);
            if (hasCanonical ? !HasFailureEvidence(record) : HasAnyFailureEvidence(record))
            { reason = "PartialFailureEvidence"; return false; }
            if (!string.IsNullOrEmpty(record.LastFailureCanonicalSha256) && !RecoveryFailureReceipt.IsSha256(record.LastFailureCanonicalSha256)) { reason = "LastCanonicalInvalid"; return false; }
            if (!string.IsNullOrEmpty(record.LastFailureCanonicalSha256))
            {
                string recomputed;
                if (!TryCanonicalFromRecord(record, out recomputed) || !string.Equals(recomputed, record.LastFailureCanonicalSha256, StringComparison.Ordinal))
                { reason = "LastCanonicalMismatch"; return false; }
                if (!RecoveryFailureReceipt.IsValidCorrelation(record.LastFailureCorrelationId) ||
                    !RecoveryFailureReceipt.IsSha256(record.LastFailurePayloadSha256) ||
                    !HasToken(record.LastFailureOperationId) || !HasToken(record.LastFailureCode) ||
                    !HasToken(record.LastFailureFingerprint) || !HasToken(record.LastFailureDetailCode) ||
                    record.LastFailureDecisionSequence <= 0 || record.LastFailureDecisionUtcTicks <= 0)
                { reason = "FailureEvidenceIncomplete"; return false; }
            }
            return true;
        }

        private static bool ValidateState(DurableRelaunchAuthorityRecord record, string expectedSessionId, out string reason)
        {
            reason = null;
            bool permit = record.Generation > 0 && HasToken(record.PermitId) && HasToken(record.PermitNonce);
            bool evidence = HasFailureEvidence(record);
            bool pendingDisposition = string.Equals(record.LastFailureDisposition, RecoveryFailureDispositions.RelaunchApproved, StringComparison.Ordinal) ||
                                      string.Equals(record.LastFailureDisposition, RecoveryFailureDispositions.RelaunchAlreadyPending, StringComparison.Ordinal);
            switch (record.State)
            {
                case DurableRelaunchPermitState.None:
                    if (!record.IsPristineNone(expectedSessionId)) reason = "NoneNotPristine";
                    break;
                case DurableRelaunchPermitState.Approved:
                case DurableRelaunchPermitState.LaunchIntent:
                    // Before a sidecar is started no process identity or
                    // commit generation may be present.
                    if (record.CircuitOpen || !permit || !evidence || record.ProcessId != 0 ||
                        record.ProcessStartUtcTicks != 0 || record.RecoveryCommitGeneration != 0 || !pendingDisposition)
                        reason = "PermitIdentityIncomplete";
                    else if (!ValidateActiveEvidenceBinding(
                                 record, expectedSessionId, false, false, out reason))
                        return false;
                    else if (record.State == DurableRelaunchPermitState.LaunchIntent && !HasLaunchIntent(record))
                        reason = "LaunchIntentIncomplete";
                    else if (record.State == DurableRelaunchPermitState.Approved && HasAnyLaunchIntent(record))
                        reason = "ApprovedContainsLaunchIntent";
                    break;
                case DurableRelaunchPermitState.Started:
                case DurableRelaunchPermitState.Attached:
                    if (record.CircuitOpen || !permit || !evidence || record.ProcessId <= 0 ||
                        record.ProcessStartUtcTicks <= 0 || record.ConnectionGeneration <= 0 ||
                        record.RecoveryAttemptGeneration <= 0 || record.RecoveryCommitGeneration != 0 ||
                        !pendingDisposition || !record.LaunchConsumed)
                        reason = "ProcessIdentityIncomplete";
                    else if (!ValidateActiveEvidenceBinding(
                                 record, expectedSessionId, true, false, out reason))
                        return false;
                    else if (!HasLaunchIntent(record))
                        reason = "StartedLaunchIntentMissing";
                    break;
                case DurableRelaunchPermitState.Committed:
                    if (record.CircuitOpen || !permit || !evidence || record.RecoveryCommitGeneration <= 0 ||
                        string.IsNullOrEmpty(record.RunId) || record.RunEpoch <= 0 ||
                        string.IsNullOrEmpty(record.RecoveryStage) || string.IsNullOrEmpty(record.RecoveryProgressToken) ||
                        !record.LaunchConsumed)
                        reason = "CommittedEvidenceIncomplete";
                    else if (!ValidateActiveEvidenceBinding(
                                 record, expectedSessionId, true, true, out reason))
                        return false;
                    else if (!HasLaunchIntent(record))
                        reason = "CommittedLaunchIntentMissing";
                    break;
                case DurableRelaunchPermitState.Failed:
                    if (record.CircuitOpen || !evidence) reason = "FailedEvidenceIncomplete";
                    break;
                case DurableRelaunchPermitState.RejectedNoWork:
                    if (record.CircuitOpen || !permit || !evidence ||
                        !HasToken(record.DetailCode) || record.ProcessId != 0 ||
                        record.ProcessStartUtcTicks != 0 ||
                        record.RecoveryCommitGeneration != 0 || HasAnyLaunchIntent(record))
                        reason = "RejectedNoWorkEvidenceInvalid";
                    else if (!ValidateActiveEvidenceBinding(
                                 record, expectedSessionId, false, false, out reason))
                        return false;
                    break;
                case DurableRelaunchPermitState.Blocked:
                case DurableRelaunchPermitState.Revoked:
                    if (!record.CircuitOpen) reason = record.State == DurableRelaunchPermitState.Blocked ? "BlockedWithoutCircuit" : "RevokedWithoutCircuit";
                    else if (evidence &&
                             !string.Equals(record.LastFailureDisposition, RecoveryFailureDispositions.CircuitOpen, StringComparison.Ordinal) &&
                             !string.Equals(record.LastFailureDisposition, RecoveryFailureDispositions.IdentityConflict, StringComparison.Ordinal) &&
                             !string.Equals(record.LastFailureDisposition, RecoveryFailureDispositions.Superseded, StringComparison.Ordinal))
                        reason = "BlockedDispositionMismatch";
                    break;
                default:
                    reason = "StateInvalid";
                    break;
            }
            if (reason != null) return false;
            if (!RecoveryFailureDispositions.IsDefined(record.LastFailureDisposition) && !string.IsNullOrEmpty(record.LastFailureDisposition))
            { reason = "DispositionInvalid"; return false; }
            return true;
        }

        /// <summary>
        /// The mutable current identity and the frozen LastFailure evidence
        /// are two projections of one active transaction.  They must agree
        /// on the session/attempt/run identity.  Pre-process states retain a
        /// deliberately empty current PID/start pair; Started/Attached must
        /// bind that pair exactly as well.  This prevents a hand-edited
        /// current connection/attempt from reopening an otherwise valid
        /// authority with stale failure evidence.
        /// </summary>
        private static bool ValidateActiveEvidenceBinding(
            DurableRelaunchAuthorityRecord record,
            string expectedSessionId,
            bool requiresProcess,
            bool allowRecoveredContext,
            out string reason)
        {
            reason = null;
            if (record == null || !HasFailureEvidence(record))
            {
                reason = "ActiveEvidenceMissing";
                return false;
            }
            if (!string.Equals(record.SessionId, expectedSessionId, StringComparison.Ordinal))
            {
                reason = "ActiveSessionMismatch";
                return false;
            }
            if (record.ConnectionGeneration <= 0 ||
                record.ConnectionGeneration != record.LastFailureConnectionGeneration ||
                record.RecoveryAttemptGeneration <= 0 ||
                record.RecoveryAttemptGeneration != record.LastFailureAttemptGeneration)
            {
                reason = "ActiveConnectionAttemptMismatch";
                return false;
            }
            if (record.Generation <= 0 || !HasToken(record.PermitId) || !HasToken(record.PermitNonce))
            {
                reason = "ActivePermitIdentityMissing";
                return false;
            }
            if (record.LastFailurePermitGeneration != record.Generation ||
                !string.Equals(record.LastFailurePermitId, record.PermitId, StringComparison.Ordinal) ||
                !string.Equals(record.LastFailurePermitNonce, record.PermitNonce, StringComparison.Ordinal))
            {
                reason = "ActivePermitIdentityMismatch";
                return false;
            }
            if (!allowRecoveredContext && !string.IsNullOrEmpty(record.LastFailureRunId) &&
                (!string.Equals(record.RunId, record.LastFailureRunId, StringComparison.Ordinal) ||
                 record.RunEpoch != record.LastFailureRunEpoch ||
                 !string.Equals(record.RecoveryStage, record.LastFailureRecoveryStage, StringComparison.Ordinal) ||
                 !string.Equals(record.RecoveryProgressToken, record.LastFailureRecoveryProgressToken, StringComparison.Ordinal) ||
                 !string.Equals(record.RecoveryProcessSource, record.LastFailureRecoveryProcessSource, StringComparison.Ordinal) ||
                 !string.Equals(record.DeviceOrChannelGroup, record.LastFailureDeviceOrChannelGroup, StringComparison.Ordinal)))
            {
                reason = "ActiveRecoveryContextMismatch";
                return false;
            }
            // LastFailureProcess* identifies the sidecar/reporting process
            // which registered the failure.  Process* identifies the newly
            // launched main process after CommitStarted.  They are distinct
            // authorities and normally have different PIDs/start ticks; each
            // must be complete, but equality would make every real relaunch
            // impossible.
            if (requiresProcess)
            {
                if (record.ProcessId <= 0 || record.ProcessStartUtcTicks <= 0 ||
                    record.LastFailureProcessId <= 0 || record.LastFailureProcessStartUtcTicks <= 0)
                {
                    reason = "ActiveProcessIdentityIncomplete";
                    return false;
                }
            }
            else if (record.ProcessId != 0 || record.ProcessStartUtcTicks != 0 ||
                     record.LastFailureProcessId <= 0 || record.LastFailureProcessStartUtcTicks <= 0)
            {
                reason = "PreProcessIdentityMismatch";
                return false;
            }
            return true;
        }

        public static DurableAuthorityValidationResult ValidateAndMigrate(string json, string expectedSessionId)
        {
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(expectedSessionId))
                return Blocked("ExpectedSessionNotN", DurableAuthorityFailureKind.Invalid);
            if (string.IsNullOrWhiteSpace(json)) return Blocked("MissingOrEmpty", DurableAuthorityFailureKind.Missing);
            Dictionary<string, object> map;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = Math.Max(1024, json.Length + 16) };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                map = root;
                if (map == null) return Blocked("InvalidJsonRoot", DurableAuthorityFailureKind.Corrupt);
            }
            catch (Exception ex) { return Blocked("InvalidJson:" + ex.GetType().Name, DurableAuthorityFailureKind.Corrupt); }

            var schema = Int(map, "SchemaVersion", 0);
            var session = String(map, "SessionId");
            if (!string.Equals(session, expectedSessionId, StringComparison.Ordinal)) return Blocked("SessionMismatch", DurableAuthorityFailureKind.SessionMismatch);
            if (schema == 2 || schema == 3)
            {
                var legacy = FromMap(map, expectedSessionId, schema, false);
                if (legacy == null) return Blocked("LegacyInvalid", DurableAuthorityFailureKind.Invalid);
                if (IsLegacyPristine(legacy))
                {
                    legacy.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion; legacy.RecordKind = RequiredRecordKind; legacy.RecordFormatRevision = RequiredFormatRevision;
                    legacy.AuthorityRevision = 0; legacy.MaximumProcessRelaunches = Math.Max(1, legacy.MaximumProcessRelaunches);
                    return new DurableAuthorityValidationResult { Record = legacy, Migrated = true, Proven = true, Reason = "LegacyPristine" };
                }
                return new DurableAuthorityValidationResult { Record = CreateBlocked(expectedSessionId, "LegacyAmbiguous"), Migrated = true, Blocked = true, Proven = false, Reason = "LegacyAmbiguous" };
            }
            if (schema != 4 && schema != WatchdogJournalPolicy.CurrentSchemaVersion)
                return Blocked("SchemaMismatch", DurableAuthorityFailureKind.Invalid);

            var migratedFromV4 = schema == 4;
            var record = FromMap(map, expectedSessionId, schema, true);
            if (record == null) return Blocked("RecordInvalid", DurableAuthorityFailureKind.Invalid);
            record.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
            var kind = String(map, "RecordKind");
            var format = Int(map, "RecordFormatRevision", 0);
            // Only an actual v4 record may enter the conservative legacy
            // migration.  A schema 5 record missing its identity/format is a
            // damaged current authority and must fail closed; treating it as
            // old-v4 would manufacture a pristine launch authority.
            var oldV4 = migratedFromV4 &&
                        (string.IsNullOrEmpty(kind) || format == 0);
            if (oldV4)
            {
                if (record.State == DurableRelaunchPermitState.None && IsLegacyPristine(record))
                {
                    record.RecordKind = RequiredRecordKind; record.RecordFormatRevision = RequiredFormatRevision; record.AuthorityRevision = 0;
                    return new DurableAuthorityValidationResult { Record = record, Migrated = true, Proven = true, Reason = "OldV4Pristine" };
                }
                if (record.State == DurableRelaunchPermitState.Blocked)
                {
                    record.RecordKind = RequiredRecordKind; record.RecordFormatRevision = RequiredFormatRevision;
                    return new DurableAuthorityValidationResult { Record = CreateBlocked(expectedSessionId, "LegacyBlocked", record), Migrated = true, Blocked = true, Reason = "LegacyBlocked" };
                }
                if (record.State == DurableRelaunchPermitState.Failed &&
                    RecoveryFailureReceipt.IsSha256(record.LastFailureCanonicalSha256) &&
                    !string.IsNullOrEmpty(record.LastFailureCorrelationId))
                {
                    record.RecordKind = RequiredRecordKind; record.RecordFormatRevision = RequiredFormatRevision;
                    return new DurableAuthorityValidationResult { Record = record, Migrated = true, Proven = true, Reason = "OldV4Failed" };
                }
                // Old-v4 Committed did not carry the format-2 launch-consume
                // proof or the complete launch intent.  It is terminal from
                // the legacy process' point of view, but it is not safe to
                // reopen as a proven format-2 authority.  Persist a blocked
                // migration instead of manufacturing evidence that never
                // existed.
                return new DurableAuthorityValidationResult { Record = CreateBlocked(expectedSessionId, "OldV4Ambiguous", record), Migrated = true, Blocked = true, Reason = "OldV4Ambiguous" };
            }
            if (!string.Equals(kind, RequiredRecordKind, StringComparison.Ordinal) ||
                (format != RequiredFormatRevision && format != LegacyFormatRevision))
                return Blocked("RecordIdentityMismatch", DurableAuthorityFailureKind.Invalid);

            // Format 1 never contained a durable launch-intent identity.  A
            // pristine record can safely be upgraded; every active permit is
            // ambiguous and is durably migrated to Blocked by the file store.
            if (format == LegacyFormatRevision)
            {
                if (record.State == DurableRelaunchPermitState.None && IsLegacyPristine(record))
                {
                    record.RecordKind = RequiredRecordKind;
                    record.RecordFormatRevision = RequiredFormatRevision;
                    return new DurableAuthorityValidationResult { Record = record, Migrated = true, Proven = true, Reason = "Format1Pristine" };
                }
                return new DurableAuthorityValidationResult
                {
                    Record = CreateBlocked(expectedSessionId, "Format1ActiveMigratedBlocked", record),
                    Migrated = true, Blocked = true, Proven = false,
                    Reason = "Format1ActiveMigratedBlocked"
                };
            }

            string validation;
            if (TryValidateRecord(record, expectedSessionId, out validation))
                return new DurableAuthorityValidationResult
                {
                    Record = record,
                    Migrated = migratedFromV4,
                    Proven = true,
                    Reason = migratedFromV4 ? "V4ReadOnlyMigratedToV5" : "Valid"
                };

            // Current schema records with an ambiguous in-flight state must
            // remain fail-closed rather than being silently normalized.
            return new DurableAuthorityValidationResult { Record = CreateBlocked(expectedSessionId, validation ?? "RecordInvalid", record), Blocked = true, Proven = false, Reason = validation ?? "RecordInvalid" };
        }

        public static string Serialize(DurableRelaunchAuthorityRecord record)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            return serializer.Serialize(record);
        }

        private static DurableAuthorityValidationResult Blocked(string reason, DurableAuthorityFailureKind kind) => new DurableAuthorityValidationResult
        {
            Record = null, Blocked = true, Proven = false, Reason = reason, FailureKind = kind
        };

        private static bool IsLegacyPristine(DurableRelaunchAuthorityRecord record)
        {
            if (record == null || record.State == DurableRelaunchPermitState.Blocked || record.CircuitOpen) return false;
            return record.MaximumProcessRelaunches > 0 && record.Generation == 0 && string.IsNullOrEmpty(record.PermitId) && string.IsNullOrEmpty(record.PermitNonce) &&
                   record.ConsecutiveFailures == 0 && record.ProcessId == 0 && record.ProcessStartUtcTicks == 0 &&
                   record.ConnectionGeneration == 0 && record.RecoveryAttemptGeneration == 0 &&
                   record.RecoveryCommitGeneration == 0 && record.RunEpoch == 0 &&
                   string.IsNullOrEmpty(record.LastFailureCanonicalSha256) && string.IsNullOrEmpty(record.LastFailureCorrelationId) &&
                   string.IsNullOrEmpty(record.LastFailureOperationId) && string.IsNullOrEmpty(record.LastFailurePayloadSha256) &&
                   string.IsNullOrEmpty(record.LastFailureCode) && string.IsNullOrEmpty(record.LastFailureDetailCode) && string.IsNullOrEmpty(record.LastFailureFingerprint) &&
                   string.IsNullOrEmpty(record.LastFailureRunId) && record.LastFailureRunEpoch == 0 &&
                   string.IsNullOrEmpty(record.LastFailureRecoveryStage) &&
                   string.IsNullOrEmpty(record.LastFailureRecoveryProgressToken) &&
                   string.IsNullOrEmpty(record.LastFailureRecoveryProcessSource) &&
                   string.IsNullOrEmpty(record.LastFailureDeviceOrChannelGroup) &&
                   record.LastFailureProcessId == 0 && record.LastFailureProcessStartUtcTicks == 0 &&
                   record.LastFailureConnectionGeneration == 0 && record.LastFailureAttemptGeneration == 0 &&
                   record.LastFailurePermitGeneration == 0 && string.IsNullOrEmpty(record.LastFailurePermitId) &&
                   string.IsNullOrEmpty(record.LastFailurePermitNonce) &&
                   record.LastFailureDecisionSequence == 0 && record.LastFailureDecisionUtcTicks == 0 && !record.LastFailurePermanent &&
                   string.IsNullOrEmpty(record.RunId) && string.IsNullOrEmpty(record.RecoveryStage) &&
                   string.IsNullOrEmpty(record.RecoveryProgressToken) && string.IsNullOrEmpty(record.RecoveryProcessSource) &&
                   string.IsNullOrEmpty(record.DeviceOrChannelGroup) && string.IsNullOrEmpty(record.DetailCode) &&
                   string.IsNullOrEmpty(record.BootstrapMarker) && string.IsNullOrEmpty(record.BootstrapPrimaryPath) &&
                   string.IsNullOrEmpty(record.BootstrapPrimarySha256) && string.IsNullOrEmpty(record.BootstrapProofPath) &&
                   string.IsNullOrEmpty(record.BootstrapProofSha256) && string.IsNullOrEmpty(record.BootstrapProofMarker) &&
                   record.BootstrapMaximumProcessRelaunches == 0;
        }

        private static DurableRelaunchAuthorityRecord FromMap(Dictionary<string, object> map, string sessionId, int schema, bool strict)
        {
            try
            {
                var record = new DurableRelaunchAuthorityRecord
                {
                    SchemaVersion = schema,
                    RecordKind = String(map, "RecordKind"),
                    RecordFormatRevision = Int(map, "RecordFormatRevision", 0),
                    AuthorityRevision = Long(map, "AuthorityRevision", 0), SessionId = sessionId,
                    State = State(map, "State", map.ContainsKey("RelaunchState") ? String(map, "RelaunchState") : null),
                    Generation = Long(map, "Generation", Long(map, "RelaunchGeneration", 0)),
                    PermitId = String(map, "PermitId"), PermitNonce = String(map, "PermitNonce"),
                    ConsecutiveFailures = Int(map, "ConsecutiveFailures", 0), MaximumProcessRelaunches = Int(map, "MaximumProcessRelaunches", Int(map, "RelaunchBudget", 1)),
                    ProcessId = Int(map, "ProcessId", Int(map, "CurrentPid", 0)), ProcessStartUtcTicks = Long(map, "ProcessStartUtcTicks", Long(map, "CurrentProcessStartUtcTicks", 0)),
                    ConnectionGeneration = Long(map, "ConnectionGeneration", 0), RecoveryAttemptGeneration = Long(map, "RecoveryAttemptGeneration", 0),
                    RecoveryCommitGeneration = Long(map, "RecoveryCommitGeneration", 0),
                    RunId = String(map, "RunId"), RunEpoch = Long(map, "RunEpoch", 0), RecoveryStage = String(map, "RecoveryStage"),
                    RecoveryProgressToken = String(map, "RecoveryProgressToken") ?? String(map, "ProgressToken"), RecoveryProcessSource = String(map, "RecoveryProcessSource") ?? String(map, "ProcessSource"),
                    DeviceOrChannelGroup = String(map, "DeviceOrChannelGroup"), LastFailureOperationId = String(map, "LastFailureOperationId"),
                    LastFailureCorrelationId = String(map, "LastFailureCorrelationId"), LastFailurePayloadSha256 = String(map, "LastFailurePayloadSha256"),
                    LastFailureCanonicalSha256 = String(map, "LastFailureCanonicalSha256"), LastFailureFingerprint = String(map, "LastFailureFingerprint"),
                    LastFailureCode = String(map, "LastFailureCode"), LastFailureDetailCode = String(map, "LastFailureDetailCode"), LastFailureSessionNonce = String(map, "LastFailureSessionNonce"),
                    LastFailureRunId = String(map, "LastFailureRunId"), LastFailureRunEpoch = Long(map, "LastFailureRunEpoch", 0),
                    LastFailureRecoveryStage = String(map, "LastFailureRecoveryStage"),
                    LastFailureRecoveryProgressToken = String(map, "LastFailureRecoveryProgressToken"),
                    LastFailureRecoveryProcessSource = String(map, "LastFailureRecoveryProcessSource"),
                    LastFailureDeviceOrChannelGroup = String(map, "LastFailureDeviceOrChannelGroup"),
                    LastFailureProcessId = Int(map, "LastFailureProcessId", 0), LastFailureProcessStartUtcTicks = Long(map, "LastFailureProcessStartUtcTicks", 0),
                    LastFailureConnectionGeneration = Long(map, "LastFailureConnectionGeneration", 0), LastFailureAttemptGeneration = Long(map, "LastFailureAttemptGeneration", 0),
                    LastFailurePermitGeneration = Long(map, "LastFailurePermitGeneration", 0), LastFailurePermitId = String(map, "LastFailurePermitId"),
                    LastFailurePermitNonce = String(map, "LastFailurePermitNonce"),
                    LastFailureDisposition = String(map, "LastFailureDisposition"), LastFailureDecisionSequence = Long(map, "LastFailureDecisionSequence", 0),
                    LastFailureDecisionUtcTicks = Long(map, "LastFailureDecisionUtcTicks", 0), LastFailurePermanent = Bool(map, "LastFailurePermanent", false),
                    CircuitOpen = Bool(map, "CircuitOpen", Bool(map, "RecoveryBlocked", false)), DetailCode = String(map, "DetailCode"),
                    BootstrapMarker = String(map, "BootstrapMarker")
                    ,BootstrapPrimaryPath = String(map, "BootstrapPrimaryPath")
                    ,BootstrapPrimarySha256 = String(map, "BootstrapPrimarySha256")
                    ,BootstrapProofPath = String(map, "BootstrapProofPath")
                    ,BootstrapProofSha256 = String(map, "BootstrapProofSha256")
                    ,BootstrapProofMarker = String(map, "BootstrapProofMarker")
                    ,BootstrapMaximumProcessRelaunches = Int(map, "BootstrapMaximumProcessRelaunches", 0)
                    ,LaunchIntentId = String(map, "LaunchIntentId")
                    ,LaunchExecutablePath = String(map, "LaunchExecutablePath")
                    ,LaunchExecutableSha256 = String(map, "LaunchExecutableSha256")
                    ,LaunchArguments = String(map, "LaunchArguments")
                    ,LaunchWorkingDirectory = String(map, "LaunchWorkingDirectory")
                    ,LaunchOptionsCanonical = String(map, "LaunchOptionsCanonical")
                    ,LaunchSpecSha256 = String(map, "LaunchSpecSha256")
                    ,LaunchAuthorityRevision = Long(map, "LaunchAuthorityRevision", 0)
                    ,LaunchAuthoritySha256 = String(map, "LaunchAuthoritySha256")
                    ,LaunchConsumed = Bool(map, "LaunchConsumed", false)
                };
                if (!strict && record.MaximumProcessRelaunches <= 0) record.MaximumProcessRelaunches = 1;
                return record;
            }
            catch { return null; }
        }

        private static string String(Dictionary<string, object> map, string key) => map.ContainsKey(key) && map[key] != null ? Convert.ToString(map[key], CultureInfo.InvariantCulture) : null;
        private static int Int(Dictionary<string, object> map, string key, int fallback) { long value = Long(map, key, fallback); return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value; }
        private static long Long(Dictionary<string, object> map, string key, long fallback) { if (!map.ContainsKey(key) || map[key] == null) return fallback; long value; return long.TryParse(Convert.ToString(map[key], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback; }
        private static bool Bool(Dictionary<string, object> map, string key, bool fallback) { if (!map.ContainsKey(key) || map[key] == null) return fallback; bool value; return bool.TryParse(Convert.ToString(map[key], CultureInfo.InvariantCulture), out value) ? value : fallback; }
        private static DurableRelaunchPermitState State(Dictionary<string, object> map, string key, string fallback)
        {
            object raw = map.ContainsKey(key) ? map[key] : fallback;
            if (raw is int) return (DurableRelaunchPermitState)(int)raw;
            if (raw is long) return (DurableRelaunchPermitState)(long)raw;
            DurableRelaunchPermitState state; if (Enum.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), true, out state) && Enum.IsDefined(typeof(DurableRelaunchPermitState), state)) return state;
            int numeric; if (int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric) && Enum.IsDefined(typeof(DurableRelaunchPermitState), numeric)) return (DurableRelaunchPermitState)numeric;
            throw new InvalidDataException("StateInvalid");
        }

        private static bool HasToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
            return value.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c));
        }

        private static bool HasPathToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
                   value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        private static bool HasProof(DurableRelaunchAuthorityRecord record)
        {
            return record != null && HasToken(record.BootstrapProofPath) &&
                   RecoveryFailureReceipt.IsSha256(record.BootstrapProofSha256) &&
                   string.Equals(record.BootstrapProofMarker, "schema4-authority-bootstrap-proof-v1", StringComparison.Ordinal) &&
                   record.BootstrapMaximumProcessRelaunches > 0 &&
                   record.BootstrapMaximumProcessRelaunches == record.MaximumProcessRelaunches &&
                   HasToken(record.BootstrapPrimaryPath) && RecoveryFailureReceipt.IsSha256(record.BootstrapPrimarySha256);
        }

        private static bool HasAnyFailureEvidence(DurableRelaunchAuthorityRecord record)
        {
            if (record == null) return false;
            return !string.IsNullOrEmpty(record.LastFailureCanonicalSha256) ||
                   !string.IsNullOrEmpty(record.LastFailureOperationId) ||
                   !string.IsNullOrEmpty(record.LastFailureCorrelationId) ||
                   !string.IsNullOrEmpty(record.LastFailurePayloadSha256) ||
                   !string.IsNullOrEmpty(record.LastFailureFingerprint) ||
                   !string.IsNullOrEmpty(record.LastFailureCode) ||
                   !string.IsNullOrEmpty(record.LastFailureDetailCode) ||
                   !string.IsNullOrEmpty(record.LastFailureRunId) || record.LastFailureRunEpoch != 0 ||
                   !string.IsNullOrEmpty(record.LastFailureRecoveryStage) ||
                   !string.IsNullOrEmpty(record.LastFailureRecoveryProgressToken) ||
                   !string.IsNullOrEmpty(record.LastFailureRecoveryProcessSource) ||
                   !string.IsNullOrEmpty(record.LastFailureDeviceOrChannelGroup) ||
                   !string.IsNullOrEmpty(record.LastFailureSessionNonce) ||
                   record.LastFailureProcessId != 0 || record.LastFailureProcessStartUtcTicks != 0 ||
                   record.LastFailureConnectionGeneration != 0 || record.LastFailureAttemptGeneration != 0 ||
                   record.LastFailurePermitGeneration != 0 || !string.IsNullOrEmpty(record.LastFailurePermitId) ||
                   !string.IsNullOrEmpty(record.LastFailurePermitNonce) ||
                   !string.IsNullOrEmpty(record.LastFailureDisposition) ||
                   record.LastFailureDecisionSequence != 0 || record.LastFailureDecisionUtcTicks != 0 ||
                   record.LastFailurePermanent;
        }

        private static bool HasAnyLaunchIntent(DurableRelaunchAuthorityRecord record)
        {
            return record != null &&
                   (!string.IsNullOrEmpty(record.LaunchIntentId) ||
                    !string.IsNullOrEmpty(record.LaunchExecutablePath) ||
                    !string.IsNullOrEmpty(record.LaunchExecutableSha256) ||
                    !string.IsNullOrEmpty(record.LaunchArguments) ||
                    !string.IsNullOrEmpty(record.LaunchWorkingDirectory) ||
                    !string.IsNullOrEmpty(record.LaunchOptionsCanonical) ||
                    !string.IsNullOrEmpty(record.LaunchSpecSha256) ||
                    record.LaunchAuthorityRevision != 0 ||
                    !string.IsNullOrEmpty(record.LaunchAuthoritySha256) ||
                    record.LaunchConsumed);
        }

        private static bool HasLaunchIntent(DurableRelaunchAuthorityRecord record)
        {
            return record != null && HasToken(record.LaunchIntentId) &&
                   HasPathToken(record.LaunchExecutablePath) &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchExecutableSha256) &&
                   HasPathToken(record.LaunchWorkingDirectory) &&
                   HasToken(record.LaunchOptionsCanonical) &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchSpecSha256) &&
                   record.LaunchAuthorityRevision >= 0 &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchAuthoritySha256);
        }

        private static bool HasFailureEvidence(DurableRelaunchAuthorityRecord record)
        {
            var runId = string.IsNullOrEmpty(record?.LastFailureRunId)
                ? record?.RunId
                : record.LastFailureRunId;
            var runEpoch = record?.LastFailureRunEpoch > 0
                ? record.LastFailureRunEpoch
                : record?.RunEpoch ?? 0;
            var recoveryStage = string.IsNullOrEmpty(record?.LastFailureRecoveryStage)
                ? record?.RecoveryStage
                : record.LastFailureRecoveryStage;
            var progress = string.IsNullOrEmpty(record?.LastFailureRecoveryProgressToken)
                ? record?.RecoveryProgressToken
                : record.LastFailureRecoveryProgressToken;
            var source = string.IsNullOrEmpty(record?.LastFailureRecoveryProcessSource)
                ? record?.RecoveryProcessSource
                : record.LastFailureRecoveryProcessSource;
            var device = string.IsNullOrEmpty(record?.LastFailureDeviceOrChannelGroup)
                ? record?.DeviceOrChannelGroup
                : record.LastFailureDeviceOrChannelGroup;
            if (record == null || !RecoveryFailureReceipt.IsSha256(record.LastFailureCanonicalSha256) ||
                !RecoveryFailureReceipt.IsValidCorrelation(record.LastFailureCorrelationId) ||
                !RecoveryFailureReceipt.IsSha256(record.LastFailurePayloadSha256) ||
                !HasToken(record.LastFailureOperationId) || !HasToken(record.LastFailureSessionNonce) ||
                !HasToken(record.LastFailureCode) || !HasToken(record.LastFailureFingerprint) ||
                !HasToken(record.LastFailureDetailCode) || record.LastFailureProcessId <= 0 ||
                record.LastFailureProcessStartUtcTicks <= 0 || record.LastFailureConnectionGeneration <= 0 ||
                record.LastFailureAttemptGeneration <= 0 || record.LastFailureDecisionSequence <= 0 ||
                record.LastFailureDecisionUtcTicks <= 0 || record.ConsecutiveFailures <= 0 ||
                string.IsNullOrEmpty(runId) || runEpoch <= 0 ||
                string.IsNullOrEmpty(recoveryStage) || string.IsNullOrEmpty(progress) ||
                string.IsNullOrEmpty(source) || string.IsNullOrEmpty(device))
                return false;
            string recomputed;
            return TryCanonicalFromRecord(record, out recomputed) &&
                   string.Equals(recomputed, record.LastFailureCanonicalSha256, StringComparison.Ordinal);
        }

        private static bool TryCanonicalFromRecord(DurableRelaunchAuthorityRecord record, out string canonical)
        {
            canonical = null;
            if (record == null || !RecoveryFailureReceipt.IsValidCorrelation(record.LastFailureCorrelationId) ||
                !RecoveryFailureReceipt.IsSha256(record.LastFailurePayloadSha256) ||
                !HasToken(record.LastFailureSessionNonce) || !HasToken(record.LastFailureCode) ||
                !HasToken(record.LastFailureFingerprint) || !HasToken(record.LastFailureOperationId)) return false;
            var op = new RecoveryFailureOperation
            {
                OperationId = record.LastFailureOperationId, SessionId = record.SessionId,
                SessionNonce = record.LastFailureSessionNonce, SidecarProcessId = record.LastFailureProcessId,
                SidecarProcessStartUtcTicks = record.LastFailureProcessStartUtcTicks,
                ConnectionGeneration = record.LastFailureConnectionGeneration,
                RecoveryAttemptGeneration = record.LastFailureAttemptGeneration,
                RequestCorrelationId = record.LastFailureCorrelationId,
                RequestPayloadSha256 = record.LastFailurePayloadSha256,
                FailureCode = record.LastFailureCode, FailureFingerprint = record.LastFailureFingerprint,
                Permanent = record.LastFailurePermanent, DetailCode = record.LastFailureDetailCode,
                RunId = string.IsNullOrEmpty(record.LastFailureRunId)
                    ? record.RunId : record.LastFailureRunId,
                RunEpoch = record.LastFailureRunEpoch > 0
                    ? record.LastFailureRunEpoch : record.RunEpoch,
                RecoveryStage = string.IsNullOrEmpty(record.LastFailureRecoveryStage)
                    ? record.RecoveryStage : record.LastFailureRecoveryStage,
                RecoveryProgressToken = string.IsNullOrEmpty(record.LastFailureRecoveryProgressToken)
                    ? record.RecoveryProgressToken : record.LastFailureRecoveryProgressToken,
                RecoveryProcessSource = string.IsNullOrEmpty(record.LastFailureRecoveryProcessSource)
                    ? record.RecoveryProcessSource : record.LastFailureRecoveryProcessSource,
                DeviceOrChannelGroup = string.IsNullOrEmpty(record.LastFailureDeviceOrChannelGroup)
                    ? record.DeviceOrChannelGroup : record.LastFailureDeviceOrChannelGroup,
                MaximumProcessRelaunches = record.MaximumProcessRelaunches
            };
            try { canonical = RecoveryFailureCanonical.Sha256(op); return true; }
            catch { return false; }
        }
    }

    public sealed class DurableAuthorityValidationResult
    {
        public DurableRelaunchAuthorityRecord Record { get; internal set; }
        public bool Migrated { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Proven { get; internal set; }
        public string Reason { get; internal set; }
        public DurableAuthorityFailureKind FailureKind { get; internal set; }
    }
}
