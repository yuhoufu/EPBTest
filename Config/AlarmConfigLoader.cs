using System;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace Config
{
    public static class AlarmConfigLoader
    {
        public static AlarmConfig Load(string path, IAppLogger log = null)
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
                cfg.Behavior.AdaptivePermanentOvershootDeltaA = Math.Max(
                    0.1,
                    GetDoubleAttr(
                        behaviorNode,
                        "AdaptivePermanentOvershootDeltaA",
                        cfg.Behavior.AdaptivePermanentOvershootDeltaA));
                cfg.Behavior.AdaptiveOvershootWarningDeltaA = GetDoubleAttr(
                    behaviorNode,
                    "AdaptiveOvershootWarningDeltaA",
                    cfg.Behavior.AdaptiveOvershootWarningDeltaA);
                cfg.Behavior.AdaptiveOvershootConfirmCycles = Math.Max(
                    1,
                    GetIntAttr(
                        behaviorNode,
                        "AdaptiveOvershootConfirmCycles",
                        cfg.Behavior.AdaptiveOvershootConfirmCycles));
                cfg.Behavior.PeakEvidenceMismatchConfirmCycles = Math.Max(
                    1,
                    GetIntAttr(
                        behaviorNode,
                        "PeakEvidenceMismatchConfirmCycles",
                        cfg.Behavior.PeakEvidenceMismatchConfirmCycles));
                cfg.Behavior.AdaptiveForwardStallConfirmCycles = Math.Max(
                    1,
                    GetIntAttr(
                        behaviorNode,
                        "AdaptiveForwardStallConfirmCycles",
                        cfg.Behavior.AdaptiveForwardStallConfirmCycles));
                cfg.Behavior.GenericFaultConfirmCycles = Math.Max(
                    3,
                    GetIntAttr(
                        behaviorNode,
                        "GenericFaultConfirmCycles",
                        cfg.Behavior.GenericFaultConfirmCycles));
                cfg.Behavior.SnapshotLastNCycles = GetIntAttr(behaviorNode, "SnapshotLastNCycles", cfg.Behavior.SnapshotLastNCycles);
                cfg.Behavior.SnapshotCooldownMs = GetIntAttr(behaviorNode, "SnapshotCooldownMs", cfg.Behavior.SnapshotCooldownMs);
                cfg.Behavior.SnapshotPostOffTailMs = Math.Max(
                    0,
                    Math.Min(
                        3000,
                        GetIntAttr(
                            behaviorNode,
                            "SnapshotPostOffTailMs",
                            cfg.Behavior.SnapshotPostOffTailMs)));
            }

            var warningNode = doc.SelectSingleNode("//AlarmConfig/WarningSnapshots") as XmlElement;
            if (warningNode != null)
            {
                cfg.WarningSnapshots.Enabled = GetBoolAttr(warningNode, "Enabled", cfg.WarningSnapshots.Enabled);
                cfg.WarningSnapshots.RootDirectory = GetAttr(
                    warningNode,
                    "RootDirectory",
                    cfg.WarningSnapshots.RootDirectory);
                cfg.WarningSnapshots.SaveCsv = GetBoolAttr(warningNode, "SaveCsv", cfg.WarningSnapshots.SaveCsv);
                cfg.WarningSnapshots.SaveBin = GetBoolAttr(warningNode, "SaveBin", cfg.WarningSnapshots.SaveBin);
                cfg.WarningSnapshots.HardAlarmLastNCycles = Math.Max(
                    1,
                    GetIntAttr(warningNode, "HardAlarmLastNCycles", cfg.WarningSnapshots.HardAlarmLastNCycles));
                cfg.WarningSnapshots.HardAlarmIncludeSameElectricalGroup = GetBoolAttr(
                    warningNode,
                    "HardAlarmIncludeSameElectricalGroup",
                    cfg.WarningSnapshots.HardAlarmIncludeSameElectricalGroup);
                cfg.WarningSnapshots.HardAlarmSameGroupLastNCycles = ParseBoundedIntAttr(
                    warningNode,
                    "HardAlarmSameGroupLastNCycles",
                    10,
                    1,
                    1000,
                    path,
                    log);
                var warningModeText = warningNode.HasAttribute("SoftWarningRetentionMode")
                    ? warningNode.GetAttribute("SoftWarningRetentionMode")
                    : StorageRetentionMode.Count.ToString();
                StorageRetentionMode warningRetentionMode;
                if (string.Equals(warningModeText, "Unlimited", StringComparison.OrdinalIgnoreCase))
                    warningRetentionMode = StorageRetentionMode.Unlimited;
                else if (string.Equals(warningModeText, "Count", StringComparison.OrdinalIgnoreCase))
                    warningRetentionMode = StorageRetentionMode.Count;
                else
                {
                    warningRetentionMode = StorageRetentionMode.Count;
                    log?.Warn(
                        "SoftWarningRetentionMode 非法，已回退 Count。" +
                        $"Value={warningModeText} Path={path}",
                        "配置");
                }
                cfg.WarningSnapshots.SoftWarningRetentionMode = warningRetentionMode;
                cfg.WarningSnapshots.SoftWarningRetainCountPerChannelCode = ParseBoundedIntAttr(
                    warningNode,
                    "SoftWarningRetainCountPerChannelCode",
                    30,
                    1,
                    100000,
                    path,
                    log);
                cfg.WarningSnapshots.SoftWarningQuotaMb = Math.Max(
                    0,
                    GetLongAttr(warningNode, "SoftWarningQuotaMb", cfg.WarningSnapshots.SoftWarningQuotaMb));
                cfg.WarningSnapshots.DiskFreeWarningMb = Math.Max(
                    0,
                    GetLongAttr(warningNode, "DiskFreeWarningMb", cfg.WarningSnapshots.DiskFreeWarningMb));
            }

            log?.Info(
                "报警快照保留策略已生效：" +
                $"AlarmCycles={cfg.WarningSnapshots.HardAlarmLastNCycles} " +
                $"IncludeSameGroup={cfg.WarningSnapshots.HardAlarmIncludeSameElectricalGroup} " +
                $"SameGroupCycles={cfg.WarningSnapshots.HardAlarmSameGroupLastNCycles} " +
                $"WarningMode={cfg.WarningSnapshots.SoftWarningRetentionMode} " +
                $"WarningCount={cfg.WarningSnapshots.SoftWarningRetainCountPerChannelCode} " +
                $"Path={path}",
                "配置");

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

        private static int ParseBoundedIntAttr(
            XmlElement element,
            string name,
            int defaultValue,
            int minimum,
            int maximum,
            string path,
            IAppLogger log)
        {
            if (!element.HasAttribute(name)) return defaultValue;
            var raw = element.GetAttribute(name);
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= minimum && value <= maximum)
                return value;
            log?.Warn(
                $"{name} 非法，已回退 {defaultValue}。Value={raw} Path={path}",
                "配置");
            return defaultValue;
        }

        private static long GetLongAttr(XmlElement e, string name, long def)
            => long.TryParse(GetAttr(e, name, def.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

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
