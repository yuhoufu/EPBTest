using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using DataOperation;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineHardwareCommandResult
    {
        internal bool Succeeded { get; set; }
        internal string Detail { get; set; } = string.Empty;
        internal bool OutputsOff { get; set; }
        internal bool PressureSafe { get; set; }
        internal bool DataBoundaryClosed { get; set; }
        internal bool ExecutionAuthorizationRevoked { get; set; }
        internal int QualificationCyclesCompleted { get; set; }
        internal int FormalCyclesCompleted { get; set; }
        internal long StableSinceUtcTicks { get; set; }
        internal bool InterruptedCycleCounted { get; set; }
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

    internal sealed class PhysicalEngineRuntime : IEngineHardwareRuntime
    {
        private readonly EngineHostLog _log = new EngineHostLog();
        private RecoveryIdentity _identity;
        private GlobalConfig _config;
        private EpbManager _manager;
        private TwoDeviceAiAcquirer _acquirer;
        private DoController _do;
        private AoController _ao;
        private EngineRunCheckpointStore _checkpointStore;

        public event Action<FaultObservation> FaultObserved;
        public bool Initialized { get; private set; }

        public async Task<(bool Succeeded, string Detail)> InitializeSafeIdleAsync(
            RecoveryIdentity identity,
            CancellationToken token)
        {
            _identity = identity?.Clone() ?? throw new ArgumentNullException(nameof(identity));
            _checkpointStore = new EngineRunCheckpointStore(_identity.SessionId);
            return await ComposeSafeIdleAsync(token).ConfigureAwait(false);
        }

        private async Task<(bool Succeeded, string Detail)> ComposeSafeIdleAsync(
            CancellationToken token)
        {
            try
            {
                if (!RuntimeConfigPaths.Validate(false, out var validation))
                    return (false, "RuntimeConfigInvalid:" + validation);
                var config = ConfigLoader.LoadAll(RuntimeConfigPaths.Directory, _log);
                config.Test = ConfigLoader.EnsureProjectTestConfig(config, _log);
                ConfigLoader.UpdateDefaultTestFromProject(config.Test, _log);
                _config = config;
                _do = new DoController(config.DO, _log);
                _ao = new AoController(config.AO, _log);
                var daq = DaqRuntimeSettings.Load(ConfigurationManager.AppSettings);
                var ai = AiConfigLoader.Load(RuntimeConfigPaths.GetPath("AIConfig.xml"));
                _acquirer = new TwoDeviceAiAcquirer(
                    ai, daq.SampleRateHz, daq.SamplesPerChannel, 10, _log);
                _manager = new EpbManager(config, _do, _ao, _acquirer, _log);
                _manager.SystemFaultRaised += OnSystemFaultRaised;
                _acquirer.Start();
                var safety = await _manager.StopAllAsync(token).ConfigureAwait(false);
                Initialized = safety?.PhysicalSafetyConfirmed == true;
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
            if (_manager == null)
                return Failed("EngineHardwareUnavailable");
            switch (command.Kind)
            {
                case RecoveryCommandKind.DisableOutputs:
                case RecoveryCommandKind.SealActiveCycle:
                case RecoveryCommandKind.EnterSafeIdle:
                case RecoveryCommandKind.StopByOperator:
                    var result = await _manager.StopAllAsync(token).ConfigureAwait(false);
                    if (_checkpointStore != null && _identity != null)
                        TryUpdateCheckpoint(value =>
                        {
                            value.ActiveCycleInvalidated = true;
                            value.State = command.Kind == RecoveryCommandKind.StopByOperator
                                ? "StoppedByOperator"
                                : "SafeIdle";
                            return true;
                        }, command.Kind.ToString());
                    return FromSafety(result);
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
                        checkpoint.QualificationCyclesCompleted >= 2)
                    {
                        checkpoint = _checkpointStore.Update(
                            _identity,
                            value =>
                            {
                                value.State = "Running";
                                if (value.StableSinceUtcTicks <= 0)
                                    value.StableSinceUtcTicks = DateTime.UtcNow.Ticks;
                                return true;
                            },
                            "FormalRunResumed");
                        return new EngineHardwareCommandResult
                        {
                            Succeeded = true,
                            Detail = "FormalRunActive",
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
            var configPath = RuntimeConfigPaths.GetPath("TestConfig.xml");
            if (!File.Exists(configPath)) return Failed("TestConfigMissing");
            var configurationSha256 = SupervisorProtocol.ComputeSha256(configPath);
            var checkpoint = _checkpointStore.LoadOrCreate(
                command.Identity,
                _config.Test,
                configurationSha256);
            var runnable = (checkpoint.SelectedChannels ?? Array.Empty<int>())
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

        private async Task<EngineHardwareCommandResult> RunQualificationAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            var checkpoint = _checkpointStore?.Snapshot();
            if (checkpoint?.IsValidFor(command.Identity) != true)
                return Failed("QualificationCheckpointUnavailable");
            var channels = checkpoint.SelectedChannels
                .Where(channel => checkpoint.RemainingFormalCycles[channel - 1] > 0)
                .ToArray();
            if (channels.Length == 0) return Failed("NoRemainingFormalWork");
            _manager.EpbTestCycle = channels.ToDictionary(
                channel => channel,
                channel => checkpoint.RemainingFormalCycles[channel - 1]);
            var runId = Guid.Parse(command.Identity.RunId);
            var start = await _manager.StartBatchFromGracefulCheckpointAsync(
                    channels,
                    qualificationCycles: 2,
                    new RunChainIdentity(
                        runId,
                        runId,
                        Guid.Empty,
                        (int)Math.Min(int.MaxValue,
                            Math.Max(0, command.Identity.Generation - 1)),
                        command.Identity.RunEpoch),
                    token)
                .ConfigureAwait(false);
            var satisfied = (start?.StartedChannels ?? Array.Empty<int>())
                .Concat(start?.CompletedDuringStartChannels ?? Array.Empty<int>())
                .Distinct()
                .ToArray();
            var missing = channels.Except(satisfied).ToArray();
            if (start == null || start.TestRunId == Guid.Empty ||
                missing.Length > 0 || (start.Faults?.Length ?? 0) > 0)
                return Failed(
                    "QualificationStartIncomplete;Missing=" +
                    string.Join(",", missing) + ";Faults=" +
                    string.Join("|", (start?.Faults ?? Array.Empty<ChannelStartFault>())
                        .Select(fault => "EPB" + fault.Channel + ":" +
                                         fault.Stage + ":" + fault.Reason)));
            checkpoint = _checkpointStore.Update(
                command.Identity,
                value =>
                {
                    value.QualificationCyclesCompleted = Math.Max(
                        value.QualificationCyclesCompleted, 2);
                    value.ActiveCycleInvalidated = false;
                    value.State = "QualifiedFormalStarted";
                    value.StableSinceUtcTicks = DateTime.UtcNow.Ticks;
                    return true;
                },
                "QualificationCompletedFormalStarted");
            return new EngineHardwareCommandResult
            {
                Succeeded = true,
                Detail = "QualificationCompleted;FormalStarted=" +
                         string.Join(",", start.StartedChannels),
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
            var affected = ResolveAffectedChannels(scope);
            foreach (var channel in affected)
            {
                try { _manager.StopChannel(channel); } catch { }
                var record = _config.Test.GetEpbRecord(channel);
                record.Enabled = false;
                record.PermanentAlarmLatched = true;
                record.PermanentAlarmCode = "V3RecoveryKernelIsolation";
                record.PermanentAlarmReason = "RecoveryKernel:" + scope;
                record.PermanentAlarmUtc = DateTime.UtcNow;
            }
            try
            {
                var project = ConfigLoader.GetProjectTestConfigPath(
                    _config.Test.StoreDir, _config.Test.TestName);
                if (!string.IsNullOrWhiteSpace(project))
                    ConfigLoader.SaveTest(project, _config.Test);
                ConfigLoader.UpdateDefaultTestFromProject(_config.Test, _log);
                TryUpdateCheckpoint(value =>
                {
                    value.IsolatedResources = (value.IsolatedResources ?? Array.Empty<string>())
                        .Concat(new[] { scope })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    value.SelectedChannels = value.SelectedChannels
                        .Except(affected)
                        .ToArray();
                    value.State = value.SelectedChannels.Length > 0
                        ? "RunningDegraded"
                        : "SafeIdle";
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

                _manager.EpbTestCycle = remaining.ToDictionary(
                    channel => channel,
                    channel => checkpoint.RemainingFormalCycles[channel - 1]);
                var runId = Guid.Parse(command.Identity.RunId);
                var start = await _manager.StartBatchFromGracefulCheckpointAsync(
                        remaining,
                        qualificationCycles: 2,
                        new RunChainIdentity(
                            runId,
                            runId,
                            Guid.Empty,
                            (int)Math.Min(int.MaxValue,
                                Math.Max(0, command.Identity.Generation - 1)),
                            command.Identity.RunEpoch),
                        token)
                    .ConfigureAwait(false);
                var satisfied = (start?.StartedChannels ?? Array.Empty<int>())
                    .Concat(start?.CompletedDuringStartChannels ?? Array.Empty<int>())
                    .Distinct()
                    .ToArray();
                var missing = remaining.Except(satisfied).ToArray();
                if (start == null || start.TestRunId == Guid.Empty ||
                    missing.Length > 0 || (start.Faults?.Length ?? 0) > 0)
                    return Failed(
                        "HealthyDomainRestartAfterIsolationFailed;Missing=" +
                        string.Join(",", missing));
                checkpoint = _checkpointStore.Update(
                    command.Identity,
                    value =>
                    {
                        value.QualificationCyclesCompleted = Math.Max(
                            value.QualificationCyclesCompleted, 2);
                        value.ActiveCycleInvalidated = false;
                        value.State = "RunningDegraded";
                        value.StableSinceUtcTicks = DateTime.UtcNow.Ticks;
                        return true;
                    },
                    "HealthyDomainsResumedAfterIsolation:" + scope);
                return new EngineHardwareCommandResult
                {
                    Succeeded = true,
                    Detail = "ResourceIsolationPersistedRunningDegraded:" + scope +
                             ";Channels=" + string.Join(",", affected) +
                             ";Healthy=" + string.Join(",", remaining),
                    QualificationCyclesCompleted =
                        checkpoint.QualificationCyclesCompleted,
                    FormalCyclesCompleted = checkpoint.FormalCyclesSinceRecovery,
                    StableSinceUtcTicks = checkpoint.StableSinceUtcTicks
                };
            }
            catch (Exception ex)
            {
                return Failed("ResourceIsolationPersistenceFailed:" +
                              ex.GetBaseException().Message);
            }
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
                    result?.LogicalQuiescenceConfirmed == true
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
            EngineRunCheckpoint checkpoint = null;
            if (_checkpointStore != null && _identity != null && _config?.Test != null)
            {
                try
                {
                    checkpoint = _checkpointStore.RefreshFormalProgress(
                        _identity, _config.Test);
                }
                catch { checkpoint = _checkpointStore.Snapshot(); }
            }
            return new EngineTelemetryFrame
            {
                Sequence = sequence,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                Currents = currents,
                Pressures = pressures,
                FormalCycleCounts = checkpoint?.FormalCyclesCompleted ?? new int[12],
                QualificationCyclesCompleted =
                    checkpoint?.QualificationCyclesCompleted ?? 0,
                FormalCyclesSinceRecovery =
                    checkpoint?.FormalCyclesSinceRecovery ?? 0,
                StableSinceUtcTicks = checkpoint?.StableSinceUtcTicks ?? 0
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

        private void DisposeHardware()
        {
            Initialized = false;
            try { if (_manager != null) _manager.SystemFaultRaised -= OnSystemFaultRaised; }
            catch { }
            try { _manager?.ReleaseHardwareForRestart(); } catch { }
            _manager = null;
            // EpbManager owns these after construction.  The explicit paths
            // cover partial composition failures before manager ownership.
            try { _acquirer?.Dispose(); } catch { }
            try { _ao?.Dispose(); } catch { }
            try { _do?.Dispose(); } catch { }
            _acquirer = null;
            _ao = null;
            _do = null;
            _config = null;
        }
    }

    internal sealed class SimulatedEngineHardwareRuntime : IEngineHardwareRuntime
    {
        public event Action<FaultObservation> FaultObserved;
        public bool Initialized { get; private set; }

        public Task<(bool Succeeded, string Detail)> InitializeSafeIdleAsync(
            RecoveryIdentity identity,
            CancellationToken token)
        {
            Initialized = true;
            return Task.FromResult((true, "SimulatedSafeIdle"));
        }

        public Task<EngineHardwareCommandResult> ExecuteAsync(
            RecoveryCommand command,
            CancellationToken token)
        {
            return Task.FromResult(new EngineHardwareCommandResult
            {
                Succeeded = true,
                Detail = "Simulated:" + command.Kind,
                OutputsOff = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                ExecutionAuthorizationRevoked = true,
                QualificationCyclesCompleted =
                    command.Kind == RecoveryCommandKind.RunQualificationCycle ? 2 : 0,
                StableSinceUtcTicks = DateTime.UtcNow.Ticks
            });
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
                QualificationCyclesCompleted = 2,
                StableSinceUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        public void Dispose()
        {
            Initialized = false;
        }
    }
}
