using System;
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
    }
}
