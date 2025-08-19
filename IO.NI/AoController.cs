using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;
using NationalInstruments.DAQmx;

// 避免与 System.Threading.Tasks.Task 混淆，做别名
using NIDaqTask = NationalInstruments.DAQmx.Task;
using NIAnalogWriter = NationalInstruments.DAQmx.AnalogSingleChannelWriter;

namespace IO.NI
{
    #region Config Models

    /// <summary>
    /// AO 全局配置根节点。
    /// - 仍采用“原方案”：PhysicalChannel 写完整，如 Dev1/ao0。
    /// - Min/MaxVoltage 与 Min/MaxPercent 控制全局映射关系（默认 0–10V ⇄ 0–100%）。
    /// </summary>
    [XmlRoot("AOConfig")]
    public class AoConfig
    {
        /// <summary>最小输出电压（V），默认 0。</summary>
        public double MinVoltage { get; set; } = 0;

        /// <summary>最大输出电压（V），默认 10。</summary>
        public double MaxVoltage { get; set; } = 10;

        /// <summary>最小能力百分比，默认 0。</summary>
        public double MinPercent { get; set; } = 0;

        /// <summary>最大能力百分比，默认 100。</summary>
        public double MaxPercent { get; set; } = 100;

        /// <summary>AO 设备列表（每条对应一个气缸或一个独立控制点）。</summary>
        [XmlArray("Devices")]
        [XmlArrayItem("Device")]
        public List<AoDeviceConfig> Devices { get; set; } = new();
    }

    /// <summary>
    /// 单个 AO 设备（气缸）配置：保持原方案（物理通道为完整通道名）。
    /// </summary>
    public class AoDeviceConfig
    {
        /// <summary>逻辑名，如 "Cylinder1"、"Cylinder2"。</summary>
        public string Name { get; set; }

        /// <summary>NI 物理通道完整名，如 "Dev1/ao0"。</summary>
        public string PhysicalChannel { get; set; }

        /// <summary>
        /// 线性缩放系数：最终输出电压 = (映射电压) * ScaleK + Offset。
        /// 用于细微校准，默认 1.0。
        /// </summary>
        public double ScaleK { get; set; } = 1.0;

        /// <summary>电压偏置（V），默认 0.0。</summary>
        public double Offset { get; set; } = 0.0;

        /// <summary>（可选）初始化完成后要下发的默认百分比，空则不下发。</summary>
        public double? DefaultPercent { get; set; }

        public override string ToString() => $"{Name}({PhysicalChannel})";
    }

    #endregion

    #region Controller

    /// <summary>
    /// AO 控制器（Analog Output）：
    /// - 读取 AoConfig 并为每个逻辑设备创建 AO 任务与电压通道；
    /// - 提供百分比（0–100%）与电压（V）两种写入接口；
    /// - 单点同步写入（SingleSample），写失败自动重试 1 次；
    /// - 可做线性缓变（Ramp）；
    /// - 日志风格与 DoController 一致：支持 Info/Error、可选分类（默认 "AO"）。
    /// </summary>
    public sealed class AOController : IDisposable
    {
        private readonly object _aoTaskLock = new object();
        private readonly AoConfig _cfg;
        private readonly IAppLogger _log;

        // 任务、写入器、锁等各按“逻辑名”组织（Cylinder1/Cylinder2）
        private readonly Dictionary<string, NIDaqTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;
        private string _configPath;

        /// <summary>
        /// 使用注入的配置与日志实例构造。
        /// </summary>
        /// <param name="config">已反序列化的 AO 配置。</param>
        /// <param name="logger">日志接口；null 则使用 NullLogger。</param>
        public AOController(AoConfig config, IAppLogger logger = null)
        {
            _cfg = config ?? throw new ArgumentNullException(nameof(config));
            _log = logger ?? NullLogger.Instance;

            foreach (var dev in _cfg.Devices ?? Enumerable.Empty<AoDeviceConfig>())
                _locks[dev.Name] = new object();
        }

        /// <summary>
        /// 通过 XML 文件加载配置并创建控制器。
        /// </summary>
        public static AOController FromXml(string xmlPath, IAppLogger logger = null)
        {
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("AO 配置文件未找到", xmlPath);

            var ser = new XmlSerializer(typeof(AoConfig));
            using var fs = File.OpenRead(xmlPath);
            var cfg = (AoConfig)ser.Deserialize(fs);

            var ctrl = new AOController(cfg, logger);
            ctrl._configPath = xmlPath;
            return ctrl;
        }

        /// <summary>可选：记录配置路径（仅用于日志或再次加载）。</summary>
        public void SetConfigPath(string xmlPath) => _configPath = xmlPath;

        #region 初始化

        /// <summary>
        /// 初始化所有 AO 设备：为每个 PhysicalChannel 创建一个 AO 电压通道。
        /// 若配置了 DefaultPercent，则在成功初始化后写入安全默认值。
        /// </summary>
        public bool Initialize()
        {
            lock (_aoTaskLock)
            {
                try
                {
                    ResetAllTasks();

                    int chCount = 0;
                    foreach (var dev in _cfg.Devices)
                    {
                        InitializeDevice(dev);
                        chCount++;
                    }

                    // 可选：下发默认百分比
                    foreach (var dev in _cfg.Devices)
                    {
                        if (dev.DefaultPercent.HasValue)
                        {
                            try
                            {
                                SetPercent(dev.Name, dev.DefaultPercent.Value);
                            }
                            catch (Exception ex)
                            {
                                LogError($"[{dev.Name}] 默认百分比写入失败：{ex.Message}", "AO初始化", ex);
                            }
                        }
                    }

                    LogInfo($"AO 初始化完成：设备数={_cfg.Devices.Count}，通道数={chCount}（配置：{_configPath ?? "内存"}）", "AO初始化");
                    return true;
                }
                catch (DaqException ex)
                {
                    LogError("AO 初始化失败（DAQ）：" + ex.Message, "AO初始化", ex);
                    ResetAllTasks();
                    return false;
                }
                catch (Exception ex2)
                {
                    LogError("AO 初始化失败（未知）：" + ex2, "AO初始化", ex2);
                    ResetAllTasks();
                    return false;
                }
            }
        }

        private void InitializeDevice(AoDeviceConfig dev)
        {
            // 独立设备锁，避免多线程同时操作相同设备
            lock (GetLock(dev.Name))
            {
                try
                {
                    // 1) 清理旧 Task
                    if (_tasks.TryGetValue(dev.Name, out var old))
                    {
                        try { old.Dispose(); } catch { /* ignore */ }
                        _tasks.Remove(dev.Name);
                    }

                    // 2) 创建新 Task 与电压通道
                    var task = new NIDaqTask("AO_" + dev.Name);
                    task.AOChannels.CreateVoltageChannel(
                        dev.PhysicalChannel,
                        "aoChannel",
                        _cfg.MinVoltage,
                        _cfg.MaxVoltage,
                        AOVoltageUnits.Volts
                    );

                    // 3) 存储
                    _tasks[dev.Name] = task;

                    LogInfo($"[{dev.Name}] AO 通道创建：{dev.PhysicalChannel}，范围={_cfg.MinVoltage}~{_cfg.MaxVoltage} V", "AO初始化");
                }
                catch (DaqException)
                {
                    // 向上抛出，统一由 Initialize() 捕获并记录
                    throw;
                }
            }
        }

        #endregion

        #region 写入（百分比/电压）

        /// <summary>
        /// 设置某逻辑设备的“能力百分比”（0–100%），内部按全局映射换算到电压输出。
        /// </summary>
        /// <param name="deviceName">设备逻辑名（如 "Cylinder1"）。</param>
        /// <param name="percent">目标百分比（将限幅到全局 MinPercent~MaxPercent）。</param>
        public void SetPercent(string deviceName, double percent)
        {
            ThrowIfDisposed();
            var dev = FindDevice(deviceName);

            // 映射：百分比 → 电压
            var v = MapPercentToVoltage(percent);
            v = v * dev.ScaleK + dev.Offset;
            v = Clamp(v, _cfg.MinVoltage, _cfg.MaxVoltage);

            SafeWriteVoltage(deviceName, dev, v);
        }

        /// <summary>
        /// 直接以电压（V）形式输出（将限幅到全局 MinVoltage~MaxVoltage）。
        /// </summary>
        public void SetVoltage(string deviceName, double voltage)
        {
            ThrowIfDisposed();
            var dev = FindDevice(deviceName);

            var v = Clamp(voltage, _cfg.MinVoltage, _cfg.MaxVoltage);
            SafeWriteVoltage(deviceName, dev, v);
        }

        /// <summary>
        /// 以线性方式缓变至目标百分比（阻塞式，适合 UI 线程外调用）。
        /// </summary>
        /// <param name="deviceName">设备逻辑名。</param>
        /// <param name="targetPercent">目标百分比（限幅到全局范围）。</param>
        /// <param name="stepPercent">每步变化的百分比步长，默认 2%。</param>
        /// <param name="stepIntervalMs">每步间隔（毫秒），默认 20ms。</param>
        /// <param name="ct">取消令牌。</param>
        public void RampToPercent(string deviceName, double targetPercent,
                                  double stepPercent = 2,
                                  int stepIntervalMs = 20,
                                  CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var dev = FindDevice(deviceName);

            // 备注：这里没有读回卡，默认当前从全局 MinPercent 起步；
            // 如需“从上次值缓变”，可在控制器中缓存最后输出的百分比，并从该值开始。
            double startPercent = _cfg.MinPercent;
            double endPercent = Clamp(targetPercent, _cfg.MinPercent, _cfg.MaxPercent);

            double dir = endPercent >= startPercent ? 1 : -1;
            double step = Math.Max(1e-9, Math.Abs(stepPercent)) * dir;

            for (double p = startPercent; dir > 0 ? p < endPercent : p > endPercent; p += step)
            {
                ct.ThrowIfCancellationRequested();
                var v = Clamp(MapPercentToVoltage(p) * dev.ScaleK + dev.Offset, _cfg.MinVoltage, _cfg.MaxVoltage);
                SafeWriteVoltage(deviceName, dev, v);
                Thread.Sleep(stepIntervalMs);
            }

            // 最后一拍对齐到目标
            var vEnd = Clamp(MapPercentToVoltage(endPercent) * dev.ScaleK + dev.Offset, _cfg.MinVoltage, _cfg.MaxVoltage);
            SafeWriteVoltage(deviceName, dev, vEnd);
        }

        /// <summary>便捷方法：设置 Cylinder1 百分比。</summary>
        public void SetCylinder1Percent(double percent) => SetPercent("Cylinder1", percent);

        /// <summary>便捷方法：设置 Cylinder2 百分比。</summary>
        public void SetCylinder2Percent(double percent) => SetPercent("Cylinder2", percent);

        #endregion

        #region 资源与工具

        /// <summary>释放所有 AO 任务。</summary>
        public void Dispose()
        {
            lock (_aoTaskLock)
            {
                if (_disposed) return;
                _disposed = true;

                ResetAllTasks();
            }
            GC.SuppressFinalize(this);
        }

        private void ResetAllTasks()
        {
            foreach (var kv in _tasks)
            {
                try { kv.Value?.Dispose(); } catch { /* ignore */ }
            }
            _tasks.Clear();
        }

        private AoDeviceConfig FindDevice(string name)
        {
            var dev = _cfg.Devices?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (dev == null)
                throw new ArgumentException($"未找到 AO 设备：{name}", nameof(name));

            if (string.IsNullOrWhiteSpace(dev.PhysicalChannel))
                throw new InvalidOperationException($"设备 {dev.Name} 的 PhysicalChannel 为空。");

            return dev;
        }

        private object GetLock(string deviceName) => _locks[deviceName];

        private double MapPercentToVoltage(double percent)
        {
            var p = Clamp(percent, _cfg.MinPercent, _cfg.MaxPercent);
            if (Math.Abs(_cfg.MaxPercent - _cfg.MinPercent) < 1e-9)
                throw new InvalidOperationException("AOConfig 百分比范围配置非法：MaxPercent == MinPercent。");

            var ratio = (p - _cfg.MinPercent) / (_cfg.MaxPercent - _cfg.MinPercent);
            return _cfg.MinVoltage + ratio * (_cfg.MaxVoltage - _cfg.MinVoltage);
        }

        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AOController));
        }

        /// <summary>
        /// 安全写电压（单点写入）：内部一次重试；失败则释放 Task 并向外抛异常。
        /// </summary>
        private void SafeWriteVoltage(string deviceName, AoDeviceConfig dev, double voltage)
        {
            const int maxRetries = 1;
            int attempts = 0;

            while (true)
            {
                lock (GetLock(deviceName))
                {
                    try
                    {
                        if (!_tasks.TryGetValue(deviceName, out var task) || task == null)
                        {
                            // 任务不存在或已释放，尝试重新初始化该设备
                            InitializeDevice(dev);
                            task = _tasks[deviceName];
                        }

                        var writer = new NIAnalogWriter(task.Stream);
                        writer.WriteSingleSample(true, voltage);

                        LogInfo($"[{deviceName}] AO = {voltage:F3} V", "AO写入");
                        return;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"[{deviceName}] AO 写入失败（第 {attempts}/{maxRetries + 1} 次）：{ex.Message}", "AO写入", ex);

                        if (attempts > maxRetries)
                        {
                            // 超出最大重试：释放 Task，抛出异常让上层感知
                            if (_tasks.TryGetValue(deviceName, out var t))
                            {
                                try { t.Dispose(); } catch { /* ignore */ }
                                _tasks.Remove(deviceName);
                            }
                            throw;
                        }

                        // 重试前尝试重建任务
                        try { InitializeDevice(dev); } catch { /* 忽略，进入下一轮重试 */ }
                    }
                }
            }
        }

        private void LogInfo(string message, string category = null)
            => _log?.Info(message, category ?? "AO");

        private void LogError(string message, string category = null, Exception ex = null)
            => _log?.Error(message, category ?? "AO", ex);

        #endregion
    }

    #endregion
}
