using System;

namespace Controller
{
    /// <summary>
    /// Decides whether a real-time UI curve should contain a pen break.
    /// Display publications may be rate-limited or replaced while the UI thread is busy, so the
    /// distance from the last rendered point is not evidence of an acquisition discontinuity.
    /// </summary>
    public static class UiCurveContinuityPolicy
    {
        private const double MinimumToleranceSeconds = 0.050;

        public static bool ShouldBreakLine(
            DateTime currentBatchEndUtc,
            DateTime previousAcquisitionBatchEndUtc,
            int sampleCount,
            double sampleIntervalSeconds)
        {
            if (currentBatchEndUtc == DateTime.MinValue ||
                previousAcquisitionBatchEndUtc == DateTime.MinValue ||
                sampleCount <= 0 ||
                sampleIntervalSeconds <= 0 ||
                double.IsNaN(sampleIntervalSeconds) ||
                double.IsInfinity(sampleIntervalSeconds))
                return false;

            var elapsedSeconds = (currentBatchEndUtc - previousAcquisitionBatchEndUtc).TotalSeconds;
            var expectedSeconds = sampleCount * sampleIntervalSeconds;
            var toleranceSeconds = Math.Max(MinimumToleranceSeconds, expectedSeconds * 2.0);

            return elapsedSeconds < -toleranceSeconds ||
                   elapsedSeconds > expectedSeconds + toleranceSeconds;
        }
    }
}
