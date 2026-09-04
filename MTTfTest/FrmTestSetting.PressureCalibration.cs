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
        private PressureCalibrationCoordinator _pressureCalibrationCoordinator;
        private bool _updatingMeasuredPressure;
        private bool _measuredPressureEdited;
        private bool _bindingPressureCalibrationGrid;
        private double _latestPressureBar = double.NaN;


        private void LoadPressureCalibrationConfiguration()
        {
            try
            {
                _pressureCalibrationRows.Clear();
                CmbPressureCalibrationCylinder.Items.Clear();
                var invalidPoints = 0;
                var auditPoints = LoadPressureCalibrationAuditPoints(
                    RuntimeConfigPaths.GetPath("AOConfig.xml"));
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
                var hardware = new PressureCalibrationHardware(
                    _cfg, logger, _daqRuntimeSettings);
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
                var path = RuntimeConfigPaths.GetPath("AOConfig.xml");
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

    }
}
