using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Write-through JSON persistence with a last-known-good backup and
    /// classified multi-candidate reads.  Callers own semantic validation.
    /// </summary>
    public static class DurableJsonFileStore
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static DurableJsonReadResult<T> ReadFirstValid<T>(params string[] candidates)
            where T : class
        {
            var result = new DurableJsonReadResult<T> { Status = DurableJsonReadStatus.Missing };
            var serializer = new JavaScriptSerializer();
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
                    var value = serializer.Deserialize<T>(Utf8.GetString(bytes));
                    if (value == null) throw new InvalidDataException("JSON deserialized to null.");
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

        /// <summary>
        /// Reads every candidate and returns the valid value with the greatest
        /// caller-defined revision. Candidate order breaks revision ties. This
        /// prevents an older Armed backup from overriding a newer Disarm mirror
        /// when the newest primary file is unreadable.
        /// </summary>
        public static DurableJsonReadResult<T> ReadLatestValid<T>(
            Func<T, long> revisionSelector,
            params string[] candidates)
            where T : class
        {
            if (revisionSelector == null) throw new ArgumentNullException(nameof(revisionSelector));
            var result = new DurableJsonReadResult<T> { Status = DurableJsonReadStatus.Missing };
            var serializer = new JavaScriptSerializer();
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
                    var value = serializer.Deserialize<T>(Utf8.GetString(bytes));
                    if (value == null) throw new InvalidDataException("JSON deserialized to null.");
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
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            bytes = bytes ?? Array.Empty<byte>();
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, ".durable-" + Guid.NewGuid().ToString("N") + ".tmp");
            var backup = fullPath + ".bak";
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(fullPath))
                            File.Replace(temporary, fullPath, backup, true);
                        else
                        {
                            File.Move(temporary, fullPath);
                            File.Copy(fullPath, backup, true);
                        }
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        // Antivirus/indexer and a concurrently polling watchdog may
                        // hold a transient handle. Shared readers normally avoid the
                        // collision; this bounded retry covers the remaining rename
                        // race without weakening atomic replacement.
                        Thread.Sleep(attempt * 20);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        public static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes ?? Array.Empty<byte>()))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length > int.MaxValue)
                    throw new IOException("Durable JSON file is too large.");
                var bytes = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw new EndOfStreamException("Durable JSON read was truncated.");
                    offset += read;
                }
                return bytes;
            }
        }
    }
}
