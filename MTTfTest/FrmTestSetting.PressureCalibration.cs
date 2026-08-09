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

        private const double PressureCalibrationCommandMatchToleranceBar = 0.01;
        private static readonly Color PressureCalibrationPrimaryColor = Color.FromArgb(80, 160, 255);
        private static readonly Color PressureCalibrationPrimaryHoverColor = Color.FromArgb(64, 145, 240);
        private static readonly Color PressureCalibrationPrimaryPressColor = Color.FromArgb(48, 128, 220);
        private static readonly Color PressureCalibrationSelectionColor = Color.FromArgb(204, 226, 255);
        private static readonly Color PressureCalibrationChangedColor = Color.FromArgb(230, 242, 255);

        private TabPage tabPagePressureCalibration;
        private UserControl _pressureCalibrationScaleHost;
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
        private bool _bindingPressureCalibrationGrid;
        private double _latestPressureBar = double.NaN;

        private void InitializePressureCalibrationPage()
        {
            tabPagePressureCalibration = new TabPage
            {
                Name = "tabPagePressureCalibration",
                Text = "气缸压力输出校正",
                UseVisualStyleBackColor = false,
                BackColor = Color.FromArgb(243, 249, 255)
            };

            // 主窗体历史页面使用 AutoScaleMode.None。动态控件的字体会随系统 DPI 放大，
            // 但 TableLayoutPanel 的绝对行列尺寸不会自动同比放大，因此在页面组装完成后
            // 再按实际 DPI 显式缩放整个布局树，避免 200% 开发机上文字被裁切。
            _pressureCalibrationScaleHost = new UserControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(243, 249, 255),
                AutoScaleMode = AutoScaleMode.None
            };

            var root = new UITableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(243, 249, 255),
                ColumnCount = 3,
                RowCount = 3,
                Font = new Font("Microsoft YaHei UI", 10F)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

            var group = new UIGroupBox
            {
                Dock = DockStyle.Fill,
                Text = "气缸压力输出校正",
                TextAlignment = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 10.5F),
                Padding = new Padding(0, 30, 0, 0),
                Margin = new Padding(4)
            };
            root.Controls.Add(group, 1, 1);

            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(18, 10, 18, 12)
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 148F));
            group.Controls.Add(content);

            var commandBar = NewCalibrationTable(
                8,
                70F, 200F, 150F, 130F, -1F, 140F, 140F, 140F);
            CmbPressureCalibrationCylinder = NewCalibrationComboBox(188);
            CmbPressureCalibrationCylinder.Items.AddRange(new object[] { "Cylinder1", "Cylinder2" });
            CmbPressureCalibrationCylinder.SelectedIndexChanged += PressureCalibrationCylinderChanged;
            TxtPressureCalibrationCommand = NewCalibrationTextBox("70", 118);
            TxtPressureCalibrationCommand.Maximum = PressureCalibrationCoordinator.CalibrationPressureLimitBar;
            TxtPressureCalibrationCommand.Minimum = 0;
            BtnPressureCalibrationStart = NewCalibrationButton(
                "开始校正", 122, CalibrationButtonStyle.Primary);
            BtnPressureCalibrationOutput = NewCalibrationButton(
                "输出压力", 122, CalibrationButtonStyle.Secondary);
            BtnPressureCalibrationStop = NewCalibrationButton(
                "停止输出", 122, CalibrationButtonStyle.Danger);
            BtnPressureCalibrationStart.Click += PressureCalibrationStartClick;
            BtnPressureCalibrationOutput.Click += PressureCalibrationOutputClick;
            BtnPressureCalibrationStop.Click += PressureCalibrationStopClick;
            AddCalibrationCell(commandBar, NewCalibrationLabel("气缸"), 0);
            AddCalibrationCell(commandBar, CmbPressureCalibrationCylinder, 1);
            AddCalibrationCell(commandBar, NewCalibrationLabel("命令压力 (bar)"), 2);
            AddCalibrationCell(commandBar, TxtPressureCalibrationCommand, 3);
            AddCalibrationCell(commandBar, BtnPressureCalibrationStart, 5);
            AddCalibrationCell(commandBar, BtnPressureCalibrationOutput, 6);
            AddCalibrationCell(commandBar, BtnPressureCalibrationStop, 7);
            content.Controls.Add(commandBar, 0, 0);

            var liveBar = NewCalibrationTable(
                7,
                150F, 190F, 150F, 145F, 160F, 140F, -1F);
            LblPressureCalibrationVoltage = NewCalibrationValueLabel("AO：-- V", 150);
            LblPressureCalibrationLive = NewCalibrationValueLabel("实时压力：-- bar", 190);
            LblPressureCalibrationSample = NewCalibrationValueLabel("采样：未启动", 180);
            TxtPressureCalibrationMeasured = NewCalibrationTextBox(string.Empty, 150);
            TxtPressureCalibrationMeasured.Watermark = "自动读取，可修改";
            TxtPressureCalibrationMeasured.TextChanged += PressureCalibrationMeasuredTextChanged;
            BtnPressureCalibrationUseLive = NewCalibrationButton(
                "使用实时值", 122, CalibrationButtonStyle.Secondary);
            BtnPressureCalibrationUseLive.Click += PressureCalibrationUseLiveClick;
            AddCalibrationCell(liveBar, LblPressureCalibrationVoltage, 0);
            AddCalibrationCell(liveBar, LblPressureCalibrationLive, 1);
            AddCalibrationCell(liveBar, LblPressureCalibrationSample, 2);
            AddCalibrationCell(liveBar, NewCalibrationLabel("实测压力 (bar)"), 3);
            AddCalibrationCell(liveBar, TxtPressureCalibrationMeasured, 4);
            AddCalibrationCell(liveBar, BtnPressureCalibrationUseLive, 5);
            content.Controls.Add(liveBar, 0, 1);

            DgvPressureCalibration = NewPressureCalibrationGrid();
            DgvPressureCalibration.SelectionChanged += PressureCalibrationGridSelectionChanged;
            content.Controls.Add(DgvPressureCalibration, 0, 2);

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.Transparent
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            var actionBar = NewCalibrationTable(5, -1F, 155F, 165F, 105F, 145F);
            BtnPressureCalibrationSave = NewCalibrationButton(
                "保存校正", 127, CalibrationButtonStyle.Primary);
            BtnPressureCalibrationClear = NewCalibrationButton(
                "清空", 87, CalibrationButtonStyle.Secondary);
            // “清空”所在列较窄，使用与短文本匹配的最小宽度，避免最小宽度
            // 挤占单元格边距后与右侧“保存校正”按钮贴在一起。
            BtnPressureCalibrationClear.MinimumSize = new Size(80, 40);
            BtnPressureCalibrationDelete = NewCalibrationButton(
                "删除选中点", 147, CalibrationButtonStyle.Danger);
            BtnPressureCalibrationRecord = NewCalibrationButton(
                "记录当前点", 137, CalibrationButtonStyle.Secondary);
            BtnPressureCalibrationRecord.Click += PressureCalibrationRecordClick;
            BtnPressureCalibrationDelete.Click += PressureCalibrationDeleteClick;
            BtnPressureCalibrationClear.Click += PressureCalibrationClearClick;
            BtnPressureCalibrationSave.Click += PressureCalibrationSaveClick;
            AddCalibrationCell(actionBar, BtnPressureCalibrationRecord, 1);
            AddCalibrationCell(actionBar, BtnPressureCalibrationDelete, 2);
            AddCalibrationCell(actionBar, BtnPressureCalibrationClear, 3);
            AddCalibrationCell(actionBar, BtnPressureCalibrationSave, 4);
            LblPressureCalibrationFormula = NewCalibrationValueLabel(string.Empty, 650);
            LblPressureCalibrationFormula.Dock = DockStyle.Fill;
            LblPressureCalibrationFormula.TextAlign = ContentAlignment.MiddleLeft;
            LblPressureCalibrationFormula.Padding = new Padding(8, 0, 8, 0);
            footer.Controls.Add(LblPressureCalibrationFormula, 0, 0);
            footer.Controls.Add(actionBar, 0, 1);
            LblPressureCalibrationStatus = NewCalibrationValueLabel(
                "请先开始校正；输出前请确认管路安全。",
                1000);
            LblPressureCalibrationStatus.ForeColor = Color.FromArgb(48, 48, 48);
            LblPressureCalibrationStatus.Dock = DockStyle.Fill;
            LblPressureCalibrationStatus.TextAlign = ContentAlignment.MiddleLeft;
            LblPressureCalibrationStatus.Padding = new Padding(8, 0, 8, 0);
            footer.Controls.Add(LblPressureCalibrationStatus, 0, 2);
            content.Controls.Add(footer, 0, 3);

            using (var graphics = CreateGraphics())
            {
                var layoutScale = Math.Max(1F, graphics.DpiX / 96F);
                if (layoutScale > 1.01F)
                    root.Scale(new SizeF(layoutScale, layoutScale));
            }
            _pressureCalibrationScaleHost.Controls.Add(root);
            tabPagePressureCalibration.Controls.Add(_pressureCalibrationScaleHost);
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
                            Voltage = point.Voltage,
                            IsHistorical = true
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
                        ? $"已忽略 {invalidPoints} 个超出 AO 电压范围的历史记录；当前输出继续使用 ScaleK/Offset 线性公式。"
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

            var command = _pressureCalibrationCoordinator.ActiveCommandPressureBar;
            var matchingIndex = CalibrationMath.FindMatchingCommandIndex(
                rows.Select(x => x.CommandPressure),
                command,
                PressureCalibrationCommandMatchToleranceBar);
            var row = matchingIndex >= 0 ? rows[matchingIndex] : null;
            var updatedExisting = row != null;
            if (row == null)
            {
                row = new PressureCalibrationRow
                {
                    CommandPressure = command,
                    IsChanged = true
                };
                rows.Add(row);
            }

            row.CommandPressure = command;
            row.MeasuredPressure = measured;
            row.Voltage = _pressureCalibrationCoordinator.ActiveVoltage;
            row.IsChanged = true;
            SortPressureCalibrationRows(device, row);
            SetPressureCalibrationStatus(
                $"已{(updatedExisting ? "更新" : "新增")}第 {row.Sequence} 行：命令 {command:F2} bar，" +
                $"实测 {measured:F2} bar，AO {_pressureCalibrationCoordinator.ActiveVoltage:F4} V；尚未保存。",
                false);
        }

        private void PressureCalibrationDeleteClick(object sender, EventArgs e)
        {
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows) ||
                !(DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow row))
                return;
            if (MessageBox.Show(
                    this,
                    $"确定删除第 {row.Sequence} 行校正点吗？\r\n" +
                    $"命令 {row.CommandPressure:F2} bar，实测 {row.MeasuredPressure:F2} bar，" +
                    $"AO {row.Voltage:F4} V\r\n\r\n删除后需点击“保存校正”才会写入配置。",
                    "确认删除校正点",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            rows.Remove(row);
            RenumberPressureCalibrationRows(rows);
            BindPressureCalibrationGrid();
            SetPressureCalibrationStatus("已删除选中校正点；尚未保存。", false);
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
            {
                rows.Clear();
                BindPressureCalibrationGrid();
                SetPressureCalibrationStatus("当前页面的校正点已清空；尚未保存。", false);
            }
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

            var ordered = rows.OrderBy(x => x.Voltage).ToArray();
            var validationError = ValidatePressureCalibrationRows(ordered);
            if (!string.IsNullOrEmpty(validationError))
            {
                ShowPressureCalibrationWarning(validationError);
                return;
            }

            if (!TryFitPressureCalibrationRows(
                    ordered,
                    out var scaleK,
                    out var offset,
                    out var rSquared,
                    out var fitError))
            {
                ShowPressureCalibrationWarning(fitError);
                return;
            }

            if (!ConfirmMixedPressureCalibrationSave(rows, scaleK, offset, rSquared))
                return;

            try
            {
                var path = Path.Combine(Application.StartupPath, "Config", "AOConfig.xml");
                CalibrationConfigStore.SaveAoLinearCalibration(
                    path,
                    device,
                    ordered.Select(x => new AoCalibrationPoint
                    {
                        CommandPressure = x.CommandPressure,
                        Pressure = x.MeasuredPressure,
                        Voltage = x.Voltage
                    }).ToArray(),
                    _cfg.AO.MinVoltage,
                    _cfg.AO.MaxVoltage);

                StopAndDisposePressureCalibrationSession();
                _cfg.AO = ConfigLoader.LoadAO(path, logger);
                LoadPressureCalibrationConfiguration();
                SetPressureCalibrationStatus(
                    $"{device} 线性校正已保存：P = V × {scaleK:G8} + {offset:G8}，R²={rSquared:F5}。",
                    false);
                MessageBox.Show(
                    this,
                    $"{device} 气缸压力线性公式保存成功。\r\n\r\n" +
                    $"P = V × {scaleK:G8} + {offset:G8}\r\nR² = {rSquared:F5}\r\n\r\n" +
                    "原配置已备份为 AOConfig.xml.bak。",
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

        private bool ConfirmMixedPressureCalibrationSave(
            IEnumerable<PressureCalibrationRow> rows,
            double mixedScaleK,
            double mixedOffset,
            double mixedRSquared)
        {
            var analysis = CalibrationMath.AnalyzeMixedCalibration(rows.Select(x =>
                (x.Voltage, x.MeasuredPressure, x.IsChanged, x.IsHistorical)));
            if (!analysis.HasMixedData) return true;

            var impact = analysis.CanEvaluateChangedTrend
                ? $"仅按本次更新点拟合：P = V × {analysis.ChangedScaleK:G7} + " +
                  $"{analysis.ChangedOffset:G7}\r\n" +
                  $"未更新旧点偏离本次趋势的最大值：{analysis.MaxUnchangedDeviationBar:F2} bar" +
                  $"（参考阈值 {analysis.EvaluationToleranceBar:F2} bar）"
                : "本次有效更新点不足两个，无法独立判断新的压力关系；旧点会直接影响拟合结果。";
            var risk = analysis.HasMaterialImpact ? "较高" : "可见";
            var message =
                $"检测到新旧校正点混合，影响风险：{risk}。\r\n\r\n" +
                $"本次新增/更新：{analysis.ChangedPointCount} 点\r\n" +
                $"未更新历史点：{analysis.UnchangedHistoricalPointCount} 点\r\n" +
                $"混合拟合：P = V × {mixedScaleK:G7} + {mixedOffset:G7}，" +
                $"R²={mixedRSquared:F5}\r\n{impact}\r\n\r\n" +
                "建议先更新全部计划压力点，或删除已不适用的旧点，再保存。\r\n" +
                "是否仍要保存当前混合数据？";
            return MessageBox.Show(
                       this,
                       message,
                       "新旧校正点混用确认",
                       MessageBoxButtons.YesNo,
                       MessageBoxIcon.Warning,
                       MessageBoxDefaultButton.Button2) == DialogResult.Yes;
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

        private void BindPressureCalibrationGrid(PressureCalibrationRow rowToSelect = null)
        {
            var device = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
            if (device == null || !_pressureCalibrationRows.TryGetValue(device, out var rows))
            {
                DgvPressureCalibration.DataSource = null;
                LblPressureCalibrationFormula.Text = string.Empty;
                UpdatePressureCalibrationActionStates();
                return;
            }

            _bindingPressureCalibrationGrid = true;
            try
            {
                DgvPressureCalibration.DataSource = rows;
                DgvPressureCalibration.CurrentCell = null;
                DgvPressureCalibration.ClearSelection();
                RefreshPressureCalibrationRowStyles();
                if (rowToSelect != null)
                {
                    foreach (DataGridViewRow gridRow in DgvPressureCalibration.Rows)
                    {
                        if (!ReferenceEquals(gridRow.DataBoundItem, rowToSelect)) continue;
                        gridRow.Selected = true;
                        DgvPressureCalibration.CurrentCell = gridRow.Cells[0];
                        break;
                    }
                }
            }
            finally
            {
                _bindingPressureCalibrationGrid = false;
            }

            if (_cfg.AO.Devices.TryGetValue(device, out var config))
            {
                var current = $"当前公式：P = V × {config.ScaleK:G7} + {config.Offset:G7}";
                if (TryFitPressureCalibrationRows(
                        rows.ToArray(),
                        out var scaleK,
                        out var offset,
                        out var rSquared,
                        out _))
                    LblPressureCalibrationFormula.Text =
                        $"{current}    拟合预览：P = V × {scaleK:G7} + {offset:G7}（R²={rSquared:F5}）";
                else
                    LblPressureCalibrationFormula.Text = current + "    至少记录两个点后显示拟合预览。";
            }
            UpdatePressureCalibrationActionStates();
        }

        private void PressureCalibrationGridSelectionChanged(object sender, EventArgs e)
        {
            if (_bindingPressureCalibrationGrid) return;
            UpdatePressureCalibrationActionStates();
            if (!(DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow row)) return;
            if (_pressureCalibrationCoordinator?.IsOutputActive == true)
            {
                SetPressureCalibrationStatus("当前仍在输出压力；请先停止输出，再选择其他校正点。", true);
                return;
            }

            TxtPressureCalibrationCommand.Text =
                row.CommandPressure.ToString("F2", CultureInfo.CurrentCulture);
            SetPressureCalibrationStatus(
                $"已选择第 {row.Sequence} 行，命令压力已同步为 {row.CommandPressure:F2} bar。",
                false);
        }

        private void RefreshPressureCalibrationRowStyles()
        {
            foreach (DataGridViewRow gridRow in DgvPressureCalibration.Rows)
            {
                var changed = gridRow.DataBoundItem is PressureCalibrationRow row && row.IsChanged;
                gridRow.DefaultCellStyle.BackColor = changed
                    ? PressureCalibrationChangedColor
                    : gridRow.Index % 2 == 0
                        ? Color.White
                        : Color.FromArgb(243, 249, 255);
                gridRow.DefaultCellStyle.ForeColor = changed
                    ? Color.FromArgb(32, 96, 170)
                    : Color.FromArgb(48, 48, 48);
                gridRow.Cells[0].ToolTipText = changed ? "本次校正中已新增或更新，尚未保存" : string.Empty;
            }
        }

        private void SortPressureCalibrationRows(string device, PressureCalibrationRow rowToSelect = null)
        {
            var sorted = _pressureCalibrationRows[device]
                .OrderBy(x => x.CommandPressure)
                .ThenBy(x => x.MeasuredPressure)
                .ThenBy(x => x.Voltage)
                .ToArray();
            var rows = new BindingList<PressureCalibrationRow>();
            for (var i = 0; i < sorted.Length; i++)
            {
                sorted[i].Sequence = i + 1;
                rows.Add(sorted[i]);
            }
            _pressureCalibrationRows[device] = rows;
            BindPressureCalibrationGrid(rowToSelect);
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

        private static bool TryFitPressureCalibrationRows(
            IEnumerable<PressureCalibrationRow> rows,
            out double scaleK,
            out double offset,
            out double rSquared,
            out string error) =>
            CalibrationMath.TryFitPressureLine(
                rows?.Select(x => (x.Voltage, x.MeasuredPressure)),
                out scaleK,
                out offset,
                out rSquared,
                out error);

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
            BtnPressureCalibrationUseLive.Enabled = sessionStarted && outputActive;
            CmbPressureCalibrationCylinder.Enabled = !outputActive;
            TxtPressureCalibrationCommand.Enabled = !outputActive;
            TxtPressureCalibrationMeasured.Enabled = sessionStarted && outputActive;
            DgvPressureCalibration.Enabled = !outputActive;
            UpdatePressureCalibrationActionStates();
        }

        private void UpdatePressureCalibrationActionStates()
        {
            if (BtnPressureCalibrationDelete == null) return;
            var outputActive = _pressureCalibrationCoordinator?.IsOutputActive == true;
            var device = CmbPressureCalibrationCylinder?.SelectedItem?.ToString();
            BindingList<PressureCalibrationRow> rows = null;
            var hasRows = device != null &&
                          _pressureCalibrationRows.TryGetValue(device, out rows) &&
                          rows.Count > 0;
            var hasSelection = DgvPressureCalibration?.CurrentRow?.DataBoundItem is PressureCalibrationRow;
            BtnPressureCalibrationDelete.Enabled = !outputActive && hasSelection;
            BtnPressureCalibrationClear.Enabled = !outputActive && hasRows;
            BtnPressureCalibrationSave.Enabled = !outputActive && rows?.Count >= 2;
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

        private static TableLayoutPanel NewCalibrationTable(int columnCount, params float[] widths)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = columnCount,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(2, 4, 2, 4)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            for (var i = 0; i < columnCount; i++)
            {
                var width = i < widths.Length ? widths[i] : -1F;
                panel.ColumnStyles.Add(width > 0
                    ? new ColumnStyle(SizeType.Absolute, width)
                    : new ColumnStyle(SizeType.Percent, 100F));
            }
            return panel;
        }

        private static void AddCalibrationCell(TableLayoutPanel panel, Control control, int column)
        {
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(6, 7, 6, 7);
            panel.Controls.Add(control, column, 0);
        }

        private static UILabel NewCalibrationLabel(string text) => new UILabel
        {
            Text = text,
            AutoSize = false,
            AutoEllipsis = true,
            MinimumSize = new Size(64, 40),
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = Color.FromArgb(30, 41, 59),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty
        };

        private static UILabel NewCalibrationValueLabel(string text, int width) => new UILabel
        {
            Text = text,
            AutoSize = false,
            AutoEllipsis = true,
            Width = width,
            MinimumSize = new Size(80, 40),
            Font = new Font("Microsoft YaHei UI", 10F),
            ForeColor = Color.FromArgb(30, 41, 59),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty
        };

        private static UIComboBox NewCalibrationComboBox(int width) => new UIComboBox
        {
            DataSource = null,
            FillColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F),
            Width = width,
            Height = 42,
            MinimumSize = new Size(168, 40),
            Padding = new Padding(0, 0, 30, 2),
            SymbolSize = 24,
            TextAlignment = ContentAlignment.MiddleCenter,
            DropDownStyle = UIDropDownStyle.DropDownList,
            Margin = Padding.Empty
        };

        private static UITextBox NewCalibrationTextBox(string text, int width)
        {
            var textBox = new UITextBox
            {
                Text = text,
                Width = width,
                Height = 42,
                MinimumSize = new Size(105, 40),
                Padding = new Padding(5),
                Font = new Font("Microsoft YaHei UI", 10F),
                TextAlignment = ContentAlignment.MiddleCenter,
                ShowText = false,
                Margin = Padding.Empty
            };

            // Sunny.UI 的内部 TextBox 默认会填满放大后的控件高度，单行文字因此靠上。
            // 让内部文本框保持首选单行高度，再在每次 DPI/布局改变后垂直居中。
            textBox.TextBox.AutoSize = true;
            void CenterInnerTextBox(object sender, EventArgs e)
            {
                var inner = textBox.TextBox;
                inner.Width = Math.Max(1, textBox.ClientSize.Width - inner.Left * 2);
                inner.Top = Math.Max(0, (textBox.ClientSize.Height - inner.Height) / 2);
            }
            textBox.SizeChanged += CenterInnerTextBox;
            textBox.HandleCreated += CenterInnerTextBox;
            CenterInnerTextBox(textBox, EventArgs.Empty);
            return textBox;
        }

        private enum CalibrationButtonStyle
        {
            Primary,
            Secondary,
            Danger
        }

        private static UIButton NewCalibrationButton(
            string text,
            int width,
            CalibrationButtonStyle style)
        {
            var primary = PressureCalibrationPrimaryColor;
            var primaryHover = PressureCalibrationPrimaryHoverColor;
            var primaryPress = PressureCalibrationPrimaryPressColor;
            var danger = Color.FromArgb(220, 38, 38);
            var button = new UIButton
            {
                Text = text,
                Width = width,
                Height = 42,
                MinimumSize = new Size(104, 40),
                Font = new Font("Microsoft YaHei UI", 10F),
                Cursor = Cursors.Hand,
                Radius = 4,
                RectSize = 1,
                Margin = Padding.Empty
            };

            if (style == CalibrationButtonStyle.Primary)
            {
                button.FillColor = primary;
                button.FillHoverColor = primaryHover;
                button.FillPressColor = primaryPress;
                button.RectColor = primary;
                button.RectHoverColor = primaryHover;
                button.RectPressColor = primaryPress;
                button.ForeColor = Color.White;
            }
            else if (style == CalibrationButtonStyle.Danger)
            {
                button.FillColor = Color.White;
                button.FillHoverColor = Color.FromArgb(254, 242, 242);
                button.FillPressColor = Color.FromArgb(254, 226, 226);
                button.RectColor = danger;
                button.RectHoverColor = danger;
                button.RectPressColor = danger;
                button.ForeColor = danger;
            }
            else
            {
                button.FillColor = Color.White;
                button.FillHoverColor = Color.FromArgb(224, 242, 254);
                button.FillPressColor = Color.FromArgb(186, 230, 253);
                button.RectColor = primaryHover;
                button.RectHoverColor = primary;
                button.RectPressColor = primaryPress;
                button.ForeColor = primary;
            }

            return button;
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
                ColumnHeadersHeight = 48,
                RowHeadersVisible = false,
                RowTemplate = { Height = 44 },
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(203, 213, 225),
                StripeOddColor = Color.FromArgb(243, 249, 255),
                Font = new Font("Microsoft YaHei UI", 10F)
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = PressureCalibrationPrimaryColor,
                ForeColor = Color.White,
                SelectionBackColor = PressureCalibrationPrimaryColor,
                SelectionForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                WrapMode = DataGridViewTriState.True
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(30, 41, 59),
                SelectionBackColor = PressureCalibrationSelectionColor,
                SelectionForeColor = Color.FromArgb(15, 23, 42)
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(243, 249, 255),
                ForeColor = Color.FromArgb(30, 41, 59),
                SelectionBackColor = PressureCalibrationSelectionColor,
                SelectionForeColor = Color.FromArgb(15, 23, 42)
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "序号",
                DataPropertyName = "Sequence",
                Width = 90
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
            public bool IsHistorical { get; set; }
            public bool IsChanged { get; set; }
        }
    }
}
