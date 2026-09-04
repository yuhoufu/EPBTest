using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class FrmTestSetting
    {
        private OriginalSettingsDraft _daqEdit;
        private bool _daqSubmitted;
        private bool _daqAdmissionInFlight;
        private long _daqSubmittedKernelRevision;
        private Task _daqSaveTask = Task.CompletedTask;
        internal Task PendingDaqSave => _daqSaveTask;

        private void InitializeDaqGrid()
        {
            var grid = dgvDaqAI;
            grid.AutoGenerateColumns = false; grid.Columns.Clear();
            var properties = new[] { "Sequence", "PhysicalChannel", "ParameterName", "Unit", "Slope", "Intercept", "ParameterType", "Enabled", "ZeroOffset" };
            var headers = new[] { "序号", "物理通道", "参数名", "单位", "变换斜率", "变换截距", "参数类型", "是否启用", "零位漂移" };
            for (var index = 0; index < properties.Length; index++)
            {
                DataGridViewColumn column;
                if (index == 6)
                {
                    var combo = new DataGridViewComboBoxColumn();
                    combo.Items.AddRange("电流", "管路压力", "夹紧力"); column = combo;
                }
                else if (index == 7) column = new DataGridViewCheckBoxColumn();
                else column = new DataGridViewTextBoxColumn();
                column.Name = column.DataPropertyName = properties[index]; column.HeaderText = headers[index];
                column.ReadOnly = index == 0 || index == 1 || index == 8;
                column.Visible = index != 8; column.SortMode = DataGridViewColumnSortMode.NotSortable;
                if (index == 4 || index == 5) column.DefaultCellStyle.FormatProvider = CultureInfo.InvariantCulture;
                grid.Columns.Add(column);
            }
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            grid.DataError += (_, e) => { e.ThrowException = false; e.Cancel = true; _status.Text = "DAQ 参数输入格式错误，请检查该单元格。"; };
        }

        private void LoadDaqDraft()
        {
            if (!_session.IsConnected || !_session.HasCapability(EngineUiContract.DaqConfiguration) || _session.Latest.DaqConfiguration == null) return;
            _daqEdit = new OriginalSettingsDraft(_session.Latest, daq: true);
            dgvDaqAI.DataSource = new BindingList<EngineDaqChannelConfiguration>(_daqEdit.Commit.DaqConfiguration.Channels.ToList());
        }

        private bool DaqEditable => _session.CanConfigureDaq && _daqEdit != null && _daqEdit.IsCurrent(_session.Latest) &&
            !_daqSubmitted && !_submitted && !_admissionInFlight && !_browsing && !_switchSubmitted &&
            !_aoSubmitted && !_aoAdmissionInFlight && !_pressureMaintenanceUi.Requested;

        private void UpdateDaqAvailability()
        {
            if (_daqEdit == null) LoadDaqDraft();
            if (_daqSubmitted && !_daqAdmissionInFlight && !_session.CommandPending && _session.CanConfigureDaq && _daqEdit.IsApplied(_session.Latest))
            {
                LoadDaqDraft(); _daqSubmitted = false;
            }
            BtnSaveDaqAI.Enabled = DaqEditable;
            dgvDaqAI.ReadOnly = !DaqEditable;
        }

        private string DaqStatusText() => _daqEdit == null ? " DAQ 参数未就绪／组件未提供能力。" :
            _daqSubmitted ? " DAQ 配置事务已提交，等待独立断能及权威版本；不会自动复跑。" :
            !_daqEdit.IsCurrent(_session.Latest) ? " DAQ 配置或运行身份已变化，保留旧编辑；停止后右键重新读取。" :
            " DAQ 参数为设备共享配置（Revision " + _daqEdit.Commit.BaseConfigurationRevision + "），保存后保持停止，下次启动重新加载并资格复核。压力输出校正须单独申请维护权限。";

        private void BtnSaveDaqAI_Click(object sender, EventArgs e)
        {
            if (ViewClosed || !_daqSaveTask.IsCompleted) return;
            _daqSaveTask = SaveDaqAsync();
            _ = ObserveSelectionAsync(_daqSaveTask);
        }

        private async Task SaveDaqAsync()
        {
            if (ViewClosed || !DaqEditable) return;
            string failure = null;
            try
            {
                if (!dgvDaqAI.EndEdit()) throw new InvalidOperationException("请先修正无效单元格。");
                var rows = (BindingList<EngineDaqChannelConfiguration>)dgvDaqAI.DataSource;
                var request = _daqEdit.Commit;
                request.DaqConfiguration = new EngineDaqConfiguration { Channels = rows.Select(c => c.Clone()).ToArray() };
                if (!request.IsStructurallyValid()) throw new InvalidOperationException("参数名、单位、类型、启用项重复或斜率／截距无效。");
                _daqSubmittedKernelRevision = _session.Latest.Kernel.Revision;
                _daqSubmitted = _daqAdmissionInFlight = true; UpdateAvailability();
                var response = await _session.SubmitConfigurationAsync(request);
                if (response != null && !response.Accepted && response.FailureCode != "OperatorCommandNotFound" &&
                    response.FailureCode != "SupervisorOperatorCommandRejected") _daqSubmitted = false;
            }
            catch (Exception ex) { _daqSubmitted = false; failure = "DAQ 参数未保存：" + ex.GetBaseException().Message; }
            finally
            {
                _daqAdmissionInFlight = false;
                if (!ViewClosed) { UpdateAvailability(); if (failure != null) _status.Text = failure; }
            }
        }
    }
}
