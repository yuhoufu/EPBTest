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

    /// <summary>Resolved EXE configuration used by all cycle-export call sites.</summary>
    public sealed class ProgramStoragePolicy
    {
        public StorageFormatLevel Latest { get; set; } = StorageFormatLevel.CsvOnly;
        public StorageFormatLevel Alarm { get; set; } = StorageFormatLevel.CsvOnly;
        public StorageFormatLevel Learning { get; set; } = StorageFormatLevel.BinOnly;
        public bool HistoricalEnabled { get; set; } = true;
        public int HistoricalRetainCyclesPerChannel { get; set; } = 12;

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
                HistoricalEnabled = ParseBoolean(settings?["HistoricalEnabled"], true,
                    "HistoricalEnabled", warningSink),
                HistoricalRetainCyclesPerChannel = ParseInt(settings?["HistoricalRetainCyclesPerChannel"],
                    12, 1, 100000, "HistoricalRetainCyclesPerChannel", warningSink)
            };
            return policy;
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

        public string ToStartupLogLine()
        {
            return $"StoragePolicy Latest={Latest} Alarm={Alarm} Learning={Learning} " +
                   $"HistoricalEnabled={HistoricalEnabled} HistoricalRetainCyclesPerChannel={HistoricalRetainCyclesPerChannel}";
        }
    }
}
