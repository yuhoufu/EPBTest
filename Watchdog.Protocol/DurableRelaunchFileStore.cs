using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Small synchronous schema4 relaunch store.  It is intentionally scoped
    /// to the relaunch authority rather than the Host journal: the Host can
    /// continue implementing the legacy IDurableRelaunchStore adapter while
    /// the new coordinator gets an atomic file and a fail-closed marker.
    /// </summary>
    public sealed class DurableRelaunchFileStore : IDurableRelaunchStoreV4
    {
        private static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly object _gate;
        private readonly string _directory;
        private readonly string _sessionId;
        private readonly string _snapshotPath;
        private readonly string _blockedMarkerPath;
        private bool _failNextCommit;
        private bool _failNextMarkerCommit;

        public DurableRelaunchFileStore(string directory, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory");
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId");
            _directory = Path.GetFullPath(directory);
            _sessionId = sessionId;
            _snapshotPath = Path.Combine(_directory, "session-" + SafeName(sessionId) + ".relaunch.json");
            _blockedMarkerPath = _snapshotPath + ".blocked";
            _gate = Gates.GetOrAdd(_snapshotPath, _ => new object());
        }

        public string SnapshotPath => _snapshotPath;
        public string BlockedMarkerPath => _blockedMarkerPath;

        public void FailNextCommit()
        {
            lock (_gate) _failNextCommit = true;
        }

        public void FailNextMarkerCommit()
        {
            lock (_gate) _failNextMarkerCommit = true;
        }

        public DurableRelaunchPermitRecord Load(string sessionId)
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_snapshotPath)) return null;
                    var json = File.ReadAllText(_snapshotPath, Encoding.UTF8);
                    return Json.Deserialize<DurableRelaunchPermitRecord>(json)?.Clone();
                }
                catch { return null; }
            }
        }

        public bool TryCommit(DurableRelaunchPermitRecord record)
        {
            if (record == null) return false;
            lock (_gate)
            {
                if (_failNextCommit)
                {
                    _failNextCommit = false;
                    return false;
                }
                return WriteSnapshotLocked(record);
            }
        }

        public DurableRelaunchStoreLoadResult LoadV4(
            string sessionId,
            DurableRelaunchBootstrapEvidence bootstrapEvidence)
        {
            if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                return new DurableRelaunchStoreLoadResult { Blocked = true, Reason = "SessionMismatch" };
            lock (_gate)
            {
                try
                {
                    if (File.Exists(_blockedMarkerPath))
                        return new DurableRelaunchStoreLoadResult
                        { Blocked = true, Reason = "DurableBlockedMarker" };

                    if (!File.Exists(_snapshotPath))
                    {
                        if (bootstrapEvidence == null || !bootstrapEvidence.IsVerifiedPristine ||
                            !string.Equals(bootstrapEvidence.SessionId, sessionId, StringComparison.Ordinal))
                            return new DurableRelaunchStoreLoadResult
                            { Missing = true, Blocked = true, Reason = "MissingVerifiedBootstrap" };
                        var pristine = WatchdogJournalV4Validator.CreateNone(sessionId);
                        if (!WriteSnapshotLocked(pristine))
                            return new DurableRelaunchStoreLoadResult
                            { Missing = true, Blocked = true, Reason = "BootstrapPersistFailed" };
                        return new DurableRelaunchStoreLoadResult
                        { Record = pristine, Initialized = true, Reason = "VerifiedPristineBootstrap" };
                    }

                    var json = File.ReadAllText(_snapshotPath, Encoding.UTF8);
                    var result = WatchdogJournalV4Validator.ValidateAndMigrate(
                        json, sessionId, DurableRelaunchBootstrapEvidence.None(sessionId));
                    if (!result.IsValid || result.Record == null)
                    {
                        if (result.Blocked && result.Record != null)
                            WriteSnapshotLocked(result.Record);
                        return new DurableRelaunchStoreLoadResult
                        { Corrupt = true, Blocked = true, Reason = result.Reason ?? "InvalidDurableState" };
                    }

                    // Persist a migrated schema2/3 record exactly once.  A
                    // failed migration write is fail-closed, never runnable.
                    if (result.Migrated)
                    {
                        if (!WriteSnapshotLocked(result.Record))
                            return new DurableRelaunchStoreLoadResult
                            { Blocked = true, Reason = "MigrationPersistFailed" };
                    }
                    return new DurableRelaunchStoreLoadResult
                    {
                        Record = result.Record.Clone(),
                        Blocked = result.Blocked,
                        Reason = result.Reason ?? "Loaded"
                    };
                }
                catch
                {
                    return new DurableRelaunchStoreLoadResult
                    { Corrupt = true, Blocked = true, Reason = "UnreadableDurableState" };
                }
            }
        }

        public bool TryCommitFailure(
            DurableRelaunchPermitRecord record,
            RecoveryFailureReceipt receipt,
            string operationId)
        {
            if (record == null || receipt == null || string.IsNullOrWhiteSpace(operationId)) return false;
            lock (_gate)
            {
                if (_failNextCommit)
                {
                    _failNextCommit = false;
                    return false;
                }
                // Receipt fields are mirrored into the record before the
                // atomic replace; no separate mutable receipt file can get
                // out of sync with the authority record.
                return WriteSnapshotLocked(record);
            }
        }

        public bool TryCommitBlockedMarker(string sessionId, string reason)
        {
            if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal)) return false;
            lock (_gate)
            {
                if (_failNextMarkerCommit)
                {
                    _failNextMarkerCommit = false;
                    return false;
                }
                try
                {
                    Directory.CreateDirectory(_directory);
                    var temp = _blockedMarkerPath + ".tmp." + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temp, reason ?? "DurableBlockedMarker", Encoding.UTF8);
                    ReplaceFile(temp, _blockedMarkerPath);
                    return true;
                }
                catch { return false; }
            }
        }

        private bool WriteSnapshotLocked(DurableRelaunchPermitRecord record)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var temp = _snapshotPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, Json.Serialize(record.Clone()), Encoding.UTF8);
                ReplaceFile(temp, _snapshotPath);
                return true;
            }
            catch { return false; }
        }

        private static void ReplaceFile(string temp, string target)
        {
            if (File.Exists(target))
            {
                try { File.Replace(temp, target, null, true); }
                catch
                {
                    File.Delete(target);
                    File.Move(temp, target);
                }
            }
            else File.Move(temp, target);
        }

        private static string SafeName(string value)
        {
            var builder = new StringBuilder();
            foreach (var ch in value)
                builder.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' ? ch : '_');
            var safe = builder.ToString();
            return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
        }
    }
}
