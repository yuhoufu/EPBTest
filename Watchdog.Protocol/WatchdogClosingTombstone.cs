using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum WatchdogClosingTombstoneState
    {
        Closing = 1,
        Terminal = 2
    }

    /// <summary>
    /// Cross-process, write-through fence for one exact Watchdog session.
    /// Once written, launch/reconnect authorization for that session can never
    /// be restored by an in-memory rollback or a replacement sidecar.
    /// </summary>
    public sealed class WatchdogClosingTombstone
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; } = string.Empty;
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string CloseIntent { get; set; } = string.Empty;
        public string StopSafetyTransactionId { get; set; } = string.Empty;
        public string StopRunId { get; set; } = string.Empty;
        public long StopRunEpoch { get; set; }
        public long StopSafetyBoundaryGeneration { get; set; }
        public long StateVersion { get; set; }
        public WatchdogClosingTombstoneState State { get; set; }
        public string TerminalReason { get; set; } = string.Empty;
        public long UpdatedUtcTicks { get; set; }

        public bool IsValidFor(string sessionId)
        {
            return SchemaVersion == 1 &&
                   !string.IsNullOrWhiteSpace(SessionId) &&
                   string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
                   SessionGeneration > 0 && SessionLease > 0 && StateVersion > 0 &&
                   (State == WatchdogClosingTombstoneState.Closing ||
                    State == WatchdogClosingTombstoneState.Terminal);
        }
    }

    public static class WatchdogClosingTombstoneStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static WatchdogClosingTombstone WriteThrough(
            string projectDirectory,
            WatchdogClosingTombstone tombstone)
        {
            if (tombstone == null) throw new ArgumentNullException(nameof(tombstone));
            if (!tombstone.IsValidFor(tombstone.SessionId))
                throw new InvalidOperationException("Watchdog closing tombstone identity is incomplete.");
            tombstone.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            var payload = Serializer.Serialize(tombstone);
            AtomicWrite(WatchdogJournalPaths.LocalClosingPath(tombstone.SessionId), payload);
            AtomicWrite(
                WatchdogJournalPaths.ProjectClosingPath(projectDirectory, tombstone.SessionId),
                payload);
            return tombstone;
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogClosingTombstone tombstone)
        {
            tombstone = CandidatePaths(projectDirectory, sessionId)
                .Select(TryReadFile)
                .Where(value => value?.IsValidFor(sessionId) == true)
                .OrderByDescending(value => value.StateVersion)
                .ThenByDescending(value => value.UpdatedUtcTicks)
                .FirstOrDefault();
            return tombstone != null;
        }

        private static IEnumerable<string> CandidatePaths(string projectDirectory, string sessionId)
        {
            yield return WatchdogJournalPaths.LocalClosingPath(sessionId);
            if (!string.IsNullOrWhiteSpace(projectDirectory))
                yield return WatchdogJournalPaths.ProjectClosingPath(projectDirectory, sessionId);
        }

        private static WatchdogClosingTombstone TryReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(
                           path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                    return Serializer.Deserialize<WatchdogClosingTombstone>(reader.ReadToEnd());
            }
            catch { return null; }
        }

        private static void AtomicWrite(string path, string payload)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Watchdog closing tombstone directory is invalid.");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, payload ?? string.Empty, new UTF8Encoding(false));
                using (var stream = new FileStream(
                           temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
    }
}
