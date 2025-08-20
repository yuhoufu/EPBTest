using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Config
{
    #region POCO 模型

    public enum HydraulicMode
    {
        ByPressure,
        ByDuration,
        Either
    }

    public sealed class AoDevice
    {
        public string Name { get; set; }
        public string PhysicalChannel { get; set; }
        public double ScaleK { get; set; } = 1.0;
        public double Offset { get; set; } = 0.0;
        public List<(double Percent, double Pressure)> PercentToPressure { get; } = new List<(double, double)>();
    }

    public sealed class AoConfig
    {
        public double MinVoltage { get; set; } = 0;
        public double MaxVoltage { get; set; } = 10;
        public double MinPercent { get; set; } = 0;
        public double MaxPercent { get; set; } = 100;
        public Dictionary<string, AoDevice> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class HydraulicItem
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public HydraulicMode Mode { get; set; }
        public double SetPercent { get; set; }
        public double PressureThresholdBar { get; set; }
        public int DurationMs { get; set; }
        public int HoldAfterReachedMs { get; set; }
        public int PressureDoId { get; set; }
    }

    public sealed class ElectricalGroup
    {
        public int Id { get; set; }
        public int StaggerMs { get; set; }
        public List<int> Members { get; } = new();
    }

    public sealed class EpbLimit
    {
        public int Channel { get; set; }
        public double ForwardA { get; set; }
        public double ReverseA { get; set; }
    }

    public enum OverrunPolicy
    {
        RunToCompletionSkipMissed,
        SkipNextIfOverrun,
        AlignToWallClock,
        Throw
    }

    public sealed class TestConfig
    {
        public string TestName { get; set; }
        public int TestTarget { get; set; }
        public double TestCycleHz { get; set; } // 每秒次数
        public string StoreDir { get; set; }
        public OverrunPolicy OverrunPolicy { get; set; } = OverrunPolicy.RunToCompletionSkipMissed;

        public List<HydraulicItem> Hydraulics { get; } = new();
        public List<EpbLimit> EpbLimits { get; } = new();
        public List<ElectricalGroup> Groups { get; } = new();

        /// <summary>周期毫秒（由 TestCycleHz 推导），例如 10Hz => 100ms。</summary>
        public int PeriodMs => (int)Math.Round(1000.0 / Math.Max(TestCycleHz, 0.001));
    }

    public sealed class DoEpbRecord
    {
        public bool Enabled { get; set; }
        public int Channel { get; set; }
        public string Pos { get; set; }
        public string Neg { get; set; }
        public string Default { get; set; } // 正/反/全关
        public int? PowerGroup { get; set; } // 可选
        public int? HydraulicId { get; set; } // 可选
    }

    public sealed class DoPressureRecord
    {
        public bool Enabled { get; set; }
        public int Id { get; set; }
        public string Physical { get; set; }
        public int DefaultValue { get; set; } // 0/1
    }

    public sealed class DoConfig
    {
        public List<DoEpbRecord> Epb { get; } = new();
        public List<DoPressureRecord> Pressure { get; } = new();
    }

    public sealed class GlobalConfig
    {
        public AoConfig AO { get; set; }
        public DoConfig DO { get; set; }

        public TestConfig Test { get; set; }
        // AIConfig 在此不做强约束（你已有采集管线，按参数名取流即可）
    }

    #endregion

    /// <summary>
    /// 统一配置加载器：从四个 XML 文件读取控制所需的全部参数。
    /// </summary>
    public static class ConfigLoader
    {
        /// <summary>加载 AO/DO/Test 三类配置并组合成 <see cref="GlobalConfig"/>。</summary>
        /// <param name="configDir">配置目录（包含 AOConfig.xml/DOConfig.xml/TestConfig.xml）</param>
        /// <param name="log">日志器</param>
        public static GlobalConfig LoadAll(string configDir, IAppLogger log = null)
        {
            log ??= NullLogger.Instance;
            var ao = LoadAO(System.IO.Path.Combine(configDir, "AOConfig.xml"), log);
            var dO = LoadDO(System.IO.Path.Combine(configDir, "DOConfig.xml"), log);
            var test = LoadTest(System.IO.Path.Combine(configDir, "TestConfig.xml"), log);
            return new GlobalConfig { AO = ao, DO = dO, Test = test };
        }

        /// <summary>读取 AOConfig.xml。</summary>
        public static AoConfig LoadAO(string path, IAppLogger log)
        {
            var cfg = new AoConfig();
            var doc = new XmlDocument();
            doc.Load(path);

            cfg.MinVoltage = GetDouble(doc, "//AOConfig/MinVoltage", 0);
            cfg.MaxVoltage = GetDouble(doc, "//AOConfig/MaxVoltage", 10);
            cfg.MinPercent = GetDouble(doc, "//AOConfig/MinPercent", 0);
            cfg.MaxPercent = GetDouble(doc, "//AOConfig/MaxPercent", 100);

            foreach (XmlNode n in doc.SelectNodes("//AOConfig/Devices/Device"))
            {
                var d = new AoDevice
                {
                    Name = n.SelectSingleNode("Name")?.InnerText?.Trim(),
                    PhysicalChannel = n.SelectSingleNode("PhysicalChannel")?.InnerText?.Trim(),
                    ScaleK = GetDouble(n, "ScaleK", 1.0),
                    Offset = GetDouble(n, "Offset", 0.0),
                };
                foreach (XmlNode p in n.SelectNodes("PercentToPressureTable/Point"))
                {
                    d.PercentToPressure.Add((GetDouble(p, "Percent", 0), GetDouble(p, "Pressure", 0)));
                }

                if (!string.IsNullOrEmpty(d.Name)) cfg.Devices[d.Name] = d;
            }

            log.Info($"AO 配置加载完成：设备数={cfg.Devices.Count}", "配置");
            return cfg;
        }

        /// <summary>读取 DOConfig.xml（EPB 与 Pressure）。</summary>
        public static DoConfig LoadDO(string path, IAppLogger log)
        {
            var cfg = new DoConfig();
            var doc = new XmlDocument();
            doc.Load(path);

            foreach (XmlNode n in doc.SelectNodes("//DOConfig/EPB/Record"))
            {
                var r = new DoEpbRecord
                {
                    Enabled = GetInt(n, "是否启用", 1) == 1,
                    Channel = GetInt(n, "通道号", -1),
                    Pos = n.SelectSingleNode("正")?.InnerText?.Trim(),
                    Neg = n.SelectSingleNode("反")?.InnerText?.Trim(),
                    Default = n.SelectSingleNode("默认")?.InnerText?.Trim(),
                };
                if (int.TryParse(n.SelectSingleNode("电源组")?.InnerText, out var g)) r.PowerGroup = g;
                if (int.TryParse(n.SelectSingleNode("液压编号")?.InnerText, out var h)) r.HydraulicId = h;
                if (r.Enabled && r.Channel > 0) cfg.Epb.Add(r);
            }

            foreach (XmlNode n in doc.SelectNodes("//DOConfig/Pressure/Record"))
            {
                var r = new DoPressureRecord
                {
                    Enabled = GetInt(n, "是否启用", 1) == 1,
                    Id = GetInt(n, "编号", -1),
                    Physical = n.SelectSingleNode("物理通道")?.InnerText?.Trim(),
                    DefaultValue = GetInt(n, "默认值", 0)
                };
                if (r.Enabled && r.Id > 0) cfg.Pressure.Add(r);
            }

            return cfg;
        }

        /// <summary>读取 TestConfig.xml（高精度定时策略、液压三模式、EPB 阈值、组内错峰）。</summary>
        public static TestConfig LoadTest(string path, IAppLogger log)
        {
            var cfg = new TestConfig();
            var doc = new XmlDocument();
            doc.Load(path);

            cfg.TestName = GetString(doc, "//TestConfig/Basic/TestName", "EPB");
            cfg.TestTarget = (int)GetDouble(doc, "//TestConfig/Basic/TestTarget", 1);
            cfg.TestCycleHz = GetDouble(doc, "//TestConfig/Basic/TestCycle", 10); // Hz
            cfg.StoreDir = GetString(doc, "//TestConfig/Basic/StoreDir", "D:\\EPB_Data");

            var policyText = GetString(doc, "//TestConfig/Timer/OverrunPolicy", "RunToCompletionSkipMissed");
            if (!Enum.TryParse(policyText, out OverrunPolicy pol)) pol = OverrunPolicy.RunToCompletionSkipMissed;
            cfg.OverrunPolicy = pol;

            foreach (XmlNode n in doc.SelectNodes("//TestConfig/Hydraulics/Hydraulic"))
            {
                var h = new HydraulicItem
                {
                    Id = GetInt(n, "Id", 1),
                    Enabled = GetBool(n, "Enabled", true),
                    Mode = ParseMode(GetString(n, "Mode", "ByPressure")),
                    SetPercent = GetDouble(n, "SetPercent", 30),
                    PressureThresholdBar = GetDouble(n, "PressureThresholdBar", 20),
                    DurationMs = GetInt(n, "DurationMs", 0),
                    HoldAfterReachedMs = GetInt(n, "HoldAfterReachedMs", 0),
                    PressureDoId = GetInt(n, "PressureDoId", 1),
                };
                cfg.Hydraulics.Add(h);
            }

            foreach (XmlNode n in doc.SelectNodes(@"//TestConfig/EpbCurrentLimits/Record"))
            {
                cfg.EpbLimits.Add(new EpbLimit
                {
                    Channel = GetInt(n, "Channel", -1),
                    ForwardA = GetDouble(n, "ForwardA", 0),
                    ReverseA = GetDouble(n, "ReverseA", 0),
                });
            }

            foreach (XmlNode n in doc.SelectNodes("//TestConfig/ElectricalGroups/Group"))
            {
                var g = new ElectricalGroup
                {
                    Id = GetInt(n, "Id", -1),
                    StaggerMs = GetInt(n, "StaggerMs", 0),
                };
                var membersText = GetString(n, "Members", "");
                foreach (var s in membersText.Split(new[] { ',', '，', ';', '；', ' ' },
                             StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(s.Trim(), out var ch))
                        g.Members.Add(ch);
                if (g.Id > 0 && g.Members.Count > 0) cfg.Groups.Add(g);
            }

            log?.Info(
                $"Test 配置加载完成：周期={cfg.PeriodMs}ms，目标次数={cfg.TestTarget}，液压={cfg.Hydraulics.Count} 路，组数={cfg.Groups.Count}",
                "配置");
            return cfg;
        }

        #region XML Helpers

        private static double GetDouble(XmlNode n, string xpath, double dft)
        {
            var s = n.SelectSingleNode(xpath)?.InnerText?.Trim();
            return double.TryParse(s, out var v) ? v : dft;
        }

        private static int GetInt(XmlNode n, string xpath, int dft)
        {
            var s = n.SelectSingleNode(xpath)?.InnerText?.Trim();
            return int.TryParse(s, out var v) ? v : dft;
        }

        private static string GetString(XmlNode n, string xpath, string dft)
        {
            var s = n.SelectSingleNode(xpath)?.InnerText?.Trim();
            return string.IsNullOrEmpty(s) ? dft : s;
        }

        private static bool GetBool(XmlNode n, string xpath, bool dft)
        {
            var s = n.SelectSingleNode(xpath)?.InnerText?.Trim();
            if (string.IsNullOrEmpty(s)) return dft;
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return dft;
        }

        private static HydraulicMode ParseMode(string text)
        {
            return text switch
            {
                "ByPressure" => HydraulicMode.ByPressure,
                "ByDuration" => HydraulicMode.ByDuration,
                "Either" => HydraulicMode.Either,
                _ => HydraulicMode.ByPressure
            };
        }

        #endregion
    }
}