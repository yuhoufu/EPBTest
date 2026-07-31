using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IO.NI
{
    /// <summary>
    /// Converts the monotonic Stopwatch counter and sample offsets to DateTime ticks.
    /// DateTime.AddSeconds is intentionally avoided because .NET Framework rounds it
    /// to milliseconds, which destroys a 2 kHz (0.5 ms) sample interval.
    /// </summary>
    public sealed class HighResolutionSampleClock
    {
        private DateTime _wallTime;
        private long _startTimestamp;

        public DateTime WallTime => _wallTime;
        public long StartTimestamp => _startTimestamp;

        public void Reset(DateTime wallTime)
        {
            _wallTime = wallTime;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public DateTime Now()
        {
            return FromStopwatchTimestamp(_wallTime, _startTimestamp, Stopwatch.GetTimestamp());
        }

        public static DateTime FromStopwatchTimestamp(
            DateTime wallTime,
            long startTimestamp,
            long currentTimestamp)
        {
            var elapsed = currentTimestamp - startTimestamp;
            var ticks = (long)Math.Round(
                elapsed * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency),
                MidpointRounding.AwayFromZero);
            return wallTime.AddTicks(ticks);
        }

        public static long SampleOffsetTicks(int sampleOffset, double sampleRate)
        {
            if (sampleOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(sampleOffset));
            if (sampleRate <= 0 || double.IsNaN(sampleRate) || double.IsInfinity(sampleRate))
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            return (long)Math.Round(
                sampleOffset * (TimeSpan.TicksPerSecond / sampleRate),
                MidpointRounding.AwayFromZero);
        }

        public static DateTime AddSamples(DateTime origin, int sampleOffset, double sampleRate)
        {
            return origin.AddTicks(SampleOffsetTicks(sampleOffset, sampleRate));
        }

        public static DateTime[] BuildBatchTimestamps(
            DateTime batchLastSampleUtc,
            int sampleCount,
            double sampleRate)
        {
            if (sampleCount < 0)
                throw new ArgumentOutOfRangeException(nameof(sampleCount));

            var result = new DateTime[sampleCount];
            if (sampleCount == 0)
                return result;

            var first = batchLastSampleUtc.AddTicks(
                -SampleOffsetTicks(sampleCount - 1, sampleRate));
            for (var i = 0; i < sampleCount; i++)
                result[i] = first.AddTicks(SampleOffsetTicks(i, sampleRate));
            return result;
        }

        /// <summary>
        /// 计算下一批的批尾时间。主机时间可用于向前纠偏，但不得让批尾早于
        /// “上一批尾 + 本批采样时长”，否则向前回推批内时间戳会与上一批重叠。
        /// </summary>
        public static DateTime AdvanceBatchEnd(
            DateTime previousBatchEnd,
            DateTime hostNow,
            int sampleCount,
            double sampleRate,
            double correctionThresholdMs = 5.0)
        {
            var idealBatchEnd = AddSamples(
                previousBatchEnd,
                sampleCount,
                sampleRate);
            var driftMs = (hostNow - idealBatchEnd).TotalMilliseconds;
            if (Math.Abs(driftMs) <= Math.Max(0, correctionThresholdMs))
                return idealBatchEnd;

            return hostNow > idealBatchEnd
                ? hostNow
                : idealBatchEnd;
        }
    }

    /// <summary>
    /// Serializes per-device timestamp advancement with the caller's batch commit.
    /// The commit must stay inside the same gate as timestamp allocation; otherwise
    /// overlapping DAQ callbacks can allocate A/B/C in order but enqueue A/C/B.
    /// </summary>
    public sealed class DeviceBatchTimestampCoordinator
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTime> _lastTimestampByDevice =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _origin;

        public void Reset(DateTime origin, params string[] devices)
        {
            lock (_gate)
            {
                _origin = origin;
                _lastTimestampByDevice.Clear();
                if (devices == null) return;
                foreach (var device in devices)
                {
                    if (!string.IsNullOrWhiteSpace(device))
                        _lastTimestampByDevice[device] = origin;
                }
            }
        }

        public void AdvanceAndCommit(
            string device,
            DateTime hostNow,
            int sampleCount,
            double sampleRate,
            Action<DateTime, DateTime, double> commit)
        {
            if (string.IsNullOrWhiteSpace(device))
                throw new ArgumentException("device is required", nameof(device));
            if (commit == null)
                throw new ArgumentNullException(nameof(commit));

            lock (_gate)
            {
                if (!_lastTimestampByDevice.TryGetValue(device, out var previousEnd))
                    previousEnd = _origin;

                var idealEnd = HighResolutionSampleClock.AddSamples(
                    previousEnd,
                    sampleCount,
                    sampleRate);
                var driftMs = (hostNow - idealEnd).TotalMilliseconds;
                var currentEnd = HighResolutionSampleClock.AdvanceBatchEnd(
                    previousEnd,
                    hostNow,
                    sampleCount,
                    sampleRate);

                // Commit before releasing the ordering gate. This is intentionally
                // limited to a non-blocking queue enqueue at the call site.
                commit(previousEnd, currentEnd, driftMs);
                _lastTimestampByDevice[device] = currentEnd;
            }
        }
    }
}
