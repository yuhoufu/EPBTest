using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    /// <summary>
    /// V3 display/operation client. It has no hardware ownership;
    /// telemetry is latest-only.
    /// </summary>
    internal sealed class V3EngineClientForm : Form
    {
        private readonly V3EngineHostClient _client;
        private readonly Label _state = new Label();
        private readonly Label _identity = new Label();
        private readonly TextBox _telemetry = new TextBox();
        private readonly Label _message = new Label();
        private readonly Button _start = new Button();
        private readonly Button _stop = new Button();
        private readonly Button _refresh = new Button();
        private readonly System.Windows.Forms.Timer _timer =
            new System.Windows.Forms.Timer { Interval = 500 };
        private EngineStateSnapshot _latest;
        private int _refreshing;
        private bool _typedExitWritten;

        internal V3EngineClientForm(V3EngineIdentity identity)
        {
            _client = new V3EngineHostClient(identity);
            Text = "MT EPB常温疲劳测试 V3.0.0.0 - EngineHost 客户端";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 600);
            Size = new Size(1100, 720);
            Font = new Font("Microsoft YaHei UI", 10F);

            _state.Dock = DockStyle.Top;
            _state.Height = 72;
            _state.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold);
            _state.TextAlign = ContentAlignment.MiddleCenter;
            _identity.Dock = DockStyle.Top;
            _identity.Height = 54;
            _identity.TextAlign = ContentAlignment.MiddleCenter;
            _telemetry.Dock = DockStyle.Fill;
            _telemetry.Multiline = true;
            _telemetry.ReadOnly = true;
            _telemetry.ScrollBars = ScrollBars.Vertical;
            _telemetry.Font = new Font("Consolas", 11F);
            _message.Dock = DockStyle.Bottom;
            _message.Height = 52;
            _message.TextAlign = ContentAlignment.MiddleLeft;
            _message.Padding = new Padding(12, 0, 12, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 58,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            _stop.Text = "停止试验";
            _stop.Width = 160;
            _stop.Height = 38;
            _stop.BackColor = Color.Firebrick;
            _stop.ForeColor = Color.White;
            _stop.Click += async (sender, args) => await StopAsync();
            _start.Text = "开始 / 继续试验";
            _start.Width = 180;
            _start.Height = 38;
            _start.BackColor = Color.SeaGreen;
            _start.ForeColor = Color.White;
            _start.Click += async (sender, args) => await StartAsync();
            _refresh.Text = "立即刷新";
            _refresh.Width = 140;
            _refresh.Height = 38;
            _refresh.Click += async (sender, args) => await RefreshAsync();
            buttons.Controls.Add(_stop);
            buttons.Controls.Add(_start);
            buttons.Controls.Add(_refresh);
            Controls.Add(_telemetry);
            Controls.Add(_message);
            Controls.Add(buttons);
            Controls.Add(_identity);
            Controls.Add(_state);

            Shown += async (sender, args) =>
            {
                await RefreshAsync();
                _timer.Start();
            };
            _timer.Tick += async (sender, args) => await RefreshAsync();
            FormClosing += OnFormClosing;
        }

        private async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            try
            {
                var result = await Task.Run(() =>
                    Tuple.Create(_client.ReadSnapshot(), _client.ReadTelemetry()));
                _latest = result.Item1;
                var frame = result.Item2;
                _state.Text = StateText(_latest.State);
                _state.ForeColor = _latest.State == SystemTerminalState.Running
                    ? Color.DarkGreen
                    : _latest.State == SystemTerminalState.RunningDegraded
                        ? Color.DarkOrange
                        : Color.Firebrick;
                _identity.Text = "Session " + _latest.SessionId +
                                 "  Run " + _latest.RunId +
                                 "  Epoch " + _latest.RunEpoch +
                                 "  Pulse " + _latest.PulseSequence;
                _telemetry.Text = FormatTelemetry(frame);
                _message.Text = string.IsNullOrEmpty(_latest.RecoveryIncidentId)
                    ? "EngineHost 已连接；界面仅负责显示和提交操作命令。"
                    : "恢复中：Incident=" + _latest.RecoveryIncidentId +
                      " Owner=" + _latest.RecoveryOwnerId;
                _stop.Enabled = _latest.State != SystemTerminalState.StoppedByOperator;
                _start.Enabled = string.IsNullOrEmpty(_latest.RecoveryIncidentId) &&
                                 (_latest.State == SystemTerminalState.SafeIdleAlarmed ||
                                  _latest.State == SystemTerminalState.StoppedByOperator);
            }
            catch (Exception ex)
            {
                _state.Text = "ENGINEHOST 连接中断";
                _state.ForeColor = Color.Firebrick;
                _message.Text = ex.GetBaseException().Message;
                _stop.Enabled = false;
                _start.Enabled = false;
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        }

        private async Task StopAsync()
        {
            if (_latest == null) return;
            _stop.Enabled = false;
            _message.Text = "正在通过 Supervisor 提交持久化停止事务……";
            try
            {
                var response = await Task.Run(() => _client.Stop(_latest));
                if (!response.Accepted)
                    throw new InvalidOperationException(
                        response.FailureCode + ":" + response.Detail);
                _message.Text = "停止事务完成：" + response.DesiredState +
                                " Incident=" + response.IncidentId;
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _message.Text = "停止失败：" + ex.GetBaseException().Message;
                _stop.Enabled = true;
            }
        }

        private async Task StartAsync()
        {
            if (_latest == null) return;
            _start.Enabled = false;
            _message.Text = "正在提交启动事务；系统将先断能证明、无源预检并完成资格圈……";
            try
            {
                var response = await Task.Run(() => _client.Start(_latest));
                if (!response.Accepted)
                    throw new InvalidOperationException(
                        response.FailureCode + ":" + response.Detail);
                _message.Text = "启动事务已持久化：" + response.DesiredState +
                                " Incident=" + response.IncidentId;
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                _message.Text = "启动失败：" + ex.GetBaseException().Message;
                _start.Enabled = true;
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs args)
        {
            if (_typedExitWritten) return;
            try
            {
                var snapshot = _client.ReadSnapshot();
                if (snapshot.State == SystemTerminalState.Running ||
                    snapshot.State == SystemTerminalState.RunningDegraded ||
                    !string.IsNullOrEmpty(snapshot.RecoveryIncidentId))
                {
                    args.Cancel = true;
                    MessageBox.Show(
                        "试验运行、隔离或恢复期间不能关闭界面。请先完成“停止试验”事务。",
                        "运行中禁止关闭",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                UserInterfaceExitReceiptStore.Write(
                    UserInterfaceExitReceiptStore.ForCurrentProcess(
                        snapshot, "OperatorClosedSafeUi"));
                _typedExitWritten = true;
                _timer.Stop();
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                MessageBox.Show(
                    "无法证明 EngineHost 处于安全终态，界面关闭已被阻止。\r\n" +
                    ex.GetBaseException().Message,
                    "安全状态不可证明",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static string StateText(SystemTerminalState state)
        {
            switch (state)
            {
                case SystemTerminalState.Running: return "运行中";
                case SystemTerminalState.RunningDegraded: return "降级运行";
                case SystemTerminalState.StoppedByOperator: return "操作员已停止";
                case SystemTerminalState.SafeIdleAlarmed: return "安全待机 / 报警";
                default: return "未知状态";
            }
        }

        private static string FormatTelemetry(EngineTelemetryFrame frame)
        {
            if (frame == null) return "等待遥测……";
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("Latest-only telemetry sequence: " + frame.Sequence);
            builder.AppendLine("Captured UTC: " +
                               new DateTime(frame.CapturedUtcTicks, DateTimeKind.Utc).ToString("O"));
            builder.AppendLine();
            var currents = frame.Currents ?? Array.Empty<double>();
            for (var index = 0; index < currents.Length; index++)
                builder.AppendLine("EPB " + (index + 1).ToString("00") +
                                   " current = " + currents[index].ToString("F4") + " A");
            builder.AppendLine();
            builder.AppendLine("Pressure = " + string.Join(", ",
                (frame.Pressures ?? Array.Empty<double>())
                .Select(value => value.ToString("F3"))) + " bar");
            builder.AppendLine("Formal cycles = " + string.Join(", ",
                frame.FormalCycleCounts ?? Array.Empty<int>()));
            return builder.ToString();
        }
    }
}
