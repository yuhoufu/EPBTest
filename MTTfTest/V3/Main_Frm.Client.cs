using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class Main_Frm : Form
    {
        private readonly V3MonitorSession _session;
        // Existing generated Dispose calls this slot; it now owns only the client lifetime.
        private readonly IDisposable _watchdogUiAdapter;
        private readonly Timer _refreshTimer;
        private FrmEpbMainMonitor _monitor;
        private bool _closing;
        private bool _exitCommitted;
        private readonly string _displaySettingsPath;

        internal Main_Frm(V3EngineIdentity identity) : this(new V3EngineHostClient(identity)) { }
        internal Main_Frm(IEngineUiClient client, string displaySettingsPath = null)
        {
            _displaySettingsPath = displaySettingsPath;
            _session = new V3MonitorSession(client);
            _watchdogUiAdapter = _session;
            InitializeComponent();
            if (components == null) components = new System.ComponentModel.Container();
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Text = "MT EPB常温疲劳测试 V3.0.0.0";
            WindowState = FormWindowState.Maximized;
            关于ToolStripMenuItem1.Visible = true;
            关于ToolStripMenuItem1.Text = "运行身份";
            TsmPlayBack.Visible = true;
            关于ToolStripMenuItem1.Click += (_, __) => ShowDiagnostics();
            _refreshTimer = new Timer(components) { Interval = 500 };
            _refreshTimer.Tick += async (_, __) =>
            {
                // Schedule the next refresh after render completion; a slow DPI/layout
                // pass must not leave WM_TIMER permanently ready and starve input.
                _refreshTimer.Stop();
                try { await _session.RefreshAsync(); }
                finally { if (!IsDisposed && !_exitCommitted) _refreshTimer.Start(); }
            };
            _session.Changed += UpdateConnection;
        }

        private async void Main_Frm_Load(object sender, EventArgs e)
        {
            OpenMonitor();
            await _session.RefreshAsync();
            _refreshTimer.Start();
        }

        private void UpdateConnection()
        {
            if (IsDisposed) return;
            Text = "MT EPB常温疲劳测试 V3.0.0.0" +
                (_session.IsConnected ? " - [实时监视]" : " - [后台未连接]");
        }

        private void OpenMonitor()
        {
            if (_monitor != null && !_monitor.IsDisposed) { _monitor.Activate(); return; }
            _monitor = new FrmEpbMainMonitor(_session, _displaySettingsPath) { MdiParent = this, WindowState = FormWindowState.Maximized };
            _monitor.Show();
            menuStripMain.BringToFront();
        }

        private void TsmRealMinitor_Click(object sender, EventArgs e) => OpenMonitor();
        private void TsmDAQ_Click(object sender, EventArgs e) { }
        private void TsmPlayBack_Click(object sender, EventArgs e) { }
        private void TsmCharacterPlayBack_Click(object sender, EventArgs e) => OpenPlayback(new FrmPlayBack());
        private void TsmRawPlayBack_Click(object sender, EventArgs e) => OpenPlayback(new FrmRawPlayBack());
        private void OpenPlayback(Form form)
        {
            // Retain the old operation boundary: stop before replacing the monitoring workspace.
            if (!_session.CanClose)
            {
                form.Dispose();
                MessageBox.Show(this, "请先完成停止试验事务再打开数据回放。", "试验运行中");
                return;
            }
            form.MdiParent = this;
            form.WindowState = FormWindowState.Maximized;
            form.Show();
        }

        private void TsmSetting_Click(object sender, EventArgs e)
        {
            using (var setting = new FrmTestSetting(_session)) setting.ShowDialog(this);
        }

        private void ShowDiagnostics()
        {
            var engine = _session.Latest?.Engine;
            MessageBox.Show(this, "连接：" + (_session.IsConnected ? "正常" : _session.ConnectionError) +
                "\r\nSession=" + engine?.SessionId + "\r\nRun=" + engine?.RunId +
                "\r\nEpoch=" + engine?.RunEpoch + "\r\nEngine=" + engine?.EngineInstanceId +
                "\r\nIncident=" + engine?.RecoveryIncidentId + "\r\nOwner=" + engine?.RecoveryOwnerId +
                "\r\n" + _session.Latest?.StatusDetail, "运行身份／诊断");
        }

        private async void Main_Frm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_exitCommitted) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            try
            {
                await _session.RefreshAsync();
                if (!_session.CanClose)
                {
                    MessageBox.Show(this, "运行、恢复或状态不明时不能关闭界面。请先完成停止试验事务。", "关闭被阻止");
                    return;
                }
                UserInterfaceExitReceiptStore.Write(UserInterfaceExitReceiptStore.ForCurrentProcess(
                    _session.Latest.Engine, "OperatorClosedOriginalUi"));
                _exitCommitted = true;
                _refreshTimer.Stop();
                BeginInvoke(new Action(Close));
            }
            catch (Exception ex) { MessageBox.Show(this, "正常退出记录未完成：" + ex.Message, "关闭被阻止"); }
            finally { _closing = false; }
        }
    }
}
