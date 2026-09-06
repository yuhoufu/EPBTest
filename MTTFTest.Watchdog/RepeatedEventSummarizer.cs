using System;
using System.Collections.Generic;

namespace MTTFTest.Watchdog
{
    internal readonly struct RepeatedEventDecision
    {
        internal RepeatedEventDecision(bool emitFirst, bool emitSummary, int suppressedCount)
        {
            EmitFirst = emitFirst;
            EmitSummary = emitSummary;
            SuppressedCount = suppressedCount;
        }

        internal bool EmitFirst { get; }
        internal bool EmitSummary { get; }
        internal int SuppressedCount { get; }
    }

    /// <summary>
    /// Uses an injected monotonic timestamp domain to turn a hot repeated observation into one
    /// first event plus bounded periodic summaries. It deliberately owns no journal state.
    /// </summary>
    internal sealed class RepeatedEventSummarizer
    {
        private sealed class Entry
        {
            internal long LastEmissionTimestamp;
            internal long LastObservedTimestamp;
            internal int SuppressedCount;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly long _summaryIntervalTicks;
        private readonly int _maximumKeys;

        internal RepeatedEventSummarizer(long summaryIntervalTicks, int maximumKeys = 128)
        {
            if (summaryIntervalTicks <= 0)
                throw new ArgumentOutOfRangeException(nameof(summaryIntervalTicks));
            _summaryIntervalTicks = summaryIntervalTicks;
            _maximumKeys = Math.Max(8, maximumKeys);
        }

        internal RepeatedEventDecision Observe(string key, long monotonicTimestamp)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Repeated event key is required.", nameof(key));
            if (monotonicTimestamp < 0)
                throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));

            lock (_gate)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry))
                {
                    TrimOldestIfNeeded();
                    _entries[key] = new Entry
                    {
                        LastEmissionTimestamp = monotonicTimestamp,
                        LastObservedTimestamp = monotonicTimestamp
                    };
                    return new RepeatedEventDecision(true, false, 0);
                }

                entry.LastObservedTimestamp = monotonicTimestamp;
                entry.SuppressedCount++;
                if (monotonicTimestamp - entry.LastEmissionTimestamp < _summaryIntervalTicks)
                    return new RepeatedEventDecision(false, false, 0);

                var suppressed = entry.SuppressedCount;
                entry.SuppressedCount = 0;
                entry.LastEmissionTimestamp = monotonicTimestamp;
                return new RepeatedEventDecision(false, true, suppressed);
            }
        }

        private void TrimOldestIfNeeded()
        {
            if (_entries.Count < _maximumKeys) return;
            string oldestKey = null;
            var oldestTimestamp = long.MaxValue;
            foreach (var pair in _entries)
            {
                if (pair.Value.LastObservedTimestamp >= oldestTimestamp) continue;
                oldestTimestamp = pair.Value.LastObservedTimestamp;
                oldestKey = pair.Key;
            }
            if (oldestKey != null) _entries.Remove(oldestKey);
        }
    }
}
