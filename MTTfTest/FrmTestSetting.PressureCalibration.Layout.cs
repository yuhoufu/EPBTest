using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
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
            TxtPressureCalibrationCommand.Maximum = MTTFTest.Watchdog.Protocol.PressureMaintenanceProtocol.MaximumPressureBar;
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
