// ReSharper disable InconsistentNaming
// ReSharper disable RedundantNameQualifier

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace DataOperation;

#region 策略/结构/枚举

/// <summary>
///     落盘策略配置（可从 TestConfig.xml 读取，也可直接 new 指定）
/// </summary>
public sealed class DataRetentionPolicy
{
    /// <summary>是否使用内存映射写入（默认 true）。</summary>
    public bool UseMemoryMapped { get; set; } = true;

    /// <summary>每个 EPB 通道保留的“最新圈数”（仅统计 CycleNumber &gt; 0 的正式圈，默认 10）。</summary>
    public int RetainLatestCycles { get; set; } = 10;

    /// <summary>是否保留全部（true 则忽略 RetainLatestCycles，谨慎使用）。</summary>
    public bool RetainAllData { get; set; } = false;

    /// <summary>清理模式：delete（删索引）或 archive（导出 CSV 后删索引），默认 delete。</summary>
    public string CleanupMode { get; set; } = "delete";

    /// <summary>每个通道 .dat 文件大小（MB），默认 384 MB。</summary>
    public int FileSizeMb { get; set; } = 384;

    /// <summary>数据根目录（默认 "DataStore"）。</summary>
    public string DataStorePath { get; set; } = "DataStore";

    /// <summary>
    ///     索引与导出文件根目录（默认 null：与 <see cref="DataStorePath"/> 相同）。
    ///     <list type="bullet">
    ///         <item>1. <c>index.db</c> 会放在此目录下。</item>
    ///         <item>2. Archive/Latest 导出的 CSV/BIN 子目录也在此目录下。</item>
    ///         <item>3. 若为空或空白，则回退到 <see cref="DataStorePath"/>。</item>
    ///     </list>
    /// </summary>
    public string IndexAndExportPath { get; set; }

    /// <summary>SQLite 索引文件名（默认 "index.db"）。</summary>
    public string IndexDbFile { get; set; } = "index.db";
}

/// <summary>
///     单条采样记录（32B，已去除全部“预留字段”）
///     字节布局（小端）：
///     0  : Int64 TimestampBinary（按本地时间写入的 DateTime.ToBinary）
///     8  : Int32 CycleNumber
///     12 : Int32 SampleIndex
///     16 : Double EpbCurrent
///     24 : Double GroupPressure
/// </summary>
public struct SampleRecord
{
    public long TimestampBinary; // 8
    public int CycleNumber; // 4
    public int SampleIndex; // 4
    public double EpbCurrent; // 8
    public double GroupPressure; // 8

    /// <summary>固定记录长度（字节）。</summary>
    public const int Size = 32;
}

/// <summary>
///     用于“停止即落盘”的触发时机。
/// </summary>
public enum StopTrigger
{
    /// <summary>立即停止并落盘（当前圈按已采样本计数为准）。</summary>
    Immediate = 0,

    /// <summary>当前圈结束后停止并落盘（推荐，得到完整整圈）。</summary>
    EndOfCurrentCycle = 1,

    /// <summary>再过 K 圈后停止并落盘（需配合 SetStopAfterK）。</summary>
    AfterKMoreCycles = 2
}

#endregion

/// <summary>
///     EpbDiskWriter：12 路 EPB 的内存映射数据写入 + SQLite 圈级索引 + 最新 N 圈保留/落盘。<br />
///     —— 已改为“分块视图（窗口化映射）”，避免整文件映射导致“内存资源不足”。 ——
/// </summary>
public sealed class EpbDiskWriter : IDisposable
{
    #region 与 EpbManager 的“停止触发”配合（可选）

    /// <summary>在上层 StopChannel 的实际停止点调用（若 StopTrigger=Immediate 则已处理）。</summary>
    public void OnChannelStopped(int epbId)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            // Immediate 已经执行过；如果是 EndOfCurrentCycle 但未走到 Complete 场景，这里兜底
            if (s.StopTrigger == StopTrigger.EndOfCurrentCycle)
                PersistLatestCyclesNow(epbId, s.StopKeepLatestN, s.StopAction);
        }
    }

    #endregion

    #region 常量与内部结构

    private const int EPB_COUNT = 12;
    private const string TABLE_CYCLES = "epb_cycles";

    private sealed class EpbState
    {
        public readonly object Gate = new();
        public long CapacityRecords; // 文件可容纳记录数
        public int? CurrentCycle; // 正式圈号（null=未开圈）
        public int CurrentSampleIndex; // 当前圈内样本序号（0..）
        public bool FreeRunOn; // 是否开启 Free-Run
        public int FreeRunSampleIndex; // Free-Run 下的“伪圈”样本序号
        public string StopAction = "archive";

        public int StopAfterK; // 仅当 StopTrigger=AfterKMoreCycles
        public int StopKeepLatestN = 10; // 停止时保留的最新圈数
        public StopTrigger StopTrigger = StopTrigger.EndOfCurrentCycle;
        public long TotalWritten; // 已写入总条数（单调递增）
    }

    #endregion

    #region 字段

    private readonly string _rootDir;
    private readonly string _indexDir;  // 索引与导出用的根目录（index.db、Archive、Latest）
    private readonly long _fileBytes;
    private readonly DataRetentionPolicy _policy;

    private readonly MemoryMappedFile[] _mmfs = new MemoryMappedFile[EPB_COUNT + 1]; // 1..12
    private readonly MemoryMappedViewAccessor[] _views = new MemoryMappedViewAccessor[EPB_COUNT + 1];
    private readonly EpbState[] _states = new EpbState[EPB_COUNT + 1];

    // —— 窗口化映射新增：记录每个通道当前视图的“文件基址/长度”（单位：字节） —— //
    private readonly long[] _viewBaseOffsets = new long[EPB_COUNT + 1];
    private readonly long[] _viewLengths = new long[EPB_COUNT + 1];

    // 视图窗口大小与对齐（可按需调整）
    private const long VIEW_BYTES = 64L * 1024 * 1024; // 64MB
    private const long VIEW_ALIGN = 64L * 1024; // 64KB（Windows allocation granularity）

    private readonly SQLiteConnection _conn;

    private bool _disposed;

    #endregion

    #region 构造/释放

    /// <summary>
    ///     构造 EpbDiskWriter 并初始化 12 个通道的 .dat 内存映射与 SQLite 索引库。
    ///     —— 此版本采用“窗口化映射”，不再整文件映射到进程地址空间。
    /// </summary>
    /// <param name="policy">落盘策略配置。</param>
    public EpbDiskWriter(DataRetentionPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        // —— 1) .dat 环形文件所在根目录 —— //
        _rootDir = Path.GetFullPath(_policy.DataStorePath ?? "DataStore");
        Directory.CreateDirectory(_rootDir);

        // —— 2) 索引 + 导出（index.db、Archive、Latest）所在根目录 —— //
        if (string.IsNullOrWhiteSpace(_policy.IndexAndExportPath))
        {
            // 没配的话就与 DataStorePath 相同，保持兼容老行为
            _indexDir = _rootDir;
        }
        else
        {
            _indexDir = Path.GetFullPath(_policy.IndexAndExportPath);
            Directory.CreateDirectory(_indexDir);
        }

        _fileBytes = Math.Max(1, _policy.FileSizeMb) * 1024L * 1024L;

        // SQLite 连接：index.db 放在 _indexDir 下
        var dbPath = Path.Combine(_indexDir, _policy.IndexDbFile ?? "index.db");
        var needInit = !File.Exists(dbPath);
        _conn = new SQLiteConnection(
            $"Data Source={dbPath};Pooling=True;Journal Mode=WAL;Synchronous=Normal");
        _conn.Open();
        if (needInit) InitSchema();

        // 12 路映射 + 状态
        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            _states[ch] = new EpbState();

            var path = GetDatPath(ch);
            EnsureFixedSizeFile(path, _fileBytes);

            _mmfs[ch] = MemoryMappedFile.CreateFromFile(path, FileMode.Open, $"EPB{ch}_MMF", _fileBytes);

            // —— 仅映射“首块窗口”，避免整文件映射占用巨大虚拟地址空间 —— //
            long firstBase = 0;
            var firstLen = Math.Min(_fileBytes, AlignUp(VIEW_BYTES, VIEW_ALIGN));
            _views[ch] = _mmfs[ch].CreateViewAccessor(firstBase, firstLen, MemoryMappedFileAccess.ReadWrite);
            _viewBaseOffsets[ch] = firstBase;
            _viewLengths[ch] = firstLen;

            _states[ch].CapacityRecords = _fileBytes / SampleRecord.Size;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            _views[ch]?.Dispose();
            _mmfs[ch]?.Dispose();
            _views[ch] = null;
            _mmfs[ch] = null;
        }

        _conn?.Dispose();
    }

    #endregion

    #region 公共 API：模式/周期/写入/导出/保留

    /// <summary>
    ///     开启指定通道的 Free-Run（自由记录）模式。未开正式循环时，写入将使用 CycleNumber=0。
    /// </summary>
    public void StartFreeRun(int epbId)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            s.FreeRunOn = true;
        }
    }

    /// <summary>关闭指定通道的 Free-Run 模式。</summary>
    public void StopFreeRun(int epbId)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            s.FreeRunOn = false;
        }
    }

    /// <summary>查询指定通道是否处于 Free-Run。</summary>
    public bool IsFreeRunOn(int epbId)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            return s.FreeRunOn;
        }
    }

    /// <summary>
    ///     设置“停止卡钳”时的落盘选项（默认：保留最新 10 圈，archive，结束当前圈后停止）。
    /// </summary>
    public void SetStopRetentionOption(int epbId, int keepLatestN = 10, string action = "archive",
        StopTrigger trigger = StopTrigger.EndOfCurrentCycle, int afterK = 0)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            s.StopKeepLatestN = Math.Max(1, keepLatestN);
            s.StopAction = string.IsNullOrWhiteSpace(action) ? "archive" : action.ToLowerInvariant();
            s.StopTrigger = trigger;
            s.StopAfterK = afterK;
        }
    }

    /// <summary>
    ///     标记一个通道的“正式试验圈”开始；后续写入将打上该圈号（优先于 Free-Run）。
    ///     <list type="number">
    ///         <item>1. <paramref name="cycleNumber"/> 应在当前 index.db 中对该通道保持全局唯一。</item>
    ///         <item>2. 若重用已存在的圈号，将触发 SQLite 唯一约束异常。</item>
    ///         <item>3. 建议上层在新试验开始前调用 <see>
    ///                 <cref>GetLastCycleNumber</cref>
    ///             </see>
    ///             ，
    ///                以 <c>last + 1</c> 作为本次试验的起始圈号。</item>
    ///     </list>
    /// </summary>
    public void BeginCycle(int epbId, int cycleNumber, DateTime startUtc)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            if (s.CapacityRecords <= 0)
                throw new InvalidOperationException("CapacityRecords must be positive.");

            s.CurrentCycle = cycleNumber;
            s.CurrentSampleIndex = 0;

            var startIndex = s.TotalWritten % s.CapacityRecords; // 非负
            UpsertCycleStart(epbId, cycleNumber, startUtc.ToLocalTime(), startIndex);
        }
    }

    /// <summary>
    ///     标记一个通道的“正式试验圈”结束；写入进度将更新为 completed。
    /// </summary>
    public void CompleteCycle(int epbId, int cycleNumber, int finalSampleCount, DateTime endUtc)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            MarkCycleCompleted(epbId, cycleNumber, finalSampleCount, endUtc);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;

            // 注释掉，不去处理旧的记录
            // 处理 StopTrigger=EndOfCurrentCycle 或 AfterKMoreCycles
            // if (s.StopTrigger == StopTrigger.EndOfCurrentCycle)
            // {
            //     PersistLatestCyclesNow(epbId, s.StopKeepLatestN, s.StopAction);
            // }
            // else if (s.StopTrigger == StopTrigger.AfterKMoreCycles)
            // {
            //     s.StopAfterK = Math.Max(0, s.StopAfterK - 1);
            //     if (s.StopAfterK == 0)
            //         PersistLatestCyclesNow(epbId, s.StopKeepLatestN, s.StopAction);
            // }
        }
    }

    /// <summary>
    ///     写入单个样本（线程安全）。若未开正式圈，则在 Free-Run 开启时写入（CycleNumber=0）。
    /// </summary>
    public void WriteSample(int epbId, DateTime tsUtc, double epbCurrent, double groupPressure)
    {
        var s = GetState(epbId);

        int cycle;
        int sampleIndex;

        lock (s.Gate)
        {
            if (s.CurrentCycle.HasValue)
            {
                cycle = s.CurrentCycle.Value;
                sampleIndex = s.CurrentSampleIndex++;
            }
            else if (s.FreeRunOn)
            {
                cycle = 0;
                sampleIndex = s.FreeRunSampleIndex++;
            }
            else
            {
                // 未开圈且未开启 Free-Run：按需求不写入
                return;
            }

            var rec = new SampleRecord
            {
                // 统一使用“系统本地时间”写入
                TimestampBinary = tsUtc.ToLocalTime().ToBinary(),
                CycleNumber = cycle,
                SampleIndex = sampleIndex,
                EpbCurrent = epbCurrent,
                GroupPressure = groupPressure
            };

            var idx = s.TotalWritten % s.CapacityRecords;
            var fileOff = idx * SampleRecord.Size;

            // —— 窗口化写入 —— //
            WriteRecord(epbId, fileOff, in rec);
            s.TotalWritten++;

            // 正式圈期间，顺带更新进度（减少 DB 交互可按批处理优化）
            if (cycle > 0) UpdateCycleProgress(epbId, cycle, sampleIndex + 1, tsUtc);
        }
    }

    /// <summary>
    ///     批量写入（传相同长度的时间戳/电流/压力数组）。内部逐点调用写入（便于复用一致的并发/索引逻辑）。
    /// </summary>
    public void WriteBatch(int epbId, DateTime[] tsUtc, double[] epbCurrents, double[] groupPressures)
    {
        if (tsUtc == null || epbCurrents == null || groupPressures == null)
            throw new ArgumentNullException("tsUtc/epbCurrents/groupPressures");
        if (tsUtc.Length != epbCurrents.Length || tsUtc.Length != groupPressures.Length)
            throw new ArgumentException("tsUtc/epbCurrents/groupPressures 长度必须一致");

        for (var i = 0; i < tsUtc.Length; i++)
            WriteSample(epbId, tsUtc[i], epbCurrents[i], groupPressures[i]);
    }

    /// <summary>
    /// 基本为弃用状态
    ///     立即对某通道执行“最新 N 圈”保留。
    ///     action: delete（删索引）/ archive（导出 CSV+BIN 后删索引）。
    /// </summary>
    public void PersistLatestCyclesNow(int epbId, int keepLatestN = 10, string action = "archive")
    {
        keepLatestN = Math.Max(1, keepLatestN);
        action = string.IsNullOrWhiteSpace(action) ? "archive" : action.ToLowerInvariant();

        var purgeList = GetCyclesToPurge(epbId, keepLatestN);
        if (purgeList.Count == 0) return;

        if (action == "archive")
        {
            var dir = Path.Combine(_indexDir, "Archive", $"EPB{epbId}");
            Directory.CreateDirectory(dir);

            foreach (var cy in purgeList)
            {
                // 以圈开始时间命名子目录，便于回放/检索
                var tsFolder = cy.StartTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss");
                var subDir = Path.Combine(dir, tsFolder);
                Directory.CreateDirectory(subDir);

                // 升序导出 CSV
                var csv = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.csv");
                ExportCycleToCsv(epbId, cy, csv);

                // 升序导出二进制快照
                var bin = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.bin");
                ExportCycleToBin(epbId, cy, bin);
            }
        }

        // 删除更早圈的“圈级索引”（业务索引），底层环形数据将被自然覆盖
        DeleteCycles(purgeList);
    }


    /// <summary>
    ///     立即导出某 EPB 通道“最新 N 圈”的数据到本地文件（CSV + BIN），不删除索引。
    ///     <list type="number">
    ///         <item>1. 只导出 <c>status='completed'</c> 的正式圈（CycleNumber &gt; 0）。</item>
    ///         <item>2. 如果实际完成的圈数少于 <paramref name="latestN"/>，则导出全部已完成圈。</item>
    ///         <item>3. 导出路径示例：DataStore\Latest\EPB1\yyyyMMdd_HHmmss\EPB1_Cycle_000001.csv/bin。</item>
    ///     </list>
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <param name="latestN">需要导出的“最近圈数”（默认 10）。</param>
    public void ExportLatestCyclesNow(int epbId, int latestN = 10)
    {
        latestN = Math.Max(1, latestN);

        // 查询最近 N 个已完成的正式圈
        var latestList = GetLatestCycles(epbId, latestN);
        if (latestList.Count == 0)
            return;

        // 与 Archive 区分开，新建 Latest 目录
        var dir = Path.Combine(_indexDir, "Latest", $"EPB{epbId}");
        Directory.CreateDirectory(dir);

        // 以圈开始时间命名子目录，便于回放/检索
        // var tsFolder = cy.StartTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss");
        var tsFolder = DateTime.Now.ToLocalTime().ToString(@"yyyyMMdd_HHmmss");
        var subDir = Path.Combine(dir, tsFolder);
        Directory.CreateDirectory(subDir);

        foreach (var cy in latestList)
        {
            // CSV 文件（圈号升序）
            var csv = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.csv");
            ExportCycleToCsv(epbId, cy, csv);

            // 二进制快照（圈号升序）
            var bin = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.bin");
            ExportCycleToBin(epbId, cy, bin);
        }
    }


    /// <summary>执行全局“只保留最新 N 圈”策略（全部 12 通道）。</summary>
    public void ApplyRetentionPolicy()
    {
        if (_policy.RetainAllData) return;
        for (var epb = 1; epb <= EPB_COUNT; epb++)
            PersistLatestCyclesNow(epb, _policy.RetainLatestCycles, _policy.CleanupMode);
    }

    /// <summary>把 x 对 m 取模并规范化到 [0, m)；m 必须 &gt; 0。</summary>
    private static long ModNN(long x, long m)
    {
        return (x % m + m) % m;
    }

    /// <summary>导出格式选项。</summary>
    public sealed class ExportFormatOptions
    {
        /// <summary>时间格式（默认 yyyy-MM-dd HH:mm:ss.fff）。</summary>
        public string TimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>电流格式（默认 F3）。</summary>
        public string CurrentFormat { get; set; } = "F3";

        /// <summary>压力格式（默认 F1）。</summary>
        public string PressureFormat { get; set; } = "F1";
    }

    private static string FormatLocalTime(long tsBinary, string fmt)
    {
        return DateTime.FromBinary(tsBinary).ToLocalTime().ToString(fmt ?? "yyyy-MM-dd HH:mm:ss.fff");
    }

    /// <summary>
    ///     导出指定“正式圈”的 CSV（升序）。
    /// </summary>
    public void ExportCycleToCsv(int epbId, CycleInfo cycle, string csvPath)
    {
        ExportCycleToCsv(epbId, cycle, csvPath, new ExportFormatOptions());
    }

    public void ExportCycleToCsv(int epbId, CycleInfo cycle, string csvPath, ExportFormatOptions fmt = null)
    {
        fmt ??= new ExportFormatOptions();

        var s = GetState(epbId);
        var capacity = s.CapacityRecords;
        if (capacity <= 0)
        {
            File.WriteAllText(csvPath, "Timestamp,Cycle,SampleIndex,EpbCurrent,GroupPressure", Encoding.UTF8);
            return;
        }

        var start = ModNN(cycle.StartRecordIndex, capacity);
        var count = Math.Min(cycle.SampleCount, (int)capacity);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
        using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);
        sw.WriteLine("Timestamp,Cycle,SampleIndex,EpbCurrent,GroupPressure");
        for (var i = 0; i < count; i++)
        {
            var idx = (start + i) % capacity;
            var rec = ReadRecord(epbId, idx * SampleRecord.Size);
            if (rec.CycleNumber <= 0 || rec.TimestampBinary == 0) continue;

            var tsText = FormatLocalTime(rec.TimestampBinary, fmt.TimeFormat);
            sw.WriteLine(
                $"{tsText},{rec.CycleNumber},{rec.SampleIndex},{rec.EpbCurrent.ToString(fmt.CurrentFormat)},{rec.GroupPressure.ToString(fmt.PressureFormat)}");
        }
    }

    /// <summary>
    ///     将指定“正式圈”的数据导出为二进制快照（每条 SampleRecord 32B 原样序列化，升序写入）。
    /// </summary>
    public void ExportCycleToBin(int epbId, CycleInfo cycle, string binPath)
    {
        var s = GetState(epbId);
        var capacity = s.CapacityRecords;
        if (capacity <= 0) return;

        var start = ModNN(cycle.StartRecordIndex, capacity);
        var count = Math.Min(cycle.SampleCount, (int)capacity);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(binPath)) ?? ".");
        using var fs = new FileStream(binPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);
        for (var i = 0; i < count; i++)
        {
            var idx = (start + i) % capacity;
            var rec = ReadRecord(epbId, idx * SampleRecord.Size);
            if (rec.CycleNumber <= 0 || rec.TimestampBinary == 0) continue;

            bw.Write(rec.TimestampBinary);
            bw.Write(rec.CycleNumber);
            bw.Write(rec.SampleIndex);
            bw.Write(rec.EpbCurrent);
            bw.Write(rec.GroupPressure);
        }
    }

    /// <summary>从二进制快照（ExportCycleToBin 生成）导出 CSV（升序输出）。</summary>
    public void ImportBinToCsv(string binPath, string csvPath, ExportFormatOptions fmt = null)
    {
        fmt ??= new ExportFormatOptions();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);

        sw.WriteLine("Timestamp,Cycle,SampleIndex,EpbCurrent,GroupPressure");

        while (fs.Position + SampleRecord.Size <= fs.Length)
        {
            var tsBin = br.ReadInt64();
            var cyc = br.ReadInt32();
            var idx = br.ReadInt32();
            var cur = br.ReadDouble();
            var pr = br.ReadDouble();

            var tsText = FormatLocalTime(tsBin, fmt.TimeFormat);
            sw.WriteLine($"{tsText},{cyc},{idx},{cur.ToString(fmt.CurrentFormat)},{pr.ToString(fmt.PressureFormat)}");
        }
    }

    /// <summary>
    ///     导出 Free-Run（Cycle=0）最近窗口（按样本数向后回溯），并以升序输出到 CSV。
    /// </summary>
    /// <returns>实际导出的 Free-Run 条数</returns>
    public int ExportFreeRunBySamples(int epbId, int sampleCount, string csvPath, ExportFormatOptions fmt = null)
    {
        fmt ??= new ExportFormatOptions();

        sampleCount = Math.Max(1, sampleCount);
        var s = GetState(epbId);

        var capacity = s.CapacityRecords;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");

        if (capacity <= 0 || s.TotalWritten == 0)
        {
            using var sw0 = new StreamWriter(csvPath, false, Encoding.UTF8);
            sw0.WriteLine("Timestamp,Cycle,SampleIndex,EpbCurrent,GroupPressure");
            return 0;
        }

        var written = s.TotalWritten; // 快照
        var maxBack = Math.Min(written, capacity); // 最多回溯一圈容量
        var list = new List<SampleRecord>(Math.Min(sampleCount, (int)maxBack));

        for (long back = 1; back <= maxBack && list.Count < sampleCount; back++)
        {
            var raw = written - back;
            var idx = raw % capacity;
            if (idx < 0) idx += capacity; // 标准化
            var rec = ReadRecord(epbId, idx * SampleRecord.Size);
            if (rec.CycleNumber == 0 && rec.TimestampBinary != 0) list.Add(rec);
        }

        list.Reverse(); // 升序

        using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);
        sw.WriteLine("Timestamp,Cycle,SampleIndex,EpbCurrent,GroupPressure");
        foreach (var rec in list)
        {
            var tsText = FormatLocalTime(rec.TimestampBinary, fmt.TimeFormat);
            sw.WriteLine(
                $"{tsText},{rec.CycleNumber},{rec.SampleIndex},{rec.EpbCurrent.ToString(fmt.CurrentFormat)},{rec.GroupPressure.ToString(fmt.PressureFormat)}");
        }

        return list.Count;
    }

    /// <summary>获取通道当前圈内已写样本数（用于 CompleteCycle 的 finalSampleCount）。</summary>
    public int GetCurrentCycleSampleCount(int epbId)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            return s.CurrentSampleIndex;
        }
    }

    /// <summary>
    ///     查询指定 EPB 通道当前已存在的“最大正式圈号”（cycle_number），仅统计 CycleNumber &gt; 0。
    ///     后续在 EpbManager 里开启新试验时，就可以（举例）：
    ///     // 每个通道单独算一个“起始圈号基准”
    ///     var last = _diskWriter.GetLastCycleNumber(ch);
    ///     var baseCycle = last;         // 这次试验第1圈就是 baseCycle + 1
    ///     然后把 runner 的 _sessionRunCount 写成 baseCycle + n，再传给 BeginCycle/CompleteCycle
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <returns>若无正式圈记录，则返回 0。</returns>
    public int GetLastCycleNumber(int epbId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT COALESCE(MAX(cycle_number), 0)
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number > 0";
        cmd.Parameters.AddWithValue("@e", epbId);
        var obj = cmd.ExecuteScalar();
        return Convert.ToInt32(obj);
    }



    /// <summary>设置“立即停止并保留最新 N 圈”（上层在 StopChannel 前可调用）。</summary>
    public void StopNowAndPersist(int epbId, int keepLatestN = 10, string action = "archive")
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            s.StopTrigger = StopTrigger.Immediate;
            s.StopKeepLatestN = Math.Max(1, keepLatestN);
            s.StopAction = string.IsNullOrWhiteSpace(action) ? "archive" : action.ToLowerInvariant();
        }

        PersistLatestCyclesNow(epbId, s.StopKeepLatestN, s.StopAction);
    }

    #endregion

    #region 内部：窗口化映射 + 写读 + 文件/索引

    /// <summary>向下对齐到 align 的整数倍。</summary>
    private static long AlignDown(long x, long align)
    {
        return x / align * align;
    }

    /// <summary>向上对齐到 align 的整数倍。</summary>
    private static long AlignUp(long x, long align)
    {
        return (x + align - 1) / align * align;
    }

    /// <summary>
    ///     确保指定通道的视图覆盖目标文件偏移（至少覆盖 <paramref name="minSpanBytes" /> 字节）。<br />
    ///     若不在范围，释放旧视图并以目标点为中心重建窗口。
    /// </summary>
    private void EnsureViewCovers(int ch, long targetOffset, long minSpanBytes)
    {
        var baseOff = _viewBaseOffsets[ch];
        var len = _viewLengths[ch];
        var endOff = baseOff + len;

        var needSpan = Math.Max(1, minSpanBytes);
        if (targetOffset >= baseOff && targetOffset + needSpan - 1 < endOff)
            return;

        var desiredBase = Math.Max(0, targetOffset - VIEW_BYTES / 2);
        var newBase = AlignDown(desiredBase, VIEW_ALIGN);
        var remain = Math.Max(0, _fileBytes - newBase);
        var desiredLen = Math.Min(VIEW_BYTES, remain);
        var newLen = AlignUp(Math.Max(1, desiredLen), VIEW_ALIGN);

        if (newBase + newLen > _fileBytes)
            newLen = AlignUp(Math.Max(1, _fileBytes - newBase), VIEW_ALIGN);

        _views[ch]?.Dispose();
        _views[ch] = _mmfs[ch].CreateViewAccessor(newBase, newLen, MemoryMappedFileAccess.ReadWrite);
        _viewBaseOffsets[ch] = newBase;
        _viewLengths[ch] = newLen;
    }

    /// <summary>
    ///     写入一条记录（自动窗口化）。
    /// </summary>
    private void WriteRecord(int epbId, long fileOffset, in SampleRecord r)
    {
        EnsureViewCovers(epbId, fileOffset, SampleRecord.Size);
        var baseOff = _viewBaseOffsets[epbId];
        var v = _views[epbId];
        var off = fileOffset - baseOff;

        v.Write(off + 0, r.TimestampBinary);
        v.Write(off + 8, r.CycleNumber);
        v.Write(off + 12, r.SampleIndex);
        v.Write(off + 16, r.EpbCurrent);
        v.Write(off + 24, r.GroupPressure);
    }

    /// <summary>
    ///     读取一条记录（自动窗口化）。
    /// </summary>
    private SampleRecord ReadRecord(int epbId, long fileOffset)
    {
        EnsureViewCovers(epbId, fileOffset, SampleRecord.Size);
        var baseOff = _viewBaseOffsets[epbId];
        var v = _views[epbId];
        var off = fileOffset - baseOff;

        return new SampleRecord
        {
            TimestampBinary = v.ReadInt64(off + 0),
            CycleNumber = v.ReadInt32(off + 8),
            SampleIndex = v.ReadInt32(off + 12),
            EpbCurrent = v.ReadDouble(off + 16),
            GroupPressure = v.ReadDouble(off + 24)
        };
    }

    // 文件路径
    private string GetDatPath(int epbId)
    {
        return Path.Combine(_rootDir, $"EPB{epbId}_sliding.dat");
    }

    private static void EnsureFixedSizeFile(string path, long sizeBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length != sizeBytes)
            {
                fs.SetLength(sizeBytes);
                fs.Flush(true);
            }

            return;
        }

        using var fsNew = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
        fsNew.SetLength(sizeBytes);
        fsNew.Flush(true);
    }

    private EpbState GetState(int epbId)
    {
        if (epbId < 1 || epbId > EPB_COUNT) throw new ArgumentOutOfRangeException(nameof(epbId));
        return _states[epbId] ??= new EpbState();
    }

    // —— SQLite 圈级索引 —— //
    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {TABLE_CYCLES}(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  epb_id INTEGER NOT NULL,
  cycle_number INTEGER NOT NULL,
  start_time TEXT NOT NULL,
  end_time TEXT,
  start_position INTEGER NOT NULL,   -- 记录级起始索引（相对 .dat 的“记录号”）
  sample_count INTEGER DEFAULT 0,
  status TEXT DEFAULT 'running',
  created_at TEXT DEFAULT (datetime('now')),
  UNIQUE(epb_id, cycle_number)
);
CREATE INDEX IF NOT EXISTS idx_cycles_epb ON {TABLE_CYCLES}(epb_id, cycle_number);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    ///     写入某圈的起始信息。调用方需保证同一 epbId 上的 cycleNumber 全局唯一（不复用旧圈号）。<br/>
    ///     若传入已存在的 circleNumber，将抛出约束异常（用于提示上层逻辑错误）。
    /// </summary>
    private void UpsertCycleStart(int epbId, int cycleNumber, DateTime startUtc, long startRecordIndex)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {TABLE_CYCLES}(epb_id, cycle_number, start_time, start_position, status, sample_count)
VALUES(@e,@c,@st,@pos,'running',0);";
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@c", cycleNumber);
        cmd.Parameters.AddWithValue("@st", startUtc.ToLocalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@pos", startRecordIndex);
        cmd.ExecuteNonQuery();
    }


    private void UpdateCycleProgress(int epbId, int cycleNumber, int sampleCount, DateTime lastUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET sample_count=@n, end_time=@et, status='running'
 WHERE epb_id=@e AND cycle_number=@c";
        cmd.Parameters.AddWithValue("@n", sampleCount);
        cmd.Parameters.AddWithValue("@et", lastUtc.ToLocalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@c", cycleNumber);
        cmd.ExecuteNonQuery();
    }

    private void MarkCycleCompleted(int epbId, int cycleNumber, int finalSampleCount, DateTime endUtc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET sample_count=@n, end_time=@et, status='completed'
 WHERE epb_id=@e AND cycle_number=@c";
        cmd.Parameters.AddWithValue("@n", finalSampleCount);
        cmd.Parameters.AddWithValue("@et", endUtc.ToLocalTime().ToString("o"));
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@c", cycleNumber);
        cmd.ExecuteNonQuery();
    }

    private List<CycleInfo> GetCyclesToPurge(int epbId, int keepLatestN)
    {
        var list = new List<CycleInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
WITH nth AS (
  SELECT cycle_number FROM {TABLE_CYCLES}
   WHERE epb_id=@e
   ORDER BY cycle_number DESC
   LIMIT 1 OFFSET @off
)
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number < COALESCE((SELECT cycle_number FROM nth), -1)
 ORDER BY cycle_number ASC";
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@off", Math.Max(keepLatestN - 1, 0));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new CycleInfo
            {
                EpbId = rd.GetInt32(0),
                CycleNumber = rd.GetInt32(1),
                StartTimeUtc = DateTime.Parse(rd.GetString(2)),
                EndTimeUtc = rd.IsDBNull(3) ? null : DateTime.Parse(rd.GetString(3)),
                StartRecordIndex = rd.GetInt64(4),
                SampleCount = rd.GetInt32(5),
                Status = rd.GetString(6)
            });
        return list;
    }

    /// <summary>
    ///     查询某 EPB 通道“最新 N 圈”的圈信息（只取已完成圈）。
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）</param>
    /// <param name="latestN">需要的圈数（取最近的 N 圈）</param>
    /// <returns>按圈号升序排列的圈信息列表。</returns>
    private List<CycleInfo> GetLatestCycles(int epbId, int latestN)
    {
        latestN = Math.Max(1, latestN);

        var list = new List<CycleInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number > 0
   AND status = 'completed'
 ORDER BY cycle_number DESC
 LIMIT @n";
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@n", latestN);

        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new CycleInfo
            {
                EpbId = rd.GetInt32(0),
                CycleNumber = rd.GetInt32(1),
                StartTimeUtc = DateTime.Parse(rd.GetString(2)),
                EndTimeUtc = rd.IsDBNull(3) ? (DateTime?)null : DateTime.Parse(rd.GetString(3)),
                StartRecordIndex = rd.GetInt64(4),
                SampleCount = rd.GetInt32(5),
                Status = rd.GetString(6)
            });
        }

        // 为了导出时按圈号从小到大排序，重新升序排一下
        list.Sort((a, b) => a.CycleNumber.CompareTo(b.CycleNumber));
        return list;
    }


    private void DeleteCycles(IEnumerable<CycleInfo> cycles)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TABLE_CYCLES} WHERE epb_id=@e AND cycle_number=@c";
        var pE = cmd.Parameters.Add("@e", DbType.Int32);
        var pC = cmd.Parameters.Add("@c", DbType.Int32);

        foreach (var cy in cycles)
        {
            pE.Value = cy.EpbId;
            pC.Value = cy.CycleNumber;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    #endregion
}

/// <summary>圈级元数据（SQLite 映射）。</summary>
public sealed class CycleInfo
{
    public int EpbId { get; set; }
    public int CycleNumber { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }
    public long StartRecordIndex { get; set; } // 记录级起始索引
    public int SampleCount { get; set; }
    public string Status { get; set; }
}

#region 接口（保持你原文件里的接口/适配器，未改动其签名）

/// <summary>
///     供 EpbManager 调用的“圈级记录器”接口；
///     由 EpbDiskWriter 实现（或用适配器包装后实现）。
/// </summary>
public interface IEpbCycleRecorder
{
    /// <summary>标记某 EPB 在某圈开始；若未调用过，将使用圈号0做“常开记录”</summary>
    void BeginCycle(int epbId, int cycleNumber, DateTime utcNow);

    /// <summary>圈内批量写入：同批次时间戳、对应电流数组、对应组压数组</summary>
    void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures);

    /// <summary>获取当前圈已累计样本数（用于记账/日志/封圈）</summary>
    int GetCurrentCycleSampleCount(int epbId);

    /// <summary>圈结束（落盘结账）。finalN 可传入 GetCurrentCycleSampleCount 返回值</summary>
    void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow);

    /// <summary>
    ///     在停止卡钳时，将该通道“最新 N 圈”正式数据（Cycle&gt;0）落盘；
    ///     Free-Run（Cycle=0）不在此范围，若需要可单独调用导出 API。
    /// </summary>
    void FlushRecent(int epbId, int lastNCycles);


    int GetLastCycleNumber(int ch);
}

/// <summary>
///     将 EpbDiskWriter 适配为 IEpbCycleRecorder，避免 EpbManager 直接依赖具体类。
/// </summary>
public sealed class DiskWriterRecorderAdapter : IEpbCycleRecorder
{
    private readonly EpbDiskWriter _writer;

    public DiskWriterRecorderAdapter(EpbDiskWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void BeginCycle(int epbId, int cycleNumber, DateTime startUtc)
    {
        _writer.BeginCycle(epbId, cycleNumber, startUtc);
    }

    public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures)
    {
        _writer.WriteBatch(epbId, tsUtc, currents, groupPressures);
    }

    public int GetCurrentCycleSampleCount(int epbId)
    {
        return _writer.GetCurrentCycleSampleCount(epbId);
    }

    public void FlushRecentOld(int epbId, int lastNCycles)
    {
        _writer.PersistLatestCyclesNow(epbId, Math.Max(1, lastNCycles));
    }

    /// <summary>
    ///     在停止某 EPB 通道时调用：
    ///     导出该通道“最近 N 圈”的数据到本地文件（CSV + BIN）。
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <param name="lastNCycles">要导出的圈数（最近 N 圈）。</param>
    public void FlushRecent(int epbId, int lastNCycles)
        => _writer.ExportLatestCyclesNow(epbId, Math.Max(1, lastNCycles));

    /// <summary>
    ///  查询指定 EPB 通道当前已存在的“最大正式圈号”（cycle_number），仅统计 CycleNumber &gt; 0。
    /// </summary>
    /// <param name="ch"></param>
    /// <returns></returns>
    public int GetLastCycleNumber(int ch)
    {
        return _writer.GetLastCycleNumber(ch);
    }


    public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime endUtc)
    {
        _writer.CompleteCycle(epbId, cycleNumber, finalN, endUtc);
    }
}

#endregion