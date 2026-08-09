using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Timing;

namespace Controller
{
    internal enum TimerRuntimeHealthAction
    {
        None = 0,
        PublishPausePending = 1,
        PublishPaused = 2,
        BeginSelfHealing = 3
    }

    internal sealed class TimerRuntimeHealthDecision
    {
        internal TimerRuntimeHealthAction Action { get; set; }
        internal string ReasonCode { get; set; } = string.Empty;
        internal string ReasonText { get; set; } = string.Empty;
    }

    public sealed partial class EpbManager
    {
        private readonly System.Threading.Timer _timerRuntimeWatchdog;
        private readonly int _timerRuntimeWatchdogIntervalMs;
        private readonly int _timerRuntimeSilenceThresholdMs;
        private readonly ConcurrentDictionary<int, byte> _timerRuntimeRecoveries =
            new ConcurrentDictionary<int, byte>();
        private int _timerRuntimeWatchdogBusy;

        private void AttachTimerRuntimeObserver(int channel, HighPrecisionTimer timer)
        {
            if (timer == null) return;
            timer.StateChanged += update => OnTimerRuntimeStateChanged(channel, timer, update);
        }

        private void OnTimerRuntimeStateChanged(
            int channel,
            HighPrecisionTimer timer,
            HighPrecisionTimerStateChangedEvent update)
        {
            if (update == null) return;
            _log?.Info(
                $"TimerRuntimeState Channel={channel} " +
                $"Instance={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(timer)} " +
                $"State={update.State} IsRunning={update.IsRunning} IsPaused={update.IsPaused} " +
                $"Reason={update.Reason} LastStart={update.LastCycleStartedUtc:O} " +
                $"LastCompleted={update.LastCycleCompletedUtc:O}",
                "Timer");

            var current = _channelRuntimeStateStore.Get(channel);
            if (current == null || !IsDisplayedAsRunning(current.State)) return;

            if (ShouldAutoRecoverTimerAnomaly(
                    current.State,
                    update.State,
                    update.Reason,
                    IsBatchSessionActive,
                    CurrentBatchPauseState,
                    _channelPausedUtc.ContainsKey(channel) ||
                    _manualStopRequestedChannels.ContainsKey(channel),
                    IsAlarmStopRequested(channel),
                    IsChannelEnabled(channel)))
            {
                BeginTimerRuntimeSelfHealing(
                    channel,
                    timer,
                    "TimerRuntimeStateLost",
                    $"Timer状态异常：State={update.State} Reason={update.Reason}");
                return;
            }

            if (update.State == HighPrecisionTimerRuntimeState.PausePending)
            {
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.PausePending,
                    "TimerPausePending",
                    $"控制定时器已收到暂停请求。Reason={update.Reason}",
                    correlationId: _activeBatchId);
            }
            else if (update.State == HighPrecisionTimerRuntimeState.Paused)
            {
                PublishChannelRuntimeState(
                    channel,
                    ChannelRuntimeState.Paused,
                    "TimerPaused",
                    $"控制定时器已暂停。Reason={update.Reason}",
                    correlationId: _activeBatchId);
            }
        }

        private void InspectTimerRuntimeHealth(object state)
        {
            if (!IsBatchSessionActive ||
                Interlocked.Exchange(ref _timerRuntimeWatchdogBusy, 1) != 0)
                return;
            try
            {
                TryLogFieldRuntimeMetrics();
                var nowUtc = DateTime.UtcNow;
                foreach (var pair in _timers.ToArray())
                {
                    var channel = pair.Key;
                    var timer = pair.Value;
                    var runtime = _channelRuntimeStateStore.Get(channel);
                    var decision = EvaluateTimerRuntimeHealth(
                        runtime?.State ?? ChannelRuntimeState.NotEnabled,
                        timer,
                        nowUtc,
                        _timerRuntimeSilenceThresholdMs);
                    if (decision.Action == TimerRuntimeHealthAction.None) continue;

                    if (decision.Action == TimerRuntimeHealthAction.PublishPausePending)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.PausePending,
                            decision.ReasonCode,
                            decision.ReasonText,
                            correlationId: _activeBatchId);
                        continue;
                    }
                    if (decision.Action == TimerRuntimeHealthAction.PublishPaused)
                    {
                        PublishChannelRuntimeState(
                            channel,
                            ChannelRuntimeState.Paused,
                            decision.ReasonCode,
                            decision.ReasonText,
                            correlationId: _activeBatchId);
                        continue;
                    }

                    BeginTimerRuntimeSelfHealing(
                        channel,
                        timer,
                        decision.ReasonCode,
                        decision.ReasonText);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Timer运行一致性看门狗异常已隔离：{ex.Message}", "Timer");
            }
            finally
            {
                Volatile.Write(ref _timerRuntimeWatchdogBusy, 0);
            }
        }

        internal static TimerRuntimeHealthDecision EvaluateTimerRuntimeHealth(
            ChannelRuntimeState runtimeState,
            HighPrecisionTimer timer,
            DateTime nowUtc,
            int silenceThresholdMs)
        {
            var none = new TimerRuntimeHealthDecision();
            if (!IsDisplayedAsRunning(runtimeState) || timer == null) return none;

            if (timer.RuntimeState == HighPrecisionTimerRuntimeState.PausePending)
                return new TimerRuntimeHealthDecision
                {
                    Action = IsIntentionalOrOwnedTimerPauseReason(timer.PauseReason)
                        ? TimerRuntimeHealthAction.PublishPausePending
                        : TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerPausePendingMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已等待暂停。Reason={timer.PauseReason}"
                };
            if (timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused || timer.IsPaused)
                return new TimerRuntimeHealthDecision
                {
                    Action = IsIntentionalOrOwnedTimerPauseReason(timer.PauseReason)
                        ? TimerRuntimeHealthAction.PublishPaused
                        : TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerPausedMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已暂停。Reason={timer.PauseReason}"
                };

            if (!timer.IsRunning ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Completed ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Faulted)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerNotRunning",
                    ReasonText = $"通道仍显示运行，但Timer状态为{timer.RuntimeState}。"
                };

            var thresholdMs = Math.Max(1000, silenceThresholdMs);
            var activityUtc = timer.LastCycleCompletedUtc ?? timer.LastCycleStartedUtc ?? timer.StartedUtc;
            if (activityUtc.HasValue && (nowUtc - activityUtc.Value).TotalMilliseconds > thresholdMs)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.BeginSelfHealing,
                    ReasonCode = "TimerHeartbeatStale",
                    ReasonText = $"Timer超过{thresholdMs}ms没有新循环心跳；" +
                                 $"LastStart={timer.LastCycleStartedUtc:O} " +
                                 $"LastCompleted={timer.LastCycleCompletedUtc:O}。"
                };
            return none;
        }

        private static bool IsDisplayedAsRunning(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.Running ||
                   state == ChannelRuntimeState.WarningRunning;
        }

        internal static bool IsIntentionalOrOwnedTimerPauseReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var ownedPrefixes = new[]
            {
                "BatchGracefulPause",
                "ChannelGracefulPause",
                "ManualPause",
                "Daq",
                "Hydraulic",
                "PowerSupply",
                "RecoverableWarning",
                "FormalPersistence",
                "FormalControl",
                "ActiveCycle",
                "ElectricalGroup",
                "TimerSelfHealing",
                "Watchdog"
            };
            return ownedPrefixes.Any(prefix =>
                reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool ShouldAutoRecoverTimerAnomaly(
            ChannelRuntimeState runtimeState,
            HighPrecisionTimerRuntimeState timerState,
            string reason,
            bool batchActive,
            BatchPauseState batchPauseState,
            bool channelPaused,
            bool alarmStopRequested,
            bool channelEnabled)
        {
            if (!batchActive || !channelEnabled || channelPaused || alarmStopRequested)
                return false;
            if (batchPauseState != BatchPauseState.Running)
                return false;
            if (!IsDisplayedAsRunning(runtimeState))
                return false;

            if (timerState == HighPrecisionTimerRuntimeState.PausePending ||
                timerState == HighPrecisionTimerRuntimeState.Paused)
                return !IsIntentionalOrOwnedTimerPauseReason(reason);

            return timerState == HighPrecisionTimerRuntimeState.Stopped ||
                   timerState == HighPrecisionTimerRuntimeState.Completed ||
                   timerState == HighPrecisionTimerRuntimeState.Faulted;
        }

        private bool CanContinueTimerRuntimeSelfHealing(int channel, Guid runId)
        {
            if (!IsBatchSessionActive || runId == Guid.Empty || runId != _activeBatchId)
                return false;
            if (CurrentBatchPauseState != BatchPauseState.Running ||
                _channelPausedUtc.ContainsKey(channel) ||
                _manualStopRequestedChannels.ContainsKey(channel) ||
                IsAlarmStopRequested(channel) ||
                !IsChannelEnabled(channel))
                return false;
            var runtime = _channelRuntimeStateStore.Get(channel);
            return runtime != null && runtime.State == ChannelRuntimeState.Recovering;
        }

        private void BeginTimerRuntimeSelfHealing(
            int channel,
            HighPrecisionTimer failedTimer,
            string reasonCode,
            string reasonText)
        {
            if (!_timerRuntimeRecoveries.TryAdd(channel, 0)) return;

            var runId = _activeBatchId;
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var sessionToken = _batchSessionCts?.Token ?? CancellationToken.None;
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Recovering,
                "TimerRuntimeSelfHealing",
                $"{reasonText}；已安全断电，正在自动重建Timer并继续测试。",
                correlationId: runId);
            _log?.Error(
                $"EPB[{channel}] Timer运行异常，进入无人值守自恢复。" +
                $"Code={reasonCode} Detail={reasonText}",
                "Timer");

            try { failedTimer?.Pause($"TimerSelfHealing:{reasonCode}"); } catch { }
            try { CancelCyclePauseCts(channel); } catch { }
            UnmarkHydraulicParticipant(channel);
            var cutoffUtc = DateTime.UtcNow;
            var cutoffCycles = CaptureSoftwareRecoveryCycles(new[] { channel });
            TrySealSoftwareRecoveryCycleWindows(
                cutoffCycles,
                cutoffUtc,
                $"TimerRuntimeSelfHealing:{reasonCode}");

            ObserveBackgroundTask(Task.Run(async () =>
            {
                var attempt = 0;
                try
                {
                    while (CanContinueTimerRuntimeSelfHealing(channel, runId))
                    {
                        attempt++;
                        HydraulicRecoveryOwnershipCoordinator.HydraulicRecoveryOwnershipLease ownership = null;
                        try
                        {
                            ownership = await _recoveryOwnership.AcquireAsync(
                                    GetHydraulicGroupForChannel(channel),
                                    $"TIMER:{channel}:{runId:N}",
                                    RecoveryOwnerPriority.Hydraulic,
                                    RecoveryOwnershipTakeoverTimeoutMs,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            var recoveryToken = ownership.Token;
                            RequireSoftwareRecoveryOutputOff(channel, "TimerRuntimeSelfHealing");
                            await HydraulicMarkReleaseAsync(channel).ConfigureAwait(false);
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            if (!await TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync(
                                    cutoffCycles,
                                    cutoffUtc,
                                    $"TimerRuntimeSelfHealing:{reasonCode}",
                                    _daqPersistenceRecoveryTimeoutMs,
                                    recoveryToken)
                                .ConfigureAwait(false))
                                throw new SoftwareSelfHealingRetryException(
                                    "Timer异常圈 Raw/耐久边界尚未闭合；保持断能并继续重试。");

                            await EnsureDaqReadyBeforeStartAsync(
                                    new[] { channel },
                                    recoveryToken)
                                .ConfigureAwait(false);
                            await EnsurePowerSupplyReadyForChannelsAsync(
                                    new[] { channel },
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            var plan = GetCompatibleStaggerPlan(new[] { channel });
                            await EnsureMotorReleasedBeforeFormalRejoinAsync(
                                    new[] { channel },
                                    plan,
                                    "TimerRuntimeSelfHealing",
                                    recoveryToken)
                                .ConfigureAwait(false);
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;

                            RejoinFormalChannelsAtSharedFutureSlot(
                                new[] { channel },
                                plan,
                                "TimerRuntimeSelfHealed",
                                $"Timer异常已完成安全重建并自动续测（Attempt={attempt}）",
                                allowTerminalReset: false);

                            if (!_timers.TryGetValue(channel, out var replacement) ||
                                ReferenceEquals(replacement, failedTimer) ||
                                !replacement.IsRunning)
                                throw new SoftwareSelfHealingRetryException(
                                    "新Timer未进入运行态。");

                            _log?.Info(
                                $"EPB[{channel}] Timer已自动重建并继续测试。Attempt={attempt}",
                                "Timer");
                            return;
                        }
                        catch (Exception ex)
                        {
                            if (!CanContinueTimerRuntimeSelfHealing(channel, runId)) return;
                            if (TryEscalateSoftwareRecoveryCircuitOpen(
                                    "TimerRuntimeSelfHealing",
                                    $"Code={reasonCode}; Error={ex.Message}",
                                    new[] { channel },
                                    runId,
                                    runEpoch,
                                    attempt))
                                return;
                            PublishChannelRuntimeState(
                                channel,
                                ChannelRuntimeState.Recovering,
                                "TimerRuntimeSelfHealingRetry",
                                $"Timer自动重建第{attempt}次未完成；三次失败将整批重建：{ex.Message}",
                                correlationId: runId);
                            _log?.Warn(
                                $"EPB[{channel}] Timer自动重建第{attempt}次失败；三次失败将整批重建：{ex.Message}",
                                "Timer");
                            ownership?.Dispose();
                            ownership = null;
                            await Task.Delay(
                                    SelectTimerRecoveryRetryDelayMs(attempt),
                                    sessionToken)
                                .ConfigureAwait(false);
                        }
                        finally
                        {
                            ownership?.Dispose();
                        }
                    }
                }
                finally
                {
                    _timerRuntimeRecoveries.TryRemove(channel, out _);
                }
            }), "TimerRuntimeSelfHealing", channel);
        }

        internal static int SelectTimerRecoveryRetryDelayMs(int attempt)
        {
            if (attempt <= 1) return 1000;
            if (attempt == 2) return 2000;
            if (attempt == 3) return 5000;
            if (attempt == 4) return 10000;
            return 30000;
        }
    }
}
