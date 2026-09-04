using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class FrmTestSetting
    {
        private readonly Func<string, bool> _confirmPressureAction;
        private PressureMaintenanceUiSession _pressureMaintenanceUi;
        private EngineAoConfiguration _aoBaseline;
        private long _aoRevision, _aoEpoch;
        private string _aoSha, _aoRunId, _aoSessionId;
        private bool _aoSubmitted, _aoAdmissionInFlight, _loadingPressure, _updatingMeasuredPressure, _measuredPressureEdited, _bindingPressureCalibrationGrid;
        private double _latestPressureBar = double.NaN;
        private AoCalibrationCommit _submittedAo;
        private Task _pressureActionTask = Task.CompletedTask;
        private long _pressureStatusUntilUtc;
        private bool _pressureTickInFlight;
        internal Task PendingPressureAction => _pressureActionTask;

        private void LoadPressureDraft(bool force = false)
        {
            if (ViewClosed || CmbPressureCalibrationCylinder == null || (!_session.IsConnected) ||
                _session.Latest.AoConfiguration?.IsStructurallyValid() != true || (!force && _aoBaseline != null)) return;
            if (force && (_pressureMaintenanceUi.Requested || _aoAdmissionInFlight || !_session.CanClose)) return;
            var snapshot = _session.Latest;
            _aoBaseline = snapshot.AoConfiguration.Clone(); _aoRevision = snapshot.AoConfigurationRevision; _aoSha = snapshot.AoConfigurationSha256;
            _aoSessionId = snapshot.Engine.SessionId; _aoRunId = snapshot.Engine.RunId; _aoEpoch = snapshot.Engine.RunEpoch;
            _aoSubmitted = false; _submittedAo = null; _loadingPressure = true;
            try
            {
                var selected = CmbPressureCalibrationCylinder.SelectedItem?.ToString();
                CmbPressureCalibrationCylinder.Items.Clear(); _pressureCalibrationRows.Clear();
                foreach (var device in _aoBaseline.Devices.OrderBy(d => d.Name, StringComparer.Ordinal))
                { CmbPressureCalibrationCylinder.Items.Add(device.Name); LoadDeviceRows(device); }
                CmbPressureCalibrationCylinder.SelectedIndex = selected == "Cylinder2" ? 1 : 0;
                TxtPressureCalibrationCommand.Maximum = Math.Min(PressureMaintenanceProtocol.MaximumPressureBar, _aoBaseline.MaxPressure);
            }
            finally { _loadingPressure = false; }
            BindPressureCalibrationGrid();
        }

        private void LoadDeviceRows(EngineAoDeviceConfiguration device) => _pressureCalibrationRows[device.Name] =
            new BindingList<PressureCalibrationRow>(device.Points.OrderBy(p => p.Pressure).Select((p, index) => new PressureCalibrationRow
            { Sequence = index + 1, CommandPressure = p.CommandPressure ?? p.Pressure, MeasuredPressure = p.Pressure, Voltage = p.Voltage, IsHistorical = true }).ToList());
        private bool SameAoRun => _session.Latest?.Engine.SessionId == _aoSessionId && _session.Latest.Engine.RunId == _aoRunId && _session.Latest.Engine.RunEpoch == _aoEpoch;
        private bool AoDraftCurrent => _aoBaseline != null && SameAoRun && _session.Latest.AoConfigurationRevision == _aoRevision &&
            _session.Latest.AoConfigurationSha256 == _aoSha;
        private bool PressureBusy => _aoAdmissionInFlight || !_pressureActionTask.IsCompleted || _aoSubmitted;
        private bool PressureCompatible => _session.HasCapability(EngineUiContract.PressureMaintenance) && _session.HasCapability(EngineUiContract.AoCalibration);
        private int SelectedHydraulic => CmbPressureCalibrationCylinder.SelectedItem?.ToString() == "Cylinder2" ? 2 : 1;
        private BindingList<PressureCalibrationRow> SelectedPressureRows => _pressureCalibrationRows.TryGetValue(
            CmbPressureCalibrationCylinder.SelectedItem?.ToString() ?? string.Empty, out var rows) ? rows : null;
        private static bool UsablePressure(EngineUiPressureMaintenance display) => display?.Pressure?.IsUsable(DateTime.UtcNow.Ticks) == true &&
            display.CapturedUtcTicks >= display.Pressure.CapturedUtcTicks &&
            display.CapturedUtcTicks - display.Pressure.CapturedUtcTicks <= TimeSpan.FromMilliseconds(display.PressureSampleMaximumAgeMilliseconds).Ticks;

        private void RunPressureAction(Func<Task> action, bool safety = false)
        {
            if (ViewClosed || (!safety && !_pressureActionTask.IsCompleted)) return;
            var task = ExecutePressureActionAsync(action);
            _pressureActionTask = task;
            _ = ObserveSelectionAsync(task);
        }
        private async Task ExecutePressureActionAsync(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { if (!ViewClosed) SetPressureCalibrationStatus("压力校正操作失败：" + ex.GetBaseException().Message, true); }
            finally
            {
                if (!ViewClosed)
                {
                    // Schedule after the tracked task has completed, so availability
                    // cannot become stuck behind its own in-flight flag.
                    try { BeginInvoke((Action)UpdatePressureCalibrationAvailability); } catch (InvalidOperationException) when (ViewClosed || !IsHandleCreated) { }
                }
            }
        }

        private void PressureCalibrationStartClick(object sender, EventArgs e) => RunPressureAction(async () =>
        {
            if (!_session.CanBeginMaintenance || !AoDraftCurrent || !_confirmPressureAction(
                "开始校正将由后台先执行独立断能检查，再取得限时维护权限。请确认试验已停止、管路连接可靠且人员远离运动部件。")) return;
            SetPressureCalibrationStatus("正在申请维护权限及安全检查，不会自动输出压力……", false);
            await _pressureMaintenanceUi.BeginAsync(SelectedHydraulic);
        });
        private void PressureCalibrationOutputClick(object sender, EventArgs e) => RunPressureAction(async () =>
        {
            if (!_pressureMaintenanceUi.Ready || !AoDraftCurrent || !PressureCompatible) return;
            if (!TryReadCalibrationNumber(TxtPressureCalibrationCommand.Text, out var command) || !CalibrationMath.IsFinite(command) ||
                command < 0 || command > Math.Min(PressureMaintenanceProtocol.MaximumPressureBar, _aoBaseline.MaxPressure))
            { ShowPressureCalibrationWarning("命令压力必须是允许范围内的有效数字。"); return; }
            _measuredPressureEdited = false;
            SetPressureCalibrationStatus("正在提交输出，等待后台实际执行结果……", false);
            await _pressureMaintenanceUi.OutputAsync(command);
        });
        private void PressureCalibrationStopClick(object sender, EventArgs e) => RunPressureAction(async () =>
        { SetPressureCalibrationStatus("正在停止输出；维护权限仍保留，等待后台确认……", false); await _pressureMaintenanceUi.StopOutputAsync(); }, true);

        private void PressureCalibrationRecordClick(object sender, EventArgs e) => RunPressureAction(async () =>
        {
            await _session.RefreshAsync(); _pressureMaintenanceUi.ObserveUi();
            var display = _pressureMaintenanceUi.Display;
            if (!PressureCompatible || !AoDraftCurrent || display?.OutputActive != true || display.HydraulicId != SelectedHydraulic || !UsablePressure(display))
            { ShowPressureCalibrationWarning("输出或实时压力尚未有效，不能记录校正点。"); return; }
            if (!_measuredPressureEdited) SetMeasuredPressureText(display.Pressure.Value);
            if (!TryReadCalibrationNumber(TxtPressureCalibrationMeasured.Text, out var measured) || !CalibrationMath.IsFinite(measured) || measured < 0 || measured > _aoBaseline.MaxPressure)
            { ShowPressureCalibrationWarning("实测压力必须是配置范围内的有效数字。"); return; }
            var rows = SelectedPressureRows; if (rows == null) return;
            var index = CalibrationMath.FindMatchingCommandIndex(rows.Select(r => r.CommandPressure), display.CommandPressureBar, PressureCalibrationCommandMatchToleranceBar);
            var row = index >= 0 ? rows[index] : new PressureCalibrationRow();
            if (index < 0) { if (rows.Count >= 256) { ShowPressureCalibrationWarning("校正点已达 256 点上限。"); return; } rows.Add(row); }
            row.CommandPressure = display.CommandPressureBar; row.Voltage = display.Voltage; row.MeasuredPressure = measured; row.IsChanged = true;
            SortPressureCalibrationRows(row);
            SetPressureCalibrationStatus("已记录第 " + row.Sequence + " 行，尚未保存；正式试验次数不变。", false);
        });
        private void PressureCalibrationDeleteClick(object sender, EventArgs e)
        {
            if (PressureBusy || !AoDraftCurrent || _pressureMaintenanceUi.MayBeEnergized ||
                !(DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow row)) return;
            if (!_confirmPressureAction("删除第 " + row.Sequence + " 行校正点？只修改本页草稿，保存后才写入配置。")) return;
            SelectedPressureRows.Remove(row); RenumberPressureCalibrationRows(SelectedPressureRows); BindPressureCalibrationGrid();
            SetPressureCalibrationStatus("已删除选中点，尚未保存。", false);
        }
        private void PressureCalibrationClearClick(object sender, EventArgs e)
        {
            if (PressureBusy || !AoDraftCurrent || _pressureMaintenanceUi.MayBeEnergized || SelectedPressureRows == null ||
                !_confirmPressureAction("清空当前气缸页面的全部校正点？至少两个有效点才能保存，后台配置和历史次数不会被清零。")) return;
            SelectedPressureRows.Clear(); BindPressureCalibrationGrid(); SetPressureCalibrationStatus("页面校正点已清空，尚未保存。", false);
        }
        private void PressureCalibrationSaveClick(object sender, EventArgs e) => RunPressureAction(SavePressureAsync);
        private async Task SavePressureAsync()
        {
            if (!PressureCompatible || !AoDraftCurrent || SelectedPressureRows == null || _aoSubmitted || _aoAdmissionInFlight) return;
            var rows = SelectedPressureRows.OrderBy(r => r.Voltage).ToArray();
            var payload = new AoCalibrationCommit { DeviceName = CmbPressureCalibrationCylinder.SelectedItem.ToString(), Points = rows.Select(r =>
                new EngineAoCalibrationPoint { Voltage = r.Voltage, Pressure = r.MeasuredPressure, CommandPressure = r.CommandPressure }).ToArray() };
            if (!payload.IsStructurallyValid() || rows.Any(r => r.Voltage < _aoBaseline.MinVoltage || r.Voltage > _aoBaseline.MaxVoltage ||
                r.CommandPressure > Math.Min(PressureMaintenanceProtocol.MaximumPressureBar, _aoBaseline.MaxPressure)) ||
                !TryFitPressureCalibrationRows(rows, out var scale, out var offset, out var rSquared, out _))
            { ShowPressureCalibrationWarning("至少需要两个有效校正点；实测压力和电压须严格递增且在允许范围内。"); return; }
            var mix = CalibrationMath.AnalyzeMixedCalibration(rows.Select(r => (r.Voltage, r.MeasuredPressure, r.IsChanged, r.IsHistorical)));
            var message = "保存 " + payload.DeviceName + " 校正？P = V × " + scale.ToString("G7") + " + " + offset.ToString("G7") +
                "，R²=" + rSquared.ToString("F5") + "。\n后台会先结束维护并确认安全，封存旧配置后提交；不会自动开始试验。";
            if (mix.HasMixedData) message += "\n新旧点混合：更新 " + mix.ChangedPointCount + " 点，未更新历史 " + mix.UnchangedHistoricalPointCount +
                " 点；影响风险" + (mix.HasMaterialImpact ? "较高" : "可见") + "。建议先更新全部计划压力点或删除失效旧点。";
            if (!_confirmPressureAction(message)) return;
            _aoAdmissionInFlight = true;
            try
            {
                SetPressureCalibrationStatus("等待维护安全收尾及旧配置封存……", false);
                if (_pressureMaintenanceUi.Requested && !await _pressureMaintenanceUi.EndAsync()) return;
                if (ViewClosed || !AoDraftCurrent || !_session.CanCalibrateAo) return;
                var settings = new TestConfigurationCommit { BaseConfigurationRevision = _aoRevision, BaseConfigurationSha256 = _aoSha, AoCalibration = payload };
                _submittedAo = payload.Clone();
                var response = await _session.SubmitConfigurationAsync(settings);
                if (ViewClosed) return;
                _aoSubmitted = response?.Accepted == true || _session.HasUnresolvedCommands;
                SetPressureCalibrationStatus(_aoSubmitted ? "保存请求已提交，正在查询原命令的最终结果；草稿保留。" : _session.OperationMessage, response?.Accepted != true);
            }
            finally { _aoAdmissionInFlight = false; }
        }

        private async void PressureCalibrationTimerTick(object sender, EventArgs e)
        {
            if (ViewClosed || _pressureTickInFlight) return;
            _pressureTickInFlight = true; _pressureCalibrationTimer.Stop();
            try
            {
                if (TabSetting.SelectedTab == tabPagePressureCalibration) _pressureMaintenanceUi.ObserveUi();
                if (_pressureMaintenanceUi.Requested && _pressureMaintenanceUi.Failure.Length != 0 && !_pressureMaintenanceUi.Ending)
                    RunPressureAction(async () => { await _pressureMaintenanceUi.EndAsync(); }, true);
                UpdatePressureCalibrationAvailability();
                if (_pressureMaintenanceUi.Requested) await _session.RefreshAsync();
                // Measure the next refresh interval after UI binding completes.
                // Slow rendering must leave room for input rather than a timer backlog.
                await Task.Delay(100);
            }
            catch (Exception ex) { if (!ViewClosed) SetPressureCalibrationStatus("维护界面刷新失败：" + ex.GetBaseException().Message, true); }
            finally
            {
                _pressureTickInFlight = false;
                if (!ViewClosed) _pressureCalibrationTimer.Start();
            }
        }
        private void PressureCalibrationTabChanged(object sender, EventArgs e)
        {
            if (TabSetting.SelectedTab != tabPagePressureCalibration && _pressureMaintenanceUi.Requested && !_pressureMaintenanceUi.Ending)
                RunPressureAction(async () => { await _pressureMaintenanceUi.EndAsync(); }, true);
        }
        private void FrmTestSettingPressureCalibrationFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_pressureMaintenanceUi.Requested || _session.CanClose) return;
            e.Cancel = true;
            RunPressureAction(async () =>
            {
                if (await _pressureMaintenanceUi.EndAsync() && !ViewClosed) Close();
                else if (!ViewClosed) SetPressureCalibrationStatus("安全收尾未完成，窗口保持打开。" + _pressureMaintenanceUi.Failure, true);
            }, true);
        }
        private void PressureCalibrationCylinderChanged(object sender, EventArgs e)
        {
            if (_loadingPressure || ViewClosed) return;
            _measuredPressureEdited = false; SetMeasuredPressureText(double.NaN);
            if (_pressureMaintenanceUi.Requested) RunPressureAction(async () => { await _pressureMaintenanceUi.EndAsync(); BindPressureCalibrationGrid(); }, true);
            else BindPressureCalibrationGrid();
        }
        private void PressureCalibrationUseLiveClick(object sender, EventArgs e)
        {
            var display = _pressureMaintenanceUi.Display;
            if (!UsablePressure(display)) { ShowPressureCalibrationWarning("当前没有有效实时压力。"); return; }
            _measuredPressureEdited = false; SetMeasuredPressureText(display.Pressure.Value);
        }
        private void PressureCalibrationMeasuredTextChanged(object sender, EventArgs e) { if (!_updatingMeasuredPressure) _measuredPressureEdited = true; }

        private void UpdatePressureCalibrationAvailability()
        {
            if (ViewClosed || BtnPressureCalibrationStart == null) return;
            if (_aoBaseline == null) LoadPressureDraft();
            if (_aoSubmitted && !_aoAdmissionInFlight && !_session.HasUnresolvedCommands && !_session.CommandPending && _session.CanClose)
            {
                var next = _session.Latest; var applied = SameAoRun && next.AoConfigurationRevision == _aoRevision + 1 && _submittedAo != null &&
                    next.AoConfiguration?.Devices.Any(d => d.Name == _submittedAo.DeviceName && new AoCalibrationCommit { DeviceName = d.Name, Points = d.Points }.ComputeSha256() == _submittedAo.ComputeSha256()) == true;
                _aoSubmitted = false;
                if (applied)
                {
                    _aoBaseline = next.AoConfiguration.Clone(); _aoRevision = next.AoConfigurationRevision; _aoSha = next.AoConfigurationSha256;
                    LoadDeviceRows(_aoBaseline.Devices.Single(d => d.Name == _submittedAo.DeviceName)); _submittedAo = null;
                    BindPressureCalibrationGrid(); SetPressureCalibrationStatus("校正已保存，旧配置已封存；试验仍停止。", false);
                }
                else SetPressureCalibrationStatus("保存结果未匹配，草稿保持：" + _session.OperationMessage, true);
            }
            var display = _pressureMaintenanceUi.Display;
            var active = display?.OutputActive == true && display.HydraulicId == SelectedHydraulic;
            _latestPressureBar = UsablePressure(display) ? display.Pressure.Value : double.NaN;
            LblPressureCalibrationLive.Text = CalibrationMath.IsFinite(_latestPressureBar) ? "实时压力：" + _latestPressureBar.ToString("F2") + " bar" : "实时压力：-- bar";
            LblPressureCalibrationVoltage.Text = display != null && !display.MayBeEnergized ? "AO：0.0000 V（命令）" : active ? "AO：" + display.Voltage.ToString("F4") + " V（命令）" : "AO：-- V";
            LblPressureCalibrationSample.Text = CalibrationMath.IsFinite(_latestPressureBar) ? "采样：" + TimeSpan.FromTicks(DateTime.UtcNow.Ticks - display.Pressure.CapturedUtcTicks).TotalMilliseconds.ToString("F0") + " ms" : "采样：未就绪／已失联";
            if (CalibrationMath.IsFinite(_latestPressureBar) && !_measuredPressureEdited) SetMeasuredPressureText(_latestPressureBar);
            SetPressureCalibrationControls(_pressureMaintenanceUi.Requested, active);
            if (!_session.HasCapability(EngineUiContract.PressureMaintenance) || !_session.HasCapability(EngineUiContract.AoCalibration))
                SetPressureCalibrationStatus("后台未提供完整压力标定能力，请核对组件版本；页面只读。", true);
            else if (!AoDraftCurrent) SetPressureCalibrationStatus("AO 配置或运行身份已变化，旧草稿保留；停止后右键顶部提示重新读取。", true);
            else if (_pressureMaintenanceUi.Failure.Length != 0) SetPressureCalibrationStatus(_pressureMaintenanceUi.Failure, true);
            else if (_pressureMaintenanceUi.Ready && !PressureBusy && DateTime.UtcNow.Ticks >= _pressureStatusUntilUtc)
                SetPressureCalibrationStatus(active ? "正在输出；请观察压力稳定后记录。" : "维护已就绪，输出已关闭；可继续输出或保存校正。", false);
        }
        private void SetPressureCalibrationControls(bool sessionStarted, bool outputActive)
        {
            var enabled = PressureCompatible && AoDraftCurrent && !PressureBusy;
            BtnPressureCalibrationStart.Enabled = enabled && !sessionStarted && _session.CanBeginMaintenance;
            BtnPressureCalibrationOutput.Enabled = enabled && _pressureMaintenanceUi.Ready && !outputActive && UsablePressure(_pressureMaintenanceUi.Display);
            BtnPressureCalibrationStop.Enabled = sessionStarted && !_pressureMaintenanceUi.Ending && _pressureMaintenanceUi.Lease != null;
            BtnPressureCalibrationRecord.Enabled = enabled && outputActive && UsablePressure(_pressureMaintenanceUi.Display);
            BtnPressureCalibrationUseLive.Enabled = BtnPressureCalibrationRecord.Enabled;
            CmbPressureCalibrationCylinder.Enabled = enabled && !_pressureMaintenanceUi.MayBeEnergized;
            TxtPressureCalibrationCommand.Enabled = enabled && !outputActive && !_pressureMaintenanceUi.Ending;
            TxtPressureCalibrationMeasured.Enabled = enabled && outputActive;
            DgvPressureCalibration.Enabled = enabled && !_pressureMaintenanceUi.MayBeEnergized;
            UpdatePressureCalibrationActionStates();
        }
        private void UpdatePressureCalibrationActionStates()
        {
            if (BtnPressureCalibrationDelete == null) return;
            var editable = PressureCompatible && AoDraftCurrent && !PressureBusy && !_pressureMaintenanceUi.MayBeEnergized &&
                (_session.CanCalibrateAo || _pressureMaintenanceUi.Ready);
            BtnPressureCalibrationDelete.Enabled = editable && DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow;
            BtnPressureCalibrationClear.Enabled = editable && SelectedPressureRows?.Count > 0;
            BtnPressureCalibrationSave.Enabled = editable && SelectedPressureRows?.Count >= 2;
        }
        private void BindPressureCalibrationGrid(PressureCalibrationRow selected = null)
        {
            if (ViewClosed) return;
            _bindingPressureCalibrationGrid = true;
            try
            {
                DgvPressureCalibration.DataSource = SelectedPressureRows;
                DgvPressureCalibration.ClearSelection(); DgvPressureCalibration.CurrentCell = null;
                foreach (DataGridViewRow item in DgvPressureCalibration.Rows)
                {
                    var row = item.DataBoundItem as PressureCalibrationRow;
                    item.DefaultCellStyle.BackColor = row?.IsChanged == true ? PressureCalibrationChangedColor : item.Index % 2 == 0 ? Color.White : Color.FromArgb(243, 249, 255);
                    item.Cells[0].ToolTipText = row?.IsChanged == true ? "本次新增或更新，尚未保存" : string.Empty;
                    if (ReferenceEquals(row, selected) && row != null) { item.Selected = true; DgvPressureCalibration.CurrentCell = item.Cells[0]; }
                }
            }
            finally { _bindingPressureCalibrationGrid = false; }
            var device = _aoBaseline?.Devices.SingleOrDefault(d => d.Name == CmbPressureCalibrationCylinder.SelectedItem?.ToString());
            LblPressureCalibrationFormula.Text = device == null ? "配置未就绪" : "当前公式：P = V × " + device.ScaleK.ToString("G7") + " + " + device.Offset.ToString("G7");
            if (TryFitPressureCalibrationRows(SelectedPressureRows, out var scale, out var offset, out var squared, out _))
                LblPressureCalibrationFormula.Text += "    拟合预览：P = V × " + scale.ToString("G7") + " + " + offset.ToString("G7") + "（R²=" + squared.ToString("F5") + "）";
            UpdatePressureCalibrationActionStates();
        }
        private void PressureCalibrationGridSelectionChanged(object sender, EventArgs e)
        {
            if (_bindingPressureCalibrationGrid || _pressureMaintenanceUi.MayBeEnergized) return;
            if (DgvPressureCalibration.CurrentRow?.DataBoundItem is PressureCalibrationRow row) TxtPressureCalibrationCommand.Text = row.CommandPressure.ToString("F2");
            UpdatePressureCalibrationActionStates();
        }
        private void SortPressureCalibrationRows(PressureCalibrationRow selected)
        {
            var rows = new BindingList<PressureCalibrationRow>(SelectedPressureRows.OrderBy(r => r.CommandPressure).ThenBy(r => r.MeasuredPressure).ToList());
            RenumberPressureCalibrationRows(rows); _pressureCalibrationRows[CmbPressureCalibrationCylinder.SelectedItem.ToString()] = rows; BindPressureCalibrationGrid(selected);
        }
        private static void RenumberPressureCalibrationRows(BindingList<PressureCalibrationRow> rows) { for (var i = 0; i < rows.Count; i++) rows[i].Sequence = i + 1; rows.ResetBindings(); }
        private static bool TryFitPressureCalibrationRows(IEnumerable<PressureCalibrationRow> rows, out double scale, out double offset, out double squared, out string error) =>
            CalibrationMath.TryFitPressureLine(rows?.Select(r => (r.Voltage, r.MeasuredPressure)), out scale, out offset, out squared, out error);
        private void SetMeasuredPressureText(double value)
        { _updatingMeasuredPressure = true; try { TxtPressureCalibrationMeasured.Text = CalibrationMath.IsFinite(value) ? value.ToString("F2") : string.Empty; } finally { _updatingMeasuredPressure = false; } }
        private static bool TryReadCalibrationNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        private void SetPressureCalibrationStatus(string text, bool error)
        {
            if (ViewClosed || LblPressureCalibrationStatus == null) return;
            LblPressureCalibrationStatus.Text = text;
            LblPressureCalibrationStatus.ForeColor = error ? Color.Firebrick : Color.FromArgb(48, 48, 48);
            _pressureStatusUntilUtc = DateTime.UtcNow.AddSeconds(5).Ticks;
        }
        private void ShowPressureCalibrationWarning(string message) => SetPressureCalibrationStatus(message, true);
    }
}
