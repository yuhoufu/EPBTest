using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>Bounded display-only envelope. Acquisition never waits for a UI reader.</summary>
    public sealed class EngineUiCurveBuffer
    {
        private sealed class Bucket
        {
            internal long Number, MinTicks, MaxTicks;
            internal double Min, Max;
            internal bool BreakBefore;
        }
        private sealed class Series
        {
            internal readonly Queue<Bucket> Buckets = new Queue<Bucket>();
            internal Bucket Current;
            internal long LastTicks;
            internal bool BreakPending;
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Series> _series = new Dictionary<string, Series>();
        private readonly long _windowTicks;
        private readonly long _bucketTicks;
        public int WindowSeconds { get; }

        public EngineUiCurveBuffer(int windowSeconds = 60)
        {
            if (windowSeconds < 1 || windowSeconds > 3600) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            WindowSeconds = windowSeconds;
            _windowTicks = TimeSpan.FromSeconds(windowSeconds).Ticks;
            _bucketTicks = Math.Max(TimeSpan.FromMilliseconds(50).Ticks,
                _windowTicks / (EngineUiContract.MaximumCurvePoints / 2 - 2) + 1);
        }

        public bool TryAppend(string key, double[,] values, int row, long endUtcTicks, double sampleRateHz)
        {
            if (string.IsNullOrWhiteSpace(key) || values == null || row < 0 || row >= values.GetLength(0) ||
                !EngineUiContract.IsUtcTicks(endUtcTicks) || !EngineUiContract.IsFinite(sampleRateHz) || sampleRateHz <= 0) return false;
            if (!Monitor.TryEnter(_gate)) return false;
            try
            {
                var count = values.GetLength(1);
                if (count == 0) return false;
                if (!_series.TryGetValue(key, out var series))
                {
                    if (_series.Count >= 32) return false;
                    _series.Add(key, series = new Series());
                }
                var step = TimeSpan.TicksPerSecond / sampleRateHz;
                for (var index = 0; index < count; index++)
                {
                    var ticks = endUtcTicks - (long)((count - 1 - index) * step);
                    if (ticks <= series.LastTicks || ticks <= 0) continue;
                    if (series.LastTicks > 0 && ticks - series.LastTicks > Math.Max(TimeSpan.TicksPerSecond / 4, step * 3))
                        series.BreakPending = true;
                    series.LastTicks = ticks;
                    var value = values[row, index];
                    if (!EngineUiContract.IsFinite(value)) { series.BreakPending = true; continue; }
                    var number = ticks / _bucketTicks;
                    var bucket = series.Current;
                    if (bucket == null || bucket.Number != number || series.BreakPending)
                    {
                        bucket = new Bucket { Number = number, MinTicks = ticks, MaxTicks = ticks, Min = value, Max = value,
                            BreakBefore = series.BreakPending };
                        series.Buckets.Enqueue(bucket);
                        series.Current = bucket;
                        series.BreakPending = false;
                    }
                    else
                    {
                        if (value < bucket.Min) { bucket.Min = value; bucket.MinTicks = ticks; }
                        if (value > bucket.Max) { bucket.Max = value; bucket.MaxTicks = ticks; }
                    }
                }
                while (series.Buckets.Count > EngineUiContract.MaximumCurvePoints / 2 ||
                    series.Buckets.Count > 0 && Math.Max(series.Buckets.Peek().MinTicks, series.Buckets.Peek().MaxTicks) < endUtcTicks - _windowTicks)
                    series.Buckets.Dequeue();
                if (series.Buckets.Count == 0) series.Current = null;
                return true;
            }
            finally { Monitor.Exit(_gate); }
        }

        public EngineUiCurve[] Snapshot()
        {
            lock (_gate) return _series.Select(pair =>
            {
                var ticks = new List<long>(); var values = new List<double>(); var breaks = new List<bool>();
                foreach (var bucket in pair.Value.Buckets)
                {
                    var minFirst = bucket.MinTicks <= bucket.MaxTicks;
                    ticks.Add(minFirst ? bucket.MinTicks : bucket.MaxTicks);
                    values.Add(minFirst ? bucket.Min : bucket.Max);
                    breaks.Add(bucket.BreakBefore);
                    if (bucket.MinTicks != bucket.MaxTicks)
                    {
                        ticks.Add(minFirst ? bucket.MaxTicks : bucket.MinTicks);
                        values.Add(minFirst ? bucket.Max : bucket.Min);
                        breaks.Add(false);
                    }
                }
                return new EngineUiCurve { Key = pair.Key, UtcTicks = ticks.ToArray(), Values = values.ToArray(), BreakBefore = breaks.ToArray() };
            }).ToArray();
        }
    }
}
