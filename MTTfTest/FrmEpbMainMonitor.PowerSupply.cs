using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using Controller;
using Controller.Alarm;
using DataOperation;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _powerSupplyUiAttached;
        private int _powerSupplyUiInitialized;
        private int _ownedRawPipelineAttached;
        private int _warningSnapshotStorageWarningShown;
        private ToolTip _powerSupplyToolTip;
        private ToolTip _channelRuntimeToolTip;
        private readonly Dictionary<int, Label> _channelRuntimeLabels = new Dictionary<int, Label>();
        private readonly Dictionary<int, ChannelRuntimeStateChangedEvent> _channelRuntimeStates =
            new Dictionary<int, ChannelRuntimeStateChangedEvent>();
        private readonly HashSet<int> _powerGroupInterlockLatches = new HashSet<int>();
        private readonly object _powerSupplyTelemetryGate = new object();
        private readonly Dictionary<int, PowerSupplyTelemetry> _latestPowerSupplyTelemetry =
            new Dictionary<int, PowerSupplyTelemetry>();

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AttachSafetyUiEvents();
            AttachPauseResumeUi();
            if (Interlocked.Exchange(ref _powerSupplyUiInitialized, 1) != 0) return;
            InitializeChannelRuntimeStatusUi();
            AttachOwnedRawPipeline();
            AttachUnattendedRecovery();
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
        }

        private void AttachOwnedRawPipeline()
        {
            if (twoDeviceAiAcquirer == null ||
                Interlocked.Exchange(ref _ownedRawPipelineAttached, 1) != 0)
                return;
            // Load 期间旧订阅只短暂存在。连续 Raw 存储默认未初始化时，
            // 不能再挂一个“收到后立即 Dispose”的空消费者；否则每 10 ms
            // 仍会拷贝两设备原始矩阵并进入异步队列，却不生成任何 Raw 文件。
            twoDeviceAiAcquirer.OnRawBatch -= Acq_OnRawBatch;
            if (_daqDev1 != null || _daqDev2 != null)
                twoDeviceAiAcquirer.OwnedRawBatchReady += Acq_OnOwnedRawBatch;
            if (_daqDev1 != null)
                _daqDev1.QueueFull += twoDeviceAiAcquirer.ReportRawPersistenceQueueFull;
            if (_daqDev2 != null)
                _daqDev2.QueueFull += twoDeviceAiAcquirer.ReportRawPersistenceQueueFull;
            // 即使连续 Raw 存储未启用，仍注册统一的最终 Flush 门禁；
            // 此时回调只排空采集/SQLite 发布链，不会伪称生成了 Raw 文件。
            _epb?.RegisterPausePersistenceFlush(FlushPausePersistenceAsync);
        }

        private async Task FlushPausePersistenceAsync(
            IReadOnlyDictionary<string, long> boundaries,
            CancellationToken token)
        {
            const int totalTimeoutMs = 10000;
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadlineCts.CancelAfter(totalTimeoutMs);
            var flushToken = deadlineCts.Token;
            var deadlineTicks = Stopwatch.GetTimestamp() +
                                (long)(totalTimeoutMs / 1000.0 * Stopwatch.Frequency);
            int RemainingMs()
                => Math.Max(
                    1,
                    (int)((deadlineTicks - Stopwatch.GetTimestamp()) * 1000.0 /
                          Stopwatch.Frequency));

            // 先释放 DaqAIContext 末端容量，再等待上游排空；否则当末端队列已满时，
            // Raw发布线程正阻塞在所有权移交，先 Drain 会与等待槽位形成闭环。
            if (_daqDev1 != null)
                await _daqDev1.FlushRawToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);
            if (_daqDev2 != null)
                await _daqDev2.FlushRawToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);

            var dev1Boundary = boundaries != null && boundaries.TryGetValue("Dev1", out var dev1)
                ? Math.Max(0, dev1)
                : 0;
            var dev2Boundary = boundaries != null && boundaries.TryGetValue("Dev2", out var dev2)
                ? Math.Max(0, dev2)
                : 0;
            if (twoDeviceAiAcquirer != null &&
                !await twoDeviceAiAcquirer.DrainBackgroundPipelinesToBoundariesAsync(
                        dev1Boundary,
                        dev2Boundary,
                        RemainingMs(),
                        flushToken)
                    .ConfigureAwait(false))
                throw new TimeoutException("暂停时DAQ工程处理/Raw发布链10秒内未排空。");

            // 上游排空过程中刚转移到末端的批次再做一次最终Flush。
            flushToken.ThrowIfCancellationRequested();
            if (_daqDev1 != null)
            {
                await _daqDev1.FlushRawToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);
                await _daqDev1.FlushStatToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);
            }
            if (_daqDev2 != null)
            {
                await _daqDev2.FlushRawToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);
                await _daqDev2.FlushStatToDiskAsync(RemainingMs(), flushToken).ConfigureAwait(false);
            }
        }

        private void Acq_OnOwnedRawBatch(OwnedDaqRawBatch batch)
        {
            if (batch == null) return;
            // 首次 FormClosing 会先置 _isClosing，再停止 DAQ 并排空已接收批次。
            // 此窗口内仍必须完成 Raw 所有权移交；否则最后批会被伪装成 transferred 后丢弃。
            if (batch.Device.Equals("Dev1", StringComparison.OrdinalIgnoreCase) && _daqDev1 != null)
                _daqDev1.EnqueueRawData(batch);
            else if (batch.Device.Equals("Dev2", StringComparison.OrdinalIgnoreCase) && _daqDev2 != null)
                _daqDev2.EnqueueRawData(batch);
            else
                batch.Dispose();
        }

        private void AttachSafetyUiEvents()
        {
            var manager = _epb;
            if (manager == null) return;
            if (Interlocked.CompareExchange(ref _powerSupplyUiAttached, 1, 0) != 0) return;
            try
            {
                // 旧事件只保留为用户消息，禁止再直接写 EpbTestRecord.Status。
                // 通道显示状态统一由 ChannelRuntimeStateChanged 驱动。
                manager.ChannelAlarmRaised -= OnEpbChannelAlarmRaised;
                manager.ChannelPaused -= OnEpbChannelPaused;
                manager.ChannelResumed -= OnEpbChannelResumed;
                manager.ChannelAlarmRaised -= LogEpbChannelAlarm;
                manager.ChannelPaused -= LogEpbChannelPaused;
                manager.ChannelResumed -= LogEpbChannelResumed;
                manager.ChannelAlarmRaised += LogEpbChannelAlarm;
                manager.ChannelPaused += LogEpbChannelPaused;
                manager.ChannelResumed += LogEpbChannelResumed;
                manager.PowerSupplyTelemetryUpdated += UpdatePowerSupplyStatus;
                manager.PowerSupplyFaultRaised += ShowPowerSupplyFault;
                manager.ChannelRuntimeStateChanged += OnChannelRuntimeStateChanged;
                manager.ChannelDisableRequested += OnNonRecoverableChannelDisableRequested;
                manager.ChannelDisablePersistenceFailed += OnChannelDisablePersistenceFailed;
                for (var index = 0; index < EpbGroup.Length; index++)
                {
                    var channelIndex = index;
                    if (EpbGroup[index]?.CtrlJoinTest != null)
                        EpbGroup[index].CtrlJoinTest.CheckedChanged +=
                            (sender, args) => PersistRuntimeChannelSelection(channelIndex);
                }
                manager.ChannelWarningRaised += (channel, reason) => PostSafetyStatus(
                    $"卡钳{channel} 警告：{AlarmMessageLocalizer.ToUserMessage(reason)}",
                    false);
                manager.ChannelWarningEvidenceRaised += warning => PostSafetyStatus(
                    $"软预警 EPB{warning.Channel:D2}【{AlarmMessageLocalizer.GetWarningName(warning.Code)}】" +
                    $"连续={warning.Streak}/{warning.ConfirmThreshold}；轻量证据已异步登记，" +
                    "完整圈证据仅在配置开启且通过限频门禁时后台保存。",
                    false);
                manager.SnapshotExportFailed += message => PostSafetyStatus(
                    "快照导出失败（不影响安全控制）：" + message,
                    true);
                manager.WarningSnapshotStorageChanged += ShowWarningSnapshotStorageWarning;
                manager.DaqRecoveryStateChanged += result =>
                {
                    var manuallyCancelled = string.Equals(
                        result.FailureKind,
                        "ManualCancelled",
                        StringComparison.OrdinalIgnoreCase);
                    var status = result.Recovered
                        ? "成功"
                        : manuallyCancelled
                            ? "已由人工停止取消"
                            : "失败";
                    PostSafetyStatus(
                        $"DAQ恢复 {result.Device}：{status}，" +
                        $"新鲜样本={result.FreshCallbacks}/{result.RequiredFreshCallbacks}，{result.ElapsedMs}ms。",
                        !result.Recovered && !manuallyCancelled);
                };
                manager.DaqPersistenceStateChanged += state =>
                {
                    // Lagging 是尚未触发安全暂停的瞬时诊断态，可能每秒上报一次；
                    // 不占用现场 UI 日志，只展示需要操作员知晓的状态迁移。
                    if (state.State == DaqPersistenceState.Lagging) return;
                    var correlation = state.CorrelationId == Guid.Empty
                        ? "-"
                        : state.CorrelationId.ToString("N");
                    PostSafetyStatus(
                        $"DAQ持久化 {state.Device}：{GetDaqPersistenceStateText(state.State)}，" +
                        $"队列={state.QueueDepth}，最老批次={state.OldestBatchAgeMs:F0}ms，关联号={correlation}。",
                        state.State == DaqPersistenceState.Failed);
                };
                manager.ControlFaultRaised += fault =>
                {
                    var hint = fault.Scope == FaultScope.HydraulicGroup
                        ? GetHydraulicFaultHint(fault.Reason)
                        : fault.Scope == FaultScope.Channel &&
                          (fault.Reason.IndexOf("Stall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           fault.Reason.IndexOf("near", StringComparison.OrdinalIgnoreCase) >= 0)
                            ? "若液压资格与电源回读均正常且该通道近零电流，请检查面板按钮、继电器、接插件和线束。"
                            : string.Empty;
                    PostSafetyStatus(
                        $"{GetFaultClassificationText(fault.Classification)}【{AlarmMessageLocalizer.GetCodeName(fault.Code)}】" +
                        $"范围={AlarmMessageLocalizer.GetScopeName(fault.Scope)}，卡钳=" +
                        $"{string.Join(",", fault.AffectedChannels ?? Array.Empty<int>())}。" +
                        $"{AlarmMessageLocalizer.ToUserMessage(fault.Reason)}{hint}",
                        fault.Classification != FaultClassification.SoftwareTransient);
                };
                var initialStorage = manager.GetWarningSnapshotStorageStatus();
                ShowWarningSnapshotStorageWarning(initialStorage);
                foreach (var state in manager.GetChannelRuntimeStates())
                    OnChannelRuntimeStateChanged(state);
            }
            catch
            {
                Interlocked.Exchange(ref _powerSupplyUiAttached, 0);
                throw;
            }
        }

        private void InitializeChannelRuntimeStatusUi()
        {
            if (_channelRuntimeLabels.Count > 0) return;
            _channelRuntimeToolTip = new ToolTip
            {
                AutoPopDelay = 15000,
                InitialDelay = 250,
                ReshowDelay = 100
            };
            var panels = new[]
            {
                uiTableLayoutPanel10, uiTableLayoutPanel11,
                uiTableLayoutPanel12, uiTableLayoutPanel13
            };
            for (var groupIndex = 0; groupIndex < panels.Length; groupIndex++)
            {
                for (var row = 0; row < 3; row++)
                {
                    var channel = groupIndex * 3 + row + 1;
                    var label = new Label
                    {
                        Name = $"RuntimeStateEpb{channel}",
                        Dock = DockStyle.Fill,
                        Margin = new Padding(4, 12, 4, 12),
                        TextAlign = ContentAlignment.MiddleCenter,
                        AutoEllipsis = true,
                        Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold),
                        ForeColor = Color.White,
                        BackColor = Color.FromArgb(120, 120, 120),
                        Text = "未启用",
                        Cursor = Cursors.Help
                    };
                    panels[groupIndex].Controls.Add(label, 3, row);
                    _channelRuntimeLabels[channel] = label;
                }
            }

            ChannelRuntimeStateChangedEvent[] pending;
            lock (_channelRuntimeStates)
                pending = _channelRuntimeStates.Values.Select(x => x.Clone()).ToArray();
            foreach (var state in pending)
                ApplyChannelRuntimeState(state);
            UpdateChannelRuntimeSummary();
        }

        private void OnChannelRuntimeStateChanged(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null || state.Channel < 1 || state.Channel > 12) return;
            lock (_channelRuntimeStates)
            {
                _channelRuntimeStates.TryGetValue(state.Channel, out var current);
                if (!state.IsNewerThan(current)) return;
                _channelRuntimeStates[state.Channel] = state.Clone();
            }
            UpdateUnattendedRunAuthorization(state);
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired)
                    BeginInvoke((Action)(() => ApplyLatestChannelRuntimeState(state.Channel)));
                else
                    ApplyLatestChannelRuntimeState(state.Channel);
            }
            catch { }
        }

        private void ApplyLatestChannelRuntimeState(int channel)
        {
            ChannelRuntimeStateChangedEvent latest;
            lock (_channelRuntimeStates)
                latest = _channelRuntimeStates.TryGetValue(channel, out var state)
                    ? state.Clone()
                    : null;
            if (latest != null)
                ApplyChannelRuntimeState(latest);
        }

        private void LogEpbChannelAlarm(int channel, string reason)
        {
            LogInfo($"卡钳{channel} 报警：{AlarmMessageLocalizer.ToUserMessage(reason)}");
        }

        private void LogEpbChannelPaused(int channel)
        {
            LogInfo($"卡钳{channel} 已暂停");
        }

        private void LogEpbChannelResumed(int channel)
        {
            LogInfo($"卡钳{channel} 已恢复运行");
        }

        private void ApplyChannelRuntimeState(ChannelRuntimeStateChangedEvent state)
        {
            if (!_channelRuntimeLabels.TryGetValue(state.Channel, out var label)) return;
            var record = EnsureEpbRecord(state.Channel);
            if (!record.Enabled && state.State != ChannelRuntimeState.NotEnabled)
            {
                state = state.Clone();
                state.State = ChannelRuntimeState.NotEnabled;
                state.ReasonCode = "DisabledChannelInvariant";
                state.ReasonText = "通道未启用；已拒绝迟到的故障或恢复状态。";
            }
            var localTime = state.TimestampUtc == default
                ? DateTime.Now
                : state.TimestampUtc.ToLocalTime();
            // 状态格宽度很小，原来的第二行时间会被截成“运行1…”或“运行0…”，
            // 容易被误解为数值状态。格内只保留状态，时间和原因放在悬浮提示中。
            label.Text = GetRuntimeStateText(state.State);
            label.BackColor = GetRuntimeStateColor(state.State);
            label.ForeColor = Color.White;
            label.Cursor = IsChannelRunTransitionState(state.State)
                ? Cursors.WaitCursor
                : Cursors.Help;
            _channelRuntimeToolTip?.SetToolTip(
                label,
                $"EPB{state.Channel:D2} {GetRuntimeStateText(state.State)}\r\n" +
                $"时间：{localTime:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                $"原因：{AlarmMessageLocalizer.ToUserMessage(state.ReasonText ?? state.ReasonCode ?? "-")}\r\n" +
                $"故障源：{(state.SourceChannel.HasValue ? "EPB" + state.SourceChannel.Value.ToString("D2") : "-")}\r\n" +
                $"关联号：{(state.CorrelationId == Guid.Empty ? "-" : state.CorrelationId.ToString("N"))}\r\n" +
                $"RunEpoch：{state.RunEpoch}，Formal：{state.FormalPhaseCommitted}\r\n" +
                $"资源：Timer={state.TimerActive} Runner={state.RunnerActive} Energized={state.Energized}");

            if (EpbGroup[state.Channel - 1]?.CtrlRunning != null)
            {
                var shouldShowRun = IsChannelRunToggleActiveState(state.State);
                EpbGroup[state.Channel - 1].CtrlRunning.CheckedText =
                    GetChannelRunToggleCheckedText(state.State);
                EpbGroup[state.Channel - 1].CtrlRunning.UncheckedText = "STOP";
                if (EpbGroup[state.Channel - 1].CtrlRunning.Checked != shouldShowRun)
                    EpbGroup[state.Channel - 1].CtrlRunning.Checked = shouldShowRun;
            }

            lock (_epbRecordsLock)
                record.Status = MapRuntimeRecordStatus(state.State);
            RefreshCurrentEpbSummary(state.Channel);

            if (state.State == ChannelRuntimeState.InterlockStopped)
                ShowPowerGroupInterlockLatch(state.Channel);
            else if (state.State == ChannelRuntimeState.Starting ||
                     state.State == ChannelRuntimeState.Qualification ||
                     state.State == ChannelRuntimeState.Running ||
                     state.State == ChannelRuntimeState.WarningRunning)
                ClearPowerGroupInterlockLatchAfterPreflight(state.Channel);
            UpdateChannelRuntimeSummary();
            UpdatePauseResumeChannelUi(state);
        }

        private void UpdateChannelRuntimeSummary()
        {
            ChannelRuntimeStateChangedEvent[] states;
            lock (_channelRuntimeStates)
                states = _channelRuntimeStates.Values.ToArray();
            var running = states.Count(x => x.State == ChannelRuntimeState.Starting ||
                                            x.State == ChannelRuntimeState.Learning ||
                                            x.State == ChannelRuntimeState.Running);
            var warning = states.Count(x => x.State == ChannelRuntimeState.WarningRunning);
            var alarm = states.Count(x => x.State == ChannelRuntimeState.AlarmStopped ||
                                          x.State == ChannelRuntimeState.StartBlocked);
            var interlock = states.Count(x => x.State == ChannelRuntimeState.InterlockStopped);
            var completed = states.Count(x => x.State == ChannelRuntimeState.Completed);
            EPBGroupBox.Text = $"EPB 控制｜运行 {running}  预警 {warning}  报警 {alarm}  联锁 {interlock}  完成 {completed}";
        }

        private void ShowPowerGroupInterlockLatch(int channel)
        {
            var group = _cfg?.Test?.Groups?.FirstOrDefault(x => x.Members.Contains(channel));
            if (group == null || group.Id < 1 || group.Id > 4) return;
            var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
            _powerGroupInterlockLatches.Add(group.Id);
            boxes[group.Id - 1].Text = $"电源{group.Id} 联锁锁存";
            boxes[group.Id - 1].ForeColor = Color.OrangeRed;
        }

        private void ClearPowerGroupInterlockLatchAfterPreflight(int channel)
        {
            var group = _cfg?.Test?.Groups?.FirstOrDefault(x => x.Members.Contains(channel));
            if (group == null) return;
            _powerGroupInterlockLatches.Remove(group.Id);

            // 预检期间的实时遥测曾被“联锁锁存”标题遮挡。锁存解除后主动回放
            // 最新一帧，避免电源实际已 ON 而界面一直停留在旧状态。
            PowerSupplyTelemetry latest;
            lock (_powerSupplyTelemetryGate)
                _latestPowerSupplyTelemetry.TryGetValue(group.Id, out latest);
            if (latest != null)
                ApplyPowerSupplyStatus(latest);
        }

        private static string GetRuntimeStateText(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.Starting: return "启动中";
                case ChannelRuntimeState.Learning: return "学习中";
                case ChannelRuntimeState.Running: return "运行";
                case ChannelRuntimeState.WarningRunning: return "软预警";
                case ChannelRuntimeState.PausePending: return "等待暂停";
                case ChannelRuntimeState.Paused: return "暂停";
                case ChannelRuntimeState.ResumeChecking: return "恢复预检";
                case ChannelRuntimeState.Qualification: return "资格复核";
                case ChannelRuntimeState.Recovering: return "系统自恢复";
                case ChannelRuntimeState.SystemFault: return "系统故障";
                case ChannelRuntimeState.AlarmStopped: return "报警停机";
                case ChannelRuntimeState.InterlockStopped: return "联锁停机";
                case ChannelRuntimeState.ManualStopped: return "人工停止";
                case ChannelRuntimeState.Completed: return "正常完成";
                case ChannelRuntimeState.StartBlocked: return "启动受阻";
                default: return "未启用";
            }
        }

        private static string GetDaqPersistenceStateText(DaqPersistenceState state)
        {
            switch (state)
            {
                case DaqPersistenceState.Paused: return "安全暂停";
                case DaqPersistenceState.Recovering: return "正在恢复";
                case DaqPersistenceState.Recovered: return "已恢复";
                case DaqPersistenceState.Failed: return "恢复失败";
                default: return "延迟";
            }
        }

        private static Color GetRuntimeStateColor(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.Running: return Color.FromArgb(32, 166, 82);
                case ChannelRuntimeState.Starting:
                case ChannelRuntimeState.Learning: return Color.FromArgb(41, 128, 185);
                case ChannelRuntimeState.ResumeChecking:
                case ChannelRuntimeState.Qualification: return Color.FromArgb(52, 152, 219);
                case ChannelRuntimeState.PausePending: return Color.FromArgb(96, 125, 139);
                case ChannelRuntimeState.WarningRunning: return Color.FromArgb(230, 126, 34);
                case ChannelRuntimeState.Recovering: return Color.FromArgb(52, 152, 219);
                case ChannelRuntimeState.SystemFault: return Color.FromArgb(245, 166, 35);
                case ChannelRuntimeState.AlarmStopped:
                case ChannelRuntimeState.StartBlocked: return Color.FromArgb(198, 40, 40);
                case ChannelRuntimeState.InterlockStopped: return Color.FromArgb(230, 74, 25);
                case ChannelRuntimeState.Completed: return Color.FromArgb(0, 121, 107);
                case ChannelRuntimeState.Paused: return Color.FromArgb(117, 117, 117);
                default: return Color.FromArgb(120, 120, 120);
            }
        }

        private static EpbTestStatus MapRuntimeRecordStatus(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.Starting:
                case ChannelRuntimeState.Running:
                case ChannelRuntimeState.WarningRunning: return EpbTestStatus.Running;
                case ChannelRuntimeState.Learning: return EpbTestStatus.Learning;
                case ChannelRuntimeState.PausePending:
                case ChannelRuntimeState.Paused: return EpbTestStatus.Paused;
                case ChannelRuntimeState.ResumeChecking:
                case ChannelRuntimeState.Qualification: return EpbTestStatus.Paused;
                case ChannelRuntimeState.Recovering: return EpbTestStatus.Paused;
                case ChannelRuntimeState.SystemFault: return EpbTestStatus.Paused;
                case ChannelRuntimeState.AlarmStopped: return EpbTestStatus.Alarm;
                case ChannelRuntimeState.InterlockStopped: return EpbTestStatus.Interlocked;
                case ChannelRuntimeState.ManualStopped: return EpbTestStatus.ManualStopped;
                case ChannelRuntimeState.Completed: return EpbTestStatus.Completed;
                case ChannelRuntimeState.StartBlocked: return EpbTestStatus.StartBlocked;
                default: return EpbTestStatus.NotStarted;
            }
        }

        private static string GetFaultClassificationText(FaultClassification classification)
        {
            switch (classification)
            {
                case FaultClassification.SoftwareTransient: return "系统自恢复";
                case FaultClassification.SystemFault: return "系统故障";
                case FaultClassification.HardwareConfirmed: return "硬件故障已确认";
                default: return "控制故障";
            }
        }

        private void ShowWarningSnapshotStorageWarning(WarningSnapshotStorageStatus status)
        {
            if (status == null) return;
            if (!status.IsBelowFreeSpaceWarning)
            {
                Interlocked.Exchange(ref _warningSnapshotStorageWarningShown, 0);
                return;
            }
            if (Interlocked.Exchange(ref _warningSnapshotStorageWarningShown, 1) != 0) return;

            PostSafetyStatus(
                $"WarningSnapshots 磁盘余量低：占用={status.UsedBytes / 1024d / 1024d:F1}MB，" +
                $"剩余={status.FreeBytes / 1024d / 1024d:F0}MB，" +
                $"估算可保存={status.EstimatedAdditionalCycles}圈。",
                true);
        }

        private void PersistRuntimeChannelSelection(int channelIndex)
        {
            if (_cfg?.Test == null || channelIndex < 0 || channelIndex >= EpbGroup.Length) return;
            var selected = EpbGroup[channelIndex]?.CtrlJoinTest?.Checked == true;
            lock (_epbRecordsLock)
            {
                EnsureEpbRecord(channelIndex + 1).Enabled = selected;
                _cfg.Test.GetEpbRecord(channelIndex + 1).Enabled = selected;
            }
            SaveEpbRecordsToTestConfigSafe();
        }

        private void OnNonRecoverableChannelDisableRequested(int channel, string reason)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(
                        new Action<int, string>(OnNonRecoverableChannelDisableRequested),
                        channel,
                        reason);
                    return;
                }

                lock (_epbRecordsLock)
                {
                    EnsureEpbRecord(channel).Enabled = false;
                    if (_cfg?.Test != null)
                        _cfg.Test.GetEpbRecord(channel).Enabled = false;
                }

                var index = channel - 1;
                if (index >= 0 && index < EpbGroup.Length && EpbGroup[index]?.CtrlJoinTest != null)
                    EpbGroup[index].CtrlJoinTest.Checked = false;
                var curve = Controls.Find($"CheckEpbA{channel}", true)
                    .OfType<DevExpress.XtraEditors.CheckEdit>()
                    .FirstOrDefault();
                if (curve != null) curve.Checked = false;
                ApplyAllChannelOperationStates();
                LogInfo(
                    $"卡钳{channel} 已锁存不可自恢复报警并取消当前项目启用；" +
                    "停止后重新开始或重启软件均不会自动选中。");
            }
            catch (Exception ex)
            {
                PostSafetyStatus($"卡钳{channel} 禁用状态同步到界面失败：{ex.Message}", true);
            }
        }

        private void OnChannelDisablePersistenceFailed(int channel, string message)
        {
            if (IsDisposed || Disposing) return;
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(
                        new Action<int, string>(OnChannelDisablePersistenceFailed),
                        channel,
                        message);
                    return;
                }

                LogInfo(message);
                ShowOperatorMessage(
                    message,
                    "卡钳禁用状态保存失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { }
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

        private static string GetHydraulicFaultHint(string reason)
        {
            if (!string.IsNullOrEmpty(reason) &&
                reason.IndexOf("AboveToleranceWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                return "压力高于允许范围，请优先检查调压阀、控制阀、AO标定、压力传感器量程及控制响应。";

            if (!string.IsNullOrEmpty(reason) &&
                (reason.IndexOf("PressureSample", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 reason.IndexOf("NoPressureSample", StringComparison.OrdinalIgnoreCase) >= 0))
                return "压力采样无效或过期，请优先检查压力传感器接线、DAQ采集及液压通道映射。";

            return "压力不足，请优先检查制动液液位及泄漏、卡钳开裂、接头、管路、泵输出和压力标定。";
        }

        private async System.Threading.Tasks.Task ResetPowerSupplyFaultFromUiAsync(int groupId)
        {
            if (_epb == null) return;
            if (ShowOperatorMessage(
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
                ShowOperatorMessage(
                    $"电源组{groupId}故障复位失败：\r\n{ex.Message}",
                    "复位失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void UpdatePowerSupplyStatus(PowerSupplyTelemetry telemetry)
        {
            if (telemetry == null || IsDisposed || Disposing) return;
            lock (_powerSupplyTelemetryGate)
                _latestPowerSupplyTelemetry[telemetry.ElectricalGroupId] = telemetry;
            try
            {
                BeginInvoke((Action)(() => ApplyPowerSupplyStatus(telemetry)));
            }
            catch
            {
                // 窗口退出期间忽略晚到的遥测。
            }
        }

        private void ApplyPowerSupplyStatus(PowerSupplyTelemetry telemetry)
        {
            var boxes = new[] { uiGroupBox4, uiGroupBox5, uiGroupBox6, uiGroupBox7 };
            if (telemetry.SupplyId < 1 || telemetry.SupplyId > boxes.Length) return;
            var box = boxes[telemetry.SupplyId - 1];
            if (_powerGroupInterlockLatches.Contains(telemetry.ElectricalGroupId))
            {
                box.Text = $"电源{telemetry.SupplyId} 联锁锁存";
                box.ForeColor = Color.OrangeRed;
                return;
            }
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
                            $"电源{fault.SupplyId} 故障【{AlarmMessageLocalizer.GetCodeName(fault.Code)}】";
                        boxes[fault.SupplyId - 1].ForeColor = Color.Red;
                    }
                    LogInfo(
                        $"电源组{fault.ElectricalGroupId}硬故障【{AlarmMessageLocalizer.GetCodeName(fault.Code)}】：" +
                        $"{AlarmMessageLocalizer.ToUserMessage(fault.Reason)}；" +
                        $"联动EPB={string.Join(",", fault.AffectedChannels ?? Array.Empty<int>())}");
                }));
            }
            catch { }
        }

    }
}
