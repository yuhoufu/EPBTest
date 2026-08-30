using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class WatchdogJournalPolicy
    {
        public const int CurrentSchemaVersion = 4;
        public const int DefaultRetentionDays = 90;
        public const int DefaultRetainSessionCount = 32;
        public const long DefaultMaxTotalBytes = 128L * 1024L * 1024L;
        public const long DefaultMaxSessionBytes = 16L * 1024L * 1024L;
        public const int DefaultHeartbeatCheckpointSeconds = 60;
        public const long DefaultEmergencySpoolMaxBytes = 8L * 1024L * 1024L;
        public const int EmergencySpoolRetentionDays = 7;

        public int RetentionDays { get; set; } = DefaultRetentionDays;
        public int RetainSessionCount { get; set; } = DefaultRetainSessionCount;
        public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;
        public long MaxSessionBytes { get; set; } = DefaultMaxSessionBytes;
        public int HeartbeatCheckpointSeconds { get; set; } = DefaultHeartbeatCheckpointSeconds;
        public long EmergencySpoolMaxBytes { get; set; } = DefaultEmergencySpoolMaxBytes;

        public static WatchdogJournalPolicy Load(
            NameValueCollection settings,
            Action<string> warningSink = null)
        {
            return new WatchdogJournalPolicy
            {
                RetentionDays = ParseInt(settings?["WatchdogJournalRetentionDays"],
                    DefaultRetentionDays, 1, 3650, "WatchdogJournalRetentionDays", warningSink),
                RetainSessionCount = ParseInt(settings?["WatchdogJournalRetainSessionCount"],
                    DefaultRetainSessionCount, 1, 10000, "WatchdogJournalRetainSessionCount", warningSink),
                MaxTotalBytes = ParseLong(settings?["WatchdogJournalMaxTotalBytes"],
                    DefaultMaxTotalBytes, 16L * 1024L * 1024L, 16L * 1024L * 1024L * 1024L,
                    "WatchdogJournalMaxTotalBytes", warningSink),
                MaxSessionBytes = ParseLong(settings?["WatchdogJournalMaxSessionBytes"],
                    DefaultMaxSessionBytes, 4L * 1024L * 1024L, 1024L * 1024L * 1024L,
                    "WatchdogJournalMaxSessionBytes", warningSink),
                HeartbeatCheckpointSeconds = ParseInt(settings?["WatchdogJournalHeartbeatCheckpointSeconds"],
                    DefaultHeartbeatCheckpointSeconds, 10, 3600,
                    "WatchdogJournalHeartbeatCheckpointSeconds", warningSink),
                EmergencySpoolMaxBytes = ParseLong(settings?["WatchdogJournalEmergencySpoolMaxBytes"],
                    DefaultEmergencySpoolMaxBytes, 1024L * 1024L, 1024L * 1024L * 1024L,
                    "WatchdogJournalEmergencySpoolMaxBytes", warningSink)
            }.Normalize(warningSink);
        }

        public WatchdogJournalPolicy Normalize(Action<string> warningSink = null)
        {
            if (RetentionDays < 1 || RetentionDays > 3650) RetentionDays = DefaultRetentionDays;
            if (RetainSessionCount < 1 || RetainSessionCount > 10000)
                RetainSessionCount = DefaultRetainSessionCount;
            if (MaxTotalBytes < 16L * 1024L * 1024L || MaxTotalBytes > 16L * 1024L * 1024L * 1024L)
                MaxTotalBytes = DefaultMaxTotalBytes;
            if (MaxSessionBytes < 4L * 1024L * 1024L || MaxSessionBytes > 1024L * 1024L * 1024L)
                MaxSessionBytes = DefaultMaxSessionBytes;
            if (HeartbeatCheckpointSeconds < 10 || HeartbeatCheckpointSeconds > 3600)
                HeartbeatCheckpointSeconds = DefaultHeartbeatCheckpointSeconds;
            if (EmergencySpoolMaxBytes < 1024L * 1024L ||
                EmergencySpoolMaxBytes > 1024L * 1024L * 1024L)
                EmergencySpoolMaxBytes = DefaultEmergencySpoolMaxBytes;
            if (MaxSessionBytes > MaxTotalBytes)
            {
                warningSink?.Invoke(
                    $"WatchdogJournalMaxSessionBytes 大于总预算，已收敛到 {MaxTotalBytes}。Value={MaxSessionBytes}");
                MaxSessionBytes = MaxTotalBytes;
            }
            return this;
        }

        public string ToStartupLogLine() => string.Format(
            CultureInfo.InvariantCulture,
            "WatchdogJournalPolicy RetentionDays={0};RetainSessions={1};MaxTotalBytes={2};" +
            "MaxSessionBytes={3};HeartbeatCheckpointSeconds={4};EmergencySpoolMaxBytes={5}",
            RetentionDays, RetainSessionCount, MaxTotalBytes, MaxSessionBytes,
            HeartbeatCheckpointSeconds, EmergencySpoolMaxBytes);

        private static int ParseInt(string value, int fallback, int minimum, int maximum,
            string name, Action<string> warningSink)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= minimum && parsed <= maximum)
                return parsed;
            warningSink?.Invoke(string.IsNullOrWhiteSpace(value)
                ? $"{name} 缺失，已回退 {fallback}。Value=<missing>"
                : $"{name} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }

        private static long ParseLong(string value, long fallback, long minimum, long maximum,
            string name, Action<string> warningSink)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= minimum && parsed <= maximum)
                return parsed;
            warningSink?.Invoke(string.IsNullOrWhiteSpace(value)
                ? $"{name} 缺失，已回退 {fallback}。Value=<missing>"
                : $"{name} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }
    }

    public sealed class WatchdogJournalEvent
    {
        public int SchemaVersion { get; set; } = WatchdogJournalPolicy.CurrentSchemaVersion;
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");
        public long EventSequence { get; set; }
        public string Utc { get; set; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        public string Source { get; set; }
        public string EventType { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long HeartbeatSequence { get; set; }
        public long AckSequence { get; set; }
        public int RecoveryAttempt { get; set; }
        public bool ManualStopRequested { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        public string RecoveryStage { get; set; }
        public string RootCode { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }
        public string RecoveryFailureFingerprint { get; set; }
        public string RecoveryIncident { get; set; }
        public string RecoveryContext { get; set; }
        // Schema4 durable process-relaunch permit evidence.  These fields are
        // additive; old event readers can ignore them while v4 validators
        // refuse to infer a runnable permit from prose/PID alone.
        public string RelaunchState { get; set; }
        public long RelaunchGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonce { get; set; }
        public string RelaunchFingerprint { get; set; }
        public string RelaunchProgressToken { get; set; }
        public string RelaunchProcessSource { get; set; }
        public string RelaunchRunId { get; set; }
        public string RelaunchRecoveryStage { get; set; }
        public int RelaunchProcessId { get; set; }
        public long RelaunchProcessStartUtcTicks { get; set; }
        public int RelaunchConsecutiveFailures { get; set; }
        public int RelaunchMaximumProcessRelaunches { get; set; }
        public long RelaunchRecoveryCommitGeneration { get; set; }
        public string RelaunchFailureCode { get; set; }
        public string RelaunchFailureReason { get; set; }
        public int[] EnabledChannels { get; set; } = Array.Empty<int>();
        public int[] EligibleChannels { get; set; } = Array.Empty<int>();
        public int[] CompletedChannels { get; set; } = Array.Empty<int>();
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        public long DroppedEventCount { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>
    /// Schema migration is intentionally additive.  A V2 event is accepted,
    /// upgraded in memory to V4, and all existing state (including blocked
    /// recovery evidence) is retained.  Unknown/newer schemas are rejected by
    /// the readers instead of being guessed.
    /// </summary>
    public static class WatchdogJournalMigration
    {
        public static WatchdogJournalEvent MigrateEvent(WatchdogJournalEvent value)
        {
            if (value == null) return null;
            if (value.SchemaVersion == WatchdogJournalPolicy.CurrentSchemaVersion)
                return value;
            if (value.SchemaVersion == 2 || value.SchemaVersion == 3)
            {
                value.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
                return value;
            }
            return null;
        }

        public static string MigrateEventJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var value = Json.Deserialize<WatchdogJournalEvent>(json);
                var migrated = MigrateEvent(value);
                return migrated == null ? null : Json.Serialize(migrated);
            }
            catch { return null; }
        }

        public static string MigrateJournalJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var values = Json.DeserializeObject(json) as Dictionary<string, object>;
                if (values == null) return null;
                var schema = 0;
                if (values.TryGetValue("SchemaVersion", out var rawSchema))
                    schema = Convert.ToInt32(rawSchema, CultureInfo.InvariantCulture);
                if (schema == 2 || schema == 3)
                {
                    // Preserve every legacy field, especially RecoveryBlocked
                    // and its failure evidence; only the schema marker changes.
                    values["SchemaVersion"] = WatchdogJournalPolicy.CurrentSchemaVersion;
                    return Json.Serialize(values);
                }
                return schema == WatchdogJournalPolicy.CurrentSchemaVersion
                    ? json
                    : null;
            }
            catch { return null; }
        }

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    }

    public sealed class WatchdogJournalLease
    {
        public int SchemaVersion { get; set; } = WatchdogJournalPolicy.CurrentSchemaVersion;
        public string SessionId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string UpdatedUtc { get; set; }
        public string RelaunchState { get; set; }
        public long RelaunchGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
    }

    public sealed class WatchdogSessionManifest
    {
        public int SchemaVersion { get; set; } = WatchdogJournalPolicy.CurrentSchemaVersion;
        public string SessionId { get; set; }
        public string TerminalUtc { get; set; }
        public string TerminalState { get; set; }
        public string Reason { get; set; }
        public long DroppedEventCount { get; set; }
        public long TotalBytes { get; set; }
        public string RelaunchState { get; set; }
        public long RelaunchGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
    }

    /// <summary>
    /// Controls which classes of journal state a store is allowed to own.
    /// FullAuthority is the historical sidecar/bootstrap behavior.  A
    /// recovery client may only append its own audit events/errors and must
    /// never replay or mutate the authority snapshot, lease, or terminal
    /// manifest.
    /// </summary>
    public enum WatchdogJournalStoreMode
    {
        FullAuthority = 0,
        ClientAuditOnly = 1
    }

    public static class WatchdogJournalPaths
    {
        public static string ValidateProjectDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Watchdog 项目日志目录不能为空。", nameof(directory));
            var full = Path.GetFullPath(directory.Trim());
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root) ||
                string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Watchdog 项目日志目录不能是卷根目录。", nameof(directory));
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string LocalControlDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTTFTest", "WatchdogControlV2");

        public static string LocalRevocationPath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".revoked");

        public static string ProjectRevocationPath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".revoked");

        public static string LocalClosingPath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".closing.json");

        public static string ProjectClosingPath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".closing.json");

        public static string LocalManualPausePath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".manual-pause.json");

        public static string ProjectManualPausePath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".manual-pause.json");

        public static string LocalSafetyHandoffPath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".safety-handoff.json");

        public static string ProjectSafetyHandoffPath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".safety-handoff.json");

        public static string LocalApplicationExitPath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".application-exit.json");

        public static string ProjectApplicationExitPath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".application-exit.json");

        public static string LocalRecoveryCommitPath(string sessionId) => Path.Combine(
            LocalControlDirectory, "session-" + SafeName(sessionId) + ".recovery-committed");

        public static string ProjectRecoveryCommitPath(string directory, string sessionId) => Path.Combine(
            ValidateProjectDirectory(directory), "session-" + SafeName(sessionId) + ".recovery-committed");

        public static string LocalSpoolDirectory(string projectDirectory, string sessionId) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MTTFTest", "WatchdogSpoolV2", StablePathToken(ValidateProjectDirectory(projectDirectory)),
            "session-" + SafeName(sessionId));

        /// <summary>
        /// Emergency evidence owned by a ClientAuditOnly store is kept below a
        /// per-session child directory.  The parent session spool is reserved
        /// for the authority's snapshot/lease/terminal pending files; keeping
        /// the two namespaces physically separate prevents an audit client
        /// from ever budget-scanning or deleting authority recovery state.
        /// </summary>
        public static string ClientAuditSpoolDirectory(string projectDirectory, string sessionId) => Path.Combine(
            LocalSpoolDirectory(projectDirectory, sessionId), "client-audit");

        public static string SafeName(string value)
        {
            var result = value ?? string.Empty;
            foreach (var invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
            return result;
        }

        internal static string StablePathToken(string value)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var character in (value ?? string.Empty).ToUpperInvariant())
                {
                    hash ^= character;
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }
    }

    public static class WatchdogControlMarker
    {
        public static void WriteLocal(string sessionId, string reason) =>
            AtomicWrite(WatchdogJournalPaths.LocalRevocationPath(sessionId), reason);

        public static void WriteProject(string projectDirectory, string sessionId, string reason) =>
            AtomicWrite(WatchdogJournalPaths.ProjectRevocationPath(projectDirectory, sessionId), reason);

        public static bool IsRevoked(string projectDirectory, string sessionId)
        {
            try
            {
                return File.Exists(WatchdogJournalPaths.LocalRevocationPath(sessionId)) ||
                       (!string.IsNullOrWhiteSpace(projectDirectory) &&
                        File.Exists(WatchdogJournalPaths.ProjectRevocationPath(projectDirectory, sessionId)));
            }
            catch { return false; }
        }

        public static void CleanupLocal(int retentionDays, long maxBytes)
        {
            try
            {
                var directory = new DirectoryInfo(WatchdogJournalPaths.LocalControlDirectory);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
                var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
                var files = directory.GetFiles("session-*.revoked", SearchOption.TopDirectoryOnly)
                    .Concat(directory.GetFiles("session-*.recovery-committed", SearchOption.TopDirectoryOnly))
                    .Concat(directory.GetFiles("session-*.closing.json", SearchOption.TopDirectoryOnly))
                    .Concat(directory.GetFiles("session-*.manual-pause.json", SearchOption.TopDirectoryOnly))
                    .Concat(directory.GetFiles("session-*.safety-handoff.json", SearchOption.TopDirectoryOnly))
                    .Concat(directory.GetFiles("session-*.application-exit.json", SearchOption.TopDirectoryOnly))
                    .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(file => file.LastWriteTimeUtc).ToList();
                foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff).ToArray())
                {
                    TryDelete(file.FullName);
                    files.Remove(file);
                }
                var total = files.Sum(file => file.Exists ? file.Length : 0);
                foreach (var file in files)
                {
                    if (total <= Math.Max(1024L * 1024L, maxBytes)) break;
                    var length = file.Exists ? file.Length : 0;
                    TryDelete(file.FullName);
                    total -= length;
                }
            }
            catch { }
        }

        private static void AtomicWrite(string path, string reason)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("撤权marker目录无效。");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + (reason ?? string.Empty),
                    new UTF8Encoding(false));
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// 恢复批次已提交的跨进程耐久旁路。命名管道只负责低延迟通知；即使双向管道
    /// 正在重连，Sidecar 仍能从本机或项目目录确认新 Run 已经正式提交并关闭过渡窗。
    /// </summary>
    public sealed class WatchdogRecoveryCommitEvidence
    {
        public int SchemaVersion { get; set; }
        public long Generation { get; set; }
        public long GeneratedUtcTicks { get; set; }
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public string Stage { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string ContentSha256 { get; set; } = string.Empty;
        public bool Legacy { get; set; }
    }

    public static class WatchdogRecoveryCommitMarker
    {
        public static void WriteLocal(string sessionId, long generation, string reason) =>
            AtomicWriteLegacy(
                WatchdogJournalPaths.LocalRecoveryCommitPath(sessionId),
                generation,
                reason);

        public static void WriteLocal(
            string sessionId,
            long generation,
            string reason,
            string runId,
            long runEpoch,
            string stage) =>
            AtomicWrite(
                WatchdogJournalPaths.LocalRecoveryCommitPath(sessionId),
                generation,
                reason,
                runId,
                runEpoch,
                stage);

        public static void WriteProject(
            string projectDirectory,
            string sessionId,
            long generation,
            string reason) =>
            AtomicWriteLegacy(
                WatchdogJournalPaths.ProjectRecoveryCommitPath(projectDirectory, sessionId),
                generation,
                reason);

        public static void WriteProject(
            string projectDirectory,
            string sessionId,
            long generation,
            string reason,
            string runId,
            long runEpoch,
            string stage) =>
            AtomicWrite(
                WatchdogJournalPaths.ProjectRecoveryCommitPath(projectDirectory, sessionId),
                generation,
                reason,
                runId,
                runEpoch,
                stage);

        public static bool TryRead(string projectDirectory, string sessionId, out long generation)
        {
            var read = TryRead(
                projectDirectory,
                sessionId,
                out WatchdogRecoveryCommitEvidence evidence);
            generation = evidence?.Generation ?? 0;
            return read;
        }

        public static bool TryRead(
            string projectDirectory,
            string sessionId,
            out WatchdogRecoveryCommitEvidence evidence)
        {
            evidence = null;
            foreach (var path in CandidatePaths(projectDirectory, sessionId))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string content;
                    using (var stream = new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), true))
                        content = reader.ReadToEnd();
                    var parsed = Parse(content);
                    if (parsed != null && parsed.Generation > (evidence?.Generation ?? 0))
                        evidence = parsed;
                }
                catch { }
            }
            return evidence != null && evidence.Generation > 0;
        }

        public static void Archive(
            string projectDirectory,
            string sessionId,
            long generation,
            string disposition)
        {
            foreach (var path in CandidatePaths(projectDirectory, sessionId))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var archive = path + "." + generation.ToString(CultureInfo.InvariantCulture) +
                                  "." + NormalizeToken(disposition) + ".processed";
                    if (File.Exists(archive)) File.Delete(archive);
                    File.Move(path, archive);
                }
                catch { }
            }
        }

        private static IEnumerable<string> CandidatePaths(string projectDirectory, string sessionId)
        {
            yield return WatchdogJournalPaths.LocalRecoveryCommitPath(sessionId);
            if (string.IsNullOrWhiteSpace(projectDirectory)) yield break;
            string projectPath = null;
            try { projectPath = WatchdogJournalPaths.ProjectRecoveryCommitPath(projectDirectory, sessionId); }
            catch { }
            if (!string.IsNullOrWhiteSpace(projectPath)) yield return projectPath;
        }

        private static void AtomicWrite(
            string path,
            long generation,
            string reason,
            string runId,
            long runEpoch,
            string stage)
        {
            if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("恢复提交marker目录无效。");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var ticks = DateTime.UtcNow.Ticks;
                var normalizedRunId = Guid.TryParse(runId, out var parsedRunId)
                    ? parsedRunId.ToString("N")
                    : string.Empty;
                var encodedStage = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(stage ?? string.Empty));
                var encodedReason = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(reason ?? string.Empty));
                var canonical = string.Join("|",
                    "2",
                    generation.ToString(CultureInfo.InvariantCulture),
                    ticks.ToString(CultureInfo.InvariantCulture),
                    normalizedRunId,
                    Math.Max(0, runEpoch).ToString(CultureInfo.InvariantCulture),
                    encodedStage,
                    encodedReason);
                var payload = canonical + "|" + ComputeSha256(canonical);
                File.WriteAllText(temporary, payload, new UTF8Encoding(false));
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static void AtomicWriteLegacy(string path, long generation, string reason)
        {
            if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("恢复提交marker目录无效。");
            Directory.CreateDirectory(directory);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var payload = generation.ToString(CultureInfo.InvariantCulture) + "|" +
                              DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "|" +
                              (reason ?? string.Empty);
                File.WriteAllText(temporary, payload, new UTF8Encoding(false));
                using (var stream = new FileStream(
                           temporary,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        private static WatchdogRecoveryCommitEvidence Parse(string content)
        {
            var tokens = (content ?? string.Empty).Split('|');
            if (tokens.Length >= 8 && tokens[0] == "2" &&
                long.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation) &&
                long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runEpoch))
            {
                var canonical = string.Join("|", tokens.Take(7));
                if (!string.Equals(
                        ComputeSha256(canonical),
                        tokens[7],
                        StringComparison.OrdinalIgnoreCase))
                    return null;
                try
                {
                    return new WatchdogRecoveryCommitEvidence
                    {
                        SchemaVersion = 2,
                        Generation = generation,
                        GeneratedUtcTicks = ticks,
                        RunId = tokens[3],
                        RunEpoch = runEpoch,
                        Stage = Encoding.UTF8.GetString(Convert.FromBase64String(tokens[5])),
                        Reason = Encoding.UTF8.GetString(Convert.FromBase64String(tokens[6])),
                        ContentSha256 = tokens[7],
                        Legacy = false
                    };
                }
                catch { return null; }
            }

            var separator = (content ?? string.Empty).IndexOf('|');
            var token = separator < 0 ? content : content.Substring(0, separator);
            if (!long.TryParse(
                    token,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var legacyGeneration) || legacyGeneration <= 0)
                return null;
            return new WatchdogRecoveryCommitEvidence
            {
                SchemaVersion = 1,
                Generation = legacyGeneration,
                Legacy = true
            };
        }

        private static string ComputeSha256(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(
                        sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty);
        }

        private static string NormalizeToken(string value)
        {
            var token = Regex.Replace(value ?? "Processed", "[^A-Za-z0-9_-]", "_");
            return string.IsNullOrWhiteSpace(token) ? "Processed" : token;
        }
    }

    /// <summary>
    /// Bounded, best-effort journal writer.  All filesystem work happens on its
    /// private worker so project or network storage cannot block watchdog IPC.
    /// </summary>
    public sealed class WatchdogJournalStore : IDisposable
    {
        private const int MaxQueuedEvents = 1024;
        private const long SidecarEventSegmentBytes = 3L * 1024L * 1024L;
        private const int SidecarEventArchiveCount = 3;
        private const long ClientEventSegmentBytes = 512L * 1024L;
        private const int ClientEventArchiveCount = 1;
        private const long ErrorSegmentBytes = 1024L * 1024L;
        private const int ErrorArchiveCount = 1;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly Regex OwnedFile = new Regex(
            @"^session-(?<id>[0-9a-f]{32})\.(json|bootstrap\.json|bootstrap-outcome-\d+\.json|relaunch\.json|lease\.json|manifest\.json|closing\.json|manual-pause\.json|safety-handoff\.json|revoked|recovery-committed|sidecar-events(?:\.\d+)?\.jsonl|client-events(?:\.\d+)?\.jsonl|errors(?:\.\d+)?\.log)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly object _gate = new object();
        private readonly Queue<PendingEvent> _events = new Queue<PendingEvent>();
        private readonly Queue<string> _errors = new Queue<string>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private readonly Thread _worker;
        private readonly string _directory;
        private readonly string _sessionId;
        private readonly string _safeSession;
        private readonly string _source;
        private readonly WatchdogJournalStoreMode _mode;
        private readonly WatchdogJournalPolicy _policy;
        private readonly int _processId;
        private readonly long _processStartTicks;
        private readonly string _spoolDirectory;
        private string _snapshot;
        private bool _leasePending = true;
        private string _revocationReason;
        private PendingTerminal _terminal;
        private bool _retentionPending = true;
        private bool _stopping;
        private long _droppedEvents;
        private long _lastLeaseUtcTicks;
        private int _spoolWritesSinceBudget;

        private sealed class PendingEvent
        {
            public string Json;
            public string EventId;
            public bool Checkpoint;
        }

        private sealed class PendingTerminal
        {
            public string State { get; set; }
            public string Reason { get; set; }
        }

        private sealed class SessionGroup
        {
            public string SessionId;
            public List<FileInfo> Files = new List<FileInfo>();
            public DateTime LastWriteUtc;
            public long Bytes;
            public bool Supported;
            public bool Live;
        }

        public WatchdogJournalStore(
            string directory,
            string sessionId,
            string source,
            WatchdogJournalPolicy policy,
            int processId,
            long processStartTicks,
            WatchdogJournalStoreMode mode = WatchdogJournalStoreMode.FullAuthority)
        {
            _directory = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            if (!Guid.TryParseExact(sessionId, "N", out _))
                throw new ArgumentException("Watchdog SessionId 必须是 N 格式 GUID。", nameof(sessionId));
            _sessionId = sessionId.ToLowerInvariant();
            _safeSession = WatchdogJournalPaths.SafeName(_sessionId);
            _source = string.Equals(source, "client", StringComparison.OrdinalIgnoreCase) ? "client" : "sidecar";
            _mode = mode;
            _leasePending = _mode == WatchdogJournalStoreMode.FullAuthority && _source == "sidecar";
            _retentionPending = _mode == WatchdogJournalStoreMode.FullAuthority;
            _policy = (policy ?? new WatchdogJournalPolicy()).Normalize();
            _processId = processId;
            _processStartTicks = processStartTicks;
            _spoolDirectory = IsClientAuditOnly
                ? WatchdogJournalPaths.ClientAuditSpoolDirectory(_directory, _sessionId)
                : WatchdogJournalPaths.LocalSpoolDirectory(_directory, _sessionId);
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "WatchdogJournal-" + _source,
                Priority = ThreadPriority.BelowNormal
            };
            _worker.Start();
        }

        public long DroppedEventCount => Interlocked.Read(ref _droppedEvents);

        public WatchdogJournalStoreMode Mode => _mode;

        private bool IsClientAuditOnly => _mode == WatchdogJournalStoreMode.ClientAuditOnly;

        private bool RejectAuthorityOperation(string operation)
        {
            // Keep the rejection observable through the permitted audit
            // channel; callers also receive false from the bool APIs.
            RecordError("ClientAuditOnlyRejected:" + (operation ?? "Unknown"));
            return false;
        }

        public bool PublishSnapshot(string json)
        {
            if (IsClientAuditOnly) return RejectAuthorityOperation("PublishSnapshot");
            if (string.IsNullOrWhiteSpace(json)) return false;
            lock (_gate)
            {
                if (_stopping) return false;
                _snapshot = json;
                if (DateTime.UtcNow.Ticks - _lastLeaseUtcTicks >= TimeSpan.FromSeconds(30).Ticks)
                    _leasePending = true;
                _idle.Reset();
            }
            _wake.Set();
            return true;
        }

        /// <summary>
        /// Synchronously commits a safety-critical snapshot.  The ordinary
        /// PublishSnapshot path is intentionally asynchronous for heartbeat
        /// throughput; recovery-block decisions must use this path before any
        /// process relaunch is scheduled.  A failure is returned to the
        /// caller, which must fail closed in memory and refuse relaunch.
        /// </summary>
        public bool TryPublishSnapshotSynchronously(string json)
        {
            if (IsClientAuditOnly) return RejectAuthorityOperation("TryPublishSnapshotSynchronously");
            if (string.IsNullOrWhiteSpace(json)) return false;
            lock (_gate)
            {
                if (_stopping) return false;
                try
                {
                    Directory.CreateDirectory(_directory);
                    AtomicWrite(
                        Path.Combine(_directory, "session-" + _safeSession + ".json"),
                        json);
                    _snapshot = null;
                    _idle.Set();
                    TryDelete(Path.Combine(_spoolDirectory, "session.snapshot.pending.json"));
                    return true;
                }
                catch
                {
                    // Keep the emergency spool attempt for later replay, but
                    // report false so the caller cannot treat this as durable.
                    TrySpoolAtomic("session.snapshot.pending.json", json);
                    return false;
                }
            }
        }

        public bool Record(WatchdogJournalEvent value, bool checkpoint = false)
        {
            if (value == null) return false;
            if (IsClientAuditOnly && !string.Equals(_source, "client", StringComparison.OrdinalIgnoreCase))
                return RejectAuthorityOperation("NonClientRecord");
            value.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
            value.SessionId = _sessionId;
            value.Source = _source;
            value.EventId = string.IsNullOrWhiteSpace(value.EventId)
                ? Guid.NewGuid().ToString("N")
                : value.EventId;
            value.Utc = string.IsNullOrWhiteSpace(value.Utc)
                ? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : value.Utc;
            value.EventType = Truncate(value.EventType, 256);
            value.State = Truncate(value.State, 256);
            value.Reason = Truncate(value.Reason, 16 * 1024);
            value.RunId = Truncate(value.RunId, 256);
            value.RecoveryStage = Truncate(value.RecoveryStage, 1024);
            value.RootCode = Truncate(value.RootCode, 256);
            value.DeviceOrChannelGroup = Truncate(value.DeviceOrChannelGroup, 1024);
            value.RecoveryProgressToken = Truncate(value.RecoveryProgressToken, 256);
            value.RecoveryProcessSource = Truncate(value.RecoveryProcessSource, 128);
            value.RecoveryFailureFingerprint = Truncate(value.RecoveryFailureFingerprint, 128);
            value.RecoveryIncident = Truncate(value.RecoveryIncident, 4096);
            value.RecoveryContext = Truncate(value.RecoveryContext, 16 * 1024);
            value.RelaunchState = Truncate(value.RelaunchState, 64);
            value.RelaunchPermitId = Truncate(value.RelaunchPermitId, 128);
            value.RelaunchPermitNonce = Truncate(value.RelaunchPermitNonce, 128);
            value.RelaunchFingerprint = Truncate(value.RelaunchFingerprint, 128);
            value.RelaunchProgressToken = Truncate(value.RelaunchProgressToken, 256);
            value.RelaunchProcessSource = Truncate(value.RelaunchProcessSource, 128);
            value.RelaunchRunId = Truncate(value.RelaunchRunId, 256);
            value.RelaunchRecoveryStage = Truncate(value.RelaunchRecoveryStage, 1024);
            value.RelaunchFailureCode = Truncate(value.RelaunchFailureCode, 256);
            value.RelaunchFailureReason = Truncate(value.RelaunchFailureReason, 16 * 1024);
            value.Detail = Truncate(value.Detail, 64 * 1024);
            value.EnabledChannels = Limit(value.EnabledChannels);
            value.EligibleChannels = Limit(value.EligibleChannels);
            value.CompletedChannels = Limit(value.CompletedChannels);
            value.PermanentAlarmedChannels = Limit(value.PermanentAlarmedChannels);
            value.DroppedEventCount = DroppedEventCount;
            var pending = new PendingEvent
            {
                Json = Json.Serialize(value),
                EventId = value.EventId,
                Checkpoint = checkpoint
            };
            lock (_gate)
            {
                if (_stopping) return false;
                if (_events.Count >= MaxQueuedEvents)
                {
                    if (checkpoint)
                    {
                        Interlocked.Increment(ref _droppedEvents);
                        return false;
                    }
                    var retained = _events.Where(item => !item.Checkpoint).ToArray();
                    if (retained.Length == _events.Count && _events.Count > 0)
                    {
                        _events.Dequeue();
                        Interlocked.Increment(ref _droppedEvents);
                    }
                    else
                    {
                        _events.Clear();
                        foreach (var item in retained) _events.Enqueue(item);
                        Interlocked.Add(ref _droppedEvents, MaxQueuedEvents - retained.Length);
                    }
                }
                _events.Enqueue(pending);
                _idle.Reset();
            }
            _wake.Set();
            return true;
        }

        public void RecordError(string message)
        {
            lock (_gate)
            {
                if (_stopping) return;
                if (_errors.Count >= 128)
                {
                    _errors.Dequeue();
                    Interlocked.Increment(ref _droppedEvents);
                }
                _errors.Enqueue(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " +
                    Truncate(message, 64 * 1024));
                _idle.Reset();
            }
            _wake.Set();
        }

        public bool PublishTerminal(string state, string reason)
        {
            if (IsClientAuditOnly) return RejectAuthorityOperation("PublishTerminal");
            lock (_gate)
            {
                if (_stopping) return false;
                _terminal = new PendingTerminal { State = state ?? string.Empty, Reason = reason ?? string.Empty };
                _retentionPending = true;
                _idle.Reset();
            }
            _wake.Set();
            return true;
        }

        public bool PublishRevocation(string reason)
        {
            lock (_gate)
            {
                if (_stopping) return false;
                _revocationReason = reason ?? string.Empty;
                _idle.Reset();
            }
            _wake.Set();
            return true;
        }

        public bool Flush(TimeSpan timeout)
        {
            _wake.Set();
            return _idle.Wait(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        }

        public bool RequestRetention()
        {
            if (IsClientAuditOnly) return RejectAuthorityOperation("RequestRetention");
            lock (_gate)
            {
                if (_stopping) return false;
                _retentionPending = true;
                _idle.Reset();
            }
            _wake.Set();
            return true;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                _wake.WaitOne(1000);
                string snapshot;
                bool lease;
                PendingTerminal terminal;
                string revocationReason;
                bool retention;
                PendingEvent[] events;
                string[] errors;
                bool stopping;
                lock (_gate)
                {
                    snapshot = _snapshot;
                    _snapshot = null;
                    lease = _leasePending;
                    _leasePending = false;
                    terminal = _terminal;
                    _terminal = null;
                    revocationReason = _revocationReason;
                    _revocationReason = null;
                    retention = _retentionPending;
                    _retentionPending = false;
                    events = _events.ToArray();
                    _events.Clear();
                    errors = _errors.ToArray();
                    _errors.Clear();
                    stopping = _stopping;
                }

                TryReplaySpool();
                if (!IsClientAuditOnly && lease) TryWriteLease();
                if (!IsClientAuditOnly && snapshot != null) TryWriteSnapshot(snapshot);
                foreach (var item in events)
                {
                    if (item.Checkpoint && IsStopping())
                    {
                        Interlocked.Increment(ref _droppedEvents);
                        continue;
                    }
                    TryWriteEvent(item);
                }
                foreach (var error in errors) TryWriteError(error);
                if (revocationReason != null) TryWriteProjectRevocation(revocationReason);
                if (!IsClientAuditOnly && terminal != null) TryPublishTerminal(terminal);
                if (!IsClientAuditOnly && retention && _source == "sidecar") TryEnforceRetention();
                // Both authority and audit stores have an independent bounded
                // emergency spool.  EnforceSpoolBudget selects the historical
                // authority root for FullAuthority and this store's private
                // client-audit directory for ClientAuditOnly.
                if (Interlocked.Exchange(ref _spoolWritesSinceBudget, 0) > 0)
                    EnforceSpoolBudget();

                lock (_gate)
                {
                    if (_snapshot == null && !_leasePending && _revocationReason == null &&
                        _terminal == null && !_retentionPending &&
                        _events.Count == 0 && _errors.Count == 0)
                        _idle.Set();
                    if (stopping && _idle.IsSet) break;
                }
            }
        }

        private void TryWriteSnapshot(string content)
        {
            if (IsClientAuditOnly) return;
            try
            {
                Directory.CreateDirectory(_directory);
                AtomicWrite(Path.Combine(_directory, "session-" + _safeSession + ".json"), content);
                var pending = Path.Combine(_spoolDirectory, "session.snapshot.pending.json");
                TryDelete(pending);
            }
            catch
            {
                TrySpoolAtomic("session.snapshot.pending.json", content);
            }
        }

        private void TryWriteLease()
        {
            if (IsClientAuditOnly) return;
            var lease = Json.Serialize(new WatchdogJournalLease
            {
                SessionId = _sessionId,
                ProcessId = _processId,
                ProcessStartUtcTicks = _processStartTicks,
                UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            try
            {
                Directory.CreateDirectory(_directory);
                AtomicWrite(Path.Combine(_directory, "session-" + _safeSession + ".lease.json"), lease);
                _lastLeaseUtcTicks = DateTime.UtcNow.Ticks;
            }
            catch
            {
                TrySpoolAtomic("session.lease.pending.json", lease);
            }
        }

        private void TryWriteEvent(PendingEvent item)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = EventPath(_source);
                AppendRotated(path, item.Json + Environment.NewLine,
                    _source == "client" ? ClientEventSegmentBytes : SidecarEventSegmentBytes,
                    _source == "client" ? ClientEventArchiveCount : SidecarEventArchiveCount);
                TryDelete(Path.Combine(_spoolDirectory, "event-" + item.EventId + ".pending.json"));
                EnforceCurrentSessionBudget();
            }
            catch
            {
                TrySpoolAtomic("event-" + item.EventId + ".pending.json", item.Json);
            }
        }

        private void TryWriteError(string value)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                AppendRotated(Path.Combine(_directory, "session-" + _safeSession + ".errors.log"),
                    value + Environment.NewLine, ErrorSegmentBytes, ErrorArchiveCount);
                EnforceCurrentSessionBudget();
            }
            catch
            {
                TrySpoolAtomic("error-" + Guid.NewGuid().ToString("N") + ".pending.log", value);
            }
        }

        private bool TryPublishTerminal(PendingTerminal value)
        {
            if (IsClientAuditOnly) return false;
            try
            {
                Directory.CreateDirectory(_directory);
                var manifest = new WatchdogSessionManifest
                {
                    SessionId = _sessionId,
                    TerminalUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    TerminalState = value.State,
                    Reason = value.Reason,
                    DroppedEventCount = DroppedEventCount,
                    TotalBytes = SessionFiles(_sessionId).Sum(file => SafeLength(file))
                };
                AtomicWrite(Path.Combine(_directory, "session-" + _safeSession + ".manifest.json"),
                    Json.Serialize(manifest));
                TryDelete(Path.Combine(_directory, "session-" + _safeSession + ".lease.json"));
                return true;
            }
            catch
            {
                TrySpoolAtomic("session.terminal.pending.json", Json.Serialize(value));
                return false;
            }
        }

        private void TryWriteProjectRevocation(string reason)
        {
            try
            {
                WatchdogControlMarker.WriteProject(_directory, _sessionId, reason);
                TryDelete(Path.Combine(_spoolDirectory, "session.revoked.pending.txt"));
            }
            catch { TrySpoolAtomic("session.revoked.pending.txt", reason ?? string.Empty); }
        }

        private void TryReplaySpool()
        {
            if (IsClientAuditOnly)
            {
                TryReplayAuditSpool();
                return;
            }
            try
            {
                if (!Directory.Exists(_spoolDirectory)) return;
                Directory.CreateDirectory(_directory);
                foreach (var file in new DirectoryInfo(_spoolDirectory).GetFiles("event-*.pending.json")
                             .OrderBy(value => value.CreationTimeUtc))
                {
                    var json = File.ReadAllText(file.FullName, Encoding.UTF8);
                    var id = Path.GetFileName(file.Name).Substring("event-".Length);
                    id = id.Substring(0, id.Length - ".pending.json".Length);
                    if (!ContainsValidEventId(id)) AppendRotated(EventPath(_source), json + Environment.NewLine,
                        _source == "client" ? ClientEventSegmentBytes : SidecarEventSegmentBytes,
                        _source == "client" ? ClientEventArchiveCount : SidecarEventArchiveCount);
                    file.Delete();
                }
                var snapshot = Path.Combine(_spoolDirectory, "session.snapshot.pending.json");
                if (File.Exists(snapshot))
                {
                    AtomicWrite(Path.Combine(_directory, "session-" + _safeSession + ".json"),
                        File.ReadAllText(snapshot, Encoding.UTF8));
                    File.Delete(snapshot);
                }
                var lease = Path.Combine(_spoolDirectory, "session.lease.pending.json");
                if (File.Exists(lease))
                {
                    AtomicWrite(Path.Combine(_directory, "session-" + _safeSession + ".lease.json"),
                        File.ReadAllText(lease, Encoding.UTF8));
                    File.Delete(lease);
                }
                var terminal = Path.Combine(_spoolDirectory, "session.terminal.pending.json");
                if (File.Exists(terminal))
                {
                    var value = Json.Deserialize<PendingTerminal>(File.ReadAllText(terminal, Encoding.UTF8));
                    if (TryPublishTerminal(value ?? new PendingTerminal
                    {
                        State = "UnknownTerminal",
                        Reason = "RecoveredFromEmergencySpool"
                    })) File.Delete(terminal);
                }
                var revoked = Path.Combine(_spoolDirectory, "session.revoked.pending.txt");
                if (File.Exists(revoked))
                {
                    WatchdogControlMarker.WriteProject(
                        _directory, _sessionId, File.ReadAllText(revoked, Encoding.UTF8));
                    File.Delete(revoked);
                }
                foreach (var file in new DirectoryInfo(_spoolDirectory).GetFiles("error-*.pending.log")
                             .OrderBy(value => value.CreationTimeUtc))
                {
                    AppendRotated(Path.Combine(_directory, "session-" + _safeSession + ".errors.log"),
                        File.ReadAllText(file.FullName, Encoding.UTF8) + Environment.NewLine,
                        ErrorSegmentBytes, ErrorArchiveCount);
                    file.Delete();
                }
                if (!Directory.EnumerateFileSystemEntries(_spoolDirectory).Any())
                    Directory.Delete(_spoolDirectory);
            }
            catch { }
        }

        private void TryReplayAuditSpool()
        {
            try
            {
                if (!Directory.Exists(_spoolDirectory)) return;

                // AuditOnly may recover only client event/error evidence.  In
                // particular, leave snapshot/lease/terminal pending files in
                // place: consuming them would transfer authority to the
                // recovery client.
                foreach (var file in new DirectoryInfo(_spoolDirectory).GetFiles("event-*.pending.json")
                             .OrderBy(value => value.CreationTimeUtc))
                {
                    var json = File.ReadAllText(file.FullName, Encoding.UTF8);
                    var id = Path.GetFileName(file.Name).Substring("event-".Length);
                    id = id.Substring(0, id.Length - ".pending.json".Length);
                    if (!ContainsValidEventId(id))
                        AppendRotated(EventPath("client"), json + Environment.NewLine,
                            ClientEventSegmentBytes, ClientEventArchiveCount);
                    file.Delete();
                }
                foreach (var file in new DirectoryInfo(_spoolDirectory).GetFiles("error-*.pending.log")
                             .OrderBy(value => value.CreationTimeUtc))
                {
                    AppendRotated(Path.Combine(_directory, "session-" + _safeSession + ".errors.log"),
                        File.ReadAllText(file.FullName, Encoding.UTF8) + Environment.NewLine,
                        ErrorSegmentBytes, ErrorArchiveCount);
                    file.Delete();
                }

                // Revocation is an existing explicit safety marker, not a
                // session-authority transition, so it remains permitted.
                var revoked = Path.Combine(_spoolDirectory, "session.revoked.pending.txt");
                if (File.Exists(revoked))
                {
                    WatchdogControlMarker.WriteProject(
                        _directory, _sessionId, File.ReadAllText(revoked, Encoding.UTF8));
                    File.Delete(revoked);
                }
            }
            catch { }
        }

        private void TrySpoolAtomic(string name, string content)
        {
            try
            {
                Directory.CreateDirectory(_spoolDirectory);
                AtomicWrite(Path.Combine(_spoolDirectory, name), content ?? string.Empty);
                Interlocked.Increment(ref _spoolWritesSinceBudget);
            }
            catch { Interlocked.Increment(ref _droppedEvents); }
        }

        private void EnforceSpoolBudget()
        {
            try
            {
                // Preserve FullAuthority's historical cross-session budget
                // behavior.  AuditOnly must never enumerate the parent spool:
                // it is allowed to see only its own client-audit directory.
                var root = IsClientAuditOnly
                    ? _spoolDirectory
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MTTFTest", "WatchdogSpoolV2");
                if (!Directory.Exists(root)) return;
                var cutoff = DateTime.UtcNow.AddDays(-WatchdogJournalPolicy.EmergencySpoolRetentionDays);
                var searchOption = IsClientAuditOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                var files = new DirectoryInfo(root).EnumerateFiles("*.pending.*", searchOption)
                    .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ToList();
                foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff).ToArray())
                {
                    TryDelete(file.FullName);
                    files.Remove(file);
                }
                var total = files.Sum(SafeLength);
                foreach (var file in files)
                {
                    if (total <= _policy.EmergencySpoolMaxBytes) break;
                    var length = SafeLength(file);
                    TryDelete(file.FullName);
                    total -= length;
                    Interlocked.Increment(ref _droppedEvents);
                }
            }
            catch { }
        }

        private void TryEnforceRetention()
        {
            try
            {
                WatchdogControlMarker.CleanupLocal(
                    WatchdogJournalPolicy.EmergencySpoolRetentionDays,
                    _policy.EmergencySpoolMaxBytes);
                if (!Directory.Exists(_directory)) return;
                var root = new DirectoryInfo(_directory);
                if ((root.Attributes & FileAttributes.ReparsePoint) != 0) return;
                using (var mutex = new Mutex(false,
                           "Local\\MTTFTest.Watchdog.Retention." + WatchdogJournalPaths.StablePathToken(_directory)))
                {
                    bool owns;
                    try { owns = mutex.WaitOne(0, false); }
                    catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return;
                    try { EnforceRetentionCore(root); }
                    finally { try { mutex.ReleaseMutex(); } catch { } }
                }
            }
            catch (Exception ex)
            {
                TryWriteError("RetentionFailed: " + ex.GetBaseException().Message);
            }
        }

        private void EnforceRetentionCore(DirectoryInfo root)
        {
            var groups = new Dictionary<string, SessionGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in root.GetFiles("session-*", SearchOption.TopDirectoryOnly))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var match = OwnedFile.Match(file.Name);
                if (!match.Success) continue;
                var id = match.Groups["id"].Value.ToLowerInvariant();
                if (!groups.TryGetValue(id, out var group))
                {
                    group = new SessionGroup { SessionId = id, LastWriteUtc = DateTime.MinValue };
                    groups[id] = group;
                }
                group.Files.Add(file);
                group.Bytes += SafeLength(file);
                if (file.LastWriteTimeUtc > group.LastWriteUtc) group.LastWriteUtc = file.LastWriteTimeUtc;
            }

            foreach (var group in groups.Values)
            {
                var snapshot = group.Files.FirstOrDefault(file =>
                    file.Name.Equals("session-" + group.SessionId + ".json", StringComparison.OrdinalIgnoreCase));
                var manifest = group.Files.FirstOrDefault(file =>
                    file.Name.Equals("session-" + group.SessionId + ".manifest.json", StringComparison.OrdinalIgnoreCase));
                group.Supported = HasSupportedSchema(snapshot) || HasSupportedSchema(manifest);
                group.Live = group.SessionId == _sessionId || IsLeaseLive(group);
            }

            var candidates = groups.Values.Where(group => group.Supported && !group.Live)
                .OrderBy(group => group.LastWriteUtc).ToList();
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow.AddDays(-_policy.RetentionDays);
            foreach (var group in candidates.Where(group => group.LastWriteUtc < cutoff))
                selected[group.SessionId] = "Age";

            var remaining = groups.Values.Where(group => group.Supported && !selected.ContainsKey(group.SessionId))
                .OrderByDescending(group => group.LastWriteUtc).ToList();
            foreach (var group in remaining.Skip(_policy.RetainSessionCount))
                if (!group.Live) selected[group.SessionId] = "Count";

            var total = groups.Values.Where(group => group.Supported && !selected.ContainsKey(group.SessionId))
                .Sum(group => group.Bytes);
            foreach (var group in candidates.Where(group => !selected.ContainsKey(group.SessionId)))
            {
                if (total <= _policy.MaxTotalBytes) break;
                selected[group.SessionId] = "Bytes";
                total -= group.Bytes;
            }

            foreach (var pair in selected.OrderBy(pair => groups[pair.Key].LastWriteUtc))
            {
                var group = groups[pair.Key];
                long deletedBytes = 0;
                var deletedFiles = 0;
                foreach (var file in group.Files.OrderBy(file => file.LastWriteTimeUtc))
                {
                    if (deletedFiles >= 16 || deletedBytes >= 32L * 1024L * 1024L)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                        deletedFiles = 0;
                        deletedBytes = 0;
                    }
                    var length = SafeLength(file);
                    file.Delete();
                    deletedBytes += length;
                    deletedFiles++;
                }
                AppendRetentionAudit(group.SessionId, pair.Value, group.Bytes);
            }
        }

        private void AppendRetentionAudit(string deletedSessionId, string reason, long bytes)
        {
            var value = new WatchdogJournalEvent
            {
                Source = _source,
                SessionId = _sessionId,
                EventType = "RetentionDeleted",
                State = "Housekeeping",
                Reason = reason,
                ProcessId = _processId,
                ProcessStartUtcTicks = _processStartTicks,
                Detail = $"DeletedSession={deletedSessionId};Bytes={bytes}"
            };
            AppendRotated(EventPath(_source), Json.Serialize(value) + Environment.NewLine,
                SidecarEventSegmentBytes, SidecarEventArchiveCount);
        }

        private bool IsLeaseLive(SessionGroup group)
        {
            var leaseFile = group.Files.FirstOrDefault(file =>
                file.Name.Equals("session-" + group.SessionId + ".lease.json", StringComparison.OrdinalIgnoreCase));
            if (leaseFile == null) return false;
            try
            {
                var lease = Json.Deserialize<WatchdogJournalLease>(File.ReadAllText(leaseFile.FullName, Encoding.UTF8));
                if (lease == null ||
                    (lease.SchemaVersion != WatchdogJournalPolicy.CurrentSchemaVersion &&
                     lease.SchemaVersion != 3 && lease.SchemaVersion != 2) ||
                    lease.ProcessId <= 0 || lease.ProcessStartUtcTicks <= 0) return false;
                using (var process = Process.GetProcessById(lease.ProcessId))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.ProcessStartUtcTicks;
            }
            catch { return false; }
        }

        private static bool HasSupportedSchema(FileInfo file)
        {
            if (file == null || !file.Exists || file.Length > 4L * 1024L * 1024L) return false;
            try
            {
                var text = File.ReadAllText(file.FullName, Encoding.UTF8);
                // V2/V3 journals remain recognizable for retention/migration,
                // but all new leases/snapshots are stamped with CurrentSchemaVersion.
                return text.IndexOf("\"SchemaVersion\":4", StringComparison.Ordinal) >= 0 ||
                       text.IndexOf("\"SchemaVersion\":3", StringComparison.Ordinal) >= 0 ||
                       text.IndexOf("\"SchemaVersion\":2", StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        private void EnforceCurrentSessionBudget()
        {
            try
            {
                var files = SessionFiles(_sessionId).OrderBy(file => file.LastWriteTimeUtc).ToList();
                var total = files.Sum(SafeLength);
                foreach (var file in files.Where(IsRotatedArchive))
                {
                    if (total <= _policy.MaxSessionBytes) break;
                    var length = SafeLength(file);
                    file.Delete();
                    total -= length;
                }
            }
            catch { }
        }

        private IEnumerable<FileInfo> SessionFiles(string sessionId)
        {
            if (!Directory.Exists(_directory)) return Enumerable.Empty<FileInfo>();
            var prefix = "session-" + WatchdogJournalPaths.SafeName(sessionId) + ".";
            return new DirectoryInfo(_directory).GetFiles(prefix + "*", SearchOption.TopDirectoryOnly)
                .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0 && OwnedFile.IsMatch(file.Name))
                .ToArray();
        }

        private static bool IsRotatedArchive(FileInfo file) =>
            Regex.IsMatch(file.Name, @"(?:-events|\.errors)\.\d+\.(jsonl|log)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private string EventPath(string source) => Path.Combine(_directory,
            "session-" + _safeSession + "." + source + "-events.jsonl");

        private bool ContainsValidEventId(string eventId)
        {
            if (string.IsNullOrWhiteSpace(eventId)) return false;
            try
            {
                var basePath = EventPath(_source);
                var files = new[] { basePath, RotatedPath(basePath, 1), RotatedPath(basePath, 2), RotatedPath(basePath, 3) };
                return files.Where(File.Exists).Any(file =>
                {
                    using (var reader = new StreamReader(file, Encoding.UTF8, true, 4096))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            try
                            {
                                var value = WatchdogJournalMigration.MigrateEvent(
                                    Json.Deserialize<WatchdogJournalEvent>(line));
                                if (value != null &&
                                    string.Equals(value.EventId, eventId, StringComparison.Ordinal))
                                    return true;
                            }
                            catch { }
                        }
                    }
                    return false;
                });
            }
            catch { return false; }
        }

        public static IReadOnlyList<WatchdogJournalEvent> ReadValidEvents(string path)
        {
            var result = new List<WatchdogJournalEvent>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return result;
            using (var reader = new StreamReader(path, Encoding.UTF8, true, 4096))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        var value = WatchdogJournalMigration.MigrateEvent(
                            Json.Deserialize<WatchdogJournalEvent>(line));
                        if (value != null &&
                            !string.IsNullOrWhiteSpace(value.EventId))
                            result.Add(value);
                    }
                    catch
                    {
                        // A process may terminate between writing bytes and a
                        // newline.  Earlier complete lines remain authoritative.
                    }
                }
            }
            return result;
        }

        private static void AppendRotated(string path, string text, long maxBytes, int archiveCount)
        {
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (File.Exists(path) && new FileInfo(path).Length + bytes > maxBytes)
            {
                var oldest = RotatedPath(path, archiveCount);
                TryDelete(oldest);
                for (var index = archiveCount - 1; index >= 1; index--)
                {
                    var source = RotatedPath(path, index);
                    if (File.Exists(source)) File.Move(source, RotatedPath(path, index + 1));
                }
                File.Move(path, RotatedPath(path, 1));
            }
            File.AppendAllText(path, text, new UTF8Encoding(false));
        }

        private static string RotatedPath(string path, int index)
        {
            var extension = Path.GetExtension(path);
            return path.Substring(0, path.Length - extension.Length) + "." + index + extension;
        }

        private static void AtomicWrite(string path, string content)
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content ?? string.Empty, new UTF8Encoding(false));
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }

        private static long SafeLength(FileInfo file)
        {
            try { return file?.Exists == true ? file.Length : 0; }
            catch { return 0; }
        }

        private static string Truncate(string value, int maximumCharacters)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximumCharacters) return value ?? string.Empty;
            return value.Substring(0, maximumCharacters) + "...[truncated]";
        }

        private static int[] Limit(int[] values) => (values ?? Array.Empty<int>()).Take(64).ToArray();

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private bool IsStopping()
        {
            lock (_gate) return _stopping;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopping) return;
                _stopping = true;
                _idle.Reset();
            }
            _wake.Set();
            if (_worker.Join(TimeSpan.FromSeconds(2)))
            {
                _wake.Dispose();
                _idle.Dispose();
            }
        }
    }
}
