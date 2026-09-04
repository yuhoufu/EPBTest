using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    public partial class FrmTestSetting : Form
    {
        private readonly V3MonitorSession _session;
        private readonly Label _status;
        private TestConfigurationCommit _draft;
        private OriginalSettingsDraft _edit;
        private bool _submitted;
        private bool _admissionInFlight;
        private long _submittedKernelRevision;
        private readonly CancellationTokenSource _browserStop = new CancellationTokenSource();
        private string[] _projectChoices = Array.Empty<string>();
        private string _browseRoot;
        private bool _browsing;
        private bool _bindingProject;
        private bool _switchSubmitted;
        private readonly Func<string, bool> _confirmProjectSwitch;
        private readonly Func<string, bool> _confirmNewProject;
        private readonly Func<string, bool> _confirmResetProject;
        private bool _resetSubmitted;
        private Task _projectSelectionTask = Task.CompletedTask;
        private Task _saveTask = Task.CompletedTask;
        private bool _closing;
        internal Task PendingProjectSelection => _projectSelectionTask;
        internal Task PendingSave => _saveTask;
        private bool ViewClosed => _closing || IsDisposed || Disposing;

        internal FrmTestSetting(V3MonitorSession session, Func<string, bool> confirmProjectSwitch = null,
            Func<string, bool> confirmNewProject = null, Func<string, bool> confirmResetProject = null, Func<string, bool> confirmPressureAction = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _confirmPressureAction = confirmPressureAction ?? (message => MessageBox.Show(this, message, "压力校正确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            _confirmProjectSwitch = confirmProjectSwitch ?? (name => MessageBox.Show(this,
                "切换到已有项目“" + name + "”？\n双方历史次数均保留；当前未保存编辑将丢弃；切换完成后仍保持停止。",
                "确认项目切换", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
            _confirmNewProject = confirmNewProject ?? (name => MessageBox.Show(this,
                "新建项目“" + name + "”？\n仅新项目的试验次数从零开始；旧项目及数据保持不变。\n硬故障隔离保留，新建完成后仍保持停止，须另行开始试验。",
                "确认新建项目", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);
            _confirmResetProject = confirmResetProject ?? (name => MessageBox.Show(this,
                "确认清零当前项目“" + name + "”的全部 EPB 进度并重新学习？\n旧项目完整保留到独立封存目录，不删除旧数据。\n保留参数、目标次数及硬故障隔离；完成后仍停止，必须另行开始试验。",
                "确认封存并清零", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
            InitializeComponent();
            Text = "试验设置";
            BtnSaveDaqAI.Enabled = uiButtonResetEpbRecord.Enabled = false;
            BtnFindDir.Enabled = false; TxtTestName.Enabled = false; TxtStoreDir.ReadOnly = true;
            TxtTestName.SelectedIndexChanged += (_, __) =>
            {
                if (ViewClosed || _bindingProject || _browsing || _switchSubmitted) return;
                _projectSelectionTask = SelectExistingProjectAsync();
                _ = ObserveSelectionAsync(_projectSelectionTask);
            };
            dgvDaqAI.ReadOnly = true;
            _status = new Label { Name = "ConfigurationContractStatus", Dock = DockStyle.Top, AutoSize = true,
                ForeColor = System.Drawing.Color.Firebrick };
            Controls.Add(_status); _status.BringToFront();
            if (components == null) components = new Container();
            _status.ContextMenuStrip = new ContextMenuStrip(components);
            _status.ContextMenuStrip.Items.Add("重新读取后台配置（丢弃未保存编辑）", null, (_, __) =>
            {
                if (!CanReloadDraft()) return;
                if (MessageBox.Show(this, "丢弃当前未保存编辑并读取后台配置？此操作不会修改后台、计数或隔离状态。",
                    "重新读取配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { _submitted = _daqSubmitted = false; LoadDraft(); LoadPressureDraft(true); UpdateAvailability(); }
            });
            InitializeRunnerGrid();
            InitializeDaqGrid();
            _pressureMaintenanceUi = new PressureMaintenanceUiSession(_session);
            InitializePressureCalibrationPage();
            _pressureCalibrationTimer.Start();
            _session.Changed += UpdateAvailability;
            Disposed += (_, __) => _session.Changed -= UpdateAvailability;
            Disposed += (_, __) => _browserStop.Cancel();
            Disposed += (_, __) => { _pressureCalibrationTimer?.Dispose(); _pressureMaintenanceUi.Dispose(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel) { _closing = true; _browserStop.Cancel(); }
        }

        private async Task ObserveSelectionAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_browserStop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Never throw an async-void exception into the UI process after
                // the operator closes this child window. A live view keeps the error visible.
                if (ViewClosed || !IsHandleCreated) return;
                try { BeginInvoke((Action)(() => { if (!ViewClosed) _status.Text = "项目切换界面未更新：" + ex.GetBaseException().Message; })); }
                catch (InvalidOperationException) when (ViewClosed || !IsHandleCreated) { }
            }
        }

        private void FrmTestSetting_Load(object sender, EventArgs e)
        {
            LoadDraft();
            UpdateAvailability();
        }

        private void LoadDraft(bool reloadDaq = true)
        {
            LoadPressureDraft();
            if (reloadDaq) LoadDaqDraft();
            if (!_session.IsConnected || !_session.HasCapability(EngineUiContract.TestConfiguration) ||
                _session.Latest.TestConfiguration == null) return;
            _edit = new OriginalSettingsDraft(_session.Latest); _draft = _edit.Commit;
            BindDraft();
        }

        private bool CanReloadDraft() => _session.CanConfigure && !_aoAdmissionInFlight && !_pressureMaintenanceUi.Requested &&
            !_admissionInFlight && !_daqAdmissionInFlight && !_browsing && !_switchSubmitted &&
            (!_submitted || _session.Latest.Kernel.Revision > _submittedKernelRevision) &&
            (!_daqSubmitted || _session.Latest.Kernel.Revision > _daqSubmittedKernelRevision);

        private void BindDraft()
        {
            var value = _draft.Configuration;
            _bindingProject = true;
            TxtTestName.Text = value.TestName; TxtTestCycle.Text = value.TestPeriod.ToString(CultureInfo.InvariantCulture);
            TxtTestTarget.Text = value.TestTarget.ToString(CultureInfo.InvariantCulture); TxtStoreDir.Text = value.StoreDir;
            _browseRoot = value.StoreDir;
            _bindingProject = false;
            TxtTestMan.Text = value.Owner; RtbDesc.Text = value.Description; uiCheckBoxIsSameCycleForAllEpb.Checked = value.IsSameCycleForAllEpb;
            dgvEpbRunnerCfgControl.DataSource = new BindingList<EngineRunnerConfiguration>(value.Channels.ToList());
            foreach (var row in value.Channels)
            {
                var selection = Controls.Find("uiCheckBoxEpb" + row.Channel + "Enabled", true).OfType<Sunny.UI.UICheckBox>().Single();
                selection.Checked = row.Selected;
                var facts = _session.Latest.Channels.Single(c => c.Channel == row.Channel);
                Controls.Find("uiLabelProgress" + row.Channel, true).Single().Text = facts.CountsValid ?
                    facts.MechanicalCycles + "/" + facts.RemainingCycles : "未就绪";
            }
            var first = value.Hydraulics.Single(h => h.Id == 1); var second = value.Hydraulics.Single(h => h.Id == 2);
            checkEditPressure1To6.Checked = first.Enabled; textEditPressureValue1To6.Text = first.PressureThresholdBar.ToString();
            checkEditPressure7To12.Checked = second.Enabled; textEditPressureValue7To12.Text = second.PressureThresholdBar.ToString();
        }

        private void UpdateAvailability()
        {
            if (ViewClosed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)UpdateAvailability); }
                catch (InvalidOperationException) when (ViewClosed || !IsHandleCreated) { }
                return;
            }
            var snapshot = _session.Latest;
            if (_switchSubmitted && !_admissionInFlight && !_session.CommandPending && !_session.HasUnresolvedCommands && _session.IsConnected)
            {
                _switchSubmitted = false;
                _resetSubmitted = false;
                LoadDraft();
            }
            // Opening before attachment must not leave the original form empty forever.
            // Once editing starts, refreshes never silently replace the local draft.
            if (_draft == null) LoadDraft();
            if (_submitted && !_admissionInFlight && _session.CanConfigure && !_session.CommandPending)
            {
                if (_edit.IsApplied(snapshot))
                {
                    LoadDraft(false);
                    _submitted = false;
                }
            }
            var conflict = _edit != null && !_edit.IsCurrent(snapshot);
            UpdateDaqAvailability();
            UpdatePressureCalibrationAvailability();
            var editable = _session.CanConfigure && _draft != null && !_submitted && !_daqSubmitted && !_switchSubmitted && !_browsing &&
                !_aoSubmitted && !_aoAdmissionInFlight && !_pressureMaintenanceUi.Requested && !conflict;
            BtnSaveTest.Enabled = editable;
            uiButtonResetEpbRecord.Enabled = editable && _session.CanResetProject;
            BtnFindDir.Enabled = editable && _session.CanSwitchProject;
            TxtTestName.Enabled = BtnFindDir.Enabled && (_session.CanCreateProject || _projectChoices.Length > 0);
            TxtTestCycle.ReadOnly = TxtTestTarget.ReadOnly = TxtTestMan.ReadOnly = RtbDesc.ReadOnly = !editable;
            dgvEpbRunnerCfgControl.ReadOnly = !editable;
            uiCheckBoxIsSameCycleForAllEpb.Enabled = checkEditPressure1To6.Enabled = checkEditPressure7To12.Enabled = editable;
            textEditPressureValue1To6.ReadOnly = textEditPressureValue7To12.ReadOnly = !editable;
            for (var channel = 1; channel <= 12; channel++)
                Controls.Find("uiCheckBoxEpb" + channel + "Enabled", true).Single().Enabled = editable &&
                    snapshot?.Channels.Single(c => c.Channel == channel).Isolated == false;
            _status.Text = _browsing ? "正在读取本机项目目录……" : _resetSubmitted ? "清零事务已提交，等待旧项目封存及新运行身份；不会自动启动或解除隔离。" :
                _switchSubmitted ? "项目切换已提交，等待独立安全检查及后台交接；不会自动启动或清零。" :
                _submitted ? "配置事务已提交，等待权威配置更新及后台重新就绪；不会重复提交。" :
                _draft == null ? "后台未提供配置契约，连接后自动读取。" : conflict ?
                "后台配置或运行身份已变化，旧草稿已保留，禁止覆盖。停止后右键此提示重新读取配置。" :
                !editable ? "请先完成停止试验事务；运行／恢复期间配置只读。" :
                "当前项目参数可保存（Revision " + _draft.BaseConfigurationRevision + "）。通过名称列表切换已有项目；" +
                (_session.CanCreateProject ? "输入新名称后保存可新建项目；" : "后台未提供新建项目能力；") + "清零需单独确认。";
            _status.Text += DaqStatusText();
            if (!string.IsNullOrEmpty(snapshot?.ProjectSelection?.LastResetArchivePath))
                _status.Text += " 最近清零封存目录：" + snapshot.ProjectSelection.LastResetArchivePath;
            if (!string.IsNullOrEmpty(_session.OperationMessage)) _status.Text += " " + _session.OperationMessage;
            if (_submitted && CanReloadDraft()) _status.Text += " 后台已明确停止；可右键重新读取实际配置，未应用的编辑不会自动重试。";
            if (snapshot?.Kernel.Available == true) _status.Text += " " + snapshot.Kernel.Reason;
        }

        private void InitializeRunnerGrid()
        {
            var grid = dgvEpbRunnerCfgControl;
            grid.AutoGenerateColumns = false; grid.Columns.Clear();
            var properties = new[] { "Channel", "Name", "TargetTotalCount", "ForwardA", "SafetyMarginA", "FwdOnLimitMs", "HoldMs",
                "RevDecayLimitA", "RevDecayRigidMaxMs", "RevEmptyFixedMs", "PreReleaseKeepMs", "PreReleaseDetectTimeoutMs", "PeakIgnoreMs" };
            var headers = new[] { "通道", "名称", "目标次数", "夹紧目标(A)", "首次提前量(A)", "正上限时长(ms)", "夹紧保持(ms)",
                "反向衰减限(A)", "反衰限制时长(ms)", "反向固定空行程(ms)", "预释放维持(ms)", "预释放判定超时(ms)", "峰值忽略(ms)" };
            for (var index = 0; index < properties.Length; index++)
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = properties[index], DataPropertyName = properties[index],
                    HeaderText = headers[index], ReadOnly = index == 0, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "输入格式错误，请检查该单元格。"; };
        }

        private void BtnSaveTest_Click(object sender, EventArgs e)
        {
            if (ViewClosed || !_saveTask.IsCompleted) return;
            _saveTask = SaveTestAsync();
            _ = ObserveSelectionAsync(_saveTask);
        }

        private async Task SaveTestAsync()
        {
            if (ViewClosed || !_session.CanConfigure || _draft == null || _submitted || _daqSubmitted || _switchSubmitted || _browsing ||
                !_edit.IsCurrent(_session.Latest)) return;
            string failure = null;
            try
            {
                var value = ReadEditorConfiguration(false);
                var targetPath = Path.Combine(value.StoreDir, value.TestName, "Config", "TestConfig.xml");
                var currentPath = Path.Combine(_draft.Configuration.StoreDir, _draft.Configuration.TestName, "Config", "TestConfig.xml");
                if (!string.Equals(targetPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_session.CanCreateProject) throw new InvalidOperationException("后台未提供新建项目能力，请检查组件版本。");
                    if (!_confirmNewProject(value.TestName)) return;
                    _browsing = true; UpdateAvailability();
                    var request = await OriginalProjectBrowser.PrepareCreationRequestAsync(_session.Latest, value, _browserStop.Token);
                    if (ViewClosed || !_session.CanCreateProject || !_edit.IsCurrent(_session.Latest)) return;
                    _browsing = false; _switchSubmitted = true; _admissionInFlight = true; UpdateAvailability();
                    var created = await _session.SubmitProjectSwitchAsync(request);
                    if (created != null && !created.Accepted) _switchSubmitted = false;
                    return;
                }
                // A same-project save remains a revision/hash CAS. Do not send
                // a rename or a history reset through the configuration endpoint.
                value.TestName = _draft.Configuration.TestName; value.StoreDir = _draft.Configuration.StoreDir;
                _draft.Configuration = value;
                _submittedKernelRevision = _session.Latest.Kernel.Revision;
                _submitted = true; _admissionInFlight = true; UpdateAvailability();
                var response = await _session.SubmitConfigurationAsync(_draft);
                _admissionInFlight = false;
                // Unknown transport results stay pending under the original command ID.
                if (response != null && !response.Accepted && response.FailureCode != "OperatorCommandNotFound" &&
                    response.FailureCode != "SupervisorOperatorCommandRejected")
                    _submitted = false;
                UpdateAvailability();
            }
            catch (OperationCanceledException) when (_browserStop.IsCancellationRequested) { }
            catch (Exception ex) { _submitted = false; failure = "未保存：" + ex.GetBaseException().Message; }
            finally
            {
                _browsing = false; _admissionInFlight = false;
                if (!ViewClosed) { UpdateAvailability(); if (failure != null) _status.Text = failure; }
            }
        }

        private void dgvEmbControl_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e) { }
        private void BtnFindDir_Click(object sender, EventArgs e)
        {
            if (!_session.CanSwitchProject || _browsing || _switchSubmitted) return;
            using (var dialog = new FolderBrowserDialog { Description = "请选择项目存储路径（下一级为项目名称目录）", SelectedPath = _browseRoot })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _projectSelectionTask = LoadProjectChoicesAsync(dialog.SelectedPath);
                    _ = ObserveSelectionAsync(_projectSelectionTask);
                }
        }

        internal async Task LoadProjectChoicesAsync(string root)
        {
            if (!_session.CanSwitchProject || _browsing || _switchSubmitted) return;
            _browsing = true; UpdateAvailability();
            try
            {
                var choices = await OriginalProjectBrowser.ListAsync(root, _browserStop.Token);
                if (ViewClosed) return;
                _bindingProject = true;
                _browseRoot = root; _projectChoices = choices; TxtStoreDir.Text = root;
                TxtTestName.Items.Clear();
                foreach (var path in choices) TxtTestName.Items.Add(new DirectoryInfo(Path.GetDirectoryName(Path.GetDirectoryName(path))).Name);
                TxtTestName.Text = _draft.Configuration.TestName;
                _bindingProject = false;
                // An empty root is valid for explicit project creation. Browsing
                // itself never creates a directory, switches, or resets counts.
            }
            catch (OperationCanceledException) when (_browserStop.IsCancellationRequested) { }
            catch (Exception ex) { if (!ViewClosed) MessageBox.Show(this, ex.GetBaseException().Message, "项目读取失败"); }
            finally { _bindingProject = false; _browsing = false; if (!ViewClosed) UpdateAvailability(); }
        }

        private async Task SelectExistingProjectAsync()
        {
            if (_bindingProject || _browsing || _switchSubmitted || !_session.CanSwitchProject || TxtTestName.SelectedIndex < 0 ||
                TxtTestName.SelectedIndex >= _projectChoices.Length) return;
            var target = _projectChoices[TxtTestName.SelectedIndex];
            if (string.Equals(target, _session.Latest.ProjectSelection.ConfigurationPath, StringComparison.OrdinalIgnoreCase)) return;
            if (!_confirmProjectSwitch(TxtTestName.Text))
            {
                _bindingProject = true; TxtTestName.Text = _draft.Configuration.TestName; _bindingProject = false; return;
            }
            _browsing = true; UpdateAvailability();
            try
            {
                var source = _session.Latest;
                var request = await OriginalProjectBrowser.PrepareRequestAsync(source, target, _browserStop.Token);
                if (ViewClosed || !_session.CanSwitchProject || !_edit.IsCurrent(_session.Latest)) return;
                _switchSubmitted = true; _admissionInFlight = true; _browsing = false; UpdateAvailability();
                await _session.SubmitProjectSwitchAsync(request);
            }
            catch (OperationCanceledException) when (_browserStop.IsCancellationRequested) { }
            catch (Exception ex) { if (!ViewClosed) MessageBox.Show(this, ex.GetBaseException().Message, "项目切换未提交"); }
            finally { _browsing = false; _admissionInFlight = false; if (!ViewClosed) UpdateAvailability(); }
        }
        private EngineTestConfiguration ReadEditorConfiguration(bool preserveProjectIdentity)
        {
            if (!dgvEpbRunnerCfgControl.EndEdit()) throw new InvalidOperationException("请完成参数单元格编辑。");
            var value = _draft.Configuration.Clone();
            value.Channels = ((BindingList<EngineRunnerConfiguration>)dgvEpbRunnerCfgControl.DataSource).ToArray();
            value = value.Clone();
            value.TestPeriod = double.Parse(TxtTestCycle.Text, CultureInfo.InvariantCulture);
            value.TestTarget = int.Parse(TxtTestTarget.Text, CultureInfo.InvariantCulture);
            value.Owner = TxtTestMan.Text; value.Description = RtbDesc.Text; value.IsSameCycleForAllEpb = uiCheckBoxIsSameCycleForAllEpb.Checked;
            foreach (var row in value.Channels)
                row.Selected = Controls.Find("uiCheckBoxEpb" + row.Channel + "Enabled", true).OfType<Sunny.UI.UICheckBox>().Single().Checked;
            value.Hydraulics.Single(h => h.Id == 1).Enabled = checkEditPressure1To6.Checked;
            value.Hydraulics.Single(h => h.Id == 1).PressureThresholdBar = int.Parse(textEditPressureValue1To6.Text);
            value.Hydraulics.Single(h => h.Id == 2).Enabled = checkEditPressure7To12.Checked;
            value.Hydraulics.Single(h => h.Id == 2).PressureThresholdBar = int.Parse(textEditPressureValue7To12.Text);
            if (!preserveProjectIdentity) { value.TestName = TxtTestName.Text; value.StoreDir = _browseRoot; }
            if (!value.IsStructurallyValid()) throw new InvalidOperationException("参数范围无效，未发送配置。");
            return value;
        }

        private void uiButtonResetEpbRecord_Click(object sender, EventArgs e)
        {
            if (ViewClosed || !_saveTask.IsCompleted) return;
            _saveTask = ResetProjectAsync(); _ = ObserveSelectionAsync(_saveTask);
        }

        private async Task ResetProjectAsync()
        {
            if (ViewClosed || !_session.CanResetProject || _draft == null || _submitted || _switchSubmitted || _browsing ||
                !_edit.IsCurrent(_session.Latest)) return;
            string failure = null;
            try
            {
                // Uncommitted name/path edits never change the reset target.
                var value = ReadEditorConfiguration(true);
                if (!_confirmResetProject(value.TestName)) return;
                if (ViewClosed || !_session.CanResetProject || !_edit.IsCurrent(_session.Latest)) return;
                var request = OriginalProjectBrowser.PrepareResetRequest(_session.Latest, value);
                _resetSubmitted = _switchSubmitted = _admissionInFlight = true; UpdateAvailability();
                var result = await _session.SubmitProjectSwitchAsync(request);
                if (result != null && !result.Accepted) _resetSubmitted = _switchSubmitted = false;
            }
            catch (Exception ex) { failure = "清零未提交：" + ex.GetBaseException().Message; }
            finally
            {
                _admissionInFlight = false;
                if (!ViewClosed) { UpdateAvailability(); if (failure != null) _status.Text = failure; }
            }
        }
        private void ExplainUnavailable() => MessageBox.Show(this, "该后台事务尚未接通，未执行任何写入或硬件操作。", "功能尚未接通");
    }
}
