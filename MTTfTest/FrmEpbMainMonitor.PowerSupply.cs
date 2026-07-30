using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _powerSupplyUiAttached;
        private ToolTip _powerSupplyToolTip;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Interlocked.Exchange(ref _powerSupplyUiAttached, 1) != 0) return;
            if (_epb != null)
            {
                _epb.PowerSupplyTelemetryUpdated += UpdatePowerSupplyStatus;
                _epb.PowerSupplyFaultRaised += ShowPowerSupplyFault;
            }
            _powerSupplyToolTip = new ToolTip();
            var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
            for (var index = 0; index < boxes.Length; index++)
            {
                var groupId = index + 1;
                _powerSupplyToolTip.SetToolTip(
                    boxes[index],
                    $"电源组{groupId}：双击可在输出关闭、保护解除后人工复位故障锁存");
                boxes[index].DoubleClick += async (sender, args) =>
                    await ResetPowerSupplyFaultFromUiAsync(groupId);
            }
            FormClosing += ClosePowerSuppliesBeforeExit;
        }

        private async System.Threading.Tasks.Task ResetPowerSupplyFaultFromUiAsync(int groupId)
        {
            if (_epb == null) return;
            if (MessageBox.Show(
                    $"确认人工复位电源组 {groupId} 的故障锁存？\r\n" +
                    "程序会重新核对输出已关闭、保护已解除和设备身份；本操作不会开启输出。",
                    "复位程控电源故障",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try
            {
                await _epb.ResetPowerSupplyFaultAsync(groupId);
                LogInfo($"电源组{groupId}故障锁存已人工复位；下次启动仍执行完整预检。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"电源组{groupId}故障复位失败：\r\n{ex.Message}",
                    "复位失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void UpdatePowerSupplyStatus(PowerSupplyTelemetry telemetry)
        {
            if (telemetry == null || IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
                    if (telemetry.SupplyId < 1 || telemetry.SupplyId > boxes.Length) return;
                    var box = boxes[telemetry.SupplyId - 1];
                    var snapshot = telemetry.Snapshot;
                    if (snapshot == null)
                    {
                        box.Text = $"电源{telemetry.SupplyId} 通信异常";
                        box.ForeColor = Color.Red;
                        return;
                    }
                    var mode = snapshot.IsConstantCurrent ? "CC" :
                        snapshot.IsConstantVoltage ? "CV" : "--";
                    box.Text = $"电源{telemetry.SupplyId} {(snapshot.OutputEnabled ? "ON" : "OFF")} " +
                               $"{mode} {snapshot.MeasuredVoltage:F1}V/{snapshot.MeasuredCurrent:F1}A";
                    box.ForeColor = snapshot.ProtectionTripped ||
                                    snapshot.IsCurrentLimited ||
                                    snapshot.IsPowerLimited
                        ? Color.Red
                        : !string.IsNullOrWhiteSpace(telemetry.Error)
                            ? Color.DarkOrange
                            : snapshot.OutputEnabled ? Color.DarkGreen : Color.DimGray;
                }));
            }
            catch
            {
                // 窗口退出期间忽略晚到的遥测。
            }
        }

        private void ShowPowerSupplyFault(PowerSupplyFault fault)
        {
            if (fault == null || IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
                    if (fault.SupplyId >= 1 && fault.SupplyId <= boxes.Length)
                    {
                        boxes[fault.SupplyId - 1].Text =
                            $"电源{fault.SupplyId} 故障 [{fault.Code}]";
                        boxes[fault.SupplyId - 1].ForeColor = Color.Red;
                    }
                    LogInfo(
                        $"电源组{fault.ElectricalGroupId}硬故障[{fault.Code}]：{fault.Reason}；" +
                        $"联动EPB={string.Join(",", fault.AffectedChannels ?? Array.Empty<int>())}");
                }));
            }
            catch { }
        }

        private void ClosePowerSuppliesBeforeExit(object sender, FormClosingEventArgs e)
        {
            if (_epb == null) return;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    _epb.StopAllAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "未能确认全部程控电源已经关闭，请立即检查电源面板和急停回路。\r\n" + ex.Message,
                    "电源关闭未确认",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
