using System;
using System.Linq;
using System.Threading;
using Config;
using IO.NI;
using AppLogger = Config.IAppLogger;

namespace Controller
{
    /// <summary>气缸压力输出校正所需的最小硬件接口，便于脱离 NI 设备验证安全顺序。</summary>
    public interface IPressureCalibrationHardware : IDisposable
    {
        bool SetPressureDo(int hydraulicId, bool enabled);
        AoWriteResult WritePressure(string deviceName, double pressureBar);
        PressureSample ReadPressureSample(int hydraulicId);
    }

    public readonly struct PressureCalibrationOutputResult
    {
        public PressureCalibrationOutputResult(
            bool success,
            string error,
            int hydraulicId,
            string deviceName,
            double commandPressureBar,
            double voltage)
        {
            Success = success;
            Error = error ?? string.Empty;
            HydraulicId = hydraulicId;
            DeviceName = deviceName ?? string.Empty;
            CommandPressureBar = commandPressureBar;
            Voltage = voltage;
        }

        public bool Success { get; }
        public string Error { get; }
        public int HydraulicId { get; }
        public string DeviceName { get; }
        public double CommandPressureBar { get; }
        public double Voltage { get; }
    }

    public readonly struct PressureCalibrationSafetyResult
    {
        public PressureCalibrationSafetyResult(bool safe, bool tripped, PressureSample sample, string error)
        {
            Safe = safe;
            Tripped = tripped;
            Sample = sample;
            Error = error ?? string.Empty;
        }

        public bool Safe { get; }
        public bool Tripped { get; }
        public PressureSample Sample { get; }
        public string Error { get; }
    }

    /// <summary>
    /// 校正页专用压力输出协调器。任一输出前先撤销全部旧输出；任一失败均回到 DO 关闭、AO 回零。
    /// </summary>
    public sealed class PressureCalibrationCoordinator : IDisposable
    {
        public const double CalibrationPressureLimitBar = 120.0;

        private readonly IPressureCalibrationHardware _hardware;
        private readonly AoConfig _aoConfig;
        private readonly TestConfig _testConfig;
        private readonly AppLogger _log;
        private readonly object _gate = new object();
        private readonly Timer _watchdog;
        private readonly int _watchdogIntervalMs;
        private bool _disposed;

        public PressureCalibrationCoordinator(
            IPressureCalibrationHardware hardware,
            AoConfig aoConfig,
            TestConfig testConfig,
            AppLogger log = null,
            int watchdogIntervalMs = 50)
        {
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            _aoConfig = aoConfig ?? throw new ArgumentNullException(nameof(aoConfig));
            _testConfig = testConfig;
            _log = log ?? Config.NullLogger.Instance;
            _watchdogIntervalMs = Math.Max(0, watchdogIntervalMs);
            if (_watchdogIntervalMs > 0)
                _watchdog = new Timer(WatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public event Action<PressureCalibrationSafetyResult> SafetyTripped;

        public bool IsOutputActive { get; private set; }
        public int ActiveHydraulicId { get; private set; }
        public string ActiveDeviceName { get; private set; } = string.Empty;
        public double ActiveCommandPressureBar { get; private set; }
        public double ActiveVoltage { get; private set; } = double.NaN;

        public double EffectivePressureLimitBar
        {
            get
            {
                var configured = _aoConfig.MaxPressure;
                return CalibrationMath.IsFinite(configured) && configured > 0
                    ? Math.Min(CalibrationPressureLimitBar, configured)
                    : CalibrationPressureLimitBar;
            }
        }

        public static bool TryResolveDevice(string deviceName, out int hydraulicId, out string pressureParameter)
        {
            if (string.Equals(deviceName, "Cylinder1", StringComparison.OrdinalIgnoreCase))
            {
                hydraulicId = 1;
                pressureParameter = "Pressure_1";
                return true;
            }

            if (string.Equals(deviceName, "Cylinder2", StringComparison.OrdinalIgnoreCase))
            {
                hydraulicId = 2;
                pressureParameter = "Pressure_2";
                return true;
            }

            hydraulicId = 0;
            pressureParameter = string.Empty;
            return false;
        }

        public PressureSample ReadPressureSample(int hydraulicId)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _hardware.ReadPressureSample(hydraulicId);
            }
        }

        public PressureCalibrationOutputResult Output(string deviceName, double commandPressureBar)
        {
            lock (_gate)
            {
                return OutputCore(deviceName, commandPressureBar);
            }
        }

        private PressureCalibrationOutputResult OutputCore(string deviceName, double commandPressureBar)
        {
            ThrowIfDisposed();
            if (!TryResolveDevice(deviceName, out var hydraulicId, out _))
                return Failure("未知气缸，拒绝输出。", 0, deviceName, commandPressureBar);
            if (!CalibrationMath.IsFinite(commandPressureBar) || commandPressureBar < 0 ||
                commandPressureBar > EffectivePressureLimitBar)
                return Failure(
                    $"命令压力必须在 0～{EffectivePressureLimitBar:F0} bar 范围内。",
                    hydraulicId,
                    deviceName,
                    commandPressureBar);

            if (!StopAllCore())
                return Failure(
                    "无法确认全部压力 DO 已关闭且 AO 已回零，拒绝开始新的输出。",
                    hydraulicId,
                    deviceName,
                    commandPressureBar);
            var sample = _hardware.ReadPressureSample(hydraulicId);
            var sampleError = ClassifyPressureSample(hydraulicId, sample);
            if (!string.IsNullOrEmpty(sampleError))
                return Failure(sampleError, hydraulicId, deviceName, commandPressureBar);
            if (sample.ValueBar > CalibrationPressureLimitBar)
                return Failure(
                    $"实时压力 {sample.ValueBar:F2} bar 已超过校正安全上限 {CalibrationPressureLimitBar:F0} bar，拒绝输出。",
                    hydraulicId,
                    deviceName,
                    commandPressureBar);

            try
            {
                if (!_hardware.SetPressureDo(hydraulicId, true))
                    return FailAndRelease(
                        $"气缸 {hydraulicId} 压力 DO 打开失败。",
                        hydraulicId,
                        deviceName,
                        commandPressureBar);

                var ao = _hardware.WritePressure(deviceName, commandPressureBar);
                if (!ao.Success || !CalibrationMath.IsFinite(ao.Voltage))
                    return FailAndRelease(
                        $"{deviceName} AO 输出失败。",
                        hydraulicId,
                        deviceName,
                        commandPressureBar);

                IsOutputActive = true;
                ActiveHydraulicId = hydraulicId;
                ActiveDeviceName = deviceName;
                ActiveCommandPressureBar = ao.CommandPressureBar;
                ActiveVoltage = ao.Voltage;
                _watchdog?.Change(_watchdogIntervalMs, _watchdogIntervalMs);
                _log.Info(
                    $"压力校正输出：Device={deviceName} Command={ao.CommandPressureBar:F3}bar Voltage={ao.Voltage:F4}V",
                    "压力校正");
                return new PressureCalibrationOutputResult(
                    true,
                    string.Empty,
                    hydraulicId,
                    deviceName,
                    ao.CommandPressureBar,
                    ao.Voltage);
            }
            catch (Exception ex)
            {
                return FailAndRelease(
                    "压力输出异常：" + ex.Message,
                    hydraulicId,
                    deviceName,
                    commandPressureBar);
            }
        }

        public PressureCalibrationSafetyResult CheckSafety()
        {
            lock (_gate)
            {
                return CheckSafetyCore();
            }
        }

        private PressureCalibrationSafetyResult CheckSafetyCore()
        {
            ThrowIfDisposed();
            if (!IsOutputActive)
                return new PressureCalibrationSafetyResult(true, false, default, string.Empty);

            var sample = _hardware.ReadPressureSample(ActiveHydraulicId);
            var error = ClassifyPressureSample(ActiveHydraulicId, sample);
            if (string.IsNullOrEmpty(error) && sample.ValueBar > CalibrationPressureLimitBar)
                error = $"实时压力 {sample.ValueBar:F2} bar 超过校正安全上限 {CalibrationPressureLimitBar:F0} bar。";
            if (string.IsNullOrEmpty(error))
                return new PressureCalibrationSafetyResult(true, false, sample, string.Empty);

            StopAllCore();
            _log.Error(error, "压力校正");
            return new PressureCalibrationSafetyResult(false, true, sample, error);
        }

        public bool StopAll()
        {
            lock (_gate)
            {
                return StopAllCore();
            }
        }

        private bool StopAllCore()
        {
            if (_disposed) return true;
            try { _watchdog?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            var ok = true;
            try { ok &= _hardware.SetPressureDo(1, false); }
            catch { ok = false; }
            try { ok &= _hardware.SetPressureDo(2, false); }
            catch { ok = false; }
            try { ok &= _hardware.WritePressure("Cylinder1", 0).Success; }
            catch { ok = false; }
            try { ok &= _hardware.WritePressure("Cylinder2", 0).Success; }
            catch { ok = false; }

            IsOutputActive = false;
            ActiveHydraulicId = 0;
            ActiveDeviceName = string.Empty;
            ActiveCommandPressureBar = 0;
            ActiveVoltage = double.NaN;
            return ok;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { StopAllCore(); } catch { }
                _disposed = true;
                try { _watchdog?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                try { _hardware.Dispose(); } catch { }
            }
            try { _watchdog?.Dispose(); } catch { }
        }

        private string ClassifyPressureSample(int hydraulicId, PressureSample sample)
        {
            var maximumAgeMs = _testConfig?.Hydraulics?
                .FirstOrDefault(x => x.Id == hydraulicId)?.PressureSampleMaxAgeMs ?? 100;
            var reason = HydraulicController.ClassifyPressureSampleFailure(
                sample,
                double.NegativeInfinity,
                maximumAgeMs);
            if (!reason.HasValue) return string.Empty;
            return reason.Value switch
            {
                HydraulicPressureFailureReason.NoSample => $"Pressure_{hydraulicId} 尚无采样，拒绝输出。",
                HydraulicPressureFailureReason.InvalidValue => $"Pressure_{hydraulicId} 采样无效，已停止输出。",
                HydraulicPressureFailureReason.StaleSample =>
                    $"Pressure_{hydraulicId} 采样已过期（{sample.AgeMs:F0}ms），已停止输出。",
                _ => $"Pressure_{hydraulicId} 采样不满足输出条件，已停止输出。"
            };
        }

        private PressureCalibrationOutputResult FailAndRelease(
            string error,
            int hydraulicId,
            string deviceName,
            double commandPressureBar)
        {
            StopAllCore();
            _log.Error(error, "压力校正");
            return Failure(error, hydraulicId, deviceName, commandPressureBar);
        }

        private static PressureCalibrationOutputResult Failure(
            string error,
            int hydraulicId,
            string deviceName,
            double commandPressureBar) =>
            new PressureCalibrationOutputResult(
                false,
                error,
                hydraulicId,
                deviceName,
                commandPressureBar,
                double.NaN);

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PressureCalibrationCoordinator));
        }

        private void WatchdogTick(object state)
        {
            PressureCalibrationSafetyResult result;
            try
            {
                lock (_gate)
                {
                    if (_disposed || !IsOutputActive) return;
                    result = CheckSafetyCore();
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    try { StopAllCore(); } catch { }
                }
                result = new PressureCalibrationSafetyResult(
                    false,
                    true,
                    default,
                    "压力校正看门狗异常，已停止输出：" + ex.Message);
            }

            if (!result.Tripped) return;
            try { SafetyTripped?.Invoke(result); } catch { }
        }
    }
}
