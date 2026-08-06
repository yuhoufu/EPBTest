using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Config;

namespace MTEmbTest
{
    /// <summary>AI 传感器与液压 AO 的统一现场标定工作台。</summary>
    public sealed class FrmCalibrationWorkbench : Form
    {
        private readonly string _aiPath;
        private readonly string _aoPath;
        private readonly BindingList<SensorRow> _sensorRows = new BindingList<SensorRow>();
        private readonly Dictionary<string, BindingList<AoRow>> _aoRows =
            new Dictionary<string, BindingList<AoRow>>(StringComparer.OrdinalIgnoreCase);

        private readonly DataGridView _sensorGrid = NewGrid();
        private readonly DataGridView _aoGrid = NewGrid();
        private readonly ComboBox _sensorFilter = new ComboBox();
        private readonly ComboBox _aoDevice = new ComboBox();
        private readonly NumericUpDown _raw1 = NewNumber(-10, 10, 6);
        private readonly NumericUpDown _reference1 = NewNumber(-100000, 100000, 4);
        private readonly NumericUpDown _raw2 = NewNumber(-10, 10, 6);
        private readonly NumericUpDown _reference2 = NewNumber(-100000, 100000, 4);
        private readonly NumericUpDown _commandPressure = NewNumber(0, 1000, 2);
        private readonly NumericUpDown _measuredPressure = NewNumber(0, 1000, 2);
        private readonly Label _aoFormula = new Label();
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel("正在加载配置…");

        private AiConfigDetail _aiConfig;
        private AoConfig _aoConfig;

        public FrmCalibrationWorkbench() : this(null)
        {
        }

        public FrmCalibrationWorkbench(string applicationDirectory)
        {
            var baseDirectory = string.IsNullOrWhiteSpace(applicationDirectory)
                ? Application.StartupPath
                : Path.GetFullPath(applicationDirectory);
            _aiPath = Path.Combine(baseDirectory, "Config", "AIConfig.xml");
            _aoPath = Path.Combine(baseDirectory, "Config", "AOConfig.xml");
            Text = "数采卡校准";
            Name = "数采卡校准";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(1100, 700);
            Size = new Size(1380, 820);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Microsoft YaHei UI", 9F);
            BuildUi();
            Shown += (_, __) => LoadConfiguration();
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Color.FromArgb(15, 23, 42) };
            header.Controls.Add(new Label
            {
                Text = "传感器与气缸压力标定",
                ForeColor = Color.White,
                Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(24, 12)
            });
            header.Controls.Add(new Label
            {
                Text = "双点修正采集量程，多点修正目标压力与 AO 输出关系",
                ForeColor = Color.FromArgb(203, 213, 225),
                AutoSize = true,
                Location = new Point(27, 47)
            });

            var notice = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(20, 13, 20, 0),
                BackColor = Color.FromArgb(254, 249, 195),
                ForeColor = Color.FromArgb(113, 63, 18),
                Text = "安全提示：保存标定前请停止试验并确认所有 EPB 与气缸输出已关闭；保存后重新打开实时监视界面生效。"
            };

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 7) };
            tabs.TabPages.Add(BuildSensorTab());
            tabs.TabPages.Add(BuildAoTab());

            var statusStrip = new StatusStrip { SizingGrip = false };
            statusStrip.Items.Add(_status);

            Controls.Add(tabs);
            Controls.Add(notice);
            Controls.Add(header);
            Controls.Add(statusStrip);
        }

        private TabPage BuildSensorTab()
        {
            var tab = new TabPage("AI 传感器校准") { BackColor = BackColor, Padding = new Padding(12) };
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 7, 0, 0)
            };
            toolbar.Controls.Add(NewCaption("显示："));
            _sensorFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _sensorFilter.Width = 170;
            _sensorFilter.Items.AddRange(new object[] { "全部通道", "电流", "压力传感器", "其他传感器" });
            _sensorFilter.SelectedIndex = 0;
            _sensorFilter.SelectedIndexChanged += (_, __) => BindSensorGrid();
            toolbar.Controls.Add(_sensorFilter);
            toolbar.Controls.Add(NewHint("包括未启用通道，便于提前完成备用压力传感器标定。"));
            var save = NewPrimaryButton("保存 AI 标定", 130);
            save.Click += (_, __) => SaveSensors();
            toolbar.Controls.Add(save);

            ConfigureSensorGrid();
            var calculator = BuildSensorCalculator();

            tab.Controls.Add(_sensorGrid);
            tab.Controls.Add(calculator);
            tab.Controls.Add(toolbar);
            return tab;
        }

        private Control BuildSensorCalculator()
        {
            var group = new GroupBox
            {
                Text = "选中通道的两点标定",
                Dock = DockStyle.Bottom,
                Height = 152,
                Padding = new Padding(14)
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(4, 14, 4, 4)
            };
            layout.Controls.Add(NewCaption("点1 原始电压(V)"));
            layout.Controls.Add(_raw1);
            layout.Controls.Add(NewCaption("参考值"));
            layout.Controls.Add(_reference1);
            layout.Controls.Add(NewCaption("点2 原始电压(V)"));
            layout.Controls.Add(_raw2);
            layout.Controls.Add(NewCaption("参考值"));
            layout.Controls.Add(_reference2);

            var calculate = NewPrimaryButton("计算并应用", 118);
            calculate.Click += (_, __) => ApplyTwoPoint();
            layout.Controls.Add(calculate);
            var zero = NewSecondaryButton("按点1修正零位", 140);
            zero.Click += (_, __) => ApplyZeroPoint();
            layout.Controls.Add(zero);
            layout.Controls.Add(NewHint("置零会保留当前斜率，并令点1对应输入框中的参考值（通常填 0）。"));
            group.Controls.Add(layout);
            return group;
        }

        private TabPage BuildAoTab()
        {
            var tab = new TabPage("气缸输出标定") { BackColor = BackColor, Padding = new Padding(12) };
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 62,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 7, 0, 0)
            };
            toolbar.Controls.Add(NewCaption("气缸："));
            _aoDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            _aoDevice.Width = 180;
            _aoDevice.SelectedIndexChanged += (_, __) => BindAoGrid();
            toolbar.Controls.Add(_aoDevice);
            _aoFormula.AutoSize = true;
            _aoFormula.Margin = new Padding(18, 7, 18, 0);
            _aoFormula.ForeColor = Color.FromArgb(71, 85, 105);
            toolbar.Controls.Add(_aoFormula);
            var save = NewPrimaryButton("保存 AO 标定", 130);
            save.Click += (_, __) => SaveAo();
            toolbar.Controls.Add(save);

            ConfigureAoGrid();
            var observation = BuildAoObservationPanel();
            tab.Controls.Add(_aoGrid);
            tab.Controls.Add(observation);
            tab.Controls.Add(toolbar);
            return tab;
        }

        private Control BuildAoObservationPanel()
        {
            var panel = new GroupBox
            {
                Text = "添加一次现场观测",
                Dock = DockStyle.Bottom,
                Height = 158,
                Padding = new Padding(14)
            };
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(4, 14, 4, 4)
            };
            layout.Controls.Add(NewCaption("软件命令压力(bar)"));
            layout.Controls.Add(_commandPressure);
            layout.Controls.Add(NewCaption("参考表实测压力(bar)"));
            layout.Controls.Add(_measuredPressure);
            var add = NewPrimaryButton("换算电压并添加", 150);
            add.Click += (_, __) => AddAoObservation();
            layout.Controls.Add(add);
            var remove = NewSecondaryButton("删除选中点", 120);
            remove.Click += (_, __) => RemoveAoPoint();
            layout.Controls.Add(remove);
            var clear = NewSecondaryButton("清空并回退线性公式", 180);
            clear.Click += (_, __) => ClearAoPoints();
            layout.Controls.Add(clear);
            layout.Controls.Add(NewHint(
                "建议从低到高记录至少 3 个覆盖工作范围的点。系统按“实测压力→当时 AO 电压”插值，目标 70 bar 将直接反算对应电压。"));
            panel.Controls.Add(layout);
            return panel;
        }

        private void ConfigureSensorGrid()
        {
            _sensorGrid.Dock = DockStyle.Fill;
            _sensorGrid.AutoGenerateColumns = false;
            _sensorGrid.Columns.Add(TextColumn("启用", "EnabledText", 60, true));
            _sensorGrid.Columns.Add(TextColumn("参数名", "ParameterName", 150, true));
            _sensorGrid.Columns.Add(TextColumn("物理通道", "PhysicalChannel", 120, true));
            _sensorGrid.Columns.Add(TextColumn("类型", "Type", 110, true));
            _sensorGrid.Columns.Add(TextColumn("单位", "Unit", 65, true));
            _sensorGrid.Columns.Add(NumberColumn("斜率", "Scale", 120));
            _sensorGrid.Columns.Add(NumberColumn("截距", "Offset", 120));
            var zeroColumn = NumberColumn("零位漂移(V)", "Zero", 130);
            zeroColumn.MinimumWidth = 130;
            zeroColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _sensorGrid.Columns.Add(zeroColumn);
        }

        private void ConfigureAoGrid()
        {
            _aoGrid.Dock = DockStyle.Fill;
            _aoGrid.AutoGenerateColumns = false;
            _aoGrid.Columns.Add(NumberColumn("当时命令压力(bar)", "CommandPressure", 180));
            _aoGrid.Columns.Add(NumberColumn("参考表实测压力(bar)", "MeasuredPressure", 190));
            _aoGrid.Columns.Add(NumberColumn("AO 输出电压(V)", "Voltage", 170));
            _aoGrid.Columns.Add(TextColumn(
                "说明",
                "Description",
                520,
                true,
                DataGridViewAutoSizeColumnMode.Fill));
        }

        private void LoadConfiguration()
        {
            try
            {
                _aiConfig = AiConfigLoader.Load(_aiPath);
                _aoConfig = ConfigLoader.LoadAO(_aoPath, NullLogger.Instance);

                _sensorRows.Clear();
                foreach (var record in _aiConfig.Records.OrderBy(x => x.序号))
                {
                    _sensorRows.Add(new SensorRow
                    {
                        Enabled = record.是否启用 == 1,
                        ParameterName = record.参数名,
                        PhysicalChannel = record.物理通道,
                        Type = record.参数类型,
                        Unit = record.单位,
                        Scale = record.变换斜率,
                        Offset = record.变换截距,
                        Zero = record.零位漂移
                    });
                }

                _aoRows.Clear();
                foreach (var device in _aoConfig.Devices.Values.OrderBy(x => x.Name))
                {
                    var rows = new BindingList<AoRow>();
                    foreach (var point in device.VoltageToPressure
                                 .Where(x => CalibrationMath.IsFinite(x.Voltage) &&
                                             CalibrationMath.IsFinite(x.Pressure) &&
                                             x.Voltage >= _aoConfig.MinVoltage &&
                                             x.Voltage <= _aoConfig.MaxVoltage)
                                 .OrderBy(x => x.Pressure))
                    {
                        rows.Add(new AoRow
                        {
                            CommandPressure = point.Pressure,
                            MeasuredPressure = point.Pressure,
                            Voltage = point.Voltage
                        });
                    }
                    _aoRows[device.Name] = rows;
                }

                _aoDevice.Items.Clear();
                _aoDevice.Items.AddRange(_aoRows.Keys.Cast<object>().ToArray());
                if (_aoDevice.Items.Count > 0) _aoDevice.SelectedIndex = 0;
                BindSensorGrid();
                SetStatus($"已加载 {_sensorRows.Count} 路 AI、{_aoRows.Count} 路气缸配置。", false);
            }
            catch (Exception ex)
            {
                Tag = ex;
                SetStatus("配置加载失败：" + ex.Message, true);
                if (Visible)
                    MessageBox.Show(this, ex.Message, "无法加载标定配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BindSensorGrid()
        {
            if (_sensorRows == null) return;
            var filter = _sensorFilter.SelectedItem?.ToString() ?? "全部通道";
            IEnumerable<SensorRow> rows = _sensorRows;
            if (filter == "电流") rows = rows.Where(x => x.Type?.Contains("电流") == true);
            else if (filter == "压力传感器") rows = rows.Where(x => x.Type?.Contains("压力") == true);
            else if (filter == "其他传感器")
                rows = rows.Where(x => x.Type?.Contains("电流") != true && x.Type?.Contains("压力") != true);
            _sensorGrid.DataSource = new BindingList<SensorRow>(rows.ToList());
        }

        private void BindAoGrid()
        {
            var name = _aoDevice.SelectedItem?.ToString();
            if (name == null || !_aoRows.TryGetValue(name, out var rows)) return;
            _aoGrid.DataSource = rows;
            var device = _aoConfig.Devices[name];
            _aoFormula.Text = $"线性回退：pressure = voltage × {device.ScaleK:G6} + {device.Offset:G6}";
        }

        private SensorRow SelectedSensor() =>
            _sensorGrid.CurrentRow?.DataBoundItem as SensorRow;

        private void ApplyTwoPoint()
        {
            var row = SelectedSensor();
            if (row == null) { ShowInputError("请先选择一个 AI 通道。"); return; }
            if (!CalibrationMath.TryCalculateSensorTwoPoint(
                    (double)_raw1.Value,
                    (double)_reference1.Value,
                    (double)_raw2.Value,
                    (double)_reference2.Value,
                    out var scale,
                    out var offset,
                    out var zero,
                    out var error))
            {
                ShowInputError(error);
                return;
            }

            row.Scale = scale;
            row.Offset = offset;
            row.Zero = zero;
            _sensorGrid.Refresh();
            SetStatus($"{row.ParameterName} 已计算：scale={scale:G8}, offset={offset:G8}, zero={zero:G8}；尚未保存。", false);
        }

        private void ApplyZeroPoint()
        {
            var row = SelectedSensor();
            if (row == null) { ShowInputError("请先选择一个 AI 通道。"); return; }
            row.Zero = (double)_raw1.Value;
            row.Offset = (double)_reference1.Value;
            _sensorGrid.Refresh();
            SetStatus($"{row.ParameterName} 已按点1修正零位；尚未保存。", false);
        }

        private void SaveSensors()
        {
            try
            {
                _sensorGrid.EndEdit();
                CalibrationConfigStore.SaveSensorCalibrations(
                    _aiPath,
                    _sensorRows.Select(x => new SensorCalibrationUpdate
                    {
                        ParameterName = x.ParameterName,
                        Scale = x.Scale,
                        Offset = x.Offset,
                        Zero = x.Zero
                    }));
                SetStatus("AI 标定已保存；原文件备份为 AIConfig.xml.bak。", false);
                MessageBox.Show(this, "AI 标定保存成功，重新打开实时监视界面后生效。", "保存成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowSaveError(ex); }
        }

        private void AddAoObservation()
        {
            var name = _aoDevice.SelectedItem?.ToString();
            if (name == null || !_aoRows.TryGetValue(name, out var rows))
            {
                ShowInputError("请先选择气缸。");
                return;
            }

            var command = (double)_commandPressure.Value;
            var measured = (double)_measuredPressure.Value;
            var device = _aoConfig.Devices[name];
            var currentPoints = rows.Select(x => (x.Voltage, x.MeasuredPressure)).ToArray();
            if (!CalibrationMath.TryMapPressureToVoltage(
                    currentPoints,
                    command,
                    _aoConfig.MinVoltage,
                    _aoConfig.MaxVoltage,
                    out var voltage))
            {
                if (Math.Abs(device.ScaleK) < 1e-12)
                {
                    ShowInputError("当前线性斜率为 0，无法反算 AO 电压。");
                    return;
                }
                voltage = (command - device.Offset) / device.ScaleK;
                voltage = Math.Max(_aoConfig.MinVoltage, Math.Min(_aoConfig.MaxVoltage, voltage));
            }

            rows.Add(new AoRow
            {
                CommandPressure = command,
                MeasuredPressure = measured,
                Voltage = voltage
            });
            SetStatus($"{name} 已添加观测：命令 {command:F1} bar，实测 {measured:F1} bar，AO {voltage:F3} V；尚未保存。", false);
        }

        private void RemoveAoPoint()
        {
            if (_aoGrid.CurrentRow?.DataBoundItem is AoRow row &&
                _aoDevice.SelectedItem is string name && _aoRows.TryGetValue(name, out var rows))
                rows.Remove(row);
        }

        private void ClearAoPoints()
        {
            if (!(_aoDevice.SelectedItem is string name) || !_aoRows.TryGetValue(name, out var rows)) return;
            if (MessageBox.Show(this,
                    "清空后该气缸会回退到 ScaleK/Offset 线性公式。是否继续？",
                    "确认清空",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) == DialogResult.Yes)
                rows.Clear();
        }

        private void SaveAo()
        {
            try
            {
                _aoGrid.EndEdit();
                var selectedDevice = _aoDevice.SelectedItem?.ToString();
                if (selectedDevice == null || !_aoRows.TryGetValue(selectedDevice, out var selectedRows))
                    throw new InvalidOperationException("请先选择需要保存的气缸。");
                var values = new Dictionary<string, IReadOnlyList<AoCalibrationPoint>>(StringComparer.OrdinalIgnoreCase);
                values[selectedDevice] = selectedRows.Select(x => new AoCalibrationPoint
                    {
                        CommandPressure = x.CommandPressure,
                        Pressure = x.MeasuredPressure,
                        Voltage = x.Voltage
                    }).ToArray();

                CalibrationConfigStore.SaveAoCalibrations(
                    _aoPath,
                    values,
                    _aoConfig.MinVoltage,
                    _aoConfig.MaxVoltage);
                SetStatus($"{selectedDevice} 的 AO 多点标定已保存；原文件备份为 AOConfig.xml.bak。", false);
                MessageBox.Show(this, "气缸输出标定保存成功，重新打开实时监视界面后生效。", "保存成功",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowSaveError(ex); }
        }

        private void ShowInputError(string message)
        {
            SetStatus(message, true);
            MessageBox.Show(this, message, "标定数据不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowSaveError(Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, true);
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SetStatus(string message, bool error)
        {
            _status.Text = message;
            _status.ForeColor = error ? Color.Firebrick : Color.FromArgb(15, 118, 110);
        }

        private static DataGridView NewGrid() => new DataGridView
        {
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            RowTemplate = { Height = 34 },
            EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 38,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(248, 250, 252)
            }
        };

        private static DataGridViewColumn TextColumn(
            string header,
            string property,
            int width,
            bool readOnly,
            DataGridViewAutoSizeColumnMode autoSize = DataGridViewAutoSizeColumnMode.None) =>
            new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = property,
                Width = width,
                ReadOnly = readOnly,
                AutoSizeMode = autoSize
            };

        private static DataGridViewColumn NumberColumn(string header, string property, int width) =>
            new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = property,
                Width = width,
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Format = "G9",
                    NullValue = "0"
                }
            };

        private static NumericUpDown NewNumber(decimal min, decimal max, int decimals) => new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            DecimalPlaces = decimals,
            Width = 118,
            ThousandsSeparator = true,
            Margin = new Padding(4, 2, 16, 8)
        };

        private static Label NewCaption(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(15, 23, 42),
            Margin = new Padding(4, 7, 2, 0)
        };

        private static Label NewHint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(16, 7, 10, 0)
        };

        private static Button NewPrimaryButton(string text, int width) => NewButton(
            text, width, Color.FromArgb(3, 105, 161), Color.White);

        private static Button NewSecondaryButton(string text, int width) => NewButton(
            text, width, Color.FromArgb(226, 232, 240), Color.FromArgb(15, 23, 42));

        private static Button NewButton(string text, int width, Color back, Color fore)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                BackColor = back,
                ForeColor = fore,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Margin = new Padding(12, 0, 0, 6)
            };
            button.FlatAppearance.BorderColor = back;
            return button;
        }

        private sealed class SensorRow
        {
            public bool Enabled { get; set; }
            public string EnabledText => Enabled ? "是" : "否";
            public string ParameterName { get; set; }
            public string PhysicalChannel { get; set; }
            public string Type { get; set; }
            public string Unit { get; set; }
            public double Scale { get; set; }
            public double Offset { get; set; }
            public double Zero { get; set; }
        }

        private sealed class AoRow
        {
            public double CommandPressure { get; set; }
            public double MeasuredPressure { get; set; }
            public double Voltage { get; set; }
            public string Description =>
                $"命令 {CommandPressure.ToString("F1", CultureInfo.InvariantCulture)} bar 时实测 " +
                $"{MeasuredPressure.ToString("F1", CultureInfo.InvariantCulture)} bar";
        }
    }
}
