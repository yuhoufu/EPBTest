using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class WatchdogJournalV4ValidationResult
    {
        public DurableRelaunchPermitRecord Record { get; internal set; }
        public bool IsValid { get; internal set; }
        public bool Migrated { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Missing { get; internal set; }
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// Validates and migrates the durable relaunch portion of the watchdog
    /// journal.  V2/V3 records are never guessed into a runnable permit:
    /// explicit blocked evidence survives, pristine records become None, and
    /// any ambiguous launch/PID evidence becomes AmbiguousLegacy/Blocked.
    /// </summary>
    public static class WatchdogJournalV4Validator
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static DurableRelaunchPermitRecord CreateNone(string sessionId)
        {
            return new DurableRelaunchPermitRecord
            {
                SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion,
                SessionId = sessionId ?? string.Empty,
                State = DurableRelaunchPermitState.None,
                LastTransitionUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        public static DurableRelaunchPermitRecord CreateBlocked(
            string sessionId,
            string reason,
            DurableRelaunchPermitRecord evidence = null)
        {
            var result = evidence?.Clone() ?? new DurableRelaunchPermitRecord();
            result.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
            result.SessionId = sessionId ?? result.SessionId ?? string.Empty;
            result.State = DurableRelaunchPermitState.Blocked;
            result.LastFailureCode = string.IsNullOrWhiteSpace(reason)
                ? "DurableJournalInvariantViolation"
                : reason;
            result.LastFailureReason = reason ?? string.Empty;
            result.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
            return result;
        }

        public static WatchdogJournalV4ValidationResult ValidateAndMigrate(
            string json,
            string expectedSessionId)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new WatchdogJournalV4ValidationResult
                {
                    Record = CreateNone(expectedSessionId),
                    IsValid = true,
                    Missing = true,
                    Reason = "Missing"
                };
            }

            Dictionary<string, object> values;
            try
            {
                values = Json.DeserializeObject(json) as Dictionary<string, object>;
                if (values == null)
                    return BlockedResult(expectedSessionId, "CorruptJson");
            }
            catch
            {
                return BlockedResult(expectedSessionId, "UnreadableJson");
            }

            var schema = ReadInt(values, "SchemaVersion", 0);
            if (schema == 2 || schema == 3)
            {
                var migrated = MigrateLegacy(values, expectedSessionId);
                return new WatchdogJournalV4ValidationResult
                {
                    Record = migrated,
                    IsValid = migrated.State != DurableRelaunchPermitState.Blocked ||
                              string.Equals(migrated.LastFailureCode, "RecoveryBlocked", StringComparison.Ordinal),
                    Migrated = true,
                    Blocked = migrated.State == DurableRelaunchPermitState.Blocked,
                    Reason = migrated.LastFailureCode ?? "LegacyMigrated"
                };
            }

            if (schema == 4)
            {
                try
                {
                    var migrated = Json.Deserialize<DurableRelaunchPermitRecord>(json);
                    migrated.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
                    var v4Validation = ValidateRecord(migrated, expectedSessionId);
                    if (!v4Validation.IsValid)
                        return BlockedResult(
                            expectedSessionId,
                            "V4Migration:" + v4Validation.Reason,
                            migrated);
                    v4Validation.Record = migrated.Clone();
                    v4Validation.Migrated = true;
                    v4Validation.Reason = "V4ReadOnlyMigratedToV5";
                    return v4Validation;
                }
                catch
                {
                    return BlockedResult(expectedSessionId, "InvalidSchema4Record");
                }
            }

            if (schema != WatchdogJournalPolicy.CurrentSchemaVersion)
                return BlockedResult(expectedSessionId, schema <= 0 ? "MissingSchema" : "UnknownSchema");

            DurableRelaunchPermitRecord record;
            try
            {
                record = Json.Deserialize<DurableRelaunchPermitRecord>(json);
            }
            catch
            {
                return BlockedResult(expectedSessionId, "InvalidSchema4Record");
            }
            var validation = ValidateRecord(record, expectedSessionId);
            if (!validation.IsValid)
                return BlockedResult(expectedSessionId, validation.Reason, record);
            validation.Record = record.Clone();
            return validation;
        }

        /// <summary>
        /// Strict variant used by the failure-registration authority.  The
        /// compatibility overload above keeps historical journal readers
        /// source-compatible; this overload refuses an ambiguous/missing
        /// state unless a typed verified-pristine bootstrap is supplied.
        /// </summary>
        public static WatchdogJournalV4ValidationResult ValidateAndMigrate(
            string json,
            string expectedSessionId,
            DurableRelaunchBootstrapEvidence bootstrapEvidence)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                if (bootstrapEvidence != null && bootstrapEvidence.IsVerifiedPristine &&
                    string.Equals(bootstrapEvidence.SessionId, expectedSessionId, StringComparison.Ordinal))
                    return new WatchdogJournalV4ValidationResult
                    {
                        Record = CreateNone(expectedSessionId), IsValid = true,
                        Missing = true, Reason = "VerifiedPristineBootstrap"
                    };
                return BlockedResult(expectedSessionId, "MissingVerifiedBootstrap");
            }
            var result = ValidateAndMigrate(json, expectedSessionId);
            if (result.Missing && (bootstrapEvidence == null || !bootstrapEvidence.IsVerifiedPristine))
                return BlockedResult(expectedSessionId, "MissingVerifiedBootstrap");
            return result;
        }

        public static WatchdogJournalV4ValidationResult ValidateRecord(
            DurableRelaunchPermitRecord record,
            string expectedSessionId)
        {
            if (record == null)
                return new WatchdogJournalV4ValidationResult
                {
                    Record = CreateNone(expectedSessionId),
                    IsValid = true,
                    Missing = true,
                    Reason = "Missing"
                };
            if (record.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion)
                return Invalid("SchemaVersion");
            if (string.IsNullOrWhiteSpace(expectedSessionId) ||
                !string.Equals(record.SessionId, expectedSessionId, StringComparison.Ordinal))
                return Invalid("SessionMismatch");
            if (!Enum.IsDefined(typeof(DurableRelaunchPermitState), record.State))
                return Invalid("UnknownState");
            if (record.ConsecutiveFailures < 0 || record.MaximumProcessRelaunches < 0)
                return Invalid("NegativeBudget");
            if (record.State == DurableRelaunchPermitState.None)
            {
                if (record.Generation != 0 || !string.IsNullOrWhiteSpace(record.PermitId))
                    return Invalid("NoneHasIdentity");
                return Valid(record, "None");
            }
            // A durable circuit-open marker may intentionally have no permit
            // identity.  It is terminal evidence, not a runnable permit.
            if (record.State == DurableRelaunchPermitState.Blocked &&
                record.Generation == 0 && string.IsNullOrWhiteSpace(record.PermitId))
                return Valid(record, "Blocked");
            if (record.Generation <= 0 || string.IsNullOrWhiteSpace(record.PermitId))
                return Invalid("IdentityMissing");
            // A runnable schema4 permit must carry a fresh launch nonce.  It
            // is echoed by the recovery client and checked by the sidecar;
            // accepting an Approved/Started record without it would make a
            // stale command line indistinguishable from the current permit.
            if (record.State != DurableRelaunchPermitState.Blocked &&
                string.IsNullOrWhiteSpace(record.PermitNonce))
                return Invalid("PermitNonceMissing");
            if (record.State != DurableRelaunchPermitState.Blocked &&
                (string.IsNullOrWhiteSpace(record.Fingerprint) ||
                 string.IsNullOrWhiteSpace(record.ProgressToken) ||
                 string.IsNullOrWhiteSpace(record.ProcessSource) ||
                 string.IsNullOrWhiteSpace(record.RunId) ||
                 string.IsNullOrWhiteSpace(record.RecoveryStage)))
                return Invalid("EvidenceMissing");
            if ((record.State == DurableRelaunchPermitState.Started ||
                 record.State == DurableRelaunchPermitState.Attached) &&
                (record.ProcessId <= 0 || record.ProcessStartUtcTicks <= 0))
                return Invalid("ProcessIdentityMissing");
            if (record.State == DurableRelaunchPermitState.Committed &&
                record.RecoveryCommitGeneration <= 0)
                return Invalid("CommitEvidenceMissing");
            if (record.ConsecutiveFailures > 0 && record.MaximumProcessRelaunches == 0)
                return Invalid("BudgetMissing");
            return Valid(record, record.State.ToString());
        }

        public static string MigrateJson(string json, string expectedSessionId)
        {
            var result = ValidateAndMigrate(json, expectedSessionId);
            return Json.Serialize(result.Record);
        }

        private static DurableRelaunchPermitRecord MigrateLegacy(
            Dictionary<string, object> values,
            string expectedSessionId)
        {
            var blocked = ReadBool(values, "RecoveryBlocked", false);
            var generation = Math.Max(
                ReadLong(values, "RelaunchGeneration", 0),
                ReadLong(values, "RecoveryCommitGeneration", 0));
            var attempt = Math.Max(
                ReadInt(values, "RecoveryAttempt", 0),
                ReadInt(values, "ConsecutiveStartupFailures", 0));
            var pid = ReadInt(values, "CurrentPid", 0);
            var startTicks = ReadLong(values, "CurrentProcessStartUtcTicks", 0);
            var legacy = new DurableRelaunchPermitRecord
            {
                SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion,
                SessionId = expectedSessionId ?? ReadString(values, "SessionId"),
                Generation = generation,
                ConsecutiveFailures = attempt,
                ProcessId = pid,
                ProcessStartUtcTicks = startTicks,
                Fingerprint = ReadString(values, "RecoveryFailureFingerprint"),
                ProgressToken = ReadString(values, "RecoveryProgressToken"),
                ProcessSource = ReadString(values, "RecoveryProcessSource"),
                RunId = ReadString(values, "RunId"),
                RecoveryStage = ReadString(values, "RecoveryStage"),
                LastFailureCode = ReadString(values, "RecoveryFailureCode")
            };
            if (blocked)
            {
                legacy.LastFailureCode = string.IsNullOrWhiteSpace(legacy.LastFailureCode)
                    ? "RecoveryBlocked"
                    : legacy.LastFailureCode;
                legacy.State = DurableRelaunchPermitState.Blocked;
                legacy.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return legacy;
            }
            if (generation == 0 && attempt == 0 && pid == 0 && startTicks == 0)
                return CreateNone(expectedSessionId);
            legacy.State = DurableRelaunchPermitState.Blocked;
            legacy.LastFailureCode = "AmbiguousLegacy";
            legacy.LastFailureReason = "旧Journal含有无法绑定permit identity的启动迹象";
            legacy.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
            return legacy;
        }

        private static WatchdogJournalV4ValidationResult BlockedResult(
            string sessionId,
            string reason,
            DurableRelaunchPermitRecord evidence = null)
        {
            return new WatchdogJournalV4ValidationResult
            {
                Record = CreateBlocked(sessionId, reason, evidence),
                IsValid = false,
                Blocked = true,
                Reason = reason
            };
        }

        private static WatchdogJournalV4ValidationResult Invalid(string reason) =>
            new WatchdogJournalV4ValidationResult { IsValid = false, Reason = reason };

        private static WatchdogJournalV4ValidationResult Valid(
            DurableRelaunchPermitRecord record,
            string reason) =>
            new WatchdogJournalV4ValidationResult
            {
                Record = record.Clone(),
                IsValid = true,
                Blocked = record.State == DurableRelaunchPermitState.Blocked,
                Reason = reason
            };

        private static string ReadString(Dictionary<string, object> values, string key)
        {
            return values.TryGetValue(key, out var value) ? value?.ToString() : string.Empty;
        }

        private static int ReadInt(Dictionary<string, object> values, string key, int fallback)
        {
            if (!values.TryGetValue(key, out var value) || value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static long ReadLong(Dictionary<string, object> values, string key, long fallback)
        {
            if (!values.TryGetValue(key, out var value) || value == null) return fallback;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool ReadBool(Dictionary<string, object> values, string key, bool fallback)
        {
            if (!values.TryGetValue(key, out var value) || value == null) return fallback;
            if (value is bool boolean) return boolean;
            return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }
    }
}
