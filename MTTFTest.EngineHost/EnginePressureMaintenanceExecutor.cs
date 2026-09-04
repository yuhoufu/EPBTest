using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class PressureMaintenanceWriteResult
    {
        internal bool Succeeded;
        internal double PressureBar;
        internal double Voltage;
    }

    internal interface IPressureMaintenanceExecutorHardware
    {
        // Evaluate is memory-only and must validate the configured pressure/voltage limits.
        PressureMaintenanceWriteResult Evaluate(double pressureBar);
        UiMeasurement ReadPressure();
        bool EnablePressure();
        PressureMaintenanceWriteResult WritePressure(double pressureBar);
        bool AllOff();
        double MaximumPressureBar { get; }
        double ReleaseSafePressureBar { get; }
        int SampleMaximumAgeMilliseconds { get; }
    }

    // Executes only the existing kernel's maintenance intent. The local monitor
    // can withdraw output under that lease, but cannot restart or allocate an Owner.
    internal sealed class EnginePressureMaintenanceExecutor : IDisposable
    {
        private readonly EnginePressureMaintenanceLeaseFence _authority;
        private readonly IPressureMaintenanceExecutorHardware _hardware;
        private readonly RecoveryCommand _prepare;
        private readonly Action<string> _fault;
        private readonly ManualResetEvent _stopMonitor = new ManualResetEvent(false);
        private readonly object _emergencyGate = new object();
        private Task _emergencyOff = Task.CompletedTask;
        private Task<bool> _disposeOff;
        private Thread _monitor;
        private EngineUiPressureMaintenance _lastDisplay;
        private int _tripped, _prepared, _disposed, _mayBeEnergized, _outputGeneration;
        internal bool MayBeEnergized => Volatile.Read(ref _mayBeEnergized) != 0;
        internal bool Prepared => Volatile.Read(ref _prepared) != 0;

        internal EngineUiPressureMaintenance CaptureDisplay()
        {
            var lease = _authority.CaptureAuthorized(_prepare.Identity.IncidentId, _prepare.OwnerId);
            var last = Volatile.Read(ref _lastDisplay);
            var generation = Volatile.Read(ref _outputGeneration);
            var mayBeEnergized = MayBeEnergized;
            var active = lease != null && last?.OutputActive == true && last.OutputGeneration == generation && mayBeEnergized;
            UiMeasurement sample;
            try { sample = _hardware.ReadPressure(); } catch { sample = new UiMeasurement { Reason = "维护压力读取失败" }; }
            return new EngineUiPressureMaintenance { Identity = _prepare.Identity.Clone(), OwnerId = _prepare.OwnerId,
                EngineInstanceId = _prepare.PressureMaintenance.EngineInstanceId, HydraulicId = _prepare.PressureMaintenance.HydraulicId,
                CapturedUtcTicks = DateTime.UtcNow.Ticks, LeaseRevision = lease?.Revision ?? 0, AuthorityValid = lease != null,
                Prepared = last != null && lease != null, OutputActive = active, MayBeEnergized = mayBeEnergized,
                OutputGeneration = generation, OutputCommandId = active ? last.OutputCommandId : string.Empty,
                PressureSampleMaximumAgeMilliseconds = _hardware.SampleMaximumAgeMilliseconds,
                CommandPressureBar = active ? last.CommandPressureBar : 0, Voltage = active ? last.Voltage : 0,
                Pressure = SampleValid(sample) ? new UiMeasurement { Valid = true, Value = sample.Value, CapturedUtcTicks = sample.CapturedUtcTicks } :
                    new UiMeasurement { Reason = "维护压力未就绪、已过期或超限" } };
        }

        internal EnginePressureMaintenanceExecutor(EnginePressureMaintenanceLeaseFence authority,
            IPressureMaintenanceExecutorHardware hardware, RecoveryCommand prepare, Action<string> fault)
        {
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
            _fault = fault ?? throw new ArgumentNullException(nameof(fault));
            if (!EngineUiContract.IsFinite(hardware.MaximumPressureBar) || hardware.MaximumPressureBar <= 0 ||
                !EngineUiContract.IsFinite(hardware.ReleaseSafePressureBar) || hardware.ReleaseSafePressureBar < 0 ||
                hardware.ReleaseSafePressureBar > hardware.MaximumPressureBar || hardware.SampleMaximumAgeMilliseconds <= 0 ||
                hardware.SampleMaximumAgeMilliseconds > 3000) throw new InvalidOperationException("MaintenanceHardwareLimitsInvalid");
        }

        internal async Task<PressureMaintenanceExecutionReceipt> PrepareAsync(CancellationToken token)
        {
            _authority.RequireForCommand(_prepare);
            if (Interlocked.CompareExchange(ref _prepared, 1, 0) != 0) throw new InvalidOperationException("MaintenanceAlreadyPrepared");
            try
            {
                ValidateAuthority(token);
                if (!await OffAsync().ConfigureAwait(false)) throw new InvalidOperationException("MaintenanceInitialOffUnconfirmed");
                var end = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    ValidateAuthority(token);
                    var sample = _hardware.ReadPressure();
                    if (SampleValid(sample) && sample.Value <= _hardware.ReleaseSafePressureBar) break;
                    if (DateTime.UtcNow >= end) throw new InvalidOperationException("MaintenanceInitialPressureNotSafe");
                    await Task.Delay(25, token).ConfigureAwait(false);
                }
                _monitor = new Thread(Monitor) { IsBackground = true, Name = "EPB pressure maintenance lease" };
                _monitor.Start();
                return Receipt(_prepare, false, 0, 0);
            }
            catch
            {
                Trip("MaintenancePreparationFailed");
                await OffAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<PressureMaintenanceExecutionReceipt> OutputAsync(RecoveryCommand command, CancellationToken token)
        {
            _authority.RequireForCommand(command);
            var pressure = command.OperatorTransaction.PressureMaintenance.PressureBar;
            var generation = Interlocked.Increment(ref _outputGeneration);
            try
            {
                ValidateAuthority(token); ValidatePressure(pressure);
                // Reject bad calibration BEFORE enabling any DO.
                ValidateWrite(_hardware.Evaluate(pressure), pressure);
                if (!await OffAsync().ConfigureAwait(false)) throw new InvalidOperationException("MaintenancePreviousOffUnconfirmed");
                ValidateOutput(generation, token);
                if (!SampleValid(_hardware.ReadPressure())) throw new InvalidOperationException("MaintenancePressureSampleUnsafe");
                Interlocked.Exchange(ref _mayBeEnergized, 1); // Conservative throughout an in-flight SDK write.
                if (!await Task.Run(() => _hardware.EnablePressure()).ConfigureAwait(false)) throw new InvalidOperationException("MaintenanceDoEnableFailed");
                Interlocked.Exchange(ref _mayBeEnergized, 1);
                ValidateOutput(generation, token);
                var result = await Task.Run(() => _hardware.WritePressure(pressure)).ConfigureAwait(false);
                Interlocked.Exchange(ref _mayBeEnergized, 1);
                ValidateOutput(generation, token); ValidateWrite(result, pressure);
                return Receipt(command, true, pressure, result.Voltage);
            }
            catch (OperationCanceledException)
            {
                if (!await OffAsync().ConfigureAwait(false)) Trip("MaintenanceCancellationOffUnconfirmed");
                throw;
            }
            catch
            {
                Trip("MaintenanceOutputFailed");
                await OffAsync().ConfigureAwait(false);
                throw;
            }
        }

        internal async Task<PressureMaintenanceExecutionReceipt> StopOutputAsync(RecoveryCommand command, CancellationToken token)
        {
            Interlocked.Increment(ref _outputGeneration);
            if (!await OffAsync().ConfigureAwait(false))
            { Trip("MaintenanceStopOutputUnconfirmed"); throw new InvalidOperationException("MaintenanceStopOutputUnconfirmed"); }
            ValidateAuthority(token);
            return Receipt(command, false, 0, 0);
        }

        internal async Task<bool> StopForHandoffAsync()
        {
            _authority.Invalidate();
            Interlocked.Increment(ref _outputGeneration);
            _stopMonitor.Set();
            var off = await OffAsync().ConfigureAwait(false);
            Task emergency; lock (_emergencyGate) emergency = _emergencyOff;
            await emergency.ConfigureAwait(false);
            return off && !MayBeEnergized;
        }

        private void ValidateOutput(int generation, CancellationToken token)
        {
            ValidateAuthority(token);
            if (generation != Volatile.Read(ref _outputGeneration)) throw new OperationCanceledException("MaintenanceOutputSuperseded");
        }
        private void ValidateAuthority(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _tripped) != 0 || !Prepared ||
                !_authority.IsAuthorized(_prepare.Identity.IncidentId, _prepare.OwnerId))
                throw new InvalidOperationException("MaintenanceAuthorityWithdrawn");
        }
        private void ValidatePressure(double pressure)
        {
            if (!EngineUiContract.IsFinite(pressure) || pressure < 0 || pressure > PressureMaintenanceProtocol.MaximumPressureBar ||
                pressure > _hardware.MaximumPressureBar) throw new InvalidOperationException("MaintenancePressureOutOfRange");
        }
        private static void ValidateWrite(PressureMaintenanceWriteResult result, double expected)
        {
            if (result?.Succeeded != true || result.PressureBar != expected || !EngineUiContract.IsFinite(result.Voltage) ||
                result.Voltage < -10 || result.Voltage > 10) throw new InvalidOperationException("MaintenanceAoResultInvalid");
        }
        private bool SampleValid(UiMeasurement sample)
        {
            var now = DateTime.UtcNow.Ticks;
            return sample?.Valid == true && EngineUiContract.IsFinite(sample.Value) && sample.CapturedUtcTicks > 0 &&
                sample.CapturedUtcTicks <= now && now - sample.CapturedUtcTicks <= TimeSpan.FromMilliseconds(_hardware.SampleMaximumAgeMilliseconds).Ticks &&
                sample.Value <= Math.Min(PressureMaintenanceProtocol.MaximumPressureBar, _hardware.MaximumPressureBar);
        }

        private PressureMaintenanceExecutionReceipt Receipt(RecoveryCommand command, bool active, double pressure, double voltage)
        {
            var lease = _authority.RequireForCommand(command);
            Volatile.Write(ref _lastDisplay, new EngineUiPressureMaintenance { OutputActive = active, OutputCommandId = command.CommandId,
                OutputGeneration = Volatile.Read(ref _outputGeneration), CommandPressureBar = pressure, Voltage = voltage });
            return new PressureMaintenanceExecutionReceipt { EngineInstanceId = lease.EngineInstanceId, LeaseRevision = lease.Revision,
                LocalLeaseExpiresUtcTicks = lease.ExpiresUtcTicks, HydraulicId = lease.HydraulicId, OutputActive = active,
                CommandPressureBar = pressure, Voltage = voltage };
        }

        private Task<bool> OffAsync() => Task.Run(WriteOff);

        private bool WriteOff()
        {
            bool off;
            try { off = _hardware.AllOff(); } catch { off = false; }
            if (off) Interlocked.Exchange(ref _mayBeEnergized, 0);
            else Interlocked.Exchange(ref _mayBeEnergized, 1);
            return off;
        }

        private Task EmergencyOffAsync()
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                new Thread(() => completed.TrySetResult(WriteOff()))
                { IsBackground = true, Name = "EPB pressure maintenance emergency off" }.Start();
            }
            catch (Exception ex) { completed.TrySetException(ex); }
            return completed.Task;
        }

        private void Monitor()
        {
            while (!_stopMonitor.WaitOne(50))
            {
                try
                {
                    if (!_authority.IsAuthorized(_prepare.Identity.IncidentId, _prepare.OwnerId))
                    { Trip("MaintenanceLeaseExpiredOrRevoked"); return; }
                    if (MayBeEnergized && !SampleValid(_hardware.ReadPressure()))
                    { Trip("MaintenancePressureObservationUnsafe"); return; }
                }
                catch { Trip("MaintenanceSafetyMonitorFailed"); return; }
            }
        }

        private void Trip(string reason)
        {
            if (Interlocked.Exchange(ref _tripped, 1) != 0) return;
            _authority.Invalidate(); Interlocked.Increment(ref _outputGeneration);
            lock (_emergencyGate) _emergencyOff = EmergencyOffAsync();
            // Publish facts immediately, not after a potentially blocked native OFF.
            try { _fault(reason); } catch { }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Task<bool> finalOff;
            lock (_emergencyGate)
            {
                if (_disposeOff == null || _disposeOff.IsCompleted && (_disposeOff.IsFaulted || _disposeOff.IsCanceled || !_disposeOff.Result))
                    _disposeOff = StopForHandoffAsync();
                finalOff = _disposeOff;
            }
            if (!finalOff.Wait(5000) || !finalOff.Result) throw new InvalidOperationException("MaintenanceFinalOffUnconfirmed");
            _authority.Invalidate(); _stopMonitor.Set();
            if (_monitor != null && !_monitor.Join(5000)) throw new InvalidOperationException("MaintenanceMonitorStillOwnsHardware");
            Task emergency; lock (_emergencyGate) emergency = _emergencyOff;
            if (!emergency.Wait(5000)) throw new InvalidOperationException("MaintenanceEmergencyOffStillOwnsHardware");
            Interlocked.Exchange(ref _disposed, 1);
            _stopMonitor.Dispose();
        }
    }
}
