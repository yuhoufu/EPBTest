using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Config;
using Controller;
using Sunny.UI;

namespace MtEmbTest
{
    public partial class FrmTestSetting
    {
        private readonly Dictionary<string, BindingList<PressureCalibrationRow>> _pressureCalibrationRows =
            new Dictionary<string, BindingList<PressureCalibrationRow>>(StringComparer.OrdinalIgnoreCase);

        private TabPage tabPagePressureCalibration;
        private UIComboBox CmbPressureCalibrationCylinder;
        private UITextBox TxtPressureCalibrationCommand;
        private UITextBox TxtPressureCalibrationMeasured;
        private UILabel LblPressureCalibrationVoltage;
        private UILabel LblPressureCalibrationLive;
        private UILabel LblPressureCalibrationSample;
        private UILabel LblPressureCalibrationFormula;
        private UILabel LblPressureCalibrationStatus;
        private UIDataGridView DgvPressureCalibration;
        private UIButton BtnPressureCalibrationStart;
        private UIButton BtnPressureCalibrationOutput;
        private UIButton BtnPressureCalibrationStop;
        private UIButton BtnPressureCalibrationUseLive;
        private UIButton BtnPressureCalibrationRecord;
        private UIButton BtnPressureCalibrationDelete;
        private UIButton BtnPressureCalibrationClear;
        private UIButton BtnPressureCalibrationSave;
        private Timer _pressureCalibrationTimer;
        private PressureCalibrationCoordinator _pressureCalibrationCoordinator;
        private bool _updatingMeasuredPressure;
        private bool _measuredPressureEdited;
        private double _latestPressureBar = double.NaN;

        private void InitializePressureCalibrationPage()
        {
            tabPagePressureCalibration = new TabPage
            {
                Name = "tabPagePressureCalibration",
                Text = "气缸压力输出校正",
                UseVisualStyleBackColor = true
            };

            var root = new UITableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(243, 249, 255),
                ColumnCount = 3,
                RowCount = 3,
                Font = new Font("Arial", 10.5782F)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 37F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));

            var group = new UIGroupBox
            {
                Dock = DockStyle.Fill,
                Text = "气缸压力输出校正",
                TextAlignment = ContentAlignment.MiddleLeft,
                Font = new Font("Arial", 10.5782F),
                Padding = new Padding(0, 32, 0, 0),
                Margin = new Padding(4, 5, 4, 5)
            };
            root.Controls.Add(group, 1, 1);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(28, 12, 28, 8)
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
            group.Controls.Add(content);

            var commandBar = NewCalibrationFlowPanel();
            CmbPressureCalibrationCylinder = NewCalibrationComboBox(170);
            CmbPressureCalibrationCylinder.Items.AddRange(new object[] { "Cylinder1", "Cylinder2" });
            CmbPressureCalibrationCylinder.SelectedIndexChanged += PressureCalibrationCylinderChanged;
            TxtPressureCalibrationCommand = NewCalibrationTextBox("70", 118);
            TxtPressureCalibrationCommand.Maximum = PressureCalibrationCoordinator.CalibrationPressureLimitBar;
            TxtPressureCalibrationCommand.Minimum = 0;
            BtnPressureCalibrationStart = NewCalibrationButton("开始校正", 112);
            BtnPressureCalibrationOutput = NewCalibrationButton("输出压力", 112);
            BtnPressureCalibrationStop = NewCalibrationButton("停止输出", 112, true);
            BtnPressureCalibrationStart.Click += PressureCalibrationStartClick;
            BtnPressureCalibrationOutput.Click += PressureCalibrationOutputClick;
            BtnPressureCalibrationStop.Click += PressureCalibrationStopClick;
            commandBar.Controls.Add(NewCalibrationLabel("气缸"));
            commandBar.Controls.Add(CmbPressureCalibrationCylinder);
            commandBar.Controls.Add(NewCalibrationLabel("命令压力(bar)"));
            commandBar.Controls.Add(TxtPressureCalibrationCommand);
            commandBar.Controls.Add(BtnPressureCalibrationStart);
            commandBar.Controls.Add(BtnPressureCalibrationOutput);
            commandBar.Controls.Add(BtnPressureCalibrationStop);
            content.Controls.Add(commandBar, 0, 0);

            var liveBar = NewCalibrationFlowPanel();
            LblPressureCalibrationVoltage = NewCalibrationValueLabel("AO：-- V", 150);
            LblPressureCalibrationLive = NewCalibrationValueLabel("实时压力：-- bar", 190);
            LblPressureCalibrationSample = NewCalibrationValueLabel("采样：未启动", 180);
            TxtPressureCalibrationMeasured = NewCalibrationTextBox(string.Empty, 150);
            TxtPressureCalibrationMeasured.Watermark = "自动读取，可修改";
            TxtPressureCalibrationMeasured.TextChanged += PressureCalibrationMeasuredTextChanged;
            BtnPressureCalibrationUseLive = NewCalibrationButton("使用实时值", 112);
            BtnPressureCalibrationUseLive.Click += PressureCalibrationUseLiveClick;
            liveBar.Controls.Add(LblPressureCalibrationVoltage);
            liveBar.Controls.Add(LblPressureCalibrationLive);
            liveBar.Controls.Add(LblPressureCalibrationSample);
            liveBar.Controls.Add(NewCalibrationLabel("实测压力(bar)"));
            liveBar.Controls.Add(TxtPressureCalibrationMeasured);
            liveBar.Controls.Add(BtnPressureCalibrationUseLive);
            content.Controls.Add(liveBar, 0, 1);

            DgvPressureCalibration = NewPressureCalibrationGrid();
            content.Controls.Add(DgvPressureCalibration, 0, 2);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.Transparent
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 550F));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 25F));
            var actionBar = NewCalibrationFlowPanel();
            actionBar.FlowDirection = FlowDirection.RightToLeft;
            actionBar.AutoScroll = false;
            actionBar.AutoSize = false;
            actionBar.Width = 550;
            BtnPressureCalibrationSave = NewCalibrationButton("保存校正", 118);
            BtnPressureCalibrationClear = NewCalibrationButton("清空", 90);
            BtnPressureCalibrationDelete = NewCalibrationButton("删除选中点", 128);
            BtnPressureCalibrationRecord = NewCalibrationButton("记录当前点", 128);
            BtnPressureCalibrationRecord.Click += PressureCalibrationRecordClick;
            BtnPressureCalibrationDelete.Click += PressureCalibrationDeleteClick;
            BtnPressureCalibrationClear.Click += PressureCalibrationClearClick;
            BtnPressureCalibrationSave.Click += PressureCalibrationSaveClick;
            actionBar.Controls.Add(BtnPressureCalibrationSave);
            actionBar.Controls.Add(BtnPressureCalibrationClear);
            actionBar.Controls.Add(BtnPressureCalibrationDelete);
            actionBar.Controls.Add(BtnPressureCalibrationRecord);
            footer.Controls.Add(actionBar, 1, 0);
            LblPressureCalibrationFormula = NewCalibrationValueLabel(string.Empty, 650);
            LblPressureCalibrationFormula.Dock = DockStyle.Fill;
            LblPressureCalibrationFormula.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(LblPressureCalibrationFormula, 0, 0);
            LblPressureCalibrationStatus = NewCalibrationValueLabel(
                "请先开始校正；输出前请确认管路安全。",
                1000);
            LblPressureCalibrationStatus.ForeColor = Color.FromArgb(48, 48, 48);
            LblPressureCalibrationStatus.Dock = DockStyle.Fill;
            LblPressureCalibrationStatus.TextAlign = ContentAlignment.MiddleLeft;
            footer.SetColumnSpan(LblPressureCalibrationStatus, 2);
            footer.Controls.Add(LblPressureCalibrationStatus, 0, 1);
            content.Controls.Add(footer, 0, 3);

            tabPagePressureCalibration.Controls.Add(root);
            TabSetting.TabPages.Insert(Math.Min(2, TabSetting.TabPages.Count), tabPagePressureCalibration);
            TabSetting.SelectedIndexChanged += PressureCalibrationTabChanged;
            FormClosing += FrmTestSettingPressureCalibrationFormClosing;

            _pressureCalibrationTimer = new Timer { Interval = 100 };
            _pressureCalibrationTimer.Tick += PressureCalibrationTimerTick;
            SetPressureCalibrationControls(false, false);
        }

        private void LoadPressureCalibrationConfiguration()
        {
            try
            {
                _pressureCalibrationRows.Clear();
                CmbPressureCalibrationCylinder.Items.Clear();
                var invalidPoints = 0;
                var auditPoints = LoadPressureCalibrationAuditPoints(
                    Path.Combine(Application.StartupPath, "Config", "AOConfig.xml"));
                foreach (var device in _cfg.AO.Devices.Values.OrderBy(x => x.Name))
                {
                    var rows = new BindingList<PressureCalibrationRow>();
                    var sequence = 1;
                    foreach (var point in device.VoltageToPressure.OrderBy(x => x.Pressure))
                    {
                        if (!CalibrationMath.IsFinite(point.Voltage) ||
                            !CalibrationMath.IsFinite(point.Pressure) ||
                            point.Voltage < _cfg.AO.MinVoltage || point.Voltage > _cfg.AO.MaxVoltage)
                        {
                            invalidPoints++;
                            continue;
                        }

                        rows.Add(new PressureCalibrationRow
                        {
                            Sequence = sequence++,
                            CommandPressure = auditPoints.TryGetValue(
                                (device.Name, point.Voltage, point.Pressure),
                                out var commandPressure)
                                ? commandPressure
                                : point.Pressure,
                            MeasuredPressure = point.Pressure,
                            Voltage = point.Voltage
                        });
                    }

                    _pressureCalibrationRows[device.Name] = rows;
                    CmbPressureCalibrationCylinder.Items.Add(device.Name);
                }

                var limit = Math.Min(
                    PressureCalibrationCoordinator.CalibrationPressureLimitBar,
                    _cfg.AO.MaxPressure > 0
                        ? _cfg.AO.MaxPressure
                        : PressureCalibrationCoordinator.CalibrationPressureLimitBar);
                TxtPressureCalibrationCommand.Maximum = limit;
                if (CmbPressureCalibrationCylinder.Items.Count > 0)
                    CmbPressureCalibrationCylinder.SelectedIndex = 0;
                BindPressureCalibrationGrid();
                SetPressureCalibrationStatus(
                    invalidPoints > 0
                        ? $"已忽略 {invalidPoints} 个超出 AO 电压范围的历史点；未建立有效表前使用线性公式。"
                        : "校正配置已加载。",
                    invalidPoints > 0);
            }
            catch (Exception ex)
            {
                SetPressureCalibrationStatus("校正配置加载失败：" + ex.Message, true);
            }
        }

        private static Dictionary<(string Device, double Voltage, double Pressure), double>
            LoadPressureCalibrationAuditPoints(string path)
        {
            var result = new Dictionary<(string Device, double Voltage, double Pressure), double>();
            if (!File.Exists(path)) return result;

            try
            {
                var document = XDocument.Load(path);
                foreach (var device in document.Root?.Element("Devices")?.Elements("Device") ??
                                       Enumerable.Empty<XElement>())
                {
                    var deviceName = device.Element("Name")?.Value?.Trim();
                    if (string.IsNullOrEmpty(deviceName)) continue;
                    foreach (var point in device.Element("VoltageToPressureTable")?.Elements("Point") ??
                                          Enumerable.Empty<XElement>())
                    {
                        if (!TryReadInvariant(point.Element("Voltage")?.Value, out var voltage) ||
                            !TryReadInvariant(point.Element("Pressure")?.Value, out var pressure) ||
                            !TryReadInvariant(point.Element("CommandPressure")?.Value, out var commandPressure))
                            continue;
                        result[(deviceName, voltage, pressure)] = commandPressure;
                    }
                }
            }
            catch
            {
                // 主 AO 配置已由 ConfigLoader 验证；审计字段读取失败时仅回退为实测压力。
            }

            return result;
        }

        private static bool TryReadInvariant(string text, out double value) =>
            double.TryParse(
                text,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value) && CalibrationMath.IsFinite(value);

        private void PressureCalibrationStartClick(object sender, EventArgs e)
        {
            if (_pressureCalibrationCoordinator != null) return;
            if (MessageBox.Show(
                    this,
                    "开始校正将初始化 DAQ、压力 DO 与 AO。请确认试验已停止、管路连接可靠且人员远离运动部件。",
                    "开始气缸压力校正",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try
            {
                var hardware = new PressureCalibrationHardware(_cfg, logger, Application.StartupPath);
                _pressureCalibrationCoordinator = new PressureCalibrationCoordinator(
                    hardware,
                    _cfg.AO,
                    _cfg.Test,
                    logger);
                _pressureCalibrationCoordinator.SafetyTripped += PressureCalibrationSafetyTripped;
                _pressureCalibrationTimer.Start();
                SetPressureCalibrationControls(true, false);
                SetPressureCalibrationStatus("校正硬件已启动，等待压力采样变为有效后即可输出。", false);
            }
            catch (Exception ex)
            {
                StopAndDisposePressureCalibrationSession();
                SetPressureCalibrationStatus("校正硬件启动失败：" + ex.Message, true);
                MessageBox.Show(this, ex.Message, "启动校正失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PressureCalibrationOutputClick(object sender, EventArgs e)
        {
            if (_pressureCalibrationCoordinator == null)
            {
                ShowPressureCalibrationWarning("请先点击“开始校正”。");
                return;
            }

            if (!TryReadCalibrationNumber(TxtPressureCalibrationCommand.Text, out var command))
            {
                ShowPressureCalibrationWarning("命令压力必须是有效数字。");
                return;
            }

            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            var result = _pressureCalibrationCoordinator.Output(device, command);
            if (!result.Success)
            {
                SetPressureCalibrationControls(true, false);
                ShowPressureCalibrationWarning(result.Error);
                return;
            }

            _measuredPressureEdited = false;
            LblPressureCalibrationVoltage.Text = $"AO：{result.Voltage:F4} V";
            SetPressureCalibrationControls(true, true);
            SetPressureCalibrationStatus(
                $"{result.DeviceName} 正在输出 {result.CommandPressureBar:F2} bar；请观察压力稳定后记录。",
                false);
        }

        private void PressureCalibrationStopClick(object sender, EventArgs e)
        {
            StopPressureCalibrationOutput(true);
        }

        private void PressureCalibrationRecordClick(object sender, EventArgs e)
        {
            if (_pressureCalibrationCoordinator == null || !_pressureCalibrationCoordinator.IsOutputActive)
            {
                ShowPressureCalibrationWarning("请先输出压力，待实时压力有效后再记录校正点。");
                return;
            }

            var safety = _pressureCalibrationCoordinator.CheckSafety();
            if (!safety.Safe)
            {
                SetPressureCalibrationControls(true, false);
                ShowPressureCalibrationWarning(safety.Error);
                return;
            }

            if (!TryReadCalibrationNumber(TxtPressureCalibrationMeasured.Text, out var measured))
            {
                ShowPressureCalibrationWarning("实测压力必须是有效数字。");
                return;
            }

            var device = _pressureCalibrationCoordinator.ActiveDeviceName;
            if (!_pressureCalibrationRows.TryGetValue(device, out var rows))
            {
                ShowPressureCalibrationWarning("当前气缸没有可编辑的校正表。");
                return;
            }

            rows.Add(new PressureCalibrationRow
            {
                CommandPressure = _pressureCalibrationCoordinator.ActiveCommandPressureBar,
                MeasuredPressure = measured,
                Voltage = _pressureCalibrationCoordinator.ActiveVoltage
            });
            SortPressureCalibrationRows(device);
            SetPressureCalibrationStatus(
                $"已记录：命令 {_pressureCalibrationCoordinator.ActiveCommandPressureBar:F2} bar，" +
                $"实测 {measured:F2} bar，AO {_pressureCalibrationCoordinator.ActiveVoltage:F4} V；尚未保存。",
                false);
        }

        private void PressureCalibrationDeleteClick(object sender, EventArgs e)
        {
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows) ||
                !(DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow row))
                return;
            rows.Remove(row);
            RenumberPressureCalibrationRows(rows);
        }

        private void PressureCalibrationClearClick(object sender, EventArgs e)
        {
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows)) return;
            if (MessageBox.Show(
                    this,
                    $"确定清空 {device} 当前页面中的全部校正点吗？清空后仍需至少记录两个点才能保存。",
                    "确认清空",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) == DialogResult.Yes)
                rows.Clear();
        }

        private void PressureCalibrationSaveClick(object sender, EventArgs e)
        {
            StopAndDisposePressureCalibrationSession();
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows))
            {
                ShowPressureCalibrationWarning("请先选择需要保存的气缸。");
                return;
            }

            var ordered = rows.OrderBy(x => x.MeasuredPressure).ToArray();
            var validationError = ValidatePressureCalibrationRows(ordered);
            if (!string.IsNullOrEmpty(validationError))
            {
                ShowPressureCalibrationWarning(validationError);
                return;
            }

            try
            {
                var path = Path.Combine(Application.StartupPath, "Config", "AOConfig.xml");
                CalibrationConfigStore.SaveAoCalibrations(
                    path,
                    new Dictionary<string, IReadOnlyList<AoCalibrationPoint>>(StringComparer.OrdinalIgnoreCase)
                    {
                        [device] = ordered.Select(x => new AoCalibrationPoint
                        {
                            CommandPressure = x.CommandPressure,
                            Pressure = x.MeasuredPressure,
                            Voltage = x.Voltage
                        }).ToArray()
                    },
                    _cfg.AO.MinVoltage,
                    _cfg.AO.MaxVoltage);

                StopAndDisposePressureCalibrationSession();
                _cfg.AO = ConfigLoader.LoadAO(path, logger);
                LoadPressureCalibrationConfiguration();
                SetPressureCalibrationStatus(
                    $"{device} 校正已保存，原配置已备份为 AOConfig.xml.bak；再次开始校正后使用新映射。",
                    false);
                MessageBox.Show(
                    this,
                    $"{device} 气缸压力输出校正保存成功。",
                    "保存成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetPressureCalibrationStatus("保存失败：" + ex.Message, true);
                MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PressureCalibrationTimerTick(object sender, EventArgs e)
        {
            var coordinator = _pressureCalibrationCoordinator;
            if (coordinator == null) return;

            try
            {
                PressureCalibrationCoordinator.TryResolveDevice(
                    CmbPressureCalibrationCylinder.SelectedItem?.ToString(),
                    out var hydraulicId,
                    out _);
                var sample = coordinator.ReadPressureSample(hydraulicId);
                _latestPressureBar = sample.ValueBar;
                var finite = sample.IsFinite && sample.MonotonicTicks > 0;
                LblPressureCalibrationLive.Text = finite
                    ? $"实时压力：{sample.ValueBar:F2} bar"
                    : "实时压力：-- bar";
                LblPressureCalibrationSample.Text = finite
                    ? $"采样：{sample.AgeMs:F0} ms"
                    : "采样：无效";

                if (finite && !_measuredPressureEdited)
                    SetMeasuredPressureText(sample.ValueBar);

                if (!coordinator.IsOutputActive) return;
                var safety = coordinator.CheckSafety();
                if (safety.Safe) return;
                SetPressureCalibrationControls(true, false);
                SetPressureCalibrationStatus(safety.Error, true);
                _pressureCalibrationTimer.Stop();
                MessageBox.Show(this, safety.Error, "压力校正已紧急停止", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (_pressureCalibrationCoordinator != null) _pressureCalibrationTimer.Start();
            }
            catch (Exception ex)
            {
                StopPressureCalibrationOutput(false);
                SetPressureCalibrationStatus("压力采样异常，已停止输出：" + ex.Message, true);
            }
        }

        private void PressureCalibrationSafetyTripped(PressureCalibrationSafetyResult result)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => PressureCalibrationSafetyTripped(result))); }
                catch { }
                return;
            }

            SetPressureCalibrationControls(_pressureCalibrationCoordinator != null, false);
            LblPressureCalibrationVoltage.Text = "AO：-- V";
            SetPressureCalibrationStatus(result.Error, true);
            MessageBox.Show(
                this,
                result.Error,
                "压力校正已紧急停止",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void PressureCalibrationCylinderChanged(object sender, EventArgs e)
        {
            var hadSession = _pressureCalibrationCoordinator != null;
            StopAndDisposePressureCalibrationSession();
            _measuredPressureEdited = false;
            _latestPressureBar = double.NaN;
            SetMeasuredPressureText(double.NaN);
            BindPressureCalibrationGrid();
            if (hadSession)
                SetPressureCalibrationStatus("气缸已切换，校正硬件已安全释放；请重新开始校正。", false);
        }

        private void PressureCalibrationUseLiveClick(object sender, EventArgs e)
        {
            if (!CalibrationMath.IsFinite(_latestPressureBar))
            {
                ShowPressureCalibrationWarning("当前没有有效的实时压力值。");
                return;
            }

            _measuredPressureEdited = false;
            SetMeasuredPressureText(_latestPressureBar);
            SetPressureCalibrationStatus("实测压力已恢复为自动读取值。", false);
        }

        private void PressureCalibrationMeasuredTextChanged(object sender, EventArgs e)
        {
            if (!_updatingMeasuredPressure) _measuredPressureEdited = true;
        }

        private void PressureCalibrationTabChanged(object sender, EventArgs e)
        {
            if (TabSetting.SelectedTab != tabPagePressureCalibration)
                StopAndDisposePressureCalibrationSession();
        }

        private void FrmTestSettingPressureCalibrationFormClosing(object sender, FormClosingEventArgs e)
        {
            StopAndDisposePressureCalibrationSession();
        }

        private void StopPressureCalibrationOutput(bool showStatus)
        {
            if (_pressureCalibrationCoordinator == null) return;
            var ok = _pressureCalibrationCoordinator.StopAll();
            SetPressureCalibrationControls(true, false);
            LblPressureCalibrationVoltage.Text = "AO：-- V";
            if (showStatus)
                SetPressureCalibrationStatus(
                    ok ? "压力 DO 已关闭，AO 已回零。" : "已请求停止输出，但部分硬件复位失败，请检查设备。",
                    !ok);
        }

        private void StopAndDisposePressureCalibrationSession()
        {
            _pressureCalibrationTimer?.Stop();
            var coordinator = _pressureCalibrationCoordinator;
            _pressureCalibrationCoordinator = null;
            try { coordinator?.Dispose(); } catch { }
            if (BtnPressureCalibrationStart != null)
            {
                SetPressureCalibrationControls(false, false);
                LblPressureCalibrationVoltage.Text = "AO：-- V";
                LblPressureCalibrationSample.Text = "采样：未启动";
            }
        }

        private void BindPressureCalibrationGrid()
        {
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows))
            {
                DgvPressureCalibration.DataSource = null;
                LblPressureCalibrationFormula.Text = string.Empty;
                return;
            }

            DgvPressureCalibration.DataSource = rows;
            if (_cfg.AO.Devices.TryGetValue(device, out var config))
            {
                LblPressureCalibrationFormula.Text = rows.Count >= 2
                    ? $"当前有效多点表：{rows.Count} 点"
                    : $"线性回退：pressure = voltage × {config.ScaleK:G6} + {config.Offset:G6}";
            }
        }

        private void SortPressureCalibrationRows(string device)
        {
            var sorted = _pressureCalibrationRows[device]
                .OrderBy(x => x.MeasuredPressure)
                .ThenBy(x => x.Voltage)
                .ToArray();
            var rows = new BindingList<PressureCalibrationRow>();
            for (var i = 0; i < sorted.Length; i++)
            {
                sorted[i].Sequence = i + 1;
                rows.Add(sorted[i]);
            }
            _pressureCalibrationRows[device] = rows;
            DgvPressureCalibration.DataSource = rows;
            BindPressureCalibrationGrid();
        }

        private static void RenumberPressureCalibrationRows(BindingList<PressureCalibrationRow> rows)
        {
            for (var i = 0; i < rows.Count; i++) rows[i].Sequence = i + 1;
            rows.ResetBindings();
        }

        private string ValidatePressureCalibrationRows(PressureCalibrationRow[] rows)
        {
            if (rows.Length < 2) return "至少需要两个有效校正点。";
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                if (!CalibrationMath.IsFinite(row.CommandPressure) ||
                    !CalibrationMath.IsFinite(row.MeasuredPressure) ||
                    !CalibrationMath.IsFinite(row.Voltage))
                    return "校正点必须是有效数字。";
                var effectiveLimit = _cfg.AO.MaxPressure > 0
                    ? Math.Min(PressureCalibrationCoordinator.CalibrationPressureLimitBar, _cfg.AO.MaxPressure)
                    : PressureCalibrationCoordinator.CalibrationPressureLimitBar;
                if (row.CommandPressure < 0 || row.CommandPressure > effectiveLimit)
                    return "校正点的命令压力超出允许范围。";
                if (row.Voltage < _cfg.AO.MinVoltage || row.Voltage > _cfg.AO.MaxVoltage)
                    return "校正点的 AO 电压超出配置范围。";
                if (i > 0 && (row.MeasuredPressure <= rows[i - 1].MeasuredPressure ||
                              row.Voltage <= rows[i - 1].Voltage))
                    return "实测压力与 AO 电压必须同时严格递增，请检查重复点或异常点。";
            }
            return string.Empty;
        }

        private void SetMeasuredPressureText(double value)
        {
            _updatingMeasuredPressure = true;
            try
            {
                TxtPressureCalibrationMeasured.Text = CalibrationMath.IsFinite(value)
                    ? value.ToString("F2", CultureInfo.CurrentCulture)
                    : string.Empty;
            }
            finally
            {
                _updatingMeasuredPressure = false;
            }
        }

        private void SetPressureCalibrationControls(bool sessionStarted, bool outputActive)
        {
            BtnPressureCalibrationStart.Enabled = !sessionStarted;
            BtnPressureCalibrationOutput.Enabled = sessionStarted && !outputActive;
            BtnPressureCalibrationStop.Enabled = sessionStarted;
            BtnPressureCalibrationRecord.Enabled = sessionStarted && outputActive;
            CmbPressureCalibrationCylinder.Enabled = !outputActive;
            TxtPressureCalibrationCommand.Enabled = !outputActive;
        }

        private void SetPressureCalibrationStatus(string text, bool error)
        {
            if (LblPressureCalibrationStatus == null) return;
            LblPressureCalibrationStatus.Text = text;
            LblPressureCalibrationStatus.ForeColor = error
                ? Color.FromArgb(192, 0, 0)
                : Color.FromArgb(48, 48, 48);
        }

        private void ShowPressureCalibrationWarning(string message)
        {
            SetPressureCalibrationStatus(message, true);
            MessageBox.Show(this, message, "压力校正提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static bool TryReadCalibrationNumber(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static FlowLayoutPanel NewCalibrationFlowPanel() => new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(4, 6, 4, 2)
        };

        private static UILabel NewCalibrationLabel(string text) => new UILabel
        {
            Text = text,
            AutoSize = false,
            Width = Math.Max(62, text.Length * 17),
            Height = 42,
            Font = new Font("Arial", 10.5782F),
            ForeColor = Color.FromArgb(48, 48, 48),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(4, 0, 4, 0)
        };

        private static UILabel NewCalibrationValueLabel(string text, int width) => new UILabel
        {
            Text = text,
            AutoSize = false,
            Width = width,
            Height = 42,
            Font = new Font("Arial", 10.5782F),
            ForeColor = Color.FromArgb(48, 48, 48),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(4, 0, 4, 0)
        };

        private static UIComboBox NewCalibrationComboBox(int width) => new UIComboBox
        {
            DataSource = null,
            FillColor = Color.White,
            Font = new Font("宋体", 12F),
            Width = width,
            Height = 42,
            MinimumSize = new Size(63, 0),
            Padding = new Padding(0, 0, 30, 2),
            SymbolSize = 24,
            TextAlignment = ContentAlignment.MiddleCenter,
            DropDownStyle = UIDropDownStyle.DropDownList,
            Margin = new Padding(4, 0, 14, 0)
        };

        private static UITextBox NewCalibrationTextBox(string text, int width) => new UITextBox
        {
            Text = text,
            Width = width,
            Height = 42,
            MinimumSize = new Size(1, 16),
            Padding = new Padding(5),
            Font = new Font("Arial", 10.5782F),
            TextAlignment = ContentAlignment.MiddleCenter,
            ShowText = false,
            Margin = new Padding(4, 0, 14, 0)
        };

        private static UIButton NewCalibrationButton(string text, int width, bool danger = false)
        {
            var color = danger ? Color.FromArgb(245, 108, 108) : Color.FromArgb(80, 160, 255);
            return new UIButton
            {
                Text = text,
                Width = width,
                Height = 42,
                MinimumSize = new Size(1, 1),
                Font = new Font("Arial", 10.5782F),
                Cursor = Cursors.Hand,
                FillColor = color,
                RectColor = color,
                ForeColor = Color.White,
                Margin = new Padding(6, 0, 6, 0)
            };
        }

        private static UIDataGridView NewPressureCalibrationGrid()
        {
            var grid = new UIDataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 60,
                RowHeadersWidth = 70,
                RowTemplate = { Height = 52 },
                GridColor = Color.FromArgb(80, 160, 255),
                StripeOddColor = Color.FromArgb(235, 243, 255),
                Font = new Font("宋体", 12F)
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(80, 160, 255),
                ForeColor = Color.White,
                Font = new Font("Arial", 10.5782F),
                WrapMode = DataGridViewTriState.True
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(48, 48, 48),
                SelectionBackColor = Color.FromArgb(80, 160, 255),
                SelectionForeColor = Color.White
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(235, 243, 255),
                ForeColor = Color.FromArgb(48, 48, 48)
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "序号",
                DataPropertyName = "Sequence",
                Width = 110
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "命令压力(bar)",
                DataPropertyName = "CommandPressure",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "F2", Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "实测压力(bar)",
                DataPropertyName = "MeasuredPressure",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "F2", Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "AO 输出电压(V)",
                DataPropertyName = "Voltage",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                DefaultCellStyle = new DataGridViewCellStyle { Format = "F4", Alignment = DataGridViewContentAlignment.MiddleCenter }
            });
            return grid;
        }

        private sealed class PressureCalibrationRow
        {
            public int Sequence { get; set; }
            public double CommandPressure { get; set; }
            public double MeasuredPressure { get; set; }
            public double Voltage { get; set; }
        }
    }
}
