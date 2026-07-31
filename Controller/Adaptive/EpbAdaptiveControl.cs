using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Config;

namespace Controller.Adaptive
{
    public enum EpbControlMode
    {
        LegacyFixedTiming = 0,
        AdaptiveCurrent = 1
    }

    public static class EpbControlModeParser
    {
        public static EpbControlMode ParseOrDefault(
            string value,
            EpbControlMode defaultMode = EpbControlMode.LegacyFixedTiming)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultMode;
            var normalized = value.Trim();
            if (normalized.Equals("AdaptiveCurrent", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Adaptive", StringComparison.OrdinalIgnoreCase))
                return EpbControlMode.AdaptiveCurrent;
            if (normalized.Equals("LegacyFixedTiming", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Legacy", StringComparison.OrdinalIgnoreCase))
                return EpbControlMode.LegacyFixedTiming;
            return defaultMode;
        }
    }

    public enum EpbCurrentStage
    {
        Idle = 0,
        Inrush = 1,
        EmptyTravel = 2,
        LoadRise = 3,
        ClampReached = 4,
        Hold = 5,
        ReleaseDecay = 6,
        Released = 7,
        Faulted = 8
    }

    public enum EpbCycleOutcomeKind
    {
        Success = 0,
        SuccessWithWarning = 1,
        HardFault = 2,
        Canceled = 3
    }

    public sealed class EpbCycleOutcome
    {
        public EpbCycleOutcomeKind Kind { get; set; }
        public EpbCurrentStage Stage { get; set; }
        public string Reason { get; set; }
        public int ForwardElapsedMs { get; set; }
        public int ReverseElapsedMs { get; set; }
        public double PeakCurrentA { get; set; }
        public double TargetCurrentA { get; set; }
        public double CutoffCurrentA { get; set; }
        public double EstimatedSlopeAperMs { get; set; }
        public double PredictedPeakA { get; set; }
        public double PredictionLeadMs { get; set; }
        public double PeakErrorA { get; set; }
        public string CutoffReason { get; set; }
        public double ForwardEmptyCurrentA { get; set; }
        public double ReverseEmptyCurrentA { get; set; }

        public bool IsSuccess =>
            Kind == EpbCycleOutcomeKind.Success || Kind == EpbCycleOutcomeKind.SuccessWithWarning;

        public static EpbCycleOutcome Canceled(EpbCurrentStage stage, string reason)
        {
            return new EpbCycleOutcome
            {
                Kind = EpbCycleOutcomeKind.Canceled,
                Stage = stage,
                Reason = reason ?? "Canceled"
            };
        }

        public static EpbCycleOutcome HardFault(EpbCurrentStage stage, string reason)
        {
            return new EpbCycleOutcome
            {
                Kind = EpbCycleOutcomeKind.HardFault,
                Stage = stage,
                Reason = reason ?? "HardFault"
            };
        }
    }

    public sealed class EpbAdaptiveDecision
    {
        public bool ClampReached { get; set; }
        public bool ReleaseCompleted { get; set; }
        public bool SoftWarning { get; set; }
        public bool HardFault { get; set; }
        public bool StateChanged { get; set; }
        public EpbCurrentStage Stage { get; set; }
        public string Reason { get; set; }
        public double CurrentA { get; set; }
        public int ElapsedMs { get; set; }
        public int WindowSampleCount { get; set; }
        public int WindowSpanMs { get; set; }
        public double WindowMedianA { get; set; } = double.NaN;
        public double WindowMadA { get; set; } = double.NaN;
        public double WindowP10A { get; set; } = double.NaN;
        public double WindowP90A { get; set; } = double.NaN;
        public double ReleaseThresholdA { get; set; } = double.NaN;
        public double AllowedSpreadA { get; set; } = double.NaN;
        public double CutoffCurrentA { get; set; } = double.NaN;
        public double EstimatedSlopeAperMs { get; set; } = double.NaN;
        public double PredictedPeakA { get; set; } = double.NaN;
        public double PredictionLeadMs { get; set; } = double.NaN;
        public string CutoffReason { get; set; }
        public int ReleaseCandidateElapsedMs { get; set; }
        public bool WindowQualified { get; set; }

        public bool HasAction =>
            ClampReached || ReleaseCompleted || SoftWarning || HardFault || StateChanged;
    }

    /// <summary>
    /// 电流进展与断电代理确认的安全参数。默认值即使旧 XML 未配置也会生效。
    /// </summary>
    public sealed class EpbAdaptiveSafetyLimits
    {
        public int ForwardProgressConfirmMs { get; set; } = 1000;
        public double ForwardMinimumRiseSlopeAperMs { get; set; } = 0.001;
        public int ForwardProgressDeadlineMs { get; set; } = 5000;
        public int ReverseProgressConfirmMs { get; set; } = 200;
        public double ReverseMinimumDecaySlopeAperMs { get; set; } = 0.001;
        public int ReverseProgressDeadlineMs { get; set; } = 2500;
        public double OffCurrentClearThresholdA { get; set; } = 0.1;
        public int OffCurrentClearTimeoutMs { get; set; } = 1000;

        public EpbAdaptiveSafetyLimits Normalized()
        {
            var forwardConfirm = Math.Max(20, ForwardProgressConfirmMs);
            var reverseConfirm = Math.Max(20, ReverseProgressConfirmMs);
            return new EpbAdaptiveSafetyLimits
            {
                ForwardProgressConfirmMs = forwardConfirm,
                ForwardMinimumRiseSlopeAperMs = Math.Max(0.00001, ForwardMinimumRiseSlopeAperMs),
                ForwardProgressDeadlineMs = Math.Max(forwardConfirm, ForwardProgressDeadlineMs),
                ReverseProgressConfirmMs = reverseConfirm,
                ReverseMinimumDecaySlopeAperMs = Math.Max(0.00001, ReverseMinimumDecaySlopeAperMs),
                ReverseProgressDeadlineMs = Math.Max(reverseConfirm, ReverseProgressDeadlineMs),
                OffCurrentClearThresholdA = Math.Max(0.01, OffCurrentClearThresholdA),
                // 现场采集链路在断电后仍会经历约 200~300ms 的衰减/刷新。
                // 旧项目中的 100ms 会把正常衰减误判为继电器未断开，因此运行时强制迁移到 1s。
                OffCurrentClearTimeoutMs = Math.Max(1000, OffCurrentClearTimeoutMs)
            };
        }
    }

    /// <summary>
    /// 纯电流判定状态机。它不直接操作 DO，也不执行日志/文件 IO，可用于实时控制和离线波形测试。
    /// </summary>
    public sealed class EpbAdaptiveCurrentStateMachine
    {
        private const int StableWindowMs = 150;
        private const int StableWindowMinCoverageMs = 120;
        private const int ForwardEmptyWindowMinCoverageMs = 100;
        private const int WindowMinimumSamples = 8;
        private const int WindowRetentionMs = 350;
        private const int PredictionSlopeWindowMs = 30;
        private const int NearZeroFaultMs = 200;
        private const double NearZeroA = 0.10;
        private const double MinimumPredictionSlopeAperMs = 0.001;
        private const double MaximumPredictionSlopeAperMs = 1.0;
        private const double MaximumPredictionLeadMs = 100.0;
        private const double MaximumPlateauSpreadA = 0.75;

        private readonly object _gate = new object();
        private readonly Queue<Sample> _window = new Queue<Sample>();
        private readonly Queue<Sample> _forwardEmptyWindow = new Queue<Sample>();

        private EpbAdaptiveProfile _profile;
        private EpbCurrentStage _stage = EpbCurrentStage.Idle;
        private long _powerStartTick;
        private long _lastSampleTick;
        private long _releaseCandidateTick;
        private long _nearZeroStartTick;
        private int _inrushIgnoreMs;
        private int _absoluteMaxMs;
        private int _softLimitMs;
        private double _forwardA;
        private double _safetyMarginA;
        private double _overshootDeltaA;
        private double _reverseDecayLimitA;
        private int _consecutiveOverCurrent;
        private int _clampConfirmSamples;
        private bool _softWarningRaised;
        private bool _forwardDirection;
        private double _peakCurrentA;
        private double _lastCurrentA;
        private double _observedForwardEmptyA;
        private double _observedForwardEmptyMadA;
        private double _observedReverseEmptyA;
        private double _loadRisePeakA;
        private long _loadRiseDropStartTick;
        private long _loadRiseStartTick;
        private long _forwardProgressStallStartTick;
        private EpbAdaptiveSafetyLimits _safetyLimits = new EpbAdaptiveSafetyLimits();
        private int _reverseNoModelDeadlineMs;

        public EpbAdaptiveCurrentStateMachine(EpbAdaptiveProfile profile)
        {
            _profile = profile?.Clone() ?? new EpbAdaptiveProfile();
        }

        public EpbCurrentStage Stage
        {
            get { lock (_gate) return _stage; }
        }

        public double PeakCurrentA
        {
            get { lock (_gate) return _peakCurrentA; }
        }

        public double ObservedForwardEmptyA
        {
            get { lock (_gate) return _observedForwardEmptyA; }
        }

        public double ObservedReverseEmptyA
        {
            get { lock (_gate) return _observedReverseEmptyA; }
        }

        public double CutoffCurrentA { get; private set; }
        public double CutoffSlopeAperMs { get; private set; }
        public double PredictedPeakA { get; private set; }
        public double PredictionLeadMs { get; private set; }
        public string CutoffReason { get; private set; }

        public void UpdateProfile(EpbAdaptiveProfile profile)
        {
            lock (_gate) _profile = profile?.Clone() ?? new EpbAdaptiveProfile();
        }

        public void ArmForward(
            long startTick,
            int inrushIgnoreMs,
            int absoluteMaxMs,
            double forwardA,
            double safetyMarginA,
            double overshootDeltaA,
            EpbAdaptiveSafetyLimits safetyLimits = null)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
                _safetyLimits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
                _forwardDirection = true;
                _forwardA = Math.Max(0, forwardA);
                _safetyMarginA = Math.Max(0, safetyMarginA);
                _overshootDeltaA = Math.Max(0, overshootDeltaA);
                _softLimitMs = _profile.GetForwardSoftLimitMs();
                SetStage(EpbCurrentStage.Inrush);
            }
        }

        public void MarkHold()
        {
            lock (_gate) SetStage(EpbCurrentStage.Hold);
        }

        public void ArmReverse(
            long startTick,
            int inrushIgnoreMs,
            int absoluteMaxMs,
            double reverseDecayLimitA,
            double overshootDeltaA,
            double forwardReferenceA = 0,
            EpbAdaptiveSafetyLimits safetyLimits = null,
            int reverseNoModelDeadlineMs = 0)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
                _safetyLimits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
                _reverseNoModelDeadlineMs = reverseNoModelDeadlineMs > 0
                    ? Math.Max(_safetyLimits.ReverseProgressConfirmMs, reverseNoModelDeadlineMs)
                    : 0;
                _forwardDirection = false;
                _reverseDecayLimitA = Math.Max(0.1, reverseDecayLimitA);
                _overshootDeltaA = Math.Max(0, overshootDeltaA);
                if (forwardReferenceA > 0 &&
                    !double.IsNaN(forwardReferenceA) &&
                    !double.IsInfinity(forwardReferenceA))
                    _forwardA = forwardReferenceA;
                SetStage(EpbCurrentStage.Inrush);
            }
        }

        public void Disarm()
        {
            lock (_gate)
            {
                _window.Clear();
                _forwardEmptyWindow.Clear();
                SetStage(EpbCurrentStage.Idle);
                _lastSampleTick = 0;
            }
        }

        public EpbAdaptiveDecision OnSample(long tick, double currentAmp)
        {
            lock (_gate)
            {
                var decision = NewDecision(currentAmp);
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                var current = Math.Abs(currentAmp);
                _lastSampleTick = tick;
                _lastCurrentA = current;
                if (current > _peakCurrentA) _peakCurrentA = current;

                var elapsedMs = ElapsedMs(_powerStartTick, tick);
                decision.ElapsedMs = elapsedMs;

                if (elapsedMs >= _absoluteMaxMs)
                {
                    if (!_forwardDirection)
                        TryApplyReverseWindowDiagnostics(tick, decision, out _);
                    return Fault(decision, _forwardDirection ? "ForwardAbsoluteOnTimeExceeded" : "ReverseAbsoluteOnTimeExceeded");
                }

                var overCurrentLimit = _forwardA + _overshootDeltaA;
                if (_overshootDeltaA > 0 && current >= overCurrentLimit)
                    _consecutiveOverCurrent++;
                else
                    _consecutiveOverCurrent = 0;

                if (_consecutiveOverCurrent >= 3)
                    return Fault(decision,
                        $"OverCurrent3Samples I={current:F3}A Limit={overCurrentLimit:F3}A");

                if (elapsedMs >= _inrushIgnoreMs)
                {
                    if (current <= NearZeroA)
                    {
                        if (_nearZeroStartTick == 0) _nearZeroStartTick = tick;
                        if (ElapsedMs(_nearZeroStartTick, tick) >= NearZeroFaultMs)
                            return Fault(decision, $"OpenCircuitOrOutputFault I={current:F3}A");
                    }
                    else
                    {
                        _nearZeroStartTick = 0;
                    }
                }

                AddWindow(tick, current);

                if (_stage == EpbCurrentStage.Inrush && elapsedMs >= _inrushIgnoreMs)
                {
                    SetStage(_forwardDirection ? EpbCurrentStage.EmptyTravel : EpbCurrentStage.ReleaseDecay);
                    decision.StateChanged = true;
                    decision.Stage = _stage;
                    decision.Reason = _stage.ToString();
                }

                if (_forwardDirection)
                    EvaluateForward(tick, current, elapsedMs, decision);
                else
                    EvaluateReverse(tick, current, elapsedMs, decision);

                if (!decision.HardFault && !decision.ClampReached && !decision.ReleaseCompleted)
                    EvaluateAbnormalHighPlateau(tick, elapsedMs, decision);

                decision.Stage = _stage;
                return decision;
            }
        }

        public EpbAdaptiveDecision CheckWatchdog(long nowTick)
        {
            lock (_gate)
            {
                var decision = NewDecision(_lastCurrentA);
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.ClampReached ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                decision.ElapsedMs = ElapsedMs(_powerStartTick, nowTick);
                if (!_forwardDirection)
                    TryApplyReverseWindowDiagnostics(nowTick, decision, out _);

                if ((_lastSampleTick == 0 && decision.ElapsedMs > 100) ||
                    (_lastSampleTick != 0 && ElapsedMs(_lastSampleTick, nowTick) > 100))
                    return Fault(decision, "DaqSampleStale>100ms");

                if (decision.ElapsedMs >= _absoluteMaxMs)
                    return Fault(decision, _forwardDirection ? "ForwardAbsoluteOnTimeExceeded" : "ReverseAbsoluteOnTimeExceeded");

                return decision;
            }
        }

        private void EvaluateForward(long tick, double current, int elapsedMs, EpbAdaptiveDecision decision)
        {
            if (!_softWarningRaised && _softLimitMs > 0 && elapsedMs >= _softLimitMs &&
                _stage != EpbCurrentStage.ClampReached)
            {
                _softWarningRaised = true;
                decision.SoftWarning = true;
                decision.Reason =
                    $"ForwardSoftLimit elapsed={elapsedMs}ms baseline={_profile.ForwardClampMedianMs:F0}ms MAD={_profile.ForwardClampMadMs:F0}ms";
            }

            if (_stage == EpbCurrentStage.EmptyTravel)
            {
                var historicalBaselineAvailable =
                    _profile.IsStable && _profile.ForwardEmptyCurrentA > 0;
                var provisionalBaseline = historicalBaselineAvailable
                    ? _profile.ForwardEmptyCurrentA
                    : _observedForwardEmptyA;
                var provisionalMad = historicalBaselineAvailable
                    ? _profile.ForwardEmptyMadA
                    : _observedForwardEmptyMadA;
                var provisionalLoadRiseThreshold = provisionalBaseline > 0
                    ? provisionalBaseline + Math.Max(0.5, 4.0 * provisionalMad)
                    : double.PositiveInfinity;

                // 独立保留本圈负载上升前的样本。即使历史模型已经稳定，也必须先形成
                // 本圈真实空行程观测，禁止使用历史基线冒充本圈样本。
                if (_observedForwardEmptyA <= 0 &&
                    (!historicalBaselineAvailable || current < provisionalLoadRiseThreshold))
                {
                    AddForwardEmptyWindow(tick, current);
                    if (ForwardEmptyWindowIsStable(out var median, out var mad))
                    {
                        _observedForwardEmptyA = median;
                        _observedForwardEmptyMadA = mad;
                    }
                }

                if (_observedForwardEmptyA > 0)
                {
                    var baseline = historicalBaselineAvailable
                        ? _profile.ForwardEmptyCurrentA
                        : _observedForwardEmptyA;
                    var baselineMad = historicalBaselineAvailable
                        ? _profile.ForwardEmptyMadA
                        : _observedForwardEmptyMadA;
                    var loadRiseThreshold = baseline + Math.Max(0.5, 4.0 * baselineMad);
                    if (current >= loadRiseThreshold && WindowSlopeAperMs() > 0.001)
                    {
                        SetStage(EpbCurrentStage.LoadRise);
                        _loadRiseStartTick = tick;
                        decision.StateChanged = true;
                        decision.Reason = "LoadRise";
                    }
                }
            }

            if (_stage == EpbCurrentStage.EmptyTravel && current >= _forwardA)
            {
                Fault(
                    decision,
                    $"CurveSequenceInvalid ThresholdBeforeLoadRise I={current:F3}A Target={_forwardA:F3}A");
                return;
            }

            if (_stage == EpbCurrentStage.EmptyTravel &&
                elapsedMs >= GetForwardProgressDeadlineMs())
            {
                var deadlineMs = GetForwardProgressDeadlineMs();
                Fault(
                    decision,
                    $"ForwardLoadRiseNotStarted elapsed={elapsedMs}ms " +
                    $"deadline={deadlineMs}ms I={current:F3}A Target={_forwardA:F3}A");
                return;
            }

            if (_stage == EpbCurrentStage.LoadRise)
            {
                var slope = PredictionSlopeAperMs();
                var leadMs = GetPredictionLeadMs(slope);
                // 历史“实际峰值 - 目标值”长期偏正时，把中位数+MAD作为模型残差补偿。
                // 这会在同样的上升斜率下更早断电，优先从控制算法消除偶发超调。
                var peakBiasCorrectionA = _profile.GetForwardPeakBiasCorrectionA();
                var predictedPeak = current + slope * leadMs + peakBiasCorrectionA;
                decision.CutoffCurrentA = current;
                decision.EstimatedSlopeAperMs = slope;
                decision.PredictionLeadMs = leadMs;
                decision.PredictedPeakA = predictedPeak;

                if (current > _loadRisePeakA) _loadRisePeakA = current;
                if (_loadRisePeakA - current >= 2.0 && current < _forwardA)
                {
                    if (_loadRiseDropStartTick == 0) _loadRiseDropStartTick = tick;
                    if (ElapsedMs(_loadRiseDropStartTick, tick) >= 100)
                    {
                        Fault(
                            decision,
                            $"AbnormalLoadRiseDrop Peak={_loadRisePeakA:F3}A Current={current:F3}A");
                        return;
                    }
                }
                else
                {
                    _loadRiseDropStartTick = 0;
                }

                var stallProbeMs = Math.Max(
                    100,
                    Math.Min(200, _safetyLimits.ForwardProgressConfirmMs / 5));
                if (_loadRiseStartTick != 0 &&
                    TryGetLinearSlope(
                        tick,
                        stallProbeMs + 20,
                        stallProbeMs,
                        out var stallProbeStats,
                        out var stallProbeSlope) &&
                    current < _forwardA &&
                    stallProbeStats.P90 < _forwardA &&
                    stallProbeSlope <= _safetyLimits.ForwardMinimumRiseSlopeAperMs)
                {
                    if (_forwardProgressStallStartTick == 0)
                    {
                        _forwardProgressStallStartTick = tick -
                            (long)(stallProbeStats.SpanMs * Stopwatch.Frequency / 1000.0);
                    }

                    if (ElapsedMs(_forwardProgressStallStartTick, tick) >=
                        _safetyLimits.ForwardProgressConfirmMs &&
                        TryGetLinearSlope(
                            tick,
                            _safetyLimits.ForwardProgressConfirmMs + 20,
                            _safetyLimits.ForwardProgressConfirmMs,
                            out var progressStats,
                            out var progressSlope) &&
                        progressStats.P90 < _forwardA &&
                        progressSlope <= _safetyLimits.ForwardMinimumRiseSlopeAperMs)
                    {
                        ApplyWindowDiagnostics(decision, progressStats);
                        decision.EstimatedSlopeAperMs = progressSlope;
                        Fault(
                            decision,
                            $"ForwardCurrentRiseStalled slope={progressSlope:F6}A/ms " +
                            $"limit={_safetyLimits.ForwardMinimumRiseSlopeAperMs:F6}A/ms " +
                            $"window={progressStats.SpanMs}ms median={progressStats.Median:F3}A " +
                            $"Target={_forwardA:F3}A");
                        return;
                    }
                }
                else
                {
                    _forwardProgressStallStartTick = 0;
                }

                // 实际电流达到目标时立即断电；预测触发则仍要求连续三点，抵抗孤立毛刺。
                var directTargetReached = current >= _forwardA;
                var predictionReached =
                    slope >= MinimumPredictionSlopeAperMs && predictedPeak >= _forwardA;
                _clampConfirmSamples = predictionReached ? _clampConfirmSamples + 1 : 0;
                if (directTargetReached || _clampConfirmSamples >= 3)
                {
                    SetStage(EpbCurrentStage.ClampReached);
                    CutoffCurrentA = current;
                    CutoffSlopeAperMs = slope;
                    PredictedPeakA = predictedPeak;
                    PredictionLeadMs = leadMs;
                    CutoffReason = directTargetReached ? "DirectTarget" : "PredictedPeak";
                    decision.ClampReached = true;
                    decision.StateChanged = true;
                    decision.CutoffReason = CutoffReason;
                    decision.Reason =
                        directTargetReached
                            ? $"ClampReachedDirect I={current:F3}A Target={_forwardA:F3}A"
                            : $"ClampReachedPredicted I={current:F3}A Predicted={predictedPeak:F3}A " +
                              $"Target={_forwardA:F3}A Slope={slope:F4}A/ms Lead={leadMs:F2}ms Samples=3";
                }
            }
        }

        private void EvaluateReverse(
            long tick,
            double current,
            int elapsedMs,
            EpbAdaptiveDecision decision)
        {
            if (_stage != EpbCurrentStage.ReleaseDecay) return;

            if (!TryApplyReverseWindowDiagnostics(tick, decision, out var stats))
            {
                _releaseCandidateTick = 0;
                return;
            }

            if (decision.WindowQualified)
            {
                _observedReverseEmptyA = stats.Median;
                _releaseCandidateTick = tick;
                decision.ReleaseCandidateElapsedMs = stats.SpanMs;
                SetStage(EpbCurrentStage.Released);
                decision.ReleaseCompleted = true;
                decision.StateChanged = true;
                decision.Reason =
                    $"Released I={current:F3}A median={stats.Median:F3}A " +
                    $"p90={stats.P90:F3}A threshold={decision.ReleaseThresholdA:F3}A " +
                    $"spread={stats.P90 - stats.P10:F3}A allowed={decision.AllowedSpreadA:F3}A " +
                    $"slope={decision.EstimatedSlopeAperMs:F6}A/ms confirm={stats.SpanMs}ms";
                return;
            }
            else
            {
                _releaseCandidateTick = 0;
            }

            var progressDeadlineMs = GetReverseProgressDeadlineMs();
            if (elapsedMs < progressDeadlineMs) return;
            if (!TryGetLinearSlope(
                    tick,
                    _safetyLimits.ReverseProgressConfirmMs + 20,
                    _safetyLimits.ReverseProgressConfirmMs,
                    out var progressStats,
                    out var progressSlope))
                return;

            if (progressStats.P10 > decision.ReleaseThresholdA &&
                progressSlope >= -_safetyLimits.ReverseMinimumDecaySlopeAperMs)
            {
                ApplyWindowDiagnostics(decision, progressStats);
                decision.EstimatedSlopeAperMs = progressSlope;
                Fault(
                    decision,
                    $"ReverseCurrentDecayStalled slope={progressSlope:F6}A/ms " +
                    $"limit=-{_safetyLimits.ReverseMinimumDecaySlopeAperMs:F6}A/ms " +
                    $"elapsed={elapsedMs}ms deadline={progressDeadlineMs}ms " +
                    $"window={progressStats.SpanMs}ms median={progressStats.Median:F3}A " +
                    $"releaseThreshold={decision.ReleaseThresholdA:F3}A");
            }
        }

        private bool TryApplyReverseWindowDiagnostics(
            long tick,
            EpbAdaptiveDecision decision,
            out WindowStats stats)
        {
            if (!TryGetWindowStats(
                    tick,
                    _safetyLimits.ReverseProgressConfirmMs + 20,
                    _safetyLimits.ReverseProgressConfirmMs,
                    WindowMinimumSamples,
                    out stats))
                return false;

            if (!TryGetLinearSlope(
                    tick,
                    _safetyLimits.ReverseProgressConfirmMs + 20,
                    _safetyLimits.ReverseProgressConfirmMs,
                    out _,
                    out var plateauSlope))
                return false;

            var lowLoadThreshold = _profile.IsStable && _profile.ReverseEmptyCurrentA > 0
                ? Math.Min(
                    _reverseDecayLimitA,
                    _profile.ReverseEmptyCurrentA + Math.Max(0.30, 4.0 * _profile.ReverseEmptyMadA))
                : _reverseDecayLimitA;
            var allowedSpread = Math.Min(
                MaximumPlateauSpreadA,
                Math.Max(
                    0.30,
                    4.0 * (_profile.IsStable ? _profile.ReverseEmptyMadA : stats.Mad)));
            ApplyWindowDiagnostics(decision, stats);
            decision.ReleaseThresholdA = lowLoadThreshold;
            decision.AllowedSpreadA = allowedSpread;
            decision.EstimatedSlopeAperMs = plateauSlope;
            decision.WindowQualified =
                stats.P90 <= lowLoadThreshold &&
                stats.P90 - stats.P10 <= allowedSpread &&
                Math.Abs(plateauSlope) <= _safetyLimits.ReverseMinimumDecaySlopeAperMs;
            if (_releaseCandidateTick != 0)
                decision.ReleaseCandidateElapsedMs = ElapsedMs(_releaseCandidateTick, tick);
            return true;
        }

        private void EvaluateAbnormalHighPlateau(long tick, int elapsedMs, EpbAdaptiveDecision decision)
        {
            const int highPlateauConfirmMs = 200;
            if (elapsedMs < _inrushIgnoreMs + highPlateauConfirmMs ||
                !TryGetWindowStats(
                    tick,
                    highPlateauConfirmMs + 20,
                    highPlateauConfirmMs,
                    WindowMinimumSamples,
                    out var stats) ||
                !TryGetLinearSlope(
                    tick,
                    highPlateauConfirmMs + 20,
                    highPlateauConfirmMs,
                    out _,
                    out var plateauSlope) ||
                stats.P90 - stats.P10 > MaximumPlateauSpreadA)
            {
                return;
            }

            var slopeLimit = _forwardDirection
                ? _safetyLimits.ForwardMinimumRiseSlopeAperMs
                : _safetyLimits.ReverseMinimumDecaySlopeAperMs;
            if (Math.Abs(plateauSlope) > slopeLimit)
            {
                return;
            }

            var median = stats.Median;

            double abnormalThreshold;
            if (_forwardDirection)
            {
                if (_stage != EpbCurrentStage.EmptyTravel)
                {
                    return;
                }
                var emptyBaseline = _profile.IsStable && _profile.ForwardEmptyCurrentA > 0
                    ? _profile.ForwardEmptyCurrentA
                    : 1.0;
                abnormalThreshold = Math.Max(emptyBaseline + 2.0, _forwardA * 0.50);
            }
            else
            {
                abnormalThreshold = Math.Max(_reverseDecayLimitA + 2.0, _forwardA * 0.60);
            }

            if (median < abnormalThreshold)
            {
                return;
            }

            ApplyWindowDiagnostics(decision, stats);
            decision.EstimatedSlopeAperMs = plateauSlope;
            Fault(decision,
                $"AbnormalHighCurrentPlateau median={median:F3}A threshold={abnormalThreshold:F3}A " +
                $"window={stats.SpanMs}ms slope={plateauSlope:F6}A/ms");
        }

        private EpbAdaptiveDecision Fault(EpbAdaptiveDecision decision, string reason)
        {
            SetStage(EpbCurrentStage.Faulted);
            decision.HardFault = true;
            decision.StateChanged = true;
            decision.Stage = _stage;
            decision.Reason = reason;
            return decision;
        }

        private EpbAdaptiveDecision NewDecision(double currentA)
        {
            return new EpbAdaptiveDecision
            {
                Stage = _stage,
                CurrentA = Math.Abs(currentA)
            };
        }

        private void ResetDirection(long startTick, int inrushIgnoreMs, int absoluteMaxMs)
        {
            _window.Clear();
            _forwardEmptyWindow.Clear();
            _powerStartTick = startTick;
            _lastSampleTick = 0;
            _releaseCandidateTick = 0;
            _nearZeroStartTick = 0;
            _inrushIgnoreMs = Math.Max(0, inrushIgnoreMs);
            _absoluteMaxMs = Math.Max(1, absoluteMaxMs);
            _softLimitMs = 0;
            _consecutiveOverCurrent = 0;
            _clampConfirmSamples = 0;
            _softWarningRaised = false;
            _peakCurrentA = 0;
            _lastCurrentA = 0;
            _observedForwardEmptyA = 0;
            _observedForwardEmptyMadA = 0;
            _observedReverseEmptyA = 0;
            _loadRisePeakA = 0;
            _loadRiseDropStartTick = 0;
            _loadRiseStartTick = 0;
            _forwardProgressStallStartTick = 0;
            _reverseNoModelDeadlineMs = 0;
            CutoffCurrentA = 0;
            CutoffSlopeAperMs = 0;
            PredictedPeakA = 0;
            PredictionLeadMs = 0;
            CutoffReason = string.Empty;
        }

        private void AddWindow(long tick, double current)
        {
            _window.Enqueue(new Sample(tick, current));
            var retentionMs = Math.Max(
                WindowRetentionMs,
                Math.Max(
                    _safetyLimits.ForwardProgressConfirmMs,
                    _safetyLimits.ReverseProgressConfirmMs) + 50);
            while (_window.Count > 0 && ElapsedMs(_window.Peek().Tick, tick) > retentionMs)
                _window.Dequeue();
        }

        private bool WindowIsStable(out double median, out double mad)
        {
            median = 0;
            mad = 0;
            if (!TryGetWindowStats(
                    _lastSampleTick,
                    StableWindowMs,
                    StableWindowMinCoverageMs,
                    WindowMinimumSamples,
                    out var stats))
                return false;

            median = stats.Median;
            mad = stats.Mad;
            return stats.Range <= Math.Max(0.15, 6.0 * mad);
        }

        private void AddForwardEmptyWindow(long tick, double current)
        {
            _forwardEmptyWindow.Enqueue(new Sample(tick, current));
            while (_forwardEmptyWindow.Count > 0 &&
                   ElapsedMs(_forwardEmptyWindow.Peek().Tick, tick) > WindowRetentionMs)
                _forwardEmptyWindow.Dequeue();
        }

        private bool ForwardEmptyWindowIsStable(out double median, out double mad)
        {
            median = 0;
            mad = 0;
            if (_forwardEmptyWindow.Count == 0) return false;
            var nowTick = _forwardEmptyWindow.Last().Tick;
            if (!TryGetWindowStats(
                    _forwardEmptyWindow,
                    nowTick,
                    StableWindowMs,
                    ForwardEmptyWindowMinCoverageMs,
                    WindowMinimumSamples,
                    out var stats))
                return false;

            median = stats.Median;
            mad = stats.Mad;
            return stats.Range <= Math.Max(0.15, 6.0 * mad);
        }

        private bool TryGetWindowStats(
            long nowTick,
            int windowMs,
            int minimumCoverageMs,
            int minimumSamples,
            out WindowStats stats)
        {
            return TryGetWindowStats(
                _window,
                nowTick,
                windowMs,
                minimumCoverageMs,
                minimumSamples,
                out stats);
        }

        private static bool TryGetWindowStats(
            IEnumerable<Sample> source,
            long nowTick,
            int windowMs,
            int minimumCoverageMs,
            int minimumSamples,
            out WindowStats stats)
        {
            stats = default;
            if (source == null || nowTick <= 0) return false;

            var samples = source
                .Where(x => ElapsedMs(x.Tick, nowTick) <= windowMs)
                .ToArray();
            if (samples.Length < minimumSamples) return false;

            var spanMs = ElapsedMs(samples[0].Tick, samples[samples.Length - 1].Tick);
            if (spanMs < minimumCoverageMs) return false;

            var values = samples.Select(x => x.CurrentA).OrderBy(x => x).ToArray();
            var median = Median(values);
            var medianValue = median;
            var mad = Median(values
                .Select(x => Math.Abs(x - medianValue))
                .OrderBy(x => x)
                .ToArray());

            stats = new WindowStats(
                samples.Length,
                spanMs,
                median,
                mad,
                Quantile(values, 0.10),
                Quantile(values, 0.90),
                values[values.Length - 1] - values[0]);
            return true;
        }

        private static void ApplyWindowDiagnostics(
            EpbAdaptiveDecision decision,
            WindowStats stats)
        {
            decision.WindowSampleCount = stats.SampleCount;
            decision.WindowSpanMs = stats.SpanMs;
            decision.WindowMedianA = stats.Median;
            decision.WindowMadA = stats.Mad;
            decision.WindowP10A = stats.P10;
            decision.WindowP90A = stats.P90;
        }

        private double WindowSlopeAperMs()
        {
            var samples = _window
                .Where(x => ElapsedMs(x.Tick, _lastSampleTick) <= StableWindowMs)
                .ToArray();
            if (samples.Length < 2) return 0;
            var first = samples[0];
            var last = samples[samples.Length - 1];
            var elapsed = ElapsedMs(first.Tick, last.Tick);
            return elapsed <= 0 ? 0 : (last.CurrentA - first.CurrentA) / elapsed;
        }

        private bool TryGetLinearSlope(
            long tick,
            int windowMs,
            int minimumCoverageMs,
            out WindowStats stats,
            out double slopeAperMs)
        {
            slopeAperMs = 0;
            if (!TryGetWindowStats(
                    tick,
                    windowMs,
                    minimumCoverageMs,
                    WindowMinimumSamples,
                    out stats))
                return false;

            var samples = _window
                .Where(x => ElapsedMs(x.Tick, tick) <= windowMs)
                .ToArray();
            if (samples.Length < WindowMinimumSamples) return false;

            var firstTick = samples[0].Tick;
            var times = samples
                .Select(x => (x.Tick - firstTick) * 1000.0 / Stopwatch.Frequency)
                .ToArray();
            var meanTime = times.Average();
            var meanCurrent = samples.Average(x => x.CurrentA);
            double covariance = 0;
            double variance = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var dt = times[i] - meanTime;
                covariance += dt * (samples[i].CurrentA - meanCurrent);
                variance += dt * dt;
            }

            if (variance <= 1e-9) return false;
            slopeAperMs = covariance / variance;
            return !double.IsNaN(slopeAperMs) && !double.IsInfinity(slopeAperMs);
        }

        private int GetForwardProgressDeadlineMs()
        {
            var configured = Math.Max(
                _inrushIgnoreMs + _safetyLimits.ForwardProgressConfirmMs,
                _safetyLimits.ForwardProgressDeadlineMs);
            if (!_profile.IsStable || _profile.ForwardClampMedianMs <= 0)
                return Math.Min(_absoluteMaxMs, configured);

            var learned = (int)Math.Ceiling(
                _profile.ForwardClampMedianMs +
                Math.Max(300.0, 4.0 * _profile.ForwardClampMadMs));
            return Math.Min(
                _absoluteMaxMs,
                Math.Max(
                    _inrushIgnoreMs + _safetyLimits.ForwardProgressConfirmMs,
                    Math.Min(configured, learned)));
        }

        private int GetReverseProgressDeadlineMs()
        {
            var configured = Math.Max(
                _inrushIgnoreMs + _safetyLimits.ReverseProgressConfirmMs,
                _safetyLimits.ReverseProgressDeadlineMs);
            if (!_profile.IsStable || _profile.ReverseReleaseMedianMs <= 0)
            {
                var fallback = _reverseNoModelDeadlineMs > 0
                    ? _reverseNoModelDeadlineMs
                    : configured;
                return Math.Min(
                    _absoluteMaxMs,
                    Math.Max(_inrushIgnoreMs + _safetyLimits.ReverseProgressConfirmMs, fallback));
            }

            var learned = (int)Math.Ceiling(
                _profile.ReverseReleaseMedianMs +
                Math.Max(300.0, 4.0 * _profile.ReverseReleaseMadMs));
            return Math.Min(
                _absoluteMaxMs,
                Math.Max(
                    _inrushIgnoreMs + _safetyLimits.ReverseProgressConfirmMs,
                    Math.Min(configured, learned)));
        }

        private double PredictionSlopeAperMs()
        {
            var samples = _window
                .Where(x => ElapsedMs(x.Tick, _lastSampleTick) <= PredictionSlopeWindowMs)
                .ToArray();
            if (samples.Length < 3) return 0;

            var firstTick = samples[0].Tick;
            var times = samples
                .Select(x => (x.Tick - firstTick) * 1000.0 / Stopwatch.Frequency)
                .ToArray();
            var meanTime = times.Average();
            var meanCurrent = samples.Average(x => x.CurrentA);
            double covariance = 0;
            double variance = 0;
            for (var i = 0; i < samples.Length; i++)
            {
                var dt = times[i] - meanTime;
                covariance += dt * (samples[i].CurrentA - meanCurrent);
                variance += dt * dt;
            }

            if (variance <= 1e-9) return 0;
            var slope = covariance / variance;
            if (double.IsNaN(slope) || double.IsInfinity(slope) ||
                slope < MinimumPredictionSlopeAperMs)
                return 0;
            return Math.Min(MaximumPredictionSlopeAperMs, slope);
        }

        private double GetPredictionLeadMs(double slopeAperMs)
        {
            if (slopeAperMs < MinimumPredictionSlopeAperMs) return 0;
            if (_profile.HasCutoffPrediction)
                return Math.Max(
                    0,
                    Math.Min(MaximumPredictionLeadMs, _profile.ForwardCutoffLeadMedianMs));
            return Math.Max(
                0,
                Math.Min(MaximumPredictionLeadMs, _safetyMarginA / slopeAperMs));
        }

        private void SetStage(EpbCurrentStage stage)
        {
            _stage = stage;
        }

        private static int ElapsedMs(long startTick, long endTick)
        {
            if (startTick <= 0 || endTick <= startTick) return 0;
            return (int)Math.Min(int.MaxValue,
                (endTick - startTick) * 1000.0 / Stopwatch.Frequency);
        }

        private static double Median(double[] values)
        {
            if (values == null || values.Length == 0) return 0;
            var mid = values.Length / 2;
            return values.Length % 2 == 0
                ? (values[mid - 1] + values[mid]) / 2.0
                : values[mid];
        }

        private static double Quantile(double[] sortedValues, double probability)
        {
            if (sortedValues == null || sortedValues.Length == 0) return 0;
            var p = Math.Max(0, Math.Min(1, probability));
            var index = (int)Math.Round(
                p * (sortedValues.Length - 1),
                MidpointRounding.AwayFromZero);
            return sortedValues[index];
        }

        private readonly struct WindowStats
        {
            public WindowStats(
                int sampleCount,
                int spanMs,
                double median,
                double mad,
                double p10,
                double p90,
                double range)
            {
                SampleCount = sampleCount;
                SpanMs = spanMs;
                Median = median;
                Mad = mad;
                P10 = p10;
                P90 = p90;
                Range = range;
            }

            public int SampleCount { get; }
            public int SpanMs { get; }
            public double Median { get; }
            public double Mad { get; }
            public double P10 { get; }
            public double P90 { get; }
            public double Range { get; }
        }

        private readonly struct Sample
        {
            public Sample(long tick, double currentA)
            {
                Tick = tick;
                CurrentA = currentA;
            }

            public long Tick { get; }
            public double CurrentA { get; }
        }
    }
}
