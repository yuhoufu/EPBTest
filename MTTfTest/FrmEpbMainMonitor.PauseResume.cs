using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _pauseResumeUiAttached;
        private readonly ConcurrentDictionary<int, byte> _channelPauseResumeUiGuard =
            new ConcurrentDictionary<int, byte>();
        private UnattendedRunCheckpoint _pendingGracefulPauseCheckpoint;

        private void AttachPauseResumeUi()
        {
            if (_epb == null || Interlocked.Exchange(ref _pauseResumeUiAttached, 1) != 0)
                return;

            _epb.BatchPauseStateChanged += OnBatchPauseStateChanged;
            for (var index = 0; index < EpbGroup.Length; index++)
            {
                var channel = index + 1;
                var toggle = EpbGroup[index]?.CtrlRunning;
                if (toggle != null)
                    toggle.Click += async (sender, args) =>
                        await HandleChannelRunToggleAsync(channel).ConfigureAwait(true);
            }

            ApplyBatchPauseState(_epb.CurrentBatchPauseState);
            ApplyAllChannelOperationStates();
        }

        private void OnBatchPauseStateChanged(BatchPauseStateChangedEvent update)
        {
            if (update == null || IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired)
                    BeginInvoke((Action)(() => ApplyBatchPauseState(update.State)));
                else
                    ApplyBatchPauseState(update.State);
            }
            catch { }
        }

        private void ApplyBatchPauseState(BatchPauseState state)
        {
            if (BtnStartTest == null || BtnStartTest.IsDisposed) return;
            switch (state)
            {
                case BatchPauseState.Running:
                    ApplyBatchActionButton("暂停试验", Volatile.Read(ref _batchStartUiGuard) == 0, false);
                    break;
                case BatchPauseState.PausePending:
                    ApplyBatchActionButton("正在暂停…", false, true);
                    break;
                case BatchPauseState.Paused:
                    ApplyBatchActionButton("继续试验", Volatile.Read(ref _batchStartUiGuard) == 0, false);
                    break;
                case BatchPauseState.ResumeChecking:
                case BatchPauseState.Qualification:
                    ApplyBatchActionButton("正在恢复…", false, true);
                    break;
                case BatchPauseState.Stopping:
                    ApplyBatchActionButton("安全停止中…", false, true);
                    break;
                default:
                    if (_pendingGracefulPauseCheckpoint != null)
                    {
                        ApplyBatchActionButton(
                            "继续试验",
                            Volatile.Read(ref _batchStartUiGuard) == 0,
                            false);
                    }
                    else if (_epb?.IsBatchSessionActive ?? false)
                    {
                        ApplyBatchActionButton(
                            "重新开始",
                            Volatile.Read(ref _batchStartUiGuard) == 0,
                            false);
                    }
                    else
                    {
                        ApplyBatchActionButton(
                            "开始试验",
                            Volatile.Read(ref _batchStartUiGuard) == 0,
                            false);
                    }
                    break;
            }
            ApplyAllChannelOperationStates();
        }

        private void ApplyBatchActionButton(string text, bool enabled, bool transition)
        {
            BtnStartTest.Text = text;
            BtnStartTest.Enabled = enabled;
            BtnStartTest.Cursor = enabled
                ? Cursors.Hand
                : transition ? Cursors.WaitCursor : Cursors.Default;
        }

        private async Task HandleChannelRunToggleAsync(int channel)
        {
            if (!_channelPauseResumeUiGuard.TryAdd(channel, 0))
                return;
            try
            {
                ChannelRuntimeStateChangedEvent state;
                lock (_channelRuntimeStates)
                    _channelRuntimeStates.TryGetValue(channel, out state);
                if (state == null)
                    state = new ChannelRuntimeStateChangedEvent
                    {
                        Channel = channel,
                        State = ChannelRuntimeState.NotEnabled,
                        TimestampUtc = DateTime.UtcNow
                    };
                UpdatePauseResumeChannelUi(state);

                if (state == null)
                    return;
                if (state.State == ChannelRuntimeState.Running ||
                    state.State == ChannelRuntimeState.WarningRunning)
                {
                    await _epb.PauseChannelGracefullyAsync(channel).ConfigureAwait(true);
                    return;
                }
                if (state.State == ChannelRuntimeState.Paused && !_epb.IsBatchPaused)
                {
                    await _epb.ResumePausedChannelAsync(channel).ConfigureAwait(true);
                    return;
                }
                if (_epb.CanAcknowledgeChannelAlarm(channel, out _))
                {
                    ApplyChannelStartTransitionUi(channel);
                    LogInfo($"EPB{channel} 重新开始：已抛弃上次故障锁存，进入实时预检与自愈。");
                    await _epb.ResumeAlarmStoppedChannelAsync(channel, true).ConfigureAwait(true);
                    return;
                }

                LogInfo($"EPB{channel} 当前状态为{state.State}，不允许通过RUN/STOP直接转换。");
            }
            catch (Exception ex)
            {
                LogInfo($"EPB{channel} RUN/STOP操作失败：{ex.Message}");
                MessageBox.Show(
                    ex.Message,
                    $"EPB{channel} 状态转换失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _channelPauseResumeUiGuard.TryRemove(channel, out _);
                ChannelRuntimeStateChangedEvent state;
                lock (_channelRuntimeStates)
                    _channelRuntimeStates.TryGetValue(channel, out state);
                if (state == null)
                    state = new ChannelRuntimeStateChangedEvent
                    {
                        Channel = channel,
                        State = ChannelRuntimeState.NotEnabled,
                        TimestampUtc = DateTime.UtcNow
                    };
                UpdatePauseResumeChannelUi(state);
            }
        }

        private void ApplyAllChannelOperationStates()
        {
            for (var channel = 1; channel <= 12; channel++)
            {
                ChannelRuntimeStateChangedEvent state;
                lock (_channelRuntimeStates)
                    _channelRuntimeStates.TryGetValue(channel, out state);
                UpdatePauseResumeChannelUi(state);
            }
        }

        private void UpdatePauseResumeChannelUi(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null || state.Channel < 1 || state.Channel > 12) return;
            var group = EpbGroup[state.Channel - 1];
            var toggle = group?.CtrlRunning;
            if (toggle == null) return;

            var isRunning = IsChannelRunToggleActiveState(state.State);
            toggle.CheckedText = GetChannelRunToggleCheckedText(state.State);
            toggle.UncheckedText = "STOP";
            if (toggle.Checked != isRunning) toggle.Checked = isRunning;

            var configuredEnabled = _cfg?.Test?.GetEpbRecord(state.Channel)?.Enabled == true;
            var locallyOperable = state.State == ChannelRuntimeState.Running ||
                                  state.State == ChannelRuntimeState.WarningRunning ||
                                  (state.State == ChannelRuntimeState.Paused && !(_epb?.IsBatchPaused ?? false)) ||
                                  _epb.CanAcknowledgeChannelAlarm(state.Channel, out _);
            toggle.Enabled = configuredEnabled && locallyOperable &&
                             !_channelPauseResumeUiGuard.ContainsKey(state.Channel);
            toggle.Cursor = toggle.Enabled
                ? Cursors.Hand
                : IsChannelRunTransitionState(state.State)
                    ? Cursors.WaitCursor
                    : Cursors.Default;

            if (group.CtrlJoinTest != null)
                group.CtrlJoinTest.Enabled = !(_epb?.IsBatchSessionActive ?? false);
        }

        private void ApplyChannelStartTransitionUi(int channel)
        {
            if (channel < 1 || channel > EpbGroup.Length) return;
            var toggle = EpbGroup[channel - 1]?.CtrlRunning;
            if (toggle != null)
            {
                toggle.CheckedText = "启动中";
                toggle.Checked = true;
                toggle.Enabled = false;
                toggle.Cursor = Cursors.WaitCursor;
            }

            if (_channelRuntimeLabels.TryGetValue(channel, out var label))
            {
                label.Text = "启动中";
                label.BackColor = System.Drawing.Color.FromArgb(41, 128, 185);
                label.ForeColor = System.Drawing.Color.White;
                label.Cursor = Cursors.WaitCursor;
                _channelRuntimeToolTip?.SetToolTip(
                    label,
                    $"EPB{channel:D2} 启动中\r\n正在执行实时安全预检与自愈，请勿重复点击。");
            }
        }

        private static bool IsChannelRunToggleActiveState(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.Starting ||
                   state == ChannelRuntimeState.Learning ||
                   state == ChannelRuntimeState.Running ||
                   state == ChannelRuntimeState.WarningRunning ||
                   state == ChannelRuntimeState.PausePending ||
                   state == ChannelRuntimeState.ResumeChecking ||
                   state == ChannelRuntimeState.Qualification;
        }

        private static bool IsChannelRunTransitionState(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.Starting ||
                   state == ChannelRuntimeState.Learning ||
                   state == ChannelRuntimeState.PausePending ||
                   state == ChannelRuntimeState.ResumeChecking ||
                   state == ChannelRuntimeState.Qualification;
        }

        private static string GetChannelRunToggleCheckedText(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.Starting:
                    return "启动中";
                case ChannelRuntimeState.Learning: return "学习中";
                case ChannelRuntimeState.ResumeChecking: return "恢复预检";
                case ChannelRuntimeState.Qualification: return "资格复核";
                case ChannelRuntimeState.PausePending: return "等待暂停";
                case ChannelRuntimeState.WarningRunning: return "软预警";
                case ChannelRuntimeState.Running: return "运行";
                default: return "RUN";
            }
        }

        private void SaveGracefulPauseCheckpoint()
        {
            var pausedUtc = _epb?.BatchPausedUtc ?? DateTime.UtcNow;
            var channels = _epb?.GetChannelRuntimeStates()
                .Where(state => state.State == ChannelRuntimeState.Paused)
                .Select(state => state.Channel)
                .ToArray() ?? Array.Empty<int>();
            UnattendedRunCheckpointStore.SaveGracefulPause(_cfg, channels, pausedUtc);
        }

        private void ClearGracefulPauseCheckpoint(string reason)
        {
            _pendingGracefulPauseCheckpoint = null;
            UnattendedRunCheckpointStore.ClearGracefulPause(reason);
        }

        internal async Task PrepareGracefulPauseCheckpointAsync(UnattendedRunCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100).ConfigureAwait(true);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("实时监视硬件与控制对象初始化超时。");

            _pendingGracefulPauseCheckpoint = checkpoint;
            var selected = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            for (var channel = 1; channel <= 12; channel++)
            {
                var control = EpbGroup[channel - 1]?.CtrlJoinTest;
                if (control != null) control.Checked = selected.Contains(channel);
            }
            ApplyBatchPauseState(BatchPauseState.Idle);
            PostSafetyStatus(
                $"检测到正常暂停检查点：项目={checkpoint.TestName}，通道=[{string.Join(",", selected)}]。" +
                "点击“继续试验”后先执行2圈资格复核，资格圈不计入正式目标。",
                false);
        }

        private async Task<bool> TryResumePendingGracefulPauseAsync()
        {
            var checkpoint = _pendingGracefulPauseCheckpoint;
            if (checkpoint == null) return false;

            var checkpointChannels = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            var remaining = checkpoint.RemainingFormalCycles ??
                            new System.Collections.Generic.Dictionary<string, int>();
            var remainingByChannel = checkpointChannels.ToDictionary(
                channel => channel,
                channel => remaining.TryGetValue(channel.ToString(), out var count)
                    ? Math.Max(0, count)
                    : Math.Max(0, _cfg.Test.GetEpbRecord(channel).TotalCount -
                                  _cfg.Test.GetEpbRecord(channel).RunCount));
            var channels = checkpointChannels
                .Where(channel => remainingByChannel[channel] > 0)
                .ToArray();
            if (channels.Length == 0)
            {
                ClearGracefulPauseCheckpoint("NoRemainingFormalCycles");
                throw new InvalidOperationException("暂停检查点中的所有通道均已完成目标圈数。");
            }
            _epb.EpbTestCycle = remainingByChannel
                .Where(pair => pair.Value > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            if (_batchCts != null)
            {
                _batchCts.Dispose();
                _batchCts = null;
            }
            _batchCts = new CancellationTokenSource();
            try
            {
                await _epb.StartBatchFromGracefulCheckpointAsync(
                        channels,
                        qualificationCycles: 2,
                        _batchCts.Token)
                    .ConfigureAwait(true);
                ClearGracefulPauseCheckpoint("GracefulCheckpointResumed");
                foreach (var channel in channels)
                {
                    var record = EnsureEpbRecord(channel);
                    record.MarkTestStarted(DateTime.Now);
                    RefreshCurrentEpbSummary(channel);
                }
                return true;
            }
            catch (OperationCanceledException) when (_batchCts?.IsCancellationRequested ?? false)
            {
                throw;
            }
            catch (Exception ex)
            {
                ClearGracefulPauseCheckpoint("QualificationFailedFallbackToFullLearning");
                var learnCycles = Math.Max(5, checkpoint.LearnCycles);
                _cfg.Test.LearnCycles = learnCycles;
                LogInfo(
                    $"正常暂停资格复核未通过：{ex.Message}；" +
                    $"同一次点击将抛弃旧恢复状态并自动转为完整{learnCycles}圈学习。");
                try
                {
                    try { _batchCts?.Cancel(); } catch (ObjectDisposedException) { }
                    _batchCts?.Dispose();
                    _batchCts = null;

                    await _epb.PrepareForFreshRestartAsync(
                            new StopContext
                            {
                                Source = StopSource.ManualUi,
                                Reason = "暂停资格复核未通过；同一次请求自动回退完整学习",
                                Initiator = nameof(TryResumePendingGracefulPauseAsync),
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                RequestedUtc = DateTime.UtcNow
                            })
                        .ConfigureAwait(true);
                    if (_alarmManager != null)
                    {
                        try
                        {
                            await _alarmManager.ClearAllAsync().ConfigureAwait(true);
                        }
                        catch (Exception alarmEx)
                        {
                            LogInfo(
                                $"暂停恢复回退时声光报警复位异常，但不阻碍完整学习重启：" +
                                alarmEx.Message);
                        }
                    }

                    _batchCts = new CancellationTokenSource();
                    var startResult = await _epb.StartBatchSynchronizedWithResultAsync(
                            channels,
                            learnCycles,
                            _batchCts.Token)
                        .ConfigureAwait(true);
                    foreach (var channel in channels)
                    {
                        var record = EnsureEpbRecord(channel);
                        record.MarkTestStarted(DateTime.Now);
                        RefreshCurrentEpbSummary(channel);
                    }
                    LogInfo(
                        $"暂停恢复已自动完成完整学习并重新开始；" +
                        $"运行通道=[{string.Join(",", startResult.StartedChannels)}]。" );
                    return true;
                }
                catch (OperationCanceledException) when (_batchCts?.IsCancellationRequested ?? false)
                {
                    throw;
                }
                catch (Exception fallbackEx)
                {
                    throw new InvalidOperationException(
                        "暂停资格复核和同次完整学习重启均未通过；" +
                        "若为软件瞬态会继续自愈，此处为本次实时确认的硬件/配置故障。" +
                        Environment.NewLine + fallbackEx.Message,
                        new AggregateException(ex, fallbackEx));
                }
            }
        }
    }
}
