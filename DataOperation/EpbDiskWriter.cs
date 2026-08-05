// ReSharper disable InconsistentNaming
// ReSharper disable RedundantNameQualifier

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;

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

    /// <summary>活动圈最大样本数；0表示不单独限制。</summary>
    public int MaxActiveCycleRecords { get; set; }
}

public sealed class ActiveCycleDataLimitExceededException : InvalidOperationException
{
    public ActiveCycleDataLimitExceededException(int epbId, int cycleNumber, int limit)
        : base($"ActiveCycleDataLimitExceeded EPB={epbId} Cycle={cycleNumber} Limit={limit}") { }
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
    private const string CSV_HEADER =
        "Timestamp,RelativeTimeSeconds,Cycle,SampleIndex,EpbCurrent,GroupPressure";

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
        public DateTime CurrentCycleStartUtc;
        public DateTime? CurrentCycleEndUtc;
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
    private readonly object _dbGate = new();
    private SQLiteTransaction _activeBatchTransaction;
    private readonly object[] _latestExportGates = Enumerable.Range(0, EPB_COUNT + 1)
        .Select(_ => new object())
        .ToArray();
    private long _latestExportSequence;
    private readonly ConcurrentDictionary<string, object> _exportTargetGates =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>
    /// 设置单个活动圈允许接收的最大样本数。0 表示不限制。
    /// 该值只影响尚未写入的后续批次，不改写已经封存的数据。
    /// </summary>
    public void SetMaxActiveCycleRecords(int maxRecords)
    {
        if (maxRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRecords));
        _policy.MaxActiveCycleRecords = maxRecords;
    }

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
            _states[ch].TotalWritten = RestoreNextWritePosition(
                ch,
                _states[ch].CapacityRecords);
        }
    }

    /// <summary>
    /// 释放 EpbDiskWriter：
    /// <list type="bullet">
    ///     <item>1. 依次释放 12 路内存映射视图和文件；</item>
    ///     <item>2. 关闭并释放 SQLite 连接；</item>
    ///     <item>3. 调用 <see cref="SQLiteConnection.ClearAllPools"/>，
    ///         确保 SQLite 连接池中的句柄也完全释放，
    ///         这样外部就可以安全删除 index.db 文件。</item>
    /// </list>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 1) 关闭 12 路内存映射视图和文件
        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            try
            {
                _views[ch]?.Dispose();
            }
            catch
            {
                // 关闭阶段忽略单个通道失败
            }

            try
            {
                _mmfs[ch]?.Dispose();
            }
            catch
            {
                // 关闭阶段忽略单个通道失败
            }

            _views[ch] = null;
            _mmfs[ch] = null;
        }

        // 2) 关闭并释放 SQLite 连接
        try
        {
            if (_conn != null)
            {
                // 显式 Close 再 Dispose，保证连接状态正确
                _conn.Close();
                _conn.Dispose();
            }
        }
        finally
        {
            // 3) 非常关键：清空 SQLite 连接池，释放 index.db 的文件句柄
            //    否则即使调用了 Dispose，连接池中的物理连接仍然可能占用数据库文件，
            //    导致其它地方 File.Delete("index.db") 失败。
            SQLiteConnection.ClearAllPools();
        }
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
            if (s.CurrentCycle.HasValue)
                throw new InvalidOperationException(
                    $"EPB[{epbId}] 圈 {s.CurrentCycle.Value} 尚未封存，不能开始圈 {cycleNumber}。");

            var startIndex = s.TotalWritten % s.CapacityRecords; // 非负
            UpsertCycleStart(epbId, cycleNumber, startUtc.ToLocalTime(), startIndex);
            s.CurrentCycle = cycleNumber;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleStartUtc = startUtc.ToUniversalTime();
            s.CurrentCycleEndUtc = null;
        }
    }

    /// <summary>
    /// 原子分配并开始一个学习圈。学习圈使用负数内部圈号，不参与正式圈号、成功计数或最近圈导出。
    /// </summary>
    public int BeginLearningCycle(int epbId, DateTime startUtc)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            if (s.CapacityRecords <= 0)
                throw new InvalidOperationException("CapacityRecords must be positive.");
            if (s.CurrentCycle.HasValue)
                throw new InvalidOperationException(
                    $"EPB[{epbId}] 圈 {s.CurrentCycle.Value} 尚未封存，不能开始学习圈。");

            var cycleNumber = GetNextLearningCycleNumber(epbId);
            var startIndex = s.TotalWritten % s.CapacityRecords;
            UpsertCycleStart(epbId, cycleNumber, startUtc.ToLocalTime(), startIndex);
            s.CurrentCycle = cycleNumber;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleStartUtc = startUtc.ToUniversalTime();
            s.CurrentCycleEndUtc = null;
            return cycleNumber;
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
            s.CurrentCycleEndUtc = null;

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
    ///     将指定通道的当前圈以“报警中断”封圈。
    ///     <list type="bullet">
    ///         <item>报警圈只用于追溯，不计为成功圈；封圈可避免遗留 <c>status='running'</c>。</item>
    ///         <item>该方法与 <see cref="CompleteCycle"/> 的区别仅在于将 <c>status</c> 写为 <c>alarm</c>。</item>
    ///     </list>
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <param name="cycleNumber">圈号（必须与 BeginCycle 使用的圈号一致）。</param>
    /// <param name="finalSampleCount">截至报警发生时的样本数（通常来自 <see cref="GetCurrentCycleSampleCount"/>）。</param>
    /// <param name="endUtc">报警发生的时间（UTC）。</param>
    public void AlarmCycle(int epbId, int cycleNumber, int finalSampleCount, DateTime endUtc)
    {
        var s = GetState(epbId);
        lock (s.Gate)
        {
            MarkCycleAlarm(epbId, cycleNumber, finalSampleCount, endUtc);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleEndUtc = null;
        }
    }

    /// <summary>
    /// 在单通道锁内冻结当前报警圈，原子导出 CSV/BIN，校验后以相同边界封存数据库。
    /// </summary>
    public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc)
    {
        return SealAndExportCycle(
            epbId,
            cycleNumber,
            exportDir,
            fallbackEndUtc,
            "alarm");
    }

    /// <summary>
    /// 在单通道锁内原子领取、导出并封存当前圈。报警后台与学习收尾并发时只有首个调用方能够领取。
    /// </summary>
    public AlarmCycleSnapshotEvidence SealAndExportCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc,
        string status)
    {
        if (string.IsNullOrWhiteSpace(exportDir))
            throw new ArgumentException("exportDir is required", nameof(exportDir));
        var normalizedStatus = NormalizeSealStatus(status);

        var stem = $"EPB{epbId}_Cycle_{cycleNumber:D6}";
        var csvPath = Path.Combine(exportDir, stem + ".csv");
        var binPath = Path.Combine(exportDir, stem + ".bin");
        var evidence = new AlarmCycleSnapshotEvidence
        {
            CsvPath = csvPath,
            BinPath = binPath,
            FinalStatus = normalizedStatus
        };
        var s = GetState(epbId);
        lock (s.Gate)
        {
            var finalSampleCount = Math.Max(0, s.CurrentSampleIndex);
            var endUtc = fallbackEndUtc.Kind == DateTimeKind.Utc
                ? fallbackEndUtc
                : fallbackEndUtc.ToUniversalTime();
            if (s.CurrentCycle != cycleNumber)
            {
                evidence.WasClaimed = false;
                evidence.ValidationError =
                    $"EPB[{epbId}] Cycle={cycleNumber} 已由其它收尾路径封存，当前圈为 " +
                    $"{s.CurrentCycle?.ToString() ?? "null"}。";
                return evidence;
            }

            evidence.WasClaimed = true;
            try
            {
                var cycle = GetCycleInfo(epbId, cycleNumber);
                cycle.SampleCount = finalSampleCount;
                var allowEmpty = normalizedStatus == "learning_canceled" ||
                                 normalizedStatus == "learning_failed";
                if (cycle.SampleCount <= 0 && !allowEmpty)
                    throw new InvalidDataException($"EPB[{epbId}] Cycle={cycleNumber} 没有可封存样本。");

                var records = cycle.SampleCount > 0
                    ? ReadCycleRecordsFromRing(epbId, cycle, s.CapacityRecords)
                    : new List<SampleRecord>();
                if (records.Count > 0)
                    endUtc = DateTime.FromBinary(records[records.Count - 1].TimestampBinary).ToUniversalTime();
                Directory.CreateDirectory(exportDir);
                var csvTemp = GetTempPath(csvPath);
                var binTemp = GetTempPath(binPath);
                try
                {
                    using (var sw = new StreamWriter(csvTemp, false, Encoding.UTF8))
                        WriteCsvRecords(sw, records, new ExportFormatOptions());
                    using (var fs = new FileStream(binTemp, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var bw = new BinaryWriter(fs))
                        WriteBinRecords(bw, records);
                    CommitPair(csvTemp, csvPath, binTemp, binPath);
                }
                finally
                {
                    TryDeleteFile(csvTemp);
                    TryDeleteFile(binTemp);
                }

                evidence = ValidateAlarmCycleSnapshotPair(
                    csvPath,
                    binPath,
                    epbId,
                    cycleNumber,
                    allowEmpty);
                evidence.WasClaimed = true;
                evidence.FinalStatus = normalizedStatus;
                if (!evidence.IsValid)
                    throw new InvalidDataException(evidence.ValidationError);

                MarkCycleFinalized(
                    epbId,
                    cycleNumber,
                    evidence.SampleCount,
                    evidence.LastSampleUtc ?? endUtc,
                    normalizedStatus);
            }
            catch (Exception ex)
            {
                evidence.IsValid = false;
                evidence.WasClaimed = true;
                evidence.FinalStatus = normalizedStatus;
                evidence.SampleCount = finalSampleCount;
                evidence.ValidationError = ex.Message;
                try
                {
                    MarkCycleAborted(
                        epbId,
                        cycleNumber,
                        finalSampleCount,
                        evidence.LastSampleUtc ?? endUtc,
                        normalizedStatus.StartsWith("learning_", StringComparison.Ordinal)
                            ? "learning_failed"
                            : "failed");
                }
                catch (Exception dbEx)
                {
                    evidence.ValidationError += " | DBFinalizeFailed: " + dbEx.Message;
                }
            }
            finally
            {
                s.CurrentCycle = null;
                s.CurrentSampleIndex = 0;
                s.CurrentCycleEndUtc = null;
            }
        }

        return evidence;
    }

    private static string NormalizeSealStatus(string status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "alarm":
            case "learning_completed":
            case "learning_canceled":
            case "learning_failed":
                return normalized;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "仅支持 alarm、learning_completed、learning_canceled、learning_failed。");
        }
    }

    /// <summary>逐条校验圈快照 CSV/BIN 对的数量、圈号、序号和时间范围。</summary>
    public static AlarmCycleSnapshotEvidence ValidateAlarmCycleSnapshotPair(
        string csvPath,
        string binPath,
        int epbId,
        int cycleNumber)
    {
        return ValidateAlarmCycleSnapshotPair(csvPath, binPath, epbId, cycleNumber, false);
    }

    private static AlarmCycleSnapshotEvidence ValidateAlarmCycleSnapshotPair(
        string csvPath,
        string binPath,
        int epbId,
        int cycleNumber,
        bool allowEmpty)
    {
        var evidence = new AlarmCycleSnapshotEvidence
        {
            CsvPath = csvPath,
            BinPath = binPath
        };
        try
        {
            if (!File.Exists(csvPath) || !File.Exists(binPath))
                throw new InvalidDataException("圈快照 CSV/BIN 文件不完整。");

            var binLength = new FileInfo(binPath).Length;
            if ((!allowEmpty && binLength <= 0) || binLength % SampleRecord.Size != 0)
                throw new InvalidDataException(
                    $"BIN 长度 {binLength} 不是 {SampleRecord.Size} 字节记录的整数倍。");

            var binCount = checked((int)(binLength / SampleRecord.Size));
            DateTime? firstUtc = null;
            DateTime? lastUtc = null;
            using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                for (var i = 0; i < binCount; i++)
                {
                    var tsBinary = br.ReadInt64();
                    var actualCycle = br.ReadInt32();
                    var sampleIndex = br.ReadInt32();
                    br.ReadDouble();
                    br.ReadDouble();
                    if (actualCycle != cycleNumber)
                        throw new InvalidDataException(
                            $"BIN 圈号不一致：Expected={cycleNumber} Actual={actualCycle} Index={i}。");
                    if (sampleIndex != i)
                        throw new InvalidDataException(
                            $"BIN 样本序号不连续：Expected={i} Actual={sampleIndex}。");

                    var utc = DateTime.FromBinary(tsBinary).ToUniversalTime();
                    if (lastUtc.HasValue && utc < lastUtc.Value)
                        throw new InvalidDataException($"BIN 时间戳回退：Index={i}。");
                    firstUtc ??= utc;
                    lastUtc = utc;
                }
            }

            var csvCount = 0;
            using (var sr = new StreamReader(csvPath, Encoding.UTF8, true))
            {
                var header = sr.ReadLine();
                if (!string.Equals(header, CSV_HEADER, StringComparison.Ordinal))
                    throw new InvalidDataException("CSV 表头不符合报警证据格式。");
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var fields = line.Split(',');
                    if (fields.Length < 4 ||
                        !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualCycle) ||
                        !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleIndex))
                    {
                        throw new InvalidDataException($"CSV 第 {csvCount + 2} 行无法解析圈号或样本序号。");
                    }
                    if (actualCycle != cycleNumber || sampleIndex != csvCount)
                    {
                        throw new InvalidDataException(
                            $"CSV 证据不连续：ExpectedCycle={cycleNumber} ActualCycle={actualCycle} " +
                            $"ExpectedIndex={csvCount} ActualIndex={sampleIndex}。");
                    }
                    csvCount++;
                }
            }

            if (csvCount != binCount)
                throw new InvalidDataException($"CSV/BIN 样本数不一致：CSV={csvCount} BIN={binCount}。");

            evidence.IsValid = true;
            evidence.SampleCount = binCount;
            evidence.FirstSampleUtc = firstUtc;
            evidence.LastSampleUtc = lastUtc;
            evidence.ValidationError = string.Empty;
        }
        catch (Exception ex)
        {
            evidence.IsValid = false;
            evidence.ValidationError = $"EPB[{epbId}] Cycle={cycleNumber} 圈快照证据校验失败：{ex.Message}";
        }

        return evidence;
    }

    /// <summary>
    /// 将未完整完成的圈封为 canceled/failed。此类圈不参与成功计数和正常圈导出。
    /// </summary>
    public void AbortCycle(
        int epbId,
        int cycleNumber,
        int finalSampleCount,
        DateTime endUtc,
        string status)
    {
        var normalized = string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
            ? "canceled"
            : "failed";
        var s = GetState(epbId);
        lock (s.Gate)
        {
            MarkCycleAborted(epbId, cycleNumber, finalSampleCount, endUtc, normalized);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleEndUtc = null;
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
                if (_policy.MaxActiveCycleRecords > 0 &&
                    s.CurrentSampleIndex >= _policy.MaxActiveCycleRecords)
                    throw new ActiveCycleDataLimitExceededException(
                        epbId,
                        cycle,
                        _policy.MaxActiveCycleRecords);
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
        => WriteBatch(epbId, tsUtc, epbCurrents, groupPressures, tsUtc?.Length ?? 0);

    public void WriteBatch(
        int epbId,
        DateTime[] tsUtc,
        double[] epbCurrents,
        double[] groupPressures,
        int count)
    {
        if (tsUtc == null || epbCurrents == null || groupPressures == null)
            throw new ArgumentNullException("tsUtc/epbCurrents/groupPressures");
        if (count < 0 || count > tsUtc.Length || count > epbCurrents.Length || count > groupPressures.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        var state = GetState(epbId);
        lock (state.Gate)
        {
            var from = 0;
            var to = count;
            if (state.CurrentCycle.HasValue)
            {
                while (from < to && tsUtc[from].ToUniversalTime() < state.CurrentCycleStartUtc) from++;
                if (state.CurrentCycleEndUtc.HasValue)
                    while (to > from && tsUtc[to - 1].ToUniversalTime() > state.CurrentCycleEndUtc.Value) to--;
            }
            var accepted = to - from;
            if (accepted <= 0) return;

            if (state.CurrentCycle.HasValue &&
                _policy.MaxActiveCycleRecords > 0 &&
                state.CurrentSampleIndex + accepted > _policy.MaxActiveCycleRecords)
                throw new ActiveCycleDataLimitExceededException(
                    epbId,
                    state.CurrentCycle.Value,
                    _policy.MaxActiveCycleRecords);

            var cycle = state.CurrentCycle ?? (state.FreeRunOn ? 0 : int.MinValue);
            if (cycle == int.MinValue) return;
            var firstSampleIndex = cycle > 0 || cycle < 0
                ? state.CurrentSampleIndex
                : state.FreeRunSampleIndex;
            var records = ArrayPool<SampleRecord>.Shared.Rent(accepted);
            try
            {
                for (var i = 0; i < accepted; i++)
                {
                    var source = from + i;
                    records[i] = new SampleRecord
                    {
                        TimestampBinary = tsUtc[source].ToLocalTime().ToBinary(),
                        CycleNumber = cycle,
                        SampleIndex = firstSampleIndex + i,
                        EpbCurrent = epbCurrents[source],
                        GroupPressure = groupPressures[source]
                    };
                }

                WriteRecordBatch(epbId, state, records, accepted);
                if (cycle == 0) state.FreeRunSampleIndex += accepted;
                else state.CurrentSampleIndex += accepted;
                state.TotalWritten += accepted;

                // 每通道每批最多一次 SQLite 进度更新。
                if (cycle != 0)
                    UpdateCycleProgress(epbId, cycle, state.CurrentSampleIndex, tsUtc[to - 1]);
            }
            finally
            {
                ArrayPool<SampleRecord>.Shared.Return(records, clearArray: false);
            }
        }
    }

    public void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        IReadOnlyList<EpbChannelDiskBatch> channels,
        int count)
    {
        if (channels == null) throw new ArgumentNullException(nameof(channels));
        if (channels.Count == 0) return;
        var rented = ArrayPool<EpbChannelDiskBatch>.Shared.Rent(channels.Count);
        try
        {
            for (var i = 0; i < channels.Count; i++) rented[i] = channels[i];
            WriteDeviceBatch(timestampsUtc, rented, channels.Count, count);
        }
        finally
        {
            Array.Clear(rented, 0, channels.Count);
            ArrayPool<EpbChannelDiskBatch>.Shared.Return(rented, clearArray: false);
        }
    }

    public void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount)
    {
        if (timestampsUtc == null) throw new ArgumentNullException(nameof(timestampsUtc));
        if (channels == null) throw new ArgumentNullException(nameof(channels));
        if (channelCount < 0 || channelCount > channels.Length)
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (channelCount == 0) return;

        // 设备通道通常已按 EPB 编号排列；插入排序只处理 2~4 个描述符，
        // 不再为每个 10 ms 批次创建 Where/OrderBy/ToArray 对象图。
        for (var i = 1; i < channelCount; i++)
        {
            var value = channels[i];
            var j = i - 1;
            while (j >= 0 && channels[j].EpbId > value.EpbId)
            {
                channels[j + 1] = channels[j];
                j--;
            }
            channels[j + 1] = value;
        }

        var lockedStates = ArrayPool<EpbState>.Shared.Rent(channelCount);
        var acquired = 0;
        try
        {
            for (var i = 0; i < channelCount; i++)
            {
                var state = GetState(channels[i].EpbId);
                Monitor.Enter(state.Gate);
                lockedStates[acquired++] = state;
            }

            lock (_dbGate)
            {
                using var transaction = _conn.BeginTransaction();
                _activeBatchTransaction = transaction;
                try
                {
                    for (var i = 0; i < channelCount; i++)
                    {
                        var channel = channels[i];
                        WriteBatch(
                            channel.EpbId,
                            timestampsUtc,
                            channel.Currents,
                            channel.Pressures,
                            sampleCount);
                    }
                    transaction.Commit();
                }
                finally
                {
                    _activeBatchTransaction = null;
                }
            }
        }
        finally
        {
            for (var i = acquired - 1; i >= 0; i--)
            {
                Monitor.Exit(lockedStates[i].Gate);
                lockedStates[i] = null;
            }
            ArrayPool<EpbState>.Shared.Return(lockedStates, clearArray: false);
        }
    }

    public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc)
    {
        var state = GetState(epbId);
        lock (state.Gate)
        {
            if (state.CurrentCycle == cycleNumber)
                state.CurrentCycleEndUtc = endUtc.ToUniversalTime();
        }
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

        if (action != "archive")
        {
            DeleteCycles(purgeList);
            return;
        }

        var dir = Path.Combine(_indexDir, "Archive", $"EPB{epbId}");
        Directory.CreateDirectory(dir);
        var exported = new List<CycleInfo>();
        var failures = new List<Exception>();

        foreach (var cy in purgeList)
        {
            // 以圈开始时间命名子目录，便于回放/检索
            var tsFolder = cy.StartTimeUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss");
            var subDir = Path.Combine(dir, tsFolder);
            Directory.CreateDirectory(subDir);

            try
            {
                var csv = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.csv");
                var bin = Path.Combine(subDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.bin");
                ExportCyclePair(epbId, cy, csv, bin, new ExportFormatOptions());
                exported.Add(cy);
            }
            catch (Exception ex)
            {
                failures.Add(WrapExportFailure(epbId, cy, ex));
                WriteExportErrors(subDir, failures[failures.Count - 1]);
            }
        }

        // 只有 CSV/BIN 均成功生成的圈才能删除索引；失败圈保留以便追溯。
        if (exported.Count > 0)
            DeleteCycles(exported);

        ThrowIfExportFailures(epbId, failures);
    }


    /// <summary>
    ///     立即导出某 EPB 通道“最新 N 圈”的数据到本地文件（CSV + BIN），不删除索引。
    ///     <list type="number">
    ///         <item>1. 只导出 <c>status='completed'</c> 或 <c>status='alarm'</c> 的正式圈（CycleNumber &gt; 0）。</item>
    ///         <item>2. 如果实际完成的圈数少于 <paramref name="latestN"/>，则导出全部已完成圈。</item>
    ///         <item>3. 导出路径示例：DataStore\Latest\EPB1\yyyyMMdd_HHmmss\EPB1_Cycle_000001.csv/bin。</item>
    ///     </list>
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <param name="latestN">需要导出的“最近圈数”（默认 10）。</param>
    public void ExportLatestCyclesNow(int epbId, int latestN = 10)
    {
        lock (_latestExportGates[epbId])
        {
            latestN = Math.Max(1, latestN);
            var latestList = GetLatestCycles(epbId, latestN, includeRunningCycle: false);
            if (latestList.Count == 0) return;

            var dir = Path.Combine(_indexDir, "Latest", $"EPB{epbId}");
            Directory.CreateDirectory(dir);
            var sequence = Interlocked.Increment(ref _latestExportSequence);
            var name = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{sequence:D6}";
            var staging = Path.Combine(dir, $".{name}.tmp-{Guid.NewGuid():N}");
            var final = Path.Combine(dir, name);
            Directory.CreateDirectory(staging);
            try
            {
                ExportCycleList(epbId, latestList, staging);
                ValidateExportDirectory(epbId, latestList, staging);
                Directory.Move(staging, final);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, true); } catch { }
                }
            }
        }
    }

    private static void ValidateExportDirectory(
        int epbId,
        IEnumerable<CycleInfo> cycles,
        string directory)
    {
        foreach (var cycle in cycles)
        {
            var csv = Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.csv");
            var bin = Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.bin");
            var validation = ValidateAlarmCycleSnapshotPair(csv, bin, epbId, cycle.CycleNumber);
            if (!validation.IsValid || validation.SampleCount != cycle.SampleCount)
                throw new InvalidDataException(validation.ValidationError);
        }
    }


    /// <summary>
    ///     导出某 EPB 通道“最新 N 圈”的数据到指定目录（CSV + BIN），不删除索引。
    ///     可选择是否包含当前 <c>status='running'</c> 的圈（用于“报警快照：当前圈+之前9圈”）。
    /// </summary>
    public void ExportLatestCyclesTo(int epbId, int latestN, string exportDir, bool includeRunningCycle)
    {
        latestN = Math.Max(1, latestN);
        if (string.IsNullOrWhiteSpace(exportDir))
            throw new ArgumentException("exportDir is required", nameof(exportDir));

        var fullDirectory = Path.GetFullPath(exportDir);
        var gate = _exportTargetGates.GetOrAdd(fullDirectory, _ => new object());
        lock (gate)
        {
            var latestList = GetLatestCycles(epbId, latestN, includeRunningCycle);
            if (latestList.Count == 0) return;
            Directory.CreateDirectory(fullDirectory);
            ExportCycleList(epbId, latestList, fullDirectory);
        }
    }

    public CycleSnapshotEvidence ExportCompletedCycleTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin)
    {
        if (!saveCsv && !saveBin)
            throw new ArgumentException("至少启用CSV或BIN一种快照格式。");
        var cycle = GetCycleInfo(epbId, cycleNumber);
        if (!string.Equals(cycle.Status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"EPB[{epbId}] Cycle={cycleNumber} Status={cycle.Status} 不是完整正式圈。");
        Directory.CreateDirectory(exportDir);
        var csv = saveCsv ? Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cycleNumber:D6}.csv") : null;
        var bin = saveBin ? Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cycleNumber:D6}.bin") : null;
        if (saveCsv && saveBin)
            ExportCyclePair(epbId, cycle, csv, bin, new ExportFormatOptions());
        else if (saveCsv)
            ExportCycleToCsv(epbId, cycle, csv);
        else
            ExportCycleToBin(epbId, cycle, bin);

        return new CycleSnapshotEvidence
        {
            EpbId = epbId,
            CycleNumber = cycleNumber,
            FirstSampleUtc = cycle.StartTimeUtc,
            LastSampleUtc = cycle.EndTimeUtc ?? cycle.StartTimeUtc,
            SampleCount = cycle.SampleCount,
            IsCompleteCycle = true,
            CsvPath = csv,
            BinPath = bin
        };
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
        /// <summary>时间格式（默认保留 DateTime 的 100 ns 精度）。</summary>
        public string TimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fffffff";

        /// <summary>电流格式（默认 F3）。</summary>
        public string CurrentFormat { get; set; } = "F3";

        /// <summary>压力格式（默认 F1）。</summary>
        public string PressureFormat { get; set; } = "F1";
    }

    private static string FormatLocalTime(long tsBinary, string fmt)
    {
        return DateTime.FromBinary(tsBinary).ToLocalTime().ToString(fmt ?? "yyyy-MM-dd HH:mm:ss.fffffff");
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
        var records = ReadValidatedCycleRecords(epbId, cycle);
        WriteAtomically(csvPath, tempPath =>
        {
            using var sw = new StreamWriter(tempPath, false, Encoding.UTF8);
            WriteCsvRecords(sw, records, fmt);
        });
    }

    /// <summary>
    ///     将指定“正式圈”的数据导出为二进制快照（每条 SampleRecord 32B 原样序列化，升序写入）。
    /// </summary>
    public void ExportCycleToBin(int epbId, CycleInfo cycle, string binPath)
    {
        var records = ReadValidatedCycleRecords(epbId, cycle);
        WriteAtomically(binPath, tempPath =>
        {
            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var bw = new BinaryWriter(fs);
            WriteBinRecords(bw, records);
        });
    }

    private void ExportCycleList(int epbId, IEnumerable<CycleInfo> cycles, string exportDir)
    {
        var failures = new List<Exception>();
        foreach (var cy in cycles)
        {
            try
            {
                var csv = Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.csv");
                var bin = Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.bin");
                ExportCyclePair(epbId, cy, csv, bin, new ExportFormatOptions());
            }
            catch (Exception ex)
            {
                var failure = WrapExportFailure(epbId, cy, ex);
                failures.Add(failure);
                WriteExportErrors(exportDir, failure);
            }
        }

        ThrowIfExportFailures(epbId, failures);
    }

    private void ExportCyclePair(
        int epbId,
        CycleInfo cycle,
        string csvPath,
        string binPath,
        ExportFormatOptions fmt)
    {
        var records = ReadValidatedCycleRecords(epbId, cycle);
        var csvTemp = GetTempPath(csvPath);
        var binTemp = GetTempPath(binPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(binPath)) ?? ".");

        try
        {
            using (var sw = new StreamWriter(csvTemp, false, Encoding.UTF8))
                WriteCsvRecords(sw, records, fmt);
            using (var fs = new FileStream(binTemp, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bw = new BinaryWriter(fs))
                WriteBinRecords(bw, records);

            CommitPair(csvTemp, csvPath, binTemp, binPath);
        }
        finally
        {
            TryDeleteFile(csvTemp);
            TryDeleteFile(binTemp);
        }
    }

    private List<SampleRecord> ReadValidatedCycleRecords(int epbId, CycleInfo cycle)
    {
        if (cycle == null) throw new ArgumentNullException(nameof(cycle));
        var s = GetState(epbId);
        InvalidDataException ringFailure;
        lock (s.Gate)
        {
            try
            {
                return ReadCycleRecordsFromRing(epbId, cycle, s.CapacityRecords);
            }
            catch (InvalidDataException ex)
            {
                ringFailure = ex;
            }
        }

        if (TryReadHistoricalBinSnapshot(epbId, cycle, out var recovered, out _))
            return recovered;

        throw new InvalidDataException(
            $"{ringFailure.Message}；未在 {_indexDir} 中找到可恢复的完整 BIN 快照。",
            ringFailure);
    }

    private List<SampleRecord> ReadCycleRecordsFromRing(
        int epbId,
        CycleInfo cycle,
        long capacity)
    {
        if (capacity <= 0)
            throw new InvalidDataException($"EPB[{epbId}] 环形缓冲区容量无效。");
        if (cycle.SampleCount <= 0)
            throw new InvalidDataException(
                $"EPB[{epbId}] Cycle={cycle.CycleNumber} 没有可导出的样本。");
        if (cycle.SampleCount > capacity)
            throw new InvalidDataException(
                $"EPB[{epbId}] Cycle={cycle.CycleNumber} 样本数 {cycle.SampleCount} 超过环形容量 {capacity}，数据已被覆盖。");

        var start = ModNN(cycle.StartRecordIndex, capacity);
        var records = new List<SampleRecord>(cycle.SampleCount);
        for (var i = 0; i < cycle.SampleCount; i++)
        {
            var idx = (start + i) % capacity;
            var rec = ReadRecord(epbId, idx * SampleRecord.Size);
            ValidateCycleRecord(epbId, cycle, i, idx, rec);
            records.Add(rec);
        }

        return records;
    }

    private bool TryReadHistoricalBinSnapshot(
        int epbId,
        CycleInfo cycle,
        out List<SampleRecord> records,
        out string sourcePath)
    {
        records = null;
        sourcePath = null;
        if (!Directory.Exists(_indexDir) || cycle.SampleCount <= 0)
            return false;

        var fileName = $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.bin";
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(_indexDir, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var expectedLength = (long)cycle.SampleCount * SampleRecord.Size;
                var info = new FileInfo(candidate);
                if (info.Length != expectedLength) continue;

                var recovered = new List<SampleRecord>(cycle.SampleCount);
                using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);
                for (var i = 0; i < cycle.SampleCount; i++)
                {
                    var rec = new SampleRecord
                    {
                        TimestampBinary = br.ReadInt64(),
                        CycleNumber = br.ReadInt32(),
                        SampleIndex = br.ReadInt32(),
                        EpbCurrent = br.ReadDouble(),
                        GroupPressure = br.ReadDouble()
                    };
                    ValidateCycleRecord(epbId, cycle, i, i, rec);
                    recovered.Add(rec);
                }

                records = recovered;
                sourcePath = candidate;
                return true;
            }
            catch
            {
                // 当前候选已损坏或属于被覆盖后的错误快照，继续尝试其它历史快照。
            }
        }

        return false;
    }

    private static void ValidateCycleRecord(
        int epbId,
        CycleInfo cycle,
        int expectedSampleIndex,
        long recordIndex,
        SampleRecord actual)
    {
        if (actual.TimestampBinary == 0)
            throw InvalidCycleRecord(epbId, cycle, expectedSampleIndex, recordIndex, actual, "时间戳为空");
        if (actual.CycleNumber != cycle.CycleNumber)
            throw InvalidCycleRecord(epbId, cycle, expectedSampleIndex, recordIndex, actual, "圈号不一致");
        if (actual.SampleIndex != expectedSampleIndex)
            throw InvalidCycleRecord(epbId, cycle, expectedSampleIndex, recordIndex, actual, "样本序号不连续");

        try
        {
            DateTime.FromBinary(actual.TimestampBinary);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"EPB[{epbId}] Cycle={cycle.CycleNumber} Record={recordIndex} 时间戳无效。",
                ex);
        }
    }

    private static InvalidDataException InvalidCycleRecord(
        int epbId,
        CycleInfo cycle,
        int expectedSampleIndex,
        long recordIndex,
        SampleRecord actual,
        string reason)
    {
        return new InvalidDataException(
            $"EPB[{epbId}] Cycle={cycle.CycleNumber} 导出校验失败：{reason}；" +
            $"Record={recordIndex}，ExpectedCycle={cycle.CycleNumber}，ActualCycle={actual.CycleNumber}，" +
            $"ExpectedSampleIndex={expectedSampleIndex}，ActualSampleIndex={actual.SampleIndex}。");
    }

    private static void WriteCsvRecords(
        TextWriter writer,
        IEnumerable<SampleRecord> records,
        ExportFormatOptions fmt)
    {
        writer.WriteLine(CSV_HEADER);
        long? firstTimestampTicks = null;
        var previousRelativeSeconds = 0.0;
        foreach (var rec in records)
        {
            var relativeSeconds = GetNonDecreasingRelativeSeconds(
                rec.TimestampBinary,
                ref firstTimestampTicks,
                ref previousRelativeSeconds);
            var tsText = FormatLocalTime(rec.TimestampBinary, fmt.TimeFormat);
            writer.WriteLine(
                $"{tsText},{relativeSeconds.ToString("F7", CultureInfo.InvariantCulture)},{rec.CycleNumber},{rec.SampleIndex},{rec.EpbCurrent.ToString(fmt.CurrentFormat)},{rec.GroupPressure.ToString(fmt.PressureFormat)}");
        }
    }

    private static double GetNonDecreasingRelativeSeconds(
        long timestampBinary,
        ref long? firstTimestampTicks,
        ref double previousRelativeSeconds)
    {
        var timestamp = DateTime.FromBinary(timestampBinary);
        firstTimestampTicks ??= timestamp.Ticks;
        var relativeSeconds =
            (timestamp.Ticks - firstTimestampTicks.Value) / (double)TimeSpan.TicksPerSecond;
        if (relativeSeconds < previousRelativeSeconds)
            relativeSeconds = previousRelativeSeconds;
        previousRelativeSeconds = relativeSeconds;
        return relativeSeconds;
    }

    private static void WriteBinRecords(BinaryWriter writer, IEnumerable<SampleRecord> records)
    {
        foreach (var rec in records)
        {
            writer.Write(rec.TimestampBinary);
            writer.Write(rec.CycleNumber);
            writer.Write(rec.SampleIndex);
            writer.Write(rec.EpbCurrent);
            writer.Write(rec.GroupPressure);
        }
    }

    private static void WriteAtomically(string targetPath, Action<string> writeTemp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".");
        var tempPath = GetTempPath(targetPath);
        try
        {
            writeTemp(tempPath);
            CommitSingle(tempPath, targetPath);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static string GetTempPath(string targetPath)
    {
        return targetPath + ".tmp." + Guid.NewGuid().ToString("N");
    }

    private static void CommitSingle(string tempPath, string targetPath)
    {
        var backupPath = targetPath + ".bak." + Guid.NewGuid().ToString("N");
        var hadTarget = File.Exists(targetPath);
        var backedUp = false;
        var committed = false;
        var succeeded = false;
        try
        {
            if (hadTarget)
            {
                File.Move(targetPath, backupPath);
                backedUp = true;
            }

            File.Move(tempPath, targetPath);
            committed = true;
            succeeded = true;
        }
        catch
        {
            if (committed) TryDeleteFile(targetPath);
            if (backedUp && File.Exists(backupPath) && !File.Exists(targetPath))
                File.Move(backupPath, targetPath);
            throw;
        }
        finally
        {
            if (succeeded) TryDeleteFile(backupPath);
        }
    }

    private static void CommitPair(
        string csvTemp,
        string csvPath,
        string binTemp,
        string binPath)
    {
        if (File.Exists(csvPath) && File.Exists(binPath) &&
            FilesEqual(csvTemp, csvPath) && FilesEqual(binTemp, binPath))
        {
            TryDeleteFile(csvTemp);
            TryDeleteFile(binTemp);
            return;
        }
        var conflictSuffix = $".conflict.{DateTime.Now:yyyyMMdd_HHmmss_fff}.{Guid.NewGuid():N}";
        var csvBackup = csvPath + conflictSuffix;
        var binBackup = binPath + conflictSuffix;
        var hadCsv = File.Exists(csvPath);
        var hadBin = File.Exists(binPath);
        var csvBackedUp = false;
        var binBackedUp = false;
        var csvCommitted = false;
        var binCommitted = false;
        try
        {
            if (hadCsv)
            {
                File.Move(csvPath, csvBackup);
                csvBackedUp = true;
            }

            if (hadBin)
            {
                File.Move(binPath, binBackup);
                binBackedUp = true;
            }

            File.Move(csvTemp, csvPath);
            csvCommitted = true;
            File.Move(binTemp, binPath);
            binCommitted = true;
        }
        catch
        {
            if (csvCommitted) TryDeleteFile(csvPath);
            if (binCommitted) TryDeleteFile(binPath);
            if (csvBackedUp && File.Exists(csvBackup) && !File.Exists(csvPath))
                File.Move(csvBackup, csvPath);
            if (binBackedUp && File.Exists(binBackup) && !File.Exists(binPath))
                File.Move(binBackup, binPath);
            throw;
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length) return false;
        const int bufferSize = 64 * 1024;
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        while (true)
        {
            var leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            for (var i = 0; i < leftRead; i++)
                if (leftBuffer[i] != rightBuffer[i]) return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理临时文件失败不覆盖原始导出异常。
        }
    }

    private static Exception WrapExportFailure(int epbId, CycleInfo cycle, Exception ex)
    {
        return new InvalidDataException(
            $"EPB[{epbId}] Cycle={cycle.CycleNumber} 导出失败：{ex.Message}",
            ex);
    }

    private static void WriteExportErrors(string exportDir, Exception failure)
    {
        try
        {
            Directory.CreateDirectory(exportDir);
            File.AppendAllText(
                Path.Combine(exportDir, "export_errors.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {failure.Message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 错误清单写入失败时仍通过 AggregateException 通知调用方。
        }
    }

    private static void ThrowIfExportFailures(int epbId, List<Exception> failures)
    {
        if (failures.Count > 0)
            throw new AggregateException(
                $"EPB[{epbId}] 有 {failures.Count} 圈数据未通过导出校验，其他有效圈已继续导出。",
                failures);
    }

    /// <summary>从二进制快照（ExportCycleToBin 生成）导出 CSV（升序输出）。</summary>
    public void ImportBinToCsv(string binPath, string csvPath, ExportFormatOptions fmt = null)
    {
        fmt ??= new ExportFormatOptions();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        using var sw = new StreamWriter(csvPath, false, Encoding.UTF8);

        sw.WriteLine(CSV_HEADER);
        long? firstTimestampTicks = null;
        var previousRelativeSeconds = 0.0;

        while (fs.Position + SampleRecord.Size <= fs.Length)
        {
            var tsBin = br.ReadInt64();
            var cyc = br.ReadInt32();
            var idx = br.ReadInt32();
            var cur = br.ReadDouble();
            var pr = br.ReadDouble();

            var relativeSeconds = GetNonDecreasingRelativeSeconds(
                tsBin,
                ref firstTimestampTicks,
                ref previousRelativeSeconds);
            var tsText = FormatLocalTime(tsBin, fmt.TimeFormat);
            sw.WriteLine(
                $"{tsText},{relativeSeconds.ToString("F6", CultureInfo.InvariantCulture)},{cyc},{idx},{cur.ToString(fmt.CurrentFormat)},{pr.ToString(fmt.PressureFormat)}");
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
            sw0.WriteLine(CSV_HEADER);
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
        sw.WriteLine(CSV_HEADER);
        long? firstTimestampTicks = null;
        var previousRelativeSeconds = 0.0;
        foreach (var rec in list)
        {
            var relativeSeconds = GetNonDecreasingRelativeSeconds(
                rec.TimestampBinary,
                ref firstTimestampTicks,
                ref previousRelativeSeconds);
            var tsText = FormatLocalTime(rec.TimestampBinary, fmt.TimeFormat);
            sw.WriteLine(
                $"{tsText},{relativeSeconds.ToString("F6", CultureInfo.InvariantCulture)},{rec.CycleNumber},{rec.SampleIndex},{rec.EpbCurrent.ToString(fmt.CurrentFormat)},{rec.GroupPressure.ToString(fmt.PressureFormat)}");
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
    ///     查询指定 EPB 通道正常完成的累计圈数。
    ///     该方法保留给旧界面的启动回填逻辑使用；报警、失败、取消圈均不计入 RunCount。
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <returns>正常完成的累计圈数。</returns>
    public int GetLastCycleNumber(int epbId)
    {
        return GetClosedCycleCount(epbId);
    }

    /// <summary>
    ///     查询当前已存在的最大正式数据圈号，仅用于生成不重复的下一圈数据序号。
    ///     后续在 EpbManager 里开启新试验时，可以（举例）：
    ///     // 每个通道单独算一个“起始圈号基准”
    ///     var last = _diskWriter.GetMaxCycleNumber(ch);
    ///     var baseCycle = last;         // 这次试验第1圈就是 baseCycle + 1
    ///     然后把 runner 的 _sessionRunCount 写成 baseCycle + n，再传给 BeginCycle/CompleteCycle
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <returns>若无正式圈记录，则返回 0。</returns>
    public int GetMaxCycleNumber(int epbId)
    {
        lock (_dbGate)
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
    }

    /// <summary>返回下一个负数学习圈号；调用方必须持有对应通道锁。</summary>
    private int GetNextLearningCycleNumber(int epbId)
    {
        lock (_dbGate)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT COALESCE(MIN(cycle_number), 0)
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number < 0";
        cmd.Parameters.AddWithValue("@e", epbId);
        var currentMinimum = Convert.ToInt32(cmd.ExecuteScalar());
        if (currentMinimum == int.MinValue)
            throw new InvalidOperationException($"EPB[{epbId}] 学习圈号已耗尽。");
        return currentMinimum < 0 ? currentMinimum - 1 : -1;
        }
    }



        /// <summary>
        ///     查询指定 EPB 通道正常完成的累计圈次数。
        /// </summary>
        /// <param name="epbId">EPB 通道号（1..12）。</param>
        /// <returns>
        ///     成功圈次数（仅统计 CycleNumber &gt; 0 且 <c>status='completed'</c> 的记录）。
        /// </returns>
        /// <remarks>
        ///     <para>
        ///     口径说明：
        ///     <list type="bullet">
        ///         <item>
        ///             <description>
        ///             <c>completed</c>：正常封圈；
        ///             </description>
        ///         </item>
        ///         <item>
        ///             <description>
        ///             <c>alarm</c>：报警触发导致该圈中断，只保留故障证据，不计入成功圈；
        ///             </description>
        ///         </item>
        ///         <item>
        ///             <description>
        ///             <c>running</c>：已 BeginCycle 但尚未封圈，不应计入累计圈次数。
        ///             </description>
        ///         </item>
        ///     </list>
        ///     </para>
        ///     <para>
        ///     典型用途：软件启动加载试验时，用 DB 回填 <c>EpbTestRecord.RunCount</c>，
        ///     将 RunCount 的权威口径固定为：
        ///     <c>COUNT(status='completed')</c>。
        ///     </para>
        /// </remarks>
        public int GetClosedCycleCount(int epbId)
        {
            lock (_dbGate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $@"
SELECT COUNT(1)
    FROM {TABLE_CYCLES}
 WHERE epb_id=@e
     AND cycle_number > 0
     AND status='completed'";
                cmd.Parameters.AddWithValue("@e", epbId);
                var obj = cmd.ExecuteScalar();
                return Convert.ToInt32(obj);
            }
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

    private void WriteRecordBatch(int epbId, EpbState state, SampleRecord[] records, int count)
    {
        var startIndex = state.TotalWritten % state.CapacityRecords;
        var firstCount = (int)Math.Min(count, state.CapacityRecords - startIndex);
        WriteRecordSegment(epbId, startIndex, records, 0, firstCount);
        if (firstCount < count)
            WriteRecordSegment(epbId, 0, records, firstCount, count - firstCount);
    }

    private void WriteRecordSegment(
        int epbId,
        long recordIndex,
        SampleRecord[] records,
        int sourceIndex,
        int count)
    {
        if (count <= 0) return;
        var fileOffset = recordIndex * SampleRecord.Size;
        var bytes = (long)count * SampleRecord.Size;
        EnsureViewCovers(epbId, fileOffset, bytes);
        var viewOffset = fileOffset - _viewBaseOffsets[epbId];
        _views[epbId].WriteArray(viewOffset, records, sourceIndex, count);
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
    private long RestoreNextWritePosition(int epbId, long capacityRecords)
    {
        if (capacityRecords <= 0) return 0;
        lock (_dbGate)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT start_position, COALESCE(sample_count, 0)
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
 ORDER BY id DESC
 LIMIT 1";
        cmd.Parameters.AddWithValue("@e", epbId);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return 0;

        var startPosition = rd.GetInt64(0);
        var sampleCount = Math.Max(0L, Convert.ToInt64(rd.GetValue(1)));
        var logicalEnd = startPosition + sampleCount;

        // TotalWritten 保留“至少已写过这些数据”的语义；真正访问环形文件时统一取模。
        // 不能直接只保存余数，否则刚好写满一圈时余数为 0，会被 Free-Run 误判为从未写入。
        return logicalEnd >= 0 ? logicalEnd : ModNN(logicalEnd, capacityRecords);
        }
    }

    private void InitSchema()
    {
        lock (_dbGate)
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
    }

    /// <summary>
    ///     写入某圈的起始信息。调用方需保证同一 epbId 上的 cycleNumber 全局唯一（不复用旧圈号）。<br/>
    ///     若传入已存在的 circleNumber，将抛出约束异常（用于提示上层逻辑错误）。
    /// </summary>
    private void UpsertCycleStart(int epbId, int cycleNumber, DateTime startUtc, long startRecordIndex)
    {
        lock (_dbGate)
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
    }

    private void UpdateCycleProgress(int epbId, int cycleNumber, int sampleCount, DateTime lastUtc)
    {
        ExecuteCycleUpdate(epbId, cycleNumber, sampleCount, lastUtc, "running");
    }

    private void MarkCycleCompleted(int epbId, int cycleNumber, int finalSampleCount, DateTime endUtc)
    {
        ExecuteCycleUpdate(epbId, cycleNumber, finalSampleCount, endUtc, "completed");
    }

    private void MarkCycleAlarm(int epbId, int cycleNumber, int finalSampleCount, DateTime endUtc)
    {
        ExecuteCycleUpdate(epbId, cycleNumber, finalSampleCount, endUtc, "alarm");
    }

    private void ExecuteCycleUpdate(
        int epbId,
        int cycleNumber,
        int sampleCount,
        DateTime endUtc,
        string status)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeBatchTransaction;
            cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET sample_count=@n, end_time=@et, status=@status
 WHERE epb_id=@e AND cycle_number=@c";
            cmd.Parameters.AddWithValue("@n", sampleCount);
            cmd.Parameters.AddWithValue("@et", endUtc.ToLocalTime().ToString("o"));
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@e", epbId);
            cmd.Parameters.AddWithValue("@c", cycleNumber);
            cmd.ExecuteNonQuery();
        }
    }

    private void MarkCycleAborted(
        int epbId,
        int cycleNumber,
        int finalSampleCount,
        DateTime endUtc,
        string status)
    {
        ExecuteCycleUpdate(epbId, cycleNumber, finalSampleCount, endUtc, status);
    }

    private void MarkCycleFinalized(
        int epbId,
        int cycleNumber,
        int finalSampleCount,
        DateTime endUtc,
        string status)
    {
        ExecuteCycleUpdate(epbId, cycleNumber, finalSampleCount, endUtc, status);
    }

    private List<CycleInfo> GetCyclesToPurge(int epbId, int keepLatestN)
    {
        lock (_dbGate)
        {
        var list = new List<CycleInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
WITH nth AS (
  SELECT cycle_number FROM {TABLE_CYCLES}
   WHERE epb_id=@e
     AND cycle_number > 0
   ORDER BY cycle_number DESC
   LIMIT 1 OFFSET @off
)
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number > 0
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
    }

    /// <summary>
    ///     查询某 EPB 通道“最新 N 圈”的圈信息（只取已完成圈）。
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）</param>
    /// <param name="latestN">需要的圈数（取最近的 N 圈）</param>
    /// <returns>按圈号升序排列的圈信息列表。</returns>
    private CycleInfo GetCycleInfo(int epbId, int cycleNumber)
    {
        lock (_dbGate)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e AND cycle_number=@c
 LIMIT 1";
        cmd.Parameters.AddWithValue("@e", epbId);
        cmd.Parameters.AddWithValue("@c", cycleNumber);
        using var rd = cmd.ExecuteReader();
        if (!rd.Read())
            throw new InvalidDataException($"EPB[{epbId}] Cycle={cycleNumber} 索引不存在。");
        return new CycleInfo
        {
            EpbId = rd.GetInt32(0),
            CycleNumber = rd.GetInt32(1),
            StartTimeUtc = DateTime.Parse(rd.GetString(2)),
            EndTimeUtc = rd.IsDBNull(3) ? (DateTime?)null : DateTime.Parse(rd.GetString(3)),
            StartRecordIndex = rd.GetInt64(4),
            SampleCount = rd.GetInt32(5),
            Status = rd.GetString(6)
        };
        }
    }

    private List<CycleInfo> GetLatestCycles(int epbId, int latestN, bool includeRunningCycle)
    {
        lock (_dbGate)
        {
        latestN = Math.Max(1, latestN);

        var list = new List<CycleInfo>();
        using var cmd = _conn.CreateCommand();
        var statusFilter = includeRunningCycle
            ? "AND status IN ('completed','alarm','running')"
            : "AND status IN ('completed','alarm')";
        cmd.CommandText = $@"
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number > 0
     {statusFilter}
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
    }


    private void DeleteCycles(IEnumerable<CycleInfo> cycles)
    {
        lock (_dbGate)
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

/// <summary>报警圈原子封存和文件校验结果。</summary>
public sealed class AlarmCycleSnapshotEvidence
{
    /// <summary>本调用是否取得当前圈的唯一封存权。</summary>
    public bool WasClaimed { get; set; }
    public bool IsValid { get; set; }
    public int SampleCount { get; set; }
    public DateTime? FirstSampleUtc { get; set; }
    public DateTime? LastSampleUtc { get; set; }
    public string CsvPath { get; set; }
    public string BinPath { get; set; }
    public string FinalStatus { get; set; }
    public string ValidationError { get; set; }
}

#region 圈记录器接口与适配器

/// <summary>
///     供 EpbManager 调用的“圈级记录器”接口；
///     由 EpbDiskWriter 实现（或用适配器包装后实现）。
/// </summary>
public interface IEpbCycleRecorder
{
    /// <summary>标记某 EPB 在某圈开始；若未调用过，将使用圈号0做“常开记录”</summary>
    void BeginCycle(int epbId, int cycleNumber, DateTime utcNow);

    /// <summary>原子分配负数内部圈号并开始学习圈。</summary>
    int BeginLearningCycle(int epbId, DateTime utcNow);

    /// <summary>圈内批量写入：同批次时间戳、对应电流数组、对应组压数组</summary>
    void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures);

    /// <summary>获取当前圈已累计样本数（用于记账/日志/封圈）</summary>
    int GetCurrentCycleSampleCount(int epbId);

    /// <summary>圈结束（落盘结账）。finalN 可传入 GetCurrentCycleSampleCount 返回值</summary>
    void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow);

    /// <summary>
    ///     将当前圈以“报警中断”封圈（写入 <c>status='alarm'</c>）。
    ///     <para>
    ///     该方法用于保证“报警停机圈次也计数且可导出”，同时避免遗留 <c>status='running'</c> 悬挂圈导致计数漂移。
    ///     </para>
    /// </summary>
    /// <param name="epbId">EPB 通道号（1..12）。</param>
    /// <param name="cycleNumber">圈号（与 BeginCycle 使用的圈号一致）。</param>
    /// <param name="finalN">截至报警发生时的样本数。</param>
    /// <param name="utcNow">报警发生时间（UTC）。</param>
    void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow);

    /// <summary>原子冻结、导出、校验并封存当前报警圈。</summary>
    AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc);

    /// <summary>原子领取、导出并按指定终态封存当前圈。</summary>
    AlarmCycleSnapshotEvidence SealAndExportCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc,
        string status);

    /// <summary>将取消/失败的半圈封账，但不计为成功圈。</summary>
    void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status);

    /// <summary>
    ///     在停止卡钳时，将该通道“最新 N 圈”正式数据（Cycle&gt;0）落盘；
    ///     Free-Run（Cycle=0）不在此范围，若需要可单独调用导出 API。
    /// </summary>
    void FlushRecent(int epbId, int lastNCycles);


    int GetLastCycleNumber(int ch);

    /// <summary>
    ///     导出“最近 N 圈”到指定目录（CSV + BIN），不删除索引。
    ///     includeRunningCycle=true 时会包含当前 status='running' 的圈（用于报警快照）。
    /// </summary>
    void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle);
}

/// <summary>支持池化缓冲区长度和周期时间窗的可选扩展。</summary>
public interface IBatchedEpbCycleRecorder
{
    void WriteBatch(
        int epbId,
        DateTime[] timestampsUtc,
        double[] currents,
        double[] pressures,
        int count);

    void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        IReadOnlyList<EpbChannelDiskBatch> channels,
        int count);

    void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc);
}

/// <summary>
/// Optional zero-allocation extension used by the DAQ persistence worker. The array may be
/// rented and can be longer than <c>channelCount</c>; consumers must honor the count.
/// </summary>
public interface ICountedBatchedEpbCycleRecorder : IBatchedEpbCycleRecorder
{
    void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount);
}

public struct EpbChannelDiskBatch
{
    public EpbChannelDiskBatch(int epbId, double[] currents, double[] pressures)
    {
        EpbId = epbId;
        Currents = currents;
        Pressures = pressures;
    }

    public int EpbId { get; set; }
    public double[] Currents { get; set; }
    public double[] Pressures { get; set; }
}

public sealed class CycleSnapshotEvidence
{
    public int EpbId { get; set; }
    public int CycleNumber { get; set; }
    public DateTime FirstSampleUtc { get; set; }
    public DateTime LastSampleUtc { get; set; }
    public int SampleCount { get; set; }
    public bool IsCompleteCycle { get; set; }
    public string CsvPath { get; set; }
    public string BinPath { get; set; }
}

public interface ICycleEvidenceExporter
{
    CycleSnapshotEvidence ExportCompletedCycleTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin);
}

public interface IActiveCycleLimitConfigurator
{
    void SetMaxActiveCycleRecords(int maxRecords);
}

/// <summary>
///     将 EpbDiskWriter 适配为 IEpbCycleRecorder，避免 EpbManager 直接依赖具体类。
/// </summary>
public sealed class DiskWriterRecorderAdapter : IEpbCycleRecorder, ICountedBatchedEpbCycleRecorder, ICycleEvidenceExporter, IActiveCycleLimitConfigurator
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

    public int BeginLearningCycle(int epbId, DateTime startUtc)
    {
        return _writer.BeginLearningCycle(epbId, startUtc);
    }

    public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures)
    {
        _writer.WriteBatch(epbId, tsUtc, currents, groupPressures);
    }

    public void WriteBatch(
        int epbId,
        DateTime[] timestampsUtc,
        double[] currents,
        double[] pressures,
        int count)
        => _writer.WriteBatch(epbId, timestampsUtc, currents, pressures, count);

    public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc)
        => _writer.SealCycleWindow(epbId, cycleNumber, endUtc);

    public void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        IReadOnlyList<EpbChannelDiskBatch> channels,
        int count)
        => _writer.WriteDeviceBatch(timestampsUtc, channels, count);

    public void WriteDeviceBatch(
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount)
        => _writer.WriteDeviceBatch(timestampsUtc, channels, channelCount, sampleCount);

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

    public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle)
        => _writer.ExportLatestCyclesTo(epbId, Math.Max(1, lastNCycles), exportDir, includeRunningCycle);

    public CycleSnapshotEvidence ExportCompletedCycleTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin)
        => _writer.ExportCompletedCycleTo(epbId, cycleNumber, exportDir, saveCsv, saveBin);

    public void SetMaxActiveCycleRecords(int maxRecords)
        => _writer.SetMaxActiveCycleRecords(maxRecords);

    /// <summary>
    ///  查询指定 EPB 通道当前已存在的“最大正式圈号”（cycle_number），仅统计 CycleNumber &gt; 0。
    /// </summary>
    /// <param name="ch"></param>
    /// <returns></returns>
    public int GetLastCycleNumber(int ch)
    {
        return _writer.GetMaxCycleNumber(ch);
    }


    public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime endUtc)
    {
        _writer.CompleteCycle(epbId, cycleNumber, finalN, endUtc);
    }

    public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow)
    {
        _writer.AlarmCycle(epbId, cycleNumber, finalN, utcNow);
    }

    public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc)
    {
        return _writer.SealAndExportAlarmCycle(
            epbId,
            cycleNumber,
            exportDir,
            fallbackEndUtc);
    }

    public AlarmCycleSnapshotEvidence SealAndExportCycle(
        int epbId,
        int cycleNumber,
        string exportDir,
        DateTime fallbackEndUtc,
        string status)
    {
        return _writer.SealAndExportCycle(
            epbId,
            cycleNumber,
            exportDir,
            fallbackEndUtc,
            status);
    }

    public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status)
    {
        _writer.AbortCycle(epbId, cycleNumber, finalN, utcNow, status);
    }
}

#endregion
