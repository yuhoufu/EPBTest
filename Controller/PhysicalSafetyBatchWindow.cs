using System;
using System.Collections.Generic;
using System.Linq;
using IO.NI;
using MTTFTest.SafetyHardware;

namespace Controller
{
    internal sealed class PhysicalSafetyBatchWindow
    {
        private readonly string[] _names;
        private readonly int _currentCount;
        private readonly double[] _limits;
        private readonly SafetyFeedbackWindow _window;
        private readonly Dictionary<string, PhysicalSafetyBatchSample> _previous = new();

        internal PhysicalSafetyBatchWindow(string[] names, int currentCount, double[] limits,
            double commandCompletedMs, double maxAgeMs, double stableMs)
        {
            if (names == null || currentCount < 1 || currentCount > names.Length)
                throw new ArgumentException("PhysicalSafetyScopeInvalid");
            _names = (string[])names.Clone();
            _currentCount = currentCount;
            _window = new SafetyFeedbackWindow(names,
                names.Select((name, index) => index < currentCount ? 0.0 : -double.MaxValue).ToArray(),
                limits, commandCompletedMs, maxAgeMs, stableMs);
            _limits = (double[])limits.Clone();
        }

        internal bool Observe(IReadOnlyDictionary<string, PhysicalSafetyBatchSample> samples, double nowMs)
        {
            var frame = new Dictionary<string, SafetyFeedbackSample>(StringComparer.Ordinal);
            var repeated = false;
            var newGeneration = false;
            for (var index = 0; index < _names.Length; index++)
            {
                if (samples == null || !samples.TryGetValue(_names[index], out var sample) ||
                    sample?.Valid != true || sample.LastSampleMs > nowMs)
                    return _window.Observe(null, nowMs);
                var value = index < _currentCount ? sample.MaximumAbsolute : sample.Maximum;
                if (double.IsNaN(value) || double.IsInfinity(value) || value > _limits[index])
                    return _window.Observe(null, nowMs);
                if (_previous.TryGetValue(_names[index], out var previous))
                {
                    newGeneration |= previous.Generation != sample.Generation;
                    if (previous.Generation == sample.Generation)
                    {
                        if (sample.Sequence < previous.Sequence)
                            return _window.Observe(null, nowMs);
                        repeated |= sample.Sequence == previous.Sequence;
                    }
                }
                frame.Add(_names[index], new SafetyFeedbackSample(value, sample.FirstSampleMs));
            }
            if (newGeneration)
            {
                _window.Observe(null, nowMs);
                _previous.Clear();
            }
            // Polling faster than DAQ must neither manufacture a new sample nor
            // restart a valid hold on every cached read. No cached read succeeds.
            if (repeated && !newGeneration) return false;
            foreach (var name in _names) _previous[name] = samples[name];
            return _window.Observe(frame, nowMs);
        }
    }
}
