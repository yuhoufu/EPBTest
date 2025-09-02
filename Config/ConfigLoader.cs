using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Config;
using Utils;

namespace Config
{
    #region POCO 模型

    public enum HydraulicMode
    {
        ByPressure,
        ByDuration,
        Either,
        HoldUntilRelease // 新增：建压→保持，等待外部主动释放
    }

    public sealed class AoDevice
    {
        public string Name { get; set; }
        public string PhysicalChannel { get; set; }
        public double ScaleK { get; set; } = 1.0;
        public double Offset { get; set; }
        public List<(double Percent, double Pressure)> PercentToPressure { get; } = new();
    }

    public sealed class AoConfig
    {
        public double MinVoltage { get; set; }
        public double MaxVoltage { get; set; } = 10;
        public double MinPercent { get; set; }
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

        // 新增：该液压路所覆盖的卡钳通道（如 1..6 或 7..12）
        public List<int> Members { get; } = new();

        // 小工具：判断某通道是否在此液压管辖范围
        public bool ContainsChannel(int ch)
        {
            return Members?.Contains(ch) == true;
        }
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

        // 夹紧的时长，ms
        public int HoldMs { get; set; }
    }

    public sealed class EpbCycleRunnerConfig
    {
        /// <summary>上电涌流忽略时间（ms）。建议：80–150，默认 100。</summary>
        public int PeakIgnoreMs { get; set; } = 100;

        /// <summary>空行程电流稳定带宽（A）。建议：0.15–0.30，默认 0.20。</summary>
        public double EmptyBandA { get; set; } = 0.20;

        /// <summary>判稳窗口（ms）。建议：50–100，默认 50。</summary>
        public int StableWinMs { get; set; } = 50;

        /// <summary>EWMA 灵敏度（0~1）。建议：0.15–0.30，默认 0.20。</summary>
        public double EwmaAlpha { get; set; } = 0.20;

        /// <summary>正向空行程均值（A）。默认 0.63。</summary>
        public double EmptyCurrentForwardA { get; set; } = 0.63;

        /// <summary>反向空行程均值（A）。默认 -0.70。</summary>
        public double EmptyCurrentReverseA { get; set; } = -0.70;

        /// <summary>
        ///     （可选）自学习圈数。未配置则由上层决定；配置后用于启动前的自学习。
        /// </summary>
        public int? LearnCycles { get; set; }

        /// <summary>复制当前配置。</summary>
        public EpbCycleRunnerConfig Clone()
        {
            return (EpbCycleRunnerConfig)MemberwiseClone();
        }

        /// <summary>
        ///     用 <paramref name="overrides" /> 覆写当前实例中的非空/有效字段（只对设置过的字段生效）。
        /// </summary>
        public void ApplyOverridesFrom(EpbCycleRunnerConfig overrides)
        {
            if (overrides == null) return;
            // 逐项覆写：数值型若与默认同值也允许覆写（由上层决定何为“默认”）
            PeakIgnoreMs = overrides.PeakIgnoreMs != default ? overrides.PeakIgnoreMs : PeakIgnoreMs;
            EmptyBandA = overrides.EmptyBandA != default ? overrides.EmptyBandA : EmptyBandA;
            StableWinMs = overrides.StableWinMs != default ? overrides.StableWinMs : StableWinMs;
            EwmaAlpha = overrides.EwmaAlpha != default ? overrides.EwmaAlpha : EwmaAlpha;
            if (overrides.EmptyCurrentForwardA != default) EmptyCurrentForwardA = overrides.EmptyCurrentForwardA;
            if (overrides.EmptyCurrentReverseA != default) EmptyCurrentReverseA = overrides.EmptyCurrentReverseA;
            if (overrides.LearnCycles.HasValue) LearnCycles = overrides.LearnCycles;
        }
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
        public int PeriodMs => (int)Math.Round(1000.0 * Math.Max(TestCycleHz, 0.001));

        public EpbCycleRunnerConfig EpbCycleRunner { get; set; } = new();

        /// <summary>每通道覆写参数（仅写差异项）。Key=EPB 通道 1..12。</summary>
        public Dictionary<int, EpbCycleRunnerConfig> EpbRunnerOverrides { get; } = new();

        // —— 内部：供 Loader 写入的“显式 Defaults”缓存（有则优先于根级） —— //
        internal EpbCycleRunnerConfig _defaultsFromXml;


        // ======= 新增：EPB 试验记录集合 =======
        /// <summary>
        /// 12 条 EPB 记录（通道 1..12）。通常由 ConfigLoader 从 XML 读取或第一次启动时 EnsureEpbRecords() 初始化。
        /// 每个记录包含：Id, StartTime, LatestStartTime, RunTime(字符串), TotalCount, RunCount, Status。
        /// </summary>
        public List<EpbTestRecord> EpbRecords { get; } = new();


        /// <summary>
        /// 确保 EpbRecords 至少包含 1..12 的记录（按 Id 升序），并返回集合引用。
        /// 调用场景：首次加载配置后补齐，或需要访问某通道记录时使用。
        /// 备注：此方法不会覆盖已有记录（保留 Loader 从 XML 读取的值）。
        /// </summary>
        public List<EpbTestRecord> EnsureEpbRecords(int expectedCount = 12)
        {
            // 若已存在且数量合适则直接返回（但仍保证包含 1..expectedCount 的 id）
            // 需要 using System.Linq;
            var present = new HashSet<int>(EpbRecords.Select(r => r.Id));

            for (int id = 1; id <= expectedCount; id++)
            {
                if (!present.Contains(id))
                {
                    EpbRecords.Add(EpbTestRecord.CreateDefault(id));
                }
            }

            // 保持稳定顺序：按 Id 升序
            EpbRecords.Sort((a, b) => a.Id.CompareTo(b.Id));
            return EpbRecords;
        }

        /// <summary>获取指定通道的记录（不存在时自动创建并返回）。channel 范围期望 1..12。</summary>
        public EpbTestRecord GetEpbRecord(int channel)
        {
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            EnsureEpbRecords();
            var r = EpbRecords.Find(x => x.Id == channel);
            if (r == null)
            {
                r = EpbTestRecord.CreateDefault(channel);
                EpbRecords.Add(r);
                EpbRecords.Sort((a, b) => a.Id.CompareTo(b.Id));
            }
            return r;
        }

        /// <summary>
        /// 将指定通道记录重置为初始状态（不删除记录，仅重置字段）。
        /// 线程安全说明：若多个线程可能同时修改记录，请上层加锁或改为并发安全实现。
        /// </summary>
        public void ResetEpbRecord(int channel)
        {
            var r = GetEpbRecord(channel);
            r.Reset();
        }
        

        /// <summary>
        ///     取得某通道的“合并后”参数：PerChannel 覆写 &gt; Defaults(若存在) &gt; 全局根级。
        /// </summary>
        /// <param name="channel">EPB 通道 1..12。</param>
        public EpbCycleRunnerConfig GetEpbRunner(int channel)
        {
            // 基于全局克隆
            var merged = EpbCycleRunner?.Clone() ?? new EpbCycleRunnerConfig();

            // 若 loader 已把 <Defaults> 合过来，EpbCycleRunner 就等于 Defaults；
            // 这里仍保留一次覆盖（防御性）：
            if (_defaultsFromXml != null)
                merged.ApplyOverridesFrom(_defaultsFromXml);

            // 最后应用每通道覆写
            if (EpbRunnerOverrides.TryGetValue(channel, out var ov))
                merged.ApplyOverridesFrom(ov);

            // 软边界钳制（与现实现一致）
            merged.PeakIgnoreMs = NumericUtils.Clamp(merged.PeakIgnoreMs, 0, 1000);
            merged.StableWinMs = NumericUtils.Clamp(merged.StableWinMs, 10, 1000);
            merged.EmptyBandA = NumericUtils.Clamp(merged.EmptyBandA, 0.0, 5.0);
            merged.EwmaAlpha = NumericUtils.Clamp(merged.EwmaAlpha, 0.0, 1.0);

            return merged;
        }
    }
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


    public UiConfig UI { get; set; } // UI配置
}

#endregion

/// <summary>
///     统一配置加载器：从四个 XML 文件读取控制所需的全部参数。
/// </summary>
public static class ConfigLoader
{
    // 1) 在 ConfigLoader 类里补这个字段（线程安全用）
    private static readonly object _uiFileLock = new();


    /// <summary>加载 AO/DO/Test 三类配置并组合成 <see cref="GlobalConfig" />。</summary>
    /// <param name="configDir">配置目录（包含 AOConfig.xml/DOConfig.xml/TestConfig.xml）</param>
    /// <param name="log">日志器</param>
    public static GlobalConfig LoadAll(string configDir, IAppLogger log = null)
    {
        log ??= NullLogger.Instance;
        var ao = LoadAO(Path.Combine(configDir, "AOConfig.xml"), log);
        var dO = LoadDO(Path.Combine(configDir, "DOConfig.xml"), log);
        var test = LoadTest(Path.Combine(configDir, "TestConfig.xml"), log);


        var uiPath = Path.Combine(configDir, "UiConfig.xml");
        var ui = LoadUI(uiPath, log);
        return new GlobalConfig { AO = ao, DO = dO, Test = test, UI = ui };
    }

    /// <summary>读取 AOConfig.xml。</summary>
    // ReSharper disable once InconsistentNaming
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
                Offset = GetDouble(n, "Offset", 0.0)
            };
            foreach (XmlNode p in n.SelectNodes("PercentToPressureTable/Point"))
                d.PercentToPressure.Add((GetDouble(p, "Percent", 0), GetDouble(p, "Pressure", 0)));

            if (!string.IsNullOrEmpty(d.Name)) cfg.Devices[d.Name] = d;
        }

        log.Info($"AO 配置加载完成：设备数={cfg.Devices.Count}", "配置");
        return cfg;
    }

    /// <summary>读取 DOConfig.xml（EPB 与 Pressure）。</summary>
    // ReSharper disable once InconsistentNaming
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
                Default = n.SelectSingleNode("默认")?.InnerText?.Trim()
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
                PressureDoId = GetInt(n, "PressureDoId", 1)
            };

            var membersText = GetString(n, "Members", "");
            var members = ParseIntList(membersText);
            if (members.Count > 0) h.Members.AddRange(members);

            cfg.Hydraulics.Add(h);
        }

        foreach (XmlNode n in doc.SelectNodes(@"//TestConfig/EpbCurrentLimits/Record"))
            cfg.EpbLimits.Add(new EpbLimit
            {
                Channel = GetInt(n, "Channel", -1),
                ForwardA = GetDouble(n, "ForwardA", 0),
                ReverseA = GetDouble(n, "ReverseA", 0)
            });

        foreach (XmlNode n in doc.SelectNodes("//TestConfig/ElectricalGroups/Group"))
        {
            var g = new ElectricalGroup
            {
                Id = GetInt(n, "Id", -1),
                StaggerMs = GetInt(n, "StaggerMs", 0)
            };
            var membersText = GetString(n, "Members", "");
            foreach (var s in membersText.Split(new[] { ',', '，', ';', '；', ' ' },
                         StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(s.Trim(), out var ch))
                    g.Members.Add(ch);
            if (g.Id > 0 && g.Members.Count > 0) cfg.Groups.Add(g);
        }

        // EpbCycleRunner 配置 2025-08-21
        // 读取 EpbCycleRunnerConfig

        #region 旧的EpbCycleRunnerConfig-已注释

        /*
        var epbNode = doc.SelectSingleNode("//TestConfig/EpbCycleRunnerConfig");
        if (epbNode != null)
        {
            cfg.EpbCycleRunner = new EpbCycleRunnerConfig
            {
                PeakIgnoreMs = GetInt(epbNode, "PeakIgnoreMs", 100),
                EmptyBandA = GetDouble(epbNode, "EmptyBandA", 0.20),
                StableWinMs = GetInt(epbNode, "StableWinMs", 50),
                EwmaAlpha = GetDouble(epbNode, "EwmaAlpha", 0.20),
                EmptyCurrentForwardA = GetDouble(epbNode, "EmptyCurrentForwardA", 0.63),
                EmptyCurrentReverseA = GetDouble(epbNode, "EmptyCurrentReverseA", -0.70)
            };

            // 软边界校验 & 归一化（友好防御）
            cfg.EpbCycleRunner.PeakIgnoreMs = Math.Max(0, Math.Min(cfg.EpbCycleRunner.PeakIgnoreMs, 1000));
            cfg.EpbCycleRunner.StableWinMs = Math.Max(10, Math.Min(cfg.EpbCycleRunner.StableWinMs, 1000));
            cfg.EpbCycleRunner.EmptyBandA = Math.Max(0.0, Math.Min(cfg.EpbCycleRunner.EmptyBandA, 5.0));
            cfg.EpbCycleRunner.EwmaAlpha = Math.Max(0.0, Math.Min(cfg.EpbCycleRunner.EwmaAlpha, 1.0));
        }
        else
        {
            // 未配置则使用默认（已在 POCO 默认值里给出）
            cfg.EpbCycleRunner = new EpbCycleRunnerConfig();
        }
        */

        #endregion

        // 读取 EpbCycleRunnerConfig
        var epbNode = doc.SelectSingleNode("//TestConfig/EpbCycleRunnerConfig");
        if (epbNode != null)
        {
            // 1) 先按“根级字段”作为全局（老格式兼容）
            cfg.EpbCycleRunner = new EpbCycleRunnerConfig
            {
                PeakIgnoreMs = GetInt(epbNode, "PeakIgnoreMs", 100),
                EmptyBandA = GetDouble(epbNode, "EmptyBandA", 0.20),
                StableWinMs = GetInt(epbNode, "StableWinMs", 50),
                EwmaAlpha = GetDouble(epbNode, "EwmaAlpha", 0.20),
                EmptyCurrentForwardA = GetDouble(epbNode, "EmptyCurrentForwardA", 0.63),
                EmptyCurrentReverseA = GetDouble(epbNode, "EmptyCurrentReverseA", -0.70),
                // 新增可选 LearnCycles
                LearnCycles = TryGetNullableInt(epbNode, "LearnCycles")
            };

            // 2) 若存在 <Defaults>，读取并缓存到 TestConfig._defaultsFromXml，然后合入全局
            var defaultsNode = epbNode.SelectSingleNode("Defaults");
            if (defaultsNode != null)
            {
                var def = new EpbCycleRunnerConfig
                {
                    PeakIgnoreMs = GetInt(defaultsNode, "PeakIgnoreMs", cfg.EpbCycleRunner.PeakIgnoreMs),
                    EmptyBandA = GetDouble(defaultsNode, "EmptyBandA", cfg.EpbCycleRunner.EmptyBandA),
                    StableWinMs = GetInt(defaultsNode, "StableWinMs", cfg.EpbCycleRunner.StableWinMs),
                    EwmaAlpha = GetDouble(defaultsNode, "EwmaAlpha", cfg.EpbCycleRunner.EwmaAlpha),
                    EmptyCurrentForwardA = GetDouble(defaultsNode, "EmptyCurrentForwardA",
                        cfg.EpbCycleRunner.EmptyCurrentForwardA),
                    EmptyCurrentReverseA = GetDouble(defaultsNode, "EmptyCurrentReverseA",
                        cfg.EpbCycleRunner.EmptyCurrentReverseA),
                    LearnCycles = TryGetNullableInt(defaultsNode, "LearnCycles") ?? cfg.EpbCycleRunner.LearnCycles
                };
                cfg._defaultsFromXml = def;

                // 合并到全局
                cfg.EpbCycleRunner = def.Clone();
            }

            // 3) 读取 <PerChannel>/<Record>
            foreach (XmlNode r in epbNode.SelectNodes("PerChannel/Record"))
            {
                var ch = GetInt(r, "Channel", -1);
                if (ch <= 0) continue;

                var ov = new EpbCycleRunnerConfig
                {
                    PeakIgnoreMs = GetInt(r, "PeakIgnoreMs", 0), // 0 表示不覆写
                    EmptyBandA = GetDouble(r, "EmptyBandA", 0),
                    StableWinMs = GetInt(r, "StableWinMs", 0),
                    EwmaAlpha = GetDouble(r, "EwmaAlpha", 0),
                    EmptyCurrentForwardA = GetDouble(r, "EmptyCurrentForwardA", 0),
                    EmptyCurrentReverseA = GetDouble(r, "EmptyCurrentReverseA", 0),
                    LearnCycles = TryGetNullableInt(r, "LearnCycles")
                };
                cfg.EpbRunnerOverrides[ch] = ov;
            }

            // 4) 软边界（保留你现有的友好防御）
            cfg.EpbCycleRunner.PeakIgnoreMs = Math.Max(0, Math.Min(cfg.EpbCycleRunner.PeakIgnoreMs, 1000));
            cfg.EpbCycleRunner.StableWinMs = Math.Max(10, Math.Min(cfg.EpbCycleRunner.StableWinMs, 1000));
            cfg.EpbCycleRunner.EmptyBandA = Math.Max(0.0, Math.Min(cfg.EpbCycleRunner.EmptyBandA, 5.0));
            cfg.EpbCycleRunner.EwmaAlpha = Math.Max(0.0, Math.Min(cfg.EpbCycleRunner.EwmaAlpha, 1.0));
        }
        else
        {
            cfg.EpbCycleRunner = new EpbCycleRunnerConfig();
        }



        // 读取 EpbRecords（若存在）
        foreach (XmlNode n in doc.SelectNodes("//TestConfig/EpbRecords/Record"))
        {
            var r = new EpbTestRecord
            {
                Id = GetInt(n, "Id", -1),
                StartTime = TryParseDateTime(GetString(n, "StartTime", null)),
                LatestStartTime = TryParseDateTime(GetString(n, "LatestStartTime", null)),
                RunTime = GetString(n, "RunTime", FormatTimeSpan(TimeSpan.Zero)),
                TotalCount = GetInt(n, "TotalCount", 0),
                RunCount = GetInt(n, "RunCount", 0)
            };

            // 状态解析（容错）
            var st = GetString(n, "Status", "NotStarted");
            if (Enum.TryParse<EpbTestStatus>(st, out var status)) r.Status = status;
            else r.Status = EpbTestStatus.NotStarted;

            cfg.EpbRecords.Add(r);
        }

        // 辅助（DateTime 解析）
        static DateTime? TryParseDateTime(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt)) return dt;
            if (DateTime.TryParse(s, out dt)) return dt;
            return null;
        }




        log?.Info(
            $"Test 配置加载完成：周期={cfg.PeriodMs}ms，目标次数={cfg.TestTarget}，液压={cfg.Hydraulics.Count} 路，组数={cfg.Groups.Count}",
            "配置");
        return cfg;
    }

    
    public static string FormatTimeSpan(TimeSpan ts)
    {
        return ts.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    // —— 辅助：可空 Int 读取 —— //
    private static int? TryGetNullableInt(XmlNode node, string name)
    {
        var s = node.SelectSingleNode(name)?.InnerText?.Trim();
        return int.TryParse(s, out var v) ? v : null;
    }

    private static List<int> ParseIntList(string text)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        foreach (var tok in text.Split(new[] { ',', '，', ';', '；', ' ' },
                     StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok.Trim(), out var v) && v > 0)
                list.Add(v);

        // 去重 + 排序，确保稳定性
        return list.Distinct().OrderBy(x => x).ToList();
    }

    #region UIConfig 相关方法

    // <summary>
    /// 获取最终路径。如果传 null/空，就返回默认路径：程序当前目录\Config\UiConfig.xml
    /// </summary>
    private static string ResolveUiPath(string path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        var cfgDir = Path.Combine(Environment.CurrentDirectory, "Config");
        if (!Directory.Exists(cfgDir))
            Directory.CreateDirectory(cfgDir);
        return Path.Combine(cfgDir, "UiConfig.xml");
    }

    // ========== Load ==========
    public static UiConfig LoadUI(string path, IAppLogger log = null)
    {
        path = ResolveUiPath(path);

        var cfg = new UiConfig();
        if (!File.Exists(path))
            return cfg;

        var doc = new XmlDocument();
        doc.Load(path);

        foreach (XmlNode formNode in doc.SelectNodes("/UiConfig/*"))
        {
            var form = cfg.GetOrAddForm(formNode.Name);
            foreach (XmlNode n in formNode.SelectNodes("./Controls/Control"))
            {
                var name = n.Attributes?["Name"]?.Value?.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                bool TryAttr(string key, bool dft)
                {
                    var v = n.Attributes?[key]?.Value?.Trim();
                    if (string.IsNullOrEmpty(v)) return dft;
                    if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                    return dft;
                }

                var c = new UiControlState
                {
                    Name = name,
                    Checked = TryAttr("Checked", false),
                    Enabled = TryAttr("Enabled", true),
                    DefaultChecked = TryAttr("DefaultChecked", false)
                };
                form.Controls[name] = c;
            }
        }

        log?.Info($"UI 配置加载完成：表单数={cfg.Forms.Count}", "配置");
        return cfg;
    }

    // 不带 path 的重载
    public static UiConfig LoadUI(IAppLogger log = null)
    {
        return LoadUI(null, log);
    }


    // ========== Save ==========
    public static void SaveUI(string path, UiConfig cfg)
    {
        path = ResolveUiPath(path);

        lock (_uiFileLock)
        {
            var doc = new XmlDocument();
            var decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(decl);

            var root = doc.CreateElement("UiConfig");
            doc.AppendChild(root);

            foreach (var kv in cfg.Forms.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var formNode = doc.CreateElement(kv.Key);
                var ctrl = doc.CreateElement("Controls");
                formNode.AppendChild(ctrl);

                foreach (var c in kv.Value.Controls.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var n = doc.CreateElement("Control");

                    var attr = doc.CreateAttribute("Name");
                    attr.Value = c.Name;
                    n.Attributes.Append(attr);

                    attr = doc.CreateAttribute("Checked");
                    attr.Value = c.Checked ? "true" : "false";
                    n.Attributes.Append(attr);

                    attr = doc.CreateAttribute("Enabled");
                    attr.Value = c.Enabled ? "true" : "false";
                    n.Attributes.Append(attr);

                    attr = doc.CreateAttribute("DefaultChecked");
                    attr.Value = c.DefaultChecked ? "true" : "false";
                    n.Attributes.Append(attr);

                    ctrl.AppendChild(n);
                }

                root.AppendChild(formNode);
            }

            var tmp = path + ".tmp";
            doc.Save(tmp);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // 不带 path 的重载
    public static void SaveUI(UiConfig cfg)
    {
        SaveUI(null, cfg);
    }


    // ========== Update ==========
    public static void UpdateUIChecked(string path, UiConfig cfg, string formName, string ctrlName, bool isChecked,
        bool? enabled = null)
    {
        path = ResolveUiPath(path);

        var form = cfg.GetOrAddForm(formName);
        var c = form.GetOrAdd(ctrlName);
        c.Checked = isChecked;
        if (enabled.HasValue) c.Enabled = enabled.Value;
        SaveUI(path, cfg);
    }

    // 不带 path 的重载
    public static void UpdateUIChecked(UiConfig cfg, string formName, string ctrlName, bool isChecked,
        bool? enabled = null)
    {
        UpdateUIChecked(null, cfg, formName, ctrlName, isChecked, enabled);
    }


    public static void UpdateUIDefaultChecked(string path, UiConfig cfg, string formName, string ctrlName,
        bool defaultChecked)
    {
        path = ResolveUiPath(path);

        var form = cfg.GetOrAddForm(formName);
        var c = form.GetOrAdd(ctrlName);
        c.DefaultChecked = defaultChecked;
        SaveUI(path, cfg);
    }

    // 不带 path 的重载
    public static void UpdateUIDefaultChecked(UiConfig cfg, string formName, string ctrlName, bool defaultChecked)
    {
        UpdateUIDefaultChecked(null, cfg, formName, ctrlName, defaultChecked);
    }

    #endregion


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
            "HoldUntilRelease" => HydraulicMode.HoldUntilRelease,
            _ => HydraulicMode.ByPressure
        };
    }




    #endregion
}