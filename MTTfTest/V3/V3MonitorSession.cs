using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal interface IEngineUiClient
    {
        Task<EngineUiSnapshot> ReadUiSnapshotAsync(CancellationToken token);
        Task<EngineUiLogPage> ReadLogsAsync(EngineUiLogQuery query, EngineStateSnapshot expected, CancellationToken token);
        Task<SupervisorOperatorCommandResponse> SubmitAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind, CancellationToken token);
        Task<SupervisorOperatorCommandResponse> SubmitConfigurationAsync(EngineStateSnapshot snapshot, TestConfigurationCommit configuration, CancellationToken token);
        Task<SupervisorOperatorCommandResponse> SubmitProjectSwitchAsync(EngineStateSnapshot snapshot, ProjectSwitchRequest project, CancellationToken token);
        Task<SupervisorOperatorCommandResponse> SubmitAlarmPanelAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind, AlarmPanelCommand panel, CancellationToken token);
        Task<SupervisorOperatorCommandResponse> SubmitChannelAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind, int channel, CancellationToken token);
        bool HasUnresolvedCommands { get; }
        Task<SupervisorOperatorCommandResponse> ResolvePendingAsync(CancellationToken token);
    }

    internal sealed class V3MonitorSession : IDisposable
    {
        private readonly IEngineUiClient _client;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private int _refreshing;
        private int _commandPending;
        private int _commandGeneration;
        private int _resolving;
        private int _logsReading;
        private int _logGeneration;
        private OperatorCommandKind _activeKind;
        private bool _disposed;
        internal EngineUiSnapshot Latest { get; private set; }
        internal string ConnectionError { get; private set; } = "正在连接 EngineHost……";
        internal string OperationMessage { get; private set; } = string.Empty;
        internal string LogError { get; private set; } = string.Empty;
        internal EngineUiLogPage LogPage { get; private set; }
        internal bool IsConnected => !_disposed && string.IsNullOrEmpty(ConnectionError) && Latest != null &&
            DateTime.UtcNow.Ticks >= Latest.CapturedUtcTicks &&
            DateTime.UtcNow.Ticks - Latest.CapturedUtcTicks < TimeSpan.FromSeconds(5).Ticks;
        internal bool CommandPending => Volatile.Read(ref _commandPending) != 0;
        internal bool CanStop => (IsConnected || Latest != null && _activeKind == OperatorCommandKind.SwitchProject && _client.HasUnresolvedCommands) &&
            (!CommandPending || _activeKind != OperatorCommandKind.Stop);
        internal bool HasUnresolvedCommands => _client.HasUnresolvedCommands;
        internal bool CanClose => IsConnected && !CommandPending && !_client.HasUnresolvedCommands &&
            Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) && Latest.Kernel.CanClose &&
            string.IsNullOrEmpty(Latest.Engine.RecoveryIncidentId) &&
            string.IsNullOrEmpty(Latest.Engine.RecoveryOwnerId) && !Latest.Engine.OutputsEnergized &&
            (Latest.Engine.State == SystemTerminalState.StoppedByOperator ||
             Latest.Engine.State == SystemTerminalState.SafeIdleAlarmed);
        internal bool CanStart => IsConnected && !CommandPending && !_client.HasUnresolvedCommands &&
            Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) && Latest.Kernel.CanStart &&
            (Latest.Engine.State == SystemTerminalState.SafeIdleAlarmed || Latest.Engine.State == SystemTerminalState.StoppedByOperator) &&
            string.IsNullOrEmpty(Latest.Engine.RecoveryIncidentId) && string.IsNullOrEmpty(Latest.Engine.RecoveryOwnerId) &&
            !Latest.Engine.OutputsEnergized && (Latest.Engine.HardwareInitialized || Latest.Engine.HardwareRecompositionReady) &&
            Latest.Capabilities.Contains(EngineUiContract.Monitor);
        internal event Action Changed;
        internal bool CanPause => IsConnected && !CommandPending && !_client.HasUnresolvedCommands && HasCapability(EngineUiContract.ManualBatchControl) &&
            Latest.BatchPauseAvailable && ManualControlOwnerReady &&
            (Latest.Kernel.IsIdle || Latest.Kernel.OperatorStage == RecoveryStage.OperatorChannelsHeld);
        private bool ManualControlOwnerReady => IsConnected && Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) &&
            !Latest.Kernel.CommandPending && Latest.Engine.State == Latest.Kernel.DesiredState &&
            (Latest.Kernel.IsIdle ? (Latest.Engine.State == SystemTerminalState.Running || Latest.Engine.State == SystemTerminalState.RunningDegraded) &&
                string.IsNullOrEmpty(Latest.Engine.RecoveryOwnerId) && string.IsNullOrEmpty(Latest.Engine.RecoveryIncidentId) :
                Latest.Kernel.OperatorStage == RecoveryStage.OperatorChannelsHeld && Latest.Kernel.ActiveIncidentCount == 1 &&
                Latest.Engine.EngineInstanceId == Latest.Kernel.ManualBatchEngineInstanceId && Latest.Engine.RecoveryIncidentId == Latest.Kernel.ManualBatchIncidentId &&
                Latest.Engine.RecoveryOwnerId == Latest.Kernel.ManualBatchOwnerId);
        private bool CanOperateHealthyChannel(int channel) => channel >= 1 && channel <= 12 && !CommandPending && !_client.HasUnresolvedCommands &&
            HasCapability(EngineUiContract.ManualChannelControl) && ManualControlOwnerReady &&
            ((Latest.Engine.ChannelPauseMask & (1 << (channel - 1))) != 0 ||
                (Latest.Engine.ChannelResumeMask & Latest.Kernel.ManualPausedChannelsMask & (1 << (channel - 1))) != 0) &&
            Latest.Channels.Any(value => value.Channel == channel && value.Selected && !value.Isolated);
        internal bool CanRetryQualification(int channel)
        {
            if (channel < 1 || channel > 12 || !IsConnected || CommandPending || _client.HasUnresolvedCommands ||
                !HasCapability(EngineUiContract.ChannelQualificationRecovery) || !Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) ||
                !Latest.Kernel.IsIdle || Latest.Engine.State != Latest.Kernel.DesiredState ||
                (Latest.Engine.State != SystemTerminalState.RunningDegraded && Latest.Engine.State != SystemTerminalState.SafeIdleAlarmed) ||
                !Latest.Engine.HardwareInitialized || !string.IsNullOrEmpty(Latest.Engine.RecoveryIncidentId) ||
                !string.IsNullOrEmpty(Latest.Engine.RecoveryOwnerId)) return false;
            var scope = "Channel:" + channel;
            var state = Latest.Channels.FirstOrDefault(value => value.Channel == channel);
            return state?.Isolated == true && state.CountsValid && state.RemainingCycles > 0 &&
                   (Latest.Engine.IsolatedResources ?? Array.Empty<string>()).Contains(scope, StringComparer.OrdinalIgnoreCase) &&
                   (Latest.Kernel.QualificationRetryEligibleScopes ?? Array.Empty<string>()).Contains(scope, StringComparer.OrdinalIgnoreCase);
        }
        internal bool CanOperateChannel(int channel) => CanOperateHealthyChannel(channel) || CanRetryQualification(channel);
        internal Task SubmitChannelActionAsync(int channel) => CanRetryQualification(channel)
            ? SubmitCoreAsync(OperatorCommandKind.RetryQualification, null, null, channel)
            : CanOperateHealthyChannel(channel)
                ? SubmitCoreAsync((Latest.Engine.ChannelPauseMask & (1 << (channel - 1))) != 0 ? OperatorCommandKind.PauseChannel : OperatorCommandKind.ResumeChannel,
                    null, null, channel) : Task.CompletedTask;
        internal bool CanResume => IsConnected && !CommandPending && !_client.HasUnresolvedCommands && HasCapability(EngineUiContract.ManualBatchControl) &&
            Latest.BatchResumeAvailable && Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) && Latest.Kernel.OperatorStage == RecoveryStage.OperatorPaused &&
            !Latest.Kernel.CommandPending && Latest.Engine.State == SystemTerminalState.StoppedByOperator && !Latest.Engine.OutputsEnergized &&
            Latest.Engine.EngineInstanceId == Latest.Kernel.ManualBatchEngineInstanceId && Latest.Engine.RecoveryIncidentId == Latest.Kernel.ManualBatchIncidentId &&
            Latest.Engine.RecoveryOwnerId == Latest.Kernel.ManualBatchOwnerId;
        internal string BatchActionText => Latest?.Kernel.OperatorStage == RecoveryStage.OperatorPausePending ? "正在暂停…" :
            Latest?.Kernel.OperatorStage == RecoveryStage.OperatorResumeChecking ? "正在恢复…" :
            Latest?.Kernel.OperatorStage == RecoveryStage.OperatorPaused ? "继续试验" :
            Latest?.Kernel.OperatorStage == RecoveryStage.OperatorChannelsHeld ? "暂停试验" :
            Latest?.Engine.State == SystemTerminalState.Running || Latest?.Engine.State == SystemTerminalState.RunningDegraded ? "暂停试验" : "开始试验";
        internal Task SubmitBatchActionAsync() => CanPause ? SubmitAsync(OperatorCommandKind.Pause) :
            CanResume ? SubmitAsync(OperatorCommandKind.Resume) : CanStart ? SubmitAsync(OperatorCommandKind.Start) : Task.CompletedTask;
        internal bool CanConfigure => CanClose && HasCapability(EngineUiContract.TestConfiguration) && Latest.TestConfiguration != null;
        internal bool CanConfigureDaq => CanClose && HasCapability(EngineUiContract.DaqConfiguration) && Latest.DaqConfiguration != null;
        internal bool CanCalibrateAo => CanClose && HasCapability(EngineUiContract.AoCalibration) && Latest.AoConfiguration != null;
        internal IEnginePressureMaintenanceUiClient MaintenanceClient => _client as IEnginePressureMaintenanceUiClient;
        internal bool CanBeginMaintenance => CanClose && Latest.Engine.State == SystemTerminalState.StoppedByOperator &&
            MaintenanceClient != null && HasCapability(EngineUiContract.PressureMaintenance) && HasCapability(EngineUiContract.AoCalibration) && Latest.AoConfiguration != null;
        internal Task<SupervisorOperatorCommandResponse> SubmitMaintenanceAsync(OperatorCommandKind kind, PressureMaintenanceCommand payload) =>
            SubmitCoreAsync(kind, null, maintenance: payload);
        internal bool CanSwitchProject => CanConfigure && HasCapability(EngineUiContract.ProjectSwitch) && Latest.ProjectSelection?.IsStructurallyValid() == true;
        internal bool CanCreateProject => CanSwitchProject && HasCapability(EngineUiContract.ProjectCreation);
        internal bool CanResetProject => CanSwitchProject && HasCapability(EngineUiContract.ProjectReset);
        internal bool CanOperateAlarmPanel => IsConnected && !CommandPending && !_client.HasUnresolvedCommands &&
            HasCapability(EngineUiContract.AlarmCommands) && Latest.AlarmPanel.Available;

        internal V3MonitorSession(IEngineUiClient client) { _client = client ?? throw new ArgumentNullException(nameof(client)); }

        internal async Task RefreshAsync()
        {
            if (_disposed || Interlocked.Exchange(ref _refreshing, 1) != 0) return;
            try
            {
                var snapshot = await _client.ReadUiSnapshotAsync(_stop.Token);
                if (_disposed) return;
                if (snapshot?.IsStructurallyValid() != true) throw new InvalidOperationException("界面数据无效");
                if (Latest != null && snapshot.Engine.EngineInstanceId == Latest.Engine.EngineInstanceId &&
                    snapshot.Sequence <= Latest.Sequence) throw new InvalidOperationException("收到过期的界面数据，等待新快照");
                if (Latest?.Engine.EngineInstanceId != snapshot.Engine.EngineInstanceId) LogPage = null;
                Latest = snapshot;
                ConnectionError = string.Empty;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { ConnectionError = ex.GetBaseException().Message; }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
                if (!_disposed) Changed?.Invoke();
                if (!_disposed && _client.HasUnresolvedCommands && !CommandPending)
                    _ = ResolveUnknownAsync();
            }
        }

        internal async Task SubmitAsync(OperatorCommandKind kind)
        {
            await SubmitCoreAsync(kind, null);
        }

        internal void FollowLiveLogs() { _logGeneration++; LogPage = null; LogError = string.Empty; Changed?.Invoke(); }
        internal async Task ReadLogPageAsync(string level, long before)
        {
            if (!IsConnected || Interlocked.CompareExchange(ref _logsReading, 1, 0) != 0) return;
            var engine = Latest.Engine;
            var generation = ++_logGeneration;
            try
            {
                var page = await _client.ReadLogsAsync(new EngineUiLogQuery { Level = level, BeforeSequence = before }, engine, _stop.Token);
                if (_disposed || generation != _logGeneration || engine.EngineInstanceId != Latest?.Engine.EngineInstanceId) return;
                if (page?.IsStructurallyValid() != true) throw new InvalidOperationException("日志分页无效");
                LogPage = page; LogError = string.Empty;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { if (generation == _logGeneration) LogError = "读取日志失败：" + ex.Message; }
            finally { Interlocked.Exchange(ref _logsReading, 0); if (!_disposed) Changed?.Invoke(); }
        }

        internal async Task<SupervisorOperatorCommandResponse> SubmitConfigurationAsync(TestConfigurationCommit configuration)
        {
            return await SubmitCoreAsync(OperatorCommandKind.CommitConfiguration, configuration);
        }

        internal Task<SupervisorOperatorCommandResponse> SubmitProjectSwitchAsync(ProjectSwitchRequest project) =>
            SubmitCoreAsync(OperatorCommandKind.SwitchProject, null, project: project);

        internal Task SubmitAlarmPanelAsync(OperatorCommandKind kind, bool buzzerEnabled)
        {
            if (!CanOperateAlarmPanel || !AlarmPanelCommand.IsPanelOperation(kind)) return Task.CompletedTask;
            return SubmitCoreAsync(kind, null, new AlarmPanelCommand { PanelInstanceId = Latest.AlarmPanel.PanelInstanceId,
                BaseRevision = Latest.AlarmPanel.Revision, BuzzerEnabled = buzzerEnabled });
        }

        private async Task<SupervisorOperatorCommandResponse> SubmitCoreAsync(OperatorCommandKind kind, TestConfigurationCommit configuration,
            AlarmPanelCommand panel = null, int channel = 0, ProjectSwitchRequest project = null, PressureMaintenanceCommand maintenance = null)
        {
            if (PressureMaintenanceProtocol.IsOperation(kind) ? !CanSubmitMaintenance(kind, maintenance) :
                ManualBatchCommand.IsChannelOperation(kind) ? (kind == OperatorCommandKind.RetryQualification
                    ? !CanRetryQualification(channel) : !CanOperateHealthyChannel(channel)) : AlarmPanelCommand.IsPanelOperation(kind) ? !CanOperateAlarmPanel :
                kind == OperatorCommandKind.Stop ? !CanStop : kind == OperatorCommandKind.Pause ? !CanPause : kind == OperatorCommandKind.Resume ? !CanResume :
                kind == OperatorCommandKind.SwitchProject ? !CanSwitchProject : kind == OperatorCommandKind.CommitConfiguration ?
                    (configuration?.AoCalibration != null ? !CanCalibrateAo : configuration?.DaqConfiguration != null ? !CanConfigureDaq : !CanConfigure) : !CanStart) return null;
            if (kind == OperatorCommandKind.CommitConfiguration && configuration?.IsStructurallyValid() != true) return null;
            if (kind == OperatorCommandKind.SwitchProject && (project?.IsStructurallyValid() != true ||
                project.Creation != null && !CanCreateProject || project.Reset != null && !CanResetProject)) return null;
            var generation = ++_commandGeneration;
            _activeKind = kind;
            Interlocked.Exchange(ref _commandPending, 1);
            try
            {
                OperationMessage = project?.Reset != null ? "正在提交清零封存事务；完整保留旧数据，切换新运行身份后仍保持停止……" :
                    project?.Creation != null ? "正在提交新建项目事务；旧项目不变，仅新项目从零开始，完成后仍保持停止……" :
                    project != null ? "正在提交项目切换事务；保留双方历史计数，完成后仍保持停止……" :
                    channel != 0 ? "正在提交 EPB-" + channel + (kind == OperatorCommandKind.RetryQualification
                        ? " 单次资格复核事务；先全域断能并等待独立安全证明……" :
                        kind == OperatorCommandKind.PauseChannel ? " 暂停事务，等待当前圈完成……" : " 继续事务……") :
                    panel != null ? "正在提交报警面板操作（不解除硬故障隔离）……" : kind == OperatorCommandKind.Stop ? "正在提交停止事务……" :
                    kind == OperatorCommandKind.Pause ? "正在提交暂停事务，等待当前圈完成……" : kind == OperatorCommandKind.Resume ? "正在提交同批次继续事务……" :
                    kind == OperatorCommandKind.CommitConfiguration ? "正在提交配置事务……" : "正在提交启动事务……";
                Changed?.Invoke();
                var response = maintenance != null ? await MaintenanceClient.SubmitMaintenanceAsync(Latest.Engine, kind, maintenance, _stop.Token) :
                    project != null ? await _client.SubmitProjectSwitchAsync(Latest.Engine, project, _stop.Token) :
                    channel != 0 ? await _client.SubmitChannelAsync(Latest.Engine, kind, channel, _stop.Token) :
                    panel != null ? await _client.SubmitAlarmPanelAsync(Latest.Engine, kind, panel, _stop.Token) :
                    configuration == null ? await _client.SubmitAsync(Latest.Engine, kind, _stop.Token) :
                    await _client.SubmitConfigurationAsync(Latest.Engine, configuration, _stop.Token);
                if (_disposed || generation != _commandGeneration) return null;
                OperationMessage = DescribeResponse(response);
                return response;
            }
            catch (Exception ex) when (ex is TimeoutException || ex is System.IO.IOException)
            {
                if (generation == _commandGeneration)
                    OperationMessage = "命令回执未收到，正在查询原命令 ID；不会创建新启动命令。";
            }
            catch (Exception ex) { if (generation == _commandGeneration) OperationMessage = "操作失败：" + ex.GetBaseException().Message; }
            finally
            {
                if (generation == _commandGeneration)
                {
                    Interlocked.Exchange(ref _commandPending, 0);
                    await RefreshAsync();
                }
            }
            return null;
        }

        private async Task ResolveUnknownAsync()
        {
            if (Interlocked.CompareExchange(ref _resolving, 1, 0) != 0) return;
            var generation = _commandGeneration;
            try
            {
                var response = await _client.ResolvePendingAsync(_stop.Token);
                if (_disposed || generation != _commandGeneration || response == null) return;
                OperationMessage = DescribeResponse(response);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_disposed && generation == _commandGeneration)
                    OperationMessage = "原命令结果查询未完成：" + ex.GetBaseException().Message;
            }
            finally
            {
                Interlocked.Exchange(ref _resolving, 0);
                if (!_disposed) Changed?.Invoke();
            }
        }

        internal bool HasCapability(string name) => IsConnected && Latest.Capabilities.Contains(name);
        private bool CanSubmitMaintenance(OperatorCommandKind kind, PressureMaintenanceCommand payload)
        {
            if (MaintenanceClient == null || payload?.IsStructurallyValid(kind) != true || !IsConnected ||
                payload.EngineInstanceId != Latest.Engine.EngineInstanceId ||
                payload.UiProcessId != MaintenanceClient.UiProcessId || payload.UiProcessStartUtcTicks != MaintenanceClient.UiProcessStartUtcTicks) return false;
            if (kind == OperatorCommandKind.BeginPressureMaintenance) return CanBeginMaintenance;
            var lease = Latest.Kernel.PressureMaintenance;
            if (!Latest.Kernel.IsFresh(DateTime.UtcNow.Ticks) || !payload.Binds(lease, kind)) return false;
            if (kind == OperatorCommandKind.EndPressureMaintenance) return !CommandPending || _activeKind != kind;
            if (kind == OperatorCommandKind.StopMaintenanceOutput) return !CommandPending || _activeKind == OperatorCommandKind.SetMaintenancePressure;
            return HasCapability(EngineUiContract.PressureMaintenance) && HasCapability(EngineUiContract.AoCalibration) &&
                !CommandPending && !_client.HasUnresolvedCommands && lease.IsLive(DateTime.UtcNow.Ticks) &&
                Latest.Kernel.PressureMaintenanceStage == RecoveryStage.PressureMaintenanceReady && !Latest.Kernel.CommandPending &&
                Latest.PressureMaintenance?.Binds(lease) == true && Latest.PressureMaintenance.Prepared && Latest.PressureMaintenance.AuthorityValid;
        }
        private static string DescribeResponse(SupervisorOperatorCommandResponse response) => response.ExecutionCompleted
            ? (response.ExecutionSucceeded ? "操作已完成：" : "操作未完成：") + response.Detail
            : response.Accepted ? "Supervisor 已受理，等待后台结果：" + response.Detail
            : "操作未通过：" + response.FailureCode + "；" + response.Detail;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            // The in-flight operation owns its cancellation registrations until completion.
        }
    }
}
