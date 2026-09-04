using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace MTEmbTest
{
    // Per-user presentation state only. Never writes project settings or acquisition zeros.
    internal sealed class OriginalMonitorPreferences
    {
        private readonly string _path;
        internal readonly Dictionary<string, bool> Curves = new Dictionary<string, bool>(StringComparer.Ordinal);
        internal double WindowSeconds { get; set; } // 0 follows two test periods.
        internal string Error { get; private set; } = string.Empty;

        internal OriginalMonitorPreferences(string path = null)
        {
            _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MTTFTest", "UI", "original-monitor-v1.xml");
            try
            {
                if (!File.Exists(_path)) return;
                if (new FileInfo(_path).Length > 16384) throw new InvalidDataException("显示偏好文件过大");
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(_path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
                if (document.DocumentElement?.Name != "Monitor" || document.DocumentElement.GetAttribute("Version") != "1")
                    throw new InvalidDataException("显示偏好版本不匹配");
                if (!double.TryParse(document.DocumentElement.GetAttribute("WindowSeconds"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var seconds) || double.IsNaN(seconds) || seconds < 0 || seconds > 3600)
                    throw new InvalidDataException("显示窗口无效");
                WindowSeconds = seconds;
                foreach (XmlElement element in document.DocumentElement.SelectNodes("Curve"))
                {
                    var key = element.GetAttribute("Key");
                    if (!ValidKey(key) || Curves.ContainsKey(key) || !bool.TryParse(element.GetAttribute("Visible"), out var visible))
                        throw new InvalidDataException("曲线显示偏好无效");
                    Curves.Add(key, visible);
                }
            }
            catch (Exception ex) { Curves.Clear(); WindowSeconds = 0; Error = "显示偏好未加载：" + ex.Message; }
        }

        internal void Save()
        {
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var document = new XmlDocument(); var root = document.CreateElement("Monitor"); document.AppendChild(root);
                root.SetAttribute("Version", "1"); root.SetAttribute("WindowSeconds", WindowSeconds.ToString("R", CultureInfo.InvariantCulture));
                foreach (var item in Curves)
                {
                    if (!ValidKey(item.Key)) continue;
                    var element = document.CreateElement("Curve"); element.SetAttribute("Key", item.Key);
                    element.SetAttribute("Visible", item.Value.ToString()); root.AppendChild(element);
                }
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { document.Save(output); output.Flush(true); }
                if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
                Error = string.Empty;
            }
            catch (Exception ex) { Error = "显示偏好未保存：" + ex.Message; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static bool ValidKey(string key) => key == "P1" || key == "P2" || key == "F" ||
            key.StartsWith("A", StringComparison.Ordinal) && int.TryParse(key.Substring(1), out var channel) &&
            channel >= 1 && channel <= 12 && key == "A" + channel;
    }
}
