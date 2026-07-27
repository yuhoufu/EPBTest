using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller.Alarm;

namespace MTEmbTest
{
    public sealed class FrmAlarmPanelTest : Form
    {
        private readonly AlarmManager _alarm;

        private readonly Button[] _epbButtons = new Button[12];
        private readonly bool[] _epbOn = new bool[12];

        private Button _btnBuzzer;
        private bool _buzzerOn;

        private Button _btnClearAll;

        public FrmAlarmPanelTest(AlarmManager alarm)
        {
            _alarm = alarm;

            Text = @"报警面板输出测试";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 520);

            BuildUi();
        }

        private void BuildUi()
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(12),
            };

            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            for (var i = 0; i < table.RowCount; i++)
                table.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / table.RowCount));

            // 12 个 EPB 指示灯：按 2 列排布
            for (var i = 0; i < 12; i++)
            {
                var epbId = i + 1;
                var btn = new Button
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    Text = $@"EPB{epbId} 指示灯：关",
                    Tag = epbId,
                };

                btn.Click += async (_, __) => await ToggleEpbAsync(epbId);

                _epbButtons[i] = btn;
                table.Controls.Add(btn, i % 2, i / 2);
            }

            // 蜂鸣器
            _btnBuzzer = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(6),
                Text = @"蜂鸣器：关",
            };
            _btnBuzzer.Click += async (_, __) => await ToggleBuzzerAsync();
            table.Controls.Add(_btnBuzzer, 0, 6);

            // 全关
            _btnClearAll = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(6),
                Text = @"取消全部报警（全关）",
            };
            _btnClearAll.Click += async (_, __) => await ClearAllAsync();
            table.Controls.Add(_btnClearAll, 1, 6);

            Controls.Add(table);
        }

        private async Task ToggleEpbAsync(int epbId)
        {
            if (_alarm == null)
            {
                MessageBox.Show(@"报警子系统未初始化（AlarmManager=null）。", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var idx = epbId - 1;
            if (idx < 0 || idx >= _epbOn.Length) return;

            var target = !_epbOn[idx];
            try
            {
                await _alarm.SetIndicatorOutputAsync(epbId, target);
                _epbOn[idx] = target;
                UpdateEpbButtonText(epbId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($@"EPB{epbId} 指示灯控制失败：{ex.Message}", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task ToggleBuzzerAsync()
        {
            if (_alarm == null)
            {
                MessageBox.Show(@"报警子系统未初始化（AlarmManager=null）。", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var target = !_buzzerOn;
            try
            {
                await _alarm.SetBuzzerOutputAsync(target);
                _buzzerOn = target;
                UpdateBuzzerButtonText();
            }
            catch (Exception ex)
            {
                MessageBox.Show($@"蜂鸣器控制失败：{ex.Message}", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task ClearAllAsync()
        {
            if (_alarm == null)
            {
                MessageBox.Show(@"报警子系统未初始化（AlarmManager=null）。", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                await _alarm.ClearAllAsync();

                for (var i = 0; i < _epbOn.Length; i++)
                    _epbOn[i] = false;
                _buzzerOn = false;

                for (var i = 1; i <= 12; i++)
                    UpdateEpbButtonText(i);
                UpdateBuzzerButtonText();
            }
            catch (Exception ex)
            {
                MessageBox.Show($@"取消全部报警失败：{ex.Message}", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateEpbButtonText(int epbId)
        {
            var idx = epbId - 1;
            if (idx < 0 || idx >= _epbButtons.Length) return;
            var btn = _epbButtons[idx];
            if (btn == null) return;

            btn.Text = $@"EPB{epbId} 指示灯：{(_epbOn[idx] ? "开" : "关")}";
        }

        private void UpdateBuzzerButtonText()
        {
            if (_btnBuzzer == null) return;
            _btnBuzzer.Text = $@"蜂鸣器：{(_buzzerOn ? "开" : "关")}";
        }
    }
}
