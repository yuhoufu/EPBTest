using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Config;
using Config.Models;

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
        public List<(double Voltage, double Pressure)> VoltageToPressure { get; } = new();
    }

    public sealed class AoConfig
    {
        public double MinVoltage { get; set; }
        public double MaxVoltage { get; set; } = 10;
        public double MinPressure { get; set; }
        public double MaxPressure { get; set; } = 100;
        public Dictionary<string, AoDevice> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class HydraulicItem
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public HydraulicMode Mode { get; set; }
        public double SetPercent { get; set; }
        public int PressureThresholdBar { get; set; }
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
    public bool IsSameCycleForAllEpb { get; set; }

    public double TestPeriod { get; set; } // 每一圈的控制周期，单位秒

    /// <summary>周期毫秒（由 TestPeriod 推导），例如 10Hz => 100ms。</summary>
    public int PeriodMs => (int)Math.Round(1000.0 * Math.Max(TestPeriod, 0.001));

    public string StoreDir { get; set; }

    /// <summary>
    ///     试验负责人姓名，例如 “张三”。
    ///     用于生成报告、日志标记、任务责任人记录等。
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    ///     此测试的描述信息（备注），例如测试目的、工况说明等。
    ///     可用于界面展示和自动生成报告。
    /// </summary>
    public string Description { get; set; } = string.Empty;


    // 自学习圈数
    public int LearnCycles { get; set; } = 10;


    public OverrunPolicy OverrunPolicy { get; set; } = OverrunPolicy.RunToCompletionSkipMissed;

    public List<HydraulicItem> Hydraulics { get; } = new();
    public List<ElectricalGroup> Groups { get; } = new();

    /// <summary>
    ///     新版 EPB 循环配置（仅每通道记录）。从 TestConfig.xml 的 &lt;EpbCycleRunnerConfig&gt; 读取。
    /// </summary>
    public EpbCycleRunnerConfig EpbCycleRunner { get; set; } = new();


    // ======= 新增：EPB 试验记录集合 =======
    /// <summary>
    ///     12 条 EPB 记录（通道 1..12）。通常由 ConfigLoader 从 XML 读取或第一次启动时 EnsureEpbRecords() 初始化。
    ///     每个记录包含：Id, StartTime, LatestStartTime, RunTime(字符串), TotalCount, RunCount, Status。
    /// </summary>
    public List<EpbTestRecord> EpbRecords { get; set; } = new();


    /// <summary>
    ///     确保 EpbRecords 至少包含 1..12 的记录（按 Id 升序），并返回集合引用。
    ///     调用场景：首次加载配置后补齐，或需要访问某通道记录时使用。
    ///     备注：此方法不会覆盖已有记录（保留 Loader 从 XML 读取的值）。
    /// </summary>
    public List<EpbTestRecord> EnsureEpbRecords(int expectedCount = 12)
    {
        // 若已存在且数量合适则直接返回（但仍保证包含 1..expectedCount 的 id）
        // 需要 using System.Linq;
        var present = new HashSet<int>(EpbRecords.Select(r => r.Id));

        for (var id = 1; id <= expectedCount; id++)
            if (!present.Contains(id))
                EpbRecords.Add(EpbTestRecord.CreateDefault(id));

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

    //EpbTestRecord.cs 中暂未实现Reset(),暂时注释；
    /*/// <summary>
    ///     将指定通道记录重置为初始状态（不删除记录，仅重置字段）。
    ///     线程安全说明：若多个线程可能同时修改记录，请上层加锁或改为并发安全实现。
    /// </summary>
    public void ResetEpbRecord(int channel)
    {
        var r = GetEpbRecord(channel);
        r.Reset();
    }*/

    #region 压力相关

    public HydraulicItem GetHydraulicItemById(int id)
    {
        return Hydraulics.FirstOrDefault(h => h.Id == id) ??
               throw new InvalidOperationException($"HydraulicItem with id {id} not found.");
    }

    #endregion
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
        cfg.MinPressure = GetDouble(doc, "//AOConfig/MinPressure", 0);
        cfg.MaxPressure = GetDouble(doc, "//AOConfig/MaxPressure", 100);

        foreach (XmlNode n in doc.SelectNodes("//AOConfig/Devices/Device")!)
        {
            var d = new AoDevice
            {
                Name = n.SelectSingleNode("Name")?.InnerText?.Trim(),
                PhysicalChannel = n.SelectSingleNode("PhysicalChannel")?.InnerText?.Trim(),
                ScaleK = GetDouble(n, "ScaleK", 1.0),
                Offset = GetDouble(n, "Offset", 0.0)
            };
            foreach (XmlNode p in n.SelectNodes("VoltageToPressureTable/Point")!)
                d.VoltageToPressure.Add((GetDouble(p, "Voltage", 0), GetDouble(p, "Pressure", 0)));

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

        foreach (XmlNode n in doc.SelectNodes("//DOConfig/EPB/Record")!)
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

        foreach (XmlNode n in doc.SelectNodes("//DOConfig/Pressure/Record")!)
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
        cfg.IsSameCycleForAllEpb = GetBool(doc, "//TestConfig/Basic/IsSameCycleForAllEpb", true); // 是否所有EPB使用相同的周期次数：true-是 false-否
        cfg.TestPeriod = GetDouble(doc, "//TestConfig/Basic/TestCycle", 10); // 每圈时长，s
        cfg.LearnCycles = GetInt(doc, "//TestConfig/Basic/LearnCycle", 5); // 自学习圈数，默认5
        cfg.Owner = GetString(doc, "//TestConfig/Basic/Owner", "None");
        cfg.Description = GetString(doc, "//TestConfig/Basic/Description", "None");
        cfg.StoreDir = GetString(doc, "//TestConfig/Basic/StoreDir", "D:\\EPB_Data");


        var policyText = GetString(doc, "//TestConfig/Timer/OverrunPolicy", "RunToCompletionSkipMissed");
        if (!Enum.TryParse(policyText, out OverrunPolicy pol)) pol = OverrunPolicy.RunToCompletionSkipMissed;
        cfg.OverrunPolicy = pol;

        foreach (XmlNode n in doc.SelectNodes("//TestConfig/Hydraulics/Hydraulic")!)
        {
            var h = new HydraulicItem
            {
                Id = GetInt(n, "Id", 1),
                Enabled = GetBool(n, "Enabled", true),
                Mode = ParseMode(GetString(n, "Mode", "ByPressure")),
                SetPercent = GetDouble(n, "SetPercent", 30),
                PressureThresholdBar = (int)GetDouble(n, "PressureThresholdBar", 20),
                DurationMs = GetInt(n, "DurationMs", 0),
                HoldAfterReachedMs = GetInt(n, "HoldAfterReachedMs", 0),
                PressureDoId = GetInt(n, "PressureDoId", 1)
            };

            var membersText = GetString(n, "Members", "");
            var members = ParseIntList(membersText);
            if (members.Count > 0) h.Members.AddRange(members);

            cfg.Hydraulics.Add(h);
        }


        foreach (XmlNode n in doc.SelectNodes("//TestConfig/ElectricalGroups/Group")!)
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


        // 读取 EpbRecords（若存在）
        foreach (XmlNode n in doc.SelectNodes("//TestConfig/EpbRecords/Record")!)
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
            r.Status = Enum.TryParse<EpbTestStatus>(st, out var status) ? status : EpbTestStatus.NotStarted;

            cfg.EpbRecords.Add(r);
        }

        #region 解析读取EpbCycleRunnerConfig

        // ===== 仅解析新版 <EpbCycleRunnerConfig>/<Record> =====
        cfg.EpbCycleRunner = new EpbCycleRunnerConfig();

        foreach (XmlNode r in doc.SelectNodes("//TestConfig/EpbCycleRunnerConfig/Record"))
        {
            var ch = GetInt(r, "Channel", -1);
            if (ch <= 0) continue;

            var item = new EpbCycleRunnerConfig.Record
            {
                Channel = ch,
                Name = GetString(r, "Name", null),
                ForwardA = GetDouble(r, "ForwardA", 0),
                SafetyMarginA = GetDouble(r, "SafetyMarginA", 0),
                FwdOnLimitMs = GetInt(r, "FwdOnLimitMs", 0),
                HoldMs = GetInt(r, "HoldMs", 0),
                RevDecayLimitA = GetDouble(r, "RevDecayLimitA", 0),
                RevDecayRigidMaxMs = GetInt(r, "RevDecayRigidMaxMs", 0),
                RevEmptyFixedMs = GetInt(r, "RevEmptyFixedMs", 0),
                PreReleaseKeepMs = TryGetNullableInt(r, "PreReleaseKeepMs"),
                PeakIgnoreMs = GetInt(r, "PeakIgnoreMs", 0)
            };

            // 软边界钳制（防御性）
            item.FwdOnLimitMs = Math.Max(0, item.FwdOnLimitMs);
            item.HoldMs = Math.Max(0, item.HoldMs);
            item.RevDecayRigidMaxMs = Math.Max(0, item.RevDecayRigidMaxMs);
            item.RevEmptyFixedMs = Math.Max(0, item.RevEmptyFixedMs);

            cfg.EpbCycleRunner.Channels[ch] = item; // 覆盖写入
        }

        #endregion


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

        // ReSharper disable once PossibleNullReferenceException
        foreach (XmlNode formNode in doc.SelectNodes("/UiConfig/*"))
        {
            var form = cfg.GetOrAddForm(formNode.Name);
            foreach (XmlNode n in formNode.SelectNodes("./Controls/Control")!)
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
    
    public static void SaveTest(string path, TestConfig cfg)
    {
        var doc = new XmlDocument();
        doc.Load(path);

        var root = doc.SelectSingleNode("/TestConfig") as XmlElement;
        if (root == null)
            throw new InvalidOperationException("TestConfig.xml 缺少 <TestConfig> 根节点");

        // ===============================
        // 1) 保存 Basic
        // ===============================
        if (root.SelectSingleNode("Basic") is XmlElement basic)
        {
            SetChild(basic, "TestName", cfg.TestName);
            SetChild(basic, "TestTarget", cfg.TestTarget.ToString());
            SetChild(basic, "IsSameCycleForAllEpb", cfg.IsSameCycleForAllEpb ? "true" : "false");
            SetChild(basic, "TestCycle", cfg.TestPeriod.ToString(CultureInfo.InvariantCulture));
            SetChild(basic, "LearnCycle", cfg.LearnCycles.ToString());
            SetChild(basic, "StoreDir", cfg.StoreDir);
            SetChild(basic, "Owner", cfg.Owner);
            SetChild(basic, "Description", cfg.Description);
        }

        // ===============================
        // 2) 保存 Timer/OverrunPolicy
        // ===============================
        var timerNode = root.SelectSingleNode("Timer");
        if (timerNode == null)
        {
            timerNode = doc.CreateElement("Timer");
            root.AppendChild(timerNode);
        }

        if (timerNode is XmlElement timer)
        {
            SetChild(timer, "OverrunPolicy", cfg.OverrunPolicy.ToString());
        }

        // ===============================
        // 3) 保存 Hydraulics（液压配置）
        // ===============================
        var hydraulicsNode = root.SelectSingleNode("Hydraulics");
        if (hydraulicsNode != null) root.RemoveChild(hydraulicsNode);

        hydraulicsNode = doc.CreateElement("Hydraulics");

        foreach (var hydraulic in cfg.Hydraulics.OrderBy(h => h.Id))
        {
            var hydraulicNode = doc.CreateElement("Hydraulic");

            void AddHydraulicElement(string name, string value)
            {
                var n = doc.CreateElement(name);
                n.InnerText = value ?? "";
                hydraulicNode.AppendChild(n);
            }

            AddHydraulicElement("Id", hydraulic.Id.ToString());
            AddHydraulicElement("Enabled", hydraulic.Enabled ? "true" : "false");
            AddHydraulicElement("Mode", hydraulic.Mode.ToString());
            AddHydraulicElement("SetPercent", hydraulic.SetPercent.ToString(CultureInfo.InvariantCulture));
            AddHydraulicElement("PressureThresholdBar", hydraulic.PressureThresholdBar.ToString());
            AddHydraulicElement("DurationMs", hydraulic.DurationMs.ToString());
            AddHydraulicElement("HoldAfterReachedMs", hydraulic.HoldAfterReachedMs.ToString());
            AddHydraulicElement("PressureDoId", hydraulic.PressureDoId.ToString());

            // 保存 Members
            var membersText = string.Join(",", hydraulic.Members.OrderBy(x => x));
            AddHydraulicElement("Members", membersText);

            hydraulicsNode.AppendChild(hydraulicNode);
        }

        root.AppendChild(hydraulicsNode);

        // ===============================
        // 4) 保存 ElectricalGroups（电气组配置）
        // ===============================
        var groupsNode = root.SelectSingleNode("ElectricalGroups");
        if (groupsNode != null) root.RemoveChild(groupsNode);

        groupsNode = doc.CreateElement("ElectricalGroups");

        foreach (var group in cfg.Groups.OrderBy(g => g.Id))
        {
            var groupNode = doc.CreateElement("Group");

            void AddGroupElement(string name, string value)
            {
                var n = doc.CreateElement(name);
                n.InnerText = value ?? "";
                groupNode.AppendChild(n);
            }

            AddGroupElement("Id", group.Id.ToString());
            AddGroupElement("StaggerMs", group.StaggerMs.ToString());

            // 保存 Members
            var membersText = string.Join(",", group.Members.OrderBy(x => x));
            AddGroupElement("Members", membersText);

            groupsNode.AppendChild(groupNode);
        }

        root.AppendChild(groupsNode);

        // ===============================
        // 5) 保存 EpbRecords（EPB测试记录）
        // ===============================
        var epbRecordsNode = root.SelectSingleNode("EpbRecords");
        if (epbRecordsNode != null) root.RemoveChild(epbRecordsNode);

        epbRecordsNode = doc.CreateElement("EpbRecords");

        foreach (var record in cfg.EpbRecords.OrderBy(r => r.Id))
        {
            var recordNode = doc.CreateElement("Record");

            void AddRecordElement(string name, string value)
            {
                var n = doc.CreateElement(name);
                n.InnerText = value ?? "";
                recordNode.AppendChild(n);
            }

            AddRecordElement("Id", record.Id.ToString());
            AddRecordElement("StartTime", record.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
            AddRecordElement("LatestStartTime", record.LatestStartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
            AddRecordElement("RunTime", record.RunTime ?? "");
            AddRecordElement("TotalCount", record.TotalCount.ToString());
            AddRecordElement("RunCount", record.RunCount.ToString());
            AddRecordElement("Status", record.Status.ToString());

            epbRecordsNode.AppendChild(recordNode);
        }

        root.AppendChild(epbRecordsNode);

        // ===============================
        // 6) 保存 EpbCycleRunnerConfig（12条Record）
        // ===============================
        var epbNode = root.SelectSingleNode("EpbCycleRunnerConfig");
        if (epbNode != null) root.RemoveChild(epbNode);

        epbNode = doc.CreateElement("EpbCycleRunnerConfig");

        foreach (var ch in cfg.EpbCycleRunner.Channels.Keys.OrderBy(x => x))
        {
            var it = cfg.EpbCycleRunner.Channels[ch];
            var rec = doc.CreateElement("Record");

            void Add(string name, string value)
            {
                var n = doc.CreateElement(name);
                n.InnerText = value ?? "";
                rec.AppendChild(n);
            }

            Add("Channel", it.Channel.ToString());
            Add("Name", it.Name);
            Add("ForwardA", it.ForwardA.ToString(CultureInfo.InvariantCulture));
            Add("SafetyMarginA", it.SafetyMarginA.ToString(CultureInfo.InvariantCulture));
            Add("FwdOnLimitMs", it.FwdOnLimitMs.ToString());
            Add("HoldMs", it.HoldMs.ToString());
            Add("RevDecayLimitA", it.RevDecayLimitA.ToString(CultureInfo.InvariantCulture));
            Add("RevDecayRigidMaxMs", it.RevDecayRigidMaxMs.ToString());
            Add("RevEmptyFixedMs", it.RevEmptyFixedMs.ToString());
            Add("PreReleaseKeepMs", it.PreReleaseKeepMs?.ToString() ?? "");
            Add("PeakIgnoreMs", it.PeakIgnoreMs.ToString());

            epbNode.AppendChild(rec);
        }

        root.AppendChild(epbNode);

        // ===============================
        // 7) Save to file with temp
        // ===============================
        var tmp = path + ".tmp";
        doc.Save(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
        return;

        // local helper
        static void SetChild(XmlElement parent, string name, string value)
        {
            var node = parent.SelectSingleNode(name) as XmlElement;
            if (node == null)
            {
                node = parent.OwnerDocument!.CreateElement(name);
                parent.AppendChild(node);
            }

            node.InnerText = value ?? "";
        }
    }

    /// <summary>
    /// 使用默认的 Config\TestConfig.xml 保存试验配置（包括 EpbRecords）。
    /// </summary>
    /// <param name="cfg">要保存的试验配置对象。</param>
    public static void SaveTest(TestConfig cfg)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));

        // 与 LoadAll 一样，默认使用当前目录下的 Config 目录
        var configDir = Path.Combine(Environment.CurrentDirectory, "Config");
        Directory.CreateDirectory(configDir);

        var testPath = Path.Combine(configDir, "TestConfig.xml");
        SaveTest(testPath, cfg);
    }

    #region 项目配置相关辅助方法

    // ===============================
    // 项目配置相关辅助方法
    // ===============================

    /// <summary>
    ///     软件默认 Config 目录（程序当前目录下的 Config）。
    /// </summary>
    public static string DefaultConfigDir
    {
        get
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "Config");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    ///     计算“项目根目录”。
    ///     约定：项目根目录 = StoreDir\TestName。
    /// </summary>
    /// <param name="storeDir">试验结果根目录（TestConfig.StoreDir）。</param>
    /// <param name="testName">项目/试验名称（TestConfig.TestName）。</param>
    /// <returns>项目根目录完整路径；若参数无效则返回 null。</returns>
    public static string GetProjectRootDir(string storeDir, string testName)
    {
        storeDir = (storeDir ?? string.Empty).Trim();
        testName = (testName ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(storeDir) || string.IsNullOrEmpty(testName))
            return null;

        return Path.Combine(storeDir, testName);
    }

    /// <summary>
    ///     获取项目下的 Config 目录。
    ///     约定：项目 Config 目录 = StoreDir\TestName\Config。
    /// </summary>
    /// <param name="storeDir">试验结果根目录。</param>
    /// <param name="testName">项目/试验名称。</param>
    /// <returns>项目 Config 目录完整路径；若参数无效则返回 null。</returns>
    public static string GetProjectConfigDir(string storeDir, string testName)
    {
        var root = GetProjectRootDir(storeDir, testName);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Config");
    }

    /// <summary>
    ///     获取项目的 TestConfig.xml 完整路径。
    /// </summary>
    /// <param name="storeDir">试验结果根目录。</param>
    /// <param name="testName">项目/试验名称。</param>
    /// <returns>TestConfig.xml 路径；若参数无效则返回 null。</returns>
    public static string GetProjectTestConfigPath(string storeDir, string testName)
    {
        var cfgDir = GetProjectConfigDir(storeDir, testName);
        return string.IsNullOrEmpty(cfgDir) ? null : Path.Combine(cfgDir, "TestConfig.xml");
    }

    /// <summary>
    ///     基于“默认 Config\TestConfig.xml”，为某个项目创建一份专用的 TestConfig.xml，
    ///     并对其中的 <see cref="TestConfig.EpbRecords"/> 进行“清零但保留目标次数 TotalCount”。
    ///     若项目下已存在 TestConfig.xml，则直接加载并返回。
    /// </summary>
    /// <param name="defaultCfg">
    ///     已经通过 <see cref="LoadAll(string,IAppLogger)"/> 加载的默认全局配置。
    ///     仅使用其中的 TestConfig.TestName 和 TestConfig.StoreDir 推算项目路径。
    /// </param>
    /// <param name="log">可选日志接口，用于输出提示信息。</param>
    /// <returns>项目专用的 TestConfig 实例。</returns>
    public static TestConfig EnsureProjectTestConfig(GlobalConfig defaultCfg, IAppLogger log = null)
    {
        if (defaultCfg == null) throw new ArgumentNullException(nameof(defaultCfg));
        if (defaultCfg.Test == null) throw new ArgumentNullException(nameof(defaultCfg.Test));

        var storeDir = defaultCfg.Test.StoreDir;
        var testName = defaultCfg.Test.TestName;

        var projectTestPath = GetProjectTestConfigPath(storeDir, testName);
        if (string.IsNullOrEmpty(projectTestPath))
        {
            // 没有有效路径时，直接返回默认 TestConfig（退化为原来的单配置模式）
            log?.Warn("StoreDir 或 TestName 为空，无法推算项目配置目录，继续使用默认 TestConfig。", "配置");
            return defaultCfg.Test;
        }

        var projectConfigDir = Path.GetDirectoryName(projectTestPath);
        if (!string.IsNullOrEmpty(projectConfigDir) && !Directory.Exists(projectConfigDir))
            Directory.CreateDirectory(projectConfigDir);

        // 1) 若项目下已存在 TestConfig.xml：直接加载（保留之前运行进度）
        if (File.Exists(projectTestPath))
        {
            log?.Info($"检测到项目配置，加载项目 TestConfig：{projectTestPath}", "配置");
            return LoadTest(projectTestPath, log);
        }

        // 2) 项目第一次使用：基于默认 TestConfig 复制一份，并清零 EpbRecords 运行进度
        var defaultTestPath = Path.Combine(DefaultConfigDir, "TestConfig.xml");
        if (!File.Exists(defaultTestPath))
        {
            // 如果默认文件都不存在，只能用当前内存中的 Test 对象作为模板
            log?.Warn($"默认 TestConfig.xml 不存在，直接使用内存中的 TestConfig 作为模板：{projectTestPath}", "配置");
            var cfg = defaultCfg.Test;
            cfg.EnsureEpbRecords();
            foreach (var rec in cfg.EpbRecords)
                rec.ResetKeepTotalCount(); // 清零进度，保留 TotalCount
            SaveTest(projectTestPath, cfg);
            return cfg;
        }

        // 拷贝一份默认 TestConfig 文件，再装载并清理 EpbRecords 进度
        File.Copy(defaultTestPath, projectTestPath, overwrite: false);
        var projectCfg = LoadTest(projectTestPath, log);

        projectCfg.EnsureEpbRecords();
        foreach (var rec in projectCfg.EpbRecords)
            rec.ResetKeepTotalCount(); // 清零进度，保留目标次数

        SaveTest(projectTestPath, projectCfg);

        log?.Info($"已从默认 TestConfig.xml 创建项目专用配置：{projectTestPath}", "配置");
        return projectCfg;
    }

    /// <summary>
    ///     将当前“项目 TestConfig”的关键信息同步回“软件默认 Config\TestConfig.xml”。
    ///     约定：
    ///     <list type="number">
    ///         <item>1. Basic 区（TestName、TestTarget、StoreDir 等）直接覆盖默认配置；</item>
    ///         <item>2. EpbRecords 仅同步 <see cref="EpbTestRecord.TotalCount"/>；</item>
    ///         <item>3. 同步后会对默认配置的每条 EpbRecord 调用 <see cref="EpbTestRecord.ResetKeepTotalCount"/>，
    ///             以确保默认配置中的进度状态始终为“全新的、仅带目标次数”的模板；</item>
    ///         <item>4. 同步时，将默认配置中每条记录的 StartTime、LatestStartTime
    ///             更新为当前时间（“两个日期更新为当前日期”）。</item>
    ///     </list>
    /// </summary>
    /// <param name="projectTest">当前项目使用的 TestConfig（包含真实运行进度）。</param>
    /// <param name="log">可选日志接口。</param>
    public static void UpdateDefaultTestFromProject(TestConfig projectTest, IAppLogger log = null)
    {
        if (projectTest == null) throw new ArgumentNullException(nameof(projectTest));

        var defaultTestPath = Path.Combine(DefaultConfigDir, "TestConfig.xml");
        if (!File.Exists(defaultTestPath))
        {
            // 默认文件不存在，则以项目配置为基础，清零进度后直接写入默认路径。
            var clone = projectTest;
            clone.EnsureEpbRecords();
            foreach (var rec in clone.EpbRecords)
            {
                rec.ResetKeepTotalCount();
                // 两个日期更新为当前时间
                var now = DateTime.Now;
                rec.StartTime = now;
                rec.LatestStartTime = now;
            }

            SaveTest(defaultTestPath, clone);
            log?.Info($"默认 TestConfig.xml 不存在，已用项目配置初始化：{defaultTestPath}", "配置");
            return;
        }

        // 1) 先加载默认 TestConfig
        var defaultCfg = LoadTest(defaultTestPath, log);

        // 2) 覆盖 Basic 区的核心字段（视为“最后一次使用的项目模板”）
        defaultCfg.TestName = projectTest.TestName;
        defaultCfg.TestTarget = projectTest.TestTarget;
        defaultCfg.IsSameCycleForAllEpb = projectTest.IsSameCycleForAllEpb;
        defaultCfg.TestPeriod = projectTest.TestPeriod;
        defaultCfg.LearnCycles = projectTest.LearnCycles;
        defaultCfg.StoreDir = projectTest.StoreDir;
        defaultCfg.Owner = projectTest.Owner;
        defaultCfg.Description = projectTest.Description;

        // 3) 同步 EpbRecords 的 TotalCount，但重置进度（保持默认配置为“干净模板”）
        defaultCfg.EnsureEpbRecords();
        projectTest.EnsureEpbRecords();

        var nowGlobal = DateTime.Now; // 当前时间（两个日期用同一时间）

        for (var ch = 1; ch <= 12; ch++)
        {
            var src = projectTest.GetEpbRecord(ch);
            var dst = defaultCfg.GetEpbRecord(ch);
            if (src == null || dst == null) continue;

            // 拷贝目标次数
            dst.TotalCount = src.TotalCount;
            // 其他状态全部重置（RunCount=0、Status 重置等）
            dst.ResetKeepTotalCount();

            // 两个日期更新为当前时间
            dst.StartTime = nowGlobal;
            dst.LatestStartTime = nowGlobal;
        }

        // 4) 保存回默认 TestConfig.xml
        SaveTest(defaultTestPath, defaultCfg);
        log?.Info("已将项目 TestConfig 的基本信息和 TotalCount 同步回默认配置（进度已清零，日期已刷新）。", "配置");
    }


    #endregion


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