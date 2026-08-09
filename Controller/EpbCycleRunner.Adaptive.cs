using System;
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Adaptive;
using Controller.Alarm;
using IO.NI;

namespace Controller
{
    internal enum AdaptiveTerminalOffResolution
    {
        Pending = 0,
        HardwareCompleted = 1,
        HardwareTimedOut = 2
    }

    /// <summary>
    /// 终态 OFF 的单一终态提交门。物理完成与独立截止只允许一个获胜；
    /// 迟到硬件回调只能补证据，不能再次迁移 Runner 状态。
    /// </summary>
    internal sealed class AdaptiveTerminalOffResolutionGate
    {
        private int _resolution;

        internal AdaptiveTerminalOffResolution Resolution =>
            (AdaptiveTerminalOffResolution)Volatile.Read(ref _resolution);

        internal bool TryCommitHardwareCompletion()
        {
            return Interlocked.CompareExchange(
                       ref _resolution,
                       (int)AdaptiveTerminalOffResolution.HardwareCompleted,
                       (int)AdaptiveTerminalOffResolution.Pending) ==
                   (int)AdaptiveTerminalOffResolution.Pending;
        }

        internal bool TryCommitHardwareTimeout()
        {
            return Interlocked.CompareExchange(
                       ref _resolution,
                       (int)AdaptiveTerminalOffResolution.HardwareTimedOut,
                       (int)AdaptiveTerminalOffResolution.Pending) ==
                   (int)AdaptiveTerminalOffResolution.Pending;
        }
    }

    public sealed partial class EpbCycleRunner
    {
        internal const int AdaptiveTerminalOffHardwareDeadlineMs = 100;
        private readonly EpbControlMode _epbControlMode = EpbControlMode.LegacyFixedTiming;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveCurrentStateMachine _adaptiveStateMachine;
        private readonly EpbAdaptiveProfile _adaptiveProfile;
        private readonly EpbProgramSafetySettings _programSafetySettings;
        private readonly Action<EpbAdaptiveProfile> _saveAdaptiveProfile;
        private readonly object _adaptiveGate = new object();
        private readonly EpbAdaptiveDecision _adaptiveSampleDecisionScratch = new EpbAdaptiveDecision();
        private readonly EpbAdaptiveDecision _adaptiveWatchdogDecisionScratch = new EpbAdaptiveDecision();
        private long _lastAdaptiveNormalTraceTick;
        private long _lastInvalidCutoffObservationLogTick;
        private static readonly long AdaptiveNormalTraceIntervalTicks =
            Math.Max(1, Stopwatch.Frequency / ReadAdaptiveTraceNormalRateHz());

        private TaskCompletionSource<EpbAdaptiveDecision> _adaptiveForwardCompletion;
        private TaskCompletionSource<EpbAdaptiveDecision> _adaptiveReverseCompletion;
        private int _adaptiveFaultLatched;
        private int _adaptiveWarningLatched;
        private bool _adaptiveSoftWarningSeen;
        private int _adaptiveForwardElapsedMs;
        private int _adaptiveReverseElapsedMs;
        private double _adaptiveForwardEmptyA;
        private double _adaptiveReverseEmptyA;
        private double _adaptiveForwardPeakA;
        private double _adaptiveForwardControlPeakA;
        private double _adaptiveDecisionPeakEvidenceLagMs = double.NaN;
        private DateTime _adaptiveDecisionPeakEvidenceThroughUtc = DateTime.MinValue;
        private int _adaptiveClampPeakCaptureStarted;
        private PeakCaptureToken _adaptivePeakCaptureToken;
        private long _lastFastBatchSequence;
        private long _lastFastRepresentativeBits;
        private int _lastFastQualityFlags;
        private string _adaptiveDirection = string.Empty;
        private readonly EpbAdaptiveSafetyLimits _adaptiveSafetyLimits;
        private int _adaptiveTerminalOffLatched;
        private Task _terminalOffCurrentVerificationTask = Task.CompletedTask;
        private double _adaptivePreEnergizationCurrentA = double.NaN;
        private EpbDoTimingObservation _adaptiveForwardDoTiming;

        private const double OffCurrentBaselineWindowMs = 500.0;
        private const double OffCurrentBaselineAllowanceA = 0.05;
        private const double MaxTrustedOffCurrentBaselineA = 0.20;
        private const double MaxAdaptiveOffCurrentThresholdA = 0.25;

        internal static bool IsForwardStallConfirmed(int streak, int confirmCycles)
        {
            return streak >= Math.Max(1, confirmCycles);
        }

        internal static bool IsPeakEvidenceMismatchConfirmed(int streak, int confirmCycles)
        {
            return streak >= Math.Max(1, confirmCycles);
        }

        internal static bool IsPeakEvidenceWindowComparable(
            DateTime decisionEvidenceThroughUtc,
            DateTime finalPeakAt,
            double timestampToleranceMs = 1.0)
        {
            if (decisionEvidenceThroughUtc == default ||
                decisionEvidenceThroughUtc == DateTime.MinValue ||
                finalPeakAt == default ||
                finalPeakAt == DateTime.MinValue)
                return false;
            var decisionUtc = decisionEvidenceThroughUtc.Kind == DateTimeKind.Utc
                ? decisionEvidenceThroughUtc
                : decisionEvidenceThroughUtc.ToUniversalTime();
            var peakUtc = finalPeakAt.Kind == DateTimeKind.Utc
                ? finalPeakAt
                : finalPeakAt.ToUniversalTime();
            return peakUtc <= decisionUtc.AddMilliseconds(Math.Max(0, timestampToleranceMs));
        }

        internal static bool IsPeakEvidenceLagExceeded(
            double decisionEvidenceLagMs,
            double maximumLagMs)
        {
            // “时间窗不可比较”只意味着本圈不能做快速/完整峰值偏差比较；
            // 它不等同于后台处理滞后。只有可量化且真实超过阈值的尾差才报滞后。
            return !double.IsNaN(decisionEvidenceLagMs) &&
                   !double.IsInfinity(decisionEvidenceLagMs) &&
                   decisionEvidenceLagMs > Math.Max(0, maximumLagMs);
        }

        internal static AdaptiveWarningCode? ClassifyPeakEvidenceDiagnostic(
            DateTime decisionEvidenceThroughUtc,
            DateTime finalPeakAt,
            double decisionEvidenceLagMs,
            double maximumLagMs)
        {
            if (decisionEvidenceThroughUtc == default ||
                decisionEvidenceThroughUtc == DateTime.MinValue ||
                finalPeakAt == default ||
                finalPeakAt == DateTime.MinValue)
                return AdaptiveWarningCode.PeakEvidenceTimestampMissing;
            return IsPeakEvidenceLagExceeded(decisionEvidenceLagMs, maximumLagMs)
                ? AdaptiveWarningCode.PeakEvidenceLagWarning
                : (AdaptiveWarningCode?)null;
        }

        internal static AdaptiveWarningCode? EvaluateCompletedPeakEvidence(
            DateTime decisionEvidenceThroughUtc,
            DateTime finalPeakAt,
            double decisionEvidenceLagMs,
            double maximumLagMs,
            out bool comparableWindow)
        {
            comparableWindow = IsPeakEvidenceWindowComparable(
                decisionEvidenceThroughUtc,
                finalPeakAt);
            // 完整峰值晚于快速判定窗口只影响“是否可比较”，不能改写由采集回调
            // 量化出的处理/证据滞后，否则会把正常的物理峰值演进误报为后台卡顿。
            return ClassifyPeakEvidenceDiagnostic(
                decisionEvidenceThroughUtc,
                finalPeakAt,
                decisionEvidenceLagMs,
                maximumLagMs);
        }

        internal static bool IsFullRatePeakCaptureValid(
            PeakCaptureResult capture,
            double maximumTailLagMs,
            out double evidenceTailLagMs)
        {
            evidenceTailLagMs = double.PositiveInfinity;
            if (capture == null || !capture.IsMatched || !capture.IsCutoffCovered)
                return false;
            var peak = capture.Peak;
            if (peak.SampleCount <= 0 || peak.MaxAmp <= 0 ||
                double.IsNaN(peak.MaxAmp) || double.IsInfinity(peak.MaxAmp) ||
                capture.LogicalCutoffUtc == default ||
                peak.LastSampleAt == default || peak.LastSampleAt == DateTime.MinValue)
                return false;

            var lastSampleUtc = peak.LastSampleAt.Kind == DateTimeKind.Utc
                ? peak.LastSampleAt
                : peak.LastSampleAt.ToUniversalTime();
            evidenceTailLagMs = (capture.LogicalCutoffUtc - lastSampleUtc).TotalMilliseconds;
            return evidenceTailLagMs >= 0 &&
                   evidenceTailLagMs <= Math.Max(0, maximumTailLagMs);
        }

        internal bool ResetTransientRunState()
        {
            lock (_adaptiveGate)
            {
                var changed = _adaptiveProfile?.ResetTransientFaultStreaks() == true;
                if (_adaptiveProfile != null)
                    _adaptiveStateMachine?.UpdateProfile(_adaptiveProfile);
                Interlocked.Exchange(ref _adaptiveFaultLatched, 0);
                Interlocked.Exchange(ref _adaptiveWarningLatched, 0);
                _adaptiveSoftWarningSeen = false;
                if (changed)
                    _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone());
                return changed;
            }
        }

        public FormalCycleFaultCommitResult CommitFormalCycleFaultEvidence(
            Guid testRunId,
            int cycleNumber)
        {
            lock (_adaptiveGate)
            {
                var result = FormalCycleFaultPolicy.Commit(
                    _adaptiveProfile,
                    LastCycleOutcome,
                    testRunId,
                    cycleNumber,
                    _adaptiveForwardStallConfirmCycles,
                    _adaptiveOvershootConfirmCycles);
                _adaptiveStateMachine?.UpdateProfile(_adaptiveProfile);
                try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
                catch (Exception ex)
                {
                    _log?.Warn(
                        $"EPB[{_channel}] 正式圈卡钳异常连续计数保存失败：{ex.Message}",
                        "EPB");
                }

                _log?.Info(
                    $"EPB[{_channel}] 正式圈异常证据已提交：RunId={testRunId:N} " +
                    $"Cycle={cycleNumber} Peak={LastCycleOutcome.PeakCurrentA:F3}A " +
                    $"Target={LastCycleOutcome.TargetCurrentA:F3}A " +
                    $"Slope={LastCycleOutcome.EstimatedSlopeAperMs:F6}A/ms " +
                    $"LowPlateau={LastCycleOutcome.ForwardLowPlateauCandidate} " +
                    $"LowStreak={result.ForwardLowPlateauStreak}/{_adaptiveForwardStallConfirmCycles} " +
                    $"Overshoot2A={LastCycleOutcome.ForwardPermanentOvershootCandidate} " +
                    $"OvershootStreak={result.ForwardOvershootStreak}/{_adaptiveOvershootConfirmCycles} " +
                    "Evidence=FullRateValid Persistence=Committed",
                    "EPB");
                return result;
            }
        }

        public EpbAdaptiveProfile CaptureAdaptiveProfile()
        {
            lock (_adaptiveGate)
                return _adaptiveProfile?.Clone() ?? new EpbAdaptiveProfile { Channel = _channel };
        }

        public void RestoreAdaptiveProfile(EpbAdaptiveProfile snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.Channel != 0 && snapshot.Channel != _channel)
                throw new InvalidOperationException(
                    $"不能用 EPB[{snapshot.Channel}] 模型恢复 EPB[{_channel}]。");

            lock (_adaptiveGate)
            {
                _adaptiveProfile.RestoreFrom(snapshot);
                _adaptiveProfile.Channel = _channel;
                _adaptiveStateMachine?.UpdateProfile(_adaptiveProfile);
                Interlocked.Exchange(ref _adaptiveFaultLatched, 0);
                Interlocked.Exchange(ref _adaptiveWarningLatched, 0);
                _adaptiveSoftWarningSeen = false;
                _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone());
            }
        }

        internal event Action<AdaptiveDecisionTraceSample> AdaptiveDecisionObserved;

        private static int ReadAdaptiveTraceNormalRateHz()
        {
            try
            {
                return int.TryParse(
                           ConfigurationManager.AppSettings["AdaptiveTraceNormalRateHz"],
                           out var value) &&
                       value >= 10 && value <= 50
                    ? value
                    : 25;
            }
            catch
            {
                return 25;
            }
        }

        internal static bool ShouldRecordAdaptiveTrace(
            long tick,
            bool important,
            long intervalTicks,
            ref long lastNormalTick)
        {
            if (important) return true;
            var previous = Interlocked.Read(ref lastNormalTick);
            if (previous > 0 && tick > previous && tick - previous < intervalTicks)
                return false;
            Interlocked.Exchange(ref lastNormalTick, tick);
            return true;
        }

        public EpbCycleOutcome LastCycleOutcome { get; private set; } =
            EpbCycleOutcome.Canceled(EpbCurrentStage.Idle, "NotStarted");

        private bool AdaptiveMonitoringEnabled =>
            _epbControlMode == EpbControlMode.AdaptiveCurrent || _adaptiveShadowMode;

        private static long AdaptiveNowTicks()
        {
            return Stopwatch.GetTimestamp();
        }

        private static int GetForwardAbsoluteMaxMs(int periodMs)
        {
            return Math.Min(10_000, Math.Max(6_000, (int)Math.Ceiling(Math.Max(0, periodMs) * 0.60)));
        }

        private static int GetReverseAbsoluteMaxMs(int periodMs)
        {
            return Math.Min(5_000, Math.Max(2_000, (int)Math.Ceiling(Math.Max(0, periodMs) * 0.35)));
        }

        private void CaptureAdaptivePreEnergizationCurrent()
        {
            var nowTick = AdaptiveNowTicks();
            var windowTicks = (long)Math.Ceiling(
                OffCurrentBaselineWindowMs * Stopwatch.Frequency / 1000.0);
            var baselineA = _currentBus[_channel].MedianAbsolute(
                nowTick - windowTicks,
                nowTick);
            if (double.IsNaN(baselineA) || double.IsInfinity(baselineA))
            {
                try { baselineA = Math.Abs(_readCurrent(_channel)); }
                catch { baselineA = double.NaN; }
            }

            _adaptivePreEnergizationCurrentA = baselineA;
        }

        internal static double ResolveOffCurrentClearThreshold(
            double configuredThresholdA,
            double preEnergizationCurrentA)
        {
            var configuredA =
                double.IsNaN(configuredThresholdA) ||
                double.IsInfinity(configuredThresholdA)
                    ? 0.1
                    : Math.Max(0.01, configuredThresholdA);
            if (double.IsNaN(preEnergizationCurrentA) ||
                double.IsInfinity(preEnergizationCurrentA) ||
                preEnergizationCurrentA < 0 ||
                preEnergizationCurrentA > MaxTrustedOffCurrentBaselineA)
                return configuredA;

            var baselineAwareA = Math.Min(
                MaxAdaptiveOffCurrentThresholdA,
                preEnergizationCurrentA + OffCurrentBaselineAllowanceA);
            return Math.Max(configuredA, baselineAwareA);
        }

        internal static EpbDoTimingObservation BuildAdaptiveDoTimingObservation(
            DateTime decisionUtc,
            HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null || telemetry.HardwareCompletedUtc == default)
                return null;
            return BuildAdaptiveDoTimingObservation(
                decisionUtc,
                telemetry.HardwareCompletedUtc,
                telemetry.TotalMs,
                telemetry.NiWriteMs);
        }

        internal static EpbDoTimingObservation BuildAdaptiveDoTimingObservation(
            DateTime decisionUtc,
            DateTime doWriteCompletedUtc,
            double totalMs,
            double niWriteMs)
        {
            if (decisionUtc == default || doWriteCompletedUtc == default)
                return null;
            var completedUtc = doWriteCompletedUtc.Kind == DateTimeKind.Utc
                ? doWriteCompletedUtc
                : doWriteCompletedUtc.ToUniversalTime();
            var normalizedDecisionUtc = decisionUtc.Kind == DateTimeKind.Utc
                ? decisionUtc
                : decisionUtc.ToUniversalTime();
            // DateTime.UtcNow 的墙钟粒度可能比 DO 的单毫秒执行更粗。以物理完成事件
            // 为锚点，用 worker 的单调总时长约束决策不得晚于实际入队估计时刻。
            var enqueuedEstimateUtc = completedUtc.AddMilliseconds(-Math.Max(0, totalMs));
            if (normalizedDecisionUtc > enqueuedEstimateUtc)
                normalizedDecisionUtc = enqueuedEstimateUtc;
            return new EpbDoTimingObservation
            {
                DecisionUtc = normalizedDecisionUtc,
                DoWriteStartedUtc = completedUtc.AddMilliseconds(
                    -Math.Max(0, niWriteMs)),
                DoWriteCompletedUtc = completedUtc
            };
        }

        private void BeginAdaptiveForwardMonitoring(int periodMs)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            Interlocked.Exchange(ref _adaptiveFaultLatched, 0);
            Interlocked.Exchange(ref _adaptiveWarningLatched, 0);
            _adaptiveSoftWarningSeen = false;
            _adaptiveForwardElapsedMs = 0;
            _adaptiveReverseElapsedMs = 0;
            _adaptiveForwardEmptyA = 0;
            _adaptiveReverseEmptyA = 0;
            _adaptiveForwardPeakA = 0;
            _adaptiveForwardControlPeakA = 0;
            _adaptiveDecisionPeakEvidenceLagMs = double.NaN;
            _adaptiveDecisionPeakEvidenceThroughUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0);
            Interlocked.Exchange(ref _adaptiveTerminalOffLatched, 0);
            _adaptiveForwardDoTiming = null;

            lock (_adaptiveGate)
            {
                _adaptiveForwardCompletion = NewAdaptiveCompletion();
                _adaptiveReverseCompletion = null;
            }

            double margin;
            lock (_marginLock) margin = _safetyMarginA;
            _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
            _adaptiveStateMachine.ArmForward(
                AdaptiveNowTicks(),
                Math.Max(100, _peakIgnoreMs),
                GetForwardAbsoluteMaxMs(periodMs),
                _posThrA,
                margin,
                _overshootAlarmDeltaA,
                _adaptiveSafetyLimits);
            _adaptiveDirection = "Forward";
        }

        private void CompleteAdaptiveForwardMonitoring(int measuredElapsedMs)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;
            _adaptiveForwardElapsedMs = _adaptiveForwardElapsedMs > 0
                ? _adaptiveForwardElapsedMs
                : Math.Max(0, measuredElapsedMs);
            _adaptiveForwardEmptyA = _adaptiveStateMachine.ObservedForwardEmptyA;
            _adaptiveForwardControlPeakA = Math.Max(
                _adaptiveForwardControlPeakA,
                _adaptiveStateMachine.PeakCurrentA);
            if (_acq == null)
                _adaptiveForwardPeakA = Math.Max(
                    _adaptiveForwardPeakA,
                    _adaptiveStateMachine.CutoffCurrentA);
            _adaptiveStateMachine.MarkHold();
        }

        private void BeginAdaptiveReverseMonitoring(int periodMs)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            _adaptiveForwardEmptyA = _adaptiveForwardEmptyA > 0
                ? _adaptiveForwardEmptyA
                : _adaptiveStateMachine.ObservedForwardEmptyA;
            if (_acq == null)
                _adaptiveForwardPeakA = Math.Max(
                    _adaptiveForwardPeakA,
                    _adaptiveStateMachine.CutoffCurrentA);

            lock (_adaptiveGate)
            {
                _adaptiveReverseCompletion = NewAdaptiveCompletion();
            }
            Interlocked.Exchange(ref _adaptiveTerminalOffLatched, 0);

            _adaptiveStateMachine.ArmReverse(
                AdaptiveNowTicks(),
                Math.Max(100, _peakIgnoreMs),
                GetReverseAbsoluteMaxMs(periodMs),
                RevDecayLimitA,
                _overshootAlarmDeltaA,
                _posThrA,
                _adaptiveSafetyLimits,
                RevDecayRigidMaxMs);
            _adaptiveDirection = "Reverse";
        }

        private void CompleteAdaptiveShadowCycle()
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            _adaptiveReverseEmptyA = _adaptiveStateMachine.ObservedReverseEmptyA;
            _adaptiveStateMachine.Disarm();

            // 影子阶段只有完整识别到夹紧和动态释放才作为有效学习样本。
            if (_adaptiveForwardElapsedMs <= 0 || _adaptiveReverseElapsedMs <= 0 ||
                _adaptiveForwardEmptyA <= 0 || _adaptiveReverseEmptyA <= 0)
            {
                _log?.Info(
                    $"EPB[{_channel}] 自适应影子圈未形成完整样本：" +
                    $"Fwd={_adaptiveForwardElapsedMs}ms Rev={_adaptiveReverseElapsedMs}ms " +
                    $"Iempty+={_adaptiveForwardEmptyA:F3}A Iempty-={_adaptiveReverseEmptyA:F3}A。",
                    "EPB");
                return;
            }

            PersistSuccessfulAdaptiveSample(
                _adaptiveForwardElapsedMs,
                _adaptiveReverseElapsedMs,
                _adaptiveForwardEmptyA,
                _adaptiveReverseEmptyA,
                "影子");
        }

        private void DisarmAdaptiveMonitoring()
        {
            if (Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0) != 0)
            {
                try
                {
                    if (_adaptivePeakCaptureToken != null)
                        _acq?.CancelEpbCurrentPeak(_adaptivePeakCaptureToken);
                    else
                        _acq?.CancelEpbCurrentPeak(_channel);
                }
                catch { }
            }
            _adaptivePeakCaptureToken = null;
            _adaptiveStateMachine?.Disarm();
            _adaptiveDirection = string.Empty;
            lock (_adaptiveGate)
            {
                _adaptiveForwardCompletion = null;
                _adaptiveReverseCompletion = null;
            }
        }

        private void ProcessAdaptiveSample(
            long tick,
            double currentAmp,
            DateTime sampleUtc,
            FastSignalQualityFlags qualityFlags)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            EpbAdaptiveDecision decision;
            try
            {
                if (FastPathTripClassifier.HasInvalidControlQuality(qualityFlags))
                {
                    decision = _adaptiveStateMachine.OnInvalidFastSignalReusable(
                        tick,
                        currentAmp,
                        qualityFlags,
                        _adaptiveSampleDecisionScratch);
                    decision.FastRepresentativeA = BitConverter.Int64BitsToDouble(
                        Volatile.Read(ref _lastFastRepresentativeBits));
                    decision.BatchSequence = Volatile.Read(ref _lastFastBatchSequence);
                    DispatchAdaptiveDecisionInSafetyOrder(
                        decision,
                        () => EnsureAdaptiveTerminalPowerOff(decision),
                        () => PublishAdaptiveTrace(tick, sampleUtc, decision),
                        () => HandleAdaptiveDecision(decision));
                    return;
                }

                var fullRatePeakA = double.NaN;
                var evidenceThroughUtc = DateTime.MinValue;
                if (_acq != null &&
                    string.Equals(_adaptiveDirection, "Forward", StringComparison.Ordinal) &&
                    Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 1) == 1)
                {
                    try
                    {
                        if (_adaptivePeakCaptureToken != null &&
                            _acq.TryPeekEpbCurrentPeak(_adaptivePeakCaptureToken, out var peak))
                        {
                            fullRatePeakA = peak.MaxAmp;
                            evidenceThroughUtc = peak.LastSampleAt.ToUniversalTime();
                        }
                    }
                    catch
                    {
                        // 峰值窥视失败时继续使用快速控制通道，不得中断采集回调。
                    }
                }

                decision = _adaptiveStateMachine.OnSampleReusable(
                    tick,
                    currentAmp,
                    fullRatePeakA,
                    _adaptiveSampleDecisionScratch);
                decision.FastSignalQualityFlags = qualityFlags;
                decision.FastRepresentativeA = BitConverter.Int64BitsToDouble(
                    Volatile.Read(ref _lastFastRepresentativeBits));
                decision.BatchSequence = Volatile.Read(ref _lastFastBatchSequence);
                if (decision.ClampReached && evidenceThroughUtc != DateTime.MinValue)
                {
                    var normalizedSampleUtc = sampleUtc.Kind == DateTimeKind.Utc
                        ? sampleUtc
                        : sampleUtc.ToUniversalTime();
                    lock (_adaptiveGate)
                    {
                        _adaptiveDecisionPeakEvidenceLagMs = Math.Max(
                            0,
                            (normalizedSampleUtc - evidenceThroughUtc).TotalMilliseconds);
                        _adaptiveDecisionPeakEvidenceThroughUtc = evidenceThroughUtc;
                    }
                }
            }
            catch
            {
                // 采集回调链路不得因诊断状态机异常而中断。
                return;
            }

            DispatchAdaptiveDecisionInSafetyOrder(
                decision,
                () => EnsureAdaptiveTerminalPowerOff(decision),
                () => PublishAdaptiveTrace(tick, sampleUtc, decision),
                () => HandleAdaptiveDecision(decision));
        }

        internal static void DispatchAdaptiveDecisionInSafetyOrder(
            EpbAdaptiveDecision decision,
            Action terminalOff,
            Action publishTrace,
            Action handleDecision)
        {
            if (decision == null) return;
            if (decision.HardFault || decision.ClampReached || decision.ReleaseCompleted)
                terminalOff?.Invoke();
            publishTrace?.Invoke();
            handleDecision?.Invoke();
        }

        private void PublishAdaptiveTrace(
            long tick,
            DateTime sampleUtc,
            EpbAdaptiveDecision decision)
        {
            if (decision == null) return;
            string action;
            if (decision.HardFault) action = "HardFault";
            else if (decision.ReleaseCompleted) action = "ReleaseCompleted";
            else if (decision.ClampReached) action = "ClampReached";
            else if (decision.SoftWarning) action = "SoftWarning";
            else if (decision.StateChanged) action = "StateChanged";
            else action = string.Empty;

            if (!ShouldRecordAdaptiveTrace(
                    tick,
                    action.Length != 0,
                    AdaptiveNormalTraceIntervalTicks,
                    ref _lastAdaptiveNormalTraceTick))
                return;

            var item = new AdaptiveDecisionTraceSample
            {
                SampleUtc = sampleUtc.Kind == DateTimeKind.Utc
                    ? sampleUtc
                    : sampleUtc.ToUniversalTime(),
                MonotonicTicks = tick,
                Channel = _channel,
                Direction = _adaptiveDirection,
                Stage = decision.Stage,
                ElapsedMs = decision.ElapsedMs,
                CurrentA = decision.CurrentA,
                WindowSampleCount = decision.WindowSampleCount,
                WindowSpanMs = decision.WindowSpanMs,
                WindowMedianA = decision.WindowMedianA,
                WindowMadA = decision.WindowMadA,
                WindowP10A = decision.WindowP10A,
                WindowP90A = decision.WindowP90A,
                ReleaseThresholdA = decision.ReleaseThresholdA,
                AllowedSpreadA = decision.AllowedSpreadA,
                CutoffCurrentA = decision.CutoffCurrentA,
                EstimatedSlopeAperMs = decision.EstimatedSlopeAperMs,
                PredictedPeakA = decision.PredictedPeakA,
                ObservedFullRatePeakA = decision.ObservedFullRatePeakA,
                PredictionLeadMs = decision.PredictionLeadMs,
                CutoffReason = decision.CutoffReason,
                ReleaseCandidateElapsedMs = decision.ReleaseCandidateElapsedMs,
                WindowQualified = decision.WindowQualified,
                Action = action,
                Reason = decision.Reason
            };
            try { AdaptiveDecisionObserved?.Invoke(item); }
            catch { /* 诊断订阅者不得影响控制线程 */ }
        }

        private void HandleAdaptiveDecision(EpbAdaptiveDecision decision)
        {
            if (decision == null || !decision.HasAction) return;
            var infrastructureOpenCircuit = TryReclassifyInfrastructureOpenCircuit(decision);
            EnsureAdaptiveTerminalPowerOff(decision);

            if (decision.StateChanged &&
                decision.Stage == EpbCurrentStage.LoadRise &&
                string.Equals(_adaptiveDirection, "Forward", StringComparison.Ordinal))
                EnsureAdaptiveClampPeakCaptureStarted();

            if (decision.SoftWarning)
            {
                _adaptiveSoftWarningSeen = true;
                var deferLowPlateauWarning =
                    string.Equals(
                        decision.CutoffReason,
                        "LowTargetPlateau",
                        StringComparison.Ordinal);
                if (!deferLowPlateauWarning &&
                    Interlocked.Exchange(ref _adaptiveWarningLatched, 1) == 0)
                    RaiseAdaptiveWarning(decision.Reason);
            }

            if (decision.ClampReached)
            {
                EnsureAdaptiveClampPeakCaptureStarted();
                _adaptiveForwardElapsedMs = decision.ElapsedMs;
                if (_acq == null)
                    _adaptiveForwardPeakA = Math.Max(_adaptiveForwardPeakA, decision.CurrentA);
                if (_epbControlMode != EpbControlMode.AdaptiveCurrent)
                {
                    TaskCompletionSource<EpbAdaptiveDecision> completion;
                    lock (_adaptiveGate) completion = _adaptiveForwardCompletion;
                    completion?.TrySetResult(decision.Copy());
                }
            }

            if (decision.ReleaseCompleted)
            {
                _adaptiveReverseElapsedMs = decision.ElapsedMs;
                _adaptiveReverseEmptyA = _adaptiveStateMachine.ObservedReverseEmptyA;
                if (_epbControlMode != EpbControlMode.AdaptiveCurrent)
                {
                    TaskCompletionSource<EpbAdaptiveDecision> completion;
                    lock (_adaptiveGate) completion = _adaptiveReverseCompletion;
                    completion?.TrySetResult(decision.Copy());
                }
            }

            if (!decision.HardFault) return;

            if (_epbControlMode != EpbControlMode.AdaptiveCurrent)
            {
                if (Interlocked.Exchange(ref _adaptiveFaultLatched, 1) == 0)
                    RaiseAdaptiveWarning("【影子硬故障，不执行停机】" + decision.Reason);
                return;
            }

            if (Interlocked.Exchange(ref _adaptiveFaultLatched, 1) != 0) return;

            if (infrastructureOpenCircuit)
            {
                NotifyRecoverableFaultSafely(decision.Reason);
                return;
            }

            if (decision.Reason?.IndexOf("DaqSampleStale", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                NotifyRecoverableFaultSafely(decision.Reason);
                return;
            }

            // 快速信号相关故障已经先完成断电，但要等 2kHz 证据封口后再发布唯一的最终报警。
            if (FastPathTripClassifier.IsFastSignalDependentFault(decision.Reason)) return;

            var alarmReason = "AdaptiveHardFault " + decision.Reason;
            ObserveAdaptiveBackground(Task.Run(() =>
            {
                NotifyAlarmSafely(alarmReason);
            }), "AdaptiveHardFaultNotification");
        }

        private void EnsureAdaptiveTerminalPowerOff(EpbAdaptiveDecision decision)
        {
            if (decision == null ||
                (!decision.HardFault && !decision.ClampReached && !decision.ReleaseCompleted) ||
                _epbControlMode != EpbControlMode.AdaptiveCurrent)
                return;
            if (Interlocked.Exchange(ref _adaptiveTerminalOffLatched, 1) != 0) return;

            // 状态机在下一批会复用 scratch decision；异步完成链只能持有冻结副本。
            var decisionUtc = DateTime.UtcNow;
            var terminalDecision = decision.Copy();
            var reason = string.IsNullOrWhiteSpace(decision.Reason)
                ? decision.Stage.ToString()
                : decision.Reason;
            var direction = _adaptiveDirection;
            var resolution = new AdaptiveTerminalOffResolutionGate();
            var lifecycle = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _terminalOffCurrentVerificationTask, lifecycle.Task);

            var submitStarted = Stopwatch.GetTimestamp();
            Guid commandId;
            var accepted = false;
            try
            {
                accepted = TrySubmitCommandOffHighPriority(
                    telemetry =>
                    {
                        if (!resolution.TryCommitHardwareCompletion())
                        {
                            RecordLateAdaptiveTerminalOffCompletion(
                                telemetry,
                                reason,
                                resolution.Resolution);
                            return;
                        }
                        HandleAdaptiveTerminalOffCompletion(
                            telemetry,
                            reason,
                            direction,
                            terminalDecision,
                            lifecycle,
                            decisionUtc);
                    },
                    out commandId);
            }
            catch
            {
                commandId = Guid.Empty;
                accepted = false;
            }

            var submissionElapsedMs =
                (Stopwatch.GetTimestamp() - submitStarted) * 1000.0 / Stopwatch.Frequency;
            if (accepted)
            {
                var deadlineTask = MonitorAcceptedTerminalOffDeadlineAsync(
                    resolution,
                    AdaptiveTerminalOffHardwareDeadlineMs,
                    elapsedMs => HandleAdaptiveTerminalOffTimeout(
                        terminalDecision,
                        reason,
                        commandId,
                        elapsedMs,
                        lifecycle,
                        decisionUtc));
                ObserveAdaptiveBackground(deadlineTask, "AdaptiveTerminalOffHardwareDeadline");
                return;
            }

            _manager?.RecordTerminalOffCommand(
                _channel,
                reason,
                false,
                submissionElapsedMs,
                new EpbDoTimingObservation { DecisionUtc = decisionUtc });
            _manager?.QueueElectricalGroupEmergencyShutdownFromDaqControl(
                _channel,
                $"TerminalOffAdmissionRejected CommandId={commandId:N} Reason={reason}");
            CompleteAdaptiveDecisionAfterTerminalOff(
                terminalDecision,
                $"TerminalOffAdmissionRejected CommandId={commandId:N} Reason={reason}");
            lifecycle.TrySetResult(false);
            ObserveAdaptiveBackground(Task.Run(() =>
            {
                _log?.Error(
                    $"EPB[{_channel}] 终态高优先级断电未获有界队列接纳，已立即提交电源组联锁。" +
                    $"CommandId={commandId:N} Reason={reason} SubmissionElapsed={submissionElapsedMs:F3}ms",
                    "EPB");
                NotifyAlarmSafely(
                    $"AdaptiveHardFault TerminalOffAdmissionRejected {reason}");
            }), "TerminalOffAdmissionFailureNotification");
        }

        internal static async Task<bool> MonitorAcceptedTerminalOffDeadlineAsync(
            AdaptiveTerminalOffResolutionGate resolution,
            int deadlineMs,
            Action<double> onTimeout)
        {
            if (resolution == null) throw new ArgumentNullException(nameof(resolution));
            var boundedDeadlineMs = Math.Max(1, deadlineMs);
            var started = Stopwatch.GetTimestamp();
            while (resolution.Resolution == AdaptiveTerminalOffResolution.Pending)
            {
                var elapsedMs =
                    (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs >= boundedDeadlineMs) break;
                await Task.Delay(Math.Max(
                        1,
                        Math.Min(10, (int)Math.Ceiling(boundedDeadlineMs - elapsedMs))))
                    .ConfigureAwait(false);
            }

            if (!resolution.TryCommitHardwareTimeout()) return false;
            var finalElapsedMs =
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            try { onTimeout?.Invoke(finalElapsedMs); }
            catch
            {
                // resolution 已进入不可逆超时终态；上层实际处理函数逐项隔离，
                // 测试/诊断观察者异常也不得重新开放物理完成迁移。
            }
            return true;
        }

        private void HandleAdaptiveTerminalOffTimeout(
            EpbAdaptiveDecision terminalDecision,
            string reason,
            Guid commandId,
            double elapsedMs,
            TaskCompletionSource<bool> lifecycle,
            DateTime decisionUtc)
        {
            var timeoutReason =
                $"TerminalOffHardwareTimeout CommandId={commandId:N} " +
                $"Deadline={AdaptiveTerminalOffHardwareDeadlineMs}ms Elapsed={elapsedMs:F3}ms " +
                $"Reason={reason}";
            try
            {
                _manager?.RecordTerminalOffCommand(
                    _channel,
                    reason,
                    false,
                    elapsedMs,
                    new EpbDoTimingObservation { DecisionUtc = decisionUtc });
            }
            catch { }
            try
            {
                _manager?.QueueElectricalGroupEmergencyShutdownFromDaqControl(
                    _channel,
                    timeoutReason);
            }
            catch { }
            CompleteAdaptiveDecisionAfterTerminalOff(terminalDecision, timeoutReason);
            lifecycle?.TrySetResult(false);
            try
            {
                _log?.Error(
                    $"EPB[{_channel}] 已接纳终态OFF在硬截止内无物理完成，" +
                    $"已触发电源组断能并以硬故障完成等待。{timeoutReason}",
                    "EPB");
            }
            catch { }
            try
            {
                NotifyAlarmSafely("AdaptiveHardFault " + timeoutReason);
            }
            catch { }
        }

        private void RecordLateAdaptiveTerminalOffCompletion(
            HighPriorityDoTelemetry telemetry,
            string reason,
            AdaptiveTerminalOffResolution committedResolution)
        {
            // DoController 的全局 HighPriorityOffCompleted 仍会保存硬件完成时间、结果及
            // LateHardwareSuccess 证据；Runner 这里只写补充诊断，禁止 RecordTerminalOffCommand、
            // BeginTerminalOffCurrentVerification 或任何 forward/reverse TCS 再提交。
            try { _manager?.RecordAdaptiveTerminalOffLateEvidence(telemetry, reason); }
            catch { }
            try
            {
                _log?.Warn(
                    $"EPB[{_channel}] 收到终态OFF迟到物理回调，仅补证据，不再迁移状态。" +
                    $"CommandId={telemetry?.CommandId:N} Result={telemetry?.Result} " +
                    $"Committed={committedResolution} Total={telemetry?.TotalMs ?? 0:F3}ms " +
                    $"Reason={reason}",
                    "DO性能");
            }
            catch { }
        }

        private void HandleAdaptiveTerminalOffCompletion(
            HighPriorityDoTelemetry telemetry,
            string reason,
            string direction,
            EpbAdaptiveDecision terminalDecision,
            TaskCompletionSource<bool> lifecycle,
            DateTime decisionUtc)
        {
            var commandElapsedMs = telemetry?.TotalMs ?? 0;
            var doTiming = BuildAdaptiveDoTimingObservation(decisionUtc, telemetry);
            var commandSucceeded = CompleteSubmittedTerminalOffWithEscalation(
                telemetry,
                () => _manager?.QueueElectricalGroupEmergencyShutdownFromDaqControl(
                    _channel,
                    $"TerminalOffHardwareFailed CommandId={telemetry?.CommandId:N} Reason={reason}"));
            _manager?.RecordTerminalOffCommand(
                _channel,
                reason,
                commandSucceeded,
                commandElapsedMs,
                doTiming);

            if (string.Equals(direction, "Forward", StringComparison.Ordinal) &&
                commandSucceeded)
            {
                lock (_adaptiveGate)
                    _adaptiveForwardDoTiming = doTiming?.Clone();
            }

            if (!commandSucceeded)
            {
                CompleteAdaptiveDecisionAfterTerminalOff(
                    terminalDecision,
                    $"TerminalOffHardwareFailed CommandId={telemetry?.CommandId:N} Reason={reason}");
                lifecycle?.TrySetResult(false);
                _log?.Error(
                    $"EPB[{_channel}] 终态高优先级断电物理执行失败，已触发电源组联锁。" +
                    $"CommandId={telemetry?.CommandId:N} Reason={reason} " +
                    $"QueueWait={telemetry?.QueueWaitMs ?? 0:F3}ms " +
                    $"NIWrite={telemetry?.NiWriteMs ?? 0:F3}ms Total={commandElapsedMs:F3}ms",
                    "EPB");
                NotifyAlarmSafely(
                    $"AdaptiveHardFault TerminalOffHardwareFailed {reason}");
                return;
            }

            _log?.Info(
                $"EPB[{_channel}] 终态断电物理写入已完成。" +
                $"CommandId={telemetry.CommandId:N} Reason={reason} " +
                $"QueueWait={telemetry.QueueWaitMs:F3}ms NIWrite={telemetry.NiWriteMs:F3}ms " +
                $"Total={commandElapsedMs:F3}ms PhysicalOffStatus=NotMeasured",
                "EPB");
            var verificationTask = BeginTerminalOffCurrentVerification(reason, direction);
            verificationTask.ContinueWith(
                completed =>
                {
                    var verified = completed.Status == TaskStatus.RanToCompletion &&
                                   completed.Result;
                    CompleteAdaptiveDecisionAfterTerminalOff(
                        terminalDecision,
                        verified
                            ? null
                            : $"TerminalOffCurrentNotCleared CommandId={telemetry.CommandId:N} " +
                              $"Reason={reason}");
                    lifecycle?.TrySetResult(verified);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void CompleteAdaptiveDecisionAfterTerminalOff(
            EpbAdaptiveDecision decision,
            string terminalFailure)
        {
            if (decision == null) return;
            var completed = decision.Copy();
            if (!string.IsNullOrWhiteSpace(terminalFailure))
            {
                completed.ClampReached = false;
                completed.ReleaseCompleted = false;
                completed.SoftWarning = false;
                completed.HardFault = true;
                completed.StateChanged = true;
                completed.Stage = EpbCurrentStage.Faulted;
                completed.Reason = terminalFailure;
            }

            TaskCompletionSource<EpbAdaptiveDecision> forward;
            TaskCompletionSource<EpbAdaptiveDecision> reverse;
            lock (_adaptiveGate)
            {
                forward = _adaptiveForwardCompletion;
                reverse = _adaptiveReverseCompletion;
            }

            if (completed.HardFault)
            {
                forward?.TrySetResult(completed);
                reverse?.TrySetResult(completed);
                return;
            }
            if (completed.ClampReached) forward?.TrySetResult(completed);
            if (completed.ReleaseCompleted) reverse?.TrySetResult(completed);
        }

        internal static bool CompleteSubmittedTerminalOffWithEscalation(
            HighPriorityDoTelemetry telemetry,
            Action emergencyShutdown)
        {
            var succeeded = telemetry?.Result == true;
            if (!succeeded)
            {
                try { emergencyShutdown?.Invoke(); }
                catch
                {
                    // 联锁路径本身不得反向污染物理命令结果。
                }
            }
            return succeeded;
        }

        private Task<bool> BeginTerminalOffCurrentVerification(string reason, string direction)
        {
            var configuredThresholdA = _adaptiveSafetyLimits.OffCurrentClearThresholdA;
            var baselineA = _adaptivePreEnergizationCurrentA;
            var thresholdA = ResolveOffCurrentClearThreshold(
                configuredThresholdA,
                baselineA);
            var timeoutMs = _adaptiveSafetyLimits.OffCurrentClearTimeoutMs;
            var verificationTask = Task.Run(async () =>
            {
                try
                {
                    var verification = await PollOffCurrentUntilClearAsync(
                            ReadOffCurrentSample,
                            thresholdA,
                            timeoutMs,
                            20,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var currentA = verification.CurrentA;
                    if (!verification.SampleFresh)
                    {
                        var staleVerificationUtc = DateTime.UtcNow;
                        _manager?.RecordTerminalOffCurrentVerification(
                            _channel,
                            currentA,
                            thresholdA,
                            verification.ElapsedMs,
                            false,
                            staleVerificationUtc);
                        var powerEvidence = string.Empty;
                        if (_manager != null &&
                            _manager.TryGetFreshPowerSupplyCurrent(
                                _channel,
                                out var groupCurrentA,
                                out var powerAgeMs,
                                out var powerOutputEnabled))
                            powerEvidence =
                                $" PowerGroupIOut={groupCurrentA:F3}A " +
                                $"PowerTelemetryAge={powerAgeMs:F1}ms Output={powerOutputEnabled}";
                        var staleReason =
                            $"OffCurrentUnverifiableDaqStale AgeMs={verification.SampleAgeMs:F1}" +
                            powerEvidence;
                        _manager?.RequestElectricalGroupEmergencyShutdown(_channel, staleReason);
                        _log?.Error(
                            $"EPB[{_channel}] DAQ样本陈旧，无法确认断电电流；按失效安全触发电源组联锁：" +
                            $"LastCurrent={currentA:F3}A SampleAge={verification.SampleAgeMs:F1}ms " +
                            $"Reason={reason} PhysicalOffStatus=NotMeasured",
                            "EPB");
                        NotifyAlarmSafely("AdaptiveHardFault " + staleReason);
                        return false;
                    }
                    var cleared = VerifyOffCurrentOrEscalate(
                        currentA,
                        thresholdA,
                        () => _manager?.RequestElectricalGroupEmergencyShutdown(
                            _channel,
                            $"OffCurrentNotCleared Current={currentA:F3}A Threshold={thresholdA:F3}A"));
                    var completedVerificationUtc = DateTime.UtcNow;
                    _manager?.RecordTerminalOffCurrentVerification(
                        _channel,
                        currentA,
                        thresholdA,
                        verification.ElapsedMs,
                        cleared,
                        completedVerificationUtc);
                    if (cleared)
                    {
                        if (string.Equals(direction, "Forward", StringComparison.Ordinal))
                        {
                            lock (_adaptiveGate)
                            {
                                if (_adaptiveForwardDoTiming != null)
                                    _adaptiveForwardDoTiming.CurrentClearedUtc = completedVerificationUtc;
                            }
                        }
                        _log?.Info(
                            $"EPB[{_channel}] 断电电流代理确认通过：" +
                            $"ElectricalCurrentCleared=true Current={currentA:F3}A " +
                            $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                            $"PreEnergizationBaseline={baselineA:F3}A " +
                            $"EffectiveThreshold={thresholdA:F3}A Wait={verification.ElapsedMs}ms " +
                            "PhysicalOffStatus=NotMeasured",
                            "EPB");
                        return true;
                    }

                    _log?.Error(
                        $"EPB[{_channel}] 断电后电流未清零，立即触发电源组联锁：" +
                        $"ElectricalCurrentCleared=false Current={currentA:F3}A " +
                        $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                        $"PreEnergizationBaseline={baselineA:F3}A " +
                        $"EffectiveThreshold={thresholdA:F3}A Wait={verification.ElapsedMs}ms " +
                        $"Reason={reason} PhysicalOffStatus=NotMeasured",
                        "EPB");
                    NotifyAlarmSafely(
                        $"AdaptiveHardFault OffCurrentNotCleared " +
                        $"Current={currentA:F3}A " +
                        $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                        $"PreEnergizationBaseline={baselineA:F3}A " +
                        $"EffectiveThreshold={thresholdA:F3}A");
                    return false;
                }
                catch (Exception ex)
                {
                    var failedVerificationUtc = DateTime.UtcNow;
                    _manager?.RecordTerminalOffCurrentVerification(
                        _channel,
                        double.NaN,
                        thresholdA,
                        timeoutMs,
                        false,
                        failedVerificationUtc);
                    _log?.Error(
                        $"EPB[{_channel}] 断电电流代理确认无法完成，按失效安全触发电源组联锁：" +
                        $"{ex.Message} PhysicalOffStatus=NotMeasured",
                        "EPB");
                    _manager?.RequestElectricalGroupEmergencyShutdown(
                        _channel,
                        "OffCurrentVerificationFailed " + ex.Message);
                    NotifyAlarmSafely(
                        "AdaptiveHardFault OffCurrentVerificationFailed " + ex.Message);
                    return false;
                }
            });
            ObserveAdaptiveBackground(verificationTask, "TerminalOffCurrentVerification");
            return verificationTask;
        }

        internal static bool ShouldReclassifyOpenCircuit(
            string decisionReason,
            bool infrastructureTransition)
        {
            return infrastructureTransition &&
                   !string.IsNullOrWhiteSpace(decisionReason) &&
                   decisionReason.IndexOf(
                       "OpenCircuitOrOutputFault",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryReclassifyInfrastructureOpenCircuit(EpbAdaptiveDecision decision)
        {
            if (decision == null || _manager == null || !decision.HardFault ||
                string.IsNullOrWhiteSpace(decision.Reason) ||
                decision.Reason.IndexOf(
                    "OpenCircuitOrOutputFault",
                    StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (!_manager.IsInfrastructureTransitionForChannel(_channel, out var transitionReason) ||
                !ShouldReclassifyOpenCircuit(decision.Reason, true))
                return false;

            var original = decision.Reason;
            decision.Reason =
                $"InfrastructureTransitionOpenCircuit Channel={_channel} " +
                $"Transition={transitionReason} Original={original}";
            _log?.Warn(
                $"EPB[{_channel}] 近零电流发生在设备计划恢复/供电许可缺失窗口，" +
                $"本圈按软件恢复作废，不累计卡钳开路报警。{decision.Reason}",
                "EPB");
            return true;
        }

        internal Task GetTerminalOffCurrentVerificationTask()
        {
            return Volatile.Read(ref _terminalOffCurrentVerificationTask) ?? Task.CompletedTask;
        }

        internal readonly struct OffCurrentClearResult
        {
            public OffCurrentClearResult(
                bool cleared,
                double currentA,
                int elapsedMs,
                bool sampleFresh = true,
                double sampleAgeMs = 0)
            {
                Cleared = cleared;
                CurrentA = currentA;
                ElapsedMs = elapsedMs;
                SampleFresh = sampleFresh;
                SampleAgeMs = sampleAgeMs;
            }

            public bool Cleared { get; }
            public double CurrentA { get; }
            public int ElapsedMs { get; }
            public bool SampleFresh { get; }
            public double SampleAgeMs { get; }
        }

        internal readonly struct OffCurrentSample
        {
            public OffCurrentSample(double currentA, bool isFresh, double ageMs)
            {
                CurrentA = currentA;
                IsFresh = isFresh;
                AgeMs = ageMs;
            }
            public double CurrentA { get; }
            public bool IsFresh { get; }
            public double AgeMs { get; }
        }

        private OffCurrentSample ReadOffCurrentSample()
        {
            var currentA = Math.Abs(_readCurrent(_channel));
            if (_acq == null) return new OffCurrentSample(currentA, true, 0);
            var device = _acq.GetDeviceForEpbChannel(_channel);
            var freshness = _acq.GetDaqFreshnessSnapshot(device, 100);
            return new OffCurrentSample(currentA, freshness.IsFresh, freshness.AgeMs);
        }

        /// <summary>
        /// 断电后按固定周期读取电流；只要在窗口内清零即通过，窗口耗尽才返回失败。
        /// 调用方负责对失败结果执行一次组级联锁。
        /// </summary>
        internal static async Task<OffCurrentClearResult> PollOffCurrentUntilClearAsync(
            Func<double> readCurrent,
            double thresholdA,
            int timeoutMs,
            int pollMs,
            CancellationToken token)
        {
            if (readCurrent == null) throw new ArgumentNullException(nameof(readCurrent));
            return await PollOffCurrentUntilClearAsync(
                    () => new OffCurrentSample(readCurrent(), true, 0),
                    thresholdA,
                    timeoutMs,
                    pollMs,
                    token)
                .ConfigureAwait(false);
        }

        internal static async Task<OffCurrentClearResult> PollOffCurrentUntilClearAsync(
            Func<OffCurrentSample> readCurrent,
            double thresholdA,
            int timeoutMs,
            int pollMs,
            CancellationToken token)
        {
            if (readCurrent == null) throw new ArgumentNullException(nameof(readCurrent));
            var boundedTimeoutMs = Math.Max(20, timeoutMs);
            var boundedPollMs = Math.Max(1, pollMs);
            var started = Stopwatch.GetTimestamp();
            var currentA = double.NaN;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var sample = readCurrent();
                currentA = sample.CurrentA;
                var elapsedMs = (int)Math.Min(
                    int.MaxValue,
                    Math.Max(
                        0,
                        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency));
                // A stale cache entry is not evidence that the current failed to clear.  The
                // DAQ callback may already be catching up from its hardware buffer (the field
                // symptom is a fresh callback carrying an old sample timestamp).  Keep the
                // motor DO off and wait within the existing bounded clear-current window for a
                // genuinely fresh replacement sample.  Only the timeout result is allowed to
                // escalate an unverifiable OFF state to the power-group interlock.
                if (sample.IsFresh &&
                    !double.IsNaN(currentA) &&
                    !double.IsInfinity(currentA) &&
                    Math.Abs(currentA) <= thresholdA)
                {
                    return new OffCurrentClearResult(true, currentA, elapsedMs);
                }

                if (elapsedMs >= boundedTimeoutMs)
                    return new OffCurrentClearResult(
                        false,
                        currentA,
                        elapsedMs,
                        sample.IsFresh,
                        sample.AgeMs);

                await Task.Delay(
                        Math.Min(boundedPollMs, boundedTimeoutMs - elapsedMs),
                        token)
                    .ConfigureAwait(false);
            }
        }

        internal static bool ExecuteTerminalOffWithEscalation(
            Func<bool> commandOff,
            Action emergencyShutdown)
        {
            var succeeded = false;
            try { succeeded = commandOff?.Invoke() == true; }
            catch { succeeded = false; }
            if (!succeeded) emergencyShutdown?.Invoke();
            return succeeded;
        }

        internal static bool VerifyOffCurrentOrEscalate(
            double currentA,
            double thresholdA,
            Action emergencyShutdown)
        {
            var cleared =
                !double.IsNaN(currentA) &&
                !double.IsInfinity(currentA) &&
                Math.Abs(currentA) <= Math.Max(0.01, thresholdA);
            if (!cleared) emergencyShutdown?.Invoke();
            return cleared;
        }

        private void EnsureAdaptiveClampPeakCaptureStarted()
        {
            if (_acq == null ||
                Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 0) != 0)
                return;
            try
            {
                var runId = Guid.Empty;
                var cycleNumber = 0;
                _manager?.GetPeakCaptureIdentity(_channel, out runId, out cycleNumber);
                _adaptivePeakCaptureToken = _acq.BeginEpbCurrentPeak(
                    _channel,
                    runId,
                    cycleNumber);
            }
            catch
            {
                Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0);
                _adaptivePeakCaptureToken = null;
            }
        }

        private async Task<string> FinalizeFastPathFaultAsync(EpbAdaptiveDecision decision)
        {
            if (decision == null || !FastPathTripClassifier.IsFastSignalDependentFault(decision.Reason))
                return decision?.Reason ?? "HardFault";

            FastPathTripResult classification;
            if (decision.Reason.IndexOf("OverCurrent3Samples", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                double fullRatePeakA = double.NaN;
                double evidenceAgeMs = double.PositiveInfinity;
                try
                {
                    if (_acq != null && _adaptivePeakCaptureToken != null &&
                        Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 1) == 1)
                    {
                        var capture = await _acq.EndEpbCurrentPeakAsync(
                                _adaptivePeakCaptureToken,
                                100,
                                // 调用时即固定逻辑截止；100ms 只用于等待在途批次入账，
                                // 不能把断电后的新时间窗混入快速过流证据。
                                cutoffAfterDelay: false,
                                cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                        if (capture.IsMatched && capture.IsCutoffCovered &&
                            capture.Peak.SampleCount > 0)
                        {
                            fullRatePeakA = capture.Peak.MaxAmp;
                            evidenceAgeMs = (capture.LogicalCutoffUtc -
                                             capture.Peak.LastSampleAt.ToUniversalTime()).TotalMilliseconds;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"EPB[{_channel}] 快速过流证据封口失败：{ex.Message}", "EPB");
                }
                finally
                {
                    Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0);
                    _adaptivePeakCaptureToken = null;
                }

                var fastPeakA = Math.Max(
                    Math.Max(
                        Math.Abs(decision.CurrentA),
                        _adaptiveStateMachine?.PeakCurrentA ?? 0),
                    double.IsNaN(decision.FastRepresentativeA)
                        ? 0
                        : Math.Abs(decision.FastRepresentativeA));
                classification = FastPathTripClassifier.ClassifyOverCurrent(
                    fastPeakA,
                    _posThrA + Math.Max(0, _overshootAlarmDeltaA),
                    fullRatePeakA,
                    evidenceAgeMs,
                    _programSafetySettings.PeakEvidenceMismatchToleranceA,
                    _programSafetySettings.PeakEvidenceMaximumLagMs,
                    decision.FastSignalQualityFlags);
            }
            else
            {
                classification = FastPathTripClassifier.ClassifySignalDependentFault(
                    decision.Reason,
                    decision.FastSignalQualityFlags);
            }

            var finalReason = classification.Classification == FastPathTripClassification.NotApplicable
                ? decision.Reason
                : $"{classification.Code} {classification.Reason} Original={decision.Reason}";
            _log?.Error($"EPB[{_channel}] 快速保护最终归因：{finalReason}", "EPB");
            NotifyAlarmSafely("AdaptiveHardFault " + finalReason);
            return finalReason;
        }

        private void RaiseAdaptiveWarning(string reason)
        {
            var warningReason = reason ?? "AdaptiveWarning";
            ObserveAdaptiveBackground(Task.Run(() =>
            {
                NotifyWarningSafely(warningReason);
            }), "AdaptiveWarningNotification");
        }

        private void ObserveAdaptiveBackground(Task task, string operation)
        {
            if (task == null) return;
            if (_manager != null)
            {
                _manager.ObserveBackgroundTask(task, operation, _channel);
                return;
            }

            _ = task.ContinueWith(
                completed =>
                {
                    var exception = completed.Exception?.GetBaseException();
                    try
                    {
                        _log?.Error(
                            $"EPB[{_channel}] 后台异步操作失败：Task={operation} " +
                            $"Error={exception?.Message}",
                            "EPB",
                            exception);
                    }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RaiseAdaptiveWarning(AdaptiveWarningEvent warning)
        {
            if (warning == null) return;
            warning.Channel = _channel;
            if (warning.OccurredUtc == default) warning.OccurredUtc = DateTime.UtcNow;
            NotifyWarningEvidenceSafely(warning);
            RaiseAdaptiveWarning(warning.Reason);
        }

        private async Task<EpbAdaptiveDecision> WaitAdaptiveDecisionAsync(
            TaskCompletionSource<EpbAdaptiveDecision> completion,
            CancellationToken token)
        {
            if (completion == null) throw new InvalidOperationException("自适应等待器未初始化。");

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var finished = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(20, token)).ConfigureAwait(false);

                if (finished == completion.Task)
                    return await completion.Task.ConfigureAwait(false);

                var watchdogTick = AdaptiveNowTicks();
                var watchdog = _adaptiveStateMachine.CheckWatchdogReusable(
                    watchdogTick,
                    _adaptiveWatchdogDecisionScratch);
                if (watchdog.HardFault)
                {
                    DispatchAdaptiveDecisionInSafetyOrder(
                        watchdog,
                        () => EnsureAdaptiveTerminalPowerOff(watchdog),
                        () => PublishAdaptiveTrace(
                            watchdogTick,
                            DateTime.UtcNow,
                            watchdog),
                        () => HandleAdaptiveDecision(watchdog));
                    if (_epbControlMode == EpbControlMode.AdaptiveCurrent)
                        return await completion.Task.ConfigureAwait(false);
                    return watchdog;
                }
            }
        }

        private async Task<EpbCycleOutcome> RunOneAdaptiveAsync(int targetPeriodMs, CancellationToken token)
        {
            const int peakEvidenceDrainMs = 100;
            var balancedUndershootWarningA =
                _adaptiveSafetyLimits.ForwardAcceptableUndershootA;
            var outcome = new EpbCycleOutcome
            {
                Kind = EpbCycleOutcomeKind.HardFault,
                Stage = EpbCurrentStage.Idle,
                Reason = "AdaptiveCycleNotCompleted"
            };

            try
            {
                if (_manager != null)
                    await _manager.HydraulicEnterAsync(_channel, token).ConfigureAwait(false);

                CaptureAdaptivePreEnergizationCurrent();
                BeginAdaptiveForwardMonitoring(targetPeriodMs);
                // 从上电前开始捕获 2kHz 证据，确保浪涌和快速过流都能在断电后归因。
                EnsureAdaptiveClampPeakCaptureStarted();
                RequireMotorCommandSucceeded(CommandForward(), "Forward");
                _log?.Info(
                    $"EPB[{_channel}] 自适应正向上电：软时限={_adaptiveProfile.GetForwardSoftLimitMs()}ms，" +
                    $"硬时限={GetForwardAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> forwardCompletion;
                lock (_adaptiveGate) forwardCompletion = _adaptiveForwardCompletion;
                var forward = await WaitAdaptiveDecisionAsync(forwardCompletion, token).ConfigureAwait(false);
                if (forward.HardFault)
                {
                    if (forward.Reason?.IndexOf(
                            "InfrastructureTransitionOpenCircuit",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        DisarmAdaptiveMonitoring();
                        return EpbCycleOutcome.SoftwareRecovery(forward.Stage, forward.Reason);
                    }
                    var finalReason = await FinalizeFastPathFaultAsync(forward).ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(forward.Stage, finalReason);
                }
                if (!forward.ClampReached)
                {
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(forward.Stage, "ForwardEndedWithoutClamp");
                }

                CompleteAdaptiveForwardMonitoring(forward.ElapsedMs);

                var holdTask = _holdMs > 0
                    ? Task.Delay(_holdMs, token)
                    : Task.CompletedTask;
                Task hydraulicReleaseTask = Task.CompletedTask;
                if (_manager != null)
                    hydraulicReleaseTask = _manager.HydraulicMarkReleaseAsync(_channel);

                var peakCaptureValid = false;
                string peakCaptureFailure = null;
                string peakEvidenceMismatch = null;
                var peakEvidenceLag = false;
                var peakEvidenceTimestampMissing = false;
                double decisionEvidenceLagMs;
                DateTime decisionEvidenceThroughUtc;
                lock (_adaptiveGate)
                {
                    decisionEvidenceLagMs = _adaptiveDecisionPeakEvidenceLagMs;
                    decisionEvidenceThroughUtc = _adaptiveDecisionPeakEvidenceThroughUtc;
                }
                try
                {
                    if (_acq != null &&
                        Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 1) == 1)
                    {
                        var captureResult = await _acq.EndEpbCurrentPeakAsync(
                                _adaptivePeakCaptureToken,
                                peakEvidenceDrainMs,
                                // 在断电判定完成时固定逻辑截止，随后仅等待截止前的在途
                                // 全速率样本入账，保证快速/完整证据使用同一时间窗。
                                cutoffAfterDelay: false,
                                cancellationToken: token)
                            .ConfigureAwait(false);
                        var peak = captureResult.Peak;
                        peakCaptureValid = IsFullRatePeakCaptureValid(
                            captureResult,
                            _programSafetySettings.PeakEvidenceMaximumLagMs,
                            out var peakEvidenceTailLagMs);
                        if (!captureResult.IsMatched)
                            peakCaptureFailure = captureResult.QualityReason;
                        else if (!peakCaptureValid)
                            peakCaptureFailure =
                                $"FullRatePeakInvalid Samples={peak.SampleCount} " +
                                $"Peak={peak.MaxAmp:F3}A TailLag={peakEvidenceTailLagMs:F1}ms " +
                                $"Covered={captureResult.IsCutoffCovered} " +
                                $"Drain={captureResult.DrainElapsedMs:F1}ms " +
                                $"Quality={captureResult.QualityReason} " +
                                $"Limit={_programSafetySettings.PeakEvidenceMaximumLagMs:F1}ms";
                        else
                        {
                            _adaptiveForwardPeakA = peak.MaxAmp;
                            var quickPeak = forward.ObservedFullRatePeakA;
                            if (peakCaptureValid &&
                                !double.IsNaN(quickPeak) && !double.IsInfinity(quickPeak) && quickPeak > 0)
                            {
                                var peakDiagnostic = EvaluateCompletedPeakEvidence(
                                    decisionEvidenceThroughUtc,
                                    peak.MaxAt,
                                    decisionEvidenceLagMs,
                                    _programSafetySettings.PeakEvidenceMaximumLagMs,
                                    out var comparableWindow);
                                peakEvidenceLag =
                                    peakDiagnostic == AdaptiveWarningCode.PeakEvidenceLagWarning;
                                peakEvidenceTimestampMissing =
                                    peakDiagnostic == AdaptiveWarningCode.PeakEvidenceTimestampMissing;
                                if (comparableWindow && !peakEvidenceLag &&
                                    Math.Abs(quickPeak - peak.MaxAmp) >
                                    _programSafetySettings.PeakEvidenceMismatchToleranceA)
                                    peakEvidenceMismatch =
                                    $"QuickPeak={quickPeak:F3}A FullRatePeak={peak.MaxAmp:F3}A " +
                                    $"Tolerance={_programSafetySettings.PeakEvidenceMismatchToleranceA:F3}A " +
                                    $"CaptureId={_adaptivePeakCaptureToken?.CaptureId:N}";
                            }
                        }
                        Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0);
                        _adaptivePeakCaptureToken = null;
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0) != 0)
                    {
                        try
                        {
                            if (_adaptivePeakCaptureToken != null)
                                _acq?.CancelEpbCurrentPeak(_adaptivePeakCaptureToken);
                            else
                                _acq?.CancelEpbCurrentPeak(_channel);
                        }
                        catch { }
                    }
                    _adaptivePeakCaptureToken = null;
                    // 峰值封口失败不改变已经由快速样本确认的夹紧结果。
                    _log?.Warn($"EPB[{_channel}] 断电后峰值捕获失败：{ex.Message}", "EPB");
                }

                string forwardEvidenceSoftwareRecoveryReason = null;
                if (!string.IsNullOrWhiteSpace(peakCaptureFailure))
                {
                    forwardEvidenceSoftwareRecoveryReason =
                        "PeakCaptureInvalid " + peakCaptureFailure;
                    _log?.Warn(
                        $"EPB[{_channel}] 全速率峰值证据不完整；当前圈作废并进入软件恢复，" +
                        "不确认电流硬故障；但必须先完成反向机械释放，禁止夹紧状态进入下一圈。" +
                        forwardEvidenceSoftwareRecoveryReason,
                        "EPB");
                }

                var mismatchStreak =
                    _adaptiveProfile?.ConsecutivePeakEvidenceMismatchCount ?? 0;
                if (peakEvidenceTimestampMissing)
                {
                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(new AdaptiveWarningEvent
                    {
                        Code = AdaptiveWarningCode.PeakEvidenceTimestampMissing,
                        PeakCurrentA = _adaptiveForwardPeakA,
                        TargetCurrentA = _posThrA,
                        Streak = mismatchStreak,
                        ConfirmThreshold = _peakEvidenceMismatchConfirmCycles,
                        Reason =
                            "峰值证据时间戳缺失：无法比较快速判定窗口与完整峰值窗口；" +
                            "本圈不参与峰值偏差连续计数。该诊断不是100ms处理滞后，请检查DAQ时间轴证据。"
                    });
                }
                else if (peakEvidenceLag)
                {
                    var lagText = double.IsNaN(decisionEvidenceLagMs) ||
                                  double.IsInfinity(decisionEvidenceLagMs)
                        ? "无法确定"
                        : $"{decisionEvidenceLagMs:F1}ms";
                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(new AdaptiveWarningEvent
                    {
                        Code = AdaptiveWarningCode.PeakEvidenceLagWarning,
                        PeakCurrentA = _adaptiveForwardPeakA,
                        TargetCurrentA = _posThrA,
                        EvidenceLagMs = decisionEvidenceLagMs,
                        Streak = mismatchStreak,
                        ConfirmThreshold = _peakEvidenceMismatchConfirmCycles,
                        Reason =
                            $"峰值完整数据处理滞后：滞后={lagText}，" +
                            $"允许={_programSafetySettings.PeakEvidenceMaximumLagMs}ms；" +
                            "本圈不参与峰值证据偏差连续计数，请检查采集后台队列和落盘耗时。"
                    });
                }
                else if (!string.IsNullOrWhiteSpace(peakEvidenceMismatch))
                {
                    mismatchStreak = _adaptiveProfile.UpdatePeakEvidenceMismatchStreak(true);
                    _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
                    try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
                    catch (Exception ex)
                    {
                        _log?.Warn($"EPB[{_channel}] 峰值证据偏差连续计数保存失败：{ex.Message}", "EPB");
                    }

                    if (IsPeakEvidenceMismatchConfirmed(
                            mismatchStreak,
                            _peakEvidenceMismatchConfirmCycles))
                    {
                        await hydraulicReleaseTask.ConfigureAwait(false);
                        DisarmAdaptiveMonitoring();
                        var reason =
                            $"PeakEvidenceMismatch {peakEvidenceMismatch} " +
                            $"Streak={mismatchStreak}/{_peakEvidenceMismatchConfirmCycles}";
                        NotifyAlarmSafely("AdaptiveHardFault " + reason);
                        return EpbCycleOutcome.HardFault(EpbCurrentStage.ClampReached, reason);
                    }

                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(new AdaptiveWarningEvent
                    {
                        Code = AdaptiveWarningCode.PeakEvidenceMismatchWarning,
                        PeakCurrentA = _adaptiveForwardPeakA,
                        TargetCurrentA = _posThrA,
                        Streak = mismatchStreak,
                        ConfirmThreshold = _peakEvidenceMismatchConfirmCycles,
                        Reason =
                            $"快速峰值与完整数据峰值偏差超限：{peakEvidenceMismatch}，" +
                            $"连续={mismatchStreak}/{_peakEvidenceMismatchConfirmCycles}；" +
                            "本圈继续完成反向释放。"
                    });
                }
                else if (peakCaptureValid && mismatchStreak > 0)
                {
                    _adaptiveProfile.UpdatePeakEvidenceMismatchStreak(false);
                    _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
                    try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
                    catch (Exception ex)
                    {
                        _log?.Warn($"EPB[{_channel}] 峰值证据偏差计数清零保存失败：{ex.Message}", "EPB");
                    }
                }

                var peakErrorA = peakCaptureValid
                    ? _adaptiveForwardPeakA - _posThrA
                    : double.NaN;
                var committedOvershootStreak =
                    _adaptiveProfile?.ConsecutiveForwardOvershootCount ?? 0;
                if (peakCaptureValid)
                {
                    EpbDoTimingObservation doTiming;
                    lock (_adaptiveGate)
                        doTiming = _adaptiveForwardDoTiming?.Clone();
                    PersistAdaptiveCutoffObservation(
                        forward.CutoffCurrentA,
                        forward.EstimatedSlopeAperMs,
                        _adaptiveForwardPeakA,
                        peakErrorA,
                        doTiming);

                }
                else
                {
                    _log?.Warn(
                        $"EPB[{_channel}] 未取得有效断电后峰值，本圈不更新控流预测模型。",
                        "EPB");
                }

                var acceptableFloorA = Math.Max(
                    0,
                    _posThrA - _adaptiveSafetyLimits.ForwardAcceptableUndershootA);
                var observedPeakA = peakCaptureValid
                    ? _adaptiveForwardPeakA
                    : Math.Max(
                        Math.Max(0, forward.CutoffCurrentA),
                        double.IsNaN(forward.ObservedFullRatePeakA) ||
                        double.IsInfinity(forward.ObservedFullRatePeakA)
                            ? 0
                            : forward.ObservedFullRatePeakA);
                var lowTargetPlateau =
                    string.Equals(
                        forward.CutoffReason,
                        "LowTargetPlateau",
                    StringComparison.Ordinal) &&
                    observedPeakA < acceptableFloorA;
                var committedStallStreak =
                    _adaptiveProfile?.ConsecutiveForwardStallCount ?? 0;
                var prospectiveStallStreak = lowTargetPlateau
                    ? committedStallStreak + 1
                    : 0;
                var permanentOvershoot =
                    peakCaptureValid &&
                    FormalCycleFaultPolicy.IsPermanentOvershoot(
                        _adaptiveForwardPeakA,
                        _posThrA,
                        _adaptivePermanentOvershootDeltaA);
                var prospectiveOvershootStreak = permanentOvershoot
                    ? committedOvershootStreak + 1
                    : 0;

                if (peakCaptureValid && peakErrorA > _adaptiveOvershootWarningDeltaA)
                {
                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(new AdaptiveWarningEvent
                    {
                        Code = AdaptiveWarningCode.ForwardPeakOvershootWarning,
                        PeakCurrentA = _adaptiveForwardPeakA,
                        TargetCurrentA = _posThrA,
                        PeakErrorA = peakErrorA,
                        SlopeAperMs = forward.EstimatedSlopeAperMs,
                        WindowSpanMs = forward.WindowSpanMs,
                        Streak = prospectiveOvershootStreak,
                        ConfirmThreshold = _adaptiveOvershootConfirmCycles,
                        Reason =
                            $"正向实际峰值单圈超出平衡带：Peak={_adaptiveForwardPeakA:F3}A，" +
                            $"Target={_posThrA:F3}A，Error={peakErrorA:+0.000;-0.000;0.000}A，" +
                            (permanentOvershoot
                                ? $"永久报警候选={prospectiveOvershootStreak}/{_adaptiveOvershootConfirmCycles}；"
                                : $"尚未达到永久报警线+{_adaptivePermanentOvershootDeltaA:F3}A；") +
                            "本圈继续完成反向释放，控流模型已提前修正下一圈断电点。"
                    });
                }

                if (peakCaptureValid &&
                    !lowTargetPlateau &&
                    peakErrorA < -balancedUndershootWarningA)
                {
                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(
                        $"正向实际峰值低于目标：Peak={_adaptiveForwardPeakA:F3}A，" +
                        $"Target={_posThrA:F3}A，Error={peakErrorA:F3}A；控流模型将自动缩短提前量。");
                }

                if (lowTargetPlateau)
                {
                    _adaptiveSoftWarningSeen = true;
                    RaiseAdaptiveWarning(new AdaptiveWarningEvent
                    {
                        Code = AdaptiveWarningCode.ForwardCurrentRiseStallWarning,
                        PeakCurrentA = observedPeakA,
                        TargetCurrentA = _posThrA,
                        PeakErrorA = observedPeakA - _posThrA,
                        SlopeAperMs = forward.EstimatedSlopeAperMs,
                        WindowSpanMs = forward.WindowSpanMs,
                        Streak = prospectiveStallStreak,
                        ConfirmThreshold = _adaptiveForwardStallConfirmCycles,
                        Reason =
                            $"正向低于合格下限的平台停滞：Peak={observedPeakA:F3}A，" +
                            $"Floor={acceptableFloorA:F3}A，Target={_posThrA:F3}A，" +
                            $"Slope={forward.EstimatedSlopeAperMs:F6}A/ms，" +
                            $"Window={forward.WindowSpanMs}ms，" +
                            $"提交后连续={prospectiveStallStreak}/{_adaptiveForwardStallConfirmCycles}；" +
                            "已立即断开正向电，本圈继续完成反向释放并计数。"
                    });
                }

                await hydraulicReleaseTask.ConfigureAwait(false);
                await holdTask.ConfigureAwait(false);

                BeginAdaptiveReverseMonitoring(targetPeriodMs);
                EnsureAdaptiveClampPeakCaptureStarted();
                RequireMotorCommandSucceeded(CommandReverse(), "Reverse");
                _log?.Info(
                    $"EPB[{_channel}] 自适应反向上电：硬时限={GetReverseAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> reverseCompletion;
                lock (_adaptiveGate) reverseCompletion = _adaptiveReverseCompletion;
                var reverse = await WaitAdaptiveDecisionAsync(reverseCompletion, token).ConfigureAwait(false);
                if (reverse.HardFault)
                {
                    if (reverse.Reason?.IndexOf(
                            "InfrastructureTransitionOpenCircuit",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        DisarmAdaptiveMonitoring();
                        return EpbCycleOutcome.SoftwareRecovery(reverse.Stage, reverse.Reason);
                    }
                    var finalReason = await FinalizeFastPathFaultAsync(reverse).ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(reverse.Stage, finalReason);
                }
                if (!reverse.ReleaseCompleted)
                {
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(reverse.Stage, "ReverseEndedWithoutRelease");
                }

                _adaptiveReverseEmptyA = _adaptiveStateMachine.ObservedReverseEmptyA;
                DisarmAdaptiveMonitoring();

                if (!string.IsNullOrWhiteSpace(forwardEvidenceSoftwareRecoveryReason))
                {
                    _log?.Info(
                        $"EPB[{_channel}] 峰值证据异常圈已完成反向机械释放；" +
                        "本圈不计数，未来完整圈重试。",
                        "EPB");
                    return EpbCycleOutcome.SoftwareRecovery(
                        EpbCurrentStage.Released,
                        forwardEvidenceSoftwareRecoveryReason);
                }

                outcome = new EpbCycleOutcome
                {
                    Kind = _adaptiveSoftWarningSeen
                        ? EpbCycleOutcomeKind.SuccessWithWarning
                        : EpbCycleOutcomeKind.Success,
                    Stage = EpbCurrentStage.Released,
                    Reason = _adaptiveSoftWarningSeen ? "CompletedWithSoftWarning" : "Completed",
                    ForwardElapsedMs = _adaptiveForwardElapsedMs,
                    ReverseElapsedMs = _adaptiveReverseElapsedMs,
                    PeakCurrentA = _adaptiveForwardPeakA,
                    ControlPeakCurrentA = _adaptiveForwardControlPeakA,
                    TargetCurrentA = _posThrA,
                    CutoffCurrentA = forward.CutoffCurrentA,
                    EstimatedSlopeAperMs = forward.EstimatedSlopeAperMs,
                    PredictedPeakA = forward.PredictedPeakA,
                    PredictionLeadMs = forward.PredictionLeadMs,
                    PeakErrorA = peakErrorA,
                    CutoffReason = forward.CutoffReason,
                    ForwardEmptyCurrentA = _adaptiveForwardEmptyA,
                    ReverseEmptyCurrentA = _adaptiveReverseEmptyA,
                    ForwardLowPlateauCandidate = lowTargetPlateau,
                    ForwardPermanentOvershootCandidate = permanentOvershoot,
                    ForwardAcceptableFloorA = acceptableFloorA,
                    PermanentOvershootDeltaA = _adaptivePermanentOvershootDeltaA
                };

                PersistSuccessfulAdaptiveSample(
                    outcome.ForwardElapsedMs,
                    outcome.ReverseElapsedMs,
                    outcome.ForwardEmptyCurrentA,
                    outcome.ReverseEmptyCurrentA,
                    "控制");

                _log?.Info(
                    $"EPB[{_channel}] 自适应单圈完成：Fwd={outcome.ForwardElapsedMs}ms，" +
                    $"Rev={outcome.ReverseElapsedMs}ms，Cutoff={outcome.CutoffCurrentA:F3}A，" +
                    $"PredictedPeak={outcome.PredictedPeakA:F3}A，" +
                    $"ControlPeak={outcome.ControlPeakCurrentA:F3}A，FullRatePeak={outcome.PeakCurrentA:F3}A，" +
                    $"Target={outcome.TargetCurrentA:F3}A，Error={outcome.PeakErrorA:+0.000;-0.000;0.000}A，" +
                    $"Slope={outcome.EstimatedSlopeAperMs:F4}A/ms，Lead={outcome.PredictionLeadMs:F2}ms，" +
                    $"Trigger={outcome.CutoffReason}，SafetyPolicy={EpbProgramSafetySettings.SafetyPolicyVersion}，" +
                    $"结果={outcome.Kind}。",
                    "EPB");
                var metricRunId = Guid.Empty;
                var metricCycle = 0;
                _manager?.GetPeakCaptureIdentity(_channel, out metricRunId, out metricCycle);
                var metricFloor = outcome.TargetCurrentA -
                                  _adaptiveSafetyLimits.ForwardAcceptableUndershootA;
                var metricCeiling = outcome.TargetCurrentA + _adaptiveOvershootWarningDeltaA;
                var metricQualified = outcome.PeakCurrentA >= metricFloor &&
                                      outcome.PeakCurrentA <= metricCeiling;
                _log?.Info(
                    $"FieldMetric CYCLE RunId={metricRunId:N} Channel={_channel} " +
                    $"Cycle={metricCycle} Phase={(metricCycle > 0 ? "Formal" : "Learning")} " +
                    $"Peak={outcome.PeakCurrentA:F3} Target={outcome.TargetCurrentA:F3} " +
                    $"Floor={metricFloor:F3} Ceiling={metricCeiling:F3} " +
                    $"Qualified={metricQualified} Result={outcome.Kind}",
                    "FIELD");
                return outcome;
            }
            catch (HydraulicBarrierTimeoutException ex)
            {
                var offSucceeded = false;
                try { offSucceeded = CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                if (!CanDiscardForSoftwareRecovery(ex, offSucceeded))
                {
                    var offReason = "SoftwareRecoveryOffFailed HydraulicBarrierTimeout " + ex.Message;
                    NotifyAlarmSafely("AdaptiveHardFault " + offReason);
                    return EpbCycleOutcome.HardFault(
                        _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                        offReason);
                }
                // 协调器已经按液压组发布软件同步故障并完成安全回零。当前圈作废，
                // 由组级恢复任务从未来完整圈继续，不能升级为通道硬件报警。
                return EpbCycleOutcome.SoftwareRecovery(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    "HydraulicBarrierTimeout: " + ex.Message);
            }
            catch (HydraulicReleaseTimeoutException ex)
            {
                try { CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                const string code = "HydraulicReleaseTimeout";
                NotifyAlarmSafely("AdaptiveHardFault " + code + " " + ex.Message);
                return EpbCycleOutcome.HardFault(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    code + ": " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                var offSucceeded = false;
                try { offSucceeded = CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                if (!offSucceeded)
                {
                    const string offReason = "CancellationOffFailed";
                    NotifyAlarmSafely("AdaptiveHardFault " + offReason);
                    return EpbCycleOutcome.HardFault(
                        _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                        offReason);
                }
                return EpbCycleOutcome.Canceled(_adaptiveStateMachine?.Stage ?? EpbCurrentStage.Idle, "Canceled");
            }
            catch (Exception ex) when (IsSoftwareRecoveryException(ex))
            {
                var offSucceeded = false;
                try { offSucceeded = CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                if (!CanDiscardForSoftwareRecovery(ex, offSucceeded))
                {
                    var offReason =
                        $"SoftwareRecoveryOffFailed {ex.GetType().Name}: {ex.Message}";
                    NotifyAlarmSafely("AdaptiveHardFault " + offReason);
                    return EpbCycleOutcome.HardFault(
                        _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                        offReason);
                }
                _log?.Warn(
                    $"EPB[{_channel}] 单圈软件异常已安全断电，本圈作废并在未来完整圈重试：" +
                    $"{ex.GetType().Name}: {ex.Message}",
                    "EPB");
                return EpbCycleOutcome.SoftwareRecovery(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    $"UnhandledSoftwareException: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                try { CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                NotifyAlarmSafely("AdaptiveUnhandledException " + ex.Message);
                return EpbCycleOutcome.HardFault(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    "UnhandledException: " + ex.Message);
            }
        }

        internal static bool IsSoftwareRecoveryException(Exception exception)
        {
            if (exception == null) return true;
            var root = exception is AggregateException aggregate
                ? aggregate.GetBaseException()
                : exception;
            if (root is HydraulicBarrierTimeoutException) return true;
            if (root is HydraulicBuildTimeoutException buildTimeout)
                return HydraulicGroupCoordinator.ClassifyFault(buildTimeout) !=
                       FaultClassification.HardwareConfirmed;
            if (root is HydraulicPressureLostException pressureLost)
                return HydraulicGroupCoordinator.ClassifyFault(pressureLost) !=
                       FaultClassification.HardwareConfirmed;
            if (root is OperationCanceledException ||
                root is EpbOutputCommandException ||
                root is HydraulicBuildException ||
                root is HydraulicReleaseTimeoutException ||
                root is OutOfMemoryException)
                return false;

            return true;
        }

        internal static bool CanDiscardForSoftwareRecovery(
            Exception exception,
            bool outputOffSucceeded)
        {
            return outputOffSucceeded && IsSoftwareRecoveryException(exception);
        }

        /// <summary>
        /// 自适应模式的启动学习圈。与正式控制共用同一套电流状态机和硬保护，
        /// 但不触发正式圈完成计数；成功样本会写入项目级自适应模型。
        /// </summary>
        public async Task<EpbCycleOutcome> RunOneAdaptiveLearningAsync(
            int targetPeriodMs,
            CancellationToken token)
        {
            var outcome = await RunOneAdaptiveAsync(targetPeriodMs, token).ConfigureAwait(false);
            LastCycleOutcome = outcome;
            return outcome;
        }

        private void PersistSuccessfulAdaptiveSample(
            int forwardElapsedMs,
            int reverseElapsedMs,
            double forwardEmptyA,
            double reverseEmptyA,
            string source)
        {
            if (forwardElapsedMs <= 0 || reverseElapsedMs <= 0 ||
                forwardEmptyA <= 0 || reverseEmptyA <= 0)
            {
                _log?.Warn(
                    $"EPB[{_channel}] {source}圈未写入自适应模型：样本不完整 " +
                    $"Fwd={forwardElapsedMs} Rev={reverseElapsedMs} " +
                    $"Iempty+={forwardEmptyA:F3} Iempty-={reverseEmptyA:F3}。",
                    "EPB");
                return;
            }

            var persistentDeviation = _adaptiveProfile.AddSuccessfulCycle(
                forwardEmptyA,
                reverseEmptyA,
                forwardElapsedMs,
                reverseElapsedMs);
            _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);

            try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_channel}] 自适应模型回调保存失败：{ex.Message}", "EPB");
            }

            if (persistentDeviation)
            {
                RaiseAdaptiveWarning(
                    $"模型连续{_adaptiveProfile.ConsecutiveDeviationCount}圈偏差>30%，已渐进更新；" +
                    $"Fwd基线={_adaptiveProfile.ForwardClampMedianMs:F0}ms，" +
                    $"Rev基线={_adaptiveProfile.ReverseReleaseMedianMs:F0}ms");
            }

            _log?.Info(
                $"EPB[{_channel}] {source}样本已保存：有效样本={_adaptiveProfile.ValidSampleCount}，" +
                $"Fwd={_adaptiveProfile.ForwardClampMedianMs:F0}±MAD{_adaptiveProfile.ForwardClampMadMs:F0}ms，" +
                $"Rev={_adaptiveProfile.ReverseReleaseMedianMs:F0}±MAD{_adaptiveProfile.ReverseReleaseMadMs:F0}ms。",
                "EPB");
        }

        private void PersistAdaptiveCutoffObservation(
            double cutoffCurrentA,
            double cutoffSlopeAperMs,
            double actualPeakA,
            double peakErrorA,
            EpbDoTimingObservation doTiming)
        {
            if (!_adaptiveProfile.TryAddCutoffObservation(
                    _posThrA,
                    cutoffCurrentA,
                    cutoffSlopeAperMs,
                    actualPeakA,
                    doTiming,
                    out var equivalentLeadMs))
            {
                var now = Stopwatch.GetTimestamp();
                var last = Interlocked.Read(ref _lastInvalidCutoffObservationLogTick);
                if (last == 0 || now - last >= Stopwatch.Frequency * 30L)
                {
                    Interlocked.Exchange(ref _lastInvalidCutoffObservationLogTick, now);
                    _log?.Info(
                        $"EPB[{_channel}] 控流观测未进入学习模型：" +
                        "Rejected=InvalidOrUnlearnableSlope " +
                        $"Cutoff={cutoffCurrentA:F3}A Slope={cutoffSlopeAperMs:F4}A/ms " +
                        $"Peak={actualPeakA:F3}A Target={_posThrA:F3}A；" +
                        "保持现有模型，不作为停机故障。",
                        "EPB");
                }
                return;
            }

            _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
            try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_channel}] 控流模型回调保存失败：{ex.Message}", "EPB");
            }

            _log?.Info(
                $"EPB[{_channel}] 控流观测已保存：样本={_adaptiveProfile.ValidCutoffSampleCount}，" +
                $"本圈等效Lead={equivalentLeadMs:F2}ms，" +
                $"模型Lead={_adaptiveProfile.ForwardCutoffLeadMedianMs:F2}±MAD" +
                $"{_adaptiveProfile.ForwardCutoffLeadMadMs:F2}ms，" +
                $"PhysicalTail={_adaptiveProfile.ForwardPhysicalTailMedianMs:F2}±MAD" +
                $"{_adaptiveProfile.ForwardPhysicalTailMadMs:F2}ms，" +
                $"DO={_adaptiveProfile.ForwardDoCompletionMedianMs:F2}±MAD" +
                $"{_adaptiveProfile.ForwardDoCompletionMadMs:F2}ms/P95" +
                $"{_adaptiveProfile.ForwardDoCompletionP95Ms:F2}ms，" +
                $"StartDelay={_adaptiveProfile.ForwardDoStartDelayMedianMs:F2}/P95" +
                $"{_adaptiveProfile.ForwardDoStartDelayP95Ms:F2}ms，" +
                $"Write={_adaptiveProfile.ForwardDoWriteMedianMs:F2}/P95" +
                $"{_adaptiveProfile.ForwardDoWriteP95Ms:F2}ms，" +
                $"CurrentClear={_adaptiveProfile.ForwardCurrentClearMedianMs:F2}/P95" +
                $"{_adaptiveProfile.ForwardCurrentClearP95Ms:F2}ms，" +
                $"PeakError={peakErrorA:+0.000;-0.000;0.000}A。",
                "EPB");
        }

        private static TaskCompletionSource<EpbAdaptiveDecision> NewAdaptiveCompletion()
        {
            return new TaskCompletionSource<EpbAdaptiveDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
