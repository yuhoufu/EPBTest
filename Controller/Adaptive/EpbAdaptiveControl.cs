using System;
using System.Diagnostics;
using Config;
using IO.NI;

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
        Canceled = 3,
        /// <summary>
        /// 本圈因软件/状态瞬态未完成。硬件已安全断电，本圈必须作废且不计数，
        /// 调度器应在未来完整周期自动重试。
        /// </summary>
        SoftwareRecovery = 4
    }

    public enum PeriodOverrunKind
    {
        None = 0,
        CompletedOverrun = 1,
        ConsecutiveLimitReached = 2,
        HardLimitReached = 3
    }

    public sealed class EpbCycleOutcome
    {
        public EpbCycleOutcomeKind Kind { get; set; }
        public EpbCurrentStage Stage { get; set; }
        public string Reason { get; set; }
        public int ForwardElapsedMs { get; set; }
        public int ReverseElapsedMs { get; set; }
        /// <summary>正式计时器回调总耗时，仅供诊断，不用于卡钳超限归因。</summary>
        public long CallbackElapsedMs { get; set; }
        /// <summary>卡钳自身从上电到最终电机OFF的动作耗时，不含共享协调和写盘等待。</summary>
        public long PhysicalActionElapsedMs { get; set; }
        /// <summary>同槽液压、电源及全局屏障等待耗时。</summary>
        public long SharedCoordinationWaitMs { get; set; }
        public PeriodOverrunKind PeriodOverrunKind { get; set; }
        /// <summary>2kHz 全数据峰值；保留 PeakCurrentA 名称以兼容既有结果消费者。</summary>
        public double PeakCurrentA { get; set; }
        public double ControlPeakCurrentA { get; set; }
        public double TargetCurrentA { get; set; }
        public double CutoffCurrentA { get; set; }
        public double EstimatedSlopeAperMs { get; set; }
        public double PredictedPeakA { get; set; }
        public double PredictionLeadMs { get; set; }
        public double PeakErrorA { get; set; }
        public string CutoffReason { get; set; }
        public double ForwardEmptyCurrentA { get; set; }
        public double ReverseEmptyCurrentA { get; set; }
        /// <summary>本圈低于合格下限的平台证据；仅在正式圈提交后更新连续数。</summary>
        public bool ForwardLowPlateauCandidate { get; set; }
        /// <summary>本圈完整峰值达到永久过冲线；仅在正式圈提交后更新连续数。</summary>
        public bool ForwardPermanentOvershootCandidate { get; set; }
        public double ForwardAcceptableFloorA { get; set; }
        public double PermanentOvershootDeltaA { get; set; }
        public string SafetyPolicyVersion { get; set; } =
            EpbProgramSafetySettings.SafetyPolicyVersion;

        public bool IsSuccess =>
            Kind == EpbCycleOutcomeKind.Success || Kind == EpbCycleOutcomeKind.SuccessWithWarning;

        /// <summary>
        /// 机械夹紧与反向释放已经完成。即使完整峰值/持久化证据随后失败，
        /// 该圈仍必须进入卡钳实际动作与磨损计数。
        /// </summary>
        public bool MechanicalCycleCompleted => Stage == EpbCurrentStage.Released;

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

        public static EpbCycleOutcome SoftwareRecovery(EpbCurrentStage stage, string reason)
        {
            return new EpbCycleOutcome
            {
                Kind = EpbCycleOutcomeKind.SoftwareRecovery,
                Stage = stage,
                Reason = reason ?? "SoftwareRecovery"
            };
        }
    }

    /// <summary>
    /// DiagnosticOnly event emitted by the adaptive state machine.  It is an
    /// evidence overlay, not a control decision: consumers must not copy its
    /// message into Decision.Reason or use it to advance/rewind the stage.
    /// </summary>
    public sealed class EpbAdaptiveDiagnosticEvent
    {
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public int Channel { get; set; }
        public int CycleNumber { get; set; }
        public string Code { get; set; }
        public DateTime OccurredUtc { get; set; }
        public EpbCurrentStage Stage { get; set; }
        public double CurrentA { get; set; }
        public string Message { get; set; }
    }

    public sealed class EpbAdaptiveDecision
    {
        public bool ClampReached { get; set; }
        public bool ReleaseCompleted { get; set; }
        /// <summary>
        /// 仅诊断：本圈尚未形成空载基线，但样本已经进入历史负载范围。
        /// 该标志绝不能改变状态机阶段、故障连续数或断电动作。
        /// </summary>
        public bool DiagnosticWarning { get; set; }
        public string DiagnosticCode { get; set; }
        public EpbAdaptiveDiagnosticEvent DiagnosticEvent { get; set; }
        public bool LoadRiseEvidenceQualified { get; set; }
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
        public double ObservedFullRatePeakA { get; set; } = double.NaN;
        public double PredictionLeadMs { get; set; } = double.NaN;
        public string CutoffReason { get; set; }
        public int ReleaseCandidateElapsedMs { get; set; }
        public bool WindowQualified { get; set; }
        public FastSignalQualityFlags FastSignalQualityFlags { get; set; }
        public double FastRepresentativeA { get; set; } = double.NaN;
        public long BatchSequence { get; set; }

        public bool HasAction =>
            ClampReached || ReleaseCompleted || DiagnosticWarning || SoftWarning || HardFault || StateChanged;

        internal void Reset(EpbCurrentStage stage, double currentA)
        {
            ClampReached = false;
            ReleaseCompleted = false;
            DiagnosticWarning = false;
            DiagnosticCode = null;
            DiagnosticEvent = null;
            LoadRiseEvidenceQualified = false;
            SoftWarning = false;
            HardFault = false;
            StateChanged = false;
            Stage = stage;
            Reason = null;
            CurrentA = Math.Abs(currentA);
            ElapsedMs = 0;
            WindowSampleCount = 0;
            WindowSpanMs = 0;
            WindowMedianA = double.NaN;
            WindowMadA = double.NaN;
            WindowP10A = double.NaN;
            WindowP90A = double.NaN;
            ReleaseThresholdA = double.NaN;
            AllowedSpreadA = double.NaN;
            CutoffCurrentA = double.NaN;
            EstimatedSlopeAperMs = double.NaN;
            PredictedPeakA = double.NaN;
            ObservedFullRatePeakA = double.NaN;
            PredictionLeadMs = double.NaN;
            CutoffReason = null;
            ReleaseCandidateElapsedMs = 0;
            WindowQualified = false;
            FastSignalQualityFlags = FastSignalQualityFlags.None;
            FastRepresentativeA = double.NaN;
            BatchSequence = 0;
        }

        internal EpbAdaptiveDecision Copy()
        {
            return (EpbAdaptiveDecision)MemberwiseClone();
        }
    }

    /// <summary>
    /// 电流进展与断电代理确认的运行时安全参数，由程序级设置统一生成。
    /// </summary>
    public sealed class EpbAdaptiveSafetyLimits
    {
        public int ForwardProgressConfirmMs { get; set; } = 200;
        public double ForwardMinimumRiseSlopeAperMs { get; set; } = 0.001;
        public int ForwardProgressDeadlineMs { get; set; } = 3000;
        public int ForwardNearTargetConfirmMs { get; set; } = 200;
        public double ForwardAcceptableUndershootA { get; set; } = 0.8;
        public int ReverseProgressConfirmMs { get; set; } = 200;
        public double ReverseMinimumDecaySlopeAperMs { get; set; } = 0.001;
        public int ReverseProgressDeadlineMs { get; set; } = 2500;
        public double OffCurrentClearThresholdA { get; set; } = 0.1;
        public int OffCurrentClearTimeoutMs { get; set; } = 1000;

        public EpbAdaptiveSafetyLimits Normalized()
        {
            var forwardConfirm = Math.Max(200, ForwardProgressConfirmMs);
            var nearTargetConfirm = Math.Max(
                100,
                Math.Min(forwardConfirm, ForwardNearTargetConfirmMs));
            var reverseConfirm = Math.Max(200, ReverseProgressConfirmMs);
            return new EpbAdaptiveSafetyLimits
            {
                ForwardProgressConfirmMs = forwardConfirm,
                ForwardMinimumRiseSlopeAperMs = Math.Max(0.00001, ForwardMinimumRiseSlopeAperMs),
                ForwardProgressDeadlineMs = Math.Max(3000, Math.Max(forwardConfirm, ForwardProgressDeadlineMs)),
                ForwardNearTargetConfirmMs = nearTargetConfirm,
                ForwardAcceptableUndershootA = Math.Max(0.1, ForwardAcceptableUndershootA),
                ReverseProgressConfirmMs = reverseConfirm,
                ReverseMinimumDecaySlopeAperMs = Math.Max(0.00001, ReverseMinimumDecaySlopeAperMs),
                ReverseProgressDeadlineMs = Math.Max(2500, Math.Max(reverseConfirm, ReverseProgressDeadlineMs)),
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
        // “低目标平台”只能用于接近夹紧目标的尾段。低于目标 90% 的平台仍可能是
        // 机构中段负载变化，必须继续供电并交给正向进展硬截止保护，不能当作夹紧完成。
        private const double MinimumLowTargetPlateauFraction = 0.90;
        private const int WindowCapacity = 1024;

        private readonly object _gate = new object();
        private readonly SampleRing _window = new SampleRing(WindowCapacity);
        private readonly SampleRing _forwardEmptyWindow = new SampleRing(WindowCapacity);
        private readonly double[] _windowValues = new double[WindowCapacity];
        private readonly double[] _windowDeviations = new double[WindowCapacity];

        private EpbAdaptiveProfile _profile;
        private EpbCurrentStage _stage = EpbCurrentStage.Idle;
        private long _powerStartTick;
        private long _lastSampleTick;
        private long _releaseCandidateTick;
        private long _nearZeroStartTick;
        private int _inrushIgnoreMs;
        private int _absoluteMaxMs;
        private int _softLimitMs;
        private int _forwardProgressDeadlineOverrideMs;
        private int _reverseProgressDeadlineOverrideMs;
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
        private double _observedFullRatePeakA;
        private bool _loadRiseEvidenceQualified;
        private bool _rapidLoadRiseDiagnosticRaised;
        private long _loadRiseDropStartTick;
        private long _loadRiseStartTick;
        private long _forwardProgressStallStartTick;
        private EpbAdaptiveSafetyLimits _safetyLimits = new EpbAdaptiveSafetyLimits();
        private int _reverseNoModelDeadlineMs;
        private readonly double _daqFreshnessCutoffMs;

        public EpbAdaptiveCurrentStateMachine(
            EpbAdaptiveProfile profile,
            double daqFreshnessCutoffMs = 250)
        {
            _profile = profile?.Clone() ?? new EpbAdaptiveProfile();
            _daqFreshnessCutoffMs = Math.Max(1, daqFreshnessCutoffMs);
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

        public int RetainedWindowSampleCount
        {
            get
            {
                lock (_gate)
                    return _window.Count + _forwardEmptyWindow.Count;
            }
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
            EpbAdaptiveSafetyLimits safetyLimits = null,
            int forwardProgressDeadlineOverrideMs = 0)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
                _safetyLimits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
                _forwardProgressDeadlineOverrideMs = Math.Max(0, forwardProgressDeadlineOverrideMs);
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
            int reverseNoModelDeadlineMs = 0,
            int reverseProgressDeadlineOverrideMs = 0)
        {
            lock (_gate)
            {
                ResetDirection(startTick, inrushIgnoreMs, absoluteMaxMs);
                _safetyLimits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
                _reverseNoModelDeadlineMs = reverseNoModelDeadlineMs > 0
                    ? Math.Max(_safetyLimits.ReverseProgressConfirmMs, reverseNoModelDeadlineMs)
                    : 0;
                _reverseProgressDeadlineOverrideMs = Math.Max(0, reverseProgressDeadlineOverrideMs);
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

        public EpbAdaptiveDecision OnSample(
            long tick,
            double currentAmp,
            double observedFullRatePeakA = double.NaN)
        {
            return OnSampleReusable(
                tick,
                currentAmp,
                observedFullRatePeakA,
                default,
                new EpbAdaptiveDecision());
        }

        internal EpbAdaptiveDecision OnInvalidFastSignalReusable(
            long tick,
            double currentAmp,
            FastSignalQualityFlags qualityFlags,
            EpbAdaptiveDecision reusableDecision)
        {
            lock (_gate)
            {
                var decision = reusableDecision ?? throw new ArgumentNullException(nameof(reusableDecision));
                decision.Reset(_stage, currentAmp);
                decision.FastSignalQualityFlags = qualityFlags;
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                _lastSampleTick = tick;
                var current = double.IsNaN(currentAmp) || double.IsInfinity(currentAmp)
                    ? 0
                    : Math.Abs(currentAmp);
                _lastCurrentA = current;
                decision.CurrentA = current;
                decision.ElapsedMs = ElapsedMs(_powerStartTick, tick);
                return Fault(decision, $"FastPathSignalInvalid Quality={qualityFlags}");
            }
        }

        internal EpbAdaptiveDecision OnSampleReusable(
            long tick,
            double currentAmp,
            double observedFullRatePeakA,
            EpbAdaptiveDecision reusableDecision)
        {
            return OnSampleReusable(
                tick,
                currentAmp,
                observedFullRatePeakA,
                default,
                reusableDecision);
        }

        /// <summary>
        /// 处理快速样本以及同一捕获窗的全速率谷值后上升证据。
        /// 只有本圈 10ms 空载窗口，或身份/代次/新鲜度均有效的完整谷值后上升
        /// 证据，才能把 EmptyTravel 推进到 LoadRise。
        /// </summary>
        internal EpbAdaptiveDecision OnSampleReusable(
            long tick,
            double currentAmp,
            double observedFullRatePeakA,
            EpbLoadRiseEvidenceSnapshot loadRiseEvidence,
            EpbAdaptiveDecision reusableDecision)
        {
            lock (_gate)
            {
                var decision = reusableDecision ?? throw new ArgumentNullException(nameof(reusableDecision));
                decision.Reset(_stage, currentAmp);
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                var current = Math.Abs(currentAmp);
                if (!double.IsNaN(observedFullRatePeakA) &&
                    !double.IsInfinity(observedFullRatePeakA) &&
                    observedFullRatePeakA > _observedFullRatePeakA)
                    _observedFullRatePeakA = Math.Abs(observedFullRatePeakA);
                decision.ObservedFullRatePeakA = _observedFullRatePeakA > 0
                    ? _observedFullRatePeakA
                    : double.NaN;
                decision.LoadRiseEvidenceQualified = _loadRiseEvidenceQualified;

                // 全速率证据可能先于 10ms 控制回调形成；它只能在证据自身完整且
                // 新鲜时放行 LoadRise，任何旧 token、代次变化、零序列缓存都拒绝。
                if (_forwardDirection &&
                    HasQualifiedLoadRiseEvidence(loadRiseEvidence))
                {
                    _loadRiseEvidenceQualified = true;
                    if (_stage == EpbCurrentStage.EmptyTravel)
                    {
                        SetStage(EpbCurrentStage.LoadRise);
                        _loadRiseStartTick = tick;
                        decision.StateChanged = true;
                        decision.Reason = "LoadRiseFullRateValleyThenRise";
                    }
                }
                decision.LoadRiseEvidenceQualified = _loadRiseEvidenceQualified;
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
                {
                    if (_forwardDirection)
                    {
                        // 快速保护只负责立即断开正向输出。永久报警必须等待完整速率峰值、
                        // 反向释放和正式圈落盘均提交后，再按独立的连续圈策略确认。
                        var slope = PredictionSlopeAperMs();
                        var peak = Math.Max(current, _observedFullRatePeakA);
                        decision.SoftWarning = true;
                        CompleteForwardClamp(
                            decision,
                            current,
                            slope,
                            peak,
                            0,
                            "FastOverCurrentCutoff",
                            $"ForwardFastOverCurrentCutoff I={current:F3}A " +
                            $"Limit={overCurrentLimit:F3}A Peak={peak:F3}A");
                        return decision;
                    }

                    return Fault(decision,
                        $"OverCurrent3Samples I={current:F3}A Limit={overCurrentLimit:F3}A");
                }

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
                decision.LoadRiseEvidenceQualified = _loadRiseEvidenceQualified;
                return decision;
            }
        }

        /// <summary>
        /// 全速率负载上升证据的统一身份/新鲜度门禁。截断封口前可用于实时
        /// 进入 LoadRise；最终持久化另行要求 IsCutoffCovered。
        /// </summary>
        public static bool HasQualifiedLoadRiseEvidence(
            EpbLoadRiseEvidenceSnapshot evidence)
        {
            if (!evidence.IsQualified || !evidence.FullRateValleyThenRise)
                return false;
            if (evidence.CaptureId == Guid.Empty || !evidence.TokenValid ||
                !evidence.IdentityValid)
                return false;
            if (!evidence.IsGenerationMatched || !evidence.GenerationMatched ||
                evidence.DaqGeneration <= 0 || evidence.Generation != evidence.DaqGeneration)
                return false;
            if (evidence.CutoffSequence != evidence.CutoffAcceptedSequence)
                return false;
            if (!evidence.IsFresh || !evidence.Fresh || !evidence.FreshnessValid)
                return false;
            if (evidence.ProcessedSequence <= evidence.StartProcessedSequence)
                return false;
            if (evidence.ValleyEquivalentSampleCount < 8 ||
                evidence.ValleyWindowMs < 100.0 ||
                !evidence.ValleyWindowQualified)
                return false;
            if (evidence.RiseContinuousSampleCount < 8 ||
                evidence.RiseContinuousDurationMs < 4.0 ||
                !evidence.RiseContinuityQualified)
                return false;
            if (evidence.PostValleyRiseA <
                Math.Max(0.5, 4.0 * Math.Max(0, evidence.StableValleyMadA)))
                return false;
            var minimumSlope = evidence.MinimumRequiredSlopeAperMs > 0 &&
                               !double.IsNaN(evidence.MinimumRequiredSlopeAperMs) &&
                               !double.IsInfinity(evidence.MinimumRequiredSlopeAperMs)
                ? evidence.MinimumRequiredSlopeAperMs
                : 0.001;
            if (evidence.PostValleySlopeAperMs < minimumSlope ||
                evidence.LinearRiseSlopeAperMs < minimumSlope ||
                evidence.RobustRiseSlopeAperMs < minimumSlope)
                return false;
            if (!evidence.EvidenceAgeValid ||
                (evidence.MaximumEvidenceAgeMs > 0 &&
                 evidence.EvidenceAgeMs > evidence.MaximumEvidenceAgeMs))
                return false;
            return true;
        }

        /// <summary>
        /// Strict identity gate for a consumer that still owns the expected
        /// capture token.  Every identity field is checked against the frozen
        /// snapshot; run epoch is intentionally included here even though the
        /// DiagnosticOnly aggregation key excludes it.
        /// </summary>
        public static bool HasQualifiedLoadRiseEvidence(
            EpbLoadRiseEvidenceSnapshot evidence,
            PeakCaptureToken expectedToken)
        {
            if (expectedToken == null || !HasQualifiedLoadRiseEvidence(evidence))
                return false;
            return evidence.CaptureId == expectedToken.CaptureId &&
                   evidence.RunId == expectedToken.TestRunId &&
                   evidence.TestRunId == expectedToken.TestRunId &&
                   evidence.RunEpoch == expectedToken.RunEpoch &&
                   evidence.Channel == expectedToken.Channel &&
                   evidence.CycleNumber == expectedToken.CycleNumber &&
                   evidence.DaqGeneration == expectedToken.Generation &&
                   evidence.Generation == expectedToken.Generation &&
                   evidence.StartAcceptedSequence == expectedToken.StartAcceptedSequence &&
                   evidence.StartProcessedSequence == expectedToken.StartProcessedSequence;
        }

        /// <summary>兼容测试/诊断调用方的语义别名。</summary>
        public static bool IsLoadRiseEvidenceQualified(
            EpbLoadRiseEvidenceSnapshot evidence)
        {
            return HasQualifiedLoadRiseEvidence(evidence);
        }

        public EpbAdaptiveDecision CheckWatchdog(long nowTick)
        {
            return CheckWatchdogReusable(nowTick, new EpbAdaptiveDecision());
        }

        internal EpbAdaptiveDecision CheckWatchdogReusable(
            long nowTick,
            EpbAdaptiveDecision reusableDecision)
        {
            lock (_gate)
            {
                var decision = reusableDecision ?? throw new ArgumentNullException(nameof(reusableDecision));
                decision.Reset(_stage, _lastCurrentA);
                if (_stage == EpbCurrentStage.Idle ||
                    _stage == EpbCurrentStage.Hold ||
                    _stage == EpbCurrentStage.ClampReached ||
                    _stage == EpbCurrentStage.Released ||
                    _stage == EpbCurrentStage.Faulted)
                    return decision;

                decision.ElapsedMs = ElapsedMs(_powerStartTick, nowTick);
                if (!_forwardDirection)
                    TryApplyReverseWindowDiagnostics(nowTick, decision, out _);

                if ((_lastSampleTick == 0 && decision.ElapsedMs > _daqFreshnessCutoffMs) ||
                    (_lastSampleTick != 0 &&
                     ElapsedMs(_lastSampleTick, nowTick) > _daqFreshnessCutoffMs))
                    return Fault(
                        decision,
                        $"DaqSampleStale>{_daqFreshnessCutoffMs:0.#}ms");

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

                // 现场可能在涌流忽略期结束时已经越过历史负载上升阈值，本圈因而没有
                // 机会形成空行程窗口。历史基线只能用于诊断，不能冒充本圈证据；否则
                // 涌流尾部会被错误推进到 LoadRise，并触发后续“负载上升回落”硬故障。
                // 这里必须保持 EmptyTravel，直到本圈取得合格空载窗口（或后续完整
                // 速率证据直接达到目标，由下方快速夹紧保护处理）。
                if (_observedForwardEmptyA <= 0 &&
                    historicalBaselineAvailable &&
                    current >= provisionalLoadRiseThreshold &&
                    WindowSlopeAperMs() > 0.001)
                {
                    if (!_rapidLoadRiseDiagnosticRaised)
                    {
                        _rapidLoadRiseDiagnosticRaised = true;
                        decision.DiagnosticWarning = true;
                        decision.DiagnosticCode = "RapidLoadRiseWithoutObservedEmpty";
                        decision.DiagnosticEvent = new EpbAdaptiveDiagnosticEvent
                        {
                            Code = decision.DiagnosticCode,
                            OccurredUtc = DateTime.UtcNow,
                            Stage = _stage,
                            CurrentA = current,
                            Message =
                                $"RapidLoadRiseWithoutObservedEmpty I={current:F3}A " +
                                $"Threshold={provisionalLoadRiseThreshold:F3}A " +
                                "StateUnchanged=true Evidence=PerCycleEmptyBaselineMissing"
                        };
                        // The Inrush->EmptyTravel transition can occur in the
                        // same fast callback. It is a normal arming transition,
                        // not an action caused by this diagnostic. Preserve the
                        // control decision's existing Reason/StateChanged fields;
                        // the independent event above carries diagnostic text.
                    }
                }

                // 缺少本圈空载基线只能阻止“夹紧成功”归因，不能放宽堵转保护。
                // 当快速负载上升已经越过高负载区并形成约 200ms 欠目标平台时，
                // 无论当前仍停留在 EmptyTravel 还是已经进入 LoadRise，都必须在
                // 本窗口立即结束正向上电。连续圈策略只决定是否永久报警，不能
                // 把当前电机继续带载到 5/9 秒硬截止。
                var emptyTravelBaselineA = historicalBaselineAvailable
                    ? _profile.ForwardEmptyCurrentA
                    : Math.Max(1.0, _observedForwardEmptyA);
                if (TryFaultForwardUnderTargetHighLoadPlateau(
                        tick,
                        current,
                        Math.Max(_peakCurrentA, _observedFullRatePeakA),
                        emptyTravelBaselineA,
                        decision))
                    return;

                if (_observedForwardEmptyA > 0)
                {
                    // 本圈观测优先。历史画像只用于后续学习/审计，不能收紧本圈
                    // 的进入条件或替代缺失的本圈证据。
                    var baseline = _observedForwardEmptyA;
                    var baselineMad = _observedForwardEmptyMadA;
                    var loadRiseThreshold = baseline + Math.Max(0.5, 4.0 * baselineMad);
                    if (current >= loadRiseThreshold && WindowSlopeAperMs() > 0.001)
                    {
                        SetStage(EpbCurrentStage.LoadRise);
                        _loadRiseEvidenceQualified = true;
                        _loadRiseStartTick = tick;
                        decision.StateChanged = true;
                        decision.Reason = "LoadRise";
                    }
                }
            }

            if (_stage == EpbCurrentStage.EmptyTravel && current >= _forwardA)
            {
                // 某些卡钳负载建立极快，10ms代表样本会从空行程直接跨过目标，来不及
                // 先观察到 LoadRise。此时无论完整速率证据是否已经到达，都必须立即断电；
                // 但状态先后本身不能确认硬件故障。完整速率证据已到时记为快速夹紧，
                // 尚未到时记为FastRiseCandidate，后续仍由2kHz封口、断电清零和连续圈策略归因。
                if (_observedFullRatePeakA >= _forwardA)
                {
                    decision.SoftWarning = true;
                    CompleteForwardClamp(
                        decision,
                        current,
                        WindowSlopeAperMs(),
                        _observedFullRatePeakA,
                        0,
                        "FullRateRapidClamp",
                        $"ClampReachedBeforeLoadRiseByFullRateEvidence " +
                        $"I={current:F3}A Peak={_observedFullRatePeakA:F3}A Target={_forwardA:F3}A");
                    return;
                }
                decision.SoftWarning = true;
                CompleteForwardClamp(
                    decision,
                    current,
                    WindowSlopeAperMs(),
                    current,
                    0,
                    "FastRiseCandidate",
                    $"FastRiseCandidate ClampReachedBeforeLoadRiseAwaitingFullRateEvidence " +
                    $"I={current:F3}A Target={_forwardA:F3}A");
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
                var effectiveObservedPeakA = Math.Max(_loadRisePeakA, _observedFullRatePeakA);

                // 全数据峰值捕获基于 2kHz 原始样本。它达到目标时优先于 10ms 控制样本，
                // 避免短峰已达标、随后平台段却被误判为正向失速。
                if (_observedFullRatePeakA >= _forwardA)
                {
                    CompleteForwardClamp(
                        decision,
                        current,
                        slope,
                        _observedFullRatePeakA,
                        leadMs,
                        "FullRateTarget",
                        $"ClampReachedFullRatePeak Peak={_observedFullRatePeakA:F3}A " +
                        $"I={current:F3}A Target={_forwardA:F3}A");
                    return;
                }

                if (_loadRiseEvidenceQualified &&
                    _loadRisePeakA - current >= 2.0 && current < _forwardA)
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

                var stallProbeMs = _safetyLimits.ForwardNearTargetConfirmMs;
                var forwardEmptyBaselineA =
                    _profile.IsStable && _profile.ForwardEmptyCurrentA > 0
                        ? _profile.ForwardEmptyCurrentA
                        : Math.Max(1.0, _observedForwardEmptyA);
                var highLoadPlateauFloorA = Math.Max(
                    forwardEmptyBaselineA + 2.0,
                    _forwardA * 0.60);

                if (TryFaultForwardUnderTargetHighLoadPlateau(
                        tick,
                        current,
                        effectiveObservedPeakA,
                        forwardEmptyBaselineA,
                        decision))
                    return;

                if (_loadRiseStartTick != 0 &&
                    effectiveObservedPeakA >= highLoadPlateauFloorA &&
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
                    var acceptableFloorA = Math.Max(
                        0,
                        _forwardA - _safetyLimits.ForwardAcceptableUndershootA);
                    var lowTargetPlateauFloorA = Math.Min(
                        acceptableFloorA,
                        _forwardA * MinimumLowTargetPlateauFraction);
                    if (effectiveObservedPeakA >= acceptableFloorA)
                    {
                        ApplyWindowDiagnostics(decision, stallProbeStats);
                        decision.SoftWarning = true;
                        CompleteForwardClamp(
                            decision,
                            current,
                            stallProbeSlope,
                            effectiveObservedPeakA,
                            0,
                            "NearTargetPlateau",
                            $"ClampReachedNearTargetPlateau Peak={effectiveObservedPeakA:F3}A " +
                            $"I={current:F3}A Floor={acceptableFloorA:F3}A Target={_forwardA:F3}A " +
                            $"slope={stallProbeSlope:F6}A/ms confirm={stallProbeStats.SpanMs}ms");
                        return;
                    }

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
                        effectiveObservedPeakA >= lowTargetPlateauFloorA &&
                        progressStats.P90 >= lowTargetPlateauFloorA &&
                        progressSlope <= _safetyLimits.ForwardMinimumRiseSlopeAperMs)
                    {
                        ApplyWindowDiagnostics(decision, progressStats);
                        decision.EstimatedSlopeAperMs = progressSlope;
                        decision.SoftWarning = true;
                        CompleteForwardClamp(
                            decision,
                            current,
                            progressSlope,
                            effectiveObservedPeakA,
                            0,
                            "LowTargetPlateau",
                            $"ClampReachedLowTargetPlateau Peak={effectiveObservedPeakA:F3}A " +
                            $"I={current:F3}A AcceptableFloor={acceptableFloorA:F3}A " +
                            $"PlateauFloor={lowTargetPlateauFloorA:F3}A Target={_forwardA:F3}A " +
                            $"slope={progressSlope:F6}A/ms " +
                            $"limit={_safetyLimits.ForwardMinimumRiseSlopeAperMs:F6}A/ms " +
                            $"confirm={progressStats.SpanMs}ms");
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
                    var cutoffReason = directTargetReached ? "DirectTarget" : "PredictedPeak";
                    var reason =
                        directTargetReached
                            ? $"ClampReachedDirect I={current:F3}A Target={_forwardA:F3}A"
                            : $"ClampReachedPredicted I={current:F3}A Predicted={predictedPeak:F3}A " +
                              $"Target={_forwardA:F3}A Slope={slope:F4}A/ms Lead={leadMs:F2}ms Samples=3";
                    CompleteForwardClamp(
                        decision,
                        current,
                        slope,
                        predictedPeak,
                        leadMs,
                        cutoffReason,
                        reason);
                }
            }
        }

        private bool TryFaultForwardUnderTargetHighLoadPlateau(
            long tick,
            double current,
            double effectiveObservedPeakA,
            double forwardEmptyBaselineA,
            EpbAdaptiveDecision decision)
        {
            var highLoadPlateauFloorA = Math.Max(
                Math.Max(1.0, forwardEmptyBaselineA) + 2.0,
                _forwardA * 0.60);
            var acceptableFloorA = Math.Max(
                0,
                _forwardA - _safetyLimits.ForwardAcceptableUndershootA);
            var lowTargetPlateauFloorA = Math.Min(
                acceptableFloorA,
                _forwardA * MinimumLowTargetPlateauFraction);

            // 达到低目标合格线时仍由既有 NearTarget/LowTarget 逻辑完成本圈；
            // 这里只处理低于合格线、但已经足以造成堵转热风险的平台。
            // 历史累计峰值只用于诊断和证明本圈曾进入高负载区，不能作为
            // 当前平台的完成上限。一次接近目标的短峰后回落到 10.8A 等
            // 持续平台时，累计峰值不会下降；若用它拦截，堵转保护将一直
            // 被屏蔽到绝对上电截止。当前窗口的 P10/P90 才是连续平台证据。
            if (effectiveObservedPeakA < highLoadPlateauFloorA ||
                current >= lowTargetPlateauFloorA)
                return false;

            var confirmMs = _safetyLimits.ForwardProgressConfirmMs;
            if (!TryGetLinearSlope(
                    tick,
                    confirmMs + 20,
                    confirmMs,
                    out var stats,
                    out var slope) ||
                // P10 位于高负载区是比仅检查 P90 更稳健的连续平台证据；
                // 它同时满足 P90 高负载要求，并排除孤立高点和短促毛刺。
                stats.P10 < highLoadPlateauFloorA ||
                stats.P90 >= lowTargetPlateauFloorA ||
                slope > _safetyLimits.ForwardMinimumRiseSlopeAperMs)
                return false;

            ApplyWindowDiagnostics(decision, stats);
            decision.EstimatedSlopeAperMs = slope;
            Fault(
                decision,
                $"ForwardUnderTargetHighLoadStall Peak={effectiveObservedPeakA:F3}A " +
                $"I={current:F3}A HighLoadFloor={highLoadPlateauFloorA:F3}A " +
                $"CompletionFloor={lowTargetPlateauFloorA:F3}A Target={_forwardA:F3}A " +
                $"p10={stats.P10:F3}A p90={stats.P90:F3}A " +
                $"slope={slope:F6}A/ms " +
                $"limit={_safetyLimits.ForwardMinimumRiseSlopeAperMs:F6}A/ms " +
                $"confirm={stats.SpanMs}ms");
            return true;
        }

        private void CompleteForwardClamp(
            EpbAdaptiveDecision decision,
            double current,
            double slope,
            double peak,
            double leadMs,
            string cutoffReason,
            string reason)
        {
            SetStage(EpbCurrentStage.ClampReached);
            CutoffCurrentA = current;
            CutoffSlopeAperMs = slope;
            PredictedPeakA = peak;
            PredictionLeadMs = leadMs;
            CutoffReason = cutoffReason ?? string.Empty;
            decision.ClampReached = true;
            decision.StateChanged = true;
            decision.Stage = _stage;
            decision.CutoffCurrentA = current;
            decision.EstimatedSlopeAperMs = slope;
            decision.PredictedPeakA = peak;
            decision.PredictionLeadMs = leadMs;
            decision.CutoffReason = CutoffReason;
            decision.Reason = reason;
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

            // RevDecayLimitA 是项目允许的释放带宽。历史空载画像只能用于判断
            // 平台稳定度和审计，不能把释放阈值收紧到项目带宽以下。
            var lowLoadThreshold = _reverseDecayLimitA;
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
                // 没有本圈空载窗口时，历史模型不足以证明已进入负载上升；
                // 禁止把涌流尾部/中段平台升级为硬故障。后续仍由本圈进展期限、
                // 完整速率证据或目标截止做有界决策。
                if (_stage != EpbCurrentStage.EmptyTravel || !_loadRiseEvidenceQualified)
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
            _forwardProgressDeadlineOverrideMs = 0;
            _reverseProgressDeadlineOverrideMs = 0;
            _consecutiveOverCurrent = 0;
            _clampConfirmSamples = 0;
            _softWarningRaised = false;
            _peakCurrentA = 0;
            _lastCurrentA = 0;
            _observedForwardEmptyA = 0;
            _observedForwardEmptyMadA = 0;
            _observedReverseEmptyA = 0;
            _loadRisePeakA = 0;
            _observedFullRatePeakA = 0;
            _loadRiseEvidenceQualified = false;
            _rapidLoadRiseDiagnosticRaised = false;
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
            _window.Add(new Sample(tick, current));
            var retentionMs = Math.Max(
                WindowRetentionMs,
                Math.Max(
                    _safetyLimits.ForwardProgressConfirmMs,
                    _safetyLimits.ReverseProgressConfirmMs) + 50);
            _window.RemoveOlderThan(tick, retentionMs);
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
            _forwardEmptyWindow.Add(new Sample(tick, current));
            _forwardEmptyWindow.RemoveOlderThan(tick, WindowRetentionMs);
        }

        private bool ForwardEmptyWindowIsStable(out double median, out double mad)
        {
            median = 0;
            mad = 0;
            if (_forwardEmptyWindow.Count == 0) return false;
            var nowTick = _forwardEmptyWindow.Last.Tick;
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

        private bool TryGetWindowStats(
            SampleRing source,
            long nowTick,
            int windowMs,
            int minimumCoverageMs,
            int minimumSamples,
            out WindowStats stats)
        {
            stats = default;
            if (source == null || nowTick <= 0) return false;

            var count = 0;
            var firstTick = 0L;
            var lastTick = 0L;
            for (var i = 0; i < source.Count; i++)
            {
                var sample = source[i];
                if (ElapsedMs(sample.Tick, nowTick) > windowMs) continue;
                if (count == 0) firstTick = sample.Tick;
                lastTick = sample.Tick;
                _windowValues[count++] = sample.CurrentA;
            }
            if (count < minimumSamples) return false;

            var spanMs = ElapsedMs(firstTick, lastTick);
            if (spanMs < minimumCoverageMs) return false;

            Array.Sort(_windowValues, 0, count);
            var median = Median(_windowValues, count);
            for (var i = 0; i < count; i++)
                _windowDeviations[i] = Math.Abs(_windowValues[i] - median);
            Array.Sort(_windowDeviations, 0, count);
            var mad = Median(_windowDeviations, count);

            stats = new WindowStats(
                count,
                spanMs,
                median,
                mad,
                Quantile(_windowValues, count, 0.10),
                Quantile(_windowValues, count, 0.90),
                _windowValues[count - 1] - _windowValues[0]);
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
            var found = 0;
            var first = default(Sample);
            var last = default(Sample);
            for (var i = 0; i < _window.Count; i++)
            {
                var sample = _window[i];
                if (ElapsedMs(sample.Tick, _lastSampleTick) > StableWindowMs) continue;
                if (found++ == 0) first = sample;
                last = sample;
            }
            if (found < 2) return 0;
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

            return TryComputeLinearSlope(
                _window,
                tick,
                windowMs,
                WindowMinimumSamples,
                out slopeAperMs);
        }

        private int GetForwardProgressDeadlineMs()
        {
            if (_forwardProgressDeadlineOverrideMs > 0)
            {
                return Math.Min(
                    _absoluteMaxMs,
                    Math.Max(
                        _inrushIgnoreMs + _safetyLimits.ForwardProgressConfirmMs,
                        _forwardProgressDeadlineOverrideMs));
            }

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
            if (_reverseProgressDeadlineOverrideMs > 0)
            {
                return Math.Min(
                    _absoluteMaxMs,
                    Math.Max(
                        _inrushIgnoreMs + _safetyLimits.ReverseProgressConfirmMs,
                        _reverseProgressDeadlineOverrideMs));
            }

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
            if (!TryComputeLinearSlope(
                    _window,
                    _lastSampleTick,
                    PredictionSlopeWindowMs,
                    3,
                    out var slope))
                return 0;
            if (double.IsNaN(slope) || double.IsInfinity(slope) ||
                slope < MinimumPredictionSlopeAperMs)
                return 0;
            return Math.Min(MaximumPredictionSlopeAperMs, slope);
        }

        private static bool TryComputeLinearSlope(
            SampleRing source,
            long nowTick,
            int windowMs,
            int minimumSamples,
            out double slopeAperMs)
        {
            slopeAperMs = 0;
            var count = 0;
            var firstTick = 0L;
            double sumTime = 0;
            double sumCurrent = 0;
            for (var i = 0; i < source.Count; i++)
            {
                var sample = source[i];
                if (ElapsedMs(sample.Tick, nowTick) > windowMs) continue;
                if (count == 0) firstTick = sample.Tick;
                var timeMs = (sample.Tick - firstTick) * 1000.0 / Stopwatch.Frequency;
                sumTime += timeMs;
                sumCurrent += sample.CurrentA;
                count++;
            }
            if (count < minimumSamples) return false;

            var meanTime = sumTime / count;
            var meanCurrent = sumCurrent / count;
            double covariance = 0;
            double variance = 0;
            for (var i = 0; i < source.Count; i++)
            {
                var sample = source[i];
                if (ElapsedMs(sample.Tick, nowTick) > windowMs) continue;
                var timeMs = (sample.Tick - firstTick) * 1000.0 / Stopwatch.Frequency;
                var dt = timeMs - meanTime;
                covariance += dt * (sample.CurrentA - meanCurrent);
                variance += dt * dt;
            }
            if (variance <= 1e-9) return false;
            slopeAperMs = covariance / variance;
            return !double.IsNaN(slopeAperMs) && !double.IsInfinity(slopeAperMs);
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

        private static double Median(double[] values, int count)
        {
            if (values == null || count <= 0) return 0;
            var mid = count / 2;
            return count % 2 == 0
                ? (values[mid - 1] + values[mid]) / 2.0
                : values[mid];
        }

        private static double Quantile(double[] sortedValues, int count, double probability)
        {
            if (sortedValues == null || count <= 0) return 0;
            var p = Math.Max(0, Math.Min(1, probability));
            var index = (int)Math.Round(
                p * (count - 1),
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

        private sealed class SampleRing
        {
            private readonly Sample[] _items;
            private int _head;

            public SampleRing(int capacity)
            {
                _items = new Sample[capacity];
            }

            public int Count { get; private set; }

            public Sample this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Count)
                        throw new ArgumentOutOfRangeException(nameof(index));
                    return _items[(_head + index) % _items.Length];
                }
            }

            public Sample Last => Count == 0 ? default : this[Count - 1];

            public void Add(Sample sample)
            {
                if (Count < _items.Length)
                {
                    _items[(_head + Count) % _items.Length] = sample;
                    Count++;
                    return;
                }

                _items[_head] = sample;
                _head = (_head + 1) % _items.Length;
            }

            public void RemoveOlderThan(long nowTick, int retentionMs)
            {
                while (Count > 0 && ElapsedMs(this[0].Tick, nowTick) > retentionMs)
                {
                    _head = (_head + 1) % _items.Length;
                    Count--;
                }
            }

            public void Clear()
            {
                _head = 0;
                Count = 0;
            }
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
