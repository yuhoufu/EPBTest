using System;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace Config
{
    public static class AlarmConfigLoader
    {
        public static AlarmConfig Load(string path)
        {
            var doc = new XmlDocument();
            doc.Load(path);

            var cfg = new AlarmConfig();

            var serialNode = doc.SelectSingleNode("//AlarmConfig/Serial") as XmlElement;
            if (serialNode != null)
            {
                cfg.Serial.Port = GetAttr(serialNode, "Port", cfg.Serial.Port);
                cfg.Serial.Baud = GetIntAttr(serialNode, "Baud", cfg.Serial.Baud);
                cfg.Serial.DataBits = GetIntAttr(serialNode, "DataBits", cfg.Serial.DataBits);
                cfg.Serial.Parity = ParseEnumAttr(serialNode, "Parity", cfg.Serial.Parity);
                cfg.Serial.StopBits = ParseStopBitsAttr(serialNode, "StopBits", cfg.Serial.StopBits);
            }

            var behaviorNode = doc.SelectSingleNode("//AlarmConfig/Behavior") as XmlElement;
            if (behaviorNode != null)
            {
                cfg.Behavior.BuzzerEnabled = GetBoolAttr(behaviorNode, "BuzzerEnabled", cfg.Behavior.BuzzerEnabled);
                cfg.Behavior.BuzzerOnAnyAlarm = GetBoolAttr(behaviorNode, "BuzzerOnAnyAlarm", cfg.Behavior.BuzzerOnAnyAlarm);
                cfg.Behavior.BuzzerDebounceMs = GetIntAttr(behaviorNode, "BuzzerDebounceMs", cfg.Behavior.BuzzerDebounceMs);
                cfg.Behavior.RearmDelayMs = GetIntAttr(behaviorNode, "RearmDelayMs", cfg.Behavior.RearmDelayMs);
                cfg.Behavior.TimeoutMs = GetIntAttr(behaviorNode, "TimeoutMs", cfg.Behavior.TimeoutMs);
                cfg.Behavior.Retry = GetIntAttr(behaviorNode, "Retry", cfg.Behavior.Retry);
                cfg.Behavior.OvershootAlarmDeltaA = GetDoubleAttr(behaviorNode, "OvershootAlarmDeltaA", cfg.Behavior.OvershootAlarmDeltaA);
                cfg.Behavior.SnapshotLastNCycles = GetIntAttr(behaviorNode, "SnapshotLastNCycles", cfg.Behavior.SnapshotLastNCycles);
                cfg.Behavior.SnapshotCooldownMs = GetIntAttr(behaviorNode, "SnapshotCooldownMs", cfg.Behavior.SnapshotCooldownMs);
            }

            // Mappings
            var epbNodes = doc.SelectNodes("//AlarmConfig/Mappings/Epb");
            if (epbNodes != null)
            {
                foreach (XmlNode n in epbNodes)
                {
                    if (n is not XmlElement e) continue;
                    cfg.Mappings.Epb.Add(new AlarmEpbMapping
                    {
                        Channel = GetIntAttr(e, "Channel", 0),
                        DeviceId = GetIntAttr(e, "DeviceId", 0),
                        Line = GetIntAttr(e, "Line", 0),
                        OvershootAlarmDeltaA = GetNullableDoubleAttr(e, "OvershootAlarmDeltaA")
                    });
                }
            }

            var buzzerNode = doc.SelectSingleNode("//AlarmConfig/Mappings/Buzzer") as XmlElement;
            if (buzzerNode != null)
            {
                cfg.Mappings.Buzzer = new AlarmBuzzerMapping
                {
                    DeviceId = GetIntAttr(buzzerNode, "DeviceId", 0),
                    Line = GetIntAttr(buzzerNode, "Line", 0)
                };
            }

            // Commands/AllOff
            var allOffNodes = doc.SelectNodes("//AlarmConfig/Commands/AllOff/Device");
            if (allOffNodes != null)
            {
                foreach (XmlNode n in allOffNodes)
                {
                    if (n is not XmlElement e) continue;
                    cfg.Commands.AllOff.Add(new AlarmAllOffCommand
                    {
                        DeviceId = GetIntAttr(e, "Id", 0),
                        Hex = GetAttr(e, "Hex", string.Empty),
                        ExpectResponse = GetBoolAttr(e, "ExpectResponse", true)
                    });
                }
            }

            // Commands/SingleCoil
            var singleNodes = doc.SelectNodes("//AlarmConfig/Commands/SingleCoil/Cmd");
            if (singleNodes != null)
            {
                foreach (XmlNode n in singleNodes)
                {
                    if (n is not XmlElement e) continue;
                    cfg.Commands.SingleCoil.Add(new AlarmSingleCoilCommand
                    {
                        DeviceId = GetIntAttr(e, "DeviceId", 0),
                        Line = GetIntAttr(e, "Line", 0),
                        OnHex = GetAttr(e, "OnHex", string.Empty),
                        OffHex = GetAttr(e, "OffHex", string.Empty)
                    });
                }
            }

            // basic validation: distinct mapping by channel
            cfg.Mappings.Epb.RemoveAll(x => x.Channel <= 0 || x.DeviceId <= 0 || x.Line < 0);
            cfg.Mappings.Epb.Sort((a, b) => a.Channel.CompareTo(b.Channel));

            return cfg;
        }

        private static string GetAttr(XmlElement e, string name, string def)
            => e.HasAttribute(name) ? e.GetAttribute(name) : def;

        private static int GetIntAttr(XmlElement e, string name, int def)
            => int.TryParse(GetAttr(e, name, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

        private static bool GetBoolAttr(XmlElement e, string name, bool def)
            => bool.TryParse(GetAttr(e, name, def.ToString()), out var v) ? v : def;

        private static double GetDoubleAttr(XmlElement e, string name, double def)
            => double.TryParse(GetAttr(e, name, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        private static double? GetNullableDoubleAttr(XmlElement e, string name)
        {
            if (!e.HasAttribute(name)) return null;
            var s = e.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(s)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        private static System.IO.Ports.Parity ParseEnumAttr(XmlElement e, string name, System.IO.Ports.Parity def)
        {
            var s = GetAttr(e, name, def.ToString());
            return Enum.TryParse<System.IO.Ports.Parity>(s, true, out var v) ? v : def;
        }

        private static System.IO.Ports.StopBits ParseStopBitsAttr(XmlElement e, string name, System.IO.Ports.StopBits def)
        {
            var s = GetAttr(e, name, def == System.IO.Ports.StopBits.One ? "1" : def.ToString());
            if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)) return System.IO.Ports.StopBits.One;
            if (string.Equals(s, "2", StringComparison.OrdinalIgnoreCase)) return System.IO.Ports.StopBits.Two;
            return Enum.TryParse<System.IO.Ports.StopBits>(s, true, out var v) ? v : def;
        }
    }
}
