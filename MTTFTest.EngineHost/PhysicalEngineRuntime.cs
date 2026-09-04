using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using Controller.Alarm;
using DataOperation;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineHardwareCommandResult
    {
        internal PressureMaintenanceExecutionReceipt PressureMaintenance { get; set; }
        internal ProjectSwitchReceipt ProjectSwitch { get; set; }
        internal bool Succeeded { get; set; }
        internal string Detail { get; set; } = string.Empty;
        internal bool OutputsOff { get; set; }
        internal bool PressureSafe { get; set; }
        internal bool DataBoundaryClosed { get; set; }
        internal bool ExecutionAuthorizationRevoked { get; set; }
        internal bool LogicalQuiescent { get; set; }
        internal bool NativeResourcesReleased { get; set; }
        internal bool CallbacksIsolated { get; set; }
        internal bool ExecutorQuiescent { get; set; }
        internal int QualificationCyclesCompleted { get; set; }
        internal int FormalCyclesCompleted { get; set; }
        internal long StableSinceUtcTicks { get; set; }
        internal bool InterruptedCycleCounted { get; set; }
        internal SystemTerminalState RecoveredState { get; set; }
    }

    internal interface IEngineHardwareRuntime : IDisposable
    {
        event Action<FaultObservation> FaultObserved;
        bool Initialized { get; }
        Task<(bool Succeeded, string Detail)> InitializeSafeIdleAsync(
            RecoveryIdentity identity,
            CancellationToken token);
        Task<EngineHardwareCommandResult> ExecuteAsync(
            RecoveryCommand command,
            CancellationToken token);
        EngineTelemetryFrame CaptureTelemetry(long sequence);
    }

    internal interface IEngineAlarmPanel
    {
        Task ExecutePanelCommandAsync(OperatorCommand command, CancellationToken token);
    }

    internal interface IEngineSafetyHandoff
    {
        bool HardwareRecompositionReady { get; }
        Task PrepareSafetyHandoffAsync(RecoveryCommand command, EngineHardwareCommandResult result, CancellationToken token);
    }

    internal interface IEngineManualChannelState
    {
        int ChannelPauseMask { get; }
        int ChannelResumeMask { get; }
    }

    internal interface IEngineProjectState
    {
        EngineProjectActivation ProjectActivation { get; }
        string[] ProjectIsolatedResources { get; }
    }

    internal sealed partial class PhysicalEngineRuntime : IEngineHardwareRuntime, IEngineUiSource, IEngineAlarmPanel, IEngineManualChannelState, IEngineSafetyHandoff, IEngineProjectState
    {
        private readonly EngineHostLog _log = new EngineHostLog();
        private RecoveryIdentity _identity;
        private GlobalConfig _config;
        private EpbManager _manager;
        private TwoDeviceAiAcquirer _acquirer;
        private DoController _do;
        private AoController _ao;
        private EngineRunCheckpointStore _checkpointStore;
        private EngineTestConfigurationStore _testConfigurationStore;
        private EngineProjectSelectionStore _projectSelectionStore;
        private EngineTestConfiguration _testConfiguration;
        private long _configurationRevision;
        private AlarmManager _alarmPanel;
        private EngineProjectPersistence _projectPersistence;
        private EngineRawPersistence _rawPersistence;
        private readonly EngineManualBatchContinuation _manualBatch = new EngineManualBatchContinuation();
        private readonly EngineProjectActivation _launchProjectActivation;
        private EngineProjectActivation _projectActivation;
        private string[] _inheritedProjectIsolations = Array.Empty<string>();
        public EngineProjectActivation ProjectActivation => _projectActivation?.Clone();
        public string[] ProjectIsolatedResources => _inheritedProjectIsolations
            .Concat(_checkpointStore?.Snapshot()?.IsolatedResources ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        internal PhysicalEngineRuntime(EngineProjectActivation projectActivation = null)
        {
            if (projectActivation != null && !projectActivation.IsStructurallyValid())
                throw new ArgumentException("ProjectActivationInvalid", nameof(projectActivation));
            _launchProjectActivation = projectActivation?.Clone();
            FaultObserved += _ =>
            {
                _manualBatch.Invalidate();
                _manager?.InvalidatePreparedFormalRun();
            };
        }

        public event Action<FaultObservation> FaultObserved;
        public bool Initialized { get; private set; }

        public async Task<(bool Succeeded, string Detail)> InitializeSafeIdleAsync(
            RecoveryIdentity identity,
            CancellationToken token)
        {
            _identity = identity?.Clone() ?? throw new ArgumentNullException(nameof(identity));
            try
            {
                _projectSelectionStore = new EngineProjectSelectionStore();
                var selection = _launchProjectActivation == null ? _projectSelectionStore.ReadActive(_identity) :
                    _projectSelectionStore.Activate(_identity, _launchProjectActivation.OperatorCommandId,
                        _launchProjectActivation.PreparedDocumentSha256, expectedPlanSha256: _launchProjectActivation.PlanSha256, token: token);
                _checkpointStore = new EngineRunCheckpointStore(_identity.SessionId,
                    runId: selection?.RunScopedCheckpoint == true ? _identity.RunId : null);
                return await ComposeSafeIdleAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A corrupt checkpoint is a visible failed initialization, not a
                // reason to crash the host before its UI/status pipes are available.
                _log.Error("EngineHost检查点初始化失败，未授予运行资格。", "EngineHost", ex);
                return (false, "EngineCheckpointInitializationFailed:" + ex.GetBaseException().Message);
            }
        }

        private async Task<(bool Succeeded, string Detail)> ComposeSafeIdleAsync(
            CancellationToken token)
        {
            _releasedForSafety = false;
            try
            {
                if (!RuntimeConfigPaths.Validate(false, out var validation))
                    return (false, "RuntimeConfigInvalid:" + validation);
                var selection = _projectSelectionStore.ReadActive(_identity);
                var activatedPlan = _projectSelectionStore.ReadActivatedPlan(selection);
                _projectActivation = selection?.Activation?.Clone();
                _inheritedProjectIsolations = activatedPlan?.IsolatedResources?.ToArray() ?? Array.Empty<string>();
                var config = ConfigLoader.LoadAllForEngine(RuntimeConfigPaths.Directory, selection?.ConfigurationPath, _log);
                _aoConfigurationStore = new EngineAoConfigurationStore(RuntimeConfigPaths.GetPath("AOConfig.xml"));
                _aoConfiguration = null;
                _aoConfiguration = _aoConfigurationStore.Read();
                config.AO = EngineAoConfigurationStore.ToHardwareConfiguration(_aoConfiguration.Configuration);
                if (selection == null)
                    config.Test = EngineTestConfigurationStore.LoadProjectWithoutReset(config.Test, RuntimeConfigPaths.GetPath("TestConfig.xml"));
                _projectSelectionStore.BindInitial(_identity,
                    Path.GetFullPath(ConfigLoader.GetProjectTestConfigPath(config.Test.StoreDir, config.Test.TestName)));
                if (EngineProjectIsolation.Apply(config.Test, _inheritedProjectIsolations))
                    ConfigLoader.SaveTest(ConfigLoader.GetProjectTestConfigPath(config.Test.StoreDir, config.Test.TestName), config.Test);
                _config = config;
                _displayConfig = config;
                _testConfigurationStore = new EngineTestConfigurationStore(Path.Combine(config.Test.StoreDir, config.Test.TestName, "Config", "TestConfig.xml"));
                RefreshTestConfiguration();
                EngineAoConfigurationStore.ValidateOperatingPressures(_aoConfiguration.Configuration, _testConfiguration.Hydraulics);
                var rawEnabled = ProgramStoragePolicy.ParseBoolean(ConfigurationManager.AppSettings["RawDataLoggingEnabled"], false,
                    "RawDataLoggingEnabled", message => _log.Warn(message, "落盘"));
                _projectPersistence = EngineProjectPersistence.Open(config.Test, _log);
                _do = new DoController(config.DO, _log);
                _ao = new AoController(config.AO, _log, initializeWithZeroVoltage: true);
                var daq = DaqRuntimeSettings.Load(ConfigurationManager.AppSettings);
                _daqConfigurationStore = new EngineDaqConfigurationStore(RuntimeConfigPaths.GetPath("AIConfig.xml"));
                _daqConfiguration = null;
                _daqConfiguration = _daqConfigurationStore.Read();
                var ai = EngineDaqConfigurationStore.ToHardwareConfiguration(_daqConfiguration.DaqConfiguration);
                ConfigureUiRoutes(ai, daq.SampleRateHz);
                _acquirer = new TwoDeviceAiAcquirer(
                    ai, daq.SampleRateHz, daq.SamplesPerChannel, 10, _log);
                if (rawEnabled)
                {
                    if (!double.TryParse(ConfigurationManager.AppSettings["FileChangeMinutes"], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var rotation)) rotation = 60;
                    var rawIdentity = _identity.Clone();
                    _rawPersistence = new EngineRawPersistence(_projectPersistence.RootDirectory, ai, daq, rotation,
                        new[] { RuntimeConfigPaths.GetPath("AIConfig.xml"), RuntimeConfigPaths.GetPath("DOConfig.xml"),
                            RuntimeConfigPaths.GetPath("AOConfig.xml"), ConfigLoader.GetProjectTestConfigPath(config.Test.StoreDir, config.Test.TestName) },
                        _acquirer.ReportRawPersistenceQueueFull,
                        error => PublishProgressFault(rawIdentity, error, "RawPersistenceUnavailable", "ContinuousRawWriteFailed"),
                        message => _log.Warn(message, "落盘"));
                    _acquirer.OwnedRawBatchReady += _rawPersistence.Enqueue;
                }
                _manager = new EpbManager(config, _do, _ao, _acquirer, _log);
                _manager.Recorder = _projectPersistence.Recorder;
                var persistenceBoundary = new EnginePersistenceBoundary(_acquirer.DrainBackgroundPipelinesToBoundariesAsync);
                if (_rawPersistence == null) _manager.RegisterPausePersistenceFlush(persistenceBoundary.FlushAsync);
                else
                {
                    var raw = _rawPersistence; var acquisition = _acquirer;
                    _manager.RegisterPausePersistenceFlush((boundaries, cancellation) => raw.FlushBoundaryAsync(
                        boundaries, acquisition.DrainBackgroundPipelinesToBoundariesAsync, cancellation));
                }
                var alarmConfig = AlarmConfigLoader.Load(RuntimeConfigPaths.GetPath("AlarmConfig.xml"), _log);
                if (_alarmPanel == null) _alarmPanel = new AlarmManager(alarmConfig, _log);
                _manager.Alarm = _alarmPanel;
                _manager.AlarmConfig = alarmConfig;
                _manager.SystemFaultRaised += OnSystemFaultRaised;
                _manager.ChannelRuntimeStateChanged += OnProgressChannelState;
                _manager.ChannelCycleCompleted += OnProgressFormalCompleted;
                _manager.ChannelMechanicalCycleCompleted += OnProgressMechanicalCompleted;
                StartProgressPublisher();
                _manager.PowerSupplyTelemetryUpdated += OnUiPowerTelemetry;
                _acquirer.OnEngBatch += OnUiEngineeringBatch;
                _acquirer.Start();
                var safety = await _manager.StopAllAsync(token).ConfigureAwait(false);
                await FlushProgressAsync(token).ConfigureAwait(false);
                Initialized = safety?.FullyConfirmed == true;
                return (Initialized,
                    safety == null
                        ? "InitialSafetyProofMissing"
                        : $"InitialSafety={safety.Outcome};Physical={safety.PhysicalSafetyConfirmed};" +
                          $"Persistence={safety.PersistenceBoundaryConfirmed};Logical={safety.LogicalQuiescenceConfirmed}");
            }
            catch (Exception ex)
            {
                _log.Error("EngineHost硬件组合根初始化失败。", "EngineHost", ex);
                Dispose();
                return (false, "EngineCompositionFailed:" + ex.GetBaseException().Message);
            }
        }

        public async Task<EngineHardwareCommandResult> ExecuteAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            if (command?.IsStructurallyValid() != true)
                return Failed("RecoveryCommandInvalid");
            if (PressureMaintenanceProtocol.IsExecution(command.Kind))
                return await ExecutePressureMaintenanceAsync(command, token).ConfigureAwait(false);
            if (_pressureMaintenance != null || _maintenanceDataBoundaryClosed && !_releasedForSafety && _manager == null)
            {
                if (EngineHostProtocol.IsPrioritySafetyCommand(command.Kind))
                    return await StopMaintenanceForHandoffAsync().ConfigureAwait(false);
                return Failed("MaintenanceOwnsHardware;EndMaintenanceBeforeOtherOperations");
            }
            if (_manager == null)
            {
                if (!_releasedForSafety) return Failed("EngineHardwareUnavailable");
                if (EngineHostProtocol.IsPrioritySafetyCommand(command.Kind))
                {
                    RecordStoppedCheckpoint(command);
                    return ReleasedIdleResult("HardwareAlreadyReleased;IndependentPhysicalProofRequired");
                }
                if (command.Kind == RecoveryCommandKind.CommitTestConfiguration)
                    return CommitReleasedConfiguration(command, token);
                if (command.Kind == RecoveryCommandKind.PrepareProjectSwitch || command.Kind == RecoveryCommandKind.AbortProjectSwitch)
                    return ExecuteReleasedProjectSwitch(command, token);
                if (command.Kind == RecoveryCommandKind.RunPassivePreflight || command.Kind == RecoveryCommandKind.RebuildResource)
                {
                    _identity = command.Identity.Clone();
                    var composed = await ComposeSafeIdleAsync(token).ConfigureAwait(false);
                    if (!composed.Succeeded) return Failed(composed.Detail);
                    if (command.Kind == RecoveryCommandKind.RebuildResource)
                        return new EngineHardwareCommandResult { Succeeded = true, Detail = "ReleasedHostRecomposed:" + composed.Detail };
                }
                else if (command.Kind != RecoveryCommandKind.IsolateResource)
                    return Failed("ReleasedHardwareRequiresSupervisorPreflight");
            }
            switch (command.Kind)
            {
                case RecoveryCommandKind.PauseChannelGracefully:
                    var pauseChannel = command.OperatorTransaction.ManualBatch.Channel;
                    if ((ChannelPauseMask & (1 << (pauseChannel - 1))) == 0) return Failed("ChannelPauseNotEligible");
                    await _manualBatch.PauseAsync(command, cancellation => _manager.PauseChannelGracefullyAsync(pauseChannel, cancellation),
                        PersistManualChannelBoundaryAsync, token).ConfigureAwait(false);
                    return new EngineHardwareCommandResult { Succeeded = true, DataBoundaryClosed = true,
                        Detail = "ChannelPaused;HealthyPeersRetained;NotMaintenanceIsolation" };
                case RecoveryCommandKind.ResumePausedChannel:
                    var resumeChannel = command.OperatorTransaction.ManualBatch.Channel;
                    if ((ChannelResumeMask & (1 << (resumeChannel - 1))) == 0) return Failed("ChannelResumeNotEligible");
                    await _manualBatch.ResumeAsync(command, cancellation => _manager.ResumePausedChannelAsync(resumeChannel, cancellation),
                        async cancellation =>
                        {
                            await PersistManualChannelBoundaryAsync(cancellation).ConfigureAwait(false);
                            if (_manualBatch.ChannelMask == 0) _checkpointStore.RecordManualBatchState(_identity, false);
                        }, token).ConfigureAwait(false);
                    return new EngineHardwareCommandResult { Succeeded = true, Detail = "ChannelResumed;SameRun;CountsPreserved" };
                case RecoveryCommandKind.PauseBatchGracefully:
                    await _manualBatch.PauseAsync(command, _manager.PauseBatchGracefullyAsync,
                        async cancellation =>
                        {
                            await FlushProgressAsync(cancellation).ConfigureAwait(false);
                            _checkpointStore.RecordManualBatchState(_identity, true);
                        }, token).ConfigureAwait(false);
                    return new EngineHardwareCommandResult { Succeeded = true, OutputsOff = true, PressureSafe = true,
                        DataBoundaryClosed = true, Detail = "ManualBatchPaused;CountsPreserved;HardwareHostRetained" };
                case RecoveryCommandKind.ResumePausedBatch:
                    await _manualBatch.ResumeAsync(command, async cancellation =>
                        {
                            ValidateManualResumePressure();
                            await _manager.ResumeBatchAsync(cancellation).ConfigureAwait(false);
                        }, async cancellation =>
                        {
                            await FlushProgressAsync(cancellation).ConfigureAwait(false);
                            _checkpointStore.RecordManualBatchState(_identity, false);
                        }, token).ConfigureAwait(false);
                    return new EngineHardwareCommandResult { Succeeded = true, Detail = "ManualBatchResumed;SameRunAndModels;CountsPreserved" };
                case RecoveryCommandKind.DisableOutputs:
                case RecoveryCommandKind.SealActiveCycle:
                case RecoveryCommandKind.EnterSafeIdle:
                case RecoveryCommandKind.StopByOperator:
                    _manualBatch.Invalidate();
                    var result = await _manager.StopAllAsync(token).ConfigureAwait(false);
                    var resultWithProgress = FromSafety(result);
                    try { await FlushProgressAsync(token).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        resultWithProgress.Succeeded = false; resultWithProgress.DataBoundaryClosed = false;
                        resultWithProgress.Detail += ";ProgressProjectionFailed:" + ex.GetBaseException().Message;
                        return resultWithProgress;
                    }
                    if (result?.FullyConfirmed == true) RefreshTestConfiguration();
                    RecordStoppedCheckpoint(command);
                    return resultWithProgress;
                case RecoveryCommandKind.CommitTestConfiguration:
                    return Failed("ConfigurationRequiresReleasedHardwareHandoff");
                case RecoveryCommandKind.RunPassivePreflight:
                    return await RunPassivePreflightAsync(command, token)
                        .ConfigureAwait(false);
                case RecoveryCommandKind.RebuildResource:
                    DisposeHardware();
                    _identity = command.Identity.Clone();
                    var rebuilt = await ComposeSafeIdleAsync(token).ConfigureAwait(false);
                    return new EngineHardwareCommandResult
                    {
                        Succeeded = rebuilt.Succeeded,
                        Detail = "ResourceGenerationRebuilt:" + command.TargetResource + ";" +
                                 rebuilt.Detail,
                        OutputsOff = rebuilt.Succeeded,
                        PressureSafe = rebuilt.Succeeded,
                        DataBoundaryClosed = rebuilt.Succeeded,
                        ExecutionAuthorizationRevoked = rebuilt.Succeeded
                    };
                case RecoveryCommandKind.RunQualificationCycle:
                    return await RunQualificationAsync(command, token)
                        .ConfigureAwait(false);
                case RecoveryCommandKind.ResumeFormalRun:
                    var checkpoint = _checkpointStore?.Snapshot();
                    if (_manager.IsBatchSessionActive &&
                        checkpoint != null &&
                        checkpoint.State == "QualifiedAwaitingSupervisor" &&
                        checkpoint.QualificationCyclesCompleted >= 2)
                    {
                        var startedChannels = await _manager.CommitPreparedFormalRunAsync(command, token).ConfigureAwait(false);
                        await FlushProgressAsync(token).ConfigureAwait(false);
                        if (TryGetQualificationRetryChannel(command, checkpoint, out var recoveredChannel))
                        {
                            RefreshTestConfiguration();
                            checkpoint = _checkpointStore.RecordQualificationRetryReintegrated(command.Identity,
                                command.TargetResource, recoveredChannel, _testConfiguration.ComputeSha256());
                        }
                        checkpoint = _checkpointStore.RecordFormalRunAuthorized(command.Identity);
                        return new EngineHardwareCommandResult
                        {
                            Succeeded = true,
                            Detail = "FormalRunActive;Channels=" + string.Join(",", startedChannels),
                            RecoveredState = checkpoint.IsolatedResources.Length > 0 ? SystemTerminalState.RunningDegraded : SystemTerminalState.Running,
                            QualificationCyclesCompleted =
                                checkpoint.QualificationCyclesCompleted,
                            FormalCyclesCompleted = checkpoint.FormalCyclesSinceRecovery,
                            StableSinceUtcTicks = checkpoint.StableSinceUtcTicks
                        };
                    }
                    return Failed("FormalRunNotActiveAfterQualification");
                case RecoveryCommandKind.IsolateResource:
                    return await IsolateResourceAsync(command, token)
                        .ConfigureAwait(false);
                default:
                    return Failed("RecoveryCommandUnsupported:" + command.Kind);
            }
        }

        private async Task<EngineHardwareCommandResult> RunPassivePreflightAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            var safety = await _manager.StopAllAsync(token).ConfigureAwait(false);
            if (safety?.FullyConfirmed != true)
                return FromSafety(safety);
            await FlushProgressAsync(token).ConfigureAwait(false);
            var configPath = RuntimeConfigPaths.GetPath("TestConfig.xml");
            if (!File.Exists(configPath)) return Failed("TestConfigMissing");
            var configurationSha256 = EngineTestConfigurationStore.Capture(_config.Test).ComputeSha256();
            var previous = _checkpointStore.Snapshot();
            if (previous != null && previous.ConfigurationSha256 != configurationSha256 &&
                (previous.ConfigurationSha256 == SupervisorProtocol.ComputeSha256(configPath) ||
                 _testConfigurationStore.CanRebindCheckpoint(command.Identity, configurationSha256)))
                _checkpointStore.RebindStoppedConfiguration(command.Identity, _config.Test, configurationSha256);
            var checkpoint = _checkpointStore.LoadOrCreate(
                command.Identity,
                _config.Test,
                configurationSha256);
            var runnable = QualificationChannels(command, checkpoint)
                .Where(channel => checkpoint.RemainingFormalCycles[channel - 1] > 0)
                .ToArray();
            if (runnable.Length == 0)
                return Failed("NoRemainingFormalWork");
            return new EngineHardwareCommandResult
            {
                Succeeded = true,
                Detail = "PassivePreflightComplete;Channels=" +
                         string.Join(",", runnable) +
                         ";CheckpointRevision=" + checkpoint.Revision,
                OutputsOff = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                ExecutionAuthorizationRevoked = true
            };
        }

        private void ValidateManualResumePressure()
        {
            if (_manager?.CurrentBatchPauseState != BatchPauseState.Paused || !_manager.IsBatchSessionActive || _acquirer == null)
                throw new InvalidOperationException("ManualResumeBatchNotPaused");
            var selected = _config.Test.EpbRecords.Snapshot().Where(record => record.Enabled).Select(record => record.Id).ToArray();
            var groups = _config.Test.Hydraulics.Where(group => group.Enabled &&
                selected.Any(channel => group.Members.Count > 0 ? group.Members.Contains(channel) : (channel <= 6 ? 1 : 2) == group.Id)).ToArray();
            if (groups.Length == 0) throw new InvalidOperationException("ManualResumeHydraulicScopeMissing");
            foreach (var group in groups)
            {
                var sample = _acquirer.ReadPressureSample(group.Id);
                if (!sample.IsFinite || sample.AgeMs < 0 || sample.AgeMs > group.PressureSampleMaxAgeMs || sample.ValueBar > group.ReleaseSafePressureBar)
                    throw new InvalidOperationException("ManualResumePressureNotSafeOrFresh:" + group.Id);
            }
        }

        private async Task<EngineHardwareCommandResult> RunQualificationAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            var checkpoint = _checkpointStore?.Snapshot();
            if (checkpoint?.IsValidFor(command.Identity) != true)
                return Failed("QualificationCheckpointUnavailable");
            var channels = QualificationChannels(command, checkpoint)
                .Where(channel => checkpoint.RemainingFormalCycles[channel - 1] > 0)
                .ToArray();
            if (channels.Length == 0) return Failed("NoRemainingFormalWork");
            if (TryGetQualificationRetryChannel(command, checkpoint, out var recoveredChannel))
            {
                if (_inheritedProjectIsolations.Contains(command.TargetResource, StringComparer.OrdinalIgnoreCase))
                    return Failed("InheritedProjectIsolationRequiresProjectTransaction");
                var reset = _manager.ResetPermanentAlarm(recoveredChannel, true);
                if (reset?.Succeeded != true)
                    return Failed("QualificationRetryAlarmResetFailed:" + (reset?.Error ?? "Unknown"));
                RefreshTestConfiguration();
            }
            _manager.EpbTestCycle = channels.ToDictionary(
                channel => channel,
                channel => checkpoint.RemainingFormalCycles[channel - 1]);
            var runId = Guid.Parse(command.Identity.RunId);
            var start = await _manager.PrepareBatchQualificationAsync(
                    channels,
                    command,
                    new RunChainIdentity(
                        runId,
                        runId,
                        Guid.Empty,
                        (int)Math.Min(int.MaxValue,
                            Math.Max(0, command.Identity.Generation - 1)),
                        command.Identity.RunEpoch),
                    token)
                .ConfigureAwait(false);
            var satisfied = (start?.QualifiedChannels ?? Array.Empty<int>())
                .Concat(start?.CompletedDuringStartChannels ?? Array.Empty<int>())
                .Distinct()
                .ToArray();
            var missing = channels.Except(satisfied).ToArray();
            if (start == null || start.TestRunId == Guid.Empty ||
                missing.Length > 0 || (start.Faults?.Length ?? 0) > 0 || start.StartedChannels.Length != 0)
                return Failed(
                    "QualificationStartIncomplete;Missing=" +
                    string.Join(",", missing) + ";Faults=" +
                    string.Join("|", (start?.Faults ?? Array.Empty<ChannelStartFault>())
                        .Select(fault => "EPB" + fault.Channel + ":" +
                                         fault.Stage + ":" + fault.Reason)));
            if (start.QualifiedChannels.Length == 0)
                return Failed("NoRemainingFormalWork");
            await FlushProgressAsync(token).ConfigureAwait(false);
            checkpoint = _checkpointStore.RecordQualificationPrepared(command.Identity);
            return new EngineHardwareCommandResult
            {
                Succeeded = true,
                Detail = "QualificationCompleted;AwaitingSupervisor;Channels=" +
                         string.Join(",", start.QualifiedChannels),
                OutputsOff = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = checkpoint.FormalCyclesSinceRecovery,
                StableSinceUtcTicks = checkpoint.StableSinceUtcTicks,
                InterruptedCycleCounted = false
            };
        }

        private async Task<EngineHardwareCommandResult> IsolateResourceAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            var scope = command.TargetResource;
            if (_manager?.IsBatchSessionActive == true)
                return Failed("ResourceIsolationRequiresStoppedBatch");
            _manager?.InvalidatePreparedFormalRun();
            await FlushProgressAsync(token).ConfigureAwait(false);
            var isolationConfig = _config ?? _displayConfig;
            if (isolationConfig == null) return Failed("ResourceIsolationConfigurationMissing");
            var affected = ResolveAffectedChannels(scope);
            foreach (var channel in affected)
            {
                try { _manager?.StopChannel(channel); } catch { }
                var record = isolationConfig.Test.GetEpbRecord(channel);
                record.Enabled = false;
                record.PermanentAlarmLatched = true;
                record.PermanentAlarmCode = "V3RecoveryKernelIsolation";
                record.PermanentAlarmReason = "RecoveryKernel:" + scope;
                record.PermanentAlarmUtc = DateTime.UtcNow;
            }
            try
            {
                var project = ConfigLoader.GetProjectTestConfigPath(
                    isolationConfig.Test.StoreDir, isolationConfig.Test.TestName);
                if (!string.IsNullOrWhiteSpace(project))
                    ConfigLoader.SaveTest(project, isolationConfig.Test);
                _testConfiguration = EngineTestConfigurationStore.Capture(isolationConfig.Test);
                _checkpointStore.Update(command.Identity, value =>
                {
                    value.IsolatedResources = (value.IsolatedResources ?? Array.Empty<string>())
                        .Concat(new[] { scope })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    value.SelectedChannels = value.SelectedChannels
                        .Except(affected)
                        .ToArray();
                    value.ConfigurationSha256 = _testConfiguration.ComputeSha256();
                    value.State = "IsolationCommittedAwaitingSupervisor";
                    value.ActiveCycleInvalidated = true;
                    value.QualificationCyclesCompleted = 0;
                    value.FormalCyclesSinceRecovery = 0;
                    value.StableSinceUtcTicks = 0;
                    return true;
                }, "ResourceIsolated:" + scope);
                var checkpoint = _checkpointStore?.Snapshot();
                var remaining = (checkpoint?.SelectedChannels ?? Array.Empty<int>())
                    .Where(channel =>
                        checkpoint.RemainingFormalCycles[channel - 1] > 0)
                    .ToArray();
                if (remaining.Length == 0)
                    return new EngineHardwareCommandResult
                    {
                        Succeeded = true,
                        Detail = "ResourceIsolationPersistedSafeIdle:" + scope +
                                 ";Channels=" + string.Join(",", affected),
                        OutputsOff = true,
                        PressureSafe = true,
                        DataBoundaryClosed = true,
                        ExecutionAuthorizationRevoked = true
                    };

                return new EngineHardwareCommandResult
                {
                    Succeeded = true,
                    Detail = "ResourceIsolationPersistedAwaitingSupervisor:" + scope +
                             ";Channels=" + string.Join(",", affected) +
                             ";HealthyAwaitingQualification=" + string.Join(",", remaining)
                };
            }
            catch (Exception ex)
            {
                return Failed("ResourceIsolationPersistenceFailed:" +
                              ex.GetBaseException().Message);
            }
        }

        private int[] QualificationChannels(RecoveryCommand command, EngineRunCheckpoint checkpoint)
        {
            var selected = checkpoint?.SelectedChannels ?? Array.Empty<int>();
            return TryGetQualificationRetryChannel(command, checkpoint, out var channel)
                ? selected.Concat(new[] { channel }).Distinct().OrderBy(value => value).ToArray()
                : selected.ToArray();
        }

        private static bool TryGetQualificationRetryChannel(RecoveryCommand command, EngineRunCheckpoint checkpoint, out int channel)
        {
            channel = command?.OperatorTransaction?.ManualBatch?.Channel ?? 0;
            var expectedScope = "Channel:" + channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return command?.OperatorTransaction?.Kind == OperatorCommandKind.RetryQualification &&
                   channel >= 1 && channel <= 12 && string.Equals(command.TargetResource, expectedScope, StringComparison.Ordinal) &&
                   (checkpoint?.IsolatedResources ?? Array.Empty<string>()).Contains(expectedScope, StringComparer.OrdinalIgnoreCase);
        }

        private static int[] ResolveAffectedChannels(string scope)
        {
            if (string.Equals(scope, "System", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Range(1, 12).ToArray();
            var parts = (scope ?? string.Empty).Split(':');
            int id;
            if (parts.Length != 2)
                return Array.Empty<int>();
            var numericId = parts[1];
            if (numericId.StartsWith("Dev", StringComparison.OrdinalIgnoreCase))
                numericId = numericId.Substring(3);
            if (!int.TryParse(numericId, out id)) return Array.Empty<int>();
            if (string.Equals(parts[0], "Channel", StringComparison.OrdinalIgnoreCase))
                return id >= 1 && id <= 12 ? new[] { id } : Array.Empty<int>();
            if (string.Equals(parts[0], "Power", StringComparison.OrdinalIgnoreCase))
                return id >= 1 && id <= 4
                    ? Enumerable.Range((id - 1) * 3 + 1, 3).ToArray()
                    : Array.Empty<int>();
            if (string.Equals(parts[0], "Hydraulic", StringComparison.OrdinalIgnoreCase))
                return id >= 1 && id <= 2
                    ? Enumerable.Range((id - 1) * 6 + 1, 6).ToArray()
                    : Array.Empty<int>();
            if (string.Equals(parts[0], "DAQ", StringComparison.OrdinalIgnoreCase))
                return id >= 1 && id <= 2
                    ? Enumerable.Range((id - 1) * 6 + 1, 6).ToArray()
                    : Array.Empty<int>();
            return Array.Empty<int>();
        }

        private EngineRunCheckpoint TryUpdateCheckpoint(
            Func<EngineRunCheckpoint, bool> mutation,
            string reason)
        {
            try { return _checkpointStore?.Update(_identity, mutation, reason); }
            catch { return null; }
        }

        private static EngineHardwareCommandResult Failed(string detail)
        {
            return new EngineHardwareCommandResult
            {
                Succeeded = false,
                Detail = detail ?? "EngineCommandFailed"
            };
        }

        private static EngineHardwareCommandResult FromSafety(
            StopSafetyResult result)
        {
            return new EngineHardwareCommandResult
            {
                Succeeded = result?.FullyConfirmed == true,
                Detail = result == null
                    ? "StopSafetyResultMissing"
                    : $"Outcome={result.Outcome};Physical={result.PhysicalSafetyConfirmed};" +
                      $"Persistence={result.PersistenceBoundaryConfirmed};" +
                      $"Logical={result.LogicalQuiescenceConfirmed}",
                OutputsOff = result?.MotorOffCommandSucceeded == true &&
                             result.PowerOffConfirmed,
                PressureSafe = result?.PressureSafeConfirmed == true,
                DataBoundaryClosed = result?.PersistenceBoundaryConfirmed == true &&
                                     !result.DataContinuityCompromised,
                ExecutionAuthorizationRevoked =
                    result?.LogicalQuiescenceConfirmed == true,
                LogicalQuiescent = result?.LogicalQuiescenceConfirmed == true
            };
        }

        public EngineTelemetryFrame CaptureTelemetry(long sequence)
        {
            var currents = new double[12];
            var pressures = new double[2];
            if (_acquirer != null)
            {
                for (var channel = 1; channel <= 12; channel++)
                    currents[channel - 1] = _acquirer.ReadCurrentFiltered(channel);
                pressures[0] = _acquirer.ReadPressureFiltered(1);
                pressures[1] = _acquirer.ReadPressureFiltered(2);
            }
            // The pulse never performs file/DPAPI/SQLite I/O or waits for the
            // background progress writer's lock.
            var checkpoint = _checkpointStore?.Snapshot();
            return new EngineTelemetryFrame
            {
                Sequence = sequence,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                Currents = currents,
                Pressures = pressures,
                FormalCycleCounts = checkpoint?.FormalCyclesCompleted ?? new int[12],
                QualificationCyclesCompleted =
                    checkpoint?.ProgressSchemaVersion == 1 ? checkpoint.QualificationCyclesCompleted : 0,
                FormalCyclesSinceRecovery =
                    checkpoint?.ProgressSchemaVersion == 1 ? checkpoint.FormalCyclesSinceRecovery : 0,
                StableSinceUtcTicks = checkpoint?.ProgressSchemaVersion == 1 ? checkpoint.StableSinceUtcTicks : 0
            };
        }

        private void OnSystemFaultRaised(ControlFault fault)
        {
            if (fault == null || _identity == null) return;
            var resource = ResolveResource(fault, out var kind, out var proven);
            var identity = _identity.Clone();
            identity.IncidentId = fault.CorrelationId == Guid.Empty
                ? RecoveryProtocolV7.NewId()
                : fault.CorrelationId.ToString("N");
            identity.ResourceScope = resource;
            FaultObserved?.Invoke(new FaultObservation
            {
                ObservationId = RecoveryProtocolV7.NewId(),
                Identity = identity,
                ResourceKind = kind,
                ResourceId = resource,
                FaultCode = string.IsNullOrWhiteSpace(fault.Code) ? "Unknown" : fault.Code,
                ObservableProperty = "ControllerFaultObservation",
                Detail = fault.Reason,
                Severity = FaultSeverity.RecoveryRequired,
                ScopeProven = proven,
                HardwareConfirmed = fault.Classification == FaultClassification.HardwareConfirmed,
                OutputOffConfirmed = false,
                PressureSafe = false,
                DataBoundaryClosed = false,
                SafetyChainHealthy = true,
                ObservedUtcTicks = fault.TimestampUtc.Ticks
            });
        }

        private static string ResolveResource(
            ControlFault fault,
            out ResourceKind kind,
            out bool proven)
        {
            proven = true;
            switch (fault.Scope)
            {
                case FaultScope.Channel:
                    kind = ResourceKind.Channel;
                    if (fault.AffectedChannels?.Length > 0)
                        return "Channel:" + fault.AffectedChannels[0];
                    break;
                case FaultScope.ElectricalGroup:
                    kind = ResourceKind.ElectricalGroup;
                    if (fault.GroupId > 0) return "Power:" + fault.GroupId;
                    break;
                case FaultScope.HydraulicGroup:
                    kind = ResourceKind.HydraulicGroup;
                    if (fault.GroupId > 0) return "Hydraulic:" + fault.GroupId;
                    break;
                case FaultScope.DaqGroup:
                    kind = ResourceKind.DaqDevice;
                    if (fault.GroupId > 0) return "DAQ:Dev" + fault.GroupId;
                    break;
                default:
                    kind = ResourceKind.System;
                    proven = false;
                    return "System";
            }
            kind = ResourceKind.System;
            proven = false;
            return "System";
        }

        public void Dispose()
        {
            Initialized = false;
            DisposeHardware();
        }

        public Task ExecutePanelCommandAsync(OperatorCommand command, CancellationToken token)
        {
            var panel = _alarmPanel;
            if (panel == null) throw new InvalidOperationException("AlarmPanelUnavailable");
            return panel.ExecutePanelCommandAsync(command, token);
        }

        private void DisposeHardware(bool preserveAlarmPanel = false)
        {
            _pressureMaintenance?.Dispose(); // Join monitor and actual emergency I/O before disposing NI.
            _manualBatch.Invalidate();
            Initialized = false;
            try { if (_manager != null) _manager.SystemFaultRaised -= OnSystemFaultRaised; }
            catch { }
            try { if (_manager != null) _manager.PowerSupplyTelemetryUpdated -= OnUiPowerTelemetry; }
            catch { }
            _uiPower.Clear();
            try { if (_acquirer != null) _acquirer.OnEngBatch -= OnUiEngineeringBatch; } catch { }
            _uiCurves = new EngineUiCurveBuffer();
            _uiForce = new UiMeasurement();
            try { _manager?.ReleaseHardwareForRestart(); } catch { }
            // Partial composition may not have transferred ownership to EpbManager.
            // These calls are idempotent, but their return alone is not evidence of exit.
            _acquirer?.Dispose();
            _ao?.Dispose();
            _do?.Dispose();
            HardwareReleaseBarrier.RequireReleasedAsync(() =>
            {
                var evidence = new List<HardwareReleaseSnapshot>();
                if (_manager != null) evidence.Add(_manager.CaptureHardwareReleaseForHost());
                else
                {
                    if (_acquirer != null) evidence.Add(_acquirer.CaptureReleaseEvidence());
                    if (_ao != null) evidence.Add(_ao.CaptureReleaseEvidence());
                    if (_do != null) evidence.Add(_do.CaptureReleaseEvidence());
                }
                return evidence.ToArray();
            }, 5000, CancellationToken.None).GetAwaiter().GetResult();
            // Keep all old owner references, publishers and persistence until the NI
            // callbacks actually exit. Timeout must not permit in-process recomposition.
            if (_manager != null)
            {
                _manager.ChannelRuntimeStateChanged -= OnProgressChannelState;
                _manager.ChannelCycleCompleted -= OnProgressFormalCompleted;
                _manager.ChannelMechanicalCycleCompleted -= OnProgressMechanicalCompleted;
            }
            if (_progressPublisher != null && !_progressPublisher.StopAsync(10000).GetAwaiter().GetResult())
                throw new IOException("ProgressPublisherStillOwnsProject;ProcessReplacementRequired");
            _progressPublisher = null; _runTimeClock = null;
            if (_rawPersistence != null)
            {
                if (!_rawPersistence.StopAfterAcquisitionAsync(10000).GetAwaiter().GetResult())
                    throw new IOException("RawPersistenceStillOwnsProject;ProcessReplacementRequired");
                if (_acquirer != null) _acquirer.OwnedRawBatchReady -= _rawPersistence.Enqueue;
                _rawPersistence = null;
            }
            if (_manager != null && _projectPersistence != null &&
                !_manager.ReleasePersistenceForHostAsync(10000).GetAwaiter().GetResult())
                throw new IOException("PersistenceWorkerStillOwnsProject;ProcessReplacementRequired");
            if (_manager != null) _manager.Recorder = null;
            _manager = null;
            if (!preserveAlarmPanel)
            {
                var panel = _alarmPanel;
                _alarmPanel = null;
                try { panel?.Dispose(); } catch { }
            }
            _acquirer = null;
            _ao = null;
            _do = null;
            _projectPersistence?.Dispose();
            _projectPersistence = null;
            _config = null;
            _pressureMaintenance = null;
            _maintenanceDataBoundaryClosed = false;
        }
    }

    internal sealed partial class SimulatedEngineHardwareRuntime : IEngineHardwareRuntime, IEngineUiSource, IEngineAlarmPanel, IEngineManualChannelState, IEngineSafetyHandoff, IEngineProjectState
    {
        private readonly EngineManualBatchContinuation _manualBatch = new EngineManualBatchContinuation();
        private readonly SupervisedFormalContinuation _formalContinuation = new SupervisedFormalContinuation();
        private int _qualificationCycles;
        private long _stableSince;
        private volatile bool _batchRunning;
        private string[] _isolatedResources = Array.Empty<string>();
        public EngineProjectActivation ProjectActivation => null;
        public string[] ProjectIsolatedResources => _isolatedResources.ToArray();
        public int ChannelPauseMask => _batchRunning ? 4095 & ~_manualBatch.ChannelMask : 0;
        public int ChannelResumeMask => _batchRunning ? _manualBatch.ChannelMask : 0;
        private readonly object _panelGate = new object();
        private readonly string _panelInstance = RecoveryProtocolV7.NewId();
        private long _panelRevision = 1;
        private bool _buzzerEnabled = true;
        public event Action<FaultObservation> FaultObserved;
        public bool Initialized { get; private set; }

        public Task<(bool Succeeded, string Detail)> InitializeSafeIdleAsync(
            RecoveryIdentity identity,
            CancellationToken token)
        {
            Initialized = true;
            return Task.FromResult((true, "SimulatedSafeIdle"));
        }

        public bool HardwareRecompositionReady { get; private set; }

        public Task PrepareSafetyHandoffAsync(RecoveryCommand command, EngineHardwareCommandResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_batchRunning || _qualificationCycles != 0) throw new InvalidOperationException("SimulatedHardwareStillOwned");
            _maintenanceExecutor?.Dispose();
            _maintenanceExecutor = null;
            Initialized = false;
            HardwareRecompositionReady = true;
            result.LogicalQuiescent = true;
            result.NativeResourcesReleased = true;
            result.CallbacksIsolated = true;
            return Task.CompletedTask;
        }

        public async Task<EngineHardwareCommandResult> ExecuteAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            if (command.Kind == RecoveryCommandKind.RunPassivePreflight || command.Kind == RecoveryCommandKind.RebuildResource)
            {
                if (_maintenanceExecutor != null) throw new InvalidOperationException("SimulatedMaintenanceOwnsHardware");
            }
            if (PressureMaintenanceProtocol.IsExecution(command.Kind))
                return await ExecuteSimulatedMaintenanceAsync(command, token).ConfigureAwait(false);
            if (_maintenanceExecutor != null)
            {
                if (!EngineHostProtocol.IsPrioritySafetyCommand(command.Kind)) throw new InvalidOperationException("SimulatedMaintenanceOwnsHardware");
                var off = await _maintenanceExecutor.StopForHandoffAsync().ConfigureAwait(false);
                return new EngineHardwareCommandResult { Succeeded = off, LogicalQuiescent = off, ExecutionAuthorizationRevoked = off,
                    DataBoundaryClosed = true, Detail = "SimulatedMaintenanceOff" };
            }
            if (command.Kind == RecoveryCommandKind.RunPassivePreflight || command.Kind == RecoveryCommandKind.RebuildResource)
            { Initialized = true; HardwareRecompositionReady = false; }
            if (command.Kind == RecoveryCommandKind.PauseChannelGracefully)
                await _manualBatch.PauseAsync(command, cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if ((ChannelPauseMask & (1 << (command.OperatorTransaction.ManualBatch.Channel - 1))) == 0)
                        throw new InvalidOperationException("SimulatedChannelNotRunning");
                    return Task.CompletedTask;
                }, _ => Task.CompletedTask, token).ConfigureAwait(false);
            else if (command.Kind == RecoveryCommandKind.ResumePausedChannel)
                await _manualBatch.ResumeAsync(command, _ => Task.CompletedTask, _ => Task.CompletedTask, token).ConfigureAwait(false);
            else if (command.Kind == RecoveryCommandKind.PauseBatchGracefully)
                await _manualBatch.PauseAsync(command, cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!_batchRunning) throw new InvalidOperationException("SimulatedBatchNotRunning");
                    _batchRunning = false; return Task.CompletedTask;
                }, _ => Task.CompletedTask, token).ConfigureAwait(false);
            else if (command.Kind == RecoveryCommandKind.ResumePausedBatch)
                await _manualBatch.ResumeAsync(command, cancellation =>
                { cancellation.ThrowIfCancellationRequested(); _batchRunning = true; return Task.CompletedTask; },
                    _ => Task.CompletedTask, token).ConfigureAwait(false);
            else if (EngineHostProtocol.IsPrioritySafetyCommand(command.Kind) || command.Kind == RecoveryCommandKind.RebuildResource)
            { _manualBatch.Invalidate(); _formalContinuation.Invalidate(); _batchRunning = false; _qualificationCycles = 0; _stableSince = 0; }
            else if (command.Kind == RecoveryCommandKind.RunQualificationCycle)
            {
                if (_batchRunning) throw new InvalidOperationException("SimulatedBatchAlreadyRunning");
                var version = _formalContinuation.BeginQualification(command);
                token.ThrowIfCancellationRequested();
                _qualificationCycles = 2; _stableSince = 0;
                _formalContinuation.CompleteQualification(version, (validate, cancellation) =>
                {
                    validate(); _batchRunning = true; _stableSince = DateTime.UtcNow.Ticks;
                    return Task.FromResult(Enumerable.Range(1, 12).ToArray());
                });
            }
            else if (command.Kind == RecoveryCommandKind.ResumeFormalRun)
            {
                await _formalContinuation.CommitAsync(command, token).ConfigureAwait(false);
                if (command.OperatorTransaction?.Kind == OperatorCommandKind.RetryQualification)
                    _isolatedResources = _isolatedResources.Where(value =>
                        !string.Equals(value, command.TargetResource, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            else if (command.Kind == RecoveryCommandKind.IsolateResource)
                _isolatedResources = _isolatedResources.Concat(new[] { command.TargetResource })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (command.Kind == RecoveryCommandKind.PauseBatchGracefully) _stableSince = 0;
            if (command.Kind == RecoveryCommandKind.ResumePausedBatch) _stableSince = DateTime.UtcNow.Ticks;
            return new EngineHardwareCommandResult
            {
                Succeeded = true,
                Detail = "Simulated:" + command.Kind,
                OutputsOff = !_batchRunning,
                PressureSafe = true,
                DataBoundaryClosed = true,
                ExecutionAuthorizationRevoked = !_batchRunning && _qualificationCycles == 0 &&
                    !ManualBatchCommand.IsOperation(command.OperatorTransaction?.Kind ?? OperatorCommandKind.None),
                QualificationCyclesCompleted = _qualificationCycles,
                StableSinceUtcTicks = _stableSince
            };
        }

        public EngineTelemetryFrame CaptureTelemetry(long sequence)
        {
            return new EngineTelemetryFrame
            {
                Sequence = sequence,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                Currents = new double[12],
                Pressures = new double[2],
                FormalCycleCounts = new int[12],
                QualificationCyclesCompleted = _qualificationCycles,
                StableSinceUtcTicks = _stableSince
            };
        }

        public void Dispose()
        {
            _formalContinuation.Invalidate();
            _maintenanceExecutor?.Dispose();
            _maintenanceExecutor = null;
            Initialized = false;
        }

        public EngineUiSnapshot CaptureUiSnapshot()
        {
            var result = EngineUiSnapshotFactory.Empty("隔离模拟器：未连接现场硬件");
            lock (_panelGate) result.AlarmPanel = new AlarmPanelStatus { Available = true, PanelInstanceId = _panelInstance,
                Revision = _panelRevision, BuzzerEnabled = _buzzerEnabled, Detail = "隔离模拟报警板" };
            result.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.AlarmCommands, EngineUiContract.ManualBatchControl,
                EngineUiContract.ManualChannelControl, EngineUiContract.ChannelQualificationRecovery };
            foreach (var channel in result.Channels)
            {
                channel.Selected = true;
                channel.Isolated = _isolatedResources.Contains("Channel:" + channel.Channel, StringComparer.OrdinalIgnoreCase);
                channel.Running = (ChannelPauseMask & (1 << (channel.Channel - 1))) != 0;
                channel.State = channel.Running ? "运行（模拟）" : (ChannelResumeMask & (1 << (channel.Channel - 1))) != 0 ? "暂停（模拟）" : "待机（模拟）";
            }
            result.BatchPauseAvailable = _batchRunning;
            result.BatchResumeAvailable = _manualBatch.IsPaused;
            return result;
        }

        public Task ExecutePanelCommandAsync(OperatorCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_panelGate)
            {
                if (command.AlarmPanel.PanelInstanceId != _panelInstance || command.AlarmPanel.BaseRevision != _panelRevision)
                    throw new InvalidOperationException("AlarmPanelRevisionConflict");
                if (command.Kind == OperatorCommandKind.SetBuzzerEnabled) _buzzerEnabled = command.AlarmPanel.BuzzerEnabled;
                _panelRevision++;
            }
            return Task.CompletedTask;
        }
    }
}
