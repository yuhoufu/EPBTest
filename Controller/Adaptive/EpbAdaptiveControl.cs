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
    /// 纯电流判定状态机。它不直接操作 DO，也不执行日志/文件 IO，可用于实时控制和离线波形测试。
    /// </summary>
    public sealed class EpbAdaptiveCurrentStateMachine
    {
        private const int StableWindowMs = 150;
        private const int StableWindowMinCoverageMs = 120;
        private const int ForwardEmptyWindowMinCoverageMs = 100;
        private const int ReverseWindowMs = 200;
        private const int ReverseWindowMinCoverageMs = 120;
        private const int WindowMinimumSamples = 8;
        private const int WindowRetentionMs = 350;
        private const int PredictionSlopeWindowMs = 30;
        private const int ReleaseConfirmMs = 200;
        private const int NearZeroFaultMs = 200;
        private const double NearZeroA = 0.10;
        private const double MinimumPredictionSlopeAperMs = 0.001;
        private const double MaximumPredictionSlopeAperMs = 1.0;
        private const double MaximumPredictionLeadMs = 100.0;

        private readonly object _gate = new object();
        private readonly Queue<Sample> _window = new Queue<Sample>();
        private readonly Queue<Sample> _forwardEmptyWindow = new Queue<Sample>();

        private EpbAdaptiveProfile _profile;
        private EpbCurrentStage _stage = EpbCurrentStage.Idle;
        private long _powerStartTick;
        private long _lastSampleTick;
        private long _releaseCandidateTick;
        private long _nearZeroStartTick;
        private long _highPlateauStartTick;
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
            double overshootDeltaA)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
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
            double overshootDeltaA)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
                _forwardDirection = false;
                _reverseDecayLimitA = Math.Max(0.1, reverseDecayLimitA);
                _overshootDeltaA = Math.Max(0, overshootDeltaA);
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
                    EvaluateReverse(tick, current, decision);

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

            if (_stage == EpbCurrentStage.LoadRise)
            {
                var slope = PredictionSlopeAperMs();
                var leadMs = GetPredictionLeadMs(slope);
                var predictedPeak = current + slope * leadMs;
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

        private void EvaluateReverse(long tick, double current, EpbAdaptiveDecision decision)
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
                if (_releaseCandidateTick == 0)
                {
                    _releaseCandidateTick = tick;
                    return;
                }

                decision.ReleaseCandidateElapsedMs = ElapsedMs(_releaseCandidateTick, tick);
                if (decision.ReleaseCandidateElapsedMs >= ReleaseConfirmMs)
                {
                    SetStage(EpbCurrentStage.Released);
                    decision.ReleaseCompleted = true;
                    decision.StateChanged = true;
                    decision.Reason =
                        $"Released I={current:F3}A median={stats.Median:F3}A " +
                        $"p90={stats.P90:F3}A threshold={decision.ReleaseThresholdA:F3}A " +
                        $"spread={stats.P90 - stats.P10:F3}A allowed={decision.AllowedSpreadA:F3}A";
                }
            }
            else
            {
                _releaseCandidateTick = 0;
            }
        }

        private bool TryApplyReverseWindowDiagnostics(
            long tick,
            EpbAdaptiveDecision decision,
            out WindowStats stats)
        {
            if (!TryGetWindowStats(
                    tick,
                    ReverseWindowMs,
                    ReverseWindowMinCoverageMs,
                    WindowMinimumSamples,
                    out stats))
                return false;

            var lowLoadThreshold = _profile.IsStable && _profile.ReverseEmptyCurrentA > 0
                ? _profile.ReverseEmptyCurrentA + Math.Max(0.30, 4.0 * _profile.ReverseEmptyMadA)
                : _reverseDecayLimitA;
            var allowedSpread = Math.Max(
                0.30,
                8.0 * (_profile.IsStable ? _profile.ReverseEmptyMadA : stats.Mad));
            ApplyWindowDiagnostics(decision, stats);
            decision.ReleaseThresholdA = lowLoadThreshold;
            decision.AllowedSpreadA = allowedSpread;
            decision.WindowQualified =
                stats.P90 <= lowLoadThreshold &&
                stats.P90 - stats.P10 <= allowedSpread;
            if (_releaseCandidateTick != 0)
                decision.ReleaseCandidateElapsedMs = ElapsedMs(_releaseCandidateTick, tick);
            return true;
        }

        private void EvaluateAbnormalHighPlateau(long tick, int elapsedMs, EpbAdaptiveDecision decision)
        {
            if (elapsedMs < _inrushIgnoreMs + 200 || !WindowIsStable(out var median, out _))
            {
                _highPlateauStartTick = 0;
                return;
            }

            double abnormalThreshold;
            if (_forwardDirection)
            {
                if (_stage != EpbCurrentStage.EmptyTravel)
                {
                    _highPlateauStartTick = 0;
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
                _highPlateauStartTick = 0;
                return;
            }

            if (_highPlateauStartTick == 0)
            {
                _highPlateauStartTick = tick;
                return;
            }

            if (ElapsedMs(_highPlateauStartTick, tick) >= 200)
                Fault(decision,
                    $"AbnormalHighCurrentPlateau median={median:F3}A threshold={abnormalThreshold:F3}A");
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
            _highPlateauStartTick = 0;
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
            CutoffCurrentA = 0;
            CutoffSlopeAperMs = 0;
            PredictedPeakA = 0;
            PredictionLeadMs = 0;
            CutoffReason = string.Empty;
        }

        private void AddWindow(long tick, double current)
        {
            _window.Enqueue(new Sample(tick, current));
            while (_window.Count > 0 && ElapsedMs(_window.Peek().Tick, tick) > WindowRetentionMs)
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
