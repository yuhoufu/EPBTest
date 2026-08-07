using System;
using Config;

namespace Controller.Adaptive
{
    /// <summary>
    /// 只有正式圈完成机械释放并可靠落盘后，才能提交的卡钳异常证据。
    /// </summary>
    public sealed class FormalCycleFaultCommitResult
    {
        public int ForwardLowPlateauStreak { get; set; }
        public int ForwardOvershootStreak { get; set; }
        public bool ShouldLatchAlarm { get; set; }
        public string AlarmCode { get; set; } = string.Empty;
        public string AlarmReason { get; set; } = string.Empty;
    }

    internal static class FormalCycleFaultPolicy
    {
        internal static bool IsPermanentOvershoot(
            double peakCurrentA,
            double targetCurrentA,
            double deltaA)
        {
            return !double.IsNaN(peakCurrentA) &&
                   !double.IsInfinity(peakCurrentA) &&
                   !double.IsNaN(targetCurrentA) &&
                   !double.IsInfinity(targetCurrentA) &&
                   peakCurrentA >= targetCurrentA + Math.Max(0.1, deltaA);
        }

        internal static FormalCycleFaultCommitResult Commit(
            EpbAdaptiveProfile profile,
            EpbCycleOutcome outcome,
            Guid testRunId,
            int cycleNumber,
            int lowPlateauConfirmCycles,
            int overshootConfirmCycles)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            if (!outcome.IsSuccess)
                throw new InvalidOperationException("只有已完成控制动作的正式圈才能提交卡钳异常证据。");

            var lowThreshold = Math.Max(1, lowPlateauConfirmCycles);
            var overshootThreshold = Math.Max(1, overshootConfirmCycles);
            var lowStreak = profile.UpdateForwardStallStreak(
                outcome.ForwardLowPlateauCandidate);
            var overshootStreak = profile.UpdateForwardPermanentOvershootStreak(
                outcome.ForwardPermanentOvershootCandidate);

            var result = new FormalCycleFaultCommitResult
            {
                ForwardLowPlateauStreak = lowStreak,
                ForwardOvershootStreak = overshootStreak
            };

            if (outcome.ForwardLowPlateauCandidate && lowStreak >= lowThreshold)
            {
                result.ShouldLatchAlarm = true;
                result.AlarmCode = "ForwardLowPlateauConfirmed";
                result.AlarmReason =
                    $"{result.AlarmCode} RunId={testRunId:N} Cycle={cycleNumber} " +
                    $"Peak={outcome.PeakCurrentA:F3}A Floor={outcome.ForwardAcceptableFloorA:F3}A " +
                    $"Target={outcome.TargetCurrentA:F3}A slope={outcome.EstimatedSlopeAperMs:F6}A/ms " +
                    $"Evidence=FullRateValid Streak={lowStreak}/{lowThreshold}";
                return result;
            }

            if (outcome.ForwardPermanentOvershootCandidate && overshootStreak >= overshootThreshold)
            {
                result.ShouldLatchAlarm = true;
                result.AlarmCode = "ForwardPeakOvershoot2AConfirmed";
                result.AlarmReason =
                    $"{result.AlarmCode} RunId={testRunId:N} Cycle={cycleNumber} " +
                    $"Peak={outcome.PeakCurrentA:F3}A Target={outcome.TargetCurrentA:F3}A " +
                    $"Error={outcome.PeakErrorA:+0.000;-0.000;0.000}A " +
                    $"Limit=+{outcome.PermanentOvershootDeltaA:F3}A " +
                    $"Evidence=FullRateValid Streak={overshootStreak}/{overshootThreshold}";
            }

            return result;
        }
    }
}
