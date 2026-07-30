using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Adaptive;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        private readonly EpbControlMode _epbControlMode = EpbControlMode.LegacyFixedTiming;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveCurrentStateMachine _adaptiveStateMachine;
        private readonly EpbAdaptiveProfile _adaptiveProfile;
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
        private string _adaptiveDirection = string.Empty;

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
                _overshootAlarmDeltaA);
            _adaptiveDirection = "Forward";
        }

        private void CompleteAdaptiveForwardMonitoring(int measuredElapsedMs)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;
            _adaptiveForwardElapsedMs = _adaptiveForwardElapsedMs > 0
                ? _adaptiveForwardElapsedMs
                : Math.Max(0, measuredElapsedMs);
            _adaptiveForwardEmptyA = _adaptiveStateMachine.ObservedForwardEmptyA;
            _adaptiveForwardPeakA = _adaptiveStateMachine.PeakCurrentA;
            _adaptiveStateMachine.MarkHold();
        }

        private void BeginAdaptiveReverseMonitoring(int periodMs)
        {
            if (!AdaptiveMonitoringEnabled || _adaptiveStateMachine == null) return;

            _adaptiveForwardEmptyA = _adaptiveForwardEmptyA > 0
                ? _adaptiveForwardEmptyA
                : _adaptiveStateMachine.ObservedForwardEmptyA;
            _adaptiveForwardPeakA = Math.Max(_adaptiveForwardPeakA, _adaptiveStateMachine.PeakCurrentA);

            lock (_adaptiveGate)
            {
                _adaptiveReverseCompletion = NewAdaptiveCompletion();
            }

            _adaptiveStateMachine.ArmReverse(
                AdaptiveNowTicks(),
                Math.Max(100, _peakIgnoreMs),
                GetReverseAbsoluteMaxMs(periodMs),
                RevDecayLimitA,
                _overshootAlarmDeltaA);
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
                decision = _adaptiveStateMachine.OnSample(tick, currentAmp);
            }
            catch
            {
                // 采集回调链路不得因诊断状态机异常而中断。
                return;
            }

            PublishAdaptiveTrace(tick, sampleUtc, decision);
            HandleAdaptiveDecision(decision);
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

            if (decision.SoftWarning)
            {
                _adaptiveSoftWarningSeen = true;
                if (Interlocked.Exchange(ref _adaptiveWarningLatched, 1) == 0)
                    RaiseAdaptiveWarning(decision.Reason);
            }

            if (decision.ClampReached)
            {
                _adaptiveForwardElapsedMs = decision.ElapsedMs;
                _adaptiveForwardPeakA = Math.Max(_adaptiveForwardPeakA, _adaptiveStateMachine.PeakCurrentA);
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

            try { CommandOffHighPriority(); }
            catch { /* 报警链路仍继续 */ }

            TaskCompletionSource<EpbAdaptiveDecision> forward;
            TaskCompletionSource<EpbAdaptiveDecision> reverse;
            lock (_adaptiveGate)
            {
                forward = _adaptiveForwardCompletion;
                reverse = _adaptiveReverseCompletion;
            }

            forward?.TrySetResult(decision);
            reverse?.TrySetResult(decision);

            try { AlarmRaised?.Invoke(_channel, "AdaptiveHardFault " + decision.Reason); }
            catch { /* 上层报警订阅者异常不允许回流采集线程 */ }
        }

        private void RaiseAdaptiveWarning(string reason)
        {
            try { WarningRaised?.Invoke(_channel, reason ?? "AdaptiveWarning"); }
            catch { /* UI 订阅者异常不得影响控制 */ }
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
                    PublishAdaptiveTrace(
                        watchdogTick,
                        DateTime.UtcNow,
                        watchdog);
                    HandleAdaptiveDecision(watchdog);
                    return watchdog;
                }
            }
        }

        private async Task<EpbCycleOutcome> RunOneAdaptiveAsync(int targetPeriodMs, CancellationToken token)
        {
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

                BeginAdaptiveForwardMonitoring(targetPeriodMs);
                _acq?.BeginEpbCurrentPeak(_channel);
                CommandForward();
                _log?.Info(
                    $"EPB[{_channel}] 自适应正向上电：软时限={_adaptiveProfile.GetForwardSoftLimitMs()}ms，" +
                    $"硬时限={GetForwardAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> forwardCompletion;
                lock (_adaptiveGate) forwardCompletion = _adaptiveForwardCompletion;
                var forward = await WaitAdaptiveDecisionAsync(forwardCompletion, token).ConfigureAwait(false);
                if (forward.HardFault)
                    return EpbCycleOutcome.HardFault(forward.Stage, forward.Reason);
                if (!forward.ClampReached)
                    return EpbCycleOutcome.HardFault(forward.Stage, "ForwardEndedWithoutClamp");

                CommandOffHighPriority();
                CompleteAdaptiveForwardMonitoring(forward.ElapsedMs);
                try
                {
                    var peak = _acq?.EndEpbCurrentPeak(_channel);
                    if (peak != null) _adaptiveForwardPeakA = Math.Max(_adaptiveForwardPeakA, peak.Value.MaxAmp);
                }
                catch
                {
                    // 峰值封口失败不改变已经由快速样本确认的夹紧结果。
                }

                if (_manager != null)
                    await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

                if (_holdMs > 0)
                    await Task.Delay(_holdMs, token).ConfigureAwait(false);

                BeginAdaptiveReverseMonitoring(targetPeriodMs);
                CommandReverse();
                _log?.Info(
                    $"EPB[{_channel}] 自适应反向上电：硬时限={GetReverseAbsoluteMaxMs(targetPeriodMs)}ms。",
                    "EPB");

                TaskCompletionSource<EpbAdaptiveDecision> reverseCompletion;
                lock (_adaptiveGate) reverseCompletion = _adaptiveReverseCompletion;
                var reverse = await WaitAdaptiveDecisionAsync(reverseCompletion, token).ConfigureAwait(false);
                if (reverse.HardFault)
                    return EpbCycleOutcome.HardFault(reverse.Stage, reverse.Reason);
                if (!reverse.ReleaseCompleted)
                    return EpbCycleOutcome.HardFault(reverse.Stage, "ReverseEndedWithoutRelease");

                CommandOffHighPriority();
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
                    $"Rev={outcome.ReverseElapsedMs}ms，Peak={outcome.PeakCurrentA:F3}A，结果={outcome.Kind}。",
                    "EPB");
                return outcome;
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

        private static TaskCompletionSource<EpbAdaptiveDecision> NewAdaptiveCompletion()
        {
            return new TaskCompletionSource<EpbAdaptiveDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
