using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using MTEmbTest;

namespace MtEmbTest
{
    public partial class Main_Frm
    {
        private RecoveryStartupIntent _recoveryStartupIntent;
        private WatchdogRecoveryIntent _watchdogRecoveryIntent;

        internal Main_Frm(RecoveryStartupIntent recoveryStartupIntent) : this()
        {
            _recoveryStartupIntent = recoveryStartupIntent;
        }

        internal Main_Frm(
            RecoveryStartupIntent recoveryStartupIntent,
            WatchdogRecoveryIntent watchdogRecoveryIntent) : this(recoveryStartupIntent)
        {
            _watchdogRecoveryIntent = watchdogRecoveryIntent;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_watchdogRecoveryIntent != null)
            {
                var watchdogIntent = _watchdogRecoveryIntent;
                _watchdogRecoveryIntent = null;
                BeginInvoke((Action)(async () => await ResumeWatchdogRunAsync(watchdogIntent)));
                return;
            }
            if (_recoveryStartupIntent == null)
            {
                BeginInvoke((Action)(async () => await PrepareGracefulPauseResumeAsync()));
                return;
            }
            var intent = _recoveryStartupIntent;
            _recoveryStartupIntent = null;
            BeginInvoke((Action)(async () => await ResumeUnattendedRunAsync(intent)));
        }

        private async Task ResumeWatchdogRunAsync(WatchdogRecoveryIntent intent)
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryConsumeWatchdogRecovery(
                        intent.SessionId,
                        Cfg,
                        out var checkpoint,
                        out var error))
                {
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "独立看门狗恢复已拒绝：" + error + "；所有输出保持关闭。",
                        "独立看门狗");
                    BeginInvoke((Action)(() => Close()));
                    return;
                }

                await WatchdogRuntime.AttachRecoverySessionAsync(
                    intent,
                    checkpoint.SelectedChannels).ConfigureAwait(true);
                var monitor = new FrmEpbMainMonitor { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.ResumeFromWatchdogCheckpointAsync(checkpoint, intent)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "独立看门狗恢复进程安全接管或启动失败；保持本轮授权，等待 Watchdog 退避重试：" + ex.Message,
                    "独立看门狗",
                    ex);
                WatchdogRuntime.RequestExternalRecovery("WatchdogRecoveryStartupFailed:" + ex.GetBaseException().Message);
                BeginInvoke((Action)(() => Close()));
            }
        }

        private async Task PrepareGracefulPauseResumeAsync()
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryLoadGracefulPause(
                        Cfg,
                        out var checkpoint,
                        out _))
                    return;

                var monitor = new FrmEpbMainMonitor { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.PrepareGracefulPauseCheckpointAsync(checkpoint);
            }
            catch (Exception ex)
            {
                UnattendedRunCheckpointStore.ClearGracefulPause("GracefulPauseLoadFailed");
                ShowMainOperatorMessage(
                    "正常暂停检查点加载失败：" + ex.Message +
                    "\r\n检查点已撤销，下次开始将执行完整学习。",
                    "暂停恢复",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private async Task ResumeUnattendedRunAsync(RecoveryStartupIntent intent)
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryConsume(
                        intent,
                        Cfg,
                        out var checkpoint,
                        out var error))
                {
                    // 未授权、过期或身份不一致的恢复参数只能保持全断能，不能弹出一个
                    // 必须由现场人员确认的模态框。该次十万圈由现场门禁判失败。
                    ProjectLogHub.Write(
                        ProjectLogLevel.Error,
                        "无人值守自动续测已拒绝：" + error + "；所有输出保持关闭。",
                        "无人值守恢复");
                    return;
                }

                var monitor = new FrmEpbMainMonitor { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.ResumeFromUnattendedCheckpointAsync(checkpoint);
            }
            catch (Exception ex)
            {
                // nonce 已消费后的初始化/启动失败仍属于同一授权恢复链。保留检查点、
                // RootRunId 和重启历史，按既有三次预算再做受控进程交接；预算耗尽后
                // RestartAsync 会保持安全停机。禁止以消息框代替无人值守恢复。
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "无人值守恢复预检或批量启动失败，将进入有界进程重试：" + ex.Message,
                    "无人值守恢复",
                    ex);
                UnattendedRecoveryCoordinator.RequestRecoveryStartupRestart(
                    "RecoveryStartupFailed",
                    ex);
            }
        }
    }
}
