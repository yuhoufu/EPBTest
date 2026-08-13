using System;
using System.Collections.Specialized;
using System.Configuration;

namespace DataOperation
{
    /// <summary>
    /// Strict program-level storage format.  There is deliberately no None value:
    /// every durable cycle export must retain at least one recoverable format.
    /// </summary>
    public enum StorageFormatLevel
    {
        CsvOnly = 0,
        BinOnly = 1,
        CsvAndBin = 2
    }

    /// <summary>
    /// Learning retention is intentionally a named policy.  Numeric values and
    /// aliases such as None are rejected so a typo can never silently disable
    /// the safety guard.
    /// </summary>
    public enum LearningRetentionMode
    {
        Count = 0,
        Unlimited = 1
    }

    /// <summary>Persistence sampling policy for the power-supply telemetry writer.</summary>
    public sealed class TelemetryPersistencePolicy
    {
        public TelemetryPersistenceMode Mode { get; set; } = TelemetryPersistenceMode.Layered;
        public int SteadyRateHz { get; set; } = 1;
        public int EventRateHz { get; set; } = 10;
        public int PreEventSeconds { get; set; } = 60;
        public int PostEventSeconds { get; set; } = 30;
        public int RetentionDays { get; set; } = 3;
        public int RotateHours { get; set; } = 1;
        public long RotateBytes { get; set; } = 100L * 1024L * 1024L;
        /// <summary>Raw rollback has its own explicit rotation ceiling.</summary>
        public long RawRotateBytes { get; set; } = 100L * 1024L * 1024L;

        public override string ToString() =>
            $"Mode={Mode} SteadyRateHz={SteadyRateHz} EventRateHz={EventRateHz} " +
            $"PreEventSeconds={PreEventSeconds} PostEventSeconds={PostEventSeconds} " +
            $"RetentionDays={RetentionDays} RotateHours={RotateHours} RotateBytes={RotateBytes} " +
            $"RawRotateBytes={RawRotateBytes}";
    }

    public enum TelemetryPersistenceMode
    {
        Layered = 0,
        Raw = 1
    }

    /// <summary>Resolved EXE configuration used by all cycle-export call sites.</summary>
    public sealed class ProgramStoragePolicy
    {
        public StorageFormatLevel Latest { get; set; } = StorageFormatLevel.CsvOnly;
        public StorageFormatLevel Alarm { get; set; } = StorageFormatLevel.CsvOnly;
        public StorageFormatLevel Learning { get; set; } = StorageFormatLevel.BinOnly;
        public LearningRetentionMode LearningRetentionMode { get; set; } = LearningRetentionMode.Count;
        public int LearningSuccessfulRunRetainCount { get; set; } = 3;
        public int LearningFailedRunRetainCount { get; set; } = 3;
        public bool HistoricalEnabled { get; set; } = true;
        public int HistoricalRetainCyclesPerChannel { get; set; } = 12;
        public long HistoricalMinimumFreeBytes { get; set; } = 10L * 1024L * 1024L * 1024L;
        public TelemetryPersistencePolicy Telemetry { get; set; } = new TelemetryPersistencePolicy();

        public static ProgramStoragePolicy Load(Action<string> warningSink = null,
            NameValueCollection appSettings = null)
        {
            var settings = appSettings ?? ConfigurationManager.AppSettings;
            var policy = new ProgramStoragePolicy
            {
                Latest = ParseLevel(settings?["LatestStorageLevel"], StorageFormatLevel.CsvOnly,
                    "LatestStorageLevel", warningSink),
                Alarm = ParseLevel(settings?["AlarmStorageLevel"], StorageFormatLevel.CsvOnly,
                    "AlarmStorageLevel", warningSink),
                Learning = ParseLevel(settings?["LearningStorageLevel"], StorageFormatLevel.BinOnly,
                    "LearningStorageLevel", warningSink),
                LearningRetentionMode = ParseLearningRetentionMode(
                    settings?["LearningRetentionMode"],
                    LearningRetentionMode.Count,
                    warningSink),
                LearningSuccessfulRunRetainCount = ParseIntWithMissingWarning(
                    settings?["LearningSuccessfulRunRetainCount"],
                    3,
                    1,
                    100000,
                    "LearningSuccessfulRunRetainCount",
                    warningSink),
                LearningFailedRunRetainCount = ParseIntWithMissingWarning(
                    settings?["LearningFailedRunRetainCount"],
                    3,
                    1,
                    100000,
                    "LearningFailedRunRetainCount",
                    warningSink),
                HistoricalEnabled = ParseBoolean(settings?["HistoricalEnabled"], true,
                    "HistoricalEnabled", warningSink),
                HistoricalRetainCyclesPerChannel = ParseInt(settings?["HistoricalRetainCyclesPerChannel"],
                    12, 1, 100000, "HistoricalRetainCyclesPerChannel", warningSink),
                HistoricalMinimumFreeBytes = ParseLong(
                    settings?["HistoricalMinimumFreeBytes"],
                    10L * 1024L * 1024L * 1024L,
                    1L,
                    long.MaxValue,
                    "HistoricalMinimumFreeBytes",
                    warningSink)
            };
            policy.Telemetry = ParseTelemetryPolicy(settings, warningSink);
            return policy;
        }

        public static LearningRetentionMode ParseLearningRetentionMode(
            string value,
            LearningRetentionMode fallback = LearningRetentionMode.Count,
            Action<string> warningSink = null)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                switch (value.Trim().ToLowerInvariant())
                {
                    case "count": return LearningRetentionMode.Count;
                    case "unlimited": return LearningRetentionMode.Unlimited;
                }
            }

            warningSink?.Invoke(
                $"LearningRetentionMode 缺失或非法，已回退 {fallback}。Value={value ?? "<missing>"}");
            return fallback;
        }

        public static StorageFormatLevel ParseLevel(
            string value,
            StorageFormatLevel fallback,
            string settingName = "StorageLevel",
            Action<string> warningSink = null)
        {
            // Keep the EXE contract strict: only the three named values are
            // accepted.  Enum.TryParse would also accept numeric strings
            // ("0", "1", ...), which makes a typo look like a valid policy.
            if (!string.IsNullOrWhiteSpace(value))
            {
                switch (value.Trim().ToLowerInvariant())
                {
                    case "csvonly":
                        return StorageFormatLevel.CsvOnly;
                    case "binonly":
                        return StorageFormatLevel.BinOnly;
                    case "csvandbin":
                        return StorageFormatLevel.CsvAndBin;
                }
            }

            if (!string.IsNullOrWhiteSpace(value))
                warningSink?.Invoke($"{settingName} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }

        public static bool ParseBoolean(
            string value,
            bool fallback,
            string settingName,
            Action<string> warningSink = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (bool.TryParse(value.Trim(), out var parsed)) return parsed;
            if (value.Trim() == "1") return true;
            if (value.Trim() == "0") return false;
            warningSink?.Invoke($"{settingName} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }

        public static int ParseInt(
            string value,
            int fallback,
            int minimum,
            int maximum,
            string settingName,
            Action<string> warningSink = null)
        {
            if (int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum)
                return parsed;
            if (!string.IsNullOrWhiteSpace(value))
                warningSink?.Invoke($"{settingName} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }

        private static int ParseIntWithMissingWarning(
            string value,
            int fallback,
            int minimum,
            int maximum,
            string settingName,
            Action<string> warningSink)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                warningSink?.Invoke($"{settingName} 缺失，已回退 {fallback}。Value=<missing>");
                return fallback;
            }
            return ParseInt(value, fallback, minimum, maximum, settingName, warningSink);
        }

        private static TelemetryPersistencePolicy ParseTelemetryPolicy(
            NameValueCollection settings,
            Action<string> warningSink)
        {
            return new TelemetryPersistencePolicy
            {
                Mode = ParseTelemetryMode(settings?["TelemetryPersistenceMode"],
                    TelemetryPersistenceMode.Layered, warningSink),
                SteadyRateHz = ParseInt(settings?["TelemetrySteadyRateHz"], 1, 1, 10,
                    "TelemetrySteadyRateHz", warningSink),
                EventRateHz = ParseInt(settings?["TelemetryEventRateHz"], 10, 1, 100,
                    "TelemetryEventRateHz", warningSink),
                PreEventSeconds = ParseInt(settings?["TelemetryPreEventSeconds"], 60, 0, 3600,
                    "TelemetryPreEventSeconds", warningSink),
                PostEventSeconds = ParseInt(settings?["TelemetryPostEventSeconds"], 30, 0, 3600,
                    "TelemetryPostEventSeconds", warningSink),
                RetentionDays = ParseInt(settings?["TelemetryRetentionDays"], 3, 1, 3650,
                    "TelemetryRetentionDays", warningSink),
                RotateHours = ParseInt(settings?["TelemetryRotateHours"], 1, 1, 168,
                    "TelemetryRotateHours", warningSink),
                RotateBytes = ParseLong(settings?["TelemetryRotateBytes"], 100L * 1024L * 1024L,
                    1024L, long.MaxValue, "TelemetryRotateBytes", warningSink),
                RawRotateBytes = ParseLong(settings?["TelemetryRawRotateBytes"],
                    100L * 1024L * 1024L, 1024L, long.MaxValue,
                    "TelemetryRawRotateBytes", warningSink)
            };
        }

        public static TelemetryPersistenceMode ParseTelemetryMode(
            string value,
            TelemetryPersistenceMode fallback = TelemetryPersistenceMode.Layered,
            Action<string> warningSink = null)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                switch (value.Trim().ToLowerInvariant())
                {
                    case "layered": return TelemetryPersistenceMode.Layered;
                    case "raw": return TelemetryPersistenceMode.Raw;
                }
            }

            warningSink?.Invoke(
                $"TelemetryPersistenceMode 缺失或非法，已回退 {fallback}。Value={value ?? "<missing>"}");
            return fallback;
        }

        private static long ParseLong(
            string value,
            long fallback,
            long minimum,
            long maximum,
            string settingName,
            Action<string> warningSink)
        {
            if (long.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum)
                return parsed;
            if (!string.IsNullOrWhiteSpace(value))
                warningSink?.Invoke($"{settingName} 非法，已回退 {fallback}。Value={value}");
            return fallback;
        }

        public string ToStartupLogLine()
        {
            return $"StoragePolicy Latest={Latest} Alarm={Alarm} Learning={Learning} " +
                   $"LearningRetentionMode={LearningRetentionMode} " +
                   $"LearningSuccessfulRunRetainCount={LearningSuccessfulRunRetainCount} " +
                   $"LearningFailedRunRetainCount={LearningFailedRunRetainCount} " +
                   $"HistoricalEnabled={HistoricalEnabled} HistoricalRetainCyclesPerChannel={HistoricalRetainCyclesPerChannel} " +
                   $"HistoricalMinimumFreeBytes={HistoricalMinimumFreeBytes} " +
                   $"Telemetry[{Telemetry}]";
        }
    }
}
