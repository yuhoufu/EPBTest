using System;
using System.Collections.Generic;
using System.Linq;

namespace Config
{
    public sealed class EpbSelectionState
    {
        public EpbSelectionState(
            bool settingsEnabled,
            bool powerSelected,
            bool curveSelected)
        {
            SettingsEnabled = settingsEnabled;
            PowerSelected = powerSelected;
            CurveSelected = curveSelected;
        }

        public bool SettingsEnabled { get; }
        public bool PowerSelected { get; }
        public bool CurveSelected { get; }
    }

    /// <summary>
    /// Pure project-initialization and monitor-selection rules shared by the UI and tests.
    /// </summary>
    public static class EpbProjectPolicies
    {
        public static EpbSelectionState ApplySettingsSelection(bool enabled)
        {
            return new EpbSelectionState(enabled, enabled, enabled);
        }

        public static EpbSelectionState ApplyPowerSelection(
            EpbSelectionState current,
            bool selected)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            return new EpbSelectionState(
                current.SettingsEnabled,
                selected,
                selected);
        }

        public static EpbSelectionState ApplyCurveSelection(
            EpbSelectionState current,
            bool selected)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            return new EpbSelectionState(
                current.SettingsEnabled,
                current.PowerSelected,
                selected);
        }

        public static void InitializeNewProject(
            TestConfig config,
            string storeDir,
            string testName)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            config.StoreDir = storeDir;
            config.TestName = testName;
            config.EnsureEpbRecords(12);

            foreach (var record in config.EpbRecords)
            {
                var total = Math.Max(0, record.TotalCount);
                record.Enabled = false;
                record.ResetKeepTotalCount();
                record.StartTime = null;
                record.LatestStartTime = null;
                record.TotalCount = total;
            }
        }

        public static int FindInitialSummaryChannel(IEnumerable<EpbTestRecord> records)
        {
            var ordered = Normalize(records);
            var started = ordered.FirstOrDefault(IsStarted);
            if (started != null) return started.Id;

            var enabled = ordered.FirstOrDefault(record => record.Enabled);
            return enabled?.Id ?? ordered.FirstOrDefault()?.Id ?? 0;
        }

        public static int FindSummaryChannelAfterCompletion(
            IEnumerable<EpbTestRecord> records,
            int currentChannel)
        {
            var ordered = Normalize(records);
            var current = ordered.FirstOrDefault(record => record.Id == currentChannel);
            if (current == null || !IsComplete(current))
                return currentChannel;

            var next = ordered.FirstOrDefault(record =>
                record.Id != currentChannel &&
                record.Enabled &&
                IsStarted(record) &&
                !IsComplete(record));

            return next?.Id ?? currentChannel;
        }

        public static bool IsStarted(EpbTestRecord record)
        {
            return record != null &&
                   (record.RunCount > 0 ||
                    record.Status != EpbTestStatus.NotStarted);
        }

        private static bool IsComplete(EpbTestRecord record)
        {
            var total = Math.Max(0, record.TotalCount);
            return record.Status == EpbTestStatus.Completed ||
                   (total > 0 && record.RunCount >= total);
        }

        private static List<EpbTestRecord> Normalize(IEnumerable<EpbTestRecord> records)
        {
            return (records ?? Enumerable.Empty<EpbTestRecord>())
                .Where(record => record != null && record.Id >= 1 && record.Id <= 12)
                .OrderBy(record => record.Id)
                .ToList();
        }
    }
}
