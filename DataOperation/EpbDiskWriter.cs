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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataOperation;

#region 策略/结构/枚举

/// <summary>
///     落盘策略配置（可从 TestConfig.xml 读取，也可直接 new 指定）
/// </summary>
public sealed class DataRetentionPolicy
{
    /// <summary>Latest 导出格式。直接 new 策略时保留旧双格式兼容；EXE 配置解析默认为 CsvOnly。</summary>
    public StorageFormatLevel LatestStorageLevel { get; set; } = StorageFormatLevel.CsvAndBin;

    /// <summary>硬报警圈证据格式。直接 new 策略时保留旧双格式兼容；EXE 配置解析默认为 CsvOnly。</summary>
    public StorageFormatLevel AlarmStorageLevel { get; set; } = StorageFormatLevel.CsvAndBin;

    /// <summary>学习/资格圈封存格式。直接 new 策略时保留旧双格式兼容；EXE 配置解析结果默认为 BinOnly。</summary>
    public StorageFormatLevel LearningStorageLevel { get; set; } = StorageFormatLevel.CsvAndBin;

    /// <summary>历史滚动快照开关及每通道保留上限（由上层配置解析后传入）。</summary>
    public bool HistoricalEnabled { get; set; } = true;
    public int HistoricalRetainCyclesPerChannel { get; set; } = 12;

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

    /// <summary>每通道 Latest 停止包上限；只影响导出副本，不改变圈索引。</summary>
    public int RetainLatestStopPackagesPerChannel { get; set; } = 10;

    /// <summary>true 时不清理任何 Latest 停止包。</summary>
    public bool RetainAllLatestStopPackages { get; set; }

    /// <summary>容量清理警告出口；调用失败绝不能抛回控制链路。</summary>
    public Action<string> RetentionWarningSink { get; set; }
}

public sealed class ActiveCycleDataLimitExceededException : InvalidOperationException
{
    public ActiveCycleDataLimitExceededException(int epbId, int cycleNumber, int limit)
        : base($"ActiveCycleDataLimitExceeded EPB={epbId} Cycle={cycleNumber} Limit={limit}")
    {
        EpbId = epbId;
        CycleNumber = cycleNumber;
        Limit = limit;
    }

    public int EpbId { get; }
    public int CycleNumber { get; }
    public int Limit { get; }
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
        public readonly Queue<PreTriggerSample> PreTriggerSamples = new();
        public long CapacityRecords; // 文件可容纳记录数
        public int? CurrentCycle; // 正式圈号（null=未开圈）
        public int CurrentSampleIndex; // 当前圈内样本序号（0..）
        public DateTime CurrentCycleStartUtc;
        public DateTime? CurrentCycleEndUtc;
        public bool SequenceBoundaryEnabled;
        public string CurrentCycleDevice = string.Empty;
        public long CurrentCycleGeneration;
        public long CurrentCycleStartAfterSequence;
        public long CurrentCycleLastSequence;
        public long? CurrentCycleEndSequence;
        public int CurrentCyclePreTriggerSamples;
        public DateTime LastProgressCheckpointUtc = DateTime.MinValue;
        public bool ActiveCycleLimitLatched;
        public bool FreeRunOn; // 是否开启 Free-Run
        public int FreeRunSampleIndex; // Free-Run 下的“伪圈”样本序号
        public string StopAction = "archive";

        public int StopAfterK; // 仅当 StopTrigger=AfterKMoreCycles
        public int StopKeepLatestN = 10; // 停止时保留的最新圈数
        public StopTrigger StopTrigger = StopTrigger.EndOfCurrentCycle;
        public long TotalWritten; // 已写入总条数（单调递增）
    }

    private readonly struct PreTriggerSample
    {
        public PreTriggerSample(
            string device,
            long generation,
            long sequence,
            DateTime timestampUtc,
            double current,
            double pressure)
        {
            Device = device ?? string.Empty;
            Generation = generation;
            Sequence = sequence;
            TimestampBinary = timestampUtc.ToLocalTime().ToBinary();
            Current = current;
            Pressure = pressure;
        }

        public string Device { get; }
        public long Generation { get; }
        public long Sequence { get; }
        public long TimestampBinary { get; }
        public double Current { get; }
        public double Pressure { get; }
    }

    private struct StateWriteSnapshot
    {
        public long TotalWritten;
        public int CurrentSampleIndex;
        public int FreeRunSampleIndex;
        public DateTime LastProgressCheckpointUtc;
        public long CurrentCycleLastSequence;
        public int CurrentCyclePreTriggerSamples;
    }

    // 2 kHz 下保留 250 ms。缓存只在未开圈时更新，正式圈开始后原子写入圈头。
    private const int PRE_TRIGGER_SAMPLE_CAPACITY = 500;
    // 语义完整至少要求 200 ms 未上电证据；2 kHz 下为 400 点。
    private const int MINIMUM_PRE_TRIGGER_SAMPLE_COUNT = 400;

    #endregion

    #region 字段

    private readonly string _rootDir;
    private readonly string _indexDir;  // 索引与导出用的根目录（index.db、Archive、Latest）
    private readonly string _mappingScope;
    private readonly long _fileBytes;
    private readonly DataRetentionPolicy _policy;

    public StorageFormatLevel AlarmStorageLevel =>
        NormalizeStorageLevel(_policy.AlarmStorageLevel, StorageFormatLevel.CsvAndBin);

    private readonly MemoryMappedFile[] _mmfs = new MemoryMappedFile[EPB_COUNT + 1]; // 1..12
    private readonly MemoryMappedViewAccessor[] _views = new MemoryMappedViewAccessor[EPB_COUNT + 1];
    private readonly EpbState[] _states = new EpbState[EPB_COUNT + 1];

    // —— 窗口化映射新增：记录每个通道当前视图的“文件基址/长度”（单位：字节） —— //
    private readonly long[] _viewBaseOffsets = new long[EPB_COUNT + 1];
    private readonly long[] _viewLengths = new long[EPB_COUNT + 1];

    // x86 进程的虚拟地址空间很有限：禁止为 12 通道预先常驻 64MB 视图。
    // 8MB 足以覆盖多个 2kHz/15s 圈，并大幅降低重映射时的地址空间压力。
    private const long VIEW_BYTES = 8L * 1024 * 1024;
    private const long VIEW_ALIGN = 64L * 1024; // 64KB（Windows allocation granularity）

    private readonly SQLiteConnection _conn;
    private readonly object _dbGate = new();
    private SQLiteTransaction _activeBatchTransaction;
    private long _progressCheckpointTransactionCount;
    private readonly object[] _latestExportGates = Enumerable.Range(0, EPB_COUNT + 1)
        .Select(_ => new object())
        .ToArray();
    private long _latestExportSequence;
    private readonly int[] _latestCleanupRequested = new int[EPB_COUNT + 1];
    private readonly int[] _latestCleanupRunning = new int[EPB_COUNT + 1];
    private readonly Thread[] _latestCleanupThreads = new Thread[EPB_COUNT + 1];
    private readonly ConcurrentDictionary<string, object> _exportTargetGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LatestPackageNamePattern = new(
        @"^\d{8}_\d{6}_\d{3}-\d{6}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private int _disposed;

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
        _mappingScope = BuildMappingScope(_rootDir);

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
        _conn = new SQLiteConnection(
            $"Data Source={dbPath};Pooling=True;Journal Mode=WAL;Synchronous=Normal");
        _conn.Open();
        InitSchema();
        RecoverInterruptedCyclesOnStartup();

        // 12 路映射 + 状态
        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            _states[ch] = new EpbState();

            var path = GetDatPath(ch);
            EnsureFixedSizeFile(path, _fileBytes);

            // 保留同一数据根目录的跨进程互斥，同时避免不同项目/测试目录共享
            // EPB1_MMF...EPB12_MMF 而互相阻塞。
            _mmfs[ch] = MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                $"EPB_{_mappingScope}_{ch}_MMF",
                _fileBytes);

            // 视图延迟到通道第一次真正读/写时才创建。未启用通道不占用视图地址空间。
            _views[ch] = null;
            _viewBaseOffsets[ch] = 0;
            _viewLengths[ch] = 0;

            _states[ch].CapacityRecords = _fileBytes / SampleRecord.Size;
            _states[ch].TotalWritten = RestoreNextWritePosition(
                ch,
                _states[ch].CapacityRecords);
        }
    }

    private static string BuildMappingScope(string rootDirectory)
    {
        var normalized = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        if (normalized.EndsWith(":", StringComparison.Ordinal))
            normalized += Path.DirectorySeparatorChar;

        // 稳定 FNV-1a 64-bit：相同目录跨进程得到相同名字，不依赖运行时随机哈希。
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        unchecked
        {
            foreach (var character in normalized)
            {
                hash ^= (byte)character;
                hash *= prime;
                hash ^= (byte)(character >> 8);
                hash *= prime;
            }
        }

        return hash.ToString("X16", CultureInfo.InvariantCulture);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Latest保留清理由专用线程执行；关闭前停止接收并观察线程终态，
        // 禁止关闭SQLite/MMF后仍有裸后台任务访问同一项目目录。
        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            Interlocked.Exchange(ref _latestCleanupRequested[ch], 0);
            var cleanupThread = Volatile.Read(ref _latestCleanupThreads[ch]);
            if (cleanupThread == null || cleanupThread == Thread.CurrentThread) continue;
            try
            {
                if (!cleanupThread.Join(5000))
                    WarnRetention($"Latest EPB{ch} 清理线程在关闭门禁5秒内未退出。LatestCleanupDrainTimeout=1");
            }
            catch (Exception ex)
            {
                WarnRetention($"Latest EPB{ch} 清理线程关闭异常：{ex.Message}");
            }
        }

        // 1) 关闭 12 路内存映射视图和文件
        for (var ch = 1; ch <= EPB_COUNT; ch++)
        {
            var state = _states[ch];
            lock (state.Gate)
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
                _viewBaseOffsets[ch] = 0;
                _viewLengths[ch] = 0;
            }
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
            ResetSequenceBoundary(s);
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;
        }
    }

    /// <summary>
    /// 使用 DAQ 代次/批次序号原子开始正式圈。startAfterSequence 及以前的未上电缓存
    /// 写入圈头；后续圈归属只按同一 generation 的 sequence 判断，不再比较墙钟 UTC。
    /// </summary>
    public void BeginCycleAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime startUtc,
        string device,
        long generation,
        long startAfterSequence)
    {
        if (string.IsNullOrWhiteSpace(device))
            throw new ArgumentException("device is required", nameof(device));
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (startAfterSequence < 0) throw new ArgumentOutOfRangeException(nameof(startAfterSequence));

        var s = GetState(epbId);
        lock (s.Gate)
        {
            if (s.CapacityRecords <= 0)
                throw new InvalidOperationException("CapacityRecords must be positive.");
            if (s.CurrentCycle.HasValue)
                throw new InvalidOperationException(
                    $"EPB[{epbId}] 圈 {s.CurrentCycle.Value} 尚未封存，不能开始圈 {cycleNumber}。");

            var matching = s.PreTriggerSamples
                .Where(sample =>
                    sample.Generation == generation &&
                    string.Equals(sample.Device, device, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var effectiveBoundary = matching.Length == 0
                ? startAfterSequence
                : Math.Max(startAfterSequence, matching[matching.Length - 1].Sequence);
            var startIndex = s.TotalWritten % s.CapacityRecords;
            UpsertCycleStart(epbId, cycleNumber, startUtc.ToLocalTime(), startIndex);

            s.CurrentCycle = cycleNumber;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleStartUtc = startUtc.ToUniversalTime();
            s.CurrentCycleEndUtc = null;
            s.SequenceBoundaryEnabled = true;
            s.CurrentCycleDevice = device;
            s.CurrentCycleGeneration = generation;
            s.CurrentCycleStartAfterSequence = effectiveBoundary;
            s.CurrentCycleLastSequence = 0;
            s.CurrentCycleEndSequence = null;
            s.CurrentCyclePreTriggerSamples = 0;
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;

            if (matching.Length > 0)
            {
                var records = ArrayPool<SampleRecord>.Shared.Rent(matching.Length);
                try
                {
                    for (var i = 0; i < matching.Length; i++)
                    {
                        records[i] = new SampleRecord
                        {
                            TimestampBinary = matching[i].TimestampBinary,
                            CycleNumber = cycleNumber,
                            SampleIndex = i,
                            EpbCurrent = matching[i].Current,
                            GroupPressure = matching[i].Pressure
                        };
                    }
                    WriteRecordBatch(epbId, s, records, matching.Length);
                    s.TotalWritten += matching.Length;
                    s.CurrentSampleIndex = matching.Length;
                    s.CurrentCyclePreTriggerSamples = matching.Length;
                    s.CurrentCycleLastSequence = matching[matching.Length - 1].Sequence;
                }
                finally
                {
                    ArrayPool<SampleRecord>.Shared.Return(records, clearArray: false);
                }
            }
            s.PreTriggerSamples.Clear();
        }
    }

    private static void ResetSequenceBoundary(EpbState state)
    {
        state.SequenceBoundaryEnabled = false;
        state.CurrentCycleDevice = string.Empty;
        state.CurrentCycleGeneration = 0;
        state.CurrentCycleStartAfterSequence = 0;
        state.CurrentCycleLastSequence = 0;
        state.CurrentCycleEndSequence = null;
        state.CurrentCyclePreTriggerSamples = 0;
    }

    private static bool ValidateSequencedCycleSemantics(
        EpbState state,
        out string validationError)
    {
        validationError = string.Empty;
        if (!state.SequenceBoundaryEnabled) return true;
        if (state.CurrentCyclePreTriggerSamples < MINIMUM_PRE_TRIGGER_SAMPLE_COUNT)
        {
            validationError =
                $"未上电预触发样本不足：Actual={state.CurrentCyclePreTriggerSamples} " +
                $"Required={MINIMUM_PRE_TRIGGER_SAMPLE_COUNT}。";
            return false;
        }
        if (!state.CurrentCycleEndSequence.HasValue)
        {
            validationError = "缺少权威DAQ圈尾序号。";
            return false;
        }
        if (state.CurrentCycleEndSequence.Value < state.CurrentCycleStartAfterSequence)
        {
            validationError =
                $"DAQ圈尾早于圈头：StartAfter={state.CurrentCycleStartAfterSequence} " +
                $"End={state.CurrentCycleEndSequence.Value}。";
            return false;
        }
        if (state.CurrentCycleLastSequence < state.CurrentCycleEndSequence.Value)
        {
            validationError =
                $"已接纳圈尾尚未完整写入：LastWritten={state.CurrentCycleLastSequence} " +
                $"RequiredEnd={state.CurrentCycleEndSequence.Value}。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 释放所有通道的短视图，保留 MMF 和圈索引；下一批数据将按需重建视图。
    /// 该操作只对地址空间/访问器生命周期故障开放，真实 I/O 故障仍走安全暂停。
    /// </summary>
    public bool TryRecoverStorageMappings(Exception cause, out string detail)
    {
        detail = string.Empty;
        if (Volatile.Read(ref _disposed) != 0 || !IsRecoverableMappingFailure(cause))
            return false;

        var acquired = 0;
        try
        {
            for (var ch = 1; ch <= EPB_COUNT; ch++)
            {
                Monitor.Enter(_states[ch].Gate);
                acquired++;
            }
            if (Volatile.Read(ref _disposed) != 0) return false;

            var released = 0;
            for (var ch = 1; ch <= EPB_COUNT; ch++)
            {
                var view = _views[ch];
                _views[ch] = null;
                _viewBaseOffsets[ch] = 0;
                _viewLengths[ch] = 0;
                if (view == null) continue;
                try { view.Dispose(); }
                catch { /* 旧视图已失效不影响后续延迟重建 */ }
                released++;
            }
            detail = $"已释放{released}个短视图；下一次写入将按需重建8MB视图";
            return true;
        }
        finally
        {
            for (var ch = acquired; ch >= 1; ch--)
                Monitor.Exit(_states[ch].Gate);
        }
    }

    private static bool IsRecoverableMappingFailure(Exception cause)
    {
        for (var ex = cause; ex != null; ex = ex.InnerException)
        {
            if (ex is OutOfMemoryException) return true;
            if (ex is ObjectDisposedException disposed &&
                ((disposed.ObjectName ?? string.Empty).IndexOf("MemoryAccessor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 disposed.Message.IndexOf("访问器", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 disposed.Message.IndexOf("accessor", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            // ERROR_NOT_ENOUGH_MEMORY(8), ERROR_OUTOFMEMORY(14),
            // ERROR_COMMITMENT_LIMIT(1455) 可被 IOException/Win32Exception 包装。
            var win32Code = ex.HResult & 0xFFFF;
            if (win32Code == 8 || win32Code == 14 || win32Code == 1455)
                return true;
        }
        return false;
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
            ResetSequenceBoundary(s);
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;
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
            EnsureCurrentCycleIdentity(s, epbId, cycleNumber, "CompleteCycle");
            MarkCycleCompleted(epbId, cycleNumber, finalSampleCount, endUtc);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleEndUtc = null;
            ResetSequenceBoundary(s);
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;

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
            EnsureCurrentCycleIdentity(s, epbId, cycleNumber, "AlarmCycle");
            MarkCycleAlarm(epbId, cycleNumber, finalSampleCount, endUtc);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleEndUtc = null;
            ResetSequenceBoundary(s);
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;
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
        var storage = GetSealStorage(normalizedStatus);
        var saveCsv = HasCsv(storage);
        var saveBin = HasBin(storage);
        var csvPath = saveCsv ? Path.Combine(exportDir, stem + ".csv") : null;
        var binPath = saveBin ? Path.Combine(exportDir, stem + ".bin") : null;
        var evidence = new AlarmCycleSnapshotEvidence
        {
            CsvPath = csvPath,
            BinPath = binPath,
            FinalStatus = normalizedStatus,
            StorageFormat = storage.ToString()
        };
        var s = GetState(epbId);
        Exception terminalCommitFailure = null;
        var terminalCommitted = false;
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
                                 normalizedStatus == "learning_failed" ||
                                 normalizedStatus == "qualification_canceled" ||
                                 normalizedStatus == "qualification_failed";
                if (cycle.SampleCount <= 0 && !allowEmpty)
                    throw new InvalidDataException($"EPB[{epbId}] Cycle={cycleNumber} 没有可封存样本。");

                var records = cycle.SampleCount > 0
                    ? ReadCycleRecordsFromRing(epbId, cycle, s.CapacityRecords)
                    : new List<SampleRecord>();
                if (records.Count > 0)
                    endUtc = DateTime.FromBinary(records[records.Count - 1].TimestampBinary).ToUniversalTime();
                Directory.CreateDirectory(exportDir);
                try
                {
                    if (saveCsv && saveBin)
                    {
                        ExportCyclePair(epbId, cycle, csvPath, binPath, new ExportFormatOptions(), records);
                    }
                    else if (saveCsv)
                    {
                        WriteAtomically(csvPath, tempPath =>
                        {
                            using (var sw = new StreamWriter(tempPath, false, Encoding.UTF8))
                                WriteCsvRecords(sw, records, new ExportFormatOptions());
                        });
                    }
                    else
                    {
                        WriteAtomically(binPath, tempPath =>
                        {
                            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                            using (var bw = new BinaryWriter(fs))
                                WriteBinRecords(bw, records);
                        });
                    }
                }
                finally
                {
                    // WriteAtomically/ExportCyclePair remove their own temporary files.
                }

                evidence = ValidateAlarmCycleSnapshotFiles(
                    csvPath,
                    binPath,
                    epbId,
                    cycleNumber,
                    allowEmpty,
                    storage);
                evidence.WasClaimed = true;
                evidence.FinalStatus = normalizedStatus;
                evidence.StorageFormat = storage.ToString();
                evidence.SampleClockDevice = s.CurrentCycleDevice;
                evidence.SampleClockGeneration = s.CurrentCycleGeneration;
                evidence.CycleStartAfterSequence = s.CurrentCycleStartAfterSequence;
                evidence.CycleEndSequence = s.CurrentCycleEndSequence ?? s.CurrentCycleLastSequence;
                evidence.LastWrittenSequence = s.CurrentCycleLastSequence;
                evidence.PreTriggerSampleCount = s.CurrentCyclePreTriggerSamples;
                evidence.RequiredPreTriggerSampleCount = s.SequenceBoundaryEnabled
                    ? MINIMUM_PRE_TRIGGER_SAMPLE_COUNT
                    : 0;
                evidence.SemanticEvidenceComplete = ValidateSequencedCycleSemantics(
                    s,
                    out var semanticValidationError);
                if (evidence.IsValid && !evidence.SemanticEvidenceComplete)
                {
                    evidence.IsValid = false;
                    evidence.ValidationError =
                        $"EPB[{epbId}] Cycle={cycleNumber} 文件存在但波形语义不完整：" +
                        semanticValidationError;
                }
                if (!evidence.IsValid)
                    throw new InvalidDataException(evidence.ValidationError);

                MarkCycleFinalized(
                    epbId,
                    cycleNumber,
                    evidence.SampleCount,
                    evidence.LastSampleUtc ?? endUtc,
                    normalizedStatus);
                terminalCommitted = true;
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
                            : normalizedStatus.StartsWith("qualification_", StringComparison.Ordinal)
                                ? "qualification_failed"
                            : "failed");
                    terminalCommitted = true;
                }
                catch (Exception dbEx)
                {
                    evidence.ValidationError += " | DBFinalizeFailed: " + dbEx.Message;
                    terminalCommitFailure = dbEx;
                }
            }
            finally
            {
                if (terminalCommitted)
                {
                    s.CurrentCycle = null;
                    s.CurrentSampleIndex = 0;
                    s.CurrentCycleEndUtc = null;
                    ResetSequenceBoundary(s);
                    s.LastProgressCheckpointUtc = DateTime.MinValue;
                    s.ActiveCycleLimitLatched = false;
                }
            }
        }

        if (terminalCommitFailure != null)
            throw new InvalidOperationException(
                $"EPB[{epbId}] Cycle={cycleNumber} 文件封存失败后的数据库终态也未提交；" +
                "保留活动圈等待重试。",
                terminalCommitFailure);

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
            case "qualification_completed":
            case "qualification_canceled":
            case "qualification_failed":
                return normalized;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(status),
                    status,
                    "仅支持 alarm、learning_*、qualification_* 的明确终态。");
        }
    }

    private StorageFormatLevel GetSealStorage(string normalizedStatus)
    {
        if (string.Equals(normalizedStatus, "alarm", StringComparison.OrdinalIgnoreCase))
            return NormalizeStorageLevel(_policy.AlarmStorageLevel, StorageFormatLevel.CsvOnly);
        if (normalizedStatus.StartsWith("learning_", StringComparison.OrdinalIgnoreCase) ||
            normalizedStatus.StartsWith("qualification_", StringComparison.OrdinalIgnoreCase))
            return NormalizeStorageLevel(_policy.LearningStorageLevel, StorageFormatLevel.BinOnly);
        return StorageFormatLevel.CsvAndBin;
    }

    private static StorageFormatLevel NormalizeStorageLevel(
        StorageFormatLevel value,
        StorageFormatLevel fallback)
    {
        return Enum.IsDefined(typeof(StorageFormatLevel), value) ? value : fallback;
    }

    private static bool HasCsv(StorageFormatLevel value)
    {
        return value == StorageFormatLevel.CsvOnly || value == StorageFormatLevel.CsvAndBin;
    }

    private static bool HasBin(StorageFormatLevel value)
    {
        return value == StorageFormatLevel.BinOnly || value == StorageFormatLevel.CsvAndBin;
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

    /// <summary>
    /// 校验按程序级格式保存的单格式或双格式圈证据。
    /// 旧 CSV+BIN 包始终按双格式兼容校验；单格式包只校验实际存在的文件。
    /// </summary>
    public static AlarmCycleSnapshotEvidence ValidateAlarmCycleSnapshotFiles(
        string csvPath,
        string binPath,
        int epbId,
        int cycleNumber,
        bool allowEmpty = false,
        StorageFormatLevel? expectedFormat = null)
    {
        var hasCsv = !string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath);
        var hasBin = !string.IsNullOrWhiteSpace(binPath) && File.Exists(binPath);
        var inferred = hasCsv && hasBin
            ? StorageFormatLevel.CsvAndBin
            : hasCsv
                ? StorageFormatLevel.CsvOnly
                : hasBin ? StorageFormatLevel.BinOnly : StorageFormatLevel.CsvAndBin;
        if (!hasCsv && !hasBin)
            return new AlarmCycleSnapshotEvidence
            {
                CsvPath = csvPath,
                BinPath = binPath,
                StorageFormat = (expectedFormat ?? inferred).ToString(),
                ValidationError = $"EPB[{epbId}] Cycle={cycleNumber} 圈快照文件不存在。"
            };

        // Existing pair packages remain valid even when the new policy is single-format.
        if (hasCsv && hasBin)
        {
            var pair = ValidateAlarmCycleSnapshotPair(csvPath, binPath, epbId, cycleNumber, allowEmpty);
            pair.StorageFormat = StorageFormatLevel.CsvAndBin.ToString();
            return pair;
        }

        var evidence = new AlarmCycleSnapshotEvidence
        {
            CsvPath = csvPath,
            BinPath = binPath,
            StorageFormat = inferred.ToString()
        };
        try
        {
            if (hasBin)
            {
                var length = new FileInfo(binPath).Length;
                if ((!allowEmpty && length <= 0) || length % SampleRecord.Size != 0)
                    throw new InvalidDataException(
                        $"BIN 长度 {length} 不是 {SampleRecord.Size} 字节记录的整数倍。");
                var count = checked((int)(length / SampleRecord.Size));
                DateTime? first = null;
                DateTime? last = null;
                using (var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    for (var i = 0; i < count; i++)
                    {
                        var ts = br.ReadInt64();
                        var actualCycle = br.ReadInt32();
                        var sampleIndex = br.ReadInt32();
                        br.ReadDouble();
                        br.ReadDouble();
                        if (actualCycle != cycleNumber || sampleIndex != i)
                            throw new InvalidDataException(
                                $"BIN 圈号/样本序号不一致：ExpectedCycle={cycleNumber} ActualCycle={actualCycle} " +
                                $"ExpectedIndex={i} ActualIndex={sampleIndex}。");
                        var utc = DateTime.FromBinary(ts).ToUniversalTime();
                        if (last.HasValue && utc < last.Value)
                            throw new InvalidDataException($"BIN 时间戳回退：Index={i}。");
                        first ??= utc;
                        last = utc;
                    }
                }
                evidence.SampleCount = count;
                evidence.FirstSampleUtc = first;
                evidence.LastSampleUtc = last;
            }
            else
            {
                var count = 0;
                DateTime? first = null;
                DateTime? last = null;
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
                            throw new InvalidDataException($"CSV 第 {count + 2} 行无法解析圈号或样本序号。");
                        if (actualCycle != cycleNumber || sampleIndex != count)
                            throw new InvalidDataException(
                                $"CSV 证据不连续：ExpectedCycle={cycleNumber} ActualCycle={actualCycle} " +
                                $"ExpectedIndex={count} ActualIndex={sampleIndex}。");
                        if (DateTime.TryParse(fields[0], CultureInfo.CurrentCulture,
                                DateTimeStyles.AllowWhiteSpaces, out var local))
                        {
                            var utc = local.ToUniversalTime();
                            if (last.HasValue && utc < last.Value)
                                throw new InvalidDataException($"CSV 时间戳回退：Index={count}。");
                            first ??= utc;
                            last = utc;
                        }
                        count++;
                    }
                }
                evidence.SampleCount = count;
                evidence.FirstSampleUtc = first;
                evidence.LastSampleUtc = last;
            }
            evidence.IsValid = true;
            evidence.ValidationError = string.Empty;
        }
        catch (Exception ex)
        {
            evidence.IsValid = false;
            evidence.ValidationError = $"EPB[{epbId}] Cycle={cycleNumber} 圈快照证据校验失败：{ex.Message}";
        }
        return evidence;
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
            BinPath = binPath,
            StorageFormat = StorageFormatLevel.CsvAndBin.ToString()
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
    /// 将未完整完成的圈封为 canceled/failed/AbortedByDaqClockRecovery/
    /// AbortedBySoftwareRecovery。
    /// 此类圈不参与成功计数和正常圈导出。
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
            : string.Equals(status, "AbortedByDaqClockRecovery", StringComparison.OrdinalIgnoreCase)
                ? "AbortedByDaqClockRecovery"
                : string.Equals(status, "AbortedBySoftwareRecovery", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(status, "SoftwareRecoveryAborted", StringComparison.OrdinalIgnoreCase)
                    ? "AbortedBySoftwareRecovery"
                    : "failed";
        var s = GetState(epbId);
        lock (s.Gate)
        {
            EnsureCurrentCycleIdentity(s, epbId, cycleNumber, "AbortCycle");
            MarkCycleAborted(epbId, cycleNumber, finalSampleCount, endUtc, normalized);
            s.CurrentCycle = null;
            s.CurrentSampleIndex = 0;
            s.CurrentCycleEndUtc = null;
            ResetSequenceBoundary(s);
            s.LastProgressCheckpointUtc = DateTime.MinValue;
            s.ActiveCycleLimitLatched = false;
        }
    }

    private static void EnsureCurrentCycleIdentity(
        EpbState state,
        int epbId,
        int expectedCycle,
        string operation)
    {
        if (state.CurrentCycle == expectedCycle) return;
        throw new InvalidOperationException(
            $"EPB[{epbId}] {operation} 拒绝修改非当前圈。" +
            $"Expected={expectedCycle} Current={state.CurrentCycle?.ToString() ?? "null"}。");
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
                if (s.ActiveCycleLimitLatched) return;
                if (_policy.MaxActiveCycleRecords > 0 &&
                    s.CurrentSampleIndex >= _policy.MaxActiveCycleRecords)
                {
                    s.ActiveCycleLimitLatched = true;
                    throw new ActiveCycleDataLimitExceededException(
                        epbId,
                        cycle,
                        _policy.MaxActiveCycleRecords);
                }
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
            if (cycle != 0 && ShouldCheckpointCycleProgress(s, tsUtc, commit: true))
                UpdateCycleProgress(epbId, cycle, sampleIndex + 1, tsUtc);
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
                state.ActiveCycleLimitLatched)
                return;

            if (state.CurrentCycle.HasValue &&
                _policy.MaxActiveCycleRecords > 0 &&
                state.CurrentSampleIndex + accepted > _policy.MaxActiveCycleRecords)
            {
                state.ActiveCycleLimitLatched = true;
                throw new ActiveCycleDataLimitExceededException(
                    epbId,
                    state.CurrentCycle.Value,
                    _policy.MaxActiveCycleRecords);
            }

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

                // 原始样本每批写 MMF；SQLite 仅每秒做一次进度检查点。
                // 封圈仍会同步写最终样本数，因此不会影响完成圈的索引准确性。
                if (cycle != 0 &&
                    ShouldCheckpointCycleProgress(state, tsUtc[to - 1], commit: true))
                    UpdateCycleProgress(epbId, cycle, state.CurrentSampleIndex, tsUtc[to - 1]);
            }
            finally
            {
                ArrayPool<SampleRecord>.Shared.Return(records, clearArray: false);
            }
        }
    }

    private void WriteSequencedBatch(
        int epbId,
        string device,
        long generation,
        long sequence,
        DateTime[] tsUtc,
        double[] epbCurrents,
        double[] groupPressures,
        int count)
    {
        if (tsUtc == null || epbCurrents == null || groupPressures == null)
            throw new ArgumentNullException("tsUtc/epbCurrents/groupPressures");
        if (string.IsNullOrWhiteSpace(device))
            throw new ArgumentException("device is required", nameof(device));
        if (generation < 0 || sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (count < 0 || count > tsUtc.Length || count > epbCurrents.Length || count > groupPressures.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        var state = GetState(epbId);
        lock (state.Gate)
        {
            if (!state.CurrentCycle.HasValue)
            {
                BufferPreTriggerSamples(
                    state,
                    device,
                    generation,
                    sequence,
                    tsUtc,
                    epbCurrents,
                    groupPressures,
                    count);
                if (!state.FreeRunOn) return;
                WriteBatch(epbId, tsUtc, epbCurrents, groupPressures, count);
                return;
            }

            if (!state.SequenceBoundaryEnabled)
            {
                WriteBatch(epbId, tsUtc, epbCurrents, groupPressures, count);
                return;
            }
            if (!string.Equals(state.CurrentCycleDevice, device, StringComparison.OrdinalIgnoreCase) ||
                state.CurrentCycleGeneration != generation)
                throw new InvalidDataException(
                    $"EPB[{epbId}] 圈跨越DAQ代次或设备。" +
                    $"Expected={state.CurrentCycleDevice}/{state.CurrentCycleGeneration} " +
                    $"Actual={device}/{generation} Sequence={sequence}。");
            if (state.CurrentCycleEndSequence.HasValue && sequence > state.CurrentCycleEndSequence.Value)
                return;
            if (state.CurrentCycleLastSequence > 0 && sequence <= state.CurrentCycleLastSequence)
                return;
            if (state.CurrentCycleLastSequence > 0 && sequence != state.CurrentCycleLastSequence + 1)
                throw new InvalidDataException(
                    $"EPB[{epbId}] 圈内DAQ批次序号不连续。" +
                    $"Previous={state.CurrentCycleLastSequence} Current={sequence}。");
            if (state.ActiveCycleLimitLatched || count == 0) return;
            if (_policy.MaxActiveCycleRecords > 0 &&
                state.CurrentSampleIndex + count > _policy.MaxActiveCycleRecords)
            {
                state.ActiveCycleLimitLatched = true;
                throw new ActiveCycleDataLimitExceededException(
                    epbId,
                    state.CurrentCycle.Value,
                    _policy.MaxActiveCycleRecords);
            }

            var cycle = state.CurrentCycle.Value;
            var firstSampleIndex = state.CurrentSampleIndex;
            var records = ArrayPool<SampleRecord>.Shared.Rent(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    records[i] = new SampleRecord
                    {
                        TimestampBinary = tsUtc[i].ToLocalTime().ToBinary(),
                        CycleNumber = cycle,
                        SampleIndex = firstSampleIndex + i,
                        EpbCurrent = epbCurrents[i],
                        GroupPressure = groupPressures[i]
                    };
                }
                WriteRecordBatch(epbId, state, records, count);
                state.CurrentSampleIndex += count;
                state.TotalWritten += count;
                state.CurrentCycleLastSequence = sequence;
                if (sequence <= state.CurrentCycleStartAfterSequence)
                    state.CurrentCyclePreTriggerSamples += count;
                if (ShouldCheckpointCycleProgress(state, tsUtc[count - 1], commit: true))
                    UpdateCycleProgress(epbId, cycle, state.CurrentSampleIndex, tsUtc[count - 1]);
            }
            finally
            {
                ArrayPool<SampleRecord>.Shared.Return(records, clearArray: false);
            }
        }
    }

    private static void BufferPreTriggerSamples(
        EpbState state,
        string device,
        long generation,
        long sequence,
        DateTime[] timestampsUtc,
        double[] currents,
        double[] pressures,
        int count)
    {
        for (var i = 0; i < count; i++)
        {
            state.PreTriggerSamples.Enqueue(new PreTriggerSample(
                device,
                generation,
                sequence,
                timestampsUtc[i],
                currents[i],
                pressures[i]));
            while (state.PreTriggerSamples.Count > PRE_TRIGGER_SAMPLE_CAPACITY)
                state.PreTriggerSamples.Dequeue();
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
        => WriteDeviceBatchCore(
            null,
            timestampsUtc,
            channels,
            channelCount,
            sampleCount);

    public void WriteDeviceBatch(
        string device,
        long generation,
        long sequence,
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount)
        => WriteDeviceBatchCore(
            new DeviceBatchBoundary(device, generation, sequence),
            timestampsUtc,
            channels,
            channelCount,
            sampleCount);

    private readonly struct DeviceBatchBoundary
    {
        public DeviceBatchBoundary(string device, long generation, long sequence)
        {
            Device = device;
            Generation = generation;
            Sequence = sequence;
        }

        public string Device { get; }
        public long Generation { get; }
        public long Sequence { get; }
    }

    private void WriteDeviceBatchCore(
        DeviceBatchBoundary? boundary,
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
        var snapshots = ArrayPool<StateWriteSnapshot>.Shared.Rent(channelCount);
        var acquired = 0;
        try
        {
            for (var i = 0; i < channelCount; i++)
            {
                var state = GetState(channels[i].EpbId);
                Monitor.Enter(state.Gate);
                snapshots[acquired] = new StateWriteSnapshot
                {
                    TotalWritten = state.TotalWritten,
                    CurrentSampleIndex = state.CurrentSampleIndex,
                    FreeRunSampleIndex = state.FreeRunSampleIndex,
                    LastProgressCheckpointUtc = state.LastProgressCheckpointUtc,
                    CurrentCycleLastSequence = state.CurrentCycleLastSequence,
                    CurrentCyclePreTriggerSamples = state.CurrentCyclePreTriggerSamples
                };
                lockedStates[acquired++] = state;
            }

            var needsProgressTransaction = false;
            var lastTimestampUtc = sampleCount > 0
                ? timestampsUtc[Math.Min(sampleCount, timestampsUtc.Length) - 1]
                : DateTime.UtcNow;
            for (var i = 0; i < acquired; i++)
            {
                if (lockedStates[i].CurrentCycle.HasValue &&
                    !lockedStates[i].ActiveCycleLimitLatched &&
                    ShouldCheckpointCycleProgress(lockedStates[i], lastTimestampUtc, commit: false))
                {
                    needsProgressTransaction = true;
                    break;
                }
            }

            if (needsProgressTransaction)
            {
                lock (_dbGate)
                {
                    Interlocked.Increment(ref _progressCheckpointTransactionCount);
                    using var transaction = _conn.BeginTransaction();
                    _activeBatchTransaction = transaction;
                    try
                    {
                        for (var i = 0; i < channelCount; i++)
                        {
                            var channel = channels[i];
                            if (boundary.HasValue)
                                WriteSequencedBatch(
                                    channel.EpbId,
                                    boundary.Value.Device,
                                    boundary.Value.Generation,
                                    boundary.Value.Sequence,
                                    timestampsUtc,
                                    channel.Currents,
                                    channel.Pressures,
                                    sampleCount);
                            else
                                WriteBatch(channel.EpbId, timestampsUtc, channel.Currents, channel.Pressures, sampleCount);
                        }
                        transaction.Commit();
                    }
                    finally
                    {
                        _activeBatchTransaction = null;
                    }
                }
            }
            else
            {
                for (var i = 0; i < channelCount; i++)
                {
                    var channel = channels[i];
                    if (boundary.HasValue)
                        WriteSequencedBatch(
                            channel.EpbId,
                            boundary.Value.Device,
                            boundary.Value.Generation,
                            boundary.Value.Sequence,
                            timestampsUtc,
                            channel.Currents,
                            channel.Pressures,
                            sampleCount);
                    else
                        WriteBatch(channel.EpbId, timestampsUtc, channel.Currents, channel.Pressures, sampleCount);
                }
            }
        }
        catch
        {
            // 批次可能已写了前面的通道后才在后续通道重映射失败。
            // 回滚内存记账，使协调器重试时覆写同一组环形位置，避免重复样本。
            // 如果本批启用了 SQLite 事务，异常离开 using 时同步回滚索引更新。
            for (var i = 0; i < acquired; i++)
            {
                lockedStates[i].TotalWritten = snapshots[i].TotalWritten;
                lockedStates[i].CurrentSampleIndex = snapshots[i].CurrentSampleIndex;
                lockedStates[i].FreeRunSampleIndex = snapshots[i].FreeRunSampleIndex;
                lockedStates[i].LastProgressCheckpointUtc = snapshots[i].LastProgressCheckpointUtc;
                lockedStates[i].CurrentCycleLastSequence = snapshots[i].CurrentCycleLastSequence;
                lockedStates[i].CurrentCyclePreTriggerSamples = snapshots[i].CurrentCyclePreTriggerSamples;
            }
            throw;
        }
        finally
        {
            for (var i = acquired - 1; i >= 0; i--)
            {
                Monitor.Exit(lockedStates[i].Gate);
                lockedStates[i] = null;
            }
            ArrayPool<EpbState>.Shared.Return(lockedStates, clearArray: false);
            ArrayPool<StateWriteSnapshot>.Shared.Return(snapshots, clearArray: false);
        }
    }

    private static bool ShouldCheckpointCycleProgress(
        EpbState state,
        DateTime sampleUtc,
        bool commit)
    {
        var utc = sampleUtc.Kind == DateTimeKind.Utc ? sampleUtc : sampleUtc.ToUniversalTime();
        var due = state.LastProgressCheckpointUtc == DateTime.MinValue ||
                  utc - state.LastProgressCheckpointUtc >= TimeSpan.FromSeconds(1);
        if (due && commit) state.LastProgressCheckpointUtc = utc;
        return due;
    }

    public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc)
    {
        var state = GetState(epbId);
        lock (state.Gate)
        {
            if (state.CurrentCycle == cycleNumber)
            {
                var cutoffUtc = endUtc.ToUniversalTime();
                // 多条恢复链可能并发观察到同一活动圈。截止时间窗只能收紧，不能被
                // 较晚到达的液压/电源恢复请求重新扩大，否则已宣布截止后的 Raw
                // 样本会重新获得圈归属并破坏耐久边界。
                if (!state.CurrentCycleEndUtc.HasValue ||
                    cutoffUtc < state.CurrentCycleEndUtc.Value)
                    state.CurrentCycleEndUtc = cutoffUtc;
            }
        }
    }

    public void SealCycleWindowAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime endUtc,
        string device,
        long generation,
        long endSequence)
    {
        var state = GetState(epbId);
        lock (state.Gate)
        {
            if (state.CurrentCycle != cycleNumber) return;
            if (!state.SequenceBoundaryEnabled)
            {
                SealCycleWindow(epbId, cycleNumber, endUtc);
                return;
            }
            if (!string.Equals(state.CurrentCycleDevice, device, StringComparison.OrdinalIgnoreCase) ||
                state.CurrentCycleGeneration != generation)
                throw new InvalidDataException(
                    $"EPB[{epbId}] 圈封口代次不一致。" +
                    $"Expected={state.CurrentCycleDevice}/{state.CurrentCycleGeneration} " +
                    $"Actual={device}/{generation}。");
            var bounded = Math.Max(0, endSequence);
            if (!state.CurrentCycleEndSequence.HasValue || bounded < state.CurrentCycleEndSequence.Value)
                state.CurrentCycleEndSequence = bounded;
            var cutoffUtc = endUtc.ToUniversalTime();
            if (!state.CurrentCycleEndUtc.HasValue || cutoffUtc < state.CurrentCycleEndUtc.Value)
                state.CurrentCycleEndUtc = cutoffUtc;
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
        => ExportLatestCyclePackageNow(epbId, latestN, null, false);

    /// <summary>
    ///     为暂停导出最近正式圈。同一通道已经存在内容完全相同且校验通过的 Latest 包时复用，
    ///     避免“暂停后停止”重复生成同一批证据。
    /// </summary>
    public void ExportLatestCyclesOnceNow(int epbId, int latestN = 10)
        => ExportLatestCyclePackageNow(epbId, latestN, null, true);

    /// <summary>
    ///     为停止导出最近圈；若停止截断了活动圈，强制把该终态半圈纳入最近 N 圈。
    /// </summary>
    public void ExportLatestCyclesForStopNow(
        int epbId,
        int latestN = 10,
        int interruptedCycleNumber = 0)
        => ExportLatestCyclePackageNow(
            epbId,
            latestN,
            interruptedCycleNumber > 0 ? (int?)interruptedCycleNumber : null,
            true);

    private void ExportLatestCyclePackageNow(
        int epbId,
        int latestN,
        int? requiredTerminalCycleNumber,
        bool reuseMatchingPackage)
    {
        var published = false;
        lock (_latestExportGates[epbId])
        {
            latestN = Math.Max(1, latestN);
            var latestList = requiredTerminalCycleNumber.HasValue
                ? GetLatestCyclesForStop(epbId, latestN, requiredTerminalCycleNumber.Value)
                : GetLatestCycles(epbId, latestN, includeRunningCycle: false);
            if (requiredTerminalCycleNumber.HasValue &&
                latestList.All(cycle => cycle.CycleNumber != requiredTerminalCycleNumber.Value))
                throw new InvalidDataException(
                    $"EPB[{epbId}] 停止圈 {requiredTerminalCycleNumber.Value} 尚未封为可导出终态。");
            if (latestList.Count == 0) return;

            var dir = Path.Combine(_indexDir, "Latest", $"EPB{epbId}");
            Directory.CreateDirectory(dir);
            if (reuseMatchingPackage && HasMatchingLatestPackage(dir, epbId, latestList))
                return;
            var sequence = Interlocked.Increment(ref _latestExportSequence);
            var name = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{sequence:D6}";
            var staging = Path.Combine(dir, $".{name}.tmp-{Guid.NewGuid():N}");
            var final = Path.Combine(dir, name);
            Directory.CreateDirectory(staging);
            try
            {
                var latestStorage = NormalizeStorageLevel(_policy.LatestStorageLevel, StorageFormatLevel.CsvOnly);
                ExportCycleList(epbId, latestList, staging, latestStorage);
                ValidateExportDirectory(epbId, latestList, staging, latestStorage);
                WriteLatestManifest(
                    staging,
                    epbId,
                    latestList,
                    latestStorage,
                    requiredTerminalCycleNumber);
                Directory.Move(staging, final);
                published = true;
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    try { Directory.Delete(staging, true); } catch { }
                }
            }
        }
        if (published) QueueLatestPackageRetention(epbId);
    }

    private static void WriteLatestManifest(
        string directory,
        int epbId,
        IReadOnlyList<CycleInfo> cycles,
        StorageFormatLevel storage,
        int? requiredTerminalCycleNumber)
    {
        var path = Path.Combine(directory, "latest-manifest.json");
        WriteAtomically(path, tempPath =>
        {
            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"schemaVersion\": 1,");
            json.AppendLine("  \"snapshotState\": \"Final\",");
            json.AppendLine($"  \"generatedUtc\": \"{DateTime.UtcNow:O}\",");
            json.AppendLine($"  \"epbId\": {epbId},");
            json.AppendLine($"  \"storageFormat\": \"{storage}\",");
            json.AppendLine($"  \"requiredTerminalCycle\": {(requiredTerminalCycleNumber?.ToString(CultureInfo.InvariantCulture) ?? "null")},");
            json.AppendLine("  \"cycles\": [");
            for (var i = 0; i < cycles.Count; i++)
            {
                var cycle = cycles[i];
                var files = new List<string>();
                if (HasCsv(storage))
                    files.Add($"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.csv");
                if (HasBin(storage))
                    files.Add($"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.bin");
                json.Append("    {");
                json.Append($"\"cycleNumber\": {cycle.CycleNumber}, ");
                json.Append($"\"status\": \"{EscapeJson(cycle.Status)}\", ");
                json.Append($"\"sampleCount\": {cycle.SampleCount}, ");
                json.Append("\"files\": [");
                for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
                {
                    var file = files[fileIndex];
                    json.Append(
                        $"{{\"name\": \"{file}\", \"sha256\": \"{ComputeSha256(Path.Combine(directory, file))}\"}}");
                    if (fileIndex < files.Count - 1) json.Append(", ");
                }
                json.Append("]}");
                json.AppendLine(i < cycles.Count - 1 ? "," : string.Empty);
            }
            json.AppendLine("  ]");
            json.AppendLine("}");
            File.WriteAllText(tempPath, json.ToString(), new UTF8Encoding(false));
        });
    }

    private static string ComputeSha256(string path)
    {
        using var algorithm = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return string.Concat(algorithm.ComputeHash(stream).Select(value => value.ToString("x2")));
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private bool HasMatchingLatestPackage(
        string root,
        int epbId,
        IReadOnlyCollection<CycleInfo> cycles)
    {
        var storage = NormalizeStorageLevel(_policy.LatestStorageLevel, StorageFormatLevel.CsvOnly);
        var requireCsv = HasCsv(storage);
        var requireBin = HasBin(storage);
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => LatestPackageNamePattern.IsMatch(Path.GetFileName(path)))
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return false;
        }

        foreach (var directory in directories)
        {
            try
            {
                // 旧版本没有最终态 manifest，不能作为“相同停止证据包”复用，
                // 否则升级后仍可能把缺少终止语义的临时包误认为最终包。
                var manifestPath = Path.Combine(directory, "latest-manifest.json");
                if (!File.Exists(manifestPath)) continue;
                var manifest = File.ReadAllText(manifestPath);
                if (manifest.IndexOf(
                        "\"snapshotState\": \"Final\"",
                        StringComparison.Ordinal) < 0)
                    continue;
                if (requireCsv && Directory.GetFiles(directory, "*.csv", SearchOption.TopDirectoryOnly).Length < cycles.Count ||
                    requireBin && Directory.GetFiles(directory, "*.bin", SearchOption.TopDirectoryOnly).Length < cycles.Count)
                    continue;
                var matches = true;
                foreach (var cycle in cycles)
                {
                    var csv = requireCsv
                        ? Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.csv")
                        : null;
                    var bin = requireBin
                        ? Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.bin")
                        : null;
                    var validation = ValidateAlarmCycleSnapshotFiles(
                        csv,
                        bin,
                        epbId,
                        cycle.CycleNumber,
                        cycle.SampleCount == 0,
                        storage);
                    if (!validation.IsValid || validation.SampleCount != cycle.SampleCount)
                    {
                        matches = false;
                        break;
                    }
                }
                if (matches) return true;
            }
            catch
            {
                // 损坏或不完整的旧包不能作为去重证据，继续创建新的原子包。
            }
        }
        return false;
    }

    private void QueueLatestPackageRetention(int epbId)
    {
        if (_policy.RetainAllLatestStopPackages || Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _latestCleanupRequested[epbId], 1);
        if (Interlocked.CompareExchange(ref _latestCleanupRunning[epbId], 1, 0) != 0) return;

        var thread = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref _disposed) == 0 &&
                       Interlocked.Exchange(ref _latestCleanupRequested[epbId], 0) == 1)
                {
                    lock (_latestExportGates[epbId])
                    {
                        try { EnforceLatestPackageRetention(epbId); }
                        catch (Exception ex) { WarnRetention($"Latest EPB{epbId} 清理失败：{ex.Message}"); }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _latestCleanupRunning[epbId], 0);
                Interlocked.CompareExchange(
                    ref _latestCleanupThreads[epbId],
                    null,
                    Thread.CurrentThread);
                if (Volatile.Read(ref _disposed) == 0 &&
                    Volatile.Read(ref _latestCleanupRequested[epbId]) == 1)
                    QueueLatestPackageRetention(epbId);
            }
        })
        {
            IsBackground = true,
            Name = $"EPB-LatestRetention-{epbId}"
        };
        Volatile.Write(ref _latestCleanupThreads[epbId], thread);
        try
        {
            thread.Start();
        }
        catch
        {
            Interlocked.CompareExchange(ref _latestCleanupThreads[epbId], null, thread);
            Interlocked.Exchange(ref _latestCleanupRunning[epbId], 0);
            throw;
        }
    }

    private void EnforceLatestPackageRetention(int epbId)
    {
        if (_policy.RetainAllLatestStopPackages) return;
        var keep = _policy.RetainLatestStopPackagesPerChannel;
        if (keep < 1) keep = 10;
        var root = Path.Combine(_indexDir, "Latest", $"EPB{epbId}");
        if (!Directory.Exists(root)) return;

        var valid = new List<DirectoryInfo>();
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            var name = directory.Name;
            if (name.StartsWith(".", StringComparison.Ordinal) ||
                name.IndexOf(".tmp-", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;
            if (!LatestPackageNamePattern.IsMatch(name) || !IsValidLatestPackage(directory.FullName, epbId))
            {
                WarnRetention($"Latest EPB{epbId} 跳过无法识别或不完整目录：{directory.FullName}");
                continue;
            }
            valid.Add(directory);
        }

        var expired = valid
            .OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.LastWriteTimeUtc)
            .Skip(keep)
            .Reverse()
            .ToArray();
        foreach (var directory in expired)
        {
            try { directory.Delete(true); }
            catch (Exception ex)
            {
                WarnRetention($"Latest EPB{epbId} 删除失败：{directory.FullName} Error={ex.Message}");
            }
        }
    }

    private bool IsValidLatestPackage(string directory, int epbId)
    {
        try
        {
            var prefix = $"EPB{epbId}_Cycle_";
            var csv = Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bin = Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var storage = NormalizeStorageLevel(_policy.LatestStorageLevel, StorageFormatLevel.CsvOnly);
            if (storage == StorageFormatLevel.CsvOnly)
                return csv.Count > 0;
            if (storage == StorageFormatLevel.BinOnly)
                return bin.Count > 0;
            return csv.Count > 0 && csv.SetEquals(bin);
        }
        catch
        {
            return false;
        }
    }

    private void WarnRetention(string message)
    {
        try { _policy.RetentionWarningSink?.Invoke(message); }
        catch { }
    }

    private void ValidateExportDirectory(
        int epbId,
        IEnumerable<CycleInfo> cycles,
        string directory,
        StorageFormatLevel storage)
    {
        storage = NormalizeStorageLevel(storage, StorageFormatLevel.CsvAndBin);
        foreach (var cycle in cycles)
        {
            var csv = HasCsv(storage)
                ? Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.csv")
                : null;
            var bin = HasBin(storage)
                ? Path.Combine(directory, $"EPB{epbId}_Cycle_{cycle.CycleNumber:D6}.bin")
                : null;
            var validation = ValidateAlarmCycleSnapshotFiles(
                csv,
                bin,
                epbId,
                cycle.CycleNumber,
                cycle.SampleCount == 0,
                storage);
            if (!validation.IsValid || validation.SampleCount != cycle.SampleCount)
                throw new InvalidDataException(validation.ValidationError);
        }
    }


    /// <summary>
    ///     导出某 EPB 通道“最新 N 圈”的数据到指定目录（CSV + BIN），不删除索引。
    ///     可选择是否包含当前 <c>status='running'</c> 的圈（用于“报警快照：当前圈+之前9圈”）。
    /// </summary>
    public void ExportLatestCyclesTo(int epbId, int latestN, string exportDir, bool includeRunningCycle)
        => ExportLatestCyclesTo(
            epbId,
            latestN,
            exportDir,
            includeRunningCycle,
            StorageFormatLevel.CsvAndBin);

    /// <summary>
    /// 导出最近圈并显式指定证据格式。通用调用默认双格式；报警适配器使用 AlarmStorageLevel。
    /// </summary>
    public void ExportLatestCyclesTo(
        int epbId,
        int latestN,
        string exportDir,
        bool includeRunningCycle,
        StorageFormatLevel storage)
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
            ExportCycleList(epbId, latestList, fullDirectory, storage);
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

    /// <summary>
    /// 导出已经封账的尝试圈证据。允许 completed、alarm、failed、canceled 和
    /// AbortedBySoftwareRecovery 等终态，但拒绝仍在写入的 running 圈。
    /// </summary>
    public CycleSnapshotEvidence ExportCycleAttemptTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin)
    {
        if (!saveCsv && !saveBin)
            throw new ArgumentException("至少启用CSV或BIN一种快照格式。");
        var cycle = GetCycleInfo(epbId, cycleNumber);
        if (string.Equals(cycle.Status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(cycle.Status))
            throw new InvalidDataException(
                $"EPB[{epbId}] Cycle={cycleNumber} 尚未封账，拒绝导出可变证据。");
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
            IsCompleteCycle = string.Equals(cycle.Status, "completed", StringComparison.OrdinalIgnoreCase),
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
        // Generic recent-cycle exports are shared by incident/diagnostic paths and
        // retain the legacy pair regardless of the Latest package preference.
        ExportCycleList(epbId, cycles, exportDir, StorageFormatLevel.CsvAndBin);
    }

    private void ExportCycleList(
        int epbId,
        IEnumerable<CycleInfo> cycles,
        string exportDir,
        StorageFormatLevel storage)
    {
        storage = NormalizeStorageLevel(storage, StorageFormatLevel.CsvAndBin);
        var failures = new List<Exception>();
        foreach (var cy in cycles)
        {
            try
            {
                var csv = HasCsv(storage)
                    ? Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.csv")
                    : null;
                var bin = HasBin(storage)
                    ? Path.Combine(exportDir, $"EPB{epbId}_Cycle_{cy.CycleNumber:D6}.bin")
                    : null;
                if (HasCsv(storage) && HasBin(storage))
                    ExportCyclePair(epbId, cy, csv, bin, new ExportFormatOptions());
                else if (HasCsv(storage))
                    ExportCycleToCsv(epbId, cy, csv);
                else
                    ExportCycleToBin(epbId, cy, bin);
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
        ExportCyclePair(epbId, cycle, csvPath, binPath, fmt, records);
    }

    private void ExportCyclePair(
        int epbId,
        CycleInfo cycle,
        string csvPath,
        string binPath,
        ExportFormatOptions fmt,
        IReadOnlyList<SampleRecord> records)
    {
        if (string.IsNullOrWhiteSpace(csvPath) || string.IsNullOrWhiteSpace(binPath))
            throw new ArgumentException("双格式导出必须同时提供 CSV 和 BIN 路径。");
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
        {
            if (cycle.SampleCount <= 0) cycle.SampleCount = recovered.Count;
            return recovered;
        }

        if (cycle.SampleCount == 0 &&
            !string.Equals(cycle.Status, "running", StringComparison.OrdinalIgnoreCase))
            return new List<SampleRecord>();

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
        if (!Directory.Exists(_indexDir))
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
                var info = new FileInfo(candidate);
                if (info.Length <= 0 || info.Length % SampleRecord.Size != 0) continue;
                if (cycle.SampleCount > 0 &&
                    info.Length != (long)cycle.SampleCount * SampleRecord.Size)
                    continue;
                var recordCount = cycle.SampleCount > 0
                    ? cycle.SampleCount
                    : checked((int)(info.Length / SampleRecord.Size));

                var recovered = new List<SampleRecord>(recordCount);
                using var fs = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);
                for (var i = 0; i < recordCount; i++)
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

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)) ?? ".");
        List<SampleRecord> list;
        lock (s.Gate)
        {
            var capacity = s.CapacityRecords;
            if (capacity <= 0 || s.TotalWritten == 0)
                list = new List<SampleRecord>();
            else
            {
                var written = s.TotalWritten;
                var maxBack = Math.Min(written, capacity);
                list = new List<SampleRecord>(Math.Min(sampleCount, (int)maxBack));
                for (long back = 1; back <= maxBack && list.Count < sampleCount; back++)
                {
                    var raw = written - back;
                    var idx = raw % capacity;
                    if (idx < 0) idx += capacity;
                    var rec = ReadRecord(epbId, idx * SampleRecord.Size);
                    if (rec.CycleNumber == 0 && rec.TimestampBinary != 0) list.Add(rec);
                }
                list.Reverse();
            }
        }

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
    ///     若不在范围，先创建新视图，成功后再原子替换并释放旧视图。
    /// </summary>
    private void EnsureViewCovers(int ch, long targetOffset, long minSpanBytes)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EpbDiskWriter));
        if (targetOffset < 0 || minSpanBytes < 1 || targetOffset > _fileBytes - minSpanBytes)
            throw new ArgumentOutOfRangeException(nameof(targetOffset));

        var current = _views[ch];
        var baseOff = _viewBaseOffsets[ch];
        var len = _viewLengths[ch];
        var endOff = baseOff + len;

        var needSpan = Math.Max(1, minSpanBytes);
        if (current != null &&
            !current.SafeMemoryMappedViewHandle.IsClosed &&
            !current.SafeMemoryMappedViewHandle.IsInvalid &&
            targetOffset >= baseOff && targetOffset + needSpan <= endOff)
            return;

        var windowBytes = Math.Min(_fileBytes, Math.Max(VIEW_BYTES, needSpan));
        var desiredBase = Math.Max(0, targetOffset - Math.Max(0, windowBytes - needSpan) / 2);
        var newBase = AlignDown(desiredBase, VIEW_ALIGN);
        if (newBase + windowBytes > _fileBytes)
            newBase = AlignDown(Math.Max(0, _fileBytes - windowBytes), VIEW_ALIGN);
        var remain = Math.Max(0, _fileBytes - newBase);
        var newLen = Math.Min(windowBytes, remain);
        if (targetOffset + needSpan > newBase + newLen)
            newLen = targetOffset + needSpan - newBase;

        // 绝不先关旧视图。CreateViewAccessor 在 x86 地址空间紧张时可能失败；
        // 只有新视图已成功后才交换，从而避免将通道永久留在“已关闭访问器”状态。
        var replacement = _mmfs[ch].CreateViewAccessor(newBase, newLen, MemoryMappedFileAccess.ReadWrite);
        var previous = _views[ch];
        _views[ch] = replacement;
        _viewBaseOffsets[ch] = newBase;
        _viewLengths[ch] = newLen;
        previous?.Dispose();
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
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(EpbDiskWriter));
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
  mechanical_completed INTEGER NOT NULL DEFAULT 0,
  mechanical_completed_at TEXT,
  created_at TEXT DEFAULT (datetime('now')),
  UNIQUE(epb_id, cycle_number)
);
CREATE INDEX IF NOT EXISTS idx_cycles_epb ON {TABLE_CYCLES}(epb_id, cycle_number);";
        cmd.ExecuteNonQuery();
        EnsureCycleColumn("mechanical_completed", "INTEGER NOT NULL DEFAULT 0");
        EnsureCycleColumn("mechanical_completed_at", "TEXT");
        BackfillCertainMechanicalCompletionFacts();
        }
    }

    private void EnsureCycleColumn(string columnName, string definition)
    {
        using var inspect = _conn.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({TABLE_CYCLES})";
        using (var reader = inspect.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(
                        Convert.ToString(reader["name"], CultureInfo.InvariantCulture),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = _conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {TABLE_CYCLES} ADD COLUMN {columnName} {definition}";
        alter.ExecuteNonQuery();
    }

    private void BackfillCertainMechanicalCompletionFacts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET mechanical_completed=1,
       mechanical_completed_at=COALESCE(mechanical_completed_at,end_time)
 WHERE mechanical_completed=0
   AND status IN ('completed','learning_completed','qualification_completed')";
        cmd.ExecuteNonQuery();
    }

    /// <summary>持久化“夹紧+释放已经完成”的物理事实；不依赖该圈最终证据状态。</summary>
    public void MarkMechanicalCycleCompleted(int epbId, int cycleNumber, DateTime completedUtc)
    {
        if (cycleNumber == 0)
            throw new ArgumentOutOfRangeException(nameof(cycleNumber), "机械完成圈必须有正式或学习圈号。");
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET mechanical_completed=1,
       mechanical_completed_at=COALESCE(mechanical_completed_at,@completed)
 WHERE epb_id=@e AND cycle_number=@c";
            cmd.Parameters.AddWithValue("@completed", completedUtc.ToLocalTime().ToString("o"));
            cmd.Parameters.AddWithValue("@e", epbId);
            cmd.Parameters.AddWithValue("@c", cycleNumber);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    $"EPB[{epbId}] Cycle={cycleNumber} 机械完成事实没有对应数据库圈边界。");
        }
    }

    public long GetMechanicalCycleCompletedCount(int epbId)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
SELECT COUNT(*) FROM {TABLE_CYCLES}
 WHERE epb_id=@e AND mechanical_completed=1";
            cmd.Parameters.AddWithValue("@e", epbId);
            return Math.Max(0L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
    }

    public DateTime? GetLastMechanicalCycleCompletedUtc(int epbId)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
SELECT mechanical_completed_at FROM {TABLE_CYCLES}
 WHERE epb_id=@e AND mechanical_completed=1
       AND mechanical_completed_at IS NOT NULL
 ORDER BY mechanical_completed_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@e", epbId);
            var value = cmd.ExecuteScalar();
            if (value == null || value == DBNull.Value) return null;
            if (!DateTime.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                    out var parsed))
                return null;
            return parsed.ToUniversalTime();
        }
    }

    /// <summary>
    /// 上一进程崩溃、断电或被强制结束时，SQLite 中可能留下 running 圈。
    /// 构造完成前本进程尚未创建任何新圈，因此现存 running 全部属于已终止的旧
    /// Writer/进程。无论是 Watchdog、人工重启还是普通重新打开项目，都必须立即
    /// 收口；保留 running 不会增加证据，只会制造永久孤儿和错误恢复基线。
    /// </summary>
    private void RecoverInterruptedCyclesOnStartup()
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET status='aborted_on_startup',
       end_time=CASE
           WHEN end_time IS NOT NULL THEN end_time
           WHEN julianday(@now) < julianday(start_time) THEN start_time
           ELSE @now
       END
 WHERE status='running'
;";
            cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Watchdog 恢复新进程在开始学习前调用。此时尚未创建本进程圈，数据库中全部
    /// running 记录均属于被终止旧进程；一次 UPDATE 保证每圈只得到一个作废终态。
    /// </summary>
    public int AbortInterruptedCyclesForSoftwareRecovery(DateTime recoveryUtc)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
UPDATE {TABLE_CYCLES}
   SET status='AbortedBySoftwareRecovery',
       end_time=CASE
           WHEN end_time IS NOT NULL THEN end_time
           WHEN julianday(@now) < julianday(start_time) THEN start_time
           ELSE @now
       END
 WHERE status='running';";
            cmd.Parameters.AddWithValue("@now", recoveryUtc.ToLocalTime().ToString("o"));
            return cmd.ExecuteNonQuery();
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
   SET sample_count=CASE
           WHEN @n > COALESCE(sample_count, 0) THEN @n
           ELSE COALESCE(sample_count, 0)
       END,
       end_time=CASE
           WHEN julianday(@et) < julianday(start_time) THEN start_time
           WHEN end_time IS NULL OR julianday(@et) > julianday(end_time) THEN @et
           ELSE end_time
       END,
       status=@status
 WHERE epb_id=@e AND cycle_number=@c AND status='running'";
            cmd.Parameters.AddWithValue("@n", sampleCount);
            cmd.Parameters.AddWithValue("@et", endUtc.ToLocalTime().ToString("o"));
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@e", epbId);
            cmd.Parameters.AddWithValue("@c", cycleNumber);
            var affected = cmd.ExecuteNonQuery();
            if (affected == 1) return;

            using var inspect = _conn.CreateCommand();
            inspect.Transaction = _activeBatchTransaction;
            inspect.CommandText = $@"
SELECT status FROM {TABLE_CYCLES}
 WHERE epb_id=@e AND cycle_number=@c
 LIMIT 1";
            inspect.Parameters.AddWithValue("@e", epbId);
            inspect.Parameters.AddWithValue("@c", cycleNumber);
            var existing = Convert.ToString(inspect.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing, status, StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(existing) &&
                !string.Equals(existing, "running", StringComparison.OrdinalIgnoreCase))
                return;
            throw new InvalidOperationException(
                $"圈状态迁移未命中running唯一记录。EPB={epbId} Cycle={cycleNumber} " +
                $"Requested={status} Existing={existing ?? "Missing"} Affected={affected}。");
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

    private List<CycleInfo> GetLatestCyclesForStop(
        int epbId,
        int latestN,
        int requiredTerminalCycleNumber)
    {
        lock (_dbGate)
        {
            latestN = Math.Max(1, latestN);
            var list = new List<CycleInfo>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"
SELECT epb_id, cycle_number, start_time, end_time, start_position, sample_count, status
  FROM {TABLE_CYCLES}
 WHERE epb_id=@e
   AND cycle_number > 0
   AND (status IN ('completed','alarm')
        OR (cycle_number=@required AND status <> 'running'))
 ORDER BY cycle_number DESC
 LIMIT @n";
            cmd.Parameters.AddWithValue("@e", epbId);
            cmd.Parameters.AddWithValue("@required", requiredTerminalCycleNumber);
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
            list.Sort((a, b) => a.CycleNumber.CompareTo(b.CycleNumber));
            return list;
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
    /// <summary>实际生成/校验的格式：CsvOnly、BinOnly 或 CsvAndBin。</summary>
    public string StorageFormat { get; set; }
    public string FinalStatus { get; set; }
    public string ValidationError { get; set; }
    public string SampleClockDevice { get; set; }
    public long SampleClockGeneration { get; set; }
    public long CycleStartAfterSequence { get; set; }
    public long CycleEndSequence { get; set; }
    public long LastWrittenSequence { get; set; }
    public int PreTriggerSampleCount { get; set; }
    public int RequiredPreTriggerSampleCount { get; set; }
    public bool SemanticEvidenceComplete { get; set; }
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

/// <summary>
/// 内建 Recorder 的权威 DAQ 圈边界扩展。UTC 仅作审计；样本归属由
/// device/generation/sequence 决定，并保证圈头包含未上电预触发样本。
/// </summary>
public interface ISequencedEpbCycleRecorder : ICountedBatchedEpbCycleRecorder
{
    void BeginCycleAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime startUtc,
        string device,
        long generation,
        long startAfterSequence);

    void WriteDeviceBatch(
        string device,
        long generation,
        long sequence,
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount);

    void SealCycleWindowAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime endUtc,
        string device,
        long generation,
        long endSequence);
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

public interface ICycleAttemptEvidenceExporter
{
    CycleSnapshotEvidence ExportCycleAttemptTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin);
}

/// <summary>
/// 停止流程使用的可选扩展：把已封口但可能不完整的当前圈纳入最近圈证据包。
/// </summary>
public interface IStopRecentCycleEvidenceExporter
{
    void FlushRecentForStop(int epbId, int lastNCycles, int interruptedCycleNumber);
}

/// <summary>
/// Optional alarm-only recent-cycle exporter. Legacy recorders remain pair-format;
/// the disk writer adapter can apply the independent AlarmStorageLevel policy.
/// </summary>
public interface IAlarmRecentCycleEvidenceExporter
{
    void FlushRecentForAlarm(
        int epbId,
        int lastNCycles,
        string exportDir,
        bool includeRunningCycle);
}

/// <summary>
/// 报警触发圈已经被正式圈收尾先行封存时，从不可变的圈级索引回读 CSV/BIN。
/// 该路径只处理“未取得当前圈封存权”的竞态；真正的原子封存失败不得被回读掩盖。
/// </summary>
public static class AlarmCycleSnapshotRecovery
{
    public static AlarmCycleSnapshotEvidence TryExportFinalizedCycle(
        IEpbCycleRecorder recorder,
        int epbId,
        int cycleNumber,
        string exportDir,
        AlarmCycleSnapshotEvidence originalEvidence)
    {
        if (originalEvidence?.IsValid == true || originalEvidence?.WasClaimed == true)
            return originalEvidence;
        if (!(recorder is ICycleAttemptEvidenceExporter exporter))
            return originalEvidence;

        try
        {
            var storage = ParseStorageFormat(originalEvidence?.StorageFormat);
            var saveCsv = storage == StorageFormatLevel.CsvOnly ||
                          storage == StorageFormatLevel.CsvAndBin;
            var saveBin = storage == StorageFormatLevel.BinOnly ||
                          storage == StorageFormatLevel.CsvAndBin;
            var persisted = exporter.ExportCycleAttemptTo(
                epbId,
                cycleNumber,
                exportDir,
                saveCsv,
                saveBin);
            var recovered = EpbDiskWriter.ValidateAlarmCycleSnapshotFiles(
                persisted.CsvPath,
                persisted.BinPath,
                epbId,
                cycleNumber,
                false,
                storage);
            recovered.WasClaimed = false;
            recovered.FinalStatus = persisted.IsCompleteCycle
                ? "CompletedTriggerCycle"
                : "FinalizedAttempt";
            if (!recovered.IsValid && originalEvidence != null &&
                !string.IsNullOrWhiteSpace(originalEvidence.ValidationError))
            {
                recovered.ValidationError = originalEvidence.ValidationError +
                                            "；持久化回读校验失败：" +
                                            recovered.ValidationError;
            }
            return recovered;
        }
        catch (Exception ex)
        {
            var evidence = originalEvidence ?? new AlarmCycleSnapshotEvidence();
            var prefix = string.IsNullOrWhiteSpace(evidence.ValidationError)
                ? string.Empty
                : evidence.ValidationError + "；";
            evidence.ValidationError = prefix + "持久化终态圈回读失败：" + ex.Message;
            return evidence;
        }
    }

    private static StorageFormatLevel ParseStorageFormat(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse(value, true, out StorageFormatLevel parsed) &&
            Enum.IsDefined(typeof(StorageFormatLevel), parsed))
            return parsed;
        // A missing field denotes a legacy evidence object and therefore retains
        // the old CSV+BIN recovery contract.
        return StorageFormatLevel.CsvAndBin;
    }
}

public interface IActiveCycleLimitConfigurator
{
    void SetMaxActiveCycleRecords(int maxRecords);
}

/// <summary>
/// 写盘器的可选进程内自愈契约。仅用于重建可恢复的存储视图，不得吞掉数据或真实磁盘故障。
/// </summary>
public interface IRecoverableCycleRecorder
{
    bool TryRecoverStorage(Exception cause, out string detail);
}

/// <summary>可选的机械完成事实持久化能力；旧测试记录器无需实现。</summary>
public interface IMechanicalCycleRecorder
{
    void MarkMechanicalCycleCompleted(int epbId, int cycleNumber, DateTime completedUtc);
    long GetMechanicalCycleCompletedCount(int epbId);
    DateTime? GetLastMechanicalCycleCompletedUtc(int epbId);
}

/// <summary>
///     将 EpbDiskWriter 适配为 IEpbCycleRecorder，避免 EpbManager 直接依赖具体类。
/// </summary>
public sealed class DiskWriterRecorderAdapter : IEpbCycleRecorder, ISequencedEpbCycleRecorder, ICycleEvidenceExporter, ICycleAttemptEvidenceExporter, IStopRecentCycleEvidenceExporter, IAlarmRecentCycleEvidenceExporter, IActiveCycleLimitConfigurator, IRecoverableCycleRecorder, IMechanicalCycleRecorder
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

    public bool TryRecoverStorage(Exception cause, out string detail)
        => _writer.TryRecoverStorageMappings(cause, out detail);

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

    public void BeginCycleAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime startUtc,
        string device,
        long generation,
        long startAfterSequence)
        => _writer.BeginCycleAtDaqBoundary(
            epbId,
            cycleNumber,
            startUtc,
            device,
            generation,
            startAfterSequence);

    public void SealCycleWindowAtDaqBoundary(
        int epbId,
        int cycleNumber,
        DateTime endUtc,
        string device,
        long generation,
        long endSequence)
        => _writer.SealCycleWindowAtDaqBoundary(
            epbId,
            cycleNumber,
            endUtc,
            device,
            generation,
            endSequence);

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

    public void WriteDeviceBatch(
        string device,
        long generation,
        long sequence,
        DateTime[] timestampsUtc,
        EpbChannelDiskBatch[] channels,
        int channelCount,
        int sampleCount)
        => _writer.WriteDeviceBatch(
            device,
            generation,
            sequence,
            timestampsUtc,
            channels,
            channelCount,
            sampleCount);

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
        => _writer.ExportLatestCyclesOnceNow(epbId, Math.Max(1, lastNCycles));

    public void FlushRecentForStop(int epbId, int lastNCycles, int interruptedCycleNumber)
        => _writer.ExportLatestCyclesForStopNow(
            epbId,
            Math.Max(1, lastNCycles),
            interruptedCycleNumber);

    public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle)
        => _writer.ExportLatestCyclesTo(epbId, Math.Max(1, lastNCycles), exportDir, includeRunningCycle);

    public void FlushRecentForAlarm(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle)
        => _writer.ExportLatestCyclesTo(
            epbId,
            Math.Max(1, lastNCycles),
            exportDir,
            includeRunningCycle,
            _writer.AlarmStorageLevel);

    public CycleSnapshotEvidence ExportCompletedCycleTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin)
        => _writer.ExportCompletedCycleTo(epbId, cycleNumber, exportDir, saveCsv, saveBin);

    public CycleSnapshotEvidence ExportCycleAttemptTo(
        int epbId,
        int cycleNumber,
        string exportDir,
        bool saveCsv,
        bool saveBin)
        => _writer.ExportCycleAttemptTo(epbId, cycleNumber, exportDir, saveCsv, saveBin);

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

    public void MarkMechanicalCycleCompleted(int epbId, int cycleNumber, DateTime completedUtc)
        => _writer.MarkMechanicalCycleCompleted(epbId, cycleNumber, completedUtc);

    public long GetMechanicalCycleCompletedCount(int epbId)
        => _writer.GetMechanicalCycleCompletedCount(epbId);

    public DateTime? GetLastMechanicalCycleCompletedUtc(int epbId)
        => _writer.GetLastMechanicalCycleCompletedUtc(epbId);


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
