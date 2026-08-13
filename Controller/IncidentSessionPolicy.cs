using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;

namespace Controller
{
    /// <summary>
    /// Runtime knobs for DAQ incident sessions.  The policy only decides what
    /// evidence may be attached; it never changes a recovery or safety decision.
    /// </summary>
    public sealed class IncidentSessionPolicyOptions
    {
        public TimeSpan MergeWindow { get; set; } = TimeSpan.FromSeconds(60);
        public int ActiveSessionLimitPerDevice { get; set; } = 4;
        public long DailyByteQuota { get; set; } = 512L * 1024L * 1024L;
        public long MinimumFreeBytes { get; set; } = 10L * 1024L * 1024L * 1024L;
        public int CompleteRetentionPerDevice { get; set; } = 10;
        public int StormWarningThresholdPerHour { get; set; } = 2;
        public int StormErrorThresholdPerHour { get; set; } = 5;
        public bool CopyRecentCycles { get; set; }

        internal IncidentSessionPolicyOptions Normalize()
        {
            var warning = Math.Max(1, StormWarningThresholdPerHour);
            var error = Math.Max(warning + 1, StormErrorThresholdPerHour);
            return new IncidentSessionPolicyOptions
            {
                MergeWindow = TimeSpan.FromSeconds(
                    Math.Max(1, Math.Min(3600, MergeWindow.TotalSeconds))),
                ActiveSessionLimitPerDevice = Math.Max(1, Math.Min(1024, ActiveSessionLimitPerDevice)),
                DailyByteQuota = Math.Max(0, DailyByteQuota),
                MinimumFreeBytes = Math.Max(0, MinimumFreeBytes),
                CompleteRetentionPerDevice = Math.Max(1, Math.Min(10000, CompleteRetentionPerDevice)),
                StormWarningThresholdPerHour = warning,
                StormErrorThresholdPerHour = error,
                CopyRecentCycles = CopyRecentCycles
            };
        }
    }

    public enum IncidentStormLevel
    {
        Normal,
        Warning,
        Error
    }

    /// <summary>One immutable observation submitted by an incident phase.</summary>
    public sealed class IncidentSessionRequest
    {
        public string Device { get; set; } = string.Empty;
        public string FaultCode { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public DateTime OccurredUtc { get; set; }
        public bool IsTrigger { get; set; }
        public bool IsTerminal { get; set; }
        public bool IsHeavyEvidence { get; set; }
        public long EstimatedHeavyBytes { get; set; }
        /// <summary>
        /// Available bytes on the incident volume. A negative value means that
        /// the caller could not probe the volume; when a minimum is configured,
        /// heavy evidence is conservatively suppressed for this observation.
        /// </summary>
        public long AvailableFreeBytes { get; set; } = -1;
    }

    public sealed class IncidentSessionDecision
    {
        public string SessionKey { get; internal set; } = string.Empty;
        public Guid SessionId { get; internal set; }
        public bool IsNewSession { get; internal set; }
        public bool Merged { get; internal set; }
        public bool HeavyEvidenceAllowed { get; internal set; }
        public bool HeavyEvidenceSuppressed => !HeavyEvidenceAllowed;
        public string HeavySuppressionReason { get; internal set; } = string.Empty;
        public IncidentStormLevel StormLevel { get; internal set; }
        public int StormCountLastHour { get; internal set; }
        public int ActiveSessionsForDevice { get; internal set; }
        public bool SummaryMustBeWritten => true;
    }

    /// <summary>
    /// In-memory incident session state. SessionKey is deliberately made from
    /// normalized Device + immutable fault code + RunEpoch. A terminal session
    /// is never reopened; a later observation creates a new SessionId even though
    /// its deterministic SessionKey is the same.
    /// </summary>
    public sealed class IncidentSessionPolicy
    {
        private sealed class SessionState
        {
            internal readonly string Key;
            internal readonly string Device;
            internal readonly Guid Id = Guid.NewGuid();
            internal DateTime FirstUtc;
            internal DateTime LastUtc;
            internal bool Terminal;

            internal SessionState(string key, string device, DateTime now)
            {
                Key = key;
                Device = device;
                FirstUtc = now;
                LastUtc = now;
            }
        }

        private sealed class DeviceState
        {
            internal readonly object Gate = new object();
            // Keep every runtime session instance. A deterministic key may
            // legitimately start a second session after the 60s window; a
            // single key->state slot would overwrite the previous active
            // session and under-count the quota gate.
            internal readonly Dictionary<Guid, SessionState> Sessions =
                new Dictionary<Guid, SessionState>();
            internal readonly Dictionary<string, SessionState> LatestByKey =
                new Dictionary<string, SessionState>(StringComparer.OrdinalIgnoreCase);
            internal readonly Queue<DateTime> TriggerTimes = new Queue<DateTime>();
            internal DateTime DailyDateUtc;
            internal long DailyBytes;
        }

        private readonly IncidentSessionPolicyOptions _options;
        private readonly Action<string> _warning;
        private readonly ConcurrentDictionary<string, DeviceState> _devices =
            new ConcurrentDictionary<string, DeviceState>(StringComparer.OrdinalIgnoreCase);

        public IncidentSessionPolicy(
            IncidentSessionPolicyOptions options = null,
            Action<string> warningSink = null)
        {
            _options = (options ?? new IncidentSessionPolicyOptions()).Normalize();
            _warning = warningSink;
        }

        public IncidentSessionPolicyOptions Options => _options;

        public static string NormalizeDevice(string device)
        {
            var value = string.IsNullOrWhiteSpace(device) ? "UNKNOWN" : device.Trim();
            return value.ToUpperInvariant();
        }

        public static string NormalizeFaultCode(string faultCode)
        {
            var value = string.IsNullOrWhiteSpace(faultCode) ? "UNKNOWN" : faultCode.Trim();
            // Fault codes are identity values, not free-form reasons. Collapse
            // whitespace so callback formatting cannot create a second session.
            return string.Join(" ", value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();
        }

        public static string BuildSessionKey(string device, string faultCode, long runEpoch)
        {
            return NormalizeDevice(device) + "|" +
                   NormalizeFaultCode(faultCode) + "|" +
                   Math.Max(0, runEpoch).ToString(CultureInfo.InvariantCulture);
        }

        public IncidentSessionDecision Observe(IncidentSessionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var device = NormalizeDevice(request.Device);
            var key = BuildSessionKey(device, request.FaultCode, request.RunEpoch);
            var now = request.OccurredUtc == default
                ? DateTime.UtcNow
                : request.OccurredUtc.ToUniversalTime();
            var state = _devices.GetOrAdd(device, _ => new DeviceState());
            lock (state.Gate)
            {
                ResetDailyIfNeeded(state, now);
                PruneTriggers(state, now);

                state.LatestByKey.TryGetValue(key, out var session);
                var merged = session != null && !session.Terminal &&
                             now >= session.LastUtc &&
                             now - session.LastUtc <= _options.MergeWindow;
                if (!merged)
                {
                    session = new SessionState(key, device, now);
                    state.Sessions[session.Id] = session;
                    state.LatestByKey[key] = session;
                }
                else
                {
                    session.LastUtc = now;
                }

                if (request.IsTrigger || (!merged && !request.IsTerminal))
                    state.TriggerTimes.Enqueue(now);
                if (request.IsTerminal) session.Terminal = true;

                var active = state.Sessions.Values.Count(item => !item.Terminal);
                var stormCount = state.TriggerTimes.Count;
                var storm = stormCount > _options.StormErrorThresholdPerHour
                    ? IncidentStormLevel.Error
                    : stormCount > _options.StormWarningThresholdPerHour
                        ? IncidentStormLevel.Warning
                        : IncidentStormLevel.Normal;

                var suppressionReason = string.Empty;
                var heavyAllowed = !request.IsHeavyEvidence ||
                                   CanWriteHeavyUnderGates(
                                       state,
                                       request.EstimatedHeavyBytes,
                                       request.AvailableFreeBytes,
                                       active,
                                       out suppressionReason);
                if (!heavyAllowed && request.IsHeavyEvidence)
                {
                    try
                    {
                        _warning?.Invoke(
                            $"DAQ事故重证据已抑制：Device={device} SessionKey={key} " +
                            $"Reason={suppressionReason}");
                    }
                    catch { }
                }

                if (storm != IncidentStormLevel.Normal)
                {
                    try
                    {
                        _warning?.Invoke(
                            $"DAQ事故风暴：Device={device} CountLastHour={stormCount} " +
                            $"Level={storm} SessionKey={key}；不改变恢复控制。");
                    }
                    catch { }
                }

                return new IncidentSessionDecision
                {
                    SessionKey = key,
                    SessionId = session.Id,
                    IsNewSession = !merged,
                    Merged = merged,
                    HeavyEvidenceAllowed = heavyAllowed,
                    HeavySuppressionReason = heavyAllowed ? string.Empty : suppressionReason,
                    StormLevel = storm,
                    StormCountLastHour = stormCount,
                    ActiveSessionsForDevice = active
                };
            }
        }

        public void RecordPersistedBytes(string device, long bytes, DateTime occurredUtc = default)
        {
            if (bytes <= 0) return;
            var normalized = NormalizeDevice(device);
            var now = occurredUtc == default ? DateTime.UtcNow : occurredUtc.ToUniversalTime();
            var state = _devices.GetOrAdd(normalized, _ => new DeviceState());
            lock (state.Gate)
            {
                ResetDailyIfNeeded(state, now);
                state.DailyBytes = Math.Max(0, state.DailyBytes + bytes);
            }
        }

        public long GetDailyPersistedBytes(string device, DateTime utcNow = default)
        {
            var normalized = NormalizeDevice(device);
            var now = utcNow == default ? DateTime.UtcNow : utcNow.ToUniversalTime();
            var state = _devices.GetOrAdd(normalized, _ => new DeviceState());
            lock (state.Gate)
            {
                ResetDailyIfNeeded(state, now);
                return state.DailyBytes;
            }
        }

        public int GetActiveSessionCount(string device)
        {
            var normalized = NormalizeDevice(device);
            if (!_devices.TryGetValue(normalized, out var state)) return 0;
            lock (state.Gate) return state.Sessions.Values.Count(item => !item.Terminal);
        }

        public bool IsSessionActive(string sessionKey)
        {
            if (string.IsNullOrWhiteSpace(sessionKey)) return false;
            foreach (var state in _devices.Values)
                lock (state.Gate)
                    if (state.Sessions.Values.Any(item =>
                            string.Equals(item.Key, sessionKey, StringComparison.OrdinalIgnoreCase) &&
                            !item.Terminal)) return true;
            return false;
        }

        public static IncidentSessionPolicy FromAppSettings(
            NameValueCollection values,
            Action<string> warningSink = null)
        {
            values = values ?? new NameValueCollection();
            var options = new IncidentSessionPolicyOptions();
            options.MergeWindow = TimeSpan.FromSeconds(ReadDouble(
                values, "DaqIncidentMergeWindowSeconds", 60, 1, 3600, warningSink));
            options.ActiveSessionLimitPerDevice = ReadInt(
                values, "DaqIncidentActiveSessionLimitPerDevice", 4, 1, 1024, warningSink);
            options.DailyByteQuota = ReadLong(
                values, "DaqIncidentDailyBytesQuota", 512L * 1024L * 1024L, 0,
                long.MaxValue, warningSink, "DaqIncidentDailyByteQuota", "DaqIncidentDailyQuotaBytes");
            options.MinimumFreeBytes = ReadLong(
                values, "DaqIncidentMinimumFreeBytes", 10L * 1024L * 1024L * 1024L, 0,
                long.MaxValue, warningSink, "DaqIncidentMinFreeBytes", "DaqIncidentMinimumFreeSpaceBytes");
            options.CompleteRetentionPerDevice = ReadInt(
                values, "DaqIncidentCompleteRetentionPerDevice", 10, 1, 10000, warningSink,
                "DaqIncidentRetentionPerDevice", "DaqIncidentRetentionCountPerDevice");
            options.StormWarningThresholdPerHour = ReadInt(
                values, "DaqIncidentStormWarnPerHour", 2, 1, 100000, warningSink);
            options.StormErrorThresholdPerHour = ReadInt(
                values, "DaqIncidentStormErrorPerHour", 5, 2, 100000, warningSink);
            options.CopyRecentCycles = ReadBool(
                values, "DaqIncidentCopyRecentCycles", false, warningSink);
            return new IncidentSessionPolicy(options, warningSink);
        }

        private bool CanWriteHeavyUnderGates(
            DeviceState state,
            long estimatedBytes,
            long availableFreeBytes,
            int active,
            out string reason)
        {
            reason = string.Empty;
            if (active > _options.ActiveSessionLimitPerDevice)
            {
                reason = "ActiveSessionLimit";
                return false;
            }
            if (_options.MinimumFreeBytes > 0 && availableFreeBytes < 0)
            {
                reason = "MinimumFreeSpaceUnknown";
                return false;
            }
            if (availableFreeBytes >= 0 && availableFreeBytes < _options.MinimumFreeBytes)
            {
                reason = "MinimumFreeSpace";
                return false;
            }
            var bytes = Math.Max(0, state.DailyBytes);
            var estimate = Math.Max(0, estimatedBytes);
            if (_options.DailyByteQuota > 0 &&
                (bytes >= _options.DailyByteQuota ||
                 estimate > _options.DailyByteQuota - Math.Min(bytes, _options.DailyByteQuota)))
            {
                reason = "DailyByteQuota";
                return false;
            }
            return true;
        }

        private static void ResetDailyIfNeeded(DeviceState state, DateTime now)
        {
            if (state.DailyDateUtc.Date == now.Date) return;
            state.DailyDateUtc = now.Date;
            state.DailyBytes = 0;
        }

        private static void PruneTriggers(DeviceState state, DateTime now)
        {
            var cutoff = now - TimeSpan.FromHours(1);
            while (state.TriggerTimes.Count > 0 && state.TriggerTimes.Peek() < cutoff)
                state.TriggerTimes.Dequeue();
        }

        private static bool ReadBool(
            NameValueCollection values,
            string key,
            bool fallback,
            Action<string> warning)
        {
            var raw = values[key];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (bool.TryParse(raw, out var value)) return value;
            Warn(warning, $"{key} 非法，已回退 {fallback}。");
            return fallback;
        }

        private static int ReadInt(
            NameValueCollection values,
            string key,
            int fallback,
            int min,
            int max,
            Action<string> warning,
            params string[] aliases)
        {
            var raw = First(values, key, aliases);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= min && value <= max) return value;
            Warn(warning, $"{key} 非法，已回退 {fallback}。");
            return fallback;
        }

        private static long ReadLong(
            NameValueCollection values,
            string key,
            long fallback,
            long min,
            long max,
            Action<string> warning,
            params string[] aliases)
        {
            var raw = First(values, key, aliases);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= min && value <= max) return value;
            Warn(warning, $"{key} 非法，已回退 {fallback}。");
            return fallback;
        }

        private static double ReadDouble(
            NameValueCollection values,
            string key,
            double fallback,
            double min,
            double max,
            Action<string> warning)
        {
            var raw = values[key];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value >= min && value <= max) return value;
            Warn(warning, $"{key} 非法，已回退 {fallback}。");
            return fallback;
        }

        private static string First(NameValueCollection values, string key, IEnumerable<string> aliases)
        {
            var value = values[key];
            if (!string.IsNullOrWhiteSpace(value)) return value;
            foreach (var alias in aliases ?? Enumerable.Empty<string>())
            {
                value = values[alias];
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        private static void Warn(Action<string> warning, string message)
        {
            try { warning?.Invoke(message); } catch { }
        }
    }
}
