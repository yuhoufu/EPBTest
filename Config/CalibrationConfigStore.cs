using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Config
{
    public sealed class AoCalibrationPoint
    {
        public double Pressure { get; set; }
        public double Voltage { get; set; }
        public double? CommandPressure { get; set; }
    }

    /// <summary>以备份 + 原子替换方式保存现场标定配置。</summary>
    public static class CalibrationConfigStore
    {
        public static void SaveAoLinearCalibration(
            string path,
            string deviceName,
            IReadOnlyList<AoCalibrationPoint> calibrationPoints,
            double minVoltage,
            double maxVoltage)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (string.IsNullOrWhiteSpace(deviceName)) throw new ArgumentNullException(nameof(deviceName));
            if (calibrationPoints == null || calibrationPoints.Count < 2)
                throw new InvalidOperationException("没有可保存的气缸标定记录。");

            var points = calibrationPoints.OrderBy(x => x.Voltage).ToArray();
            if (points.Any(x => !CalibrationMath.IsFinite(x.Voltage) ||
                                !CalibrationMath.IsFinite(x.Pressure) ||
                                x.Voltage < minVoltage || x.Voltage > maxVoltage))
                throw new InvalidDataException("校正点包含无效值或超出 AO 电压范围。");
            for (var i = 1; i < points.Length; i++)
            {
                if (points[i].Voltage <= points[i - 1].Voltage ||
                    points[i].Pressure <= points[i - 1].Pressure)
                    throw new InvalidDataException("实测压力与 AO 电压必须同时严格递增。");
            }
            if (!CalibrationMath.TryFitPressureLine(
                    points.Select(x => (x.Voltage, x.Pressure)),
                    out var scaleK,
                    out var offset,
                    out _,
                    out var fitError))
                throw new InvalidDataException(fitError);

            var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var deviceNodes = doc.Root?
                .Element("Devices")?
                .Elements("Device")
                .ToDictionary(
                    x => x.Element("Name")?.Value?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase) ??
                new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);

            if (!deviceNodes.TryGetValue(deviceName, out var deviceNode))
                throw new InvalidDataException($"AO 配置中不存在设备 {deviceName}。");

            SetElementValue(deviceNode, "ScaleK", Format(scaleK));
            SetElementValue(deviceNode, "Offset", Format(offset));
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
