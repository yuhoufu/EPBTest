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
                    BtnStartTest.Text = "暂停试验";
                    BtnStartTest.Enabled = Volatile.Read(ref _batchStartUiGuard) == 0;
                    break;
                case BatchPauseState.PausePending:
                    BtnStartTest.Text = "正在暂停…";
                    BtnStartTest.Enabled = false;
                    break;
                case BatchPauseState.Paused:
                    BtnStartTest.Text = "继续试验";
                    BtnStartTest.Enabled = Volatile.Read(ref _batchStartUiGuard) == 0;
                    break;
                case BatchPauseState.ResumeChecking:
                case BatchPauseState.Qualification:
                    BtnStartTest.Text = "正在恢复…";
                    BtnStartTest.Enabled = false;
                    break;
                case BatchPauseState.Stopping:
                    BtnStartTest.Text = "安全停止中…";
                    BtnStartTest.Enabled = false;
                    break;
                default:
                    if (_pendingGracefulPauseCheckpoint != null)
                    {
                        BtnStartTest.Text = "继续试验";
                        BtnStartTest.Enabled = Volatile.Read(ref _batchStartUiGuard) == 0;
                    }
                    else if (_epb?.IsBatchSessionActive ?? false)
                    {
                        BtnStartTest.Text = "启动中…";
                        BtnStartTest.Enabled = false;
                    }
                    else
                    {
                        BtnStartTest.Text = "开始试验";
                        BtnStartTest.Enabled = Volatile.Read(ref _batchStartUiGuard) == 0;
                    }
                    break;
            }
            ApplyAllChannelOperationStates();
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
                if (state.State == ChannelRuntimeState.AlarmStopped)
                {
                    if (!_epb.CanAcknowledgeChannelAlarm(channel, out var rejection))
                    {
                        MessageBox.Show(
                            rejection,
                            "禁止单通道恢复",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                    var confirmed = MessageBox.Show(
                        $"请确认 EPB{channel} 的故障原因已经排除。\r\n\r\n" +
                        "继续后软件将重新执行安全预检、启动定位和2圈资格复核；" +
                        "资格复核失败会保持报警锁存。是否继续？",
                        "确认报警恢复",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (confirmed)
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

            var isRunning = state.State == ChannelRuntimeState.Starting ||
                            state.State == ChannelRuntimeState.Learning ||
                            state.State == ChannelRuntimeState.Running ||
                            state.State == ChannelRuntimeState.WarningRunning ||
                            state.State == ChannelRuntimeState.PausePending;
            if (toggle.Checked != isRunning) toggle.Checked = isRunning;

            var configuredEnabled = _cfg?.Test?.GetEpbRecord(state.Channel)?.Enabled == true;
            var locallyOperable = state.State == ChannelRuntimeState.Running ||
                                  state.State == ChannelRuntimeState.WarningRunning ||
                                  (state.State == ChannelRuntimeState.Paused && !(_epb?.IsBatchPaused ?? false)) ||
                                  (state.State == ChannelRuntimeState.AlarmStopped &&
                                   _epb.CanAcknowledgeChannelAlarm(state.Channel, out _));
            toggle.Enabled = configuredEnabled && locallyOperable &&
                             !_channelPauseResumeUiGuard.ContainsKey(state.Channel);

            if (group.CtrlJoinTest != null)
                group.CtrlJoinTest.Enabled = !(_epb?.IsBatchSessionActive ?? false);
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
            catch (Exception ex)
            {
                ClearGracefulPauseCheckpoint("QualificationFailedFallbackToFullLearning");
                LogInfo($"正常暂停资格复核失败，将回退完整学习：{ex.Message}");
                _cfg.Test.LearnCycles = Math.Max(5, checkpoint.LearnCycles);
                throw new InvalidOperationException(
                    "正常暂停资格复核失败，检查点已撤销。请再次点击“开始试验”执行完整学习。" +
                    Environment.NewLine + ex.Message,
                    ex);
            }
        }
    }
}
