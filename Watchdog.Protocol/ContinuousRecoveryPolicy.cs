using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public static class ContinuousRecoveryPolicy
    {
        public const int MaximumRestarts = 3;
        public static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);

        public static DateTime NextAllowedUtc(IEnumerable<DateTime> attempts, DateTime nowUtc)
        {
            var recent = (attempts ?? Enumerable.Empty<DateTime>())
                .Where(t => t + RestartWindow > nowUtc).OrderBy(t => t).ToArray();
            return recent.Length < MaximumRestarts ? nowUtc : recent[recent.Length - MaximumRestarts] + RestartWindow;
        }

        public static bool TryReadRetryUtc(string detail, out DateTime retryUtc)
        {
            retryUtc = default(DateTime);
            const string marker = "NextRetryUtc=";
            var start = (detail ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            var value = detail.Substring(start + marker.Length).Split(';', ' ', '\r', '\n')[0];
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out retryUtc);
        }

        public static bool CanRetrySoftwareFailure(string code, bool permanent)
        {
            if (permanent) return false;
            var value = code ?? string.Empty;
            return !new[] { "ManualStop", "EmergencyStop", "AuthorizationRevoked", "SessionRevoked",
                "IdentityConflict", "HardwareConfirmed", "PermanentAlarm" }
                .Any(prefix => value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>Supervisor-owned installation-wide frequency fence; a process death cannot reset it.</summary>
    public sealed class RecoveryRestartWindowStore
    {
        public sealed class Attempt
        {
            public string Identity { get; set; }
            public long UtcTicks { get; set; }
        }
        private readonly string _path;
        private readonly string _mutexName;
        public RecoveryRestartWindowStore(string path)
        {
            _path = Path.GetFullPath(path);
            using (var sha = SHA256.Create())
                _mutexName = "Global\\EPB.RestartWindow." + BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()))).Replace("-", "");
        }

        public bool TryReserve(string identity, DateTime nowUtc, out DateTime nextRetryUtc)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new ArgumentException(nameof(identity));
            using (var mutex = new Mutex(false, _mutexName))
            {
                var held = false;
                try
                {
                    try { held = mutex.WaitOne(3000); } catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("RestartWindowBusy");
                    var json = new JavaScriptSerializer();
                    var attempts = File.Exists(_path)
                        ? json.Deserialize<List<Attempt>>(File.ReadAllText(_path, Encoding.UTF8))
                        : new List<Attempt>();
                    if (attempts == null || attempts.Any(a => a == null || a.UtcTicks <= 0 || a.UtcTicks > DateTime.MaxValue.Ticks || string.IsNullOrWhiteSpace(a.Identity)))
                        throw new InvalidDataException("RestartWindowInvalid");
                    // Duplicate requests cannot start a second process after an uncertain response.
                    if (attempts.Any(a => a.Identity == identity)) throw new InvalidOperationException("RestartAttemptAlreadyReserved");
                    nextRetryUtc = ContinuousRecoveryPolicy.NextAllowedUtc(
                        attempts.Select(a => new DateTime(a.UtcTicks, DateTimeKind.Utc)), nowUtc);
                    if (nextRetryUtc > nowUtc) return false;
                    attempts.RemoveAll(a => new DateTime(a.UtcTicks, DateTimeKind.Utc) + ContinuousRecoveryPolicy.RestartWindow <= nowUtc);
                    attempts.Add(new Attempt { Identity = identity, UtcTicks = nowUtc.Ticks });
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        var bytes = Encoding.UTF8.GetBytes(json.Serialize(attempts));
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                        if (File.Exists(_path)) File.Replace(temporary, _path, null);
                        else File.Move(temporary, _path);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return true;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
    }
}
