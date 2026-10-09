using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class Main_Frm
    {
        private ToolStripMenuItem _installationAlarmMenu;
        private Timer _installationAlarmTimer;
        private bool _installationAlarmRefreshRunning;
        private bool _idleAlarmCloseRunning;
        private bool _idleAlarmCloseConfirmed;

        private bool TryQueueIdleAlarmClose(FormClosingEventArgs e)
        {
            var enabled = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.UnattendedMode.required")) ||
                File.Exists(IndependentInstallationBinding.PathFor(Application.ExecutablePath));
            if (!enabled || _idleAlarmCloseConfirmed) return false;
            e.Cancel = true;
            if (!_idleAlarmCloseRunning)
            {
                _idleAlarmCloseRunning = true;
                BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        var state = await InstallationAlarmClient.CompleteSafeCloseAsync();
                        Logger?.Info("正常空闲关闭声光命令已发送；Revision=" + state.Revision +
                            ";PhysicalState=Unverified;LatchPreserved=" + state.Latched, "报警");
                        _idleAlarmCloseConfirmed = true;
                        if (!IsDisposed) Close();
                    }
                    catch (Exception error)
                    {
                        Logger?.Warn("正常空闲关闭声光未确认：" + error.GetBaseException().Message, "报警");
                        if (!IsDisposed) MessageBox.Show(this, "声光关闭未确认，窗口保持打开。\r\n" +
                            error.GetBaseException().Message + "\r\n可查看安装报警状态后再次关闭。",
                            "声光关闭未确认", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    finally { _idleAlarmCloseRunning = false; }
                }));
            }
            return true;
        }

        private void InitializeInstallationAlarmMenu()
        {
            _installationAlarmMenu = new ToolStripMenuItem("安装报警：待读取");
            _installationAlarmMenu.Click += async (_, __) => await ShowInstallationAlarmAsync();
            menuStripMain.Items.Add(_installationAlarmMenu);
            _installationAlarmTimer = new Timer { Interval = 5000 };
            _installationAlarmTimer.Tick += async (_, __) => await RefreshInstallationAlarmMenuAsync();
            Shown += async (_, __) =>
            {
                _installationAlarmTimer.Start();
                await RefreshInstallationAlarmMenuAsync();
            };
            FormClosed += (_, __) => { _installationAlarmTimer.Stop(); _installationAlarmTimer.Dispose(); };
        }

        private async Task RefreshInstallationAlarmMenuAsync()
        {
            if (_installationAlarmRefreshRunning || IsDisposed) return;
            _installationAlarmRefreshRunning = true;
            try
            {
                var state = (await Task.Run(() => InstallationAlarmClient.Send(InstallationAlarmAction.Query))).State;
                if (IsDisposed) return;
                _installationAlarmMenu.Text = "安装报警：" + AlarmStateText(state);
                _installationAlarmMenu.ForeColor = state.Latched ? Color.DarkRed : SystemColors.ControlText;
            }
            catch
            {
                if (!IsDisposed) _installationAlarmMenu.Text = "安装报警：状态不可用";
            }
            finally { _installationAlarmRefreshRunning = false; }
        }

        private static string AlarmStateText(InstallationAlarmSnapshot state)
        {
            if (state == null) return "未知";
            if (state.OutputsSuppressed)
            {
                if (state.RequestedLightCount > 0 || state.RequestedBuzzerOn)
                    return "旧事件输出已关闭 · 当前项目有报警输出需求" + (state.Latched ? " · 故障仍锁存" : string.Empty);
                return (state.OutputStatus == "CommandSent" && state.CommandSentRevision == state.Revision
                    ? "声光关闭命令已发送" : "声光关闭已保存 · 待输出") + (state.Latched ? " · 故障仍锁存" : string.Empty);
            }
            if (state.BuzzerMuted) return "蜂鸣器静音已保存 · 故障仍锁存";
            return state.Latched ? "故障锁存 · 请求声光报警" : "无安装级锁存";
        }

        private static string AlarmTime(long ticks)
        {
            return ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "历史记录未保存";
        }

        private static string DescribeInstallationAlarm(InstallationAlarmSnapshot state)
        {
            if (state == null) return "未读取到 Supervisor 报警状态。";
            var output = state.OutputStatus == "CommandSent" ? "命令已发送，设备协议/现场物理状态未独立确认" :
                state.OutputStatus == "RetryPending" ? "输出失败，后台等待重试" : "等待后台应用最新输出";
            return "状态：" + AlarmStateText(state) + "\r\n" +
                "原故障：" + (state.Code ?? "无") + "\r\n" + (state.Detail ?? string.Empty) + "\r\n\r\n" +
                "事件：" + (state.EventId ?? "无") + "\r\n版本：" + state.Revision + "\r\n" +
                "首次观测：" + AlarmTime(state.FirstObservedUtcTicks) + "\r\n" +
                "最后更新：" + AlarmTime(state.UpdatedUtcTicks) + "\r\n" +
                "最近输出操作：" + (state.LastOutputAction ?? "无") + " · " + AlarmTime(state.LastOutputActionUtcTicks) + "\r\n" +
                "操作员 SID：" + (state.LastOutputOperatorSid ?? "未记录") + "\r\n\r\n" +
                "输出：" + output + "\r\n" + (state.OutputError ?? string.Empty) + "\r\n\r\n" +
                "仅静音保留故障灯；关闭声光保留故障锁存和证据。新的 P0 故障会重新启声亮灯。";
        }

        private async Task ShowInstallationAlarmAsync()
        {
            using (var dialog = new Form { Text = "安装级报警", Size = new Size(760, 580),
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
            using (var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10), Text = "正在读取安装报警…" })
            using (var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8) })
            using (var refresh = new Button { Text = "刷新状态", AutoSize = true })
            using (var mute = new Button { Text = "仅静音蜂鸣器", AutoSize = true, Enabled = false })
            using (var suppress = new Button { Text = "关闭声光（保留故障）", AutoSize = true, Enabled = false })
            {
                InstallationAlarmSnapshot displayed = null;
                bool busy = false;
                async Task Apply(InstallationAlarmAction action)
                {
                    if (busy) return;
                    busy = true;
                    refresh.Enabled = mute.Enabled = suppress.Enabled = false;
                    try
                    {
                        var response = await Task.Run(() => InstallationAlarmClient.Send(action, expected: displayed));
                        displayed = response.State;
                        if (!dialog.IsDisposed) details.Text = DescribeInstallationAlarm(displayed);
                    }
                    catch (Exception error)
                    {
                        displayed = null;
                        if (!dialog.IsDisposed) details.Text = "操作未确认：" + error.GetBaseException().Message +
                            "\r\n请刷新状态；未确认时不能认为声光已关闭。";
                    }
                    finally
                    {
                        busy = false;
                        if (!dialog.IsDisposed)
                        {
                            refresh.Enabled = true;
                            mute.Enabled = displayed?.Latched == true && !displayed.BuzzerMuted && !displayed.OutputsSuppressed;
                            suppress.Enabled = displayed != null;
                        }
                    }
                }
                refresh.Click += async (_, __) => await Apply(InstallationAlarmAction.Query);
                mute.Click += async (_, __) => await Apply(InstallationAlarmAction.MuteBuzzer);
                suppress.Click += async (_, __) => await Apply(InstallationAlarmAction.SuppressOutputs);
                dialog.Shown += async (_, __) => await Apply(InstallationAlarmAction.Query);
                buttons.Controls.AddRange(new Control[] { refresh, mute, suppress });
                dialog.Controls.Add(details);
                dialog.Controls.Add(buttons);
                dialog.ShowDialog(this);
            }
            await RefreshInstallationAlarmMenuAsync();
        }
    }
}
