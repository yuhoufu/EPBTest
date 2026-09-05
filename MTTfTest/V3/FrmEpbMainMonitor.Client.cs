using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MTTFTest.Watchdog.Protocol;
using ZedGraph;
using Label = System.Windows.Forms.Label;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
        private readonly V3MonitorSession _session;
        private readonly Label[] _states = new Label[12];
        private readonly Dictionary<string, LineItem> _curves = new Dictionary<string, LineItem>();
        private readonly Dictionary<string, double> _displayOffsets = new Dictionary<string, double>();
        private readonly ToolTip _details;
        private readonly OriginalMonitorPreferences _preferences;
        private string _curveInstance;
        private string _logLevel = "INFO";
        private bool _binding;
        private bool _selectionInitialized;
        private string _selectionIdentity;
        private double _windowSeconds = 30;
        internal Func<int, bool> ConfirmQualificationRetry { get; set; }

        internal FrmEpbMainMonitor(V3MonitorSession session, string displaySettingsPath = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _preferences = new OriginalMonitorPreferences(displaySettingsPath);
            InitializeComponent();
            var closeMonitor = new Button
            {
                Name = "BtnCloseMonitor", Text = "关闭监控", Location = new Point(8, 8),
                Size = new Size(Math.Max(80, uiPanel6.ClientSize.Width - 16), Math.Max(40, Font.Height * 2)),
                BackColor = Color.White, ForeColor = Color.FromArgb(40, 80, 120), FlatStyle = FlatStyle.Flat
            };
            closeMonitor.Click += (_, __) => Close();
            uiPanel6.Controls.Add(closeMonitor);
            closeMonitor.BringToFront();
            Shown += (_, __) => ResizeLedDisplays();
            Resize += (_, __) => ResizeLedDisplays();
            _details = new ToolTip(components) { AutoPopDelay = 15000 };
            ConfirmQualificationRetry = channel => MessageBox.Show(this,
                "EPB-" + channel + " 将执行一次主动资格复核。\r\n\r\n" +
                "系统会先停止全部通道、请求独立断能与泄压证明，再执行无源预检和两圈不计正式次数的资格复核。" +
                "失败后该通道保持永久隔离，不能再次自动复跑。\r\n\r\n仅在已排除或修复硬件问题后继续。",
                "确认资格复核", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
            Text = "实时监视";
            comboBoxEditCurrentRecord.Properties.Items.Clear();
            var panels = new[] { uiTableLayoutPanel10, uiTableLayoutPanel11, uiTableLayoutPanel12, uiTableLayoutPanel13 };
            for (var index = 0; index < 12; index++)
            {
                var channel = index + 1;
                _states[index] = new Label
                {
                    Name = "RuntimeStateEpb" + channel, Dock = DockStyle.Fill,
                    Margin = new Padding(2, 6, 2, 6), TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular), ForeColor = Color.White,
                    BackColor = Color.Gray, Text = "未就绪"
                };
                panels[index / 3].Controls.Add(_states[index], 3, index % 3);
                var selection = FindControl<DevExpress.XtraEditors.CheckEdit>("ChkEpb" + channel);
                selection.Enabled = false;
                var toggle = FindControl<DevExpress.UITemplates.Collection.Editors.ToggleButton>("SwitchEpb" + channel);
                toggle.Enabled = false;
                toggle.Click += async (_, __) =>
                {
                    if (_binding) return;
                    if (_session.CanRetryQualification(channel) && !ConfirmQualificationRetry(channel))
                    {
                        Render();
                        return;
                    }
                    await _session.SubmitChannelActionAsync(channel);
                    Render(); // Rebind authoritative state even when the command was rejected.
                };
                var draw = FindControl<DevExpress.XtraEditors.CheckEdit>("CheckEpbA" + channel);
                draw.Checked = true;
                draw.CheckedChanged += (_, __) => CurveSelectionChanged("A" + channel, draw.Checked);
                comboBoxEditCurrentRecord.Properties.Items.Add("EPB-" + channel);
            }
            comboBoxEditCurrentRecord.SelectedIndex = 0;
            TxtTestName.ReadOnly = true;
            TxtTestCycleTime.ReadOnly = true;
            TxtTargetCycles.ReadOnly = true;
            uiCheckBoxIsSameCycleForAllEpb.Enabled = false;
            BtnApply.Visible = false;
            BtnCancel.Visible = false;
            BtnTest.Visible = false;
            toggleSwitch1.Visible = false;
            BtnClearAlarms.Enabled = false;
            CbBuzzerEnabled.Enabled = false;
            BtnClearAlarms.Click += BtnClearAlarms_Click;
            CbBuzzerEnabled.CheckedChanged += CbBuzzerEnabled_CheckedChanged;
            CheckP1.CheckedChanged += (_, __) => CurveSelectionChanged("P1", CheckP1.Checked);
            CheckP2.CheckedChanged += (_, __) => CurveSelectionChanged("P2", CheckP2.Checked);
            CheckF.CheckedChanged += (_, __) => CurveSelectionChanged("F", CheckF.Checked);
            InitializeCurves();
            RtbInfo.ContextMenuStrip = new ContextMenuStrip(components);
            RtbInfo.ContextMenuStrip.Items.Add("读取更早日志（按当前类型）", null, async (_, __) =>
            {
                var page = _session.LogPage;
                if (page != null && !page.HasEarlier) return;
                var before = page?.NextBeforeSequence ?? (_session.Latest?.Logs.Where(l => l.Level == _logLevel)
                    .Select(l => l.Sequence).DefaultIfEmpty(0).Min() ?? 0);
                await _session.ReadLogPageAsync(_logLevel, before);
            });
            RtbInfo.ContextMenuStrip.Items.Add("返回实时日志", null, (_, __) => _session.FollowLiveLogs());
            _details.SetToolTip(RtbInfo, "右键可按序号读取更早日志或返回实时显示。");
            var originalLayout = new OriginalMonitorLayout(this, ResizeLedDisplays);
            Disposed += (_, __) => originalLayout.Dispose();
            _session.Changed += Render;
        }

        private T FindControl<T>(string name) where T : Control => Controls.Find(name, true).OfType<T>().Single();

        private void InitializeCurves()
        {
            var pane = zedGraphRealChart.GraphPane;
            pane.CurveList.Clear();
            while (pane.YAxisList.Count > 1) pane.YAxisList.RemoveAt(pane.YAxisList.Count - 1);
            while (pane.Y2AxisList.Count > 1) pane.Y2AxisList.RemoveAt(pane.Y2AxisList.Count - 1);
            pane.Title.IsVisible = false;
            pane.XAxis.Title.IsVisible = pane.YAxis.Title.IsVisible = pane.Y2Axis.Title.IsVisible = false;
            pane.Chart.Border.IsVisible = false;
            pane.Fill = new Fill(Color.White); pane.Chart.Fill = new Fill(Color.FromArgb(248, 248, 248));
            foreach (var axis in new Axis[] { pane.XAxis, pane.YAxis, pane.Y2Axis })
            {
                axis.Color = Color.Gray; axis.MajorTic.Color = Color.Gray; axis.MinorTic.Size = 0;
                axis.Title.FontSpec.Size = axis.Scale.FontSpec.Size = 12;
                axis.Scale.MagAuto = axis.Scale.FormatAuto = false;
            }
            pane.XAxis.MajorGrid.IsVisible = pane.YAxis.MajorGrid.IsVisible = true;
            pane.XAxis.MajorGrid.Color = Color.Gray; pane.YAxis.MajorGrid.Color = Color.FromArgb(80, 160, 255);
            pane.XAxis.MajorGrid.DashOn = pane.YAxis.MajorGrid.DashOn = float.MaxValue;
            pane.XAxis.MajorGrid.DashOff = pane.YAxis.MajorGrid.DashOff = 0;
            pane.Y2Axis.IsVisible = true;
            pane.Y2Axis.Color = pane.Y2Axis.Scale.FontSpec.FontColor = Color.Purple;
            pane.Y2Axis.MajorGrid.IsVisible = false;
            var pressure = new YAxis("Pressure (bar)") { Tag = "PRESSURE_AXIS", Color = Color.DarkBlue };
            pressure.Title.FontSpec.FontColor = pressure.Scale.FontSpec.FontColor = Color.DarkBlue;
            pressure.Title.FontSpec.Size = pressure.Scale.FontSpec.Size = 12;
            pressure.MajorGrid.IsVisible = pressure.MajorGrid.IsZeroLine = false;
            pressure.MajorTic.Color = Color.Gray; pressure.MinorTic.Size = 0;
            pressure.Scale.MagAuto = pressure.Scale.FormatAuto = false;
            pane.YAxisList.Add(pressure);
            var colors = new[] { Color.Blue, Color.Red, Color.Green, Color.Orange, Color.Purple,
                Color.Brown, Color.DarkCyan, Color.Magenta, Color.DarkOliveGreen, Color.Maroon, Color.Teal, Color.Goldenrod };
            for (var channel = 1; channel <= 12; channel++)
                _curves["A" + channel] = pane.AddCurve("DAQ_A" + channel + " (A)", new PointPairList(), colors[channel - 1], SymbolType.None);
            _curves["P1"] = pane.AddCurve("DAQ_P1 (bar)", new PointPairList(), Color.DarkBlue, SymbolType.None);
            _curves["P2"] = pane.AddCurve("DAQ_P2 (bar)", new PointPairList(), Color.DarkRed, SymbolType.None);
            _curves["F"] = pane.AddCurve("DAQ_F (N)", new PointPairList(), Color.DarkGreen, SymbolType.None);
            foreach (var curve in _curves.Values) { curve.Line.Width = 2; curve.Line.IsSmooth = false; }
            foreach (var key in new[] { "P1", "P2" }) _curves[key].YAxisIndex = 1;
            _curves["F"].IsY2Axis = true;
            pane.XAxis.Scale.Min = 0;
            pane.XAxis.Scale.Max = _windowSeconds;
            pane.XAxis.Scale.MinAuto = pane.XAxis.Scale.MaxAuto = false;
            zedGraphRealChart.IsAntiAlias = false;
            zedGraphRealChart.IsEnableHZoom = zedGraphRealChart.IsEnableVZoom = true;
            zedGraphRealChart.IsEnableWheelZoom = true;
            zedGraphRealChart.ContextMenuBuilder += (_, menu, point, state) =>
            {
                var window = new ToolStripMenuItem("显示窗口（仅界面）");
                window.DropDownItems.Add("跟随试验周期 × 2", null, (s, e) => SetDisplayWindow(0));
                foreach (var seconds in new[] { 15, 30, 60, 120, 300, 600, 1200 })
                {
                    var captured = seconds;
                    var item = window.DropDownItems.Add(seconds + " 秒", null, (s, e) => SetDisplayWindow(captured));
                    item.Enabled = seconds <= (_session.Latest?.CurveWindowSeconds ?? 60);
                }
                menu.Items.Add(window);
                menu.Items.Add("恢复实时窗口", null, (s, e) => ResetCurveZoom());
            };
        }

        private void SetDisplayWindow(double seconds)
        {
            _preferences.WindowSeconds = seconds; _preferences.Save(); ResetCurveZoom();
        }
        private void ResetCurveZoom()
        {
            zedGraphRealChart.ZoomOutAll(zedGraphRealChart.GraphPane);
            RenderCurves();
        }
        private void CurveSelectionChanged(string key, bool visible)
        {
            if (_binding) return;
            _preferences.Curves[key] = visible; _preferences.Save(); RenderCurves();
        }

        private void Render()
        {
            if (IsDisposed) return;
            _binding = true;
            try
            {
                var snapshot = _session.Latest;
                BtnStartTest.Text = _session.BatchActionText;
                BtnStartTest.Enabled = _session.CanStart || _session.CanPause || _session.CanResume;
                BtnStop.Enabled = _session.CanStop;
                for (var channel = 1; channel <= 12; channel++)
                {
                    var toggle = FindControl<DevExpress.UITemplates.Collection.Editors.ToggleButton>("SwitchEpb" + channel);
                    toggle.Enabled = _session.CanOperateChannel(channel);
                    _details.SetToolTip(toggle, _session.CanRetryQualification(channel)
                        ? "资格复核：仅允许一次。先停止全部通道并形成独立断能／泄压证明；失败后永久隔离。"
                        : toggle.Enabled ? "暂停／继续本通道；不解除硬故障隔离，暂停不等于维护断能。" :
                        "当前不可操作：等待有效运行状态，或资格预算已用尽／共享故障域需维护处理。" );
                }
                BtnClearAlarms.Enabled = CbBuzzerEnabled.Enabled = _session.CanOperateAlarmPanel;
                CbBuzzerEnabled.Checked = snapshot?.AlarmPanel.Available == true && snapshot.AlarmPanel.BuzzerEnabled;
                _details.SetToolTip(BtnClearAlarms, (snapshot?.AlarmPanel.Detail ?? "报警板未就绪") +
                    "\r\n仅确认当前已显示的报警；新报警和硬故障隔离不受影响。");
                _details.SetToolTip(CbBuzzerEnabled, snapshot?.AlarmPanel.Detail ?? "报警板未就绪");
                if (snapshot != null)
                {
                    TxtTestName.Text = snapshot.TestName;
                    TxtTestCycleTime.Text = snapshot.PeriodSeconds.ToString(CultureInfo.InvariantCulture);
                    TxtTargetCycles.Text = snapshot.TargetCycles.ToString(CultureInfo.InvariantCulture);
                    uiCheckBoxIsSameCycleForAllEpb.Checked = snapshot.SharedTargetCycles;
                    var selectionIdentity = snapshot.Engine.SessionId + "/" + snapshot.Engine.RunId + "/" +
                        string.Join(",", snapshot.Channels.Where(c => c.Selected).Select(c => c.Channel));
                    if (_selectionIdentity != selectionIdentity &&
                        (snapshot.TestConfiguration != null || snapshot.Channels.Any(c => c.CountsValid)))
                    {
                        foreach (var channel in snapshot.Channels)
                            FindControl<DevExpress.XtraEditors.CheckEdit>("CheckEpbA" + channel.Channel).Checked =
                                !_selectionInitialized && _preferences.Curves.TryGetValue("A" + channel.Channel, out var visible) ? visible : channel.Selected;
                        if (_preferences.Curves.TryGetValue("P1", out var p1)) CheckP1.Checked = p1;
                        if (_preferences.Curves.TryGetValue("P2", out var p2)) CheckP2.Checked = p2;
                        if (_preferences.Curves.TryGetValue("F", out var force)) CheckF.Checked = force;
                        var first = snapshot.Channels.Where(c => c.Selected).OrderBy(c => c.Channel).FirstOrDefault();
                        comboBoxEditCurrentRecord.SelectedIndex = (first?.Channel ?? 1) - 1;
                        comboBoxEditCurrentRecord.EditValue = "EPB-" + (first?.Channel ?? 1);
                        _selectionInitialized = true;
                        _selectionIdentity = selectionIdentity;
                    }
                    foreach (var channel in snapshot.Channels)
                    {
                        var index = channel.Channel - 1;
                        FindControl<DevExpress.XtraEditors.CheckEdit>("ChkEpb" + channel.Channel).Checked = channel.Selected;
                        FindControl<Sunny.UI.UILabel>("LabEpb" + channel.Channel).Text = channel.CountsValid ? channel.MechanicalCycles.ToString() : "—";
                        FindControl<DevExpress.UITemplates.Collection.Editors.ToggleButton>("SwitchEpb" + channel.Channel).Checked = _session.IsConnected && channel.Running;
                        _states[index].Text = _session.IsConnected ? channel.State : "已失联";
                        _states[index].BackColor = !_session.IsConnected ? Color.Gray : channel.Isolated ? Color.Firebrick :
                            channel.Running ? Color.SeaGreen : Color.SteelBlue;
                        _details.SetToolTip(_states[index], channel.Reason + "\r\n正式计数：" +
                            (channel.CountsValid ? channel.FormalCycles.ToString() : "未就绪"));
                        FindControl<DevExpress.XtraEditors.TextEdit>("textEditCurrent" + channel.Channel).Text = Measure(channel.Current, "A", 3);
                    }
                    var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
                    foreach (var power in snapshot.PowerSupplies)
                    {
                        var fresh = _session.IsConnected && power.Valid && power.CapturedUtcTicks > 0 &&
                            DateTime.UtcNow.Ticks - power.CapturedUtcTicks >= 0 &&
                            DateTime.UtcNow.Ticks - power.CapturedUtcTicks < TimeSpan.FromSeconds(5).Ticks;
                        boxes[power.Group - 1].Text = "电源" + power.Group + (fresh ?
                            (power.Connected ? power.OutputEnabled ? " ON " : " OFF " : " 已断开 ") +
                            power.Mode + " " + power.Voltage.ToString("F1") + "V/" + power.Current.ToString("F1") + "A" : " 未就绪／已失联");
                    }
                    textEditP1.Text = Measure(snapshot.Pressures[0], "bar", 1);
                    textEditP2.Text = Measure(snapshot.Pressures[1], "bar", 1);
                    textEditF.Text = Measure(snapshot.Force, "N", 0);
                    UpdateCounter();
                    RenderCurves();
                }
                else
                {
                    foreach (var state in _states) state.Text = "未就绪";
                    for (var channel = 1; channel <= 12; channel++)
                    {
                        FindControl<Sunny.UI.UILabel>("LabEpb" + channel).Text = "—";
                        FindControl<DevExpress.XtraEditors.TextEdit>("textEditCurrent" + channel).Text = "—";
                    }
                    textEditP1.Text = textEditP2.Text = textEditF.Text = "—";
                    LedRunTime.Text = LedRunCycles.Text = LedLastCycles.Text = "—";
                }
                RenderLogs();
            }
            finally { _binding = false; }
        }

        private string Measure(UiMeasurement value, string unit, int decimals) =>
            _session.IsConnected && value?.IsUsable(DateTime.UtcNow.Ticks) == true ?
                value.Value.ToString("F" + decimals, CultureInfo.InvariantCulture) + " " + unit : "—";

        private void UpdateCounter()
        {
            var channel = _session.Latest?.Channels.FirstOrDefault(c => c.Channel == comboBoxEditCurrentRecord.SelectedIndex + 1);
            if (channel?.CountsValid != true) { LedRunTime.Text = LedRunCycles.Text = LedLastCycles.Text = "—"; return; }
            var time = TimeSpan.FromTicks(channel.RunTimeTicks);
            LedRunTime.Text = string.Format("{0:00}D {1:00}H {2:00}M {3:00}S", time.Days, time.Hours, time.Minutes, time.Seconds);
            uiLabel57.Text = "机械完成次数";
            LedRunCycles.Text = channel.MechanicalCycles.ToString();
            LedLastCycles.Text = channel.RemainingCycles.ToString();
            var total = channel.MechanicalCycles + channel.RemainingCycles;
            ProcBar.Value = total > 0 ? (int)Math.Min(100, channel.MechanicalCycles * 100.0 / total) : 0;
            ResizeLedDisplays();
        }

        private void ResizeLedDisplays()
        {
            var closeMonitor = uiPanel6?.Controls["BtnCloseMonitor"];
            if (closeMonitor != null)
                closeMonitor.SetBounds(6, 6, Math.Max(1, uiPanel6.ClientSize.Width - 12),
                    Math.Max(24, closeMonitor.Font.Height + 12));
            if (LedRunTime?.Parent == null) return;
            UIHelpers.LedAutoSizer.ResizeLedToParentWidth(LedRunTime, LedRunTime.Parent);
            LedRunTime.Height = 7 * LedRunTime.IntervalOn + 6 * LedRunTime.IntervalIn + 4;
            foreach (var led in new[] { LedRunCycles, LedLastCycles })
            {
                led.IntervalOn = LedRunTime.IntervalOn;
                led.IntervalIn = LedRunTime.IntervalIn;
                var columns = led.CharCount * 6 - 1;
                led.Width = led.IntervalIn * (1 + columns) + led.IntervalOn * (2 + columns) + 4;
                led.Height = LedRunTime.Height;
                led.Left = Math.Max(0, (led.Parent.ClientSize.Width - led.Width) / 2);
            }
        }

        private void RenderLogs()
        {
            var snapshot = _session.Latest;
            var heading = !_session.IsConnected ? "后台未连接：" + _session.ConnectionError : snapshot.StatusDetail;
            if (snapshot?.Kernel.Available == true) heading += "\r\nSupervisor：" + snapshot.Kernel.Reason;
            var page = _session.LogPage;
            var lines = (page?.Entries ?? snapshot?.Logs ?? Array.Empty<EngineUiLogEntry>()).Where(entry => entry.Level == _logLevel)
                .Select(entry => "[" + new DateTime(entry.CapturedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    "] " + entry.Message);
            RtbInfo.Text = heading + "\r\n" + _session.OperationMessage + "\r\n" +
                _preferences.Error + "\r\n" +
                _session.LogError + "\r\n" +
                (zedGraphRealChart.GraphPane.ZoomStack.Count > 0 ? "曲线处于缩放查看状态，右键恢复实时窗口。\r\n" : string.Empty) +
                (page != null ? "日志分页：" + page.Entries.FirstOrDefault()?.Sequence + "—" + page.Entries.LastOrDefault()?.Sequence +
                    (page.HasEarlier ? "；右键可继续向前读取。\r\n" : "；已到当前缓存起点。\r\n") +
                    (page.RetentionTruncated || page.DroppedEntries > 0 ? "日志缓存曾溢出／跳过，完整记录请查阅 EngineHost 日志；显示丢弃=" + page.DroppedEntries + "。\r\n" : "") :
                    snapshot?.LogsTruncated == true ? "仅显示最近日志；右键分页读取。\r\n" : string.Empty) + string.Join("\r\n", lines);
        }

        private void RenderCurves()
        {
            if (_session.Latest == null) return;
            var pane = zedGraphRealChart.GraphPane;
            if (_curveInstance != _session.Latest.Engine.EngineInstanceId)
            {
                _curveInstance = _session.Latest.Engine.EngineInstanceId;
                _displayOffsets.Clear();
                pane.ZoomStack.Clear();
            }
            // A user zoom freezes only the displayed frame. Acquisition/saving continue.
            if (pane.ZoomStack.Count > 0) return;
            _windowSeconds = Math.Min(_session.Latest.CurveWindowSeconds, _preferences.WindowSeconds > 0 ?
                _preferences.WindowSeconds : Math.Max(1, _session.Latest.PeriodSeconds * 2));
            pane.XAxis.Scale.Min = 0; pane.XAxis.Scale.Max = _windowSeconds;
            pane.XAxis.Scale.MinAuto = pane.XAxis.Scale.MaxAuto = false;
            foreach (var item in _curves)
            {
                item.Value.Clear();
                item.Value.IsVisible = item.Key.StartsWith("A", StringComparison.Ordinal) ?
                    FindControl<DevExpress.XtraEditors.CheckEdit>("CheckEpbA" + item.Key.Substring(1)).Checked :
                    item.Key == "P1" ? CheckP1.Checked : item.Key == "P2" ? CheckP2.Checked : CheckF.Checked;
            }
            var end = _session.Latest.CapturedUtcTicks;
            var begin = end - (long)(_windowSeconds * TimeSpan.TicksPerSecond);
            foreach (var curve in _session.Latest.Curves)
            {
                if (!_curves.TryGetValue(curve.Key, out var line)) continue;
                var draw = curve.Key.StartsWith("A", StringComparison.Ordinal) ?
                    FindControl<DevExpress.XtraEditors.CheckEdit>("CheckEpbA" + curve.Key.Substring(1)).Checked :
                    curve.Key == "P1" ? CheckP1.Checked : curve.Key == "P2" ? CheckP2.Checked : CheckF.Checked;
                line.IsVisible = draw;
                _displayOffsets.TryGetValue(curve.Key, out var offset);
                for (var index = 0; index < curve.Values.Length; index++)
                    if (curve.UtcTicks[index] >= begin && curve.UtcTicks[index] <= end)
                    {
                        if (curve.BreakBefore.Length > 0 && curve.BreakBefore[index] || index > 0 &&
                            curve.UtcTicks[index] - curve.UtcTicks[index - 1] > TimeSpan.TicksPerSecond * 3)
                            line.AddPoint(PointPair.Missing, PointPair.Missing);
                        line.AddPoint((curve.UtcTicks[index] - begin) / (double)TimeSpan.TicksPerSecond, curve.Values[index] - offset);
                    }
            }
            zedGraphRealChart.AxisChange();
            zedGraphRealChart.Invalidate();
        }

        private async void BtnStartTestGuarded_Click(object sender, EventArgs e) => await _session.SubmitBatchActionAsync();
        private async void BtnStop_Click(object sender, EventArgs e) => await _session.SubmitAsync(OperatorCommandKind.Stop);
        private async void BtnClearAlarms_Click(object sender, EventArgs e) =>
            await _session.SubmitAlarmPanelAsync(OperatorCommandKind.AcknowledgeAlarms, _session.Latest?.AlarmPanel.BuzzerEnabled == true);
        private async void CbBuzzerEnabled_CheckedChanged(object sender, EventArgs e)
        {
            if (!_binding) await _session.SubmitAlarmPanelAsync(OperatorCommandKind.SetBuzzerEnabled, CbBuzzerEnabled.Checked);
        }
        private void BtnRunLog_Click(object sender, EventArgs e) { _logLevel = "INFO"; _session.FollowLiveLogs(); }
        private void BtnWarnLog_Click(object sender, EventArgs e) { _logLevel = "WARN"; _session.FollowLiveLogs(); }
        private void BtnErrorLog_Click(object sender, EventArgs e) { _logLevel = "ERROR"; _session.FollowLiveLogs(); }
        private void comboBoxEditCurrentRecord_SelectedIndexChanged(object sender, EventArgs e) { if (!_binding) UpdateCounter(); }
        private void FrmEpbMainMonitor_Load(object sender, EventArgs e) => Render();
        private void FrmEpbMainMonitor_FormClosed(object sender, FormClosedEventArgs e) => _session.Changed -= Render;
        private void FrmEpbMainMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_session.CanClose) return;
            e.Cancel = true;
            MessageBox.Show(this, "请先完成停止试验事务；连接中断时不能推定试验已停止。", "关闭被阻止");
        }
        private void ZeroButton_Click(object sender, EventArgs e)
        {
            foreach (var curve in _session.Latest?.Curves ?? Array.Empty<EngineUiCurve>())
                if (curve.Values.Length > 0 && _curves.TryGetValue(curve.Key, out var line) && line.IsVisible)
                    _displayOffsets[curve.Key] = curve.Values.Last();
            RenderCurves();
        }
        private void ClearZeroButton_Click(object sender, EventArgs e) { _displayOffsets.Clear(); RenderCurves(); }
        private void uiTableLayoutPanel15_Paint(object sender, PaintEventArgs e) { }
        private void BtnTest_Click(object sender, EventArgs e) { }
        private void toggleSwitch1_Toggled(object sender, EventArgs e) { }
    }
}
