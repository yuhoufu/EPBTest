using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Config
{
    public sealed class PowerSupplyFleetConfig
    {
        public bool Enabled { get; set; } = true;
        public int PollIntervalMs { get; set; } = 100;
        /// <summary>成功轮询超过该耗时时只记录慢查询预警，不触发电源故障。</summary>
        public int TelemetryDelayWarnMs { get; set; } = 500;
        /// <summary>兼容旧配置及安全证据判鲜；新代码不再用它单次锁存通信故障。</summary>
        public int TelemetryStaleMs { get; set; } = 500;
        /// <summary>连续多少个电源组正式动作槽无成功通信才确认通信故障。</summary>
        public int CommunicationAlarmConfirmCycles { get; set; } = 8;
        /// <summary>没有周期推进时通信降级允许持续的最长墙钟时间。</summary>
        public int CommunicationAlarmMaxMs { get; set; } = 120000;
        /// <summary>通信异常后的重连间隔，避免以100ms节拍冲击设备和网络。</summary>
        public int CommunicationRetryMs { get; set; } = 500;
        public int CcTripMs { get; set; } = 300;
        public int LowVoltageTripMs { get; set; } = 300;
        public double NearLimitWarnRatio { get; set; } = 0.90;
        public double StartupZeroCurrentA { get; set; } = 0.50;
        public int StartupZeroStableMs { get; set; } = 300;
        public int StartupVoltageStableMs { get; set; } = 300;
        public int StartupZeroTimeoutMs { get; set; } = 5000;
        /// <summary>确认全部电源关闭、DO/AO 归零后，继电器触点释放的冷启动稳定等待。</summary>
        public int ColdStartRelaySettleMs { get; set; } = 300;
        public List<PowerSupplyDeviceConfig> Supplies { get; } = new List<PowerSupplyDeviceConfig>();

        public PowerSupplyDeviceConfig GetByGroup(int groupId) =>
            Supplies.SingleOrDefault(x => x.ElectricalGroupId == groupId);
    }

    public sealed class PowerSupplyDeviceConfig
    {
        public int Id { get; set; }
        public int ElectricalGroupId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 2268;
        public string Terminator { get; set; } = "\\r\\n";
        public string ExpectedModel { get; set; } = string.Empty;
        public string ExpectedSerial { get; set; } = string.Empty;
        public double? VoltageV { get; set; }
        public double? CurrentA { get; set; }
        public double? OvpV { get; set; }
        public double? OcpA { get; set; }
        public double SetpointTolerance { get; set; } = 0.05;
        public double MinimumOutputVoltageV { get; set; }
    }

    public static class PowerSupplyConfigLoader
    {
        public static PowerSupplyFleetConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空。", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("未找到程控电源配置。", path);

            var root = XDocument.Load(path).Root ??
                       throw new InvalidDataException("PowerSupplyConfig.xml 缺少根节点。");
            var legacyTelemetryStaleMs = IntAttr(root, "TelemetryStaleMs", 500);
            var config = new PowerSupplyFleetConfig
            {
                Enabled = BoolAttr(root, "Enabled", true),
                PollIntervalMs = IntAttr(root, "PollIntervalMs", 100),
                TelemetryDelayWarnMs = IntAttr(root, "TelemetryDelayWarnMs", legacyTelemetryStaleMs),
                TelemetryStaleMs = legacyTelemetryStaleMs,
                CommunicationAlarmConfirmCycles = IntAttr(root, "CommunicationAlarmConfirmCycles", 8),
                CommunicationAlarmMaxMs = IntAttr(root, "CommunicationAlarmMaxMs", 120000),
                CommunicationRetryMs = IntAttr(root, "CommunicationRetryMs", 500),
                CcTripMs = IntAttr(root, "CcTripMs", 300),
                LowVoltageTripMs = IntAttr(root, "LowVoltageTripMs", 300),
                NearLimitWarnRatio = DoubleAttr(root, "NearLimitWarnRatio", 0.90),
                StartupZeroCurrentA = DoubleAttr(root, "StartupZeroCurrentA", 0.50),
                StartupZeroStableMs = IntAttr(root, "StartupZeroStableMs", 300),
                StartupVoltageStableMs = IntAttr(root, "StartupVoltageStableMs", 300),
                StartupZeroTimeoutMs = IntAttr(root, "StartupZeroTimeoutMs", 5000),
                ColdStartRelaySettleMs = IntAttr(root, "ColdStartRelaySettleMs", 300)
            };

            foreach (var node in root.Elements("Supply"))
            {
                config.Supplies.Add(new PowerSupplyDeviceConfig
                {
                    Id = IntAttr(node, "Id", 0),
                    ElectricalGroupId = IntAttr(node, "ElectricalGroupId", 0),
                    DisplayName = StringAttr(node, "DisplayName", string.Empty),
                    Host = StringAttr(node, "Host", string.Empty),
                    Port = IntAttr(node, "Port", 2268),
                    Terminator = StringAttr(node, "Terminator", "\\r\\n"),
                    ExpectedModel = StringAttr(node, "ExpectedModel", string.Empty),
                    ExpectedSerial = StringAttr(node, "ExpectedSerial", string.Empty),
                    VoltageV = NullableDoubleAttr(node, "VoltageV"),
                    CurrentA = NullableDoubleAttr(node, "CurrentA"),
                    OvpV = NullableDoubleAttr(node, "OvpV"),
                    OcpA = NullableDoubleAttr(node, "OcpA"),
                    SetpointTolerance = DoubleAttr(node, "SetpointTolerance", 0.05),
                    MinimumOutputVoltageV = DoubleAttr(node, "MinimumOutputVoltageV", 0)
                });
            }

            Validate(config);
            return config;
        }

        public static void Validate(PowerSupplyFleetConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!config.Enabled) return;
            var errors = new List<string>();
            if (config.PollIntervalMs < 50) errors.Add("PollIntervalMs 不能小于 50ms。");
            if (config.TelemetryDelayWarnMs < config.PollIntervalMs * 2)
                errors.Add("TelemetryDelayWarnMs 至少应为轮询周期的两倍。");
            if (config.TelemetryStaleMs < config.PollIntervalMs * 2)
                errors.Add("TelemetryStaleMs 至少应为轮询周期的两倍。");
            if (config.CommunicationAlarmConfirmCycles < 1 ||
                config.CommunicationAlarmConfirmCycles > 100)
                errors.Add("CommunicationAlarmConfirmCycles 必须在 1～100 之间。");
            if (config.CommunicationAlarmMaxMs < config.TelemetryDelayWarnMs)
                errors.Add("CommunicationAlarmMaxMs 不能小于 TelemetryDelayWarnMs。");
            if (config.CommunicationRetryMs < config.PollIntervalMs ||
                config.CommunicationRetryMs > 10000)
                errors.Add("CommunicationRetryMs 必须在轮询周期与 10000ms 之间。");
            if (config.CcTripMs < config.PollIntervalMs) errors.Add("CcTripMs 不能小于轮询周期。");
            if (config.LowVoltageTripMs < config.PollIntervalMs) errors.Add("LowVoltageTripMs 不能小于轮询周期。");
            if (config.NearLimitWarnRatio <= 0 || config.NearLimitWarnRatio >= 1)
                errors.Add("NearLimitWarnRatio 必须在 0～1 之间。");
            if (config.StartupZeroCurrentA <= 0)
                errors.Add("StartupZeroCurrentA 必须大于0。");
            if (config.StartupZeroStableMs < config.PollIntervalMs)
                errors.Add("StartupZeroStableMs 不能小于轮询周期。");
            if (config.StartupVoltageStableMs < config.PollIntervalMs)
                errors.Add("StartupVoltageStableMs 不能小于轮询周期。");
            if (config.StartupZeroTimeoutMs <=
                Math.Max(config.StartupZeroStableMs, config.StartupVoltageStableMs))
                errors.Add(
                    "StartupZeroTimeoutMs 必须大于 StartupZeroStableMs 和 StartupVoltageStableMs。");
            if (config.ColdStartRelaySettleMs < 50 || config.ColdStartRelaySettleMs > 5000)
                errors.Add("ColdStartRelaySettleMs 必须在 50～5000ms 之间。");
            if (config.Supplies.Count != 4) errors.Add("必须配置四台程控电源。");

            foreach (var duplicate in config.Supplies.GroupBy(x => x.Id).Where(x => x.Count() > 1))
                errors.Add($"电源 Id={duplicate.Key} 重复。");
            foreach (var duplicate in config.Supplies.GroupBy(x => x.ElectricalGroupId).Where(x => x.Count() > 1))
                errors.Add($"电气组 {duplicate.Key} 绑定了多台电源。");

            foreach (var supply in config.Supplies)
            {
                if (supply.Id < 1 || supply.Id > 4) errors.Add($"电源 Id={supply.Id} 超出 1～4。");
                if (supply.ElectricalGroupId < 1 || supply.ElectricalGroupId > 4)
                    errors.Add($"电源 {supply.Id} 的 ElectricalGroupId 无效。");
                if (string.IsNullOrWhiteSpace(supply.Host)) errors.Add($"电源 {supply.Id} 缺少 Host。");
                if (supply.Port <= 0 || supply.Port > 65535) errors.Add($"电源 {supply.Id} 端口无效。");
                if (!supply.VoltageV.HasValue) errors.Add($"电源 {supply.Id} 缺少 VoltageV。");
                if (!supply.CurrentA.HasValue) errors.Add($"电源 {supply.Id} 缺少 CurrentA。");
                if (!supply.OvpV.HasValue) errors.Add($"电源 {supply.Id} 缺少 OvpV。");
                if (!supply.OcpA.HasValue) errors.Add($"电源 {supply.Id} 缺少 OcpA。");
                if (supply.VoltageV.HasValue && supply.VoltageV.Value <= 0)
                    errors.Add($"电源 {supply.Id} 的 VoltageV 必须大于0。");
                if (supply.CurrentA.HasValue && supply.CurrentA.Value <= 0)
                    errors.Add($"电源 {supply.Id} 的 CurrentA 必须大于0。");
                if (supply.OvpV.HasValue && supply.VoltageV.HasValue &&
                    supply.OvpV.Value <= supply.VoltageV.Value)
                    errors.Add($"电源 {supply.Id} 的 OvpV 必须高于 VoltageV。");
                if (supply.OcpA.HasValue && supply.CurrentA.HasValue &&
                    supply.OcpA.Value <= supply.CurrentA.Value)
                    errors.Add($"电源 {supply.Id} 的 OcpA 必须高于 CurrentA。");
                if (supply.SetpointTolerance <= 0) errors.Add($"电源 {supply.Id} 的回读容差必须大于0。");
                if (supply.MinimumOutputVoltageV <= 0) errors.Add($"电源 {supply.Id} 缺少有效最低输出电压。");
                if (supply.VoltageV.HasValue && supply.MinimumOutputVoltageV >= supply.VoltageV.Value)
                    errors.Add($"电源 {supply.Id} 的最低输出电压必须低于 VSET。");
            }

            if (errors.Count > 0)
                throw new InvalidDataException("程控电源配置无效：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        private static string StringAttr(XElement element, string name, string fallback) =>
            ((string)element.Attribute(name) ?? fallback).Trim();

        private static int IntAttr(XElement element, string name, int fallback) =>
            int.TryParse((string)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        private static double DoubleAttr(XElement element, string name, double fallback) =>
            double.TryParse((string)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;

        private static double? NullableDoubleAttr(XElement element, string name) =>
            double.TryParse((string)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (double?)null;

        private static bool BoolAttr(XElement element, string name, bool fallback) =>
            bool.TryParse((string)element.Attribute(name), out var value) ? value : fallback;
    }
}
