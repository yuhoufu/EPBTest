using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal interface IEnginePressureMaintenanceRuntime
    {
        void BindMaintenanceAuthority(EnginePressureMaintenanceLeaseFence authority);
        bool MaintenanceMayBeEnergized { get; }
        EngineUiPressureMaintenance CaptureMaintenanceDisplay();
    }

    internal sealed partial class PhysicalEngineRuntime : IEnginePressureMaintenanceRuntime
    {
        private EnginePressureMaintenanceLeaseFence _maintenanceAuthority;
        private EnginePressureMaintenanceExecutor _pressureMaintenance;
        private bool _maintenanceDataBoundaryClosed;
        public bool MaintenanceMayBeEnergized => _pressureMaintenance?.MayBeEnergized == true;
        public EngineUiPressureMaintenance CaptureMaintenanceDisplay() => _pressureMaintenance?.CaptureDisplay();

        public void BindMaintenanceAuthority(EnginePressureMaintenanceLeaseFence authority)
        {
            if (_maintenanceAuthority != null) throw new InvalidOperationException("MaintenanceAuthorityAlreadyBound");
            _maintenanceAuthority = authority ?? throw new ArgumentNullException(nameof(authority));
        }

        private async Task<EngineHardwareCommandResult> ExecutePressureMaintenanceAsync(RecoveryCommand command, CancellationToken token)
        {
            if (_maintenanceAuthority == null) return Failed("MaintenanceAuthorityUnavailable");
            if (command.Kind != RecoveryCommandKind.StopMaintenanceOutput)
                _maintenanceAuthority.RequireForCommand(command);
            if (command.Kind == RecoveryCommandKind.PreparePressureMaintenance)
            {
                if (!HardwareRecompositionReady || _manager != null || _do != null || _ao != null || _acquirer != null ||
                    _progressPublisher != null || _projectPersistence != null || _rawPersistence != null || _pressureMaintenance != null)
                    return Failed("MaintenanceRequiresExclusiveReleasedHardwareAndWriters");
                _identity = command.Identity.Clone();
                _maintenanceDataBoundaryClosed = true; // Prior joined handoff; no maintenance writer is created below.
                _releasedForSafety = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var selected = _projectSelectionStore.ReadActive(_identity);
                    if (selected == null) throw new InvalidOperationException("MaintenanceProjectSelectionMissing");
                    _config = ConfigLoader.LoadAllForEngine(RuntimeConfigPaths.Directory, selected.ConfigurationPath, _log);
                    _aoConfiguration = _aoConfigurationStore.Read();
                    _config.AO = EngineAoConfigurationStore.ToHardwareConfiguration(_aoConfiguration.Configuration);
                    var hydraulic = _config.Test.Hydraulics?.SingleOrDefault(item => item.Id == command.PressureMaintenance.HydraulicId);
                    if (hydraulic == null) throw new InvalidOperationException("MaintenanceHydraulicConfigurationMissing");
                    var adapter = new MaintenanceHardwareAdapter(this, _config.AO, hydraulic);
                    adapter.ValidateConfiguration();
                    token.ThrowIfCancellationRequested();
                    _do = new DoController(_config.DO, _log);
                    if (!_do.Initialize() || !_do.AllOff()) throw new InvalidOperationException("MaintenanceDoInitialOffFailed");
                    token.ThrowIfCancellationRequested();
                    _ao = new AoController(_config.AO, _log, initializeWithZeroVoltage: true);
                    if (!_ao.TryWriteZeroVoltageAll()) throw new InvalidOperationException("MaintenanceAoInitialOffFailed");
                    token.ThrowIfCancellationRequested();
                    var settings = DaqRuntimeSettings.Load(System.Configuration.ConfigurationManager.AppSettings);
                    _daqConfiguration = _daqConfigurationStore.Read();
                    var ai = EngineDaqConfigurationStore.ToHardwareConfiguration(_daqConfiguration.DaqConfiguration);
                    ConfigureUiRoutes(ai, settings.SampleRateHz);
                    _acquirer = new TwoDeviceAiAcquirer(ai, settings.SampleRateHz, settings.SamplesPerChannel, 10, _log);
                    _acquirer.OnEngBatch += OnUiEngineeringBatch;
                    _acquirer.Start();
                    token.ThrowIfCancellationRequested();
                    _pressureMaintenance = new EnginePressureMaintenanceExecutor(_maintenanceAuthority, adapter, command,
                        reason => PublishMaintenanceFault(command.Identity, reason));
                    var receipt = await _pressureMaintenance.PrepareAsync(token).ConfigureAwait(false);
                    return MaintenanceResult(receipt, "MaintenancePrepared;NoFormalOrQualificationCycles");
                }
                catch
                {
                    _maintenanceAuthority.Invalidate();
                    // Keep partial resource references until the kernel's joined handoff.
                    // A failed constructor/SDK call must not become a false release proof.
                    await MaintenanceAllOffAsync().ConfigureAwait(false);
                    throw;
                }
            }
            if (_pressureMaintenance == null) return Failed("MaintenanceNotPrepared");
            var result = command.Kind == RecoveryCommandKind.SetMaintenancePressure
                ? await _pressureMaintenance.OutputAsync(command, token).ConfigureAwait(false)
                : await _pressureMaintenance.StopOutputAsync(command, token).ConfigureAwait(false);
            return MaintenanceResult(result, command.Kind + ";NoFormalOrQualificationCycles");
        }

        private EngineHardwareCommandResult MaintenanceResult(PressureMaintenanceExecutionReceipt receipt, string detail) =>
            new EngineHardwareCommandResult { Succeeded = true, Detail = detail, PressureMaintenance = receipt,
                // Output writes are not independent physical proof. All cycle increments stay zero.
                DataBoundaryClosed = _maintenanceDataBoundaryClosed, LogicalQuiescent = !receipt.OutputActive,
                ExecutionAuthorizationRevoked = !receipt.OutputActive };

        private async Task<EngineHardwareCommandResult> StopMaintenanceForHandoffAsync()
        {
            var off = _pressureMaintenance != null ? await _pressureMaintenance.StopForHandoffAsync().ConfigureAwait(false)
                : await MaintenanceAllOffAsync().ConfigureAwait(false);
            return new EngineHardwareCommandResult { Succeeded = off, Detail = off ? "MaintenanceOffWritten;IndependentProofRequired" : "MaintenanceOffUnconfirmed",
                DataBoundaryClosed = _maintenanceDataBoundaryClosed && _manager == null && _projectPersistence == null && _rawPersistence == null && _progressPublisher == null,
                LogicalQuiescent = off, ExecutionAuthorizationRevoked = off };
        }

        private Task<bool> MaintenanceAllOffAsync() => Task.Run(() =>
        {
            var off = true;
            try { if (_do != null) off &= _do.AllOff(); } catch { off = false; }
            try { if (_ao != null) off &= _ao.TryWriteZeroVoltageAll(); } catch { off = false; }
            return off;
        });

        private void PublishMaintenanceFault(RecoveryIdentity identity, string reason)
        {
            FaultObserved?.Invoke(new FaultObservation { ObservationId = RecoveryProtocolV7.NewId(), Identity = identity.Clone(),
                ResourceKind = ResourceKind.System, ResourceId = "System", FaultCode = "PressureMaintenanceSafety",
                ObservableProperty = reason, Detail = "Maintenance output authority withdrawn; independent physical safety proof required.",
                Severity = FaultSeverity.SafetyCritical, ScopeProven = false, SafetyChainHealthy = true, ObservedUtcTicks = DateTime.UtcNow.Ticks });
        }

        private sealed class MaintenanceHardwareAdapter : IPressureMaintenanceExecutorHardware
        {
            private readonly PhysicalEngineRuntime _owner;
            private readonly AoConfig _aoConfig;
            private readonly HydraulicItem _hydraulic;
            private readonly string _device;
            internal MaintenanceHardwareAdapter(PhysicalEngineRuntime owner, AoConfig ao, HydraulicItem hydraulic)
            { _owner = owner; _aoConfig = ao; _hydraulic = hydraulic; _device = "Cylinder" + hydraulic.Id; }
            public double MaximumPressureBar => Math.Min(PressureMaintenanceProtocol.MaximumPressureBar, _aoConfig.MaxPressure);
            public double ReleaseSafePressureBar => _hydraulic.ReleaseSafePressureBar;
            public int SampleMaximumAgeMilliseconds => _hydraulic.PressureSampleMaxAgeMs;
            internal void ValidateConfiguration()
            {
                if (_aoConfig == null || !EngineUiContract.IsFinite(_aoConfig.MinVoltage) || !EngineUiContract.IsFinite(_aoConfig.MaxVoltage) ||
                    _aoConfig.MinVoltage < -10 || _aoConfig.MinVoltage > 0 || _aoConfig.MaxVoltage < 0 || _aoConfig.MaxVoltage > 10 || _aoConfig.MaxVoltage <= _aoConfig.MinVoltage ||
                    !EngineUiContract.IsFinite(_aoConfig.MinPressure) || _aoConfig.MinPressure > 0 ||
                    !EngineUiContract.IsFinite(_aoConfig.MaxPressure) || _aoConfig.MaxPressure <= 0 ||
                    !EngineUiContract.IsFinite(ReleaseSafePressureBar) || ReleaseSafePressureBar < 0 || ReleaseSafePressureBar > MaximumPressureBar ||
                    SampleMaximumAgeMilliseconds <= 0 || SampleMaximumAgeMilliseconds > 3000)
                    throw new InvalidOperationException("MaintenanceLimitsInvalid");
                foreach (var name in new[] { "Cylinder1", "Cylinder2" })
                {
                    if (!_aoConfig.Devices.ContainsKey(name)) throw new InvalidOperationException("MaintenanceAoDeviceMissing:" + name);
                }
                foreach (var entry in _aoConfig.Devices)
                {
                    var device = entry.Value;
                    if (device == null || !EngineUiContract.IsFinite(device.ScaleK) || device.ScaleK <= 0 ||
                        !EngineUiContract.IsFinite(device.Offset))
                        throw new InvalidOperationException("MaintenanceAoCalibrationInvalid:" + entry.Key);
                }
            }
            private bool VoltageValid(double voltage) => EngineUiContract.IsFinite(voltage) && voltage >= _aoConfig.MinVoltage && voltage <= _aoConfig.MaxVoltage;
            public PressureMaintenanceWriteResult Evaluate(double pressureBar)
            {
                var device = _aoConfig.Devices[_device]; var voltage = pressureBar == 0 ? 0 : (pressureBar - device.Offset) / device.ScaleK;
                return new PressureMaintenanceWriteResult { PressureBar = pressureBar, Voltage = voltage,
                    Succeeded = EngineUiContract.IsFinite(pressureBar) && pressureBar >= 0 && pressureBar >= _aoConfig.MinPressure && pressureBar <= MaximumPressureBar && VoltageValid(voltage) };
            }
            public UiMeasurement ReadPressure()
            {
                var acquirer = _owner._acquirer;
                if (acquirer == null) return new UiMeasurement();
                var sample = acquirer.ReadPressureSample(_hydraulic.Id);
                return new UiMeasurement { Valid = sample.IsFinite && sample.AgeMs >= 0 && sample.AgeMs <= SampleMaximumAgeMilliseconds,
                    Value = sample.IsFinite ? sample.ValueBar : 0, CapturedUtcTicks = sample.TimestampUtc.Ticks, Reason = "MaintenancePressureSample" };
            }
            public bool EnablePressure() => _owner._do?.SetPressure(_hydraulic.Id, true) == true;
            public PressureMaintenanceWriteResult WritePressure(double pressureBar)
            {
                var expected = Evaluate(pressureBar); if (!expected.Succeeded) return expected;
                var result = _owner._ao.WritePressureDetailed(_device, pressureBar);
                return new PressureMaintenanceWriteResult { Succeeded = result.Success && VoltageValid(result.Voltage),
                    PressureBar = result.CommandPressureBar, Voltage = result.Voltage };
            }
            public bool AllOff()
            {
                var off = true;
                try { off &= _owner._do?.AllOff() == true; } catch { off = false; }
                try { off &= _owner._ao?.TryWriteZeroVoltageAll() == true; } catch { off = false; }
                return off;
            }
        }
    }
}
