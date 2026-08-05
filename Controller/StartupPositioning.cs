using System;
using System.Collections.Generic;

namespace Controller
{
    /// <summary>
    /// 启动定位正向阶段的两级时限。进展期限用于判断是否仍处于可信空行程，
    /// 绝对上电上限用于最终硬保护；两者禁止再共用同一个配置值。
    /// </summary>
    internal sealed class StartupForwardTimingBudget
    {
        public int ProgramProgressDeadlineMs { get; set; }
        public int ProjectForwardLimitMs { get; set; }
        public int LearnedProgressDeadlineMs { get; set; }
        public int EffectiveProgressDeadlineMs { get; set; }
        public int AbsoluteOnTimeMs { get; set; }
    }

    /// <summary>
    /// 启动定位反向阶段的两级时限。程序级进展期限用于识别稳定高负载停滞，
    /// 绝对上电上限继续由预释放检测时限提供；正式循环历史时序只作审计。
    /// </summary>
    internal sealed class StartupReverseTimingBudget
    {
        public int ProgramProgressDeadlineMs { get; set; }
        public int LearnedProgressDeadlineMs { get; set; }
        public int EffectiveProgressDeadlineMs { get; set; }
        public int AbsoluteOnTimeMs { get; set; }
        public double ReleaseThresholdA { get; set; }
    }

    internal enum StartupCurrentClassification
    {
        None,
        HighCurrentConfirmed,
        OverCurrent
    }

    internal sealed class StartupPositioningCurrentClassifier
    {
        private readonly int _ignoreMs;
        private readonly double _highFloorA;
        private readonly double _overCurrentLimitA;
        private readonly int _requiredSamples;
        private int _highCount;
        private int _overCurrentCount;
        private long _lastBatchSequence;
        private double _peakCurrentA;

        public StartupPositioningCurrentClassifier(
            int ignoreMs,
            double highFloorA,
            double overCurrentLimitA,
            int requiredSamples)
        {
            _ignoreMs = Math.Max(0, ignoreMs);
            _highFloorA = Math.Max(0.01, highFloorA);
            _overCurrentLimitA = overCurrentLimitA;
            _requiredSamples = Math.Max(1, requiredSamples);
        }

        public StartupCurrentClassification Evaluate(int elapsedMs, double currentA)
        {
            return Evaluate(0, elapsedMs, currentA);
        }

        public StartupCurrentClassification Evaluate(long batchSequence, int elapsedMs, double currentA)
        {
            if (batchSequence > 0 && batchSequence == _lastBatchSequence)
                return StartupCurrentClassification.None;
            if (batchSequence > 0) _lastBatchSequence = batchSequence;
            if (elapsedMs < _ignoreMs || double.IsNaN(currentA) || double.IsInfinity(currentA))
            {
                _highCount = 0;
                _overCurrentCount = 0;
                return StartupCurrentClassification.None;
            }

            var magnitude = Math.Abs(currentA);
            if (magnitude > _peakCurrentA) _peakCurrentA = magnitude;
            _overCurrentCount = magnitude >= _overCurrentLimitA ? _overCurrentCount + 1 : 0;
            _highCount = magnitude >= _highFloorA ? _highCount + 1 : 0;
            if (_overCurrentCount >= _requiredSamples) return StartupCurrentClassification.OverCurrent;
            if (_highCount >= _requiredSamples) return StartupCurrentClassification.HighCurrentConfirmed;
            return StartupCurrentClassification.None;
        }

        public bool HasHighCurrentCandidate => _highCount > 0;
        public double PeakCurrentA => _peakCurrentA;
    }

    internal enum StartupPositioningStage
    {
        None,
        ForwardPositioning,
        ForwardOffVerification,
        ReverseRelease,
        Completed
    }

    internal enum StartupPositioningCompletionKind
    {
        None,
        ReverseEmptyTravel,
        ReverseMechanicalEndpoint
    }

    internal sealed class StartupPositioningSample
    {
        public DateTime Utc { get; set; }
        public int ElapsedMs { get; set; }
        public StartupPositioningStage Stage { get; set; }
        public double CurrentA { get; set; }
        public double SlopeAperMs { get; set; }
        public string Decision { get; set; }
    }

    internal sealed class StartupPositioningResult
    {
        public int Channel { get; set; }
        public bool Succeeded { get; set; }
        public StartupPositioningStage Stage { get; set; }
        public StartupPositioningCompletionKind CompletionKind { get; set; }
        public string Code { get; set; }
        public string Reason { get; set; }
        public double PeakCurrentA { get; set; }
        public double LastSlopeAperMs { get; set; }
        public int ElapsedMs { get; set; }
        public int ForwardProgramProgressDeadlineMs { get; set; }
        public int ForwardProjectLimitMs { get; set; }
        public int ForwardLearnedProgressDeadlineMs { get; set; }
        public int ForwardEffectiveProgressDeadlineMs { get; set; }
        public int ForwardAbsoluteOnTimeMs { get; set; }
        public int ReverseProgramProgressDeadlineMs { get; set; }
        public int ReverseLearnedProgressDeadlineMs { get; set; }
        public int ReverseEffectiveProgressDeadlineMs { get; set; }
        public int ReverseAbsoluteOnTimeMs { get; set; }
        public double ReverseReleaseThresholdA { get; set; }
        public IReadOnlyList<StartupPositioningSample> Samples { get; set; } =
            Array.Empty<StartupPositioningSample>();
    }
}
