using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    public enum UnattendedAlarmDeliveryState
    {
        LocalOnly = 0,
        WebhookHealthy = 1,
        WebhookDegraded = 2,
        LocalPersistenceDegraded = 3
    }

    internal interface IUnattendedAlarmSink : IDisposable
    {
        UnattendedAlarmDeliveryState DeliveryState { get; }
        bool DeliveryDegraded { get; }
        void Publish(string severity, string code, string message,
            RecoveryFailureDomain domain, WatchdogSafetyHandoffReceipt handoff = null);
    }

    internal sealed class UnattendedAlarmSink : IUnattendedAlarmSink
    {
        private sealed class AlarmEnvelope
        {
            public int SchemaVersion { get; set; } = 1;
            public string EventId { get; set; }
            public string TimestampUtc { get; set; }
            public string Severity { get; set; }
            public string Code { get; set; }
            public string Message { get; set; }
            public string MachineName { get; set; }
            public string ProductVersion { get; set; } = "V2.14.1.0";
            public string SessionId { get; set; }
            public long PermitGeneration { get; set; }
            public string FailureDomain { get; set; }
            public string SafetyStage { get; set; }
            public string Fingerprint { get; set; }
            public int OccurrenceCount { get; set; } = 1;
            public string LastTimestampUtc { get; set; }
        }

        private sealed class Settings
        {
            internal bool Enabled;
            internal string Endpoint = string.Empty;
            internal string SecretEnvironmentVariable = "EPB_UNATTENDED_WEBHOOK_SECRET";
            internal int RequestTimeoutMs = 5000;
            internal long SpoolMaxBytes = 16777216;
            internal int[] RetryDelaysMs = { 1000, 5000, 15000, 30000, 60000, 300000 };
        }

        private readonly string _localDirectory;
        private readonly string _webhookSpoolDirectory;
        private readonly string _webhookDegradedPath;
        private readonly string _localDegradedPath;
        private readonly bool _formalSupervisorRequired;
        private readonly Settings _settings;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, byte> _pending =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly object _localGate = new object();
        private readonly object _webhookSpoolGate = new object();
        private readonly Task _worker;
        private int _webhookDegraded;
        private int _localPersistenceDegraded;

        internal static IUnattendedAlarmSink Create(string executableDirectory, string journalDirectory)
        {
            try { return new UnattendedAlarmSink(executableDirectory, journalDirectory); }
            catch (Exception ex)
            {
                return new LocalPersistenceFailedAlarmSink(
                    "AlarmSinkInitializationFailed:" + ex.GetBaseException().Message);
            }
        }

        private UnattendedAlarmSink(string executableDirectory, string journalDirectory)
            : this(executableDirectory, journalDirectory, null, null)
        {
        }

        internal UnattendedAlarmSink(
            string executableDirectory,
            string journalDirectory,
            HttpMessageHandler handler,
            int[] retryDelaysMs)
        {
            _settings = LoadSettings(Path.Combine(
                executableDirectory ?? Environment.CurrentDirectory,
                "Config", "UnattendedAlarmConfig.xml"));
            _formalSupervisorRequired = File.Exists(Path.Combine(
                executableDirectory ?? Environment.CurrentDirectory,
                "MTTFTest.UnattendedMode.required"));
            var root = WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory);
            _localDirectory = Path.Combine(root, "UnattendedAlarmJournal");
            _localDegradedPath = Path.Combine(root, "unattended-alarm-local-degraded.log");
            Directory.CreateDirectory(_localDirectory);

            if (retryDelaysMs != null && retryDelaysMs.Length > 0)
                _settings.RetryDelaysMs = retryDelaysMs
                    .Select(value => Math.Max(1, value)).ToArray();

            if (!_settings.Enabled)
            {
                // LocalOnly is normal: no remote directory, client or sending worker.
                _worker = Task.CompletedTask;
                return;
            }

            _webhookSpoolDirectory = Path.Combine(root, "UnattendedAlarmSpool");
            _webhookDegradedPath = Path.Combine(root, "unattended-alarm-degraded.log");
            Directory.CreateDirectory(_webhookSpoolDirectory);
            _http = handler == null ? new HttpClient() : new HttpClient(handler, true);
            _http.Timeout = TimeSpan.FromMilliseconds(_settings.RequestTimeoutMs);
            foreach (var path in Directory.GetFiles(_webhookSpoolDirectory, "*.json"))
                _pending.TryAdd(path, 0);
            if (!CanDeliver()) MarkWebhookDegraded("WebhookNotConfigured");
            _worker = Task.Run(DeliveryLoopAsync);
        }

        public UnattendedAlarmDeliveryState DeliveryState
        {
            get
            {
                if (Volatile.Read(ref _localPersistenceDegraded) != 0)
                    return UnattendedAlarmDeliveryState.LocalPersistenceDegraded;
                if (!_settings.Enabled)
                    return UnattendedAlarmDeliveryState.LocalOnly;
                return Volatile.Read(ref _webhookDegraded) != 0
                    ? UnattendedAlarmDeliveryState.WebhookDegraded
                    : UnattendedAlarmDeliveryState.WebhookHealthy;
            }
        }

        public bool DeliveryDegraded =>
            DeliveryState == UnattendedAlarmDeliveryState.WebhookDegraded ||
            DeliveryState == UnattendedAlarmDeliveryState.LocalPersistenceDegraded;

        public void Publish(string severity, string code, string message,
            RecoveryFailureDomain domain, WatchdogSafetyHandoffReceipt handoff = null)
        {
            var envelope = new AlarmEnvelope
            {
                EventId = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Severity = severity ?? "P0",
                Code = code ?? "UnattendedAlarm",
                Message = message ?? string.Empty,
                MachineName = Environment.MachineName,
                SessionId = handoff?.SessionId ?? string.Empty,
                PermitGeneration = handoff?.RelaunchPermitGeneration ?? 0,
                FailureDomain = domain.ToString(),
                SafetyStage = handoff?.Stage.ToString() ?? WatchdogSafetyStage.None.ToString()
            };
            envelope.LastTimestampUtc = envelope.TimestampUtc;
            envelope.Fingerprint = ComputeFingerprint(envelope);
            WriteLocalEvent(envelope.Code, envelope.Message);
            PersistLocal(envelope);
            if (_formalSupervisorRequired &&
                string.Equals(envelope.Severity, "P0", StringComparison.OrdinalIgnoreCase) &&
                !SupervisorP0AlarmClient.TryLatch(
                    envelope.EventId,
                    envelope.Code,
                    envelope.Message))
                MarkLocalPersistenceDegraded(
                    "SupervisorP0AlarmHardwareOwnerUnavailable");

            if (!_settings.Enabled) return;
            PersistWebhook(envelope);
        }

        private void PersistLocal(AlarmEnvelope envelope)
        {
            try
            {
                lock (_localGate)
                {
                    EnsureDirectoryCapacity(_localDirectory, true);
                    WriteCompressedEnvelope(_localDirectory, envelope);
                }
            }
            catch (Exception ex)
            {
                MarkLocalPersistenceDegraded(ex.GetBaseException().Message);
            }
        }

        private void PersistWebhook(AlarmEnvelope envelope)
        {
            try
            {
                lock (_webhookSpoolGate)
                {
                    EnsureDirectoryCapacity(_webhookSpoolDirectory, false);
                    var path = WriteCompressedEnvelope(_webhookSpoolDirectory, envelope);
                    _pending.TryAdd(path, 0);
                }
            }
            catch (Exception ex)
            {
                MarkWebhookDegraded("AlarmSpoolWriteFailed:" + ex.GetBaseException().Message);
            }
        }

        private string WriteCompressedEnvelope(string directory, AlarmEnvelope envelope)
        {
            var path = Path.Combine(directory, "alarm-" + envelope.Fingerprint + ".json");
            if (File.Exists(path))
            {
                try
                {
                    var existing = _serializer.Deserialize<AlarmEnvelope>(
                        File.ReadAllText(path, Encoding.UTF8));
                    if (existing != null && string.Equals(
                            existing.Fingerprint,
                            envelope.Fingerprint,
                            StringComparison.Ordinal))
                    {
                        existing.OccurrenceCount = Math.Max(1, existing.OccurrenceCount) + 1;
                        existing.LastTimestampUtc = envelope.TimestampUtc;
                        envelope = existing;
                    }
                }
                catch
                {
                    // Replace a corrupt prior record atomically with the current event.
                }
            }
            DurableJsonFileStore.WriteAtomicWithBackup(
                path,
                new UTF8Encoding(false).GetBytes(_serializer.Serialize(envelope)));
            return path;
        }

        private async Task DeliveryLoopAsync()
        {
            var attempt = 0;
            while (!_stop.IsCancellationRequested)
            {
                var delivered = false;
                foreach (var path in _pending.Keys.OrderBy(
                             value => value,
                             StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path))
                    {
                        _pending.TryRemove(path, out _);
                        continue;
                    }
                    if (!CanDeliver()) break;
                    try
                    {
                        var body = File.ReadAllText(path, Encoding.UTF8);
                        await SendAsync(body, _stop.Token).ConfigureAwait(false);
                        lock (_webhookSpoolGate)
                        {
                            if (File.Exists(path) && string.Equals(
                                    File.ReadAllText(path, Encoding.UTF8),
                                    body,
                                    StringComparison.Ordinal))
                            {
                                File.Delete(path);
                                try { File.Delete(path + ".bak"); } catch { }
                                _pending.TryRemove(path, out _);
                            }
                        }
                        delivered = true;
                        attempt = 0;
                        Interlocked.Exchange(ref _webhookDegraded, 0);
                    }
                    catch (Exception ex)
                    {
                        MarkWebhookDegraded(ex.GetBaseException().Message);
                        break;
                    }
                }
                var delayMs = delivered
                    ? Math.Min(1000, _settings.RetryDelaysMs[0])
                    : _settings.RetryDelaysMs[
                        Math.Min(attempt++, _settings.RetryDelaysMs.Length - 1)];
                try { await Task.Delay(delayMs, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task SendAsync(string body, CancellationToken token)
        {
            var secret = Environment.GetEnvironmentVariable(
                _settings.SecretEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("WebhookSecretMissing");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture);
            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                signature = BitConverter.ToString(hmac.ComputeHash(
                        Encoding.UTF8.GetBytes(timestamp + "\n" + body)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            using (var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint))
            {
                request.Headers.Add("X-EPB-Timestamp", timestamp);
                request.Headers.Add("X-EPB-Signature", "sha256=" + signature);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await _http.SendAsync(request, token).ConfigureAwait(false))
                    response.EnsureSuccessStatusCode();
            }
        }

        private bool CanDeliver()
        {
            Uri endpoint;
            return _settings.Enabled &&
                   Uri.TryCreate(_settings.Endpoint, UriKind.Absolute, out endpoint) &&
                   (endpoint.Scheme == Uri.UriSchemeHttps ||
                    endpoint.Scheme == Uri.UriSchemeHttp) &&
                   !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                       _settings.SecretEnvironmentVariable));
        }

        private void EnsureDirectoryCapacity(string directory, bool rollOldest)
        {
            var files = Directory.GetFiles(directory, "*.json")
                .Select(path => new FileInfo(path))
                .OrderBy(value => value.LastWriteTimeUtc)
                .ToArray();
            var total = files.Sum(value => value.Length);
            if (total <= _settings.SpoolMaxBytes) return;
            if (!rollOldest)
                throw new IOException("UnattendedAlarmSpoolCapacityExceeded");
            var target = _settings.SpoolMaxBytes * 3 / 4;
            foreach (var file in files)
            {
                if (total <= target) break;
                var length = file.Length;
                file.Delete();
                try { File.Delete(file.FullName + ".bak"); } catch { }
                total -= length;
            }
        }

        private void MarkWebhookDegraded(string detail)
        {
            var first = Interlocked.Exchange(ref _webhookDegraded, 1) == 0;
            if (!first) return;
            TryAppend(_webhookDegradedPath, "AlarmDeliveryDegraded:" + detail);
            WriteLocalEvent("AlarmDeliveryDegraded", detail);
        }

        private void MarkLocalPersistenceDegraded(string detail)
        {
            var first = Interlocked.Exchange(ref _localPersistenceDegraded, 1) == 0;
            if (!first) return;
            TryAppend(_localDegradedPath, "LocalPersistenceDegraded:" + detail);
            WriteLocalEvent("LocalPersistenceDegraded", detail);
        }

        private static void TryAppend(string path, string detail)
        {
            try
            {
                File.AppendAllText(
                    path,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " +
                    detail + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string ComputeFingerprint(AlarmEnvelope value)
        {
            var canonical = string.Join("\n", new[]
            {
                value.Severity ?? string.Empty,
                value.Code ?? string.Empty,
                value.Message ?? string.Empty,
                value.SessionId ?? string.Empty,
                value.PermitGeneration.ToString(CultureInfo.InvariantCulture),
                value.FailureDomain ?? string.Empty,
                value.SafetyStage ?? string.Empty
            });
            return DurableJsonFileStore.ComputeSha256(
                new UTF8Encoding(false).GetBytes(canonical));
        }

        private static void WriteLocalEvent(string code, string message)
        {
            try
            {
                EventLog.WriteEntry(
                    "MTTFTest.Watchdog",
                    (code ?? "UnattendedAlarm") + ": " + (message ?? string.Empty),
                    EventLogEntryType.Error,
                    4301);
            }
            catch
            {
            }
        }

        private static Settings LoadSettings(string path)
        {
            if (!File.Exists(path)) return new Settings();
            var root = XDocument.Load(path).Root;
            if (root == null) return new Settings();
            bool enabled;
            int timeout;
            long spool;
            var retrySeconds = ((string)root.Attribute("RetrySeconds") ??
                "1,5,15,30,60,300").Split(',')
                .Select(value =>
                {
                    int seconds;
                    return int.TryParse(
                               value.Trim(),
                               NumberStyles.Integer,
                               CultureInfo.InvariantCulture,
                               out seconds)
                        ? Math.Max(1, Math.Min(3600, seconds)) * 1000
                        : 0;
                })
                .Where(value => value > 0)
                .ToArray();
            return new Settings
            {
                Enabled = bool.TryParse((string)root.Attribute("Enabled"), out enabled) && enabled,
                Endpoint = ((string)root.Attribute("Endpoint") ?? string.Empty).Trim(),
                SecretEnvironmentVariable = NormalizeSecretEnvironmentVariable(
                    (string)root.Attribute("SecretEnvironmentVariable")),
                RequestTimeoutMs = int.TryParse(
                    (string)root.Attribute("RequestTimeoutMs"),
                    out timeout)
                    ? Math.Max(1000, Math.Min(30000, timeout))
                    : 5000,
                SpoolMaxBytes = long.TryParse(
                    (string)root.Attribute("SpoolMaxBytes"),
                    out spool)
                    ? Math.Max(1048576, Math.Min(268435456, spool))
                    : 16777216,
                RetryDelaysMs = retrySeconds.Length > 0
                    ? retrySeconds
                    : new[] { 1000, 5000, 15000, 30000, 60000, 300000 }
            };
        }

        private static string NormalizeSecretEnvironmentVariable(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? "EPB_UNATTENDED_WEBHOOK_SECRET"
                : normalized;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _worker?.Wait(1000); } catch { }
            _http?.Dispose();
            _stop.Dispose();
        }

        private sealed class LocalPersistenceFailedAlarmSink : IUnattendedAlarmSink
        {
            private readonly string _initializationFailure;

            internal LocalPersistenceFailedAlarmSink(string initializationFailure)
            {
                _initializationFailure = initializationFailure ??
                    "AlarmSinkInitializationFailed";
                WriteLocalEvent("LocalPersistenceDegraded", _initializationFailure);
            }

            public UnattendedAlarmDeliveryState DeliveryState =>
                UnattendedAlarmDeliveryState.LocalPersistenceDegraded;

            public bool DeliveryDegraded => true;

            public void Publish(string severity, string code, string message,
                RecoveryFailureDomain domain, WatchdogSafetyHandoffReceipt handoff = null)
            {
                WriteLocalEvent(
                    code ?? "UnattendedAlarm",
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}; Severity={1}; Domain={2}; {3}",
                        _initializationFailure,
                        severity ?? "P0",
                        domain,
                        message ?? string.Empty));
            }

            public void Dispose()
            {
            }
        }
    }
}
