using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // Explicit --simulation path only. The same lease/executor runs without NI or project writes.
    internal sealed partial class SimulatedEngineHardwareRuntime : IEnginePressureMaintenanceRuntime
    {
        private EnginePressureMaintenanceLeaseFence _maintenanceAuthority;
        private EnginePressureMaintenanceExecutor _maintenanceExecutor;
        public bool MaintenanceMayBeEnergized => _maintenanceExecutor?.MayBeEnergized == true;
        public EngineUiPressureMaintenance CaptureMaintenanceDisplay() => _maintenanceExecutor?.CaptureDisplay();
        public void BindMaintenanceAuthority(EnginePressureMaintenanceLeaseFence authority)
        { _maintenanceAuthority = authority ?? throw new ArgumentNullException(nameof(authority)); }

        private async Task<EngineHardwareCommandResult> ExecuteSimulatedMaintenanceAsync(RecoveryCommand command, CancellationToken token)
        {
            if (command.Kind == RecoveryCommandKind.PreparePressureMaintenance)
            {
                if (!HardwareRecompositionReady || Initialized || _maintenanceExecutor != null || _batchRunning)
                    throw new InvalidOperationException("SimulatedMaintenanceRequiresReleasedHardware");
                HardwareRecompositionReady = false;
                _maintenanceExecutor = new EnginePressureMaintenanceExecutor(_maintenanceAuthority, new SimulatedPressureHardware(), command,
                    reason => FaultObserved?.Invoke(new FaultObservation { ObservationId = RecoveryProtocolV7.NewId(), Identity = command.Identity.Clone(),
                        ResourceKind = ResourceKind.System, ResourceId = "System", FaultCode = "SimulatedMaintenanceFault", ObservableProperty = reason,
                        Detail = "Isolated simulated hardware only", ScopeProven = false, Severity = FaultSeverity.SafetyCritical,
                        SafetyChainHealthy = true, ObservedUtcTicks = DateTime.UtcNow.Ticks }));
                var prepared = await _maintenanceExecutor.PrepareAsync(token).ConfigureAwait(false);
                return new EngineHardwareCommandResult { Succeeded = true, PressureMaintenance = prepared, DataBoundaryClosed = true, Detail = "SimulatedMaintenancePrepared" };
            }
            if (_maintenanceExecutor == null) throw new InvalidOperationException("SimulatedMaintenanceNotPrepared");
            var result = command.Kind == RecoveryCommandKind.SetMaintenancePressure
                ? await _maintenanceExecutor.OutputAsync(command, token).ConfigureAwait(false)
                : await _maintenanceExecutor.StopOutputAsync(command, token).ConfigureAwait(false);
            return new EngineHardwareCommandResult { Succeeded = true, PressureMaintenance = result, DataBoundaryClosed = true, Detail = "SimulatedMaintenanceExecution" };
        }

        private sealed class SimulatedPressureHardware : IPressureMaintenanceExecutorHardware
        {
            private double _pressure;
            public double MaximumPressureBar => 120;
            public double ReleaseSafePressureBar => 5;
            public int SampleMaximumAgeMilliseconds => 100;
            public PressureMaintenanceWriteResult Evaluate(double value) => new PressureMaintenanceWriteResult
            { Succeeded = EngineUiContract.IsFinite(value) && value >= 0 && value <= 120, PressureBar = value, Voltage = value / 20 };
            public UiMeasurement ReadPressure() => new UiMeasurement { Valid = true, Value = Volatile.Read(ref _pressure), CapturedUtcTicks = DateTime.UtcNow.Ticks };
            public bool EnablePressure() => true;
            public PressureMaintenanceWriteResult WritePressure(double value)
            { Interlocked.Exchange(ref _pressure, value); return Evaluate(value); }
            public bool AllOff() { Interlocked.Exchange(ref _pressure, 0); return true; }
        }
    }
}
