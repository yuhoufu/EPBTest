using System;
using System.Collections.Generic;
using System.Linq;

namespace MTTFTest.SafetyHardware
{
    /// <summary>
    /// A process-local monotonic evidence window. Call only after the required
    /// output commands have been acknowledged. A new command/attempt requires
    /// a new window; this object is not a durable recovery authorization.
    /// </summary>
    public sealed class SafetyFeedbackWindow
    {
        private readonly string[] _channels;
        private readonly double[] _minimum;
        private readonly double[] _maximum;
        private readonly double _commandCompletedMs;
        private readonly double _maximumAgeMs;
        private readonly double _stableMs;
        private readonly Dictionary<string, double> _lastSamples = new Dictionary<string, double>(StringComparer.Ordinal);
        private double _stableSinceMs = double.NaN;
        private double _lastNowMs;

        public SafetyFeedbackWindow(string[] channels, double[] minimum, double[] maximum,
            double commandCompletedMs, double maximumAgeMs, double stableMs)
        {
            if (channels == null || channels.Length == 0 || channels.Any(string.IsNullOrWhiteSpace) ||
                channels.Distinct(StringComparer.Ordinal).Count() != channels.Length ||
                minimum == null || maximum == null || minimum.Length != channels.Length || maximum.Length != channels.Length ||
                !Finite(commandCompletedMs) || commandCompletedMs < 0 || !Finite(maximumAgeMs) || maximumAgeMs <= 0 ||
                !Finite(stableMs) || stableMs < 0)
                throw new ArgumentException("Safety feedback window configuration invalid.");
            for (var index = 0; index < channels.Length; index++)
                if (!Finite(minimum[index]) || !Finite(maximum[index]) || minimum[index] > maximum[index])
                    throw new ArgumentException("Safety feedback bounds invalid.");
            _channels = (string[])channels.Clone();
            _minimum = (double[])minimum.Clone();
            _maximum = (double[])maximum.Clone();
            _commandCompletedMs = commandCompletedMs;
            _maximumAgeMs = maximumAgeMs;
            _stableMs = stableMs;
            _lastNowMs = commandCompletedMs;
        }

        public bool Observe(IReadOnlyDictionary<string, SafetyFeedbackSample> samples, double nowMs)
        {
            if (!Finite(nowMs) || nowMs < _lastNowMs)
            {
                _stableSinceMs = double.NaN;
                return false;
            }
            var gap = nowMs - _lastNowMs;
            _lastNowMs = nowMs;
            if (gap > _maximumAgeMs) _stableSinceMs = double.NaN;
            var valid = samples != null && samples.Count == _channels.Length;
            var oldest = nowMs;
            for (var index = 0; index < _channels.Length; index++)
            {
                SafetyFeedbackSample sample;
                if (samples == null || !samples.TryGetValue(_channels[index], out sample) || sample == null)
                {
                    valid = false;
                    continue;
                }
                double previous;
                var fresh = Finite(sample.AcquiredMs) && sample.AcquiredMs > _commandCompletedMs &&
                    sample.AcquiredMs <= nowMs && nowMs - sample.AcquiredMs <= _maximumAgeMs &&
                    (!_lastSamples.TryGetValue(_channels[index], out previous) || sample.AcquiredMs > previous);
                // Never let a replay or a backwards timestamp lower the high-water mark.
                if (Finite(sample.AcquiredMs) && sample.AcquiredMs <= nowMs &&
                    (!_lastSamples.TryGetValue(_channels[index], out previous) || sample.AcquiredMs > previous))
                    _lastSamples[_channels[index]] = sample.AcquiredMs;
                valid &= fresh && Finite(sample.Value) && sample.Value >= _minimum[index] && sample.Value <= _maximum[index];
                oldest = Math.Min(oldest, sample.AcquiredMs);
            }
            if (!valid)
            {
                _stableSinceMs = double.NaN;
                return false;
            }
            // Every channel must contribute new safe feedback across the same
            // observed interval. Sleeping after the last sample proves nothing.
            if (double.IsNaN(_stableSinceMs)) _stableSinceMs = nowMs;
            return oldest - _stableSinceMs >= _stableMs;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class SafetyFeedbackSample
    {
        public SafetyFeedbackSample(double value, double acquiredMs)
        {
            Value = value;
            AcquiredMs = acquiredMs;
        }
        public double Value { get; }
        public double AcquiredMs { get; }
    }
}
