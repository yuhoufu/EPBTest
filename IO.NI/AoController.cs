using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Config;
using NationalInstruments.DAQmx;
using Logger = Config.IAppLogger;
using NLogger = Config.NullLogger;
using Task = System.Threading.Tasks.Task;

namespace IO.NI
{
    public readonly struct AoWriteResult
    {
        public AoWriteResult(bool success, string deviceName, double commandPressureBar, double voltage)
        {
            Success = success;
            DeviceName = deviceName ?? string.Empty;
            CommandPressureBar = commandPressureBar;
            Voltage = voltage;
        }

        public bool Success { get; }
        public string DeviceName { get; }
        public double CommandPressureBar { get; }
        public double Voltage { get; }
    }

    /// <summary>
    /// AO 控制器：基于配置文件统一管理多个 AO 通道（液压比例控制）。
    /// - 支持按百分比写入（内部转电压）
    /// - 支持初始化/复位（落位）
    /// </summary>
    public sealed class AoController : IDisposable
    {
        private readonly AoConfig _cfg;
        private readonly Logger _log;
        private readonly bool _initializeWithZeroVoltage;
        private readonly object _lifecycleGate = new object();
        private int _disposed;
        private int _postDisposeWarningLogged;
        private int _resetAllExecutionCount;
        private readonly HardwareReleaseEvidence _releaseEvidence = new HardwareReleaseEvidence();

        public HardwareReleaseSnapshot CaptureReleaseEvidence() => _releaseEvidence.Capture();

        // 每个设备名 -> 物理通道信息
        private readonly Dictionary<string, AnalogSingleChannelWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NationalInstruments.DAQmx.Task> _tasks = new(StringComparer.OrdinalIgnoreCase);

        public AoController(AoConfig cfg, Logger log = null) : this(cfg, log, false)
        {
        }

        public AoController(AoConfig cfg, Logger log, bool initializeWithZeroVoltage)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log ?? NLogger.Instance;
            _initializeWithZeroVoltage = initializeWithZeroVoltage;

            Initialize();
        }

        /// <summary>
        /// 初始化所有 AO 通道，构建 Task + Writer。
        /// </summary>
        private void Initialize()
        {
            foreach (var kv in _cfg.Devices)
            {
                var dev = kv.Value;
                NationalInstruments.DAQmx.Task task = null;
                try
                {
                    task = new NationalInstruments.DAQmx.Task($"AO_{dev.Name}");
                    task.AOChannels.CreateVoltageChannel(
                        dev.PhysicalChannel, "",
                        _cfg.MinVoltage, _cfg.MaxVoltage,
                        AOVoltageUnits.Volts);

                    var writer = new AnalogSingleChannelWriter(task.Stream);

                    _tasks[dev.Name] = task;
                    _writers[dev.Name] = writer;

                    // 初始化为 0%
                    if (_initializeWithZeroVoltage) WriteZeroVoltage(dev.Name);
                    else WritePressure(dev.Name, 0);
                }
                catch (Exception ex)
                {
                    // 构造通道/Writer 失败也必须释放已经创建的 NI Task。
                    // 未存入字典不表示没有占用原生设备。
                    if (task != null && !_tasks.ContainsKey(dev.Name))
                    {
                        try { task.Dispose(); }
                        catch (Exception releaseError)
                        { _releaseEvidence.RecordFailure("AO initialization cleanup", releaseError); }
                    }
                    _log.Error($"AO[{dev.Name}] 初始化失败：{ex.Message}", "AO", ex);
                }
            }

            _log.Info($"AO 控制器初始化完成：共 {_writers.Count} 路", "AO");
        }

        /// <summary>
        /// 按百分比写入电压。
        /// </summary>
        public bool WritePercent(string deviceName, double percent)
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(WritePercent))) return false;
                if (!_writers.TryGetValue(deviceName, out var writer)) return false;
                if (!_cfg.Devices.TryGetValue(deviceName, out var dev)) return false;

                // 限幅
                percent = Math.Max(_cfg.MinPressure, Math.Min(_cfg.MaxPressure, percent));

                // 转电压
                double v = (percent * (_cfg.MaxVoltage - _cfg.MinVoltage) / 100.0) + _cfg.MinVoltage;
                v = v * dev.ScaleK + dev.Offset;

                try
                {
                    writer.WriteSingleSample(true, v);
                    _log.Info($"AO[{deviceName}] 输出百分比 {percent:F1}% -> 电压 {v:F2} V", "AO");
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Error($"AO[{deviceName}] 输出失败：{ex.Message}", "AO", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// 按压力写入电压。
        /// </summary>
        public bool WritePressure(string deviceName, double pressure)
            => WritePressureDetailed(deviceName, pressure).Success;

        /// <summary>按压力标定写入，并返回实际限幅命令及换算电压。</summary>
        public AoWriteResult WritePressureDetailed(string deviceName, double pressure)
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(WritePressureDetailed)))
                    return new AoWriteResult(false, deviceName, pressure, double.NaN);
                if (!_writers.TryGetValue(deviceName, out var writer))
                    return new AoWriteResult(false, deviceName, pressure, double.NaN);
                if (!_cfg.Devices.TryGetValue(deviceName, out var dev))
                    return new AoWriteResult(false, deviceName, pressure, double.NaN);

                double v;
                if (_initializeWithZeroVoltage)
                {
                    // V3: every OFF path (including Controller's historical zero
                    // pressure calls) means physical zero volts. Active requests
                    // must be representable exactly, never silently clamped.
                    if (!TryResolveSupervisedPressure(_cfg, dev, pressure, out v))
                        return new AoWriteResult(false, deviceName, pressure, double.NaN);
                }
                else
                {
                    pressure = Math.Min(Math.Max(pressure, _cfg.MinPressure), _cfg.MaxPressure);
                    v = (pressure - dev.Offset) / dev.ScaleK;
                }

                try
                {
                    writer.WriteSingleSample(true, v);
                    _log.Info($"AO[{deviceName}] CommandPressure={pressure:F1}bar AoVoltage={v:F3}V", "AO");
                    return new AoWriteResult(true, deviceName, pressure, v);
                }
                catch (Exception ex)
                {
                    _log.Error($"AO[{deviceName}] 输出失败：{ex.Message}", "AO", ex);
                    return new AoWriteResult(false, deviceName, pressure, v);
                }
            }
        }

        /// <summary>
        /// 异步写入百分比。
        /// </summary>
        public Task<bool> SetPressureAsync(string deviceName, double percent)
        {
            return Task.Run(() => WritePressure(deviceName, percent));
        }

        /// <summary>
        /// 异步写入百分比。
        /// </summary>
        public Task<bool> SetPercentAsync(string deviceName, double percent)
        {
            return Task.Run(() => WritePercent(deviceName, percent));
        }

        /// <summary>
        /// 将所有 AO 通道复位为 0%。
        /// </summary>
        public void ResetAll()
        {
            TryResetAll();
        }

        /// <summary>将所有 AO 通道复位为零，并返回每一路写入是否全部成功。</summary>
        public bool TryResetAll()
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(TryResetAll))) return false;
                Interlocked.Increment(ref _resetAllExecutionCount);
                var success = _cfg.Devices.Count > 0 && _writers.Count == _cfg.Devices.Count;
                foreach (var name in _cfg.Devices.Keys)
                {
                    if (!WritePressure(name, 0)) success = false;
                }
                if (success)
                    _log.Info("AO 所有通道已复位为 0%。", "AO");
                else
                    _log.Error("AO 冷启动安全基线写零失败；至少一路未确认归零。", "AO");
                return success;
            }
        }

        internal static bool TryResolveSupervisedPressure(AoConfig config, AoDevice device, double pressure, out double voltage)
        {
            voltage = double.NaN;
            if (config == null || device == null || !Finite(pressure) || pressure < 0 ||
                !Finite(config.MinVoltage) || !Finite(config.MaxVoltage) || config.MinVoltage < -10 || config.MinVoltage > 0 ||
                config.MaxVoltage <= 0 || config.MaxVoltage > 10) return false;
            if (pressure == 0) { voltage = 0; return true; }
            if (!Finite(config.MinPressure) || !Finite(config.MaxPressure) || pressure < config.MinPressure || pressure > config.MaxPressure ||
                !Finite(device.ScaleK) || device.ScaleK <= 1e-9 || !Finite(device.Offset)) return false;
            voltage = (pressure - device.Offset) / device.ScaleK;
            return Finite(voltage) && voltage >= config.MinVoltage && voltage <= config.MaxVoltage;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // V3 OFF matches the independent SafetyAgent's physical 0 V,
        // not (0 bar - calibration offset) / scale. A positive fitted offset
        // must not make safe shutdown request an out-of-range negative voltage.
        public bool TryWriteZeroVoltageAll()
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(TryWriteZeroVoltageAll))) return false;
                var success = _cfg.Devices.Count > 0 && _writers.Count == _cfg.Devices.Count;
                foreach (var name in _cfg.Devices.Keys)
                    if (!WriteZeroVoltage(name)) success = false;
                return success;
            }
        }

        private bool WriteZeroVoltage(string deviceName)
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(WriteZeroVoltage)) || double.IsNaN(_cfg.MinVoltage) || double.IsInfinity(_cfg.MinVoltage) ||
                    double.IsNaN(_cfg.MaxVoltage) || double.IsInfinity(_cfg.MaxVoltage) || _cfg.MinVoltage > 0 || _cfg.MaxVoltage < 0 ||
                    !_writers.TryGetValue(deviceName, out var writer)) return false;
                try { writer.WriteSingleSample(true, 0.0); return true; }
                catch (Exception ex) { _log.Error($"AO[{deviceName}] 安全零电压写入失败：{ex.Message}", "AO", ex); return false; }
            }
        }

        public void Dispose()
        {
            _releaseEvidence.RequestRelease();
            lock (_lifecycleGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                foreach (var t in _tasks.Values)
                {
                    try { t?.Dispose(); }
                    catch (Exception ex) { _releaseEvidence.RecordFailure("AO Task.Dispose", ex); }
                }
                _tasks.Clear();
                _writers.Clear();
                _releaseEvidence.CompleteNativeRelease();
                // 所有写入与 Dispose 共用 lifecycle gate；等待中的写入看到 disposed
                // 后只能拒绝，不能再调用 NI。此处不是输出电压为零的证明。
                _releaseEvidence.CloseCallbackAdmission();
            }
            GC.SuppressFinalize(this);
        }

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal int ResetAllExecutionCount => Volatile.Read(ref _resetAllExecutionCount);

        private bool RejectDisposedOperation(string operation)
        {
            if (Volatile.Read(ref _disposed) == 0) return false;
            if (Interlocked.Exchange(ref _postDisposeWarningLogged, 1) == 0)
                _log.Warn($"AO 控制器已释放，拒绝后续操作：{operation}。", "AO");
            return true;
        }
    }
}
