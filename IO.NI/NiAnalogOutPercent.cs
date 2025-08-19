using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

// （可选）IO/NiAnalogOutPercent.cs —— 若你已有此类，可无视本文件。
// 该实现演示如何从 ConfigLoader 的 AO 部分读出物理通道，并按百分比输出电压。
namespace IO.NI
{
    /// <summary>
    /// AO 百分比输出：线性映射到电压（Min/MaxVoltage 与 ScaleK/Offset 生效）。
    /// </summary>
    public sealed class NiAnalogOutPercentAdapter
    {
        private readonly AoConfig _cfg;
        private readonly IAppLogger _log;

        private readonly Dictionary<string, AoDeviceConfig> _devMap =
            new(StringComparer.OrdinalIgnoreCase);

        // 这里仅保留接口签名。实际项目中请替换为 NI 的 AO 写入代码。
        private readonly Func<string, double, Task> _writeVoltage;

        public NiAnalogOutPercentAdapter(AoConfig cfg, Func<string, double, Task> writeVoltage, IAppLogger log = null)
        {
            _cfg = cfg;
            _writeVoltage = writeVoltage;
            _log = log ?? NullLogger.Instance;
            // 在构造函数或 Initialize 里填充
            _devMap = _cfg.Devices?.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase)
                      ?? new Dictionary<string, AoDeviceConfig>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 将百分比写到指定液压设备（如 Cylinder1/2）。
        /// </summary>
        /// <param name="deviceName">逻辑名，例如 "Cylinder1"</param>
        /// <param name="percent">目标百分比（自动限幅到全局范围）</param>
        /// <returns>写入是否成功</returns>
        public async Task<bool> SetPercentAsync(string deviceName, double percent)
        {
            // ① 替换 TryGetValue：从字典（或 LINQ）取设备
            if (!_devMap.TryGetValue(deviceName, out var dev))
                return false;

            // ② 替换 Math.Clamp：使用自定义 Clamp
            var p = Clamp(percent, _cfg.MinPercent, _cfg.MaxPercent);

            // 百分比 -> 电压 的线性映射
            var v = _cfg.MinVoltage + (p - _cfg.MinPercent)
                / (_cfg.MaxPercent - _cfg.MinPercent)
                * (_cfg.MaxVoltage - _cfg.MinVoltage);

            // 设备级微调 & 电压限幅
            v = Clamp(v * dev.ScaleK + dev.Offset, _cfg.MinVoltage, _cfg.MaxVoltage);

            await _writeVoltage(dev.PhysicalChannel, v);
            _log?.Info($"AO {deviceName} = {p:F2}% ({v:F3}V)", "AO");
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Clamp(double value, double min, double max)
        {
            if (min > max)
            {
                (min, max) = (max, min);
            } // 防御

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}