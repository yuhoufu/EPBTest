using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Adaptive;
using Controller.Alarm;
using IO.NI;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        private readonly EpbControlMode _epbControlMode = EpbControlMode.LegacyFixedTiming;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveCurrentStateMachine _adaptiveStateMachine;
        private readonly EpbAdaptiveProfile _adaptiveProfile;
        private readonly EpbProgramSafetySettings _programSafetySettings;
        private readonly Action<EpbAdaptiveProfile> _saveAdaptiveProfile;
        private readonly object _adaptiveGate = new object();

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
        private int _adaptiveClampPeakCaptureStarted;
        private PeakCaptureToken _adaptivePeakCaptureToken;
        private string _adaptiveDirection = string.Empty;
        private readonly EpbAdaptiveSafetyLimits _adaptiveSafetyLimits;
        private int _adaptiveTerminalOffLatched;
        private double _adaptivePreEnergizationCurrentA = double.NaN;

        private const double OffCurrentBaselineWindowMs = 500.0;
        private const double OffCurrentBaselineAllowanceA = 0.05;
        private const double MaxTrustedOffCurrentBaselineA = 0.20;
        private const double MaxAdaptiveOffCurrentThresholdA = 0.25;

        internal static bool IsForwardStallConfirmed(int streak, int confirmCycles)
        {
            return streak >= Math.Max(1, confirmCycles);
        }

        internal event Action<AdaptiveDecisionTraceEvent> AdaptiveDecisionObserved;

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
            Interlocked.Exchange(ref _adaptiveClampPeakCaptureStarted, 0);
            Interlocked.Exchange(ref _adaptiveTerminalOffLatched, 0);

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
            DateTime sampleUtc)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            EpbAdaptiveDecision decision;
            try
            {
                var fullRatePeakA = double.NaN;
                if (_acq != null &&
                    string.Equals(_adaptiveDirection, "Forward", StringComparison.Ordinal) &&
                    Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 1) == 1)
                {
                    try
                    {
                        var evidence = _adaptivePeakCaptureToken == null
                            ? null
                            : _acq.PeekEpbCurrentPeak(_adaptivePeakCaptureToken);
                        if (evidence?.IsMatched == true && evidence.Peak.SampleCount > 0)
                            fullRatePeakA = evidence.Peak.MaxAmp;
                    }
                    catch
                    {
                        // 峰值窥视失败时继续使用快速控制通道，不得中断采集回调。
                    }
                }

                decision = _adaptiveStateMachine.OnSample(tick, currentAmp, fullRatePeakA);
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

            var item = new AdaptiveDecisionTraceEvent
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
                TaskCompletionSource<EpbAdaptiveDecision> completion;
                lock (_adaptiveGate) completion = _adaptiveForwardCompletion;
                completion?.TrySetResult(decision);
            }

            if (decision.ReleaseCompleted)
            {
                _adaptiveReverseElapsedMs = decision.ElapsedMs;
                _adaptiveReverseEmptyA = _adaptiveStateMachine.ObservedReverseEmptyA;
                TaskCompletionSource<EpbAdaptiveDecision> completion;
                lock (_adaptiveGate) completion = _adaptiveReverseCompletion;
                completion?.TrySetResult(decision);
            }

            if (!decision.HardFault) return;

            if (_epbControlMode != EpbControlMode.AdaptiveCurrent)
            {
                if (Interlocked.Exchange(ref _adaptiveFaultLatched, 1) == 0)
                    RaiseAdaptiveWarning("【影子硬故障，不执行停机】" + decision.Reason);
                return;
            }

            if (Interlocked.Exchange(ref _adaptiveFaultLatched, 1) != 0) return;

            TaskCompletionSource<EpbAdaptiveDecision> forward;
            TaskCompletionSource<EpbAdaptiveDecision> reverse;
            lock (_adaptiveGate)
            {
                forward = _adaptiveForwardCompletion;
                reverse = _adaptiveReverseCompletion;
            }

            forward?.TrySetResult(decision);
            reverse?.TrySetResult(decision);

            if (decision.Reason?.IndexOf("DaqSampleStale", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { RecoverableFaultRaised?.Invoke(_channel, decision.Reason); }
                catch { }
                return;
            }

            try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + decision.Reason); }
            catch { /* 上层报警订阅者异常不允许回流采集线程 */ }
        }

        private void EnsureAdaptiveTerminalPowerOff(EpbAdaptiveDecision decision)
        {
            if (decision == null ||
                (!decision.HardFault && !decision.ClampReached && !decision.ReleaseCompleted) ||
                _epbControlMode != EpbControlMode.AdaptiveCurrent)
                return;
            if (Interlocked.Exchange(ref _adaptiveTerminalOffLatched, 1) != 0) return;

            var reason = string.IsNullOrWhiteSpace(decision.Reason)
                ? decision.Stage.ToString()
                : decision.Reason;
            var commandElapsedMs = 0.0;
            var commandSucceeded = ExecuteTerminalOffWithEscalation(
                () =>
                {
                    var commandStarted = Stopwatch.GetTimestamp();
                    try { return CommandOffHighPriority(); }
                    finally
                    {
                        commandElapsedMs =
                            (Stopwatch.GetTimestamp() - commandStarted) * 1000.0 /
                            Stopwatch.Frequency;
                    }
                },
                () => _manager?.RequestElectricalGroupEmergencyShutdown(
                    _channel,
                    $"TerminalOffCommandFailed {reason}"));
            _manager?.RecordTerminalOffCommand(
                _channel,
                reason,
                commandSucceeded,
                commandElapsedMs);

            if (!commandSucceeded)
            {
                _log?.Error(
                    $"EPB[{_channel}] 终态高优先级断电失败，立即触发电源组联锁。" +
                    $"Reason={reason} CommandElapsed={commandElapsedMs:F3}ms",
                    "EPB");
                try
                {
                    AlarmRaised?.Invoke(
                        _channel,
                        $"AdaptiveHardFault TerminalOffCommandFailed {reason}");
                }
                catch { }
                return;
            }

            _log?.Info(
                $"EPB[{_channel}] 终态断电命令已优先执行。" +
                $"Reason={reason} CommandElapsed={commandElapsedMs:F3}ms " +
                "PhysicalOffStatus=NotMeasured",
                "EPB");
            BeginTerminalOffCurrentVerification(reason);
        }

        private void BeginTerminalOffCurrentVerification(string reason)
        {
            var configuredThresholdA = _adaptiveSafetyLimits.OffCurrentClearThresholdA;
            var baselineA = _adaptivePreEnergizationCurrentA;
            var thresholdA = ResolveOffCurrentClearThreshold(
                configuredThresholdA,
                baselineA);
            var timeoutMs = _adaptiveSafetyLimits.OffCurrentClearTimeoutMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    var verification = await PollOffCurrentUntilClearAsync(
                            () => Math.Abs(_readCurrent(_channel)),
                            thresholdA,
                            timeoutMs,
                            20,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    var currentA = verification.CurrentA;
                    var cleared = VerifyOffCurrentOrEscalate(
                        currentA,
                        thresholdA,
                        () => _manager?.RequestElectricalGroupEmergencyShutdown(
                            _channel,
                            $"OffCurrentNotCleared Current={currentA:F3}A Threshold={thresholdA:F3}A"));
                    _manager?.RecordTerminalOffCurrentVerification(
                        _channel,
                        currentA,
                        thresholdA,
                        verification.ElapsedMs,
                        cleared);
                    if (cleared)
                    {
                        _log?.Info(
                            $"EPB[{_channel}] 断电电流代理确认通过：" +
                            $"ElectricalCurrentCleared=true Current={currentA:F3}A " +
                            $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                            $"PreEnergizationBaseline={baselineA:F3}A " +
                            $"EffectiveThreshold={thresholdA:F3}A Wait={verification.ElapsedMs}ms " +
                            "PhysicalOffStatus=NotMeasured",
                            "EPB");
                        return;
                    }

                    _log?.Error(
                        $"EPB[{_channel}] 断电后电流未清零，立即触发电源组联锁：" +
                        $"ElectricalCurrentCleared=false Current={currentA:F3}A " +
                        $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                        $"PreEnergizationBaseline={baselineA:F3}A " +
                        $"EffectiveThreshold={thresholdA:F3}A Wait={verification.ElapsedMs}ms " +
                        $"Reason={reason} PhysicalOffStatus=NotMeasured",
                        "EPB");
                    try
                    {
                        AlarmRaised?.Invoke(
                            _channel,
                            $"AdaptiveHardFault OffCurrentNotCleared " +
                            $"Current={currentA:F3}A " +
                            $"ConfiguredThreshold={configuredThresholdA:F3}A " +
                            $"PreEnergizationBaseline={baselineA:F3}A " +
                            $"EffectiveThreshold={thresholdA:F3}A");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    _manager?.RecordTerminalOffCurrentVerification(
                        _channel,
                        double.NaN,
                        thresholdA,
                        timeoutMs,
                        false);
                    _log?.Error(
                        $"EPB[{_channel}] 断电电流代理确认无法完成，按失效安全触发电源组联锁：" +
                        $"{ex.Message} PhysicalOffStatus=NotMeasured",
                        "EPB");
                    _manager?.RequestElectricalGroupEmergencyShutdown(
                        _channel,
                        "OffCurrentVerificationFailed " + ex.Message);
                    try
                    {
                        AlarmRaised?.Invoke(
                            _channel,
                            "AdaptiveHardFault OffCurrentVerificationFailed " + ex.Message);
                    }
                    catch { }
                }
            });
        }

        internal readonly struct OffCurrentClearResult
        {
            public OffCurrentClearResult(bool cleared, double currentA, int elapsedMs)
            {
                Cleared = cleared;
                CurrentA = currentA;
                ElapsedMs = elapsedMs;
            }

            public bool Cleared { get; }
            public double CurrentA { get; }
            public int ElapsedMs { get; }
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
            var boundedTimeoutMs = Math.Max(20, timeoutMs);
            var boundedPollMs = Math.Max(1, pollMs);
            var started = Stopwatch.GetTimestamp();
            var currentA = double.NaN;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                currentA = readCurrent();
                var elapsedMs = (int)Math.Min(
                    int.MaxValue,
                    Math.Max(
                        0,
                        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency));
                if (!double.IsNaN(currentA) &&
                    !double.IsInfinity(currentA) &&
                    currentA <= thresholdA)
                {
                    return new OffCurrentClearResult(true, currentA, elapsedMs);
                }

                if (elapsedMs >= boundedTimeoutMs)
                    return new OffCurrentClearResult(false, currentA, elapsedMs);

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

        private void RaiseAdaptiveWarning(string reason)
        {
            try { WarningRaised?.Invoke(_channel, reason ?? "AdaptiveWarning"); }
            catch { /* UI 订阅者异常不得影响控制 */ }
        }

        private void RaiseAdaptiveWarning(AdaptiveWarningEvent warning)
        {
            if (warning == null) return;
            warning.Channel = _channel;
            if (warning.OccurredUtc == default) warning.OccurredUtc = DateTime.UtcNow;
            try { WarningEvidenceRaised?.Invoke(warning); }
            catch { }
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
                var watchdog = _adaptiveStateMachine.CheckWatchdog(watchdogTick);
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
                    return watchdog;
                }
            }
        }

        private async Task<EpbCycleOutcome> RunOneAdaptiveAsync(int targetPeriodMs, CancellationToken token)
        {
            const int postOffPeakCaptureMs = 100;
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
                CommandForward();
                _log?.Info(
                    $"EPB[{_channel}] 自适应正向上电：软时限={_adaptiveProfile.GetForwardSoftLimitMs()}ms，" +
                    $"硬时限={GetForwardAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> forwardCompletion;
                lock (_adaptiveGate) forwardCompletion = _adaptiveForwardCompletion;
                var forward = await WaitAdaptiveDecisionAsync(forwardCompletion, token).ConfigureAwait(false);
                if (forward.HardFault)
                {
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(forward.Stage, forward.Reason);
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
                string peakEvidenceFailure = null;
                try
                {
                    if (_acq != null &&
                        Interlocked.CompareExchange(ref _adaptiveClampPeakCaptureStarted, 1, 1) == 1)
                    {
                        var captureResult = await _acq.EndEpbCurrentPeakAsync(
                                _adaptivePeakCaptureToken,
                                postOffPeakCaptureMs,
                                cutoffAfterDelay: true,
                                cancellationToken: token)
                            .ConfigureAwait(false);
                        var peak = captureResult.Peak;
                        peakCaptureValid = captureResult.IsMatched &&
                                           peak.SampleCount > 0 && peak.MaxAmp > 0 &&
                                           !double.IsNaN(peak.MaxAmp) && !double.IsInfinity(peak.MaxAmp);
                        if (!captureResult.IsMatched)
                            peakEvidenceFailure = captureResult.QualityReason;
                        else
                        {
                            _adaptiveForwardPeakA = peak.MaxAmp;
                            var quickPeak = forward.ObservedFullRatePeakA;
                            if (!double.IsNaN(quickPeak) && !double.IsInfinity(quickPeak) && quickPeak > 0 &&
                                Math.Abs(quickPeak - peak.MaxAmp) >
                                _programSafetySettings.PeakEvidenceMismatchToleranceA)
                                peakEvidenceFailure =
                                    $"QuickPeak={quickPeak:F3}A FullRatePeak={peak.MaxAmp:F3}A " +
                                    $"Tolerance={_programSafetySettings.PeakEvidenceMismatchToleranceA:F3}A " +
                                    $"CaptureId={_adaptivePeakCaptureToken?.CaptureId:N}";
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

                if (!string.IsNullOrWhiteSpace(peakEvidenceFailure))
                {
                    await hydraulicReleaseTask.ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    var reason = "PeakEvidenceMismatch " + peakEvidenceFailure;
                    try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + reason); } catch { }
                    return EpbCycleOutcome.HardFault(EpbCurrentStage.ClampReached, reason);
                }

                var peakErrorA = peakCaptureValid
                    ? _adaptiveForwardPeakA - _posThrA
                    : double.NaN;
                var overshootStreak = _adaptiveProfile?.ConsecutiveForwardOvershootCount ?? 0;
                if (peakCaptureValid)
                {
                    PersistAdaptiveCutoffObservation(
                        forward.CutoffCurrentA,
                        forward.EstimatedSlopeAperMs,
                        _adaptiveForwardPeakA,
                        peakErrorA);

                    overshootStreak = _adaptiveProfile.UpdateForwardOvershootStreak(
                        peakErrorA,
                        _adaptiveOvershootWarningDeltaA);
                    _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
                    try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
                    catch (Exception ex)
                    {
                        _log?.Warn($"EPB[{_channel}] 超调连续计数保存失败：{ex.Message}", "EPB");
                    }
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
                var stallStreak = _adaptiveProfile.UpdateForwardStallStreak(lowTargetPlateau);
                _adaptiveStateMachine.UpdateProfile(_adaptiveProfile);
                try { _saveAdaptiveProfile?.Invoke(_adaptiveProfile.Clone()); }
                catch (Exception ex)
                {
                    _log?.Warn($"EPB[{_channel}] 正向低平台连续计数保存失败：{ex.Message}", "EPB");
                }

                if (lowTargetPlateau &&
                    IsForwardStallConfirmed(
                        stallStreak,
                        _adaptiveForwardStallConfirmCycles))
                {
                    await hydraulicReleaseTask.ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    var reason =
                        $"ForwardCurrentRiseStalled Peak={observedPeakA:F3}A " +
                        $"Floor={acceptableFloorA:F3}A Target={_posThrA:F3}A " +
                        $"slope={forward.EstimatedSlopeAperMs:F6}A/ms " +
                        $"window={forward.WindowSpanMs}ms median={forward.WindowMedianA:F3}A " +
                        $"Streak={stallStreak}/{_adaptiveForwardStallConfirmCycles}";
                    try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + reason); }
                    catch { }
                    return new EpbCycleOutcome
                    {
                        Kind = EpbCycleOutcomeKind.HardFault,
                        Stage = EpbCurrentStage.ClampReached,
                        Reason = reason,
                        ForwardElapsedMs = _adaptiveForwardElapsedMs,
                        PeakCurrentA = observedPeakA,
                        ControlPeakCurrentA = _adaptiveForwardControlPeakA,
                        TargetCurrentA = _posThrA,
                        CutoffCurrentA = forward.CutoffCurrentA,
                        EstimatedSlopeAperMs = forward.EstimatedSlopeAperMs,
                        PredictedPeakA = forward.PredictedPeakA,
                        PredictionLeadMs = forward.PredictionLeadMs,
                        PeakErrorA = observedPeakA - _posThrA,
                        CutoffReason = forward.CutoffReason,
                        ForwardEmptyCurrentA = _adaptiveForwardEmptyA
                    };
                }

                var immediateOvershoot =
                    peakCaptureValid &&
                    _overshootAlarmDeltaA > 0 &&
                    peakErrorA > _overshootAlarmDeltaA;
                var confirmedOvershoot =
                    peakCaptureValid &&
                    peakErrorA > _adaptiveOvershootWarningDeltaA &&
                    overshootStreak >= _adaptiveOvershootConfirmCycles;
                if (immediateOvershoot || confirmedOvershoot)
                {
                    await hydraulicReleaseTask.ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    var policy = immediateOvershoot
                        ? $"Immediate Limit=+{_overshootAlarmDeltaA:F3}A"
                        : $"Consecutive Streak={overshootStreak}/{_adaptiveOvershootConfirmCycles} " +
                          $"WarningLimit=+{_adaptiveOvershootWarningDeltaA:F3}A";
                    var reason =
                        $"ForwardPeakOvershoot Peak={_adaptiveForwardPeakA:F3}A " +
                        $"Target={_posThrA:F3}A Error={peakErrorA:+0.000;-0.000;0.000}A " +
                        $"Policy={policy}";
                    try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + reason); }
                    catch { }
                    return new EpbCycleOutcome
                    {
                        Kind = EpbCycleOutcomeKind.HardFault,
                        Stage = EpbCurrentStage.ClampReached,
                        Reason = reason,
                        ForwardElapsedMs = _adaptiveForwardElapsedMs,
                        PeakCurrentA = _adaptiveForwardPeakA,
                        ControlPeakCurrentA = _adaptiveForwardControlPeakA,
                        TargetCurrentA = _posThrA,
                        CutoffCurrentA = forward.CutoffCurrentA,
                        EstimatedSlopeAperMs = forward.EstimatedSlopeAperMs,
                        PredictedPeakA = forward.PredictedPeakA,
                        PredictionLeadMs = forward.PredictionLeadMs,
                        PeakErrorA = peakErrorA,
                        CutoffReason = forward.CutoffReason,
                        ForwardEmptyCurrentA = _adaptiveForwardEmptyA
                    };
                }

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
                        Streak = overshootStreak,
                        ConfirmThreshold = _adaptiveOvershootConfirmCycles,
                        Reason =
                            $"正向实际峰值单圈超出平衡带：Peak={_adaptiveForwardPeakA:F3}A，" +
                            $"Target={_posThrA:F3}A，Error={peakErrorA:+0.000;-0.000;0.000}A，" +
                            $"连续={overshootStreak}/{_adaptiveOvershootConfirmCycles}；" +
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
                        Streak = stallStreak,
                        ConfirmThreshold = _adaptiveForwardStallConfirmCycles,
                        Reason =
                            $"正向低于合格下限的平台停滞：Peak={observedPeakA:F3}A，" +
                            $"Floor={acceptableFloorA:F3}A，Target={_posThrA:F3}A，" +
                            $"Slope={forward.EstimatedSlopeAperMs:F6}A/ms，" +
                            $"Window={forward.WindowSpanMs}ms，" +
                            $"连续={stallStreak}/{_adaptiveForwardStallConfirmCycles}；" +
                            "已立即断开正向电，本圈继续完成反向释放并计数。"
                    });
                }

                await hydraulicReleaseTask.ConfigureAwait(false);
                await holdTask.ConfigureAwait(false);

                BeginAdaptiveReverseMonitoring(targetPeriodMs);
                CommandReverse();
                _log?.Info(
                    $"EPB[{_channel}] 自适应反向上电：硬时限={GetReverseAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> reverseCompletion;
                lock (_adaptiveGate) reverseCompletion = _adaptiveReverseCompletion;
                var reverse = await WaitAdaptiveDecisionAsync(reverseCompletion, token).ConfigureAwait(false);
                if (reverse.HardFault)
                {
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(reverse.Stage, reverse.Reason);
                }
                if (!reverse.ReleaseCompleted)
                {
                    DisarmAdaptiveMonitoring();
                    return EpbCycleOutcome.HardFault(reverse.Stage, "ReverseEndedWithoutRelease");
                }

                _adaptiveReverseEmptyA = _adaptiveStateMachine.ObservedReverseEmptyA;
                _adaptiveStateMachine.Disarm();

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
                    ReverseEmptyCurrentA = _adaptiveReverseEmptyA
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
                return outcome;
            }
            catch (HydraulicReleaseTimeoutException ex)
            {
                try { CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                const string code = "HydraulicReleaseTimeout";
                try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + code + " " + ex.Message); } catch { }
                return EpbCycleOutcome.HardFault(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    code + ": " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                try { CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                return EpbCycleOutcome.Canceled(_adaptiveStateMachine?.Stage ?? EpbCurrentStage.Idle, "Canceled");
            }
            catch (Exception ex)
            {
                try { CommandOffHighPriority(); } catch { }
                DisarmAdaptiveMonitoring();
                try { AlarmRaised?.Invoke(_channel, "AdaptiveUnhandledException " + ex.Message); } catch { }
                return EpbCycleOutcome.HardFault(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    "UnhandledException: " + ex.Message);
            }
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
            double peakErrorA)
        {
            if (!_adaptiveProfile.TryAddCutoffObservation(
                    _posThrA,
                    cutoffCurrentA,
                    cutoffSlopeAperMs,
                    actualPeakA,
                    out var equivalentLeadMs))
            {
                _log?.Warn(
                    $"EPB[{_channel}] 控流观测无效，未更新预测模型：" +
                    $"Cutoff={cutoffCurrentA:F3}A Slope={cutoffSlopeAperMs:F4}A/ms " +
                    $"Peak={actualPeakA:F3}A Target={_posThrA:F3}A。",
                    "EPB");
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
