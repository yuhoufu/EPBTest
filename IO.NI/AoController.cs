using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Config;
using NationalInstruments.DAQmx;
using Logger = Config.IAppLogger;
using NLogger = Config.NullLogger;
using Task = System.Threading.Tasks.Task;

namespace IO.NI
{
    /// <summary>
    /// AO 控制器：基于配置文件统一管理多个 AO 通道（液压比例控制）。
    /// - 支持按百分比写入（内部转电压）
    /// - 支持初始化/复位（落位）
    /// </summary>
    public sealed class AoController : IDisposable
    {
        private readonly AoConfig _cfg;
        private readonly Logger _log;

        // 每个设备名 -> 物理通道信息
        private readonly Dictionary<string, AnalogSingleChannelWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NationalInstruments.DAQmx.Task> _tasks = new(StringComparer.OrdinalIgnoreCase);

        public AoController(AoConfig cfg, Logger log = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log ?? NLogger.Instance;

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
                try
                {
                    var task = new NationalInstruments.DAQmx.Task($"AO_{dev.Name}");
                    task.AOChannels.CreateVoltageChannel(
                        dev.PhysicalChannel, "",
                        _cfg.MinVoltage, _cfg.MaxVoltage,
                        AOVoltageUnits.Volts);

                    var writer = new AnalogSingleChannelWriter(task.Stream);

                    _tasks[dev.Name] = task;
                    _writers[dev.Name] = writer;

                    // 初始化为 0%
                    WritePercent(dev.Name, 0);
                }
                catch (Exception ex)
                {
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
            if (!_writers.TryGetValue(deviceName, out var writer)) return false;
            if (!_cfg.Devices.TryGetValue(deviceName, out var dev)) return false;

            // 限幅
            percent = Math.Max(_cfg.MinPercent, Math.Min(_cfg.MaxPercent, percent));

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
            foreach (var name in _writers.Keys)
            {
                WritePercent(name, 0);
            }
            _log.Info("AO 所有通道已复位为 0%。", "AO");
        }

        public void Dispose()
        {
            foreach (var t in _tasks.Values)
            {
                try { t?.Dispose(); } catch { }
            }
            _tasks.Clear();
            _writers.Clear();
        }
    }
}
