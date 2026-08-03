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
                _epb.ChannelWarningEvidenceRaised += warning => PostSafetyStatus(
                    $"软预警 EPB{warning.Channel:D2} [{warning.Code}] " +
                    $"连续={warning.Streak}/{warning.ConfirmThreshold}；当前完整圈封存后后台保存证据。",
                    false);
                _epb.SnapshotExportFailed += message => PostSafetyStatus(
                    "快照导出失败（不影响安全控制）：" + message,
                    true);
                _epb.WarningSnapshotStorageChanged += status => PostSafetyStatus(
                    $"WarningSnapshots：占用={status.UsedBytes / 1024d / 1024d:F1}MB，" +
                    $"剩余={status.FreeBytes / 1024d / 1024d:F0}MB，" +
                    $"估算可保存={status.EstimatedAdditionalCycles}圈。",
                    status.IsBelowFreeSpaceWarning);
                _epb.PressureQualificationChanged += qualification => PostSafetyStatus(
                    $"液压{qualification.HydraulicId}资格通过：目标={qualification.TargetBar:F2}bar，" +
                    $"实际={qualification.ActualBar:F2}bar，稳定={qualification.StableMs}ms。",
                    false);
                _epb.DaqRecoveryStateChanged += result => PostSafetyStatus(
                    $"DAQ恢复 {result.Device}：{(result.Recovered ? "成功" : "失败")}，" +
                    $"新鲜样本={result.FreshCallbacks}/{result.RequiredFreshCallbacks}，{result.ElapsedMs}ms。",
                    !result.Recovered);
                _epb.ControlFaultRaised += fault =>
                {
                    var hint = fault.Scope == FaultScope.HydraulicGroup
                        ? "请优先检查卡钳开裂、接头、管路、制动液液位及泄漏。"
                        : fault.Scope == FaultScope.Channel &&
                          (fault.Reason.IndexOf("Stall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           fault.Reason.IndexOf("near", StringComparison.OrdinalIgnoreCase) >= 0)
                            ? "若液压资格与电源回读均正常且该通道近零电流，请检查面板按钮、继电器、接插件和线束。"
                            : string.Empty;
                    PostSafetyStatus(
                        $"控制故障 [{fault.Code}] Scope={fault.Scope} EPB=" +
                        $"{string.Join(",", fault.AffectedChannels ?? Array.Empty<int>())}。{hint}",
                        true);
                };
                var initialStorage = _epb.GetWarningSnapshotStorageStatus();
                PostSafetyStatus(
                    $"WarningSnapshots：占用={initialStorage.UsedBytes / 1024d / 1024d:F1}MB，" +
                    $"剩余={initialStorage.FreeBytes / 1024d / 1024d:F0}MB，" +
                    $"估算可保存={initialStorage.EstimatedAdditionalCycles}圈。",
                    initialStorage.IsBelowFreeSpaceWarning);
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

        private void PostSafetyStatus(string message, bool important)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                BeginInvoke((Action)(() =>
                    LogInfo((important ? "[安全] " : string.Empty) + message)));
            }
            catch { }
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
                    _epb.StopAllAsync(
                            new StopContext
                            {
                                Source = StopSource.ApplicationClosing,
                                Reason = "主窗体关闭",
                                Initiator = nameof(ClosePowerSuppliesBeforeExit),
                                CorrelationId = Guid.NewGuid().ToString("N")
                            },
                            cts.Token)
                        .GetAwaiter().GetResult();
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
