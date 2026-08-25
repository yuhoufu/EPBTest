using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum WatchdogJournalOpenExistingError
    {
        None = 0,
        InvalidDirectory,
        InvalidSessionId,
        InvalidMaximumSnapshotBytes,
        Missing,
        Empty,
        Oversize,
        AccessDenied,
        ReadFailed,
        InvalidJsonRoot,
        InvalidJson,
        SchemaMismatch,
        SessionMismatch,
        RetryExhausted
    }

    /// <summary>
    /// Immutable evidence that a schema4 session snapshot was opened without
    /// changing any journal state.  This receipt deliberately contains no
    /// permit, lease, process, or authority fields.
    /// </summary>
    public sealed class WatchdogJournalOpenExistingReceipt
    {
        internal WatchdogJournalOpenExistingReceipt(
            string canonicalDirectory,
            string path,
            string sessionId,
            int schemaVersion,
            long length,
            string sha256)
        {
            CanonicalDirectory = canonicalDirectory;
            Path = path;
            SessionId = sessionId;
            SchemaVersion = schemaVersion;
            Length = length;
            SHA256 = sha256;
        }

        public string CanonicalDirectory { get; }
        public string Path { get; }
        public string SessionId { get; }
        public int SchemaVersion { get; }
        public long Length { get; }
        public string SHA256 { get; }

        // Friendly aliases retain the same immutable value while callers
        // migrate from differing casing conventions.
        public string SnapshotPath => Path;
        public long ByteLength => Length;
        public string Sha256 => SHA256;
    }

    /// <summary>
    /// Receipt for the one-time schema4 authority bootstrap.  The constructor
    /// is internal so a caller cannot manufacture a verified capability from
    /// a mutable DTO; only the bootstrap writer can issue it.
    /// </summary>
    public sealed class WatchdogJournalBootstrapReceipt
    {
        internal WatchdogJournalBootstrapReceipt(
            string canonicalDirectory,
            string path,
            string sessionId,
            string sha256,
            int schemaVersion,
            long authorityRevision,
            string marker,
            int processId,
            long processStartUtcTicks)
            : this(canonicalDirectory, path, sessionId, sha256, schemaVersion,
                   authorityRevision, marker, processId, processStartUtcTicks,
                   path, sha256, null, null, 0, null)
        {
        }

        internal WatchdogJournalBootstrapReceipt(
            string canonicalDirectory,
            string path,
            string sessionId,
            string sha256,
            int schemaVersion,
            long authorityRevision,
            string marker,
            int processId,
            long processStartUtcTicks,
            string primarySnapshotPath,
            string primarySnapshotSha256,
            string proofPath,
            string proofSha256,
            int maximumProcessRelaunches,
            string proofMarker)
        {
            CanonicalDirectory = canonicalDirectory;
            Path = path;
            SessionId = sessionId;
            SHA256 = sha256;
            SchemaVersion = schemaVersion;
            AuthorityRevision = authorityRevision;
            Marker = marker;
            ProcessId = processId;
            ProcessStartUtcTicks = processStartUtcTicks;
            PrimarySnapshotPath = primarySnapshotPath ?? path;
            PrimarySnapshotSha256 = primarySnapshotSha256 ?? sha256;
            ProofPath = proofPath;
            ProofSHA256 = proofSha256;
            MaximumProcessRelaunches = maximumProcessRelaunches;
            ProofMarker = proofMarker;
        }

        public string CanonicalDirectory { get; }
        public string Path { get; }
        public string SessionId { get; }
        public string SHA256 { get; }
        public int SchemaVersion { get; }
        public long AuthorityRevision { get; }
        public string Marker { get; }
        public int ProcessId { get; }
        public long ProcessStartUtcTicks { get; }
        public string PrimarySnapshotPath { get; }
        public string PrimarySnapshotSha256 { get; }
        public string ProofPath { get; }
        public string ProofSHA256 { get; }
        public int MaximumProcessRelaunches { get; }
        public string ProofMarker { get; }
        public string Sha256 => SHA256;
        public string ProofSha256 => ProofSHA256;
    }

    /// <summary>
    /// Creates the first schema4 session snapshot before an independent
    /// sidecar is allowed to start.  A session id is single-use: an existing
    /// snapshot is never overwritten by a second bootstrap.
    /// </summary>
    public static class WatchdogJournalBootstrap
    {
        // File.Replace briefly removes the directory entry on some Windows
        // filesystem/filter-driver combinations.  Four 5 ms probes were
        // observed to exhaust during the 64-reader atomic-replace gate even
        // though the replacement completed immediately afterwards.  Keep a
        // bounded retry (never an unbounded wait) but cover that short
        // rename window deterministically.
        private const int OpenExistingMaximumAttempts = 16;
        private const int OpenExistingRetryDelayMilliseconds = 5;

        /// <summary>
        /// Creates the strict authority snapshot with FileMode.CreateNew.
        /// This overload is deliberately distinct from the legacy JSON
        /// bootstrap overload above and returns an immutable evidence receipt.
        /// </summary>
        public static bool TryCreateAuthorityNew(
            string directory,
            string sessionId,
            int processId,
            long processStartUtcTicks,
            out WatchdogJournalBootstrapReceipt receipt,
            out string error)
        {
            return TryCreateAuthorityNew(directory, sessionId, processId,
                processStartUtcTicks, 8, out receipt, out error);
        }

        /// <summary>
        /// Creates the primary journal snapshot and its immutable authority
        /// bootstrap proof.  The primary snapshot remains a normal journal
        /// writer surface; the proof is the only immutable identity bound by
        /// the strict relaunch authority.
        /// </summary>
        public static bool TryCreateAuthorityNew(
            string directory,
            string sessionId,
            int processId,
            long processStartUtcTicks,
            int maximumProcessRelaunches,
            out WatchdogJournalBootstrapReceipt receipt,
            out string error)
        {
            receipt = null;
            error = null;
            Guid guid;
            if (!Guid.TryParseExact(sessionId ?? string.Empty, "N", out guid) ||
                !string.Equals(guid.ToString("N"), sessionId, StringComparison.Ordinal) ||
                processId <= 0 || processStartUtcTicks <= 0 ||
                maximumProcessRelaunches <= 0 || maximumProcessRelaunches > 1000000)
            {
                error = "BootstrapIdentityInvalid";
                return false;
            }
            string full;
            try { full = WatchdogJournalPaths.ValidateProjectDirectory(directory); }
            catch { error = "BootstrapDirectoryInvalid"; return false; }
            var canonicalSessionId = guid.ToString("N");
            // This writer owns only the primary/main bootstrap snapshot.  The
            // strict relaunch authority has a separate sibling file and is
            // created by DurableRelaunchAuthorityFileStore.  Never let this
            // bootstrap writer accidentally become the authority writer.
            var path = System.IO.Path.Combine(full, "session-" + canonicalSessionId + ".json");
            const string marker = "schema4-session-bootstrap-v1";
            try
            {
                Directory.CreateDirectory(full);
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["SchemaVersion"] = 4,
                    ["SessionId"] = canonicalSessionId,
                    ["BootstrapRevision"] = 0L,
                    ["BootstrapMarker"] = marker,
                    ["CurrentPid"] = processId,
                    ["CurrentProcessStartUtcTicks"] = processStartUtcTicks,
                    ["MaximumProcessRelaunches"] = maximumProcessRelaunches,
                    ["RelaunchState"] = DurableRelaunchPermitState.None.ToString(),
                    ["RelaunchGeneration"] = 0L,
                    ["RecoveryBlocked"] = false,
                    ["State"] = "Starting"
                };
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var json = serializer.Serialize(values);
                var bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Read-back is part of the capability proof.  A receipt is
                // never issued for bytes that were only buffered in memory.
                byte[] readback = File.ReadAllBytes(path);
                if (readback.Length != bytes.Length || !readback.SequenceEqual(bytes))
                {
                    error = "BootstrapReadBackMismatch";
                    return false;
                }
                string primaryDigest;
                using (var sha = SHA256.Create())
                    primaryDigest = BitConverter.ToString(sha.ComputeHash(readback)).Replace("-", string.Empty).ToUpperInvariant();

                const string proofMarker = "schema4-authority-bootstrap-proof-v1";
                var proofPath = System.IO.Path.Combine(full, "session-" + canonicalSessionId + ".bootstrap.json");
                var proofValues = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["SchemaVersion"] = 4,
                    ["RecordKind"] = "DurableRelaunchBootstrapProof",
                    ["RecordFormatRevision"] = 1,
                    ["SessionId"] = canonicalSessionId,
                    ["BootstrapRevision"] = 0L,
                    ["BootstrapMarker"] = marker,
                    ["ProofMarker"] = proofMarker,
                    ["CurrentPid"] = processId,
                    ["CurrentProcessStartUtcTicks"] = processStartUtcTicks,
                    ["MaximumProcessRelaunches"] = maximumProcessRelaunches,
                    ["PrimarySnapshotPath"] = path,
                    ["PrimarySnapshotSha256"] = primaryDigest
                };
                var proofJson = serializer.Serialize(proofValues);
                var proofBytes = new UTF8Encoding(false).GetBytes(proofJson);
                using (var proofStream = new FileStream(proofPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough))
                {
                    proofStream.Write(proofBytes, 0, proofBytes.Length);
                    proofStream.Flush(true);
                }
                var proofReadback = File.ReadAllBytes(proofPath);
                if (proofReadback.Length != proofBytes.Length || !proofReadback.SequenceEqual(proofBytes))
                {
                    error = "BootstrapProofReadBackMismatch";
                    return false;
                }
                string proofDigest;
                using (var sha = SHA256.Create())
                    proofDigest = BitConverter.ToString(sha.ComputeHash(proofReadback)).Replace("-", string.Empty).ToUpperInvariant();
                receipt = new WatchdogJournalBootstrapReceipt(full, path, canonicalSessionId,
                    primaryDigest, 4, 0, marker, processId, processStartUtcTicks,
                    path, primaryDigest, proofPath, proofDigest,
                    maximumProcessRelaunches, proofMarker);
                return true;
            }
            catch (IOException) { error = "BootstrapAlreadyExistsOrWriteFailed"; return false; }
            catch (UnauthorizedAccessException) { error = "BootstrapAccessDenied"; return false; }
            catch (Exception ex) { error = "BootstrapFailed:" + ex.GetBaseException().Message; return false; }
        }

        /// <summary>Read and verify the primary bootstrap snapshot only.</summary>
        internal static bool TryReadPrimaryBootstrap(
            string directory,
            string expectedSessionId,
            out WatchdogJournalBootstrapReceipt receipt,
            out string error)
        {
            receipt = null;
            error = null;
            Guid guid;
            if (!Guid.TryParseExact(expectedSessionId ?? string.Empty, "N", out guid) ||
                !string.Equals(guid.ToString("N"), expectedSessionId, StringComparison.Ordinal))
            {
                error = "BootstrapSessionInvalid";
                return false;
            }
            string full;
            try { full = WatchdogJournalPaths.ValidateProjectDirectory(directory); }
            catch { error = "BootstrapDirectoryInvalid"; return false; }
            var session = guid.ToString("N");
            var path = System.IO.Path.Combine(full, "session-" + session + ".json");
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= 0 || stream.Length > 16 * 1024 * 1024 || stream.Length > int.MaxValue)
                    { error = "BootstrapSizeInvalid"; return false; }
                    bytes = new byte[(int)stream.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) { error = "BootstrapShortRead"; return false; }
                        offset += read;
                    }
                }
                var serializer = new JavaScriptSerializer { MaxJsonLength = Math.Max(1024, bytes.Length + 16) };
                var map = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
                if (map == null) { error = "BootstrapInvalidRoot"; return false; }
                if (ToInt(map, "SchemaVersion") != 4) { error = "BootstrapSchemaMismatch"; return false; }
                if (!string.Equals(ToString(map, "SessionId"), session, StringComparison.Ordinal)) { error = "BootstrapSessionMismatch"; return false; }
                if (!string.Equals(ToString(map, "BootstrapMarker"), "schema4-session-bootstrap-v1", StringComparison.Ordinal)) { error = "BootstrapMarkerInvalid"; return false; }
                var revision = ToLong(map, "BootstrapRevision");
                var pid = ToInt(map, "CurrentPid");
                var start = ToLong(map, "CurrentProcessStartUtcTicks");
                if (revision < 0 || pid <= 0 || start <= 0) { error = "BootstrapIdentityInvalid"; return false; }
                using (var sha = SHA256.Create())
                {
                    receipt = new WatchdogJournalBootstrapReceipt(full, path, session,
                        BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToUpperInvariant(),
                        4, revision, "schema4-session-bootstrap-v1", pid, start);
                }
                return true;
            }
            catch (FileNotFoundException) { error = "BootstrapMissing"; return false; }
            catch (UnauthorizedAccessException) { error = "BootstrapAccessDenied"; return false; }
            catch (IOException) { error = "BootstrapUnreadable"; return false; }
            catch (Exception ex) { error = "BootstrapCorrupt:" + ex.GetType().Name; return false; }
        }

        /// <summary>
        /// Opens the immutable authority proof.  This deliberately does not
        /// hash or require the current primary session snapshot: the primary
        /// file is allowed to advance under the normal journal writer after
        /// bootstrap, while the proof remains the identity anchor.
        /// </summary>
        internal static bool TryReadBootstrapProof(
            string directory,
            string expectedSessionId,
            out WatchdogJournalBootstrapReceipt receipt,
            out string error)
        {
            receipt = null;
            error = null;
            Guid guid;
            if (!Guid.TryParseExact(expectedSessionId ?? string.Empty, "N", out guid) ||
                !string.Equals(expectedSessionId, guid.ToString("N"), StringComparison.Ordinal))
            {
                error = "BootstrapSessionInvalid";
                return false;
            }
            string full;
            try { full = WatchdogJournalPaths.ValidateProjectDirectory(directory); }
            catch { error = "BootstrapDirectoryInvalid"; return false; }
            var session = guid.ToString("N");
            var proofPath = System.IO.Path.Combine(full, "session-" + session + ".bootstrap.json");
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(proofPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length <= 0 || stream.Length > 16 * 1024 * 1024 || stream.Length > int.MaxValue)
                    { error = "BootstrapProofSizeInvalid"; return false; }
                    bytes = new byte[(int)stream.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) { error = "BootstrapProofShortRead"; return false; }
                        offset += read;
                    }
                }
                var serializer = new JavaScriptSerializer { MaxJsonLength = Math.Max(1024, bytes.Length + 16) };
                var map = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
                if (map == null) { error = "BootstrapProofInvalidRoot"; return false; }
                if (ToInt(map, "SchemaVersion") != 4 ||
                    !string.Equals(ToString(map, "RecordKind"), "DurableRelaunchBootstrapProof", StringComparison.Ordinal) ||
                    ToInt(map, "RecordFormatRevision") != 1)
                { error = "BootstrapProofSchemaMismatch"; return false; }
                if (!string.Equals(ToString(map, "SessionId"), session, StringComparison.Ordinal))
                { error = "BootstrapProofSessionMismatch"; return false; }
                if (!string.Equals(ToString(map, "BootstrapMarker"), "schema4-session-bootstrap-v1", StringComparison.Ordinal) ||
                    !string.Equals(ToString(map, "ProofMarker"), "schema4-authority-bootstrap-proof-v1", StringComparison.Ordinal))
                { error = "BootstrapProofMarkerInvalid"; return false; }
                var primaryPath = ToString(map, "PrimarySnapshotPath");
                var primarySha = ToString(map, "PrimarySnapshotSha256");
                var expectedPrimaryPath = System.IO.Path.Combine(full, "session-" + session + ".json");
                if (string.IsNullOrEmpty(primaryPath) ||
                    !string.Equals(System.IO.Path.GetFullPath(primaryPath), System.IO.Path.GetFullPath(expectedPrimaryPath), StringComparison.OrdinalIgnoreCase) ||
                    !RecoveryFailureReceipt.IsSha256(primarySha))
                { error = "BootstrapProofPrimaryBindingInvalid"; return false; }
                var revision = ToLong(map, "BootstrapRevision");
                var pid = ToInt(map, "CurrentPid");
                var start = ToLong(map, "CurrentProcessStartUtcTicks");
                var budget = ToInt(map, "MaximumProcessRelaunches");
                if (revision < 0 || pid <= 0 || start <= 0 || budget <= 0 || budget > 1000000)
                { error = "BootstrapProofIdentityInvalid"; return false; }
                string proofSha;
                using (var sha = SHA256.Create())
                    proofSha = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToUpperInvariant();
                receipt = new WatchdogJournalBootstrapReceipt(full, primaryPath, session, primarySha,
                    4, revision, "schema4-session-bootstrap-v1", pid, start,
                    primaryPath, primarySha, proofPath, proofSha, budget,
                    "schema4-authority-bootstrap-proof-v1");
                return true;
            }
            catch (FileNotFoundException) { error = "BootstrapProofMissing"; return false; }
            catch (UnauthorizedAccessException) { error = "BootstrapProofAccessDenied"; return false; }
            catch (IOException) { error = "BootstrapProofUnreadable"; return false; }
            catch (DecoderFallbackException) { error = "BootstrapProofInvalidUtf8"; return false; }
            catch (Exception ex) { error = "BootstrapProofCorrupt:" + ex.GetType().Name; return false; }
        }

        private static int ToInt(Dictionary<string, object> map, string key)
        {
            long value = ToLong(map, key);
            return value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
        }

        private static long ToLong(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return 0;
            long parsed;
            return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
        }

        private static string ToString(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        public static bool TryCreateNew(
            string directory,
            string sessionId,
            int processId,
            long processStartUtcTicks,
            out string json,
            out string error)
        {
            json = null;
            error = null;
            if (string.IsNullOrWhiteSpace(sessionId) ||
                processId <= 0 ||
                processStartUtcTicks <= 0)
            {
                error = "BootstrapIdentityInvalid";
                return false;
            }
            string full;
            try
            {
                full = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            }
            catch (Exception ex)
            {
                error = "BootstrapDirectoryInvalid:" + ex.GetBaseException().Message;
                return false;
            }
            var path = Path.Combine(
                full,
                "session-" + WatchdogJournalPaths.SafeName(sessionId) + ".json");
            try
            {
                Directory.CreateDirectory(full);
                if (File.Exists(path))
                {
                    error = "BootstrapSessionAlreadyExists";
                    return false;
                }
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["SchemaVersion"] = WatchdogJournalPolicy.CurrentSchemaVersion,
                    ["SessionId"] = sessionId,
                    ["BootstrapRevision"] = 1L,
                    ["BootstrapMarker"] = "schema4-session-bootstrap-v1",
                    ["CurrentPid"] = processId,
                    ["CurrentProcessStartUtcTicks"] = processStartUtcTicks,
                    ["RelaunchState"] = DurableRelaunchPermitState.None.ToString(),
                    ["RelaunchGeneration"] = 0L,
                    ["RelaunchPermitGeneration"] = 0L,
                    ["RecoveryBlocked"] = false,
                    ["State"] = "Starting",
                    ["UpdatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                // JavaScriptSerializer is deliberately scoped to this
                // operation.  It is not thread-safe and must never be a
                // process-wide parser shared by concurrent journal opens.
                json = new JavaScriptSerializer().Serialize(values);
                var temporary = path + ".bootstrap-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, json, new UTF8Encoding(false));
                    using (var stream = new FileStream(
                               temporary,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.Read))
                        stream.Flush(true);
                    if (File.Exists(path))
                    {
                        error = "BootstrapSessionRace";
                        return false;
                    }
                    File.Move(temporary, path);
                    return true;
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
            }
            catch (Exception ex)
            {
                error = "BootstrapWriteFailed:" + ex.GetBaseException().Message;
                return false;
            }
        }

        /// <summary>
        /// Opens an existing schema4 session snapshot for read-only inspection.
        /// The operation never creates, migrates, deletes, or interprets
        /// permit/lease state.  File sharing permits an atomic File.Replace to
        /// complete while this method holds either the old or the new snapshot.
        /// </summary>
        public static bool TryOpenExistingSession(
            string directory,
            string expectedSessionId,
            long maximumSnapshotBytes,
            out WatchdogJournalOpenExistingReceipt receipt,
            out WatchdogJournalOpenExistingError error)
        {
            receipt = null;
            error = WatchdogJournalOpenExistingError.None;

            string canonicalDirectory;
            try
            {
                canonicalDirectory = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            }
            catch
            {
                error = WatchdogJournalOpenExistingError.InvalidDirectory;
                return false;
            }

            // The filename contract is the canonical N-form GUID.  Do not
            // accept dashed/brace/whitespace variants and silently redirect
            // them to another snapshot.
            if (!Guid.TryParseExact(expectedSessionId, "N", out var expectedGuid))
            {
                error = WatchdogJournalOpenExistingError.InvalidSessionId;
                return false;
            }

            if (maximumSnapshotBytes <= 0 || maximumSnapshotBytes > int.MaxValue)
            {
                error = WatchdogJournalOpenExistingError.InvalidMaximumSnapshotBytes;
                return false;
            }

            var canonicalSessionId = expectedGuid.ToString("N").ToLowerInvariant();
            var path = System.IO.Path.Combine(
                canonicalDirectory,
                "session-" + WatchdogJournalPaths.SafeName(canonicalSessionId) + ".json");
            var transientError = WatchdogJournalOpenExistingError.ReadFailed;

            for (var attempt = 0; attempt < OpenExistingMaximumAttempts; attempt++)
            {
                try
                {
                    byte[] bytes;
                    using (var stream = new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        var length = stream.Length;
                        if (length == 0)
                        {
                            error = WatchdogJournalOpenExistingError.Empty;
                            return false;
                        }
                        if (length > maximumSnapshotBytes || length > int.MaxValue)
                        {
                            error = WatchdogJournalOpenExistingError.Oversize;
                            return false;
                        }

                        bytes = new byte[(int)length];
                        var offset = 0;
                        while (offset < bytes.Length)
                        {
                            var read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read <= 0) break;
                            offset += read;
                        }
                        if (offset != bytes.Length)
                        {
                            transientError = WatchdogJournalOpenExistingError.ReadFailed;
                            continue;
                        }
                    }

                    string text;
                    try
                    {
                        text = new UTF8Encoding(false, true).GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        error = WatchdogJournalOpenExistingError.InvalidJson;
                        return false;
                    }
                    if (text.Length > 0 && text[0] == '\uFEFF')
                        text = text.Substring(1);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        error = WatchdogJournalOpenExistingError.Empty;
                        return false;
                    }

                    object parsed;
                    try
                    {
                        // MaxJsonLength is an int in System.Web.Extensions;
                        // maximumSnapshotBytes was range-checked above, so
                        // this cast is safe.  A fresh serializer per attempt
                        // also prevents concurrent opens from sharing parser
                        // state or limits.
                        var serializer = new JavaScriptSerializer
                        {
                            MaxJsonLength = (int)maximumSnapshotBytes
                        };
                        parsed = serializer.DeserializeObject(text);
                    }
                    catch
                    {
                        error = WatchdogJournalOpenExistingError.InvalidJson;
                        return false;
                    }

                    var root = parsed as Dictionary<string, object>;
                    if (root == null)
                    {
                        error = WatchdogJournalOpenExistingError.InvalidJsonRoot;
                        return false;
                    }

                    if (!root.TryGetValue("SchemaVersion", out var rawSchema) ||
                        !IsExactSchema4(rawSchema))
                    {
                        error = WatchdogJournalOpenExistingError.SchemaMismatch;
                        return false;
                    }
                    if (!root.TryGetValue("SessionId", out var rawSession) ||
                        !(rawSession is string) ||
                        !string.Equals((string)rawSession, canonicalSessionId,
                            StringComparison.Ordinal))
                    {
                        error = WatchdogJournalOpenExistingError.SessionMismatch;
                        return false;
                    }

                    var hash = ComputeSha256(bytes);
                    receipt = new WatchdogJournalOpenExistingReceipt(
                        canonicalDirectory,
                        path,
                        canonicalSessionId,
                        WatchdogJournalPolicy.CurrentSchemaVersion,
                        bytes.LongLength,
                        hash);
                    error = WatchdogJournalOpenExistingError.None;
                    return true;
                }
                catch (FileNotFoundException)
                {
                    transientError = WatchdogJournalOpenExistingError.Missing;
                }
                catch (DirectoryNotFoundException)
                {
                    transientError = WatchdogJournalOpenExistingError.Missing;
                }
                catch (UnauthorizedAccessException)
                {
                    transientError = WatchdogJournalOpenExistingError.AccessDenied;
                }
                catch (IOException)
                {
                    transientError = WatchdogJournalOpenExistingError.ReadFailed;
                }

                if (attempt + 1 < OpenExistingMaximumAttempts)
                    System.Threading.Thread.Sleep(OpenExistingRetryDelayMilliseconds);
            }

            error = transientError == WatchdogJournalOpenExistingError.ReadFailed
                ? WatchdogJournalOpenExistingError.RetryExhausted
                : transientError;
            return false;
        }

        private static bool IsExactSchema4(object rawSchema)
        {
            if (rawSchema is int intValue) return intValue == WatchdogJournalPolicy.CurrentSchemaVersion;
            if (rawSchema is long longValue) return longValue == WatchdogJournalPolicy.CurrentSchemaVersion;
            if (rawSchema is short shortValue) return shortValue == WatchdogJournalPolicy.CurrentSchemaVersion;
            return false;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }
    }
}
