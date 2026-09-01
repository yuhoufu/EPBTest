using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Synchronous schema4 bridge between the sidecar's full session journal
    /// and the durable relaunch coordinator.  The coordinator never treats
    /// the legacy in-memory counters as authority; this adapter projects its
    /// immutable permit record into the same atomic session snapshot before a
    /// launch action is exposed.
    /// </summary>
    internal sealed class CriticalJournalAdapter : IDurableRelaunchStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly object _gate = new object();
        private readonly string _directory;
        private readonly string _sessionId;
        private readonly WatchdogJournalStore _journalStore;
        private readonly Func<string> _snapshotFactory;

        internal CriticalJournalAdapter(
            string directory,
            string sessionId,
            WatchdogJournalStore journalStore,
            Func<string> snapshotFactory)
        {
            _directory = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            _sessionId = sessionId ?? string.Empty;
            _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));
            _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        }

        public DurableRelaunchPermitRecord Load(string sessionId)
        {
            if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "SessionMismatch");
            var path = Path.Combine(
                _directory,
                "session-" + WatchdogJournalPaths.SafeName(_sessionId) + ".json");
            string json;
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    using (var stream = new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                        json = reader.ReadToEnd();
                }
                catch (Exception ex)
                {
                    return WatchdogJournalV4Validator.CreateBlocked(
                        _sessionId,
                        "JournalUnreadable:" + ex.GetType().Name);
                }
            }
            return ReadPermit(json);
        }

        public bool TryCommit(DurableRelaunchPermitRecord record)
        {
            if (record == null ||
                !string.Equals(record.SessionId, _sessionId, StringComparison.Ordinal) ||
                record.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion)
                return false;
            lock (_gate)
            {
                try
                {
                    var snapshotJson = _snapshotFactory();
                    if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
                    var snapshot = Json.Deserialize<WatchdogJournal>(snapshotJson);
                    if (snapshot == null ||
                        !string.Equals(snapshot.SessionId, _sessionId, StringComparison.Ordinal))
                        return false;
                    ApplyPermit(snapshot, record);
                    snapshot.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
                    return _journalStore.TryPublishSnapshotSynchronously(Json.Serialize(snapshot));
                }
                catch
                {
                    return false;
                }
            }
        }

        private DurableRelaunchPermitRecord ReadPermit(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "JournalEmpty");
            Dictionary<string, object> values;
            try
            {
                values = Json.DeserializeObject(json) as Dictionary<string, object>;
                if (values == null)
                    return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "JournalCorrupt");
            }
            catch
            {
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "JournalUnreadable");
            }

            var storedSession = ReadString(values, "SessionId");
            if (!string.IsNullOrWhiteSpace(storedSession) &&
                !string.Equals(storedSession, _sessionId, StringComparison.Ordinal))
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "SessionMismatch");

            var schema = ReadInt(values, "SchemaVersion", 0);
            if (schema != 2 && schema != 3 && schema != 4 &&
                schema != WatchdogJournalPolicy.CurrentSchemaVersion)
                return WatchdogJournalV4Validator.CreateBlocked(
                    _sessionId,
                    schema <= 0 ? "MissingSchema" : "UnsupportedSchema");

            var stateText = ReadString(values, "RelaunchState");
            if (string.Equals(stateText, DurableRelaunchPermitState.None.ToString(), StringComparison.OrdinalIgnoreCase) &&
                ReadBool(values, "RecoveryBlocked", false))
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "LegacyRecoveryBlocked");
            if (string.IsNullOrWhiteSpace(stateText))
            {
                // The full snapshot may still be a V2/V3 journal.  Force the
                // legacy validator to inspect RecoveryBlocked, attempt and
                // PID evidence rather than deserializing it as schema4 None.
                values["SchemaVersion"] = 3;
                var migrated = WatchdogJournalV4Validator.ValidateAndMigrate(
                    Json.Serialize(values),
                    _sessionId);
                return migrated.Record ??
                       WatchdogJournalV4Validator.CreateBlocked(
                           _sessionId,
                           migrated.Reason ?? "LegacyMigrationFailed");
            }

            var record = new DurableRelaunchPermitRecord
            {
                SchemaVersion = ReadInt(values, "SchemaVersion", 0),
                SessionId = ReadString(values, "SessionId"),
                Generation = ReadLong(values, "RelaunchPermitGeneration",
                    ReadLong(values, "RelaunchGeneration", 0)),
                PermitId = ReadString(values, "RelaunchPermitId"),
                PermitNonce = ReadString(values, "RelaunchPermitNonce"),
                Fingerprint = ReadString(values, "RelaunchFingerprint"),
                ProgressToken = ReadString(values, "RelaunchProgressToken"),
                ProcessSource = ReadString(values, "RelaunchProcessSource"),
                RunId = ReadString(values, "RelaunchRunId"),
                RecoveryStage = ReadString(values, "RelaunchRecoveryStage"),
                ProcessId = ReadInt(values, "RelaunchProcessId", 0),
                ProcessStartUtcTicks = ReadLong(values, "RelaunchProcessStartUtcTicks", 0),
                ConsecutiveFailures = ReadInt(values, "RelaunchConsecutiveFailures", 0),
                MaximumProcessRelaunches = ReadInt(values, "RelaunchMaximumProcessRelaunches", 0),
                RecoveryCommitGeneration = ReadLong(values, "RelaunchRecoveryCommitGeneration", 0),
                LastFailureCode = ReadString(values, "RelaunchFailureCode"),
                LastFailureReason = ReadString(values, "RelaunchFailureReason"),
                LastTransitionUtcTicks = ReadLong(values, "UpdatedUtcTicks", 0)
            };
            if (!Enum.TryParse(stateText, true, out DurableRelaunchPermitState state) ||
                !Enum.IsDefined(typeof(DurableRelaunchPermitState), state))
                return WatchdogJournalV4Validator.CreateBlocked(_sessionId, "UnknownRelaunchState", record);
            record.State = state;
            var validation = WatchdogJournalV4Validator.ValidateRecord(record, _sessionId);
            return validation.IsValid && validation.Record != null
                ? validation.Record
                : WatchdogJournalV4Validator.CreateBlocked(
                    _sessionId,
                    validation.Reason ?? "InvalidDurableRelaunchState",
                    record);
        }

        private static void ApplyPermit(WatchdogJournal journal, DurableRelaunchPermitRecord record)
        {
            journal.RelaunchState = record.State.ToString();
            journal.RelaunchGeneration = record.Generation;
            journal.RelaunchPermitGeneration = record.Generation;
            journal.RelaunchPermitId = record.PermitId;
            journal.RelaunchPermitNonce = record.PermitNonce;
            journal.RelaunchFingerprint = record.Fingerprint;
            journal.RelaunchProgressToken = record.ProgressToken;
            journal.RelaunchProcessSource = record.ProcessSource;
            journal.RelaunchRunId = record.RunId;
            journal.RelaunchRecoveryStage = record.RecoveryStage;
            journal.RelaunchProcessId = record.ProcessId;
            journal.RelaunchProcessStartUtcTicks = record.ProcessStartUtcTicks;
            journal.RelaunchConsecutiveFailures = record.ConsecutiveFailures;
            journal.RelaunchMaximumProcessRelaunches = record.MaximumProcessRelaunches;
            journal.RelaunchRecoveryCommitGeneration = record.RecoveryCommitGeneration;
            journal.RelaunchFailureCode = record.LastFailureCode;
            journal.RelaunchFailureReason = record.LastFailureReason;
            // Compatibility mirrors are deliberately derived from the
            // durable record, never used to authorize a launch.
            journal.RecoveryBlocked = record.State == DurableRelaunchPermitState.Blocked;
            journal.ConsecutiveStartupFailures = record.ConsecutiveFailures;
            journal.RecoveryFailureMaxProcessRelaunches = record.MaximumProcessRelaunches;
            journal.RecoveryFailureCode = record.LastFailureCode;
            journal.RecoveryFailurePermanent = record.State == DurableRelaunchPermitState.Blocked;
            journal.RecoveryFailureFingerprint = record.Fingerprint;
            journal.RecoveryProgressToken = record.ProgressToken;
            journal.RecoveryProcessSource = record.ProcessSource;
            journal.RunId = record.RunId;
            journal.RecoveryStage = record.RecoveryStage;
            journal.CurrentPid = record.ProcessId > 0 ? record.ProcessId : journal.CurrentPid;
            journal.CurrentProcessStartUtcTicks = record.ProcessStartUtcTicks > 0
                ? record.ProcessStartUtcTicks
                : journal.CurrentProcessStartUtcTicks;
        }

        private static string ReadString(Dictionary<string, object> values, string key)
        {
            return values.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
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
