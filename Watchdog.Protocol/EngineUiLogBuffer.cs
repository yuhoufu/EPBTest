using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class EngineUiLogQuery
    {
        public long BeforeSequence { get; set; }
        public int PageSize { get; set; } = 100;
        public string Level { get; set; } = string.Empty;
        public bool IsStructurallyValid() => BeforeSequence >= 0 && PageSize > 0 && PageSize <= EngineUiContract.MaximumLogs &&
            (Level == "" || Level == "INFO" || Level == "WARN" || Level == "ERROR");
    }

    public sealed class EngineUiLogPage
    {
        public string Level { get; set; } = string.Empty;
        public long BeforeSequence { get; set; }
        public long NextBeforeSequence { get; set; }
        public long OldestRetainedSequence { get; set; }
        public long NewestSequence { get; set; }
        public long DroppedEntries { get; set; }
        public bool RetentionTruncated { get; set; }
        public bool HasEarlier { get; set; }
        public EngineUiLogEntry[] Entries { get; set; } = Array.Empty<EngineUiLogEntry>();

        public bool IsStructurallyValid() => new EngineUiLogQuery { BeforeSequence = BeforeSequence, Level = Level }.IsStructurallyValid() &&
            NextBeforeSequence >= 0 && OldestRetainedSequence >= 0 && NewestSequence >= OldestRetainedSequence && DroppedEntries >= 0 &&
            Entries != null && Entries.Length <= EngineUiContract.MaximumLogs && Entries.All(e => e != null &&
                e.Sequence > 0 && e.Sequence >= OldestRetainedSequence && e.Sequence <= NewestSequence &&
                (BeforeSequence == 0 || e.Sequence < BeforeSequence) && (Level == "" || e.Level == Level) &&
                EngineUiContract.IsUtcTicks(e.CapturedUtcTicks) && e.Message?.Length <= 2048) &&
            Entries.Zip(Entries.Skip(1), (a, b) => a.Sequence < b.Sequence).All(v => v) &&
            (HasEarlier ? Entries.Length > 0 && NextBeforeSequence == Entries[0].Sequence : NextBeforeSequence == 0);
    }

    public sealed class EngineUiLogBuffer
    {
        public const int RetainedEntries = 2048;
        private readonly object _gate = new object();
        private readonly Queue<EngineUiLogEntry> _entries = new Queue<EngineUiLogEntry>();
        private long _sequence;
        private long _dropped;

        public void TryAppend(string level, string category, string message, long utcTicks)
        {
            // UI pagination never blocks a control-thread logger. Loss is explicit.
            if (!Monitor.TryEnter(_gate)) { Interlocked.Increment(ref _dropped); return; }
            try
            {
                var text = message ?? string.Empty;
                _entries.Enqueue(new EngineUiLogEntry { Sequence = ++_sequence, CapturedUtcTicks = utcTicks,
                    Level = level, Category = category ?? string.Empty, Message = text.Length <= 2048 ? text : text.Substring(0, 2048) });
                while (_entries.Count > RetainedEntries) _entries.Dequeue();
            }
            finally { Monitor.Exit(_gate); }
        }

        public EngineUiLogPage Read(EngineUiLogQuery query)
        {
            if (query?.IsStructurallyValid() != true) throw new ArgumentException("UiLogQueryInvalid", nameof(query));
            EngineUiLogEntry[] entries;
            long newest;
            lock (_gate) { entries = _entries.ToArray(); newest = _sequence; }
            // Filtering/formatting stays outside the shared gate and off the producer thread.
            var matching = entries.Where(e => (query.BeforeSequence == 0 || e.Sequence < query.BeforeSequence) &&
                (query.Level == "" || e.Level == query.Level)).ToArray();
            var selected = matching.Skip(Math.Max(0, matching.Length - query.PageSize)).Select(e => new EngineUiLogEntry
                { Sequence = e.Sequence, CapturedUtcTicks = e.CapturedUtcTicks, Level = e.Level, Category = e.Category, Message = e.Message }).ToArray();
            return new EngineUiLogPage { Level = query.Level, BeforeSequence = query.BeforeSequence,
                NextBeforeSequence = matching.Length > selected.Length ? selected[0].Sequence : 0,
                HasEarlier = matching.Length > selected.Length, Entries = selected,
                OldestRetainedSequence = entries.FirstOrDefault()?.Sequence ?? 0, NewestSequence = newest,
                DroppedEntries = Interlocked.Read(ref _dropped), RetentionTruncated = entries.Length > 0 && entries[0].Sequence > 1 };
        }
    }
}
