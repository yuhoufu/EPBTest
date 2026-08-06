using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Config
{
    public sealed class SensorCalibrationUpdate
    {
        public string ParameterName { get; set; }
        public double Scale { get; set; }
        public double Offset { get; set; }
        public double Zero { get; set; }
    }

    public sealed class AoCalibrationPoint
    {
        public double Pressure { get; set; }
        public double Voltage { get; set; }
        public double? CommandPressure { get; set; }
    }

    /// <summary>以备份 + 原子替换方式保存现场标定配置。</summary>
    public static class CalibrationConfigStore
    {
        public static void SaveSensorCalibrations(
            string path,
            IEnumerable<SensorCalibrationUpdate> updates)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            var byName = (updates ?? throw new ArgumentNullException(nameof(updates)))
                .ToDictionary(x => x.ParameterName, StringComparer.OrdinalIgnoreCase);
            if (byName.Count == 0) throw new InvalidOperationException("没有可保存的传感器标定记录。");

            foreach (var item in byName.Values)
            {
                if (string.IsNullOrWhiteSpace(item.ParameterName) ||
                    !CalibrationMath.IsFinite(item.Scale) || Math.Abs(item.Scale) < 1e-12 ||
                    !CalibrationMath.IsFinite(item.Offset) ||
                    !CalibrationMath.IsFinite(item.Zero))
                    throw new InvalidDataException($"传感器 {item.ParameterName} 的标定参数无效。");
            }

            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var found = 0;
            foreach (var record in doc.Root?.Elements("Records") ?? Enumerable.Empty<XElement>())
            {
                var name = record.Element("参数名")?.Value?.Trim();
                if (name == null || !byName.TryGetValue(name, out var item)) continue;
                SetElementValue(record, "变换斜率", Format(item.Scale));
                SetElementValue(record, "变换截距", Format(item.Offset));
                SetElementValue(record, "零位漂移", Format(item.Zero));
                found++;
            }

            if (found != byName.Count)
                throw new InvalidDataException($"AI 配置中仅找到 {found}/{byName.Count} 条待保存记录。");

            SaveAtomicWithBackup(path, doc);
        }

        public static void SaveAoCalibrations(
            string path,
            IReadOnlyDictionary<string, IReadOnlyList<AoCalibrationPoint>> devices,
            double minVoltage,
            double maxVoltage)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("没有可保存的气缸标定记录。");

            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var deviceNodes = doc.Root?
                .Element("Devices")?
                .Elements("Device")
                .ToDictionary(
                    x => x.Element("Name")?.Value?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase) ??
                new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in devices)
            {
                if (!deviceNodes.TryGetValue(pair.Key, out var deviceNode))
                    throw new InvalidDataException($"AO 配置中不存在设备 {pair.Key}。");

                var points = pair.Value?.OrderBy(x => x.Pressure).ToArray() ??
                             Array.Empty<AoCalibrationPoint>();
                if (points.Length > 0)
                {
                    var tuples = points.Select(x => (x.Voltage, x.Pressure)).ToArray();
                    if (!CalibrationMath.TryMapPressureToVoltage(
                            tuples,
                            points[0].Pressure,
                            minVoltage,
                            maxVoltage,
                            out _))
                        throw new InvalidDataException(
                            $"{pair.Key} 至少需要两个压力、电压均严格递增的有效标定点。");
                }

                var table = deviceNode.Element("VoltageToPressureTable");
                if (table == null)
                {
                    table = new XElement("VoltageToPressureTable");
                    deviceNode.Add(table);
                }

                table.RemoveNodes();
                foreach (var point in points)
                {
                    var node = new XElement("Point",
                        new XElement("Voltage", Format(point.Voltage)),
                        new XElement("Pressure", Format(point.Pressure)));
                    if (point.CommandPressure.HasValue)
                        node.Add(new XElement("CommandPressure", Format(point.CommandPressure.Value)));
                    table.Add(node);
                }
            }

            SaveAtomicWithBackup(path, doc);
        }

        private static void SetElementValue(XElement parent, string name, string value)
        {
            var element = parent.Element(name);
            if (element == null) parent.Add(new XElement(name, value));
            else element.Value = value;
        }

        private static string Format(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);

        private static void SaveAtomicWithBackup(string path, XDocument doc)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, Path.GetFileName(fullPath) + ".tmp." + Guid.NewGuid().ToString("N"));
            var backupPath = fullPath + ".bak";
            try
            {
                doc.Save(tempPath, SaveOptions.DisableFormatting);
                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, backupPath, true);
                else
                    File.Move(tempPath, fullPath);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { }
            }
        }
    }
}
