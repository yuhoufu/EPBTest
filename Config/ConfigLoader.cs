using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>建压合格窗口为目标压力 ± 此容差；旧项目缺少节点时缺省为 10 bar。</summary>
        public double PressureToleranceBar { get; set; } = 10;
        public int DurationMs { get; set; }
        public int HoldAfterReachedMs { get; set; }
        public double ReleaseSafePressureBar { get; set; } = 5;
        public int ReleaseStableMs { get; set; } = 100;
        public int ReleaseTimeoutMs { get; set; } = 5000;
        public int BuildTimeoutMs { get; set; } = 5000;
        public int BuildStableMs { get; set; } = 200;
        public int PressureSampleMaxAgeMs { get; set; } = 100;
        /// <summary>
        /// 保压下降容差。保压不得比初始建压窗口更严格；旧项目中的较小值会在运行时
        /// 自动提升到 PressureToleranceBar，避免“60bar 可建压、65bar 又立即停机”。
        /// </summary>
        public double HoldDropToleranceBar { get; set; } = 10;
        /// <summary>保压低压连续确认时间；运行时下限为 1000ms，过滤负载切换瞬态。</summary>
        public int HoldDropConfirmMs { get; set; } = 1000;
        public double EffectiveHoldDropToleranceBar =>
            Math.Max(Math.Max(0, PressureToleranceBar), Math.Max(0, HoldDropToleranceBar));
        public int EffectiveHoldDropConfirmMs => Math.Max(1000, HoldDropConfirmMs);
        /// <summary>
        /// 成员到达液压释放屏障的显式下限。运行时至少使用
        /// 2×PeriodMs + ReleaseTimeoutMs，避免把慢卡钳等待误报为液压故障。
        /// </summary>
        public int BarrierTimeoutMs { get; set; }
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
    private readonly object _epbRecordsGate = new();

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

    /// <summary>
    /// 上一正式槽等待安全终态的超时。0 表示自动使用 max(2×PeriodMs, 30000ms)；
    /// 显式值不得缩短自动安全窗口，且最长限制为 10 分钟。
    /// </summary>
    public int FormalSlotClosureTimeoutMs { get; set; }

    public int EffectiveFormalSlotClosureTimeoutMs
    {
        get
        {
            var automatic = Math.Max(30000L, Math.Max(1L, PeriodMs) * 2L);
            var requested = FormalSlotClosureTimeoutMs > 0
                ? FormalSlotClosureTimeoutMs
                : automatic;
            return (int)Math.Min(600000L, Math.Max(automatic, requested));
        }
    }

    public List<HydraulicItem> Hydraulics { get; } = new();
    public List<ElectricalGroup> Groups { get; } = new();

    /// <summary>
    ///     新版 EPB 循环配置（仅每通道记录）。从 TestConfig.xml 的 &lt;EpbCycleRunnerConfig&gt; 读取。
    /// </summary>
    public EpbCycleRunnerConfig EpbCycleRunner { get; set; } = new();

    /// <summary>项目数据副本保留策略；旧项目缺少节点时使用安全默认值。</summary>
    public DataStorageRetentionConfig DataStorageRetention { get; set; } = new();


    // ======= 新增：EPB 试验记录集合 =======
    /// <summary>
    ///     12 条 EPB 记录（通道 1..12）。通常由 ConfigLoader 从 XML 读取或第一次启动时 EnsureEpbRecords() 初始化。
    ///     每个记录包含：Id, StartTime, LatestStartTime, RunTime(字符串), TotalCount, RunCount, Status。
    /// </summary>
    public EpbTestRecordCollection EpbRecords { get; } = new();


    /// <summary>
    ///     将 EpbRecords 归一化为 1..expectedCount 每个通道恰好一条记录（按 Id 升序），并返回集合引用。
    ///     调用场景：首次加载配置后补齐，或需要访问某通道记录时使用。
    ///     备注：历史文件若含重复 Id，会合并单调运行证据并保留任一副本的启用授权。
    /// </summary>
    public EpbTestRecordCollection EnsureEpbRecords(int expectedCount = 12)
    {
        if (expectedCount <= 0) throw new ArgumentOutOfRangeException(nameof(expectedCount));

        lock (_epbRecordsGate)
        {
            var normalized = EpbRecords.Snapshot()
                .Where(record => record != null && record.Id >= 1 && record.Id <= expectedCount)
                .GroupBy(record => record.Id)
                .Select(group => MergeDuplicateEpbRecords(group))
                .ToDictionary(record => record.Id);

            for (var id = 1; id <= expectedCount; id++)
                if (!normalized.ContainsKey(id))
                    normalized.Add(id, EpbTestRecord.CreateDefault(id));

            EpbRecords.ReplaceAll(normalized.Values.OrderBy(record => record.Id));
            return EpbRecords;
        }
    }

    internal static EpbTestRecord MergeDuplicateEpbRecords(
        IEnumerable<EpbTestRecord> duplicates)
    {
        var records = duplicates.ToList();
        var primary = records
            .OrderByDescending(record => record.EffectiveMechanicalCycleCount)
            .ThenByDescending(record => record.RunCount)
            .ThenByDescending(record => record.RunTimeSpan)
            .ThenByDescending(record => record.Status != EpbTestStatus.NotStarted)
            .ThenBy(record => record.StartTime ?? DateTime.MaxValue)
            .First();

        // 重复行是同一通道的多份快照，计数和时长只能取单调最大值，不能相加。
        // 永久报警是比 Enabled 更强的安全事实；存在任一锁存副本时禁止重复行
        // 合并逻辑用旧的 Enabled=true 覆盖报警禁用。
        var permanent = records
            .Where(record => record.PermanentAlarmLatched)
            .OrderByDescending(record => record.PermanentAlarmUtc ?? DateTime.MinValue)
            .FirstOrDefault();
        var fullRelearning = records
            .Where(record => record.OperatorFullRelearningRequired)
            .OrderByDescending(record =>
                record.OperatorFullRelearningUtc ?? DateTime.MinValue)
            .FirstOrDefault();
        primary.Enabled = permanent == null && records.Any(record => record.Enabled);
        primary.TotalCount = records.Max(record => Math.Max(0, record.TotalCount));
        primary.RunCount = records.Max(record => Math.Max(0, record.RunCount));
        primary.MechanicalCycleCount = records.Max(record =>
            Math.Max(record.MechanicalCycleCount, record.RunCount));
        primary.RunTimeSpan = records.Max(record => record.RunTimeSpan);
        primary.ConsecutivePeriodOverrunCount = records.Max(record =>
            Math.Max(0, record.ConsecutivePeriodOverrunCount));
        primary.LastPeriodOverrunUtc = records
            .Where(record => record.LastPeriodOverrunUtc.HasValue)
            .Select(record => record.LastPeriodOverrunUtc)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        if (permanent != null)
        {
            primary.PermanentAlarmLatched = true;
            primary.PermanentAlarmCode = permanent.PermanentAlarmCode;
            primary.PermanentAlarmReason = permanent.PermanentAlarmReason;
            primary.PermanentAlarmUtc = permanent.PermanentAlarmUtc;
            primary.PermanentAlarmCorrelationId = permanent.PermanentAlarmCorrelationId;
            primary.Status = EpbTestStatus.Alarm;
        }
        if (fullRelearning != null)
        {
            primary.OperatorFullRelearningRequired = true;
            primary.OperatorFullRelearningReason =
                fullRelearning.OperatorFullRelearningReason;
            primary.OperatorFullRelearningUtc =
                fullRelearning.OperatorFullRelearningUtc;
            primary.OperatorFullRelearningCorrelationId =
                fullRelearning.OperatorFullRelearningCorrelationId;
        }
        primary.StartTime = records
            .Where(record => record.StartTime.HasValue)
            .Select(record => record.StartTime)
            .OrderBy(value => value)
            .FirstOrDefault();

        if (!primary.LatestStartTime.HasValue)
        {
            primary.LatestStartTime = records
                .Where(record => record.LatestStartTime.HasValue)
                .Select(record => record.LatestStartTime)
                .OrderByDescending(value => value)
                .FirstOrDefault();
        }

        return primary;
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
    /// 在一个锁边界内生成卡钳启动计划。返回值是新的字典快照，后续 UI/保存线程
    /// 对记录集合的修改不会改变本次启动身份，也不可能产生重复键。
    /// </summary>
    public Dictionary<int, int> CreateEpbStartPlan(
        int fallbackTotalCount,
        int expectedCount = 12)
    {
        if (expectedCount <= 0) throw new ArgumentOutOfRangeException(nameof(expectedCount));
        lock (_epbRecordsGate)
        {
            EnsureEpbRecords(expectedCount);
            var snapshot = EpbRecords.Snapshot();
            var expectedIds = Enumerable.Range(1, expectedCount).ToArray();
            var actualIds = snapshot.Select(record => record.Id).OrderBy(id => id).ToArray();
            if (snapshot.Length != expectedCount || !actualIds.SequenceEqual(expectedIds))
                throw new InvalidOperationException(
                    "ConfigEpbRecordInvariant: EpbRecords 必须包含 1.." + expectedCount +
                    " 每通道恰好一条记录。");

            return snapshot.ToDictionary(
                record => record.Id,
                record => record.GetRemainingMechanicalCycles(fallbackTotalCount));
        }
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
    public static LastProjectRestoreResult LastProjectRestoreResult { get; private set; }

    /// <summary>
    /// 当前已经解析并接管的项目根目录。项目日志适配器据此开始持久化，
    /// 避免在项目配置尚未确定时误写到默认模板目录。
    /// </summary>
    public static string CurrentProjectRootDir { get; private set; }

    // 1) 在 ConfigLoader 类里补这个字段（线程安全用）
    private static readonly ConcurrentDictionary<string, object> UiFileLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> TestFileLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, UiSaveDebouncer> UiSaveDebouncers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每个配置文件最多保留一个300ms单次计时器。高频勾选只更新版本和最终快照，
    /// 不再为每次变化创建一个 Task.Delay/线程池任务。
    /// </summary>
    private sealed class UiSaveDebouncer
    {
        private readonly object _gate = new();
        private readonly string _path;
        private readonly Timer _timer;
        private UiConfig _latest;
        private long _version;
        private int _flushRunning;
        private bool _retired;

        internal UiSaveDebouncer(string path)
        {
            _path = path;
            _timer = new Timer(_ => FlushLatest(), null, Timeout.Infinite, Timeout.Infinite);
        }

        internal bool Schedule(UiConfig config)
        {
            lock (_gate)
            {
                if (_retired) return false;
                _latest = config;
                _version++;
                _timer.Change(300, Timeout.Infinite);
                return true;
            }
        }

        private void FlushLatest()
        {
            if (Interlocked.CompareExchange(ref _flushRunning, 1, 0) != 0) return;
            UiConfig source;
            long processedVersion;
            lock (_gate)
            {
                if (_retired)
                {
                    Interlocked.Exchange(ref _flushRunning, 0);
                    return;
                }
                source = _latest;
                processedVersion = _version;
            }

            try
            {
                UiConfig snapshot;
                lock (source) snapshot = CloneUiConfig(source);
                SaveUI(_path, snapshot);
            }
            catch (Exception ex)
            {
                // 非关键UI状态保存失败不冒泡到UI线程；原配置文件由 SaveUI 保留。
                System.Diagnostics.Trace.TraceWarning(ex.ToString());
            }
            finally
            {
                var retire = false;
                lock (_gate)
                {
                    if (_version == processedVersion)
                    {
                        _retired = true;
                        retire = true;
                    }
                }
                Interlocked.Exchange(ref _flushRunning, 0);
                if (retire)
                {
                    ((ICollection<KeyValuePair<string, UiSaveDebouncer>>)UiSaveDebouncers)
                        .Remove(new KeyValuePair<string, UiSaveDebouncer>(_path, this));
                    _timer.Dispose();
                }
                else
                {
                    lock (_gate)
                        if (!_retired) _timer.Change(300, Timeout.Infinite);
                }
            }
        }
    }


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
        var global = new GlobalConfig { AO = ao, DO = dO, Test = test, UI = ui };
        // 仅主程序自动恢复；测试程序和配置工具不会读取当前 Windows 用户的项目状态。
        if (string.Equals(
                Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName),
                "MTTFTest",
                StringComparison.OrdinalIgnoreCase))
        {
            LastProjectRestoreResult = LastProjectSelectionStore.Restore(
                global,
                ConfigurationManager.AppSettings["InitialProjectPath"],
                log);
        }
        return global;
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

        var latestRetention = new LatestSnapshotRetentionConfig();
        var latestRetentionNode = doc.SelectSingleNode(
            "//TestConfig/DataStorageRetention/Latest") as XmlElement;
        if (latestRetentionNode != null)
        {
            if (latestRetentionNode.HasAttribute("RetentionMode"))
            {
                var modeText = latestRetentionNode.GetAttribute("RetentionMode");
                if (string.Equals(modeText, "Unlimited", StringComparison.OrdinalIgnoreCase))
                    latestRetention.RetentionMode = StorageRetentionMode.Unlimited;
                else if (string.Equals(modeText, "Count", StringComparison.OrdinalIgnoreCase))
                    latestRetention.RetentionMode = StorageRetentionMode.Count;
                else
                {
                    latestRetention.RetentionMode = StorageRetentionMode.Count;
                    log?.Warn(
                        $"Latest RetentionMode 非法，已回退 Count。Value={modeText} Path={path}",
                        "配置");
                }
            }

            if (latestRetentionNode.HasAttribute("RetainStopPackagesPerChannel"))
            {
                var countText = latestRetentionNode.GetAttribute("RetainStopPackagesPerChannel");
                if (!int.TryParse(
                        countText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var count) ||
                    count < 1 ||
                    count > LatestSnapshotRetentionConfig.MaximumRetainCount)
                {
                    count = LatestSnapshotRetentionConfig.DefaultRetainCount;
                    log?.Warn(
                        "Latest RetainStopPackagesPerChannel 非法，已回退 10。" +
                        $"Value={countText} Path={path}",
                        "配置");
                }
                latestRetention.RetainStopPackagesPerChannel = count;
            }
        }
        cfg.DataStorageRetention = new DataStorageRetentionConfig { Latest = latestRetention };
        log?.Info(
            "Latest 保留策略已生效：" +
            $"Mode={latestRetention.RetentionMode} " +
            $"Count={latestRetention.RetainStopPackagesPerChannel} Path={path}",
            "配置");


        var policyText = GetString(doc, "//TestConfig/Timer/OverrunPolicy", "RunToCompletionSkipMissed");
        if (!Enum.TryParse(policyText, out OverrunPolicy pol)) pol = OverrunPolicy.RunToCompletionSkipMissed;
        cfg.OverrunPolicy = pol;
        cfg.FormalSlotClosureTimeoutMs = Math.Max(
            0,
            GetInt(doc, "//TestConfig/Timer/FormalSlotClosureTimeoutMs", 0));

        foreach (XmlNode n in doc.SelectNodes("//TestConfig/Hydraulics/Hydraulic")!)
        {
            var h = new HydraulicItem
            {
                Id = GetInt(n, "Id", 1),
                Enabled = GetBool(n, "Enabled", true),
                Mode = ParseMode(GetString(n, "Mode", "ByPressure")),
                SetPercent = GetDouble(n, "SetPercent", 30),
                PressureThresholdBar = (int)GetDouble(n, "PressureThresholdBar", 20),
                PressureToleranceBar = Math.Max(0, GetDouble(n, "PressureToleranceBar", 10)),
                DurationMs = GetInt(n, "DurationMs", 0),
                HoldAfterReachedMs = GetInt(n, "HoldAfterReachedMs", 0),
                ReleaseSafePressureBar = GetDouble(n, "ReleaseSafePressureBar", 5),
                ReleaseStableMs = GetInt(n, "ReleaseStableMs", 100),
                ReleaseTimeoutMs = GetInt(n, "ReleaseTimeoutMs", 5000),
                BuildTimeoutMs = GetInt(n, "BuildTimeoutMs", 5000),
                BuildStableMs = GetInt(n, "BuildStableMs", 200),
                PressureSampleMaxAgeMs = GetInt(n, "PressureSampleMaxAgeMs", 100),
                HoldDropToleranceBar = GetDouble(n, "HoldDropToleranceBar", 10),
                HoldDropConfirmMs = GetInt(n, "HoldDropConfirmMs", 1000),
                BarrierTimeoutMs = GetInt(n, "BarrierTimeoutMs", 0),
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


        // 先保留原始行用于诊断，再一次性写入按 Id 唯一的运行时集合。
        var loadedEpbRecords = new List<EpbTestRecord>();
        foreach (XmlNode n in doc.SelectNodes("//TestConfig/EpbRecords/Record")!)
        {
            var r = new EpbTestRecord
            {
                Id = GetInt(n, "Id", -1),
                Enabled = GetBool(n, "Enabled", false),
                StartTime = TryParseDateTime(GetString(n, "StartTime", null)),
                LatestStartTime = TryParseDateTime(GetString(n, "LatestStartTime", null)),
                RunTime = GetString(n, "RunTime", FormatTimeSpan(TimeSpan.Zero)),
                TotalCount = GetInt(n, "TotalCount", 0),
                RunCount = GetInt(n, "RunCount", 0),
                MechanicalCycleCount = GetLong(n, "MechanicalCycleCount", 0),
                PermanentAlarmLatched = GetBool(n, "PermanentAlarmLatched", false),
                PermanentAlarmCode = GetString(n, "PermanentAlarmCode", string.Empty),
                PermanentAlarmReason = GetString(n, "PermanentAlarmReason", string.Empty),
                PermanentAlarmUtc = TryParseDateTime(GetString(n, "PermanentAlarmUtc", null)),
                PermanentAlarmCorrelationId = Guid.TryParse(
                    GetString(n, "PermanentAlarmCorrelationId", string.Empty),
                    out var alarmCorrelationId)
                    ? alarmCorrelationId
                    : Guid.Empty,
                OperatorFullRelearningRequired = GetBool(
                    n,
                    "OperatorFullRelearningRequired",
                    false),
                OperatorFullRelearningReason = GetString(
                    n,
                    "OperatorFullRelearningReason",
                    string.Empty),
                OperatorFullRelearningUtc = TryParseDateTime(
                    GetString(n, "OperatorFullRelearningUtc", null)),
                OperatorFullRelearningCorrelationId = Guid.TryParse(
                    GetString(n, "OperatorFullRelearningCorrelationId", string.Empty),
                    out var relearningCorrelationId)
                    ? relearningCorrelationId
                    : Guid.Empty,
                ConsecutivePeriodOverrunCount = Math.Max(
                    0,
                    GetInt(n, "ConsecutivePeriodOverrunCount", 0)),
                LastPeriodOverrunUtc = TryParseDateTime(
                    GetString(n, "LastPeriodOverrunUtc", null))
            };

            // 状态解析（容错）
            var st = GetString(n, "Status", "NotStarted");
            r.Status = Enum.TryParse<EpbTestStatus>(st, out var status) ? status : EpbTestStatus.NotStarted;

            // V2.13.0.29 及更早版本只留下 Enabled=false + Alarm。仅这一种组合
            // 可以保守迁移；普通未启用记录不得反推为永久报警。
            if (n.SelectSingleNode("PermanentAlarmLatched") == null &&
                !r.Enabled &&
                r.Status == EpbTestStatus.Alarm)
            {
                r.PermanentAlarmLatched = true;
                r.PermanentAlarmCode = "LegacyDisabledAlarm";
                r.PermanentAlarmReason = "旧版本项目中的禁用报警状态已迁移为永久报警。";
                r.PermanentAlarmUtc = File.GetLastWriteTimeUtc(path);
                r.PermanentAlarmCorrelationId = Guid.Empty;
            }

            loadedEpbRecords.Add(r);
        }

        var duplicateEpbIds = loadedEpbRecords
            .Where(record => record != null)
            .GroupBy(record => record.Id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToArray();
        var invalidEpbRecordCount = loadedEpbRecords.Count(record =>
            record == null || record.Id < 1 || record.Id > 12);
        cfg.EpbRecords.ReplaceAll(loadedEpbRecords);
        cfg.EnsureEpbRecords(12);
        if (duplicateEpbIds.Length > 0 || invalidEpbRecordCount > 0)
        {
            log?.Warn(
                "TestConfig EpbRecords 已自动归一化：" +
                $"DuplicateIds=[{string.Join(",", duplicateEpbIds)}] " +
                $"InvalidCount={invalidEpbRecordCount} CanonicalCount={cfg.EpbRecords.Count} Path={path}",
                "配置");
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
                PreReleaseDetectTimeoutMs = TryGetNullableInt(r, "PreReleaseDetectTimeoutMs"),
                PeakIgnoreMs = GetInt(r, "PeakIgnoreMs", 0),
                ForwardProgressConfirmMs = GetInt(r, "ForwardProgressConfirmMs", 1000),
                ForwardMinimumRiseSlopeAperMs = GetDouble(r, "ForwardMinimumRiseSlopeAperMs", 0.001),
                ForwardProgressDeadlineMs = GetInt(r, "ForwardProgressDeadlineMs", 5000),
                ReverseProgressConfirmMs = GetInt(r, "ReverseProgressConfirmMs", 200),
                ReverseMinimumDecaySlopeAperMs = GetDouble(r, "ReverseMinimumDecaySlopeAperMs", 0.001),
                ReverseProgressDeadlineMs = GetInt(r, "ReverseProgressDeadlineMs", 2500),
                OffCurrentClearThresholdA = GetDouble(r, "OffCurrentClearThresholdA", 0.1),
                OffCurrentClearTimeoutMs = GetInt(r, "OffCurrentClearTimeoutMs", 1000)
            };

            // 软边界钳制（防御性）
            item.FwdOnLimitMs = Math.Max(0, item.FwdOnLimitMs);
            item.HoldMs = Math.Max(0, item.HoldMs);
            item.RevDecayRigidMaxMs = Math.Max(0, item.RevDecayRigidMaxMs);
            item.RevEmptyFixedMs = Math.Max(0, item.RevEmptyFixedMs);
            item.ForwardProgressConfirmMs = Math.Max(20, item.ForwardProgressConfirmMs);
            item.ForwardMinimumRiseSlopeAperMs = Math.Max(0.00001, item.ForwardMinimumRiseSlopeAperMs);
            item.ForwardProgressDeadlineMs = Math.Max(
                item.ForwardProgressConfirmMs,
                item.ForwardProgressDeadlineMs);
            item.ReverseProgressConfirmMs = Math.Max(20, item.ReverseProgressConfirmMs);
            item.ReverseMinimumDecaySlopeAperMs = Math.Max(0.00001, item.ReverseMinimumDecaySlopeAperMs);
            item.ReverseProgressDeadlineMs = Math.Max(
                item.ReverseProgressConfirmMs,
                item.ReverseProgressDeadlineMs);
            item.OffCurrentClearThresholdA = Math.Max(0.01, item.OffCurrentClearThresholdA);
            // 兼容读取旧项目字段；运行控制不使用这些值，也不会在项目保存时回写。
            // 这里保留防御性归一化，仅避免旧字段被其他只读诊断代码误用。
            item.OffCurrentClearTimeoutMs = Math.Max(1000, item.OffCurrentClearTimeoutMs);

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

        var cfgDir = RuntimeConfigPaths.Directory;
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
        path = Path.GetFullPath(ResolveUiPath(path));
        var fileLock = UiFileLocks.GetOrAdd(path, _ => new object());

        lock (fileLock)
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

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                doc.Save(tmp);
                if (!File.Exists(path))
                {
                    File.Move(tmp, path);
                    return;
                }

                IOException lastError = null;
                for (var i = 0; i < 5; i++)
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex;
                        if (i < 4) Thread.Sleep(50 * (1 << i));
                    }
                }

                // 原文件始终保留；替换持续失败时明确上抛，禁止“先删后移”造成配置窗口期丢失。
                throw new IOException(
                    $"UI 配置原子替换失败，原配置文件已保留。Path={path}",
                    lastError);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch
                {
                    // 临时文件清理失败不覆盖真正的保存异常。
                }
            }
        }
    }

    // 不带 path 的重载
    public static void SaveUI(UiConfig cfg)
    {
        SaveUI(null, cfg);
    }
    
    public static void SaveTest(string path, TestConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        path = Path.GetFullPath(path);
        var fileLock = TestFileLocks.GetOrAdd(path, _ => new object());
        lock (fileLock)
            SaveTestCore(path, cfg);
    }

    private static void SaveTestCore(string path, TestConfig cfg)
    {
        // 保存边界再次归一化，禁止任何调用方把重复通道写回项目配置。
        cfg.EnsureEpbRecords(12);

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
            SetChild(
                timer,
                "FormalSlotClosureTimeoutMs",
                Math.Max(0, cfg.FormalSlotClosureTimeoutMs).ToString());
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
            AddHydraulicElement(
                "PressureToleranceBar",
                Math.Max(0, hydraulic.PressureToleranceBar).ToString(CultureInfo.InvariantCulture));
            AddHydraulicElement("DurationMs", hydraulic.DurationMs.ToString());
            AddHydraulicElement("HoldAfterReachedMs", hydraulic.HoldAfterReachedMs.ToString());
            AddHydraulicElement(
                "ReleaseSafePressureBar",
                hydraulic.ReleaseSafePressureBar.ToString(CultureInfo.InvariantCulture));
            AddHydraulicElement("ReleaseStableMs", hydraulic.ReleaseStableMs.ToString());
            AddHydraulicElement("ReleaseTimeoutMs", hydraulic.ReleaseTimeoutMs.ToString());
            AddHydraulicElement("BuildTimeoutMs", hydraulic.BuildTimeoutMs.ToString());
            AddHydraulicElement("BuildStableMs", hydraulic.BuildStableMs.ToString());
            AddHydraulicElement("PressureSampleMaxAgeMs", hydraulic.PressureSampleMaxAgeMs.ToString());
            AddHydraulicElement(
                "HoldDropToleranceBar",
                hydraulic.HoldDropToleranceBar.ToString(CultureInfo.InvariantCulture));
            AddHydraulicElement("HoldDropConfirmMs", hydraulic.HoldDropConfirmMs.ToString());
            AddHydraulicElement("BarrierTimeoutMs", hydraulic.BarrierTimeoutMs.ToString());
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
        // 历史异常文件可能同时存在多个 <EpbRecords> 容器；只删第一个会让旧容器
        // 与新容器同时保留，并在下一次加载时重新形成重复 Id。
        var existingEpbRecordNodes = root.SelectNodes("EpbRecords");
        if (existingEpbRecordNodes != null)
            foreach (XmlNode existingEpbRecordNode in existingEpbRecordNodes)
                root.RemoveChild(existingEpbRecordNode);

        var epbRecordsNode = doc.CreateElement("EpbRecords");

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
            AddRecordElement("Enabled", record.Enabled.ToString());
            AddRecordElement("StartTime", record.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
            AddRecordElement("LatestStartTime", record.LatestStartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
            AddRecordElement("RunTime", record.RunTime ?? "");
            AddRecordElement("TotalCount", record.TotalCount.ToString());
            AddRecordElement("RunCount", record.RunCount.ToString());
            AddRecordElement("MechanicalCycleCount", record.MechanicalCycleCount.ToString(CultureInfo.InvariantCulture));
            AddRecordElement("Status", record.Status.ToString());
            AddRecordElement("PermanentAlarmLatched", record.PermanentAlarmLatched ? "True" : "False");
            AddRecordElement("PermanentAlarmCode", record.PermanentAlarmCode ?? string.Empty);
            AddRecordElement("PermanentAlarmReason", record.PermanentAlarmReason ?? string.Empty);
            AddRecordElement("PermanentAlarmUtc", record.PermanentAlarmUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            AddRecordElement("PermanentAlarmCorrelationId", record.PermanentAlarmCorrelationId == Guid.Empty ? string.Empty : record.PermanentAlarmCorrelationId.ToString("N"));
            AddRecordElement("OperatorFullRelearningRequired", record.OperatorFullRelearningRequired ? "True" : "False");
            AddRecordElement("OperatorFullRelearningReason", record.OperatorFullRelearningReason ?? string.Empty);
            AddRecordElement("OperatorFullRelearningUtc", record.OperatorFullRelearningUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            AddRecordElement("OperatorFullRelearningCorrelationId", record.OperatorFullRelearningCorrelationId == Guid.Empty ? string.Empty : record.OperatorFullRelearningCorrelationId.ToString("N"));
            AddRecordElement("ConsecutivePeriodOverrunCount", Math.Max(0, record.ConsecutivePeriodOverrunCount).ToString(CultureInfo.InvariantCulture));
            AddRecordElement("LastPeriodOverrunUtc", record.LastPeriodOverrunUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

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
            Add("PreReleaseDetectTimeoutMs", it.PreReleaseDetectTimeoutMs?.ToString() ?? "");
            Add("PeakIgnoreMs", it.PeakIgnoreMs.ToString());
            // 正/反向失速和断电清零属于程序级安全策略，由 EXE 同名配置统一管理。
            // 旧项目字段仍可兼容读取，但保存项目时不再写回，避免切换项目覆盖程序安全默认值。

            epbNode.AppendChild(rec);
        }

        root.AppendChild(epbNode);

        // ===============================
        // 7) 保存项目数据副本保留策略
        // ===============================
        var retentionNode = root.SelectSingleNode("DataStorageRetention");
        if (retentionNode != null) root.RemoveChild(retentionNode);
        var retentionElement = doc.CreateElement("DataStorageRetention");
        var latestElement = doc.CreateElement("Latest");
        var latestRetention = cfg.DataStorageRetention?.Latest ?? new LatestSnapshotRetentionConfig();
        var retainCount = latestRetention.RetainStopPackagesPerChannel;
        if (retainCount < 1 || retainCount > LatestSnapshotRetentionConfig.MaximumRetainCount)
            retainCount = LatestSnapshotRetentionConfig.DefaultRetainCount;
        var retentionMode = latestRetention.RetentionMode == StorageRetentionMode.Unlimited
            ? StorageRetentionMode.Unlimited
            : StorageRetentionMode.Count;
        latestElement.SetAttribute("RetentionMode", retentionMode.ToString());
        latestElement.SetAttribute(
            "RetainStopPackagesPerChannel",
            retainCount.ToString(CultureInfo.InvariantCulture));
        retentionElement.AppendChild(latestElement);
        root.AppendChild(retentionElement);

        // ===============================
        // 8) Save to file with temp
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
    /// 原子更新项目 TestConfig 中单个通道的 Enabled，保留磁盘上其余进度和配置字段。
    /// 用于报警线程持久禁用，避免用可能尚未完成 UI 同步的内存快照覆盖刚提交圈数。
    /// </summary>
    public static void UpdateTestEpbEnabled(string path, int channel, bool enabled)
    {
        UpdateTestEpbEnabled(path, new[] { channel }, enabled);
    }

    /// <summary>
    /// 原子更新项目 TestConfig 中多个通道的 Enabled。共享液压/电源硬件故障
    /// 必须一次提交整个故障cohort，禁止逐通道替换文件留下部分已禁用状态。
    /// </summary>
    public static void UpdateTestEpbEnabled(
        string path,
        IEnumerable<int> channels,
        bool enabled)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        var selected = (channels ?? throw new ArgumentNullException(nameof(channels)))
            .Distinct()
            .OrderBy(channel => channel)
            .ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("至少指定一个EPB通道", nameof(channels));
        if (selected.Any(channel => channel < 1 || channel > 12))
            throw new ArgumentOutOfRangeException(nameof(channels));
        path = Path.GetFullPath(path);
        var fileLock = TestFileLocks.GetOrAdd(path, _ => new object());
        lock (fileLock)
        {
            var doc = new XmlDocument();
            doc.Load(path);
            var records = doc.SelectNodes("/TestConfig/EpbRecords/Record");
            var targets = new Dictionary<int, XmlElement>();
            if (records != null)
            {
                foreach (XmlNode node in records)
                {
                    if (node is not XmlElement element) continue;
                    if (int.TryParse(
                            element.SelectSingleNode("Id")?.InnerText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var id) && selected.Contains(id))
                    {
                        targets[id] = element;
                    }
                }
            }
            var missing = selected.Where(channel => !targets.ContainsKey(channel)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"TestConfig.xml 缺少 EPB[{string.Join(",", missing)}] 记录");

            foreach (var channel in selected)
            {
                var target = targets[channel];
                var enabledNode = target.SelectSingleNode("Enabled") as XmlElement;
                if (enabledNode == null)
                {
                    enabledNode = doc.CreateElement("Enabled");
                    target.AppendChild(enabledNode);
                }
                enabledNode.InnerText = enabled ? "True" : "False";
            }

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                doc.Save(tmp);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch { }
            }
        }
    }

    /// <summary>原子更新项目中的永久报警、连续超限和可选启用状态。</summary>
    public static void UpdateTestEpbAlarmState(
        string path,
        IEnumerable<EpbAlarmPersistenceUpdate> requestedUpdates)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
        var updates = (requestedUpdates ?? throw new ArgumentNullException(nameof(requestedUpdates)))
            .Where(update => update != null)
            .GroupBy(update => update.Channel)
            .Select(group => group.Last())
            .OrderBy(update => update.Channel)
            .ToArray();
        if (updates.Length == 0)
            throw new ArgumentException("至少指定一个EPB报警更新", nameof(requestedUpdates));
        if (updates.Any(update => update.Channel < 1 || update.Channel > 12))
            throw new ArgumentOutOfRangeException(nameof(requestedUpdates));

        path = Path.GetFullPath(path);
        var fileLock = TestFileLocks.GetOrAdd(path, _ => new object());
        lock (fileLock)
        {
            var doc = new XmlDocument();
            doc.Load(path);
            var records = doc.SelectNodes("/TestConfig/EpbRecords/Record");
            var targets = new Dictionary<int, List<XmlElement>>();
            if (records != null)
            {
                foreach (XmlNode node in records)
                {
                    if (node is not XmlElement element) continue;
                    if (int.TryParse(
                            element.SelectSingleNode("Id")?.InnerText,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var id))
                    {
                        if (!targets.TryGetValue(id, out var matching))
                        {
                            matching = new List<XmlElement>();
                            targets[id] = matching;
                        }
                        matching.Add(element);
                    }
                }
            }

            var missing = updates.Where(update => !targets.ContainsKey(update.Channel))
                .Select(update => update.Channel)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"TestConfig.xml 缺少 EPB[{string.Join(",", missing)}] 记录");

            foreach (var update in updates)
            {
                foreach (var target in targets[update.Channel])
                {
                    void Set(string name, string value)
                    {
                        var element = target.SelectSingleNode(name) as XmlElement;
                        if (element == null)
                        {
                            element = doc.CreateElement(name);
                            target.AppendChild(element);
                        }
                        element.InnerText = value ?? string.Empty;
                    }

                    if (update.Enabled.HasValue)
                        Set("Enabled", update.Enabled.Value ? "True" : "False");
                    Set("PermanentAlarmLatched", update.PermanentAlarmLatched ? "True" : "False");
                    Set("PermanentAlarmCode", update.PermanentAlarmCode ?? string.Empty);
                    Set("PermanentAlarmReason", update.PermanentAlarmReason ?? string.Empty);
                    Set("PermanentAlarmUtc", update.PermanentAlarmUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                    Set("PermanentAlarmCorrelationId", update.PermanentAlarmCorrelationId == Guid.Empty ? string.Empty : update.PermanentAlarmCorrelationId.ToString("N"));
                    if (update.OperatorFullRelearningRequired.HasValue)
                    {
                        Set(
                            "OperatorFullRelearningRequired",
                            update.OperatorFullRelearningRequired.Value ? "True" : "False");
                        Set(
                            "OperatorFullRelearningReason",
                            update.OperatorFullRelearningRequired.Value
                                ? update.OperatorFullRelearningReason ?? string.Empty
                                : string.Empty);
                        Set(
                            "OperatorFullRelearningUtc",
                            update.OperatorFullRelearningRequired.Value
                                ? update.OperatorFullRelearningUtc?.ToUniversalTime().ToString(
                                      "O",
                                      CultureInfo.InvariantCulture) ?? string.Empty
                                : string.Empty);
                        Set(
                            "OperatorFullRelearningCorrelationId",
                            update.OperatorFullRelearningRequired.Value &&
                            update.OperatorFullRelearningCorrelationId != Guid.Empty
                                ? update.OperatorFullRelearningCorrelationId.ToString("N")
                                : string.Empty);
                    }
                    Set("ConsecutivePeriodOverrunCount", Math.Max(0, update.ConsecutivePeriodOverrunCount).ToString(CultureInfo.InvariantCulture));
                    Set("LastPeriodOverrunUtc", update.LastPeriodOverrunUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                    if (update.PermanentAlarmLatched)
                        Set("Status", EpbTestStatus.Alarm.ToString());
                    else if (string.Equals(
                                 target.SelectSingleNode("Status")?.InnerText,
                                 EpbTestStatus.Alarm.ToString(),
                                 StringComparison.OrdinalIgnoreCase))
                        Set("Status", EpbTestStatus.NotStarted.ToString());
                }
            }

            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                doc.Save(tmp);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
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
        var configDir = RuntimeConfigPaths.Directory;
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
            var dir = RuntimeConfigPaths.Directory;
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
    /// Loads an existing project config and updates the active project root used by
    /// project-scoped logging and persistence.
    /// </summary>
    public static TestConfig LoadProjectTestConfig(
        string storeDir,
        string testName,
        IAppLogger log = null)
    {
        var path = GetProjectTestConfigPath(storeDir, testName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("项目 TestConfig.xml 不存在。", path);

        var candidate = LoadTest(path, log);
        candidate.StoreDir = storeDir;
        candidate.TestName = testName;
        CurrentProjectRootDir = GetProjectRootDir(storeDir, testName);
        return candidate;
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

        CurrentProjectRootDir = GetProjectRootDir(storeDir, testName);
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
            EpbProjectPolicies.InitializeNewProject(cfg, storeDir, testName);
            SaveTest(projectTestPath, cfg);
            return cfg;
        }

        // 拷贝一份默认 TestConfig 文件，再装载并清理 EpbRecords 进度
        File.Copy(defaultTestPath, projectTestPath, overwrite: false);
        var projectCfg = LoadTest(projectTestPath, log);

        EpbProjectPolicies.InitializeNewProject(projectCfg, storeDir, testName);

        SaveTest(projectTestPath, projectCfg);

        log?.Info($"已从默认 TestConfig.xml 创建项目专用配置：{projectTestPath}", "配置");
        return projectCfg;
    }

    /// <summary>
    /// Creates a new project config from an existing in-memory config without changing
    /// the source object or touching any previous project files.
    /// </summary>
    public static TestConfig CreateNewProjectTestConfig(
        TestConfig source,
        string templatePath,
        string storeDir,
        string testName,
        IAppLogger log = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            throw new FileNotFoundException("创建项目所需的 TestConfig.xml 模板不存在。", templatePath);
        if (string.IsNullOrWhiteSpace(storeDir))
            throw new ArgumentException("项目存储路径不能为空。", nameof(storeDir));
        if (string.IsNullOrWhiteSpace(testName))
            throw new ArgumentException("项目名称不能为空。", nameof(testName));

        storeDir = Path.GetFullPath(storeDir.Trim());
        testName = testName.Trim();
        if (testName == "." || testName == ".." ||
            testName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("项目名称不是合法的 Windows 文件夹名称。", nameof(testName));

        var projectRoot = Path.GetFullPath(GetProjectRootDir(storeDir, testName));
        var targetPath = GetProjectTestConfigPath(storeDir, testName);
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidOperationException("无法生成新项目配置路径。");
        if (!string.Equals(
                Directory.GetParent(projectRoot)?.FullName,
                storeDir,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("项目目录必须是存储根目录的直接子目录。");

        if (Directory.Exists(projectRoot))
        {
            if (File.Exists(targetPath))
                throw new IOException($"项目“{testName}”已存在，不能按新项目覆盖：{targetPath}");
            throw new IOException(
                $"目标项目目录已存在，但缺少有效的 TestConfig.xml。请先处理残留目录：{projectRoot}");
        }

        Directory.CreateDirectory(storeDir);
        var stagingPath = Path.Combine(
            storeDir,
            $".epb-new-project-{Guid.NewGuid():N}.xml");
        var createdProjectRoot = false;

        try
        {
            File.Copy(templatePath, stagingPath, false);
            SaveTest(stagingPath, source);

            var candidate = LoadTest(stagingPath, log);
            EpbProjectPolicies.InitializeNewProject(candidate, storeDir, testName);
            SaveTest(stagingPath, candidate);

            // Re-read the staged file before publishing it so a malformed candidate
            // never becomes the active project.
            candidate = LoadTest(stagingPath, log);

            if (Directory.Exists(projectRoot))
                throw new IOException(
                    $"发布新项目时检测到目标目录已存在，已停止以避免覆盖：{projectRoot}");
            Directory.CreateDirectory(Path.Combine(projectRoot, "Config"));
            createdProjectRoot = true;
            File.Move(stagingPath, targetPath);
            CurrentProjectRootDir = projectRoot;
            log?.Info($"已创建新项目配置：{targetPath}", "配置");
            return candidate;
        }
        catch
        {
            TryDeleteFile(stagingPath);
            if (createdProjectRoot)
            {
                TryDeleteEmptyDirectory(Path.Combine(projectRoot, "Config"));
                TryDeleteEmptyDirectory(projectRoot);
            }
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup of a file created by this method.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path, false);
        }
        catch
        {
            // Best effort cleanup of an empty directory created by this method.
        }
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
        CurrentProjectRootDir = GetProjectRootDir(projectTest.StoreDir, projectTest.TestName);

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

        lock (cfg)
        {
            var form = cfg.GetOrAddForm(formName);
            var c = form.GetOrAdd(ctrlName);
            c.Checked = isChecked;
            if (enabled.HasValue) c.Enabled = enabled.Value;
        }
        ScheduleUiSave(path, cfg);
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

        lock (cfg)
        {
            var form = cfg.GetOrAddForm(formName);
            var c = form.GetOrAdd(ctrlName);
            c.DefaultChecked = defaultChecked;
        }
        ScheduleUiSave(path, cfg);
    }

    // 不带 path 的重载
    public static void UpdateUIDefaultChecked(UiConfig cfg, string formName, string ctrlName, bool defaultChecked)
    {
        UpdateUIDefaultChecked(null, cfg, formName, ctrlName, defaultChecked);
    }

    private static void ScheduleUiSave(string path, UiConfig cfg)
    {
        var fullPath = Path.GetFullPath(ResolveUiPath(path));
        while (true)
        {
            var debouncer = UiSaveDebouncers.GetOrAdd(
                fullPath,
                key => new UiSaveDebouncer(key));
            if (debouncer.Schedule(cfg)) return;
            ((ICollection<KeyValuePair<string, UiSaveDebouncer>>)UiSaveDebouncers)
                .Remove(new KeyValuePair<string, UiSaveDebouncer>(fullPath, debouncer));
        }
    }

    private static UiConfig CloneUiConfig(UiConfig source)
    {
        var clone = new UiConfig();
        foreach (var formPair in source.Forms)
        {
            var form = clone.GetOrAddForm(formPair.Key);
            foreach (var controlPair in formPair.Value.Controls)
            {
                var sourceControl = controlPair.Value;
                var control = form.GetOrAdd(controlPair.Key);
                control.Checked = sourceControl.Checked;
                control.Enabled = sourceControl.Enabled;
                control.DefaultChecked = sourceControl.DefaultChecked;
            }
        }
        return clone;
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

    private static long GetLong(XmlNode n, string xpath, long dft)
    {
        var s = n.SelectSingleNode(xpath)?.InnerText?.Trim();
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : dft;
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
