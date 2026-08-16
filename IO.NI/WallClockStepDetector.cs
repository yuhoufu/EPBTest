using System;

namespace IO.NI
{
    public sealed class WallClockStep
    {
        public string Direction { get; set; } = string.Empty;
        public double StepMilliseconds { get; set; }
        public double WallElapsedMilliseconds { get; set; }
        public double MonotonicElapsedMilliseconds { get; set; }
    }

    /// <summary>
    /// Compares wall-clock movement with Stopwatch movement.  DateTime is
    /// diagnostic only; control and deadlines continue using monotonic time.
    /// </summary>
    public sealed class WallClockStepDetector
    {
        private readonly object _gate = new object();
        private readonly double _thresholdMilliseconds;
        private long _lastUtcTicks;
        private long _lastMonotonicTicks;

        public WallClockStepDetector(double thresholdMilliseconds = 500)
        {
            _thresholdMilliseconds = Math.Max(50, thresholdMilliseconds);
        }

        public WallClockStep Observe(DateTime utc, long monotonicTicks, long monotonicFrequency)
        {
            if (monotonicFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
            utc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            lock (_gate)
            {
                var previousUtc = _lastUtcTicks;
                var previousMonotonic = _lastMonotonicTicks;
                _lastUtcTicks = utc.Ticks;
                _lastMonotonicTicks = monotonicTicks;
                if (previousUtc == 0 || previousMonotonic == 0 || monotonicTicks < previousMonotonic)
                    return null;

                var wallMs = (utc.Ticks - previousUtc) / (double)TimeSpan.TicksPerMillisecond;
                var monotonicMs = (monotonicTicks - previousMonotonic) * 1000.0 / monotonicFrequency;
                var stepMs = wallMs - monotonicMs;
                if (Math.Abs(stepMs) < _thresholdMilliseconds) return null;
                return new WallClockStep
                {
                    Direction = stepMs >= 0 ? "Forward" : "Backward",
                    StepMilliseconds = stepMs,
                    WallElapsedMilliseconds = wallMs,
                    MonotonicElapsedMilliseconds = monotonicMs
                };
            }
        }
    }
}
