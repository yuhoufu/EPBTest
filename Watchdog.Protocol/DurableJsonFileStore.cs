using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum DurableJsonReadStatus
    {
        Valid,
        Missing,
        JsonInvalid,
        IoError
    }

    public sealed class DurableJsonReadAttempt
    {
        public string Path { get; set; }
        public DurableJsonReadStatus Status { get; set; }
        public string Detail { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class DurableJsonReadResult<T> where T : class
    {
        public T Value { get; set; }
        public string SourcePath { get; set; }
        public DurableJsonReadStatus Status { get; set; }
        public List<DurableJsonReadAttempt> Attempts { get; } =
            new List<DurableJsonReadAttempt>();
    }

    public enum DurableJsonWriteStatus
    {
        Written = 0,
        Busy = 1,
        InvalidJson = 2,
        Failed = 3,
        OutcomeUnknown = 4
    }

    public enum DurableJsonWriteFailureKind
    {
        None = 0,
        InvalidPath = 1,
        InvalidJson = 2,
        MutexBusy = 3,
        CreateFailed = 4,
        FlushFailed = 5,
        ReplaceFailed = 6,
        ReadbackFailed = 7,
        ReadFailed = 8,
        Unknown = 9
    }

    public sealed class DurableJsonWriteResult
    {
        public DurableJsonWriteStatus Status { get; internal set; }
        public DurableJsonWriteFailureKind FailureKind { get; internal set; }
        public string Path { get; internal set; }
        public string BackupPath { get; internal set; }
        public string Sha256 { get; internal set; }
        public string KnownSha256 { get; internal set; }
        public string KnownPrimarySha256 { get; internal set; }
        public string KnownBackupSha256 { get; internal set; }
        public string Detail { get; internal set; }
        public int Attempts { get; internal set; }
        public bool BackupUpdated { get; internal set; }
        public bool PrimaryUpdated { get; internal set; }
        public bool OutcomeUnknown => Status == DurableJsonWriteStatus.OutcomeUnknown;
        public bool Succeeded => Status == DurableJsonWriteStatus.Written;
        public Exception Exception { get; internal set; }
    }

    public sealed class DurableJsonFileStoreException : IOException
    {
        public DurableJsonFileStoreException(DurableJsonWriteResult result)
            : base(result == null ? "Durable JSON write failed." :
                   (result.Detail ?? ("Durable JSON write failed: " + result.Status)))
        {
            Result = result;
        }

        public DurableJsonWriteResult Result { get; }
    }

    internal interface IDurableJsonFileIo
    {
        bool Exists(string path);
        byte[] ReadAllBytes(string path);
        void CreateAndFlush(string path, byte[] bytes);
        void ReplaceNoBackup(string temporaryPath, string targetPath);
        void MoveNew(string temporaryPath, string targetPath);
        void DeleteOwnedTemp(string path);
        IEnumerable<string> EnumerateOwnedTemps(string directory, string canonicalTargetHash);
    }

    internal enum DurableJsonWritePhase
    {
        BackupAfterFlush,
        BackupAfterPublish,
        PrimaryAfterFlush,
        PrimaryAfterPublish
    }

    internal interface IDurableJsonPhaseSignal
    {
        void Signal(DurableJsonWritePhase phase, string targetPath, string temporaryPath);
    }

    internal interface IDurableJsonClock
    {
        long Timestamp { get; }
        long Frequency { get; }
        void Sleep(int milliseconds);
    }

    internal sealed class WindowsDurableJsonFileIo : IDurableJsonFileIo
    {
        public bool Exists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                if (stream.Length < 0 || stream.Length > int.MaxValue)
                    throw new IOException("DurableJsonSizeInvalid");
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("DurableJsonShortRead");
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
                stream.Write(bytes ?? Array.Empty<byte>(), 0, bytes == null ? 0 : bytes.Length);
                stream.Flush(true);
            }
        }

        public void ReplaceNoBackup(string temporaryPath, string targetPath) =>
            File.Replace(temporaryPath, targetPath, null, true);

        public void MoveNew(string temporaryPath, string targetPath) => File.Move(temporaryPath, targetPath);

        public void DeleteOwnedTemp(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var name = Path.GetFileName(path);
            if (name == null || !name.StartsWith(".durable-json-", StringComparison.Ordinal)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public IEnumerable<string> EnumerateOwnedTemps(string directory, string canonicalTargetHash)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(canonicalTargetHash) ||
                !Directory.Exists(directory))
                return Array.Empty<string>();
            var prefix = ".durable-json-" + canonicalTargetHash + "-";
            return Directory.GetFiles(directory, prefix + "*.tmp");
        }
    }

    internal sealed class SystemDurableJsonClock : IDurableJsonClock
    {
        public long Timestamp => Stopwatch.GetTimestamp();
        public long Frequency => Stopwatch.Frequency;
        public void Sleep(int milliseconds)
        {
            if (milliseconds > 0) Thread.Sleep(milliseconds);
        }
    }

    /// <summary>
    /// Write-through JSON persistence with a last-known-good backup and
    /// classified multi-candidate reads. Callers own semantic validation.
    /// </summary>
    public static class DurableJsonFileStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private const int MutexAdmissionMilliseconds = 2000;
        private const int TransactionDeadlineMilliseconds = 3000;
        private const int MaximumJsonBytes = 16 * 1024 * 1024;

        public static DurableJsonReadResult<T> ReadFirstValid<T>(params string[] candidates)
            where T : class
        {
            var result = new DurableJsonReadResult<T> { Status = DurableJsonReadStatus.Missing };
            foreach (var candidate in candidates ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var attempt = new DurableJsonReadAttempt { Path = candidate };
                result.Attempts.Add(attempt);
                try
                {
                    if (!File.Exists(candidate))
                    {
                        attempt.Status = DurableJsonReadStatus.Missing;
                        attempt.Detail = "FileMissing";
                        continue;
                    }
                    var bytes = ReadAllBytesShared(candidate);
                    attempt.Sha256 = ComputeSha256(bytes);
                    var value = Deserialize<T>(bytes);
                    attempt.Status = DurableJsonReadStatus.Valid;
                    attempt.Detail = "Valid";
                    result.Value = value;
                    result.SourcePath = candidate;
                    result.Status = DurableJsonReadStatus.Valid;
                    return result;
                }
                catch (IOException ex)
                {
                    attempt.Status = DurableJsonReadStatus.IoError;
                    attempt.Detail = ex.GetBaseException().Message;
                    result.Status = DurableJsonReadStatus.IoError;
                }
                catch (UnauthorizedAccessException ex)
                {
                    attempt.Status = DurableJsonReadStatus.IoError;
                    attempt.Detail = ex.GetBaseException().Message;
                    result.Status = DurableJsonReadStatus.IoError;
                }
                catch (Exception ex)
                {
                    attempt.Status = DurableJsonReadStatus.JsonInvalid;
                    attempt.Detail = ex.GetBaseException().Message;
                    if (result.Status != DurableJsonReadStatus.IoError)
                        result.Status = DurableJsonReadStatus.JsonInvalid;
                }
            }
            return result;
        }

        public static DurableJsonReadResult<T> ReadLatestValid<T>(
            Func<T, long> revisionSelector,
            params string[] candidates)
            where T : class
        {
            if (revisionSelector == null) throw new ArgumentNullException(nameof(revisionSelector));
            var result = new DurableJsonReadResult<T> { Status = DurableJsonReadStatus.Missing };
            var bestRevision = long.MinValue;
            foreach (var candidate in candidates ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var attempt = new DurableJsonReadAttempt { Path = candidate };
                result.Attempts.Add(attempt);
                try
                {
                    if (!File.Exists(candidate))
                    {
                        attempt.Status = DurableJsonReadStatus.Missing;
                        attempt.Detail = "FileMissing";
                        continue;
                    }
                    var bytes = ReadAllBytesShared(candidate);
                    attempt.Sha256 = ComputeSha256(bytes);
                    var value = Deserialize<T>(bytes);
                    var revision = revisionSelector(value);
                    attempt.Status = DurableJsonReadStatus.Valid;
                    attempt.Detail = "Valid;Revision=" + revision;
                    if (result.Value != null && revision <= bestRevision) continue;
                    bestRevision = revision;
                    result.Value = value;
                    result.SourcePath = candidate;
                    result.Status = DurableJsonReadStatus.Valid;
                }
                catch (IOException ex)
                {
                    attempt.Status = DurableJsonReadStatus.IoError;
                    attempt.Detail = ex.GetBaseException().Message;
                    if (result.Value == null) result.Status = DurableJsonReadStatus.IoError;
                }
                catch (UnauthorizedAccessException ex)
                {
                    attempt.Status = DurableJsonReadStatus.IoError;
                    attempt.Detail = ex.GetBaseException().Message;
                    if (result.Value == null) result.Status = DurableJsonReadStatus.IoError;
                }
                catch (Exception ex)
                {
                    attempt.Status = DurableJsonReadStatus.JsonInvalid;
                    attempt.Detail = ex.GetBaseException().Message;
                    if (result.Value == null && result.Status != DurableJsonReadStatus.IoError)
                        result.Status = DurableJsonReadStatus.JsonInvalid;
                }
            }
            return result;
        }

        public static void WriteAtomicWithBackup(string path, byte[] bytes)
        {
            var result = TryWriteAtomicWithBackup(path, bytes);
            if (!result.Succeeded) throw new DurableJsonFileStoreException(result);
        }

        public static DurableJsonWriteResult TryWriteAtomicWithBackup(string path, byte[] bytes)
        {
            return TryWriteAtomicWithBackup(path, bytes, new WindowsDurableJsonFileIo(),
                new SystemDurableJsonClock(), null);
        }

        internal static DurableJsonWriteResult TryWriteAtomicWithBackup(
            string path,
            byte[] bytes,
            IDurableJsonFileIo io,
            IDurableJsonClock clock,
            Func<int, bool> mutexAdmission,
            IDurableJsonPhaseSignal phaseSignal = null)
        {
            var result = new DurableJsonWriteResult
            {
                Status = DurableJsonWriteStatus.Failed,
                FailureKind = DurableJsonWriteFailureKind.Unknown
            };
            var candidate = bytes ?? Array.Empty<byte>();
            string fullPath = null;
            string backupPath = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return InvalidResult(result, DurableJsonWriteFailureKind.InvalidPath, "PathMissing");
                fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(directory))
                    return InvalidResult(result, DurableJsonWriteFailureKind.InvalidPath, "DirectoryMissing");
                backupPath = fullPath + ".bak";
                result.Path = fullPath;
                result.BackupPath = backupPath;
                var ownerTargetHash = CanonicalTargetHash(fullPath);
                if (candidate.Length == 0 || candidate.Length > MaximumJsonBytes || !IsValidJson(candidate))
                    return InvalidResult(result, DurableJsonWriteFailureKind.InvalidJson, "JsonInvalid");
                io = io ?? new WindowsDurableJsonFileIo();
                clock = clock ?? new SystemDurableJsonClock();
                Directory.CreateDirectory(directory);

                using (var mutex = new Mutex(false, MutexName(ownerTargetHash)))
                {
                    bool held;
                    try { held = AcquireMutex(mutex, clock, mutexAdmission); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held)
                    {
                        result.Status = DurableJsonWriteStatus.Busy;
                        result.FailureKind = DurableJsonWriteFailureKind.MutexBusy;
                        result.Detail = "MutexAdmissionBusy";
                        return result;
                    }
                    try
                    {
                        CleanupOwnedTemps(fullPath, ownerTargetHash, io);
                        return ExecuteTransaction(fullPath, backupPath, candidate, result, io, clock,
                            ownerTargetHash, phaseSignal);
                    }
                    finally
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                result.Exception = ex;
                result.FailureKind = DurableJsonWriteFailureKind.Unknown;
                result.Detail = ex.GetBaseException().Message;
                return ProbeAfterException(result, fullPath, backupPath, candidate, io);
            }
            catch (IOException ex)
            {
                result.Exception = ex;
                result.FailureKind = DurableJsonWriteFailureKind.Unknown;
                result.Detail = ex.GetBaseException().Message;
                return ProbeAfterException(result, fullPath, backupPath, candidate, io);
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                result.FailureKind = DurableJsonWriteFailureKind.Unknown;
                result.Detail = ex.GetBaseException().Message;
                return ProbeAfterException(result, fullPath, backupPath, candidate, io);
            }
        }

        private static DurableJsonWriteResult ExecuteTransaction(
            string primaryPath,
            string backupPath,
            byte[] candidate,
            DurableJsonWriteResult result,
            IDurableJsonFileIo io,
            IDurableJsonClock clock,
            string ownerTargetHash,
            IDurableJsonPhaseSignal phaseSignal)
        {
            var deadline = AddMilliseconds(clock, TransactionDeadlineMilliseconds);
            var primary = Probe(primaryPath, io);
            var backup = Probe(backupPath, io);
            result.KnownPrimarySha256 = primary.Sha256;
            result.KnownBackupSha256 = backup.Sha256;
            if ((primary.Exists && primary.Unknown) && (backup.Exists && backup.Unknown))
                return UnknownResult(result, DurableJsonWriteFailureKind.ReadFailed, "BothCopiesUnobservable", null);

            var source = primary.Valid ? primary.Bytes : backup.Valid ? backup.Bytes : candidate;
            var sourceSha = ComputeSha256(source);
            var backupStage = EnsureFile(backupPath, source, sourceSha, deadline, result, io, clock,
                ownerTargetHash,
                DurableJsonWritePhase.BackupAfterFlush,
                DurableJsonWritePhase.BackupAfterPublish,
                phaseSignal);
            result.BackupUpdated = backupStage.Completed;
            if (!backupStage.Completed)
                return CompleteFailure(result, backupStage, primaryPath, backupPath, io);

            var candidateSha = ComputeSha256(candidate);
            var primaryStage = EnsureFile(primaryPath, candidate, candidateSha, deadline, result, io, clock,
                ownerTargetHash,
                DurableJsonWritePhase.PrimaryAfterFlush,
                DurableJsonWritePhase.PrimaryAfterPublish,
                phaseSignal);
            result.PrimaryUpdated = primaryStage.Completed;
            if (!primaryStage.Completed)
                return CompleteFailure(result, primaryStage, primaryPath, backupPath, io);

            var finalPrimary = Probe(primaryPath, io);
            var finalBackup = Probe(backupPath, io);
            result.KnownPrimarySha256 = finalPrimary.Sha256;
            result.KnownBackupSha256 = finalBackup.Sha256;
            result.KnownSha256 = finalPrimary.Sha256 ?? finalBackup.Sha256;
            if (finalPrimary.Unknown || finalBackup.Unknown)
                return UnknownResult(result, DurableJsonWriteFailureKind.ReadbackFailed,
                    "ReadbackOutcomeUnknown", null);
            if (!finalPrimary.Valid || !finalBackup.Valid ||
                !string.Equals(finalPrimary.Sha256, candidateSha, StringComparison.Ordinal) ||
                !string.Equals(finalBackup.Sha256, sourceSha, StringComparison.Ordinal))
            {
                result.Status = DurableJsonWriteStatus.Failed;
                result.FailureKind = DurableJsonWriteFailureKind.ReadbackFailed;
                result.Detail = "VerificationFailed:PrimaryOrBackupMismatch";
                return result;
            }
            result.Status = DurableJsonWriteStatus.Written;
            result.FailureKind = DurableJsonWriteFailureKind.None;
            result.Sha256 = finalPrimary.Sha256;
            result.Detail = "Committed:PrimaryAndBackupReadBackValid";
            return result;
        }

        private static StageResult EnsureFile(
            string targetPath,
            byte[] bytes,
            string expectedSha,
            long deadline,
            DurableJsonWriteResult result,
            IDurableJsonFileIo io,
            IDurableJsonClock clock,
            string ownerTargetHash,
            DurableJsonWritePhase afterFlushPhase,
            DurableJsonWritePhase afterPublishPhase,
            IDurableJsonPhaseSignal phaseSignal)
        {
            var stage = new StageResult();
            var backoff = 15;
            while (!Expired(clock, deadline))
            {
                result.Attempts++;
                var observed = Probe(targetPath, io);
                if (observed.Valid && string.Equals(observed.Sha256, expectedSha, StringComparison.Ordinal))
                {
                    stage.Completed = true;
                    stage.Observed = observed;
                    return stage;
                }

                string temporary = null;
                var failureKind = DurableJsonWriteFailureKind.CreateFailed;
                try
                {
                    var directory = Path.GetDirectoryName(targetPath);
                    temporary = TemporaryPath(directory, ownerTargetHash, afterFlushPhase);
                    io.CreateAndFlush(temporary, bytes);
                    var temporaryReadback = Probe(temporary, io);
                    if (!temporaryReadback.Valid || temporaryReadback.Bytes == null ||
                        temporaryReadback.Bytes.Length != bytes.Length ||
                        !string.Equals(temporaryReadback.Sha256, expectedSha, StringComparison.Ordinal))
                    {
                        failureKind = temporaryReadback.Unknown
                            ? DurableJsonWriteFailureKind.ReadFailed
                            : DurableJsonWriteFailureKind.ReadbackFailed;
                        stage.LastException = new IOException("TemporaryReadbackMismatch");
                        stage.FailureKind = failureKind;
                        throw stage.LastException;
                    }
                    phaseSignal?.Signal(afterFlushPhase, targetPath, temporary);
                    failureKind = DurableJsonWriteFailureKind.ReplaceFailed;
                    if (io.Exists(targetPath)) io.ReplaceNoBackup(temporary, targetPath);
                    else io.MoveNew(temporary, targetPath);
                    temporary = null;
                    phaseSignal?.Signal(afterPublishPhase, targetPath, null);
                    observed = Probe(targetPath, io);
                    if (observed.Valid && string.Equals(observed.Sha256, expectedSha, StringComparison.Ordinal))
                    {
                        stage.Completed = true;
                        stage.Observed = observed;
                        return stage;
                    }
                    failureKind = observed.Unknown
                        ? DurableJsonWriteFailureKind.ReadFailed
                        : DurableJsonWriteFailureKind.ReadbackFailed;
                    stage.LastException = new IOException("ReadbackMismatch");
                    stage.FailureKind = failureKind;
                }
                catch (Exception ex)
                {
                    stage.LastException = ex;
                    if (failureKind == DurableJsonWriteFailureKind.CreateFailed)
                        failureKind = ClassifyCreateFailure(ex);
                    stage.FailureKind = failureKind;
                    var after = Probe(targetPath, io);
                    stage.Observed = after;
                    if (after.Valid && string.Equals(after.Sha256, expectedSha, StringComparison.Ordinal))
                    {
                        stage.Completed = true;
                        return stage;
                    }
                }
                finally
                {
                    if (!string.IsNullOrEmpty(temporary)) io.DeleteOwnedTemp(temporary);
                }
                var remaining = RemainingMilliseconds(clock, deadline);
                if (remaining <= 0) break;
                clock.Sleep(Math.Min(backoff, remaining));
                backoff = Math.Min(250, backoff * 2);
            }
            return stage;
        }

        private static DurableJsonWriteResult CompleteFailure(
            DurableJsonWriteResult result,
            StageResult stage,
            string primaryPath,
            string backupPath,
            IDurableJsonFileIo io)
        {
            var primary = Probe(primaryPath, io);
            var backup = Probe(backupPath, io);
            result.KnownPrimarySha256 = primary.Sha256;
            result.KnownBackupSha256 = backup.Sha256;
            result.KnownSha256 = primary.Sha256 ?? backup.Sha256;
            result.Exception = stage.LastException;
            result.Detail = stage.LastException?.GetBaseException().Message ?? "DurableJsonStageFailed";
            if (primary.Unknown || backup.Unknown)
                return UnknownResult(result, DurableJsonWriteFailureKind.Unknown, "DurableJsonOutcomeUnknown", stage.LastException);
            result.Status = DurableJsonWriteStatus.Failed;
            result.FailureKind = stage.FailureKind == DurableJsonWriteFailureKind.None
                ? DurableJsonWriteFailureKind.ReplaceFailed
                : stage.FailureKind;
            return result;
        }

        private static DurableJsonWriteResult ProbeAfterException(
            DurableJsonWriteResult result,
            string primaryPath,
            string backupPath,
            byte[] candidate,
            IDurableJsonFileIo io)
        {
            if (io == null || string.IsNullOrEmpty(primaryPath))
                return UnknownResult(result, DurableJsonWriteFailureKind.Unknown, "DurableJsonOutcomeUnknown", result.Exception);
            try
            {
                var primary = Probe(primaryPath, io);
                var backup = Probe(backupPath, io);
                result.KnownPrimarySha256 = primary.Sha256;
                result.KnownBackupSha256 = backup.Sha256;
                result.KnownSha256 = primary.Sha256 ?? backup.Sha256;
                if (primary.Unknown || backup.Unknown)
                    return UnknownResult(result, DurableJsonWriteFailureKind.Unknown, "DurableJsonOutcomeUnknown", result.Exception);
                result.Status = DurableJsonWriteStatus.Failed;
                return result;
            }
            catch (Exception probeException)
            {
                result.Exception = probeException;
                return UnknownResult(result, DurableJsonWriteFailureKind.Unknown, "DurableJsonOutcomeUnknown", probeException);
            }
        }

        private static DurableJsonWriteResult InvalidResult(DurableJsonWriteResult result, DurableJsonWriteFailureKind kind, string detail)
        {
            result.Status = kind == DurableJsonWriteFailureKind.InvalidJson
                ? DurableJsonWriteStatus.InvalidJson : DurableJsonWriteStatus.Failed;
            result.FailureKind = kind;
            result.Detail = detail;
            return result;
        }

        private static DurableJsonWriteFailureKind ClassifyCreateFailure(Exception exception)
        {
            var detail = exception?.GetBaseException()?.Message ?? string.Empty;
            return detail.IndexOf("flush", StringComparison.OrdinalIgnoreCase) >= 0
                ? DurableJsonWriteFailureKind.FlushFailed
                : DurableJsonWriteFailureKind.CreateFailed;
        }

        private static DurableJsonWriteResult UnknownResult(
            DurableJsonWriteResult result,
            DurableJsonWriteFailureKind kind,
            string detail,
            Exception exception)
        {
            result.Status = DurableJsonWriteStatus.OutcomeUnknown;
            result.FailureKind = kind;
            result.Detail = detail;
            if (exception != null) result.Exception = exception;
            return result;
        }

        private static bool AcquireMutex(Mutex mutex, IDurableJsonClock clock, Func<int, bool> admission)
        {
            var deadline = AddMilliseconds(clock, TransactionDeadlineMilliseconds);
            var backoff = 15;
            while (!Expired(clock, deadline))
            {
                var remaining = Math.Min(MutexAdmissionMilliseconds, RemainingMilliseconds(clock, deadline));
                if (remaining <= 0) break;
                var held = admission != null ? admission(remaining) : mutex.WaitOne(remaining);
                if (held) return true;
                var wait = RemainingMilliseconds(clock, deadline);
                if (wait <= 0) break;
                clock.Sleep(Math.Min(backoff, wait));
                backoff = Math.Min(250, backoff * 2);
            }
            return false;
        }

        private static FileObservation Probe(string path, IDurableJsonFileIo io)
        {
            var observation = new FileObservation();
            if (string.IsNullOrEmpty(path)) { observation.Unknown = true; return observation; }
            try
            {
                observation.Exists = io.Exists(path);
                if (!observation.Exists) return observation;
                observation.Bytes = io.ReadAllBytes(path);
                observation.Sha256 = ComputeSha256(observation.Bytes);
                observation.Valid = IsValidJson(observation.Bytes);
                return observation;
            }
            catch (Exception ex)
            {
                observation.Unknown = true;
                observation.Error = ex;
                return observation;
            }
        }

        private static T Deserialize<T>(byte[] bytes) where T : class
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumJsonBytes };
            var value = serializer.Deserialize<T>(StrictUtf8.GetString(bytes));
            if (value == null) throw new InvalidDataException("JsonNull");
            return value;
        }

        private static bool IsValidJson(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumJsonBytes) return false;
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumJsonBytes };
                var value = serializer.DeserializeObject(StrictUtf8.GetString(bytes));
                return value != null;
            }
            catch { return false; }
        }

        private static string MutexName(string ownerTargetHash)
        {
            return "Local\\MTTFTest-DurableJson-" + ownerTargetHash.Substring(0, 32);
        }

        internal static string CanonicalWriterMutexName(string path)
        {
            var fullPath = Path.GetFullPath(path ?? string.Empty);
            return "Local\\MTTFTest-DurableJson-" + CanonicalTargetHash(fullPath).Substring(0, 32);
        }

        private static string CanonicalTargetHash(string fullPath)
        {
            return ComputeSha256(Utf8.GetBytes(fullPath.ToUpperInvariant()));
        }

        private static string TemporaryPath(string directory, string ownerTargetHash, DurableJsonWritePhase phase)
        {
            var phaseName = phase == DurableJsonWritePhase.BackupAfterFlush ||
                            phase == DurableJsonWritePhase.BackupAfterPublish
                ? "backup" : "primary";
            return Path.Combine(directory,
                ".durable-json-" + ownerTargetHash +
                "-pid" + Process.GetCurrentProcess().Id +
                "-" + phaseName + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void CleanupOwnedTemps(string targetPath, string ownerTargetHash, IDurableJsonFileIo io)
        {
            var directory = Path.GetDirectoryName(targetPath);
            IEnumerable<string> candidates;
            try { candidates = io.EnumerateOwnedTemps(directory, ownerTargetHash); }
            catch { return; }
            foreach (var candidate in candidates ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                var name = Path.GetFileName(candidate);
                if (name == null || !name.StartsWith(".durable-json-" + ownerTargetHash + "-", StringComparison.Ordinal))
                    continue;
                if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(targetPath + ".bak"), StringComparison.OrdinalIgnoreCase))
                    continue;
                try { io.DeleteOwnedTemp(candidate); } catch { }
            }
        }

        private static long AddMilliseconds(IDurableJsonClock clock, int milliseconds)
        {
            return clock.Timestamp + (long)(clock.Frequency * (milliseconds / 1000.0));
        }

        private static int RemainingMilliseconds(IDurableJsonClock clock, long deadline)
        {
            var ticks = deadline - clock.Timestamp;
            if (ticks <= 0) return 0;
            var ms = ticks * 1000.0 / Math.Max(1, clock.Frequency);
            return ms >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)Math.Ceiling(ms));
        }

        private static bool Expired(IDurableJsonClock clock, long deadline) => clock.Timestamp >= deadline;

        public static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>()))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            Exception last = null;
            // A polling reader may observe the short rename window between the
            // existence check and opening a replacement target.  Retry only the
            // shared-read boundary; JSON errors are still surfaced by the caller
            // and are never hidden by this retry.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                    {
                        if (stream.Length > int.MaxValue) throw new IOException("DurableJsonSizeInvalid");
                        var bytes = new byte[(int)stream.Length];
                        var offset = 0;
                        while (offset < bytes.Length)
                        {
                            var read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read <= 0) throw new EndOfStreamException("DurableJsonShortRead");
                            offset += read;
                        }
                        return bytes;
                    }
                }
                catch (IOException ex)
                {
                    last = ex;
                    if (attempt == 3) throw;
                    Thread.Sleep(2 << attempt);
                }
            }
            throw last ?? new IOException("DurableJsonReadFailed");
        }

        private sealed class FileObservation
        {
            public bool Exists;
            public bool Valid;
            public bool Unknown;
            public byte[] Bytes;
            public string Sha256;
            public Exception Error;
        }

        private sealed class StageResult
        {
            public bool Completed;
            public FileObservation Observed;
            public Exception LastException;
            public DurableJsonWriteFailureKind FailureKind;
        }
    }
}
