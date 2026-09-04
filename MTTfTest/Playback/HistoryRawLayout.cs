using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;
using Config;

namespace MTEmbTest.Playback
{
    internal sealed class HistoryRawLayout
    {
        internal string Device, ConfigurationPath;
        internal HistoryReadOptions Options;

        internal static HistoryRawLayout Load(string directory, int channel, int medianLength)
        {
            if (channel < 1 || channel > 12) throw new InvalidDataException("请选择 EPB1 至 EPB12。");
            var root = Path.GetFullPath(directory);
            var legacy = Path.Combine(root, "AIConfig.xml"); var current = Path.Combine(root, "Config", "AIConfig.xml");
            if (File.Exists(legacy) && File.Exists(current) && !SameFileContent(legacy, current))
                throw new InvalidDataException("目录内存在两份不同的历史 AI 配置，不能猜测采集布局。");
            var path = File.Exists(current) ? current : legacy;
            AiConfigDetail configuration;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > 1024 * 1024) throw new InvalidDataException("历史 AI 配置大小无效。");
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                    configuration = (AiConfigDetail)new XmlSerializer(typeof(AiConfigDetail)).Deserialize(reader);
            }
            var rows = configuration?.Records;
            if (rows == null || rows.Count == 0 || rows.Any(r => r == null || r.序号 < 1 || string.IsNullOrWhiteSpace(r.物理通道)) ||
                rows.Select(r => r.序号).Distinct().Count() != rows.Count ||
                rows.Select(r => r.物理通道).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
                throw new InvalidDataException("历史 AI 配置包含缺失或重复的物理通道。");
            var name = "EPB" + channel;
            var selected = rows.Where(r => string.Equals(r.参数名, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.参数名, name + "_current", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selected.Length != 1 || selected[0].是否启用 != 1) throw new InvalidDataException(name + " 没有唯一且启用的历史电流通道。");
            var device = selected[0].物理通道.Split('/')[0];
            if (!device.Equals("Dev1", StringComparison.OrdinalIgnoreCase) && !device.Equals("Dev2", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("历史采集设备不是 Dev1/Dev2。");
            var group = rows.Where(r => r.是否启用 == 1 && r.物理通道.Split('/')[0].Equals(device, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.序号).ToArray();
            var options = new HistoryReadOptions { Raw = true, ChannelCount = group.Length, ChannelIndex = Array.IndexOf(group, selected[0]),
                Scale = selected[0].变换斜率, Offset = selected[0].变换截距, Zero = selected[0].零位漂移, MedianLength = Math.Max(1, medianLength) };
            options.Validate();
            return new HistoryRawLayout { Device = device.Equals("Dev1", StringComparison.OrdinalIgnoreCase) ? "Dev1" : "Dev2",
                ConfigurationPath = path, Options = options };
        }

        private static bool SameFileContent(string first, string second)
        {
            if (new FileInfo(first).Length > 1024 * 1024 || new FileInfo(second).Length > 1024 * 1024) return false;
            using (var hash = SHA256.Create()) using (var a = File.OpenRead(first)) using (var b = File.OpenRead(second))
                return hash.ComputeHash(a).SequenceEqual(hash.ComputeHash(b));
        }
    }
}
