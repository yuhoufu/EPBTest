using System;
using IO.NI;

namespace Controller.Adaptive
{
    public enum FastPathTripClassification
    {
        NotApplicable = 0,
        ConfirmedOverCurrent = 1,
        FastPathSignalInvalid = 2,
        FastPathEvidenceUnavailable = 3
    }

    public readonly struct FastPathTripResult
    {
        public FastPathTripResult(FastPathTripClassification classification, string reason)
        {
            Classification = classification;
            Reason = reason ?? string.Empty;
        }

        public FastPathTripClassification Classification { get; }
        public string Reason { get; }
        public string Code => Classification.ToString();
    }

    public static class FastPathTripClassifier
    {
        private const FastSignalQualityFlags InvalidControlQuality =
            FastSignalQualityFlags.NonFinite |
            FastSignalQualityFlags.ProducerReentry |
            FastSignalQualityFlags.SequenceDiscontinuity |
            FastSignalQualityFlags.GenerationMismatch |
            FastSignalQualityFlags.DuplicateBatch |
            FastSignalQualityFlags.OutOfOrderBatch |
            FastSignalQualityFlags.MonotonicTickInvalid |
            FastSignalQualityFlags.FilterStateInvalid;

        public static bool HasInvalidControlQuality(FastSignalQualityFlags qualityFlags)
        {
            return (qualityFlags & InvalidControlQuality) != 0;
        }

        public static bool IsFastSignalDependentFault(string reason)
        {
            return reason?.IndexOf("OverCurrent3Samples", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason?.IndexOf("AbnormalLoadRiseDrop", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static FastPathTripResult ClassifyOverCurrent(
            double fastPeakA,
            double overCurrentLimitA,
            double fullRatePeakA,
            double evidenceAgeMs,
            double mismatchToleranceA,
            double maximumEvidenceAgeMs,
            FastSignalQualityFlags qualityFlags)
        {
            if (HasInvalidControlQuality(qualityFlags))
                return new FastPathTripResult(
                    FastPathTripClassification.FastPathSignalInvalid,
                    $"Quality={qualityFlags}");

            if (double.IsNaN(fullRatePeakA) || double.IsInfinity(fullRatePeakA) || fullRatePeakA < 0 ||
                double.IsNaN(evidenceAgeMs) || double.IsInfinity(evidenceAgeMs) ||
                evidenceAgeMs < 0 || evidenceAgeMs > Math.Max(1, maximumEvidenceAgeMs))
                return new FastPathTripResult(
                    FastPathTripClassification.FastPathEvidenceUnavailable,
                    $"FullRatePeak={fullRatePeakA:F3}A EvidenceAge={evidenceAgeMs:F1}ms");

            var limit = Math.Max(0.01, overCurrentLimitA);
            if (fullRatePeakA >= limit)
                return new FastPathTripResult(
                    FastPathTripClassification.ConfirmedOverCurrent,
                    $"FastPeak={fastPeakA:F3}A FullRatePeak={fullRatePeakA:F3}A Limit={limit:F3}A");

            var tolerance = Math.Max(0.01, mismatchToleranceA);
            if (fastPeakA - fullRatePeakA > tolerance ||
                (qualityFlags & FastSignalQualityFlags.AdcNearRail) != 0)
                return new FastPathTripResult(
                    FastPathTripClassification.FastPathSignalInvalid,
                    $"FastPeak={fastPeakA:F3}A FullRatePeak={fullRatePeakA:F3}A " +
                    $"Tolerance={tolerance:F3}A Quality={qualityFlags}");

            return new FastPathTripResult(
                FastPathTripClassification.FastPathEvidenceUnavailable,
                $"FullRatePeakBelowLimitButWithinTolerance FastPeak={fastPeakA:F3}A " +
                $"FullRatePeak={fullRatePeakA:F3}A Limit={limit:F3}A");
        }

        public static FastPathTripResult ClassifySignalDependentFault(
            string originalReason,
            FastSignalQualityFlags qualityFlags)
        {
            if (HasInvalidControlQuality(qualityFlags))
                return new FastPathTripResult(
                    FastPathTripClassification.FastPathSignalInvalid,
                    $"Original={originalReason} Quality={qualityFlags}");
            return new FastPathTripResult(FastPathTripClassification.NotApplicable, originalReason);
        }
    }
}
