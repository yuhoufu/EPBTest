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
    // The internal port keeps production voltage conversion and lifecycle under
    // test without loading a device. The public constructor always uses NI.
    internal interface IAoVoltageOutput : IDisposable
    {
        void Write(double voltage);
    }

    internal sealed class NiAoVoltageOutput : IAoVoltageOutput
    {
        private readonly NationalInstruments.DAQmx.Task _task;
        private readonly AnalogSingleChannelWriter _writer;
        internal NiAoVoltageOutput(AoConfig config, AoDevice device)
        {
            _task = new NationalInstruments.DAQmx.Task($"AO_{device.Name}");
            try
            {
                _task.AOChannels.CreateVoltageChannel(device.PhysicalChannel, "",
                    config.MinVoltage, config.MaxVoltage, AOVoltageUnits.Volts);
                _writer = new AnalogSingleChannelWriter(_task.Stream);
            }
            catch { _task.Dispose(); throw; }
        }
        public void Write(double voltage) => _writer.WriteSingleSample(true, voltage);
        public void Dispose() => _task.Dispose();
    }

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
        private readonly object _lifecycleGate = new object();
        private int _disposed;
        private int _postDisposeWarningLogged;
        private int _resetAllExecutionCount;

        private readonly Dictionary<string, IAoVoltageOutput> _outputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<AoDevice, IAoVoltageOutput> _createOutput;

        public AoController(AoConfig cfg, Logger log = null) : this(cfg, log, null) { }

        internal AoController(AoConfig cfg, Logger log, Func<AoDevice, IAoVoltageOutput> createOutput)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log ?? NLogger.Instance;
            if (double.IsNaN(cfg.MinVoltage) || double.IsInfinity(cfg.MinVoltage) ||
                double.IsNaN(cfg.MaxVoltage) || double.IsInfinity(cfg.MaxVoltage) ||
                cfg.MinVoltage >= cfg.MaxVoltage || cfg.MinVoltage > 0 || cfg.MaxVoltage < 0)
                throw new ArgumentException("AO voltage range must contain literal zero.", nameof(cfg));
            _createOutput = createOutput ?? (device => new NiAoVoltageOutput(_cfg, device));
            Initialize();
        }

        private void Initialize()
        {
            foreach (var kv in _cfg.Devices)
            {
                var dev = kv.Value;
                IAoVoltageOutput output = null;
                try
                {
                    output = _createOutput(dev) ?? throw new InvalidOperationException("AO output missing.");
                    // Safety zero is a literal voltage, never a pressure setpoint.
                    output.Write(0);
                    _outputs.Add(dev.Name, output);
                    output = null; // ownership transferred only after confirmed initialization
                }
                catch (Exception ex)
                {
                    _log.Error($"AO[{dev.Name}] 初始化失败：{ex.Message}", "AO", ex);
                }
                finally { output?.Dispose(); }
            }
        }

        /// <summary>
        /// 按百分比写入电压。
        /// </summary>
        public bool WritePercent(string deviceName, double percent)
        {
            lock (_lifecycleGate)
            {
                if (RejectDisposedOperation(nameof(WritePercent))) return false;
                if (!_outputs.TryGetValue(deviceName, out var writer)) return false;
                if (!_cfg.Devices.TryGetValue(deviceName, out var dev)) return false;

                // 限幅
                percent = Math.Max(_cfg.MinPressure, Math.Min(_cfg.MaxPressure, percent));

                // 转电压
                double v = (percent * (_cfg.MaxVoltage - _cfg.MinVoltage) / 100.0) + _cfg.MinVoltage;
                v = v * dev.ScaleK + dev.Offset;

                try
                {
                    if (v != 0) MTTFTest.Watchdog.Protocol.IndependentExecutionFence.RequireCurrentAuthority();
                    writer.Write(v);
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
                if (!_outputs.TryGetValue(deviceName, out var writer))
                    return new AoWriteResult(false, deviceName, pressure, double.NaN);
                if (!_cfg.Devices.TryGetValue(deviceName, out var dev))
                    return new AoWriteResult(false, deviceName, pressure, double.NaN);

                // 限幅
                pressure = Math.Min(Math.Max(pressure, _cfg.MinPressure), _cfg.MaxPressure);

                // 恢复原线性换算；校正页通过多点拟合更新 ScaleK/Offset。
                var v = (pressure - dev.Offset) / dev.ScaleK;

                try
                {
                    if (v != 0) MTTFTest.Watchdog.Protocol.IndependentExecutionFence.RequireCurrentAuthority();
                    writer.Write(v);
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
                var success = _cfg.Devices.Count > 0 && _outputs.Count == _cfg.Devices.Count;
                foreach (var name in _cfg.Devices.Keys)
                {
                    if (!_outputs.TryGetValue(name, out var output)) { success = false; continue; }
                    try { output.Write(0); }
                    catch (Exception ex)
                    {
                        success = false;
                        _log.Error($"AO[{name}] 安全写入0V失败：{ex.Message}", "AO", ex);
                    }
                }
                if (success)
                    _log.Info("AO 所有通道已写入 0V。", "AO");
                else
                    _log.Error("AO 冷启动安全基线写零失败；至少一路未确认归零。", "AO");
                return success;
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                foreach (var t in _outputs.Values)
                {
                    try { t?.Dispose(); } catch { }
                }
                _outputs.Clear();
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
