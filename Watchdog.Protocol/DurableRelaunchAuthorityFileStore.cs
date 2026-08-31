using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// The strict authority owns the CAS algorithm; this internal port only
    /// isolates the final Windows file boundaries (create+Flush(true),
    /// Replace, and read-back).  Production always uses
    /// <see cref="WindowsDurableAuthorityFileIo"/>.  Tests may inject a
    /// deterministic boundary fault without copying the store algorithm.
    /// </summary>
    internal interface IDurableAuthorityFileIo
    {
        bool Exists(string path);
        byte[] ReadAllBytes(string path);
        void CreateAndFlush(string path, byte[] bytes);
        void Replace(string temporaryPath, string targetPath);
        void Delete(string path);
    }

    internal sealed class WindowsDurableAuthorityFileIo : IDurableAuthorityFileIo
    {
        public bool Exists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length < 0 || stream.Length > int.MaxValue)
                    throw new IOException("AuthorityReadSizeInvalid");
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("AuthorityShortRead");
                    offset += read;
                }
                return bytes;
            }
        }

        public void CreateAndFlush(string path, byte[] bytes)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes ?? Array.Empty<byte>(), 0, bytes?.Length ?? 0);
                stream.Flush(true);
            }
        }

        public void Replace(string temporaryPath, string targetPath) =>
            File.Replace(temporaryPath, targetPath, null, true);

        public void Delete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    /// <summary>
    /// Strict authority file store.  The target is never deleted.  Updates
    /// are write-through UTF8/no-BOM temp files followed by bounded
    /// File.Replace and a read-back validation.
    /// </summary>
    public sealed class DurableRelaunchAuthorityFileStore : IDurableRelaunchAuthorityStoreV4
    {
        // All factory/open/load/commit admissions share this bounded mutex
        // budget.  A caller can wait, but never indefinitely, on a stale
        // process or abandoned writer.
        private const int LockMilliseconds = 30000;
        private const int ReadRetries = 4;
        private readonly string _sessionId;
        private readonly string _directory;
        private readonly string _path;
        private readonly string _mutexName;
        private readonly UTF8Encoding _utf8 = new UTF8Encoding(false);
        private readonly IDurableAuthorityFileIo _io;
        // Internal deterministic admission seam for the production-file
        // tests.  The default path always uses the real named mutex.
        private readonly Func<int, bool> _waitAdmission;

        public DurableRelaunchAuthorityFileStore(string directory, string sessionId)
            : this(directory, sessionId, null)
        {
        }

        internal DurableRelaunchAuthorityFileStore(
            string directory,
            string sessionId,
            IDurableAuthorityFileIo io)
            : this(directory, sessionId, io, null)
        {
        }

        internal DurableRelaunchAuthorityFileStore(
            string directory,
            string sessionId,
            IDurableAuthorityFileIo io,
            Func<int, bool> waitAdmission)
        {
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(sessionId)) throw new ArgumentException("SessionId must be GUID N.", nameof(sessionId));
            _directory = Path.GetFullPath(WatchdogJournalPaths.ValidateProjectDirectory(directory));
            _sessionId = sessionId;
            _path = Path.GetFullPath(Path.Combine(_directory, "session-" + sessionId + ".relaunch.json"));
            // Windows paths are case-insensitive.  Hash the ordinal,
            // upper-cased canonical path so C:\x and c:\X join one mutex.
            _mutexName = "Local\\MTTFTest-WatchdogAuthority-" + Sha256Hex(_path.ToUpperInvariant()).Substring(0, 32);
            _io = io ?? new WindowsDurableAuthorityFileIo();
            _waitAdmission = waitAdmission;
        }

        public string CanonicalPath => _path;

        /// <summary>
        /// Create the authority sibling once.  The primary bootstrap receipt
        /// is supplied by WatchdogJournalBootstrap; this writer never opens or
        /// replaces the primary session file.
        /// </summary>
        internal DurableAuthorityStoreCommitResult CreatePristine(
            DurableRelaunchAuthorityRecord pristine)
        {
            if (pristine == null) return Result(DurableAuthorityCommitStatus.WriteFailed, "PristineMissing");
            Mutex mutex = null; bool held = false;
            try
            {
                mutex = new Mutex(false, _mutexName);
                try { held = WaitForMutex(mutex); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return Result(DurableAuthorityCommitStatus.Busy, "AuthorityMutexBusy");
                if (_io.Exists(_path)) return Result(DurableAuthorityCommitStatus.Conflict, "AuthorityAlreadyExists");
                pristine = pristine.Clone();
                pristine.SchemaVersion = 4;
                pristine.RecordKind = DurableRelaunchAuthorityV4Validator.RequiredRecordKind;
                pristine.RecordFormatRevision = DurableRelaunchAuthorityV4Validator.RequiredFormatRevision;
                pristine.AuthorityRevision = 0;
                var bytes = Serialize(pristine);
                Directory.CreateDirectory(_directory);
                _io.CreateAndFlush(_path, bytes);
                var after = ReadCore(_sessionId);
                if (after == null || after.Record == null || after.Blocked || after.Unproven ||
                    after.Revision != 0 || !string.Equals(after.Sha256, Sha256Hex(bytes), StringComparison.Ordinal))
                    return Result(DurableAuthorityCommitStatus.WriteFailed, "PristineReadBackMismatch");
                return new DurableAuthorityStoreCommitResult
                {
                    Status = DurableAuthorityCommitStatus.Committed,
                    Record = after.Record.Clone(), Sha256 = after.Sha256, Revision = after.Revision,
                    Reason = "PristineCreated"
                };
            }
            catch (IOException ex) { return Result(DurableAuthorityCommitStatus.WriteFailed, ex.GetBaseException().Message); }
            catch (UnauthorizedAccessException ex) { return Result(DurableAuthorityCommitStatus.WriteFailed, ex.GetBaseException().Message); }
            finally { if (held) try { mutex?.ReleaseMutex(); } catch { } mutex?.Dispose(); }
        }

        public DurableAuthorityStoreReadResult Load(string expectedSessionId)
        {
            if (!string.Equals(expectedSessionId, _sessionId, StringComparison.Ordinal))
                return Block("SessionMismatch", DurableAuthorityFailureKind.SessionMismatch);
            Mutex mutex = null; bool held = false;
            try
            {
                mutex = new Mutex(false, _mutexName);
                try { held = WaitForMutex(mutex); }
                catch (AbandonedMutexException) { held = true; }
                // A bounded admission timeout is not proof that the bytes
                // are bad.  Keep this retryable and typed separately from
                // unreadable/corrupt data.
                if (!held) return new DurableAuthorityStoreReadResult { Blocked = false, Unproven = true, FailureKind = DurableAuthorityFailureKind.Busy, Reason = "AuthorityMutexBusy" };
                return ReadCore(expectedSessionId);
            }
            catch (UnauthorizedAccessException ex) { return Block(ex.GetBaseException().Message, DurableAuthorityFailureKind.Unreadable); }
            catch (IOException ex) { return Block(ex.GetBaseException().Message, DurableAuthorityFailureKind.Unreadable); }
            catch (Exception ex) { return Block(ex.GetBaseException().Message, DurableAuthorityFailureKind.Corrupt); }
            finally { if (held) try { mutex?.ReleaseMutex(); } catch { } mutex?.Dispose(); }
        }

        public DurableAuthorityStoreCommitResult TryCommit(
            DurableRelaunchAuthorityRecord candidate, long expectedRevision, string expectedSha256, bool blockedMarker)
        {
            if (candidate == null) return Result(DurableAuthorityCommitStatus.WriteFailed, "CandidateMissing");
            Mutex mutex = null; bool held = false;
            try
            {
                mutex = new Mutex(false, _mutexName);
                try { held = WaitForMutex(mutex); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return Result(DurableAuthorityCommitStatus.Busy, "AuthorityMutexBusy");
                var current = ReadCore(_sessionId);
                if (current == null || current.Record == null || current.Blocked || current.Unproven)
                    return ReadFailureCommit(current);
                if (current.Record.State == DurableRelaunchPermitState.Blocked ||
                    current.Record.State == DurableRelaunchPermitState.Revoked || current.Record.CircuitOpen)
                    return new DurableAuthorityStoreCommitResult
                    {
                        Status = DurableAuthorityCommitStatus.ExistingBlocked,
                        Record = current.Record.Clone(), Sha256 = current.Sha256, Revision = current.Revision,
                        Reason = "CircuitOpen"
                    };
                if (current.Revision != expectedRevision || !string.Equals(current.Sha256 ?? string.Empty, expectedSha256 ?? string.Empty, StringComparison.Ordinal))
                    return Result(DurableAuthorityCommitStatus.Conflict, "ExpectedRevisionOrShaMismatch");
                candidate = candidate.Clone();
                candidate.AuthorityRevision = expectedRevision + 1;
                candidate.SchemaVersion = 4; candidate.RecordKind = DurableRelaunchAuthorityV4Validator.RequiredRecordKind;
                candidate.RecordFormatRevision = DurableRelaunchAuthorityV4Validator.RequiredFormatRevision;
                var bytes = Serialize(candidate);
                var temp = Path.Combine(_directory, ".authority-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    Directory.CreateDirectory(_directory);
                    _io.CreateAndFlush(temp, bytes);
                    // Never delete the target.  A failed replace leaves the
                    // old durable bytes intact and is reported to the caller.
                    _io.Replace(temp, _path);
                    var after = ReadCore(_sessionId);
                    if (after == null || after.Record == null || after.Revision != candidate.AuthorityRevision ||
                        !string.Equals(after.Sha256, Sha256Hex(bytes), StringComparison.Ordinal) ||
                        !DurableRelaunchAuthorityV4Validator.TryValidateRecord(after.Record, _sessionId, out _))
                        return ReadFailureCommit(after, "ReplaceReadBackMismatch");
                    return new DurableAuthorityStoreCommitResult
                    {
                        // A candidate which was actually written and read
                        // back is always CandidateApplied. ExistingBlocked
                        // is reserved for a fresh read that found a prior
                        // validated circuit before this candidate was
                        // admitted.
                        Status = DurableAuthorityCommitStatus.CandidateApplied,
                        Record = after.Record.Clone(), Sha256 = after.Sha256, Revision = after.Revision,
                        Reason = blockedMarker ? "BlockedMarkerApplied" : "CandidateApplied"
                    };
                }
                catch (UnauthorizedAccessException ex) { return Result(DurableAuthorityCommitStatus.WriteFailed, ex.GetBaseException().Message); }
                catch (IOException ex) { return Result(DurableAuthorityCommitStatus.WriteFailed, ex.GetBaseException().Message); }
                    finally { _io.Delete(temp); }
            }
            catch (AbandonedMutexException) { return Result(DurableAuthorityCommitStatus.Busy, "AuthorityMutexAbandoned"); }
            catch (Exception ex) { return Result(DurableAuthorityCommitStatus.WriteFailed, ex.GetBaseException().Message); }
            finally { if (held) try { mutex?.ReleaseMutex(); } catch { } mutex?.Dispose(); }
        }

        /// <summary>
        /// Opens one automatic half-open probe after a retryable main-launch
        /// circuit.  It never reuses the consumed permit: a new generation,
        /// PermitId and nonce are written in one durable replacement.
        /// Permanent, identity-conflict and unproven circuits remain closed.
        /// </summary>
        public DurableAuthorityStoreCommitResult TryOpenAutomaticHalfOpen(
            string expectedFailureFingerprint,
            int expectedConsecutiveFailures)
        {
            Mutex mutex = null;
            var held = false;
            try
            {
                mutex = new Mutex(false, _mutexName);
                try { held = WaitForMutex(mutex); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return Result(DurableAuthorityCommitStatus.Busy, "AuthorityMutexBusy");
                var current = ReadCore(_sessionId);
                if (current == null || current.Record == null || current.Blocked || current.Unproven)
                    return ReadFailureCommit(current);
                var record = current.Record;
                if (record.State != DurableRelaunchPermitState.Blocked ||
                    !record.CircuitOpen)
                    return Result(DurableAuthorityCommitStatus.Conflict, "CircuitNotOpen");
                if (record.LastFailurePermanent ||
                    string.Equals(record.LastFailureDisposition,
                        RecoveryFailureDispositions.IdentityConflict,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(record.LastFailureCanonicalSha256))
                    return new DurableAuthorityStoreCommitResult
                    {
                        Status = DurableAuthorityCommitStatus.ExistingBlocked,
                        Record = record.Clone(),
                        Sha256 = current.Sha256,
                        Revision = current.Revision,
                        Reason = "CircuitNotRetryable"
                    };
                if (!string.Equals(
                        record.LastFailureFingerprint,
                        expectedFailureFingerprint,
                        StringComparison.Ordinal) ||
                    record.ConsecutiveFailures != expectedConsecutiveFailures)
                    return Result(
                        DurableAuthorityCommitStatus.Conflict,
                        "HalfOpenEvidenceChanged");

                var candidate = record.Clone();
                candidate.AuthorityRevision = current.Revision + 1;
                candidate.State = DurableRelaunchPermitState.Approved;
                candidate.CircuitOpen = false;
                candidate.Generation = Math.Max(0, candidate.Generation) + 1;
                candidate.PermitId = Guid.NewGuid().ToString("N");
                candidate.PermitNonce = Guid.NewGuid().ToString("N");
                candidate.ProcessId = 0;
                candidate.ProcessStartUtcTicks = 0;
                candidate.RecoveryCommitGeneration = 0;
                candidate.LastFailurePermitGeneration = candidate.Generation;
                candidate.LastFailurePermitId = candidate.PermitId;
                candidate.LastFailurePermitNonce = candidate.PermitNonce;
                candidate.LastFailureDisposition =
                    RecoveryFailureDispositions.RelaunchApproved;
                candidate.DetailCode = "AutomaticHalfOpenApproved";
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                candidate.LaunchIntentId = null;
                candidate.LaunchExecutablePath = null;
                candidate.LaunchExecutableSha256 = null;
                candidate.LaunchArguments = null;
                candidate.LaunchWorkingDirectory = null;
                candidate.LaunchOptionsCanonical = null;
                candidate.LaunchSpecSha256 = null;
                candidate.LaunchAuthorityRevision = 0;
                candidate.LaunchAuthoritySha256 = null;
                candidate.LaunchConsumed = false;
                string validation;
                if (!DurableRelaunchAuthorityV4Validator.TryValidateRecord(
                        candidate, _sessionId, out validation))
                    return Result(
                        DurableAuthorityCommitStatus.WriteFailed,
                        "HalfOpenCandidateInvalid:" + validation);

                var bytes = Serialize(candidate);
                var temporary = Path.Combine(
                    _directory,
                    ".authority-half-open-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    _io.CreateAndFlush(temporary, bytes);
                    _io.Replace(temporary, _path);
                    var after = ReadCore(_sessionId);
                    if (after == null || after.Record == null || after.Blocked ||
                        after.Unproven || after.Revision != candidate.AuthorityRevision ||
                        !string.Equals(after.Sha256, Sha256Hex(bytes), StringComparison.Ordinal))
                        return ReadFailureCommit(after, "HalfOpenReadBackMismatch");
                    return new DurableAuthorityStoreCommitResult
                    {
                        Status = DurableAuthorityCommitStatus.CandidateApplied,
                        Record = after.Record.Clone(),
                        Sha256 = after.Sha256,
                        Revision = after.Revision,
                        Reason = "AutomaticHalfOpenApproved"
                    };
                }
                finally
                {
                    _io.Delete(temporary);
                }
            }
            catch (Exception ex)
            {
                return Result(
                    DurableAuthorityCommitStatus.WriteFailed,
                    ex.GetBaseException().Message);
            }
            finally
            {
                if (held) try { mutex?.ReleaseMutex(); } catch { }
                mutex?.Dispose();
            }
        }

        private DurableAuthorityStoreReadResult ReadCore(string expectedSessionId)
        {
            if (!_io.Exists(_path)) return new DurableAuthorityStoreReadResult { Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.Missing, Reason = "AuthorityMissing" };
            WatchdogJournalBootstrapReceipt proof;
            string proofError;
            if (!WatchdogJournalBootstrap.TryReadBootstrapProof(_directory, expectedSessionId, out proof, out proofError))
                return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.BootstrapUnproven, Reason = proofError ?? "BootstrapProofUnproven" };
            Exception last = null;
            for (var attempt = 0; attempt < ReadRetries; attempt++)
            {
                try
                {
                    byte[] bytes;
                    bytes = _io.ReadAllBytes(_path);
                    if (bytes.Length <= 0 || bytes.Length > 16 * 1024 * 1024)
                        return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.Corrupt, Reason = "AuthoritySizeInvalid" };
                    var json = _utf8.GetString(bytes);
                    var validation = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(json, expectedSessionId);
                    if (validation == null || validation.Record == null)
                        return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = !validation?.Proven ?? true, FailureKind = validation?.FailureKind ?? DurableAuthorityFailureKind.Invalid, Reason = validation?.Reason ?? "AuthorityInvalid" };
                    // Legacy/old-v4 records are deliberately allowed to
                    // enter the typed migration path before proof fields are
                    // bound.  A current schema4 record, however, must match
                    // the immutable proof on every read.
                    if (!validation.Migrated)
                    {
                        string proofBindingReason;
                        if (!RecordMatchesProof(validation.Record, proof, out proofBindingReason))
                            return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.BootstrapUnproven, Reason = proofBindingReason };
                    }
                    if (validation.Blocked)
                    {
                        // A legacy/old-v4 record with non-pristine evidence
                        // is allowed one typed migration into a durable
                        // schema4 Blocked marker.  Unknown/corrupt current
                        // bytes remain Unproven and are never invented or
                        // overwritten.
                        if (!validation.Migrated)
                            return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = validation.FailureKind, Reason = validation.Reason ?? "AuthorityInvalid" };
                        validation.Record = BindBootstrapProof(validation.Record, proof);
                        validation.Record.SchemaVersion = 4;
                        validation.Record.RecordKind = DurableRelaunchAuthorityV4Validator.RequiredRecordKind;
                        validation.Record.RecordFormatRevision = DurableRelaunchAuthorityV4Validator.RequiredFormatRevision;
                        validation.Record.State = DurableRelaunchPermitState.Blocked;
                        validation.Record.CircuitOpen = true;
                        var blockedMigration = PersistMigratedRecord(validation.Record, expectedSessionId);
                        if (blockedMigration == null || blockedMigration.Record == null || blockedMigration.Blocked || blockedMigration.Unproven)
                            return blockedMigration ?? new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.WriteFailed, Reason = "BlockedMigrationPersistFailed" };
                        return blockedMigration;
                    }
                    if (validation.Migrated)
                    {
                        validation.Record = BindBootstrapProof(validation.Record, proof);
                        var normalized = Serialize(validation.Record);
                        var temporary = Path.Combine(_directory, ".authority-migration-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            _io.CreateAndFlush(temporary, normalized);
                            _io.Replace(temporary, _path);
                            bytes = _io.ReadAllBytes(_path);
                            validation = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(_utf8.GetString(bytes), expectedSessionId);
                            if (validation == null || validation.Record == null || validation.Blocked || validation.Migrated)
                                return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.Invalid, Reason = "MigrationReadBackFailed" };
                        }
                        catch (Exception ex)
                        {
                            return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.WriteFailed, Reason = "MigrationPersistFailed:" + ex.GetBaseException().Message };
                        }
                        finally { _io.Delete(temporary); }
                    }
                    var sha = Sha256Hex(bytes);
                    return new DurableAuthorityStoreReadResult { Exists = true, Record = validation.Record.Clone(), Revision = validation.Record.AuthorityRevision, Sha256 = sha, Reason = validation.Reason };
                }
                catch (Exception ex) { last = ex; Thread.Sleep(5); }
            }
            return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.Unreadable, Reason = last?.GetBaseException().Message ?? "AuthorityReadFailed" };
        }

        private static DurableAuthorityStoreReadResult Block(string reason, DurableAuthorityFailureKind kind) => new DurableAuthorityStoreReadResult { Blocked = true, Unproven = true, FailureKind = kind, Reason = reason };
        private static DurableAuthorityStoreCommitResult Result(DurableAuthorityCommitStatus status, string reason) => new DurableAuthorityStoreCommitResult
        {
            Status = status,
            FailureKind = status == DurableAuthorityCommitStatus.Busy ? DurableAuthorityFailureKind.Busy : DurableAuthorityFailureKind.None,
            Reason = reason
        };

        private bool WaitForMutex(Mutex mutex)
        {
            return _waitAdmission != null ? _waitAdmission(LockMilliseconds) : mutex.WaitOne(LockMilliseconds);
        }

        private static DurableAuthorityStoreCommitResult ReadFailureCommit(
            DurableAuthorityStoreReadResult read,
            string fallback = null)
        {
            var reason = read?.Reason ?? fallback ?? "AuthorityReadFailed";
            var status = read != null && read.FailureKind == DurableAuthorityFailureKind.BootstrapUnproven
                ? DurableAuthorityCommitStatus.ReadFailed
                : DurableAuthorityCommitStatus.ReadFailed;
            return new DurableAuthorityStoreCommitResult
            {
                Status = status, FailureKind = read?.FailureKind ?? DurableAuthorityFailureKind.Unreadable,
                Record = null, Sha256 = null, Revision = 0,
                Reason = reason
            };
        }
        private static byte[] Serialize(DurableRelaunchAuthorityRecord record) => new UTF8Encoding(false).GetBytes(DurableRelaunchAuthorityV4Validator.Serialize(record));
        private static bool RecordMatchesProof(
            DurableRelaunchAuthorityRecord record,
            WatchdogJournalBootstrapReceipt proof,
            out string reason)
        {
            reason = null;
            if (record == null || proof == null) { reason = "BootstrapProofMissing"; return false; }
            if (!string.Equals(record.BootstrapPrimaryPath, proof.PrimarySnapshotPath ?? proof.Path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.BootstrapPrimarySha256, proof.PrimarySnapshotSha256 ?? proof.SHA256, StringComparison.Ordinal) ||
                !string.Equals(record.BootstrapProofPath, proof.ProofPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.BootstrapProofSha256, proof.ProofSHA256, StringComparison.Ordinal) ||
                !string.Equals(record.BootstrapProofMarker, proof.ProofMarker, StringComparison.Ordinal) ||
                !string.Equals(record.BootstrapMarker, proof.Marker, StringComparison.Ordinal) ||
                record.BootstrapMaximumProcessRelaunches != proof.MaximumProcessRelaunches ||
                record.MaximumProcessRelaunches != proof.MaximumProcessRelaunches)
            {
                reason = "BootstrapProofBindingMismatch";
                return false;
            }
            return true;
        }
        private static DurableRelaunchAuthorityRecord BindBootstrapProof(DurableRelaunchAuthorityRecord record, WatchdogJournalBootstrapReceipt proof)
        {
            var clone = record?.Clone();
            if (clone == null || proof == null) return clone;
            clone.BootstrapPrimaryPath = proof.PrimarySnapshotPath ?? proof.Path;
            clone.BootstrapPrimarySha256 = proof.PrimarySnapshotSha256 ?? proof.SHA256;
            clone.BootstrapProofPath = proof.ProofPath;
            clone.BootstrapProofSha256 = proof.ProofSHA256;
            clone.BootstrapProofMarker = proof.ProofMarker;
            clone.BootstrapMaximumProcessRelaunches = proof.MaximumProcessRelaunches;
            clone.MaximumProcessRelaunches = proof.MaximumProcessRelaunches;
            clone.BootstrapMarker = proof.Marker;
            return clone;
        }

        private DurableAuthorityStoreReadResult PersistMigratedRecord(DurableRelaunchAuthorityRecord record, string expectedSessionId)
        {
            try
            {
                var normalized = Serialize(record);
                var temporary = Path.Combine(_directory, ".authority-migration-blocked-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    _io.CreateAndFlush(temporary, normalized);
                    _io.Replace(temporary, _path);
                }
                finally { _io.Delete(temporary); }
                var bytes = _io.ReadAllBytes(_path);
                var validation = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(_utf8.GetString(bytes), expectedSessionId);
                if (validation == null || validation.Record == null || validation.Blocked || validation.Migrated)
                    return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.WriteFailed, Reason = "BlockedMigrationReadBackFailed" };
                return new DurableAuthorityStoreReadResult
                {
                    Exists = true, Record = validation.Record.Clone(), Revision = validation.Record.AuthorityRevision,
                    Sha256 = Sha256Hex(bytes), Reason = "LegacyBlockedMigrated"
                };
            }
            catch (Exception ex)
            {
                return new DurableAuthorityStoreReadResult { Exists = true, Blocked = true, Unproven = true, FailureKind = DurableAuthorityFailureKind.WriteFailed, Reason = "BlockedMigrationPersistFailed:" + ex.GetBaseException().Message };
            }
        }
        internal static string Sha256Hex(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>())).Replace("-", string.Empty).ToUpperInvariant(); }
        private static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value ?? string.Empty));
    }

}
