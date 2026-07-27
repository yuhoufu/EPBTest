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

        public bool HasAction =>
            ClampReached || ReleaseCompleted || SoftWarning || HardFault || StateChanged;
    }

    /// <summary>
    /// 纯电流判定状态机。它不直接操作 DO，也不执行日志/文件 IO，可用于实时控制和离线波形测试。
    /// </summary>
    public sealed class EpbAdaptiveCurrentStateMachine
    {
        private const int StableWindowMs = 150;
        private const int ReleaseConfirmMs = 200;
        private const int NearZeroFaultMs = 200;
        private const double NearZeroA = 0.10;

        private readonly object _gate = new object();
        private readonly Queue<Sample> _window = new Queue<Sample>();

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
        private bool _softWarningRaised;
        private bool _forwardDirection;
        private double _peakCurrentA;
        private double _observedForwardEmptyA;
        private double _observedForwardEmptyMadA;
        private double _observedReverseEmptyA;

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
                if (current > _peakCurrentA) _peakCurrentA = current;

                var elapsedMs = ElapsedMs(_powerStartTick, tick);
                decision.ElapsedMs = elapsedMs;

                if (elapsedMs >= _absoluteMaxMs)
                    return Fault(decision, _forwardDirection ? "ForwardAbsoluteOnTimeExceeded" : "ReverseAbsoluteOnTimeExceeded");

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
                var decision = NewDecision(0);
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.ClampReached ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                if ((_lastSampleTick == 0 && ElapsedMs(_powerStartTick, nowTick) > 100) ||
                    (_lastSampleTick != 0 && ElapsedMs(_lastSampleTick, nowTick) > 100))
                    return Fault(decision, "DaqSampleStale>100ms");

                if (ElapsedMs(_powerStartTick, nowTick) >= _absoluteMaxMs)
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
                if (WindowIsStable(out var median, out var mad) && _observedForwardEmptyA <= 0)
                {
                    _observedForwardEmptyA = median;
                    _observedForwardEmptyMadA = mad;
                }

                if (_observedForwardEmptyA > 0 ||
                    (_profile.IsStable && _profile.ForwardEmptyCurrentA > 0))
                {
                    var baseline = _profile.IsStable && _profile.ForwardEmptyCurrentA > 0
                        ? _profile.ForwardEmptyCurrentA
                        : _observedForwardEmptyA;
                    var baselineMad = _profile.IsStable
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

            if (current + _safetyMarginA >= _forwardA)
            {
                SetStage(EpbCurrentStage.ClampReached);
                decision.ClampReached = true;
                decision.StateChanged = true;
                decision.Reason = $"ClampReached I={current:F3}A Margin={_safetyMarginA:F3}A";
            }
        }

        private void EvaluateReverse(long tick, double current, EpbAdaptiveDecision decision)
        {
            if (_stage != EpbCurrentStage.ReleaseDecay) return;
            if (!WindowIsStable(out var median, out var mad))
            {
                _releaseCandidateTick = 0;
                return;
            }

            var lowLoadThreshold = _profile.IsStable && _profile.ReverseEmptyCurrentA > 0
                ? _profile.ReverseEmptyCurrentA + Math.Max(0.30, 4.0 * _profile.ReverseEmptyMadA)
                : _reverseDecayLimitA;
            var flatLimit = Math.Max(0.15, 6.0 * (_profile.IsStable ? _profile.ReverseEmptyMadA : mad));

            if (median <= lowLoadThreshold && WindowRange() <= flatLimit)
            {
                _observedReverseEmptyA = median;
                if (_releaseCandidateTick == 0)
                {
                    _releaseCandidateTick = tick;
                    return;
                }

                if (ElapsedMs(_releaseCandidateTick, tick) >= ReleaseConfirmMs)
                {
                    SetStage(EpbCurrentStage.Released);
                    decision.ReleaseCompleted = true;
                    decision.StateChanged = true;
                    decision.Reason =
                        $"Released I={current:F3}A median={median:F3}A threshold={lowLoadThreshold:F3}A";
                }
            }
            else
            {
                _releaseCandidateTick = 0;
            }
        }

        private void EvaluateAbnormalHighPlateau(long tick, int elapsedMs, EpbAdaptiveDecision decision)
        {
            if (elapsedMs < 500 || !WindowIsStable(out var median, out _))
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
            _powerStartTick = startTick;
            _lastSampleTick = 0;
            _releaseCandidateTick = 0;
            _nearZeroStartTick = 0;
            _highPlateauStartTick = 0;
            _inrushIgnoreMs = Math.Max(0, inrushIgnoreMs);
            _absoluteMaxMs = Math.Max(1, absoluteMaxMs);
            _softLimitMs = 0;
            _consecutiveOverCurrent = 0;
            _softWarningRaised = false;
            _peakCurrentA = 0;
            _observedForwardEmptyA = 0;
            _observedForwardEmptyMadA = 0;
            _observedReverseEmptyA = 0;
        }

        private void AddWindow(long tick, double current)
        {
            _window.Enqueue(new Sample(tick, current));
            while (_window.Count > 0 && ElapsedMs(_window.Peek().Tick, tick) > StableWindowMs)
                _window.Dequeue();
        }

        private bool WindowIsStable(out double median, out double mad)
        {
            median = 0;
            mad = 0;
            if (_window.Count < 5) return false;
            var first = _window.Peek();
            var last = _window.Last();
            if (ElapsedMs(first.Tick, last.Tick) < StableWindowMs - 10) return false;
            var values = _window.Select(x => x.CurrentA).OrderBy(x => x).ToArray();
            median = Median(values);
            var medianValue = median;
            mad = Median(values.Select(x => Math.Abs(x - medianValue)).OrderBy(x => x).ToArray());
            return WindowRange() <= Math.Max(0.15, 6.0 * mad);
        }

        private double WindowRange()
        {
            if (_window.Count == 0) return double.MaxValue;
            var min = double.MaxValue;
            var max = double.MinValue;
            foreach (var sample in _window)
            {
                if (sample.CurrentA < min) min = sample.CurrentA;
                if (sample.CurrentA > max) max = sample.CurrentA;
            }

            return max - min;
        }

        private double WindowSlopeAperMs()
        {
            if (_window.Count < 2) return 0;
            var first = _window.Peek();
            var last = _window.Last();
            var elapsed = ElapsedMs(first.Tick, last.Tick);
            return elapsed <= 0 ? 0 : (last.CurrentA - first.CurrentA) / elapsed;
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
