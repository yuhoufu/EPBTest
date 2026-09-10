using System;
using System.Diagnostics;

namespace IO.NI
{
    /// <summary>Unfiltered whole-batch extrema with the actual DAQ timeline,
    /// not the time a polling caller happened to read a cached value.</summary>
    public sealed class PhysicalSafetyBatchSample
    {
        public string Device { get; private set; }
        public long Generation { get; private set; }
        public long Sequence { get; private set; }
        public double FirstSampleMs { get; private set; }
        public DateTime FirstSampleUtc { get; private set; }
        public double LastSampleMs { get; private set; }
        public double MaximumAbsolute { get; private set; }
        public double Maximum { get; private set; }
        public bool Valid { get; private set; }

        internal static PhysicalSafetyBatchSample Newer(PhysicalSafetyBatchSample previous,
            PhysicalSafetyBatchSample candidate) =>
            previous == null || candidate.Generation > previous.Generation ||
            (candidate.Generation == previous.Generation && candidate.Sequence > previous.Sequence)
                ? candidate : previous;

        internal static PhysicalSafetyBatchSample Capture(string device, long generation, long sequence,
            long lastSampleTicks, double rateHz, double[,] raw, int row,
            double scale, double offset, double zeroDrift,
            double minimumVolts, double maximumVolts, FastSignalQualityFlags quality, DateTime lastSampleUtc)
        {
            var result = new PhysicalSafetyBatchSample
            {
                Device = device, Generation = generation, Sequence = sequence,
                LastSampleMs = lastSampleTicks * 1000.0 / Stopwatch.Frequency,
                Maximum = double.NegativeInfinity, MaximumAbsolute = double.NaN
            };
            if (raw == null || row < 0 || row >= raw.GetLength(0) || raw.GetLength(1) == 0 ||
                generation <= 0 || sequence <= 0 || lastSampleTicks <= 0 || !Finite(rateHz) || rateHz <= 0 ||
                !Finite(scale) || scale == 0 || !Finite(offset) || !Finite(zeroDrift) ||
                !Finite(minimumVolts) || !Finite(maximumVolts) || minimumVolts >= maximumVolts ||
                quality != FastSignalQualityFlags.None || lastSampleUtc.Kind != DateTimeKind.Utc) return result;
            var durationMs = (raw.GetLength(1) - 1) * 1000.0 / rateHz;
            result.FirstSampleMs = result.LastSampleMs - durationMs;
            if (result.FirstSampleMs <= 0) return result;
            if (lastSampleUtc.Ticks <= durationMs * TimeSpan.TicksPerMillisecond) return result;
            result.FirstSampleUtc = lastSampleUtc.AddTicks(-(long)Math.Ceiling(durationMs * TimeSpan.TicksPerMillisecond));
            var maxAbs = 0.0;
            for (var column = 0; column < raw.GetLength(1); column++)
            {
                var voltage = raw[row, column];
                // A saturated input cannot establish safe physical feedback,
                // even if an offset happens to map the rail to zero.
                if (!Finite(voltage) || voltage <= minimumVolts || voltage >= maximumVolts) return result;
                // Safety evidence uses persisted calibration, exactly as the
                // independent SafetyAgent does. UI tare is not a calibration:
                // subtracting it could hide real current or residual pressure.
                var value = (voltage - zeroDrift) * scale + offset;
                if (!Finite(value)) return result;
                maxAbs = Math.Max(maxAbs, Math.Abs(value));
                result.Maximum = Math.Max(result.Maximum, value);
            }
            result.MaximumAbsolute = maxAbs;
            result.Valid = true;
            return result;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
