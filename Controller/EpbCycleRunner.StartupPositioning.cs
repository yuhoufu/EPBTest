using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Adaptive;
using IO.NI;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        private const int StartupConfirmSamples = 3;
        private const int StartupTraceMaximumSamples = 10000;
        private const int StartupSlopeWindowMs = 120;
        private const int StartupMinimumForwardProgressDeadlineMs = 5000;
        private const int StartupLearnedForwardMarginMs = 1000;

        internal static StartupForwardTimingBudget ResolveStartupForwardTiming(
            int periodMs,
            int peakIgnoreMs,
            EpbAdaptiveSafetyLimits safetyLimits,
            EpbAdaptiveProfile profile,
            int projectForwardLimitMs)
        {
            var limits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
            var absoluteOnTimeMs = GetForwardAbsoluteMaxMs(periodMs);
            var programProgressDeadlineMs = Math.Max(
                Math.Max(0, peakIgnoreMs) + limits.ForwardProgressConfirmMs,
                limits.ForwardProgressDeadlineMs);
            var learnedProgressDeadlineMs = 0;
            if (profile != null && profile.IsStable && profile.ForwardClampMedianMs > 0)
            {
                learnedProgressDeadlineMs = (int)Math.Ceiling(
                    profile.ForwardClampMedianMs +
                    Math.Max(
                        StartupLearnedForwardMarginMs,
                        4.0 * profile.ForwardClampMadMs));
            }

            var effectiveProgressDeadlineMs = Math.Min(
                absoluteOnTimeMs,
                Math.Max(
                    StartupMinimumForwardProgressDeadlineMs,
                    Math.Max(programProgressDeadlineMs, learnedProgressDeadlineMs)));
            return new StartupForwardTimingBudget
            {
                ProgramProgressDeadlineMs = programProgressDeadlineMs,
                // 项目值仅保留作审计。AdaptiveCurrent 的正式循环不使用它作为硬上限，
                // 启动定位也不得再被旧项目的 3000ms 静默截断。
                ProjectForwardLimitMs = Math.Max(0, projectForwardLimitMs),
                LearnedProgressDeadlineMs = learnedProgressDeadlineMs,
                EffectiveProgressDeadlineMs = effectiveProgressDeadlineMs,
                AbsoluteOnTimeMs = absoluteOnTimeMs
            };
        }

        internal static StartupReverseTimingBudget ResolveStartupReverseTiming(
            int peakIgnoreMs,
            EpbAdaptiveSafetyLimits safetyLimits,
            EpbAdaptiveProfile profile,
            int absoluteOnTimeMs,
            double releaseThresholdA)
        {
            var limits = (safetyLimits ?? new EpbAdaptiveSafetyLimits()).Normalized();
            var absoluteMs = Math.Max(500, absoluteOnTimeMs);
            var programProgressDeadlineMs = Math.Max(
                Math.Max(0, peakIgnoreMs) + limits.ReverseProgressConfirmMs,
                limits.ReverseProgressDeadlineMs);
            var learnedProgressDeadlineMs = 0;
            if (profile != null && profile.IsStable && profile.ReverseReleaseMedianMs > 0)
            {
                learnedProgressDeadlineMs = (int)Math.Ceiling(
                    profile.ReverseReleaseMedianMs +
                    Math.Max(300.0, 4.0 * profile.ReverseReleaseMadMs));
            }

            return new StartupReverseTimingBudget
            {
                ProgramProgressDeadlineMs = programProgressDeadlineMs,
                // 启动定位只使用程序级反向进展期限；正式循环画像保留为审计值，
                // 不能再把部分行程回退提前截断。
                LearnedProgressDeadlineMs = learnedProgressDeadlineMs,
                EffectiveProgressDeadlineMs = Math.Min(absoluteMs, programProgressDeadlineMs),
                AbsoluteOnTimeMs = absoluteMs,
                ReleaseThresholdA = Math.Max(0.1, releaseThresholdA)
            };
        }

        internal async Task<StartupPositioningResult> StartupPositioningAsync(
            int? keepMs,
            int? detectTimeoutMs,
            CancellationToken token)
        {
            var startedTick = Stopwatch.GetTimestamp();
            var trace = new List<StartupPositioningSample>();
            var slopeWindow = new Queue<(long Tick, double Current)>();
            var peakCurrentA = 0.0;
            var lastSlope = 0.0;
            var stage = StartupPositioningStage.ForwardPositioning;
            var lastFreshnessCheckTick = 0L;
            var freshnessReason = string.Empty;
            var forwardConfirmed = false;
            var holdMs = Math.Max(0, keepMs ?? DefaultPreReleaseKeepMs);
            var reverseAbsoluteOnTimeMs = Math.Max(
                500,
                detectTimeoutMs ?? DefaultPreReleaseDetectTimeoutMs);
            PeakCaptureToken forwardPeakCapture = null;
            PeakCaptureToken reversePeakCapture = null;
            var configuredForwardLimit =
                _cfg?.Test?.EpbCycleRunner.GetRunnerChannel(_channel)?.FwdOnLimitMs ?? 0;
            var timing = ResolveStartupForwardTiming(
                _manager?.PeriodMs ?? _cfg?.Test?.PeriodMs ?? 0,
                _peakIgnoreMs,
                _adaptiveSafetyLimits,
                _adaptiveProfile,
                configuredForwardLimit);
            var reverseTiming = ResolveStartupReverseTiming(
                _peakIgnoreMs,
                _adaptiveSafetyLimits,
                _adaptiveProfile,
                reverseAbsoluteOnTimeMs,
                RevDecayLimitA);
            var acceptableHighA = Math.Max(
                0.1,
                _posThrA - _adaptiveSafetyLimits.ForwardAcceptableUndershootA);
            var overCurrentLimitA = _overshootAlarmDeltaA > 0
                ? _posThrA + _overshootAlarmDeltaA
                : double.PositiveInfinity;

            StartupPositioningResult Result(
                bool succeeded,
                StartupPositioningStage failedStage,
                StartupPositioningCompletionKind completion,
                string code,
                string reason)
            {
                return new StartupPositioningResult
                {
                    Channel = _channel,
                    Succeeded = succeeded,
                    Stage = failedStage,
                    CompletionKind = completion,
                    Code = code ?? string.Empty,
                    Reason = reason ?? string.Empty,
                    PeakCurrentA = peakCurrentA,
                    LastSlopeAperMs = lastSlope,
                    ElapsedMs = ElapsedSince(startedTick),
                    ForwardProgramProgressDeadlineMs = timing.ProgramProgressDeadlineMs,
                    ForwardProjectLimitMs = timing.ProjectForwardLimitMs,
                    ForwardLearnedProgressDeadlineMs = timing.LearnedProgressDeadlineMs,
                    ForwardEffectiveProgressDeadlineMs = timing.EffectiveProgressDeadlineMs,
                    ForwardAbsoluteOnTimeMs = timing.AbsoluteOnTimeMs,
                    ReverseProgramProgressDeadlineMs = reverseTiming.ProgramProgressDeadlineMs,
                    ReverseLearnedProgressDeadlineMs = reverseTiming.LearnedProgressDeadlineMs,
                    ReverseEffectiveProgressDeadlineMs = reverseTiming.EffectiveProgressDeadlineMs,
                    ReverseAbsoluteOnTimeMs = reverseTiming.AbsoluteOnTimeMs,
                    ReverseReleaseThresholdA = reverseTiming.ReleaseThresholdA,
                    Samples = trace.ToArray()
                };
            }

            void AddTrace(double currentA, string decision)
            {
                var now = Stopwatch.GetTimestamp();
                var magnitude = Math.Abs(currentA);
                peakCurrentA = Math.Max(peakCurrentA, magnitude);
                slopeWindow.Enqueue((now, magnitude));
                while (slopeWindow.Count > 0 &&
                       ElapsedBetween(slopeWindow.Peek().Tick, now) > StartupSlopeWindowMs)
                    slopeWindow.Dequeue();
                if (slopeWindow.Count >= 2)
                {
                    var first = slopeWindow.Peek();
                    var elapsed = ElapsedBetween(first.Tick, now);
                    lastSlope = elapsed <= 0 ? 0 : (magnitude - first.Current) / elapsed;
                }
                if (trace.Count < StartupTraceMaximumSamples)
                {
                    trace.Add(new StartupPositioningSample
                    {
                        Utc = DateTime.UtcNow,
                        ElapsedMs = ElapsedSince(startedTick),
                        Stage = stage,
                        CurrentA = currentA,
                        SlopeAperMs = lastSlope,
                        Decision = decision ?? string.Empty
                    });
                }
            }

            try
            {
                _log.Info(
                    $"EPB[{_channel}] 启动定位：正向确认位置，" +
                    $"ProgramProgress={timing.ProgramProgressDeadlineMs}ms，" +
                    $"ProjectFwdLimit={timing.ProjectForwardLimitMs}ms(audit-only)，" +
                    $"LearnedProgress={timing.LearnedProgressDeadlineMs}ms，" +
                    $"EffectiveProgress={timing.EffectiveProgressDeadlineMs}ms，" +
                    $"AbsoluteOnTime={timing.AbsoluteOnTimeMs}ms，HighFloor={acceptableHighA:F3}A。",
                    "EPB");
                var forwardDetector = new EpbAdaptiveCurrentStateMachine(
                    _adaptiveProfile?.Clone() ?? new EpbAdaptiveProfile { Channel = _channel });
                var forwardStart = Stopwatch.GetTimestamp();
                forwardDetector.ArmForward(
                    forwardStart,
                    _peakIgnoreMs,
                    timing.AbsoluteOnTimeMs,
                    _posThrA,
                    _safetyMarginA,
                    _overshootAlarmDeltaA,
                    _adaptiveSafetyLimits,
                    timing.EffectiveProgressDeadlineMs);
                forwardPeakCapture = BeginStartupPeakCapture();
                if (!CommandForward(nameof(StartupPositioningAsync)))
                    return Result(false, stage, StartupPositioningCompletionKind.None,
                        "ForwardCommandFailed", "正向输出命令失败。");

                var forwardClassifier = new StartupPositioningCurrentClassifier(
                    _peakIgnoreMs,
                    acceptableHighA,
                    overCurrentLimitA,
                    StartupConfirmSamples);
                while (!forwardConfirmed)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(_sampleMs, token).ConfigureAwait(false);
                    if (!StartupDaqIsFresh(ref lastFreshnessCheckTick, out freshnessReason))
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "DaqSampleStale", freshnessReason);
                    if (!TryReadStartupFastCurrent(
                            out var currentA,
                            out var batchSequence,
                            out var sampleQuality,
                            out var sampleReason))
                    {
                        if (FastPathTripClassifier.HasInvalidControlQuality(sampleQuality))
                            CommandOff(nameof(StartupPositioningAsync));
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            FastPathTripClassifier.HasInvalidControlQuality(sampleQuality)
                                ? "FastPathSignalInvalid"
                                : "InvalidCurrentSample",
                            "正向定位电流采样无效：" + sampleReason);
                    }
                    var magnitude = Math.Abs(currentA);
                    var elapsed = ElapsedBetween(forwardStart, Stopwatch.GetTimestamp());
                    AddTrace(currentA, null);

                    var classification = forwardClassifier.Evaluate(batchSequence, (int)elapsed, currentA);
                    if (classification == StartupCurrentClassification.OverCurrent)
                    {
                        CommandOff(nameof(StartupPositioningAsync));
                        var classified = await ClassifyStartupOverCurrentAsync(
                                forwardPeakCapture,
                                forwardClassifier.PeakCurrentA,
                                overCurrentLimitA,
                                sampleQuality)
                            .ConfigureAwait(false);
                        forwardPeakCapture = null;
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            classified.Code,
                            $"正向快速保护：{classified.Reason}。");
                    }
                    if (classification == StartupCurrentClassification.HighCurrentConfirmed)
                    {
                        AddTrace(currentA, "AlreadyClampedHighCurrent");
                        forwardConfirmed = true;
                        break;
                    }

                    // 高电流候选在形成三点确认前不送入正式曲线机，避免启动定位把
                    // 尚未确认的单点高值提前当成夹紧完成；正式循环另由FastRiseCandidate封口归因。
                    if (forwardClassifier.HasHighCurrentCandidate) continue;
                    var decision = forwardDetector.OnSample(Stopwatch.GetTimestamp(), currentA);
                    if (decision.HardFault)
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "ForwardPositioningFault", decision.Reason);
                    if (decision.Stage == EpbCurrentStage.LoadRise || decision.ClampReached)
                    {
                        AddTrace(currentA, "ForwardLoadRise");
                        forwardConfirmed = true;
                        break;
                    }
                    if (elapsed >= timing.AbsoluteOnTimeMs)
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "ForwardPositioningTimeout",
                            $"正向定位达到绝对上电上限{timing.AbsoluteOnTimeMs}ms，" +
                            $"仍未检测到负载斜坡或夹紧高电流。");
                }

                CommandOff(nameof(StartupPositioningAsync));
                CancelStartupPeakCapture(ref forwardPeakCapture);
                stage = StartupPositioningStage.ForwardOffVerification;
                var off = await PollOffCurrentUntilClearAsync(
                        ReadOffCurrentSample,
                        _adaptiveSafetyLimits.OffCurrentClearThresholdA,
                        _adaptiveSafetyLimits.OffCurrentClearTimeoutMs,
                        _sampleMs,
                        token)
                    .ConfigureAwait(false);
                AddTrace(off.CurrentA, off.Cleared ? "ForwardOffCleared" : "ForwardOffNotCleared");
                if (!off.SampleFresh)
                    return Result(false, stage, StartupPositioningCompletionKind.None,
                        "DaqSampleStale",
                        $"正向断电确认时DAQ样本陈旧：Age={off.SampleAgeMs:F1}ms，无法判断电流是否清零。");
                if (!off.Cleared)
                    return Result(false, stage, StartupPositioningCompletionKind.None,
                        "ForwardOffCurrentNotCleared",
                        $"正向断电后电流未清零：I={off.CurrentA:F3}A，Elapsed={off.ElapsedMs}ms。");

                stage = StartupPositioningStage.ReverseRelease;
                slopeWindow.Clear();
                _log.Info(
                    $"EPB[{_channel}] 启动定位：反向释放，" +
                    $"ProgramProgress={reverseTiming.ProgramProgressDeadlineMs}ms，" +
                    $"LearnedProgress={reverseTiming.LearnedProgressDeadlineMs}ms(audit-only)，" +
                    $"EffectiveProgress={reverseTiming.EffectiveProgressDeadlineMs}ms，" +
                    $"AbsoluteOnTime={reverseTiming.AbsoluteOnTimeMs}ms，" +
                    $"ReleaseThreshold={reverseTiming.ReleaseThresholdA:F3}A。",
                    "EPB");
                var reverseDetector = new EpbAdaptiveCurrentStateMachine(
                    _adaptiveProfile?.Clone() ?? new EpbAdaptiveProfile { Channel = _channel });
                var reverseStart = Stopwatch.GetTimestamp();
                reverseDetector.ArmReverse(
                    reverseStart,
                    _peakIgnoreMs,
                    reverseTiming.AbsoluteOnTimeMs,
                    reverseTiming.ReleaseThresholdA,
                    _overshootAlarmDeltaA,
                    _posThrA,
                    _adaptiveSafetyLimits,
                    RevDecayRigidMaxMs,
                    reverseTiming.EffectiveProgressDeadlineMs);
                reversePeakCapture = BeginStartupPeakCapture();
                if (!CommandReverse(nameof(StartupPositioningAsync)))
                    return Result(false, stage, StartupPositioningCompletionKind.None,
                        "ReverseCommandFailed", "反向输出命令失败。");

                var reverseClassifier = new StartupPositioningCurrentClassifier(
                    _peakIgnoreMs,
                    acceptableHighA,
                    overCurrentLimitA,
                    StartupConfirmSamples);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(_sampleMs, token).ConfigureAwait(false);
                    if (!StartupDaqIsFresh(ref lastFreshnessCheckTick, out freshnessReason))
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "DaqSampleStale", freshnessReason);
                    if (!TryReadStartupFastCurrent(
                            out var currentA,
                            out var batchSequence,
                            out var sampleQuality,
                            out var sampleReason))
                    {
                        if (FastPathTripClassifier.HasInvalidControlQuality(sampleQuality))
                            CommandOff(nameof(StartupPositioningAsync));
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            FastPathTripClassifier.HasInvalidControlQuality(sampleQuality)
                                ? "FastPathSignalInvalid"
                                : "InvalidCurrentSample",
                            "反向释放电流采样无效：" + sampleReason);
                    }
                    var magnitude = Math.Abs(currentA);
                    var elapsed = ElapsedBetween(reverseStart, Stopwatch.GetTimestamp());
                    AddTrace(currentA, null);

                    var classification = reverseClassifier.Evaluate(batchSequence, (int)elapsed, currentA);
                    if (classification == StartupCurrentClassification.OverCurrent)
                    {
                        CommandOff(nameof(StartupPositioningAsync));
                        var classified = await ClassifyStartupOverCurrentAsync(
                                reversePeakCapture,
                                reverseClassifier.PeakCurrentA,
                                overCurrentLimitA,
                                sampleQuality)
                            .ConfigureAwait(false);
                        reversePeakCapture = null;
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            classified.Code,
                            $"反向快速保护：{classified.Reason}。");
                    }
                    if (classification == StartupCurrentClassification.HighCurrentConfirmed)
                    {
                        CancelStartupPeakCapture(ref reversePeakCapture);
                        AddTrace(currentA, "ReverseMechanicalEndpoint");
                        var warning =
                            $"StartupReverseEndpointReached I={magnitude:F3}A Floor={acceptableHighA:F3}A；" +
                            "正向位置已确认，已立即断电并按释放完成继续学习。";
                        NotifyWarningSafely(warning);
                        return Result(true, StartupPositioningStage.Completed,
                            StartupPositioningCompletionKind.ReverseMechanicalEndpoint,
                            "ReverseMechanicalEndpoint", warning);
                    }

                    if (reverseClassifier.HasHighCurrentCandidate) continue;
                    var decision = reverseDetector.OnSample(Stopwatch.GetTimestamp(), currentA);
                    if (decision.HardFault)
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "ReverseReleaseFault", decision.Reason);
                    if (decision.ReleaseCompleted)
                    {
                        AddTrace(currentA, "ReverseEmptyTravel");
                        var holdStart = Stopwatch.GetTimestamp();
                        while (ElapsedBetween(holdStart, Stopwatch.GetTimestamp()) < holdMs)
                        {
                            token.ThrowIfCancellationRequested();
                            await Task.Delay(_sampleMs, token).ConfigureAwait(false);
                            if (!TryReadStartupFastCurrent(
                                    out currentA,
                                    out batchSequence,
                                    out sampleQuality,
                                    out sampleReason))
                            {
                                if (FastPathTripClassifier.HasInvalidControlQuality(sampleQuality))
                                    CommandOff(nameof(StartupPositioningAsync));
                                return Result(false, stage, StartupPositioningCompletionKind.None,
                                    FastPathTripClassifier.HasInvalidControlQuality(sampleQuality)
                                        ? "FastPathSignalInvalid"
                                        : "InvalidCurrentSample",
                                    "反向保持电流采样无效：" + sampleReason);
                            }
                            magnitude = Math.Abs(currentA);
                            AddTrace(currentA, "ReverseReleaseHold");
                            classification = reverseClassifier.Evaluate(
                                batchSequence,
                                (int)ElapsedBetween(reverseStart, Stopwatch.GetTimestamp()),
                                currentA);
                            if (classification == StartupCurrentClassification.OverCurrent)
                            {
                                CommandOff(nameof(StartupPositioningAsync));
                                var classified = await ClassifyStartupOverCurrentAsync(
                                        reversePeakCapture,
                                        reverseClassifier.PeakCurrentA,
                                        overCurrentLimitA,
                                        sampleQuality)
                                    .ConfigureAwait(false);
                                reversePeakCapture = null;
                                return Result(false, stage, StartupPositioningCompletionKind.None,
                                    classified.Code,
                                    $"反向释放保持期间快速保护：{classified.Reason}。");
                            }
                            if (classification == StartupCurrentClassification.HighCurrentConfirmed)
                            {
                                CancelStartupPeakCapture(ref reversePeakCapture);
                                var warning =
                                    $"StartupReverseEndpointReached I={magnitude:F3}A during hold；" +
                                    "已立即断电并按释放完成继续学习。";
                                NotifyWarningSafely(warning);
                                return Result(true, StartupPositioningStage.Completed,
                                    StartupPositioningCompletionKind.ReverseMechanicalEndpoint,
                                    "ReverseMechanicalEndpoint", warning);
                            }
                        }
                        CancelStartupPeakCapture(ref reversePeakCapture);
                        return Result(true, StartupPositioningStage.Completed,
                            StartupPositioningCompletionKind.ReverseEmptyTravel,
                            "ReverseEmptyTravel", "已确认反向空行程并完成释放保持。");
                    }
                    if (elapsed >= reverseTiming.AbsoluteOnTimeMs)
                        return Result(false, stage, StartupPositioningCompletionKind.None,
                            "ReverseReleaseTimeout",
                            $"反向释放在{reverseTiming.AbsoluteOnTimeMs}ms内未确认空行程或负向终点。");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Result(false, stage, StartupPositioningCompletionKind.None,
                    "StartupPositioningException", ex.Message);
            }
            finally
            {
                CancelStartupPeakCapture(ref forwardPeakCapture);
                CancelStartupPeakCapture(ref reversePeakCapture);
                if (!CommandOff(nameof(StartupPositioningAsync)))
                    throw new EpbOutputCommandException(
                        _channel,
                        "StartupPositioningFinalOff");
            }
        }

        private static int ElapsedSince(long startTick)
        {
            return (int)Math.Min(int.MaxValue, ElapsedBetween(startTick, Stopwatch.GetTimestamp()));
        }

        private static double ElapsedBetween(long startTick, long endTick)
        {
            return Math.Max(0, (endTick - startTick) * 1000.0 / Stopwatch.Frequency);
        }

        private bool StartupDaqIsFresh(ref long lastCheckTick, out string reason)
        {
            reason = string.Empty;
            if (_acq == null) return true;
            var now = Stopwatch.GetTimestamp();
            if (lastCheckTick != 0 && ElapsedBetween(lastCheckTick, now) < 50) return true;
            lastCheckTick = now;
            var device = _acq.GetDeviceForEpbChannel(_channel);
            if (string.IsNullOrWhiteSpace(device))
            {
                reason = $"EPB[{_channel}]没有有效DAQ设备映射。";
                return false;
            }
            var snapshot = _acq.GetDaqFreshnessSnapshot(device, 100);
            if (snapshot.IsFresh) return true;
            reason =
                $"DAQ样本陈旧：Device={device} Age={snapshot.AgeMs:F1}ms，要求≤100ms。";
            return false;
        }

        private bool TryReadStartupFastCurrent(
            out double currentA,
            out long batchSequence,
            out FastSignalQualityFlags qualityFlags,
            out string reason)
        {
            currentA = double.NaN;
            batchSequence = 0;
            qualityFlags = FastSignalQualityFlags.None;
            reason = string.Empty;
            if (_acq == null)
            {
                currentA = _readCurrent(_channel);
                if (!double.IsNaN(currentA) && !double.IsInfinity(currentA)) return true;
                reason = "LegacyCurrentNonFinite";
                return false;
            }

            var snapshot = _acq.ReadCurrentFastSample(_channel);
            if (!snapshot.Available)
            {
                reason = "FastSampleUnavailable";
                return false;
            }
            if (!snapshot.IsFreshAndUsable(100))
            {
                qualityFlags = snapshot.Sample.QualityFlags;
                reason = $"FastSampleInvalid Age={snapshot.AgeMs:F1}ms Quality={snapshot.Sample.QualityFlags}";
                return false;
            }
            currentA = snapshot.Sample.CurrentA;
            batchSequence = snapshot.Sample.BatchSequence;
            qualityFlags = snapshot.Sample.QualityFlags;
            return true;
        }

        private PeakCaptureToken BeginStartupPeakCapture()
        {
            if (_acq == null) return null;
            try
            {
                var runId = Guid.Empty;
                var cycleNumber = 0;
                _manager?.GetPeakCaptureIdentity(_channel, out runId, out cycleNumber);
                return _acq.BeginEpbCurrentPeak(_channel, runId, cycleNumber);
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_channel}] 启动定位全速率证据启动失败：{ex.Message}", "EPB");
                return null;
            }
        }

        private void CancelStartupPeakCapture(ref PeakCaptureToken token)
        {
            if (token == null) return;
            try { _acq?.CancelEpbCurrentPeak(token); } catch { }
            token = null;
        }

        private async Task<FastPathTripResult> ClassifyStartupOverCurrentAsync(
            PeakCaptureToken token,
            double fastPeakA,
            double overCurrentLimitA,
            FastSignalQualityFlags triggeringQuality)
        {
            double fullRatePeakA = double.NaN;
            double evidenceAgeMs = double.PositiveInfinity;
            try
            {
                if (_acq != null && token != null)
                {
                    var capture = await _acq.EndEpbCurrentPeakAsync(
                            token,
                            100,
                            cutoffAfterDelay: true,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    if (capture.IsMatched && capture.IsCutoffCovered &&
                        capture.Peak.SampleCount > 0)
                    {
                        fullRatePeakA = capture.Peak.MaxAmp;
                        evidenceAgeMs = ResolvePeakEvidenceTailLagMs(capture);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"EPB[{_channel}] 启动定位全速率证据封口失败：{ex.Message}", "EPB");
            }

            return FastPathTripClassifier.ClassifyOverCurrent(
                Math.Abs(fastPeakA),
                overCurrentLimitA,
                fullRatePeakA,
                evidenceAgeMs,
                _programSafetySettings.PeakEvidenceMismatchToleranceA,
                _programSafetySettings.PeakEvidenceMaximumLagMs,
                triggeringQuality);
        }
    }
}
