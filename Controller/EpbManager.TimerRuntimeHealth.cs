using System;
using System.Threading;
using Timing;

namespace Controller
{
    internal enum TimerRuntimeHealthAction
    {
        None = 0,
        PublishPausePending = 1,
        PublishPaused = 2,
        PublishSystemFault = 3
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

                    PublishChannelRuntimeState(
                        channel,
                        ChannelRuntimeState.SystemFault,
                        decision.ReasonCode,
                        decision.ReasonText,
                        correlationId: _activeBatchId);
                    try { timer.Pause($"Watchdog:{decision.ReasonCode}"); } catch { }
                    try { CancelCyclePauseCts(channel); } catch { }
                    try { CommandEpbOffSafetyImmediate(channel); } catch { }
                    _log?.Error(
                        $"EPB[{channel}] 运行心跳失活，已切换系统故障并执行安全断电。" +
                        $"Code={decision.ReasonCode} Detail={decision.ReasonText}",
                        "Timer");
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
                    Action = TimerRuntimeHealthAction.PublishPausePending,
                    ReasonCode = "TimerPausePendingMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已等待暂停。Reason={timer.PauseReason}"
                };
            if (timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused || timer.IsPaused)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.PublishPaused,
                    ReasonCode = "TimerPausedMismatch",
                    ReasonText = $"通道仍显示运行，但Timer已暂停。Reason={timer.PauseReason}"
                };

            if (!timer.IsRunning ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Completed ||
                timer.RuntimeState == HighPrecisionTimerRuntimeState.Faulted)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.PublishSystemFault,
                    ReasonCode = "TimerNotRunning",
                    ReasonText = $"通道仍显示运行，但Timer状态为{timer.RuntimeState}。"
                };

            var thresholdMs = Math.Max(1000, silenceThresholdMs);
            var activityUtc = timer.LastCycleCompletedUtc ?? timer.LastCycleStartedUtc ?? timer.StartedUtc;
            if (activityUtc.HasValue && (nowUtc - activityUtc.Value).TotalMilliseconds > thresholdMs)
                return new TimerRuntimeHealthDecision
                {
                    Action = TimerRuntimeHealthAction.PublishSystemFault,
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
    }
}
