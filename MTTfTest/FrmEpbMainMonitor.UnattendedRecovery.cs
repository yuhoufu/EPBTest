using System;
using System.Linq;
using System.Threading.Tasks;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private void AttachUnattendedRecovery()
        {
            if (_epb == null || _cfg == null) return;
            UnattendedRecoveryCoordinator.Attach(_epb, _cfg);
            UnattendedRecoveryCoordinator.RegisterQuiesceAndFlush(
                QuiesceAndFlushForUnattendedRestartAsync);
        }

        private async Task QuiesceAndFlushForUnattendedRestartAsync()
        {
            if (twoDeviceAiAcquirer != null &&
                !await twoDeviceAiAcquirer.StopAndDrainAsync(10000).ConfigureAwait(false))
                throw new TimeoutException("DAQ采集/Raw发布链10秒内未排空。");
            if (_daqDev1 != null)
            {
                await _daqDev1.FlushRawToDiskAsync().ConfigureAwait(false);
                await _daqDev1.FlushStatToDiskAsync().ConfigureAwait(false);
            }
            if (_daqDev2 != null)
            {
                await _daqDev2.FlushRawToDiskAsync().ConfigureAwait(false);
                await _daqDev2.FlushStatToDiskAsync().ConfigureAwait(false);
            }
            twoDeviceAiAcquirer?.Dispose();
        }

        private void UpdateUnattendedRunAuthorization(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null || _cfg?.Test == null) return;
            if (state.State == ChannelRuntimeState.Starting)
            {
                var selected = Enumerable.Range(1, 12)
                    .Where(channel => EpbGroup[channel - 1]?.CtrlJoinTest?.Checked == true)
                    .ToArray();
                UnattendedRecoveryCoordinator.Arm(_cfg, selected);
                return;
            }

            if (state.State != ChannelRuntimeState.Completed) return;
            var checkpoint = UnattendedRunCheckpointStore.Load();
            if (checkpoint == null || !checkpoint.Armed || checkpoint.SelectedChannels == null) return;
            lock (_channelRuntimeStates)
            {
                if (checkpoint.SelectedChannels.All(channel =>
                        _channelRuntimeStates.TryGetValue(channel, out var current) &&
                        current.State == ChannelRuntimeState.Completed))
                    UnattendedRecoveryCoordinator.Disarm("FormalRunCompleted");
            }
        }

        internal async Task ResumeFromUnattendedCheckpointAsync(UnattendedRunCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("实时监视硬件与控制对象初始化超时。所有输出保持关闭。");

            var selected = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("检查点没有有效的测试通道。所有输出保持关闭。");

            _cfg.Test.LearnCycles = Math.Max(5, checkpoint.LearnCycles);
            for (var channel = 1; channel <= 12; channel++)
            {
                var control = EpbGroup[channel - 1]?.CtrlJoinTest;
                if (control != null) control.Checked = selected.Contains(channel);
            }

            PostSafetyStatus(
                $"系统恢复预检：项目={checkpoint.TestName}，通道=[{string.Join(",", selected)}]，" +
                $"重新学习={_cfg.Test.LearnCycles}圈；正式计数从SQLite/XML已完成圈数的下一完整圈继续。",
                false);
            await Task.Delay(250);
            BtnStartTest.PerformClick();
        }

        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            UnattendedRecoveryCoordinator.Disarm("MonitorClosing");
            base.OnFormClosing(e);
        }
    }
}
