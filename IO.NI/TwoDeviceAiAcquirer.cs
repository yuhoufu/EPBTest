using System;
using System.Buffers;
using System.CodeDom;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Config;
using DataOperation;
using NationalInstruments.DAQmx;
using NIDaqTask = NationalInstruments.DAQmx.Task;
using Task = System.Threading.Tasks.Task;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;
using static DataOperation.ClsDataFilter;
using System.Threading.Tasks;

namespace IO.NI
{
    public sealed class DaqDiagnosticsCapture
    {
        internal DaqTimingValue[] Records { get; set; } = Array.Empty<DaqTimingValue>();

        public string[] WriteTo(string directory)
        {
            Directory.CreateDirectory(directory);
            var timingPath = Path.Combine(directory, "daq_timing.csv");
            using (var writer = new StreamWriter(timingPath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("TimestampUtc,Device,Kind,Generation,BatchSize,QueueDepth,CallbackIntervalMs,EndReadMs,RearmMs,QueueAgeMs,ProcessingMs,ConvertMs,FilterMs,PeakMs,DiskBatchBuildMs,DiskDispatchMs,UiNotifyMs,PersistenceWaitMs,RingWriteMs,SqliteMs,Detail,ControlQueueCapacity,SubscriberMaxMs,ProcessedSampleUtc,DriftMs,BatchSequence,ProducerThreadId,ProducerReentryCount,SampleLeadMs,EffectiveSampleRateHz,EstimatedSkewPpm,ClockResidualMs,ClockWindowSeconds,ClockCorrectionPpm,ClockState,QualityFlags");
                foreach (var x in Records.Where(x => !string.Equals(x.Kind, "Runtime", StringComparison.OrdinalIgnoreCase)))
                    writer.WriteLine(
                        $"{x.TimestampUtc:O},{Csv(x.Device)},{Csv(x.Kind)},{x.Generation},{x.BatchSize},{x.QueueDepth}," +
                        $"{x.CallbackIntervalMs:F3},{x.EndReadMs:F3},{x.RearmMs:F3},{x.QueueAgeMs:F3},{x.ProcessingMs:F3}," +
                        $"{x.ConvertMs:F3},{x.FilterMs:F3},{x.PeakMs:F3},{x.DiskBatchBuildMs:F3},{x.DiskDispatchMs:F3}," +
                        $"{x.UiNotifyMs:F3},{x.PersistenceWaitMs:F3},{x.RingWriteMs:F3},{x.SqliteMs:F3},{Csv(x.Detail)}," +
                        $"{x.ControlQueueCapacity},{x.SubscriberMaxMs:F3},{(x.ProcessedSampleUtc == default ? string.Empty : x.ProcessedSampleUtc.ToString("O"))},{x.DriftMs:F3}," +
                        $"{x.BatchSequence},{x.ProducerThreadId},{x.ProducerReentryCount},{x.SampleLeadMs:F3}," +
                        $"{x.EffectiveSampleRateHz:F6},{x.EstimatedSkewPpm:F3},{x.ClockResidualMs:F3}," +
                        $"{x.ClockWindowSeconds:F3},{x.ClockCorrectionPpm:F3},{Csv(x.ClockState)},{Csv(x.QualityFlags)}");
            }
            var runtimePath = Path.Combine(directory, "daq_runtime.csv");
            using (var writer = new StreamWriter(runtimePath, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("TimestampUtc,Device,Kind,GC0,GC1,GC2,ManagedMemoryBytes,WorkerThreadsAvailable,IoThreadsAvailable,GcEtwStatus,GcPauseDurationMs,GcPauseUtc,GcPauseCount");
                foreach (var x in Records.Where(x => string.Equals(x.Kind, "Runtime", StringComparison.OrdinalIgnoreCase)))
                    writer.WriteLine(
                        $"{x.TimestampUtc:O},{Csv(x.Device)},{Csv(x.Kind)},{x.Gc0},{x.Gc1},{x.Gc2}," +
                        $"{x.ManagedMemoryBytes},{x.WorkerThreadsAvailable},{x.IoThreadsAvailable}," +
                        $"{Csv(x.GcEtwStatus)},{x.GcPauseDurationMs:F3}," +
                        $"{(x.GcPauseUtc == default ? string.Empty : x.GcPauseUtc.ToString("O"))},{x.GcPauseCount}");
            }
            return new[] { timingPath, runtimePath };
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }
    }

    internal static class DaqQueueAdmission
    {
        internal static bool TryEnter(ref int count, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (Interlocked.Increment(ref count) <= capacity) return true;
            Interlocked.Decrement(ref count);
            return false;
        }

        internal static void Release(ref int count)
        {
            Interlocked.Decrement(ref count);
        }
    }

    /// <summary>带新鲜度证据的压力快照。</summary>
    public readonly struct PressureSample
    {
        public PressureSample(int hydraulicId, double valueBar, DateTime timestampUtc, long monotonicTicks)
        {
            HydraulicId = hydraulicId;
            ValueBar = valueBar;
            TimestampUtc = timestampUtc.Kind == DateTimeKind.Utc
                ? timestampUtc
                : timestampUtc.ToUniversalTime();
            MonotonicTicks = monotonicTicks;
        }

        public int HydraulicId { get; }
        public double ValueBar { get; }
        public DateTime TimestampUtc { get; }
        public long MonotonicTicks { get; }
        public bool IsFinite => !double.IsNaN(ValueBar) && !double.IsInfinity(ValueBar);
        public double AgeMs => MonotonicTicks <= 0
            ? double.PositiveInfinity
            : (Stopwatch.GetTimestamp() - MonotonicTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public sealed class DaqFreshnessSnapshot
    {
        public string Device { get; set; } = string.Empty;
        public long Generation { get; set; }
        public long LastProducedSequence { get; set; }
        public long LastProcessedSequence { get; set; }
        public long ControlDiscontinuityCount { get; set; }
        public long LastControlDiscontinuitySequence { get; set; }
        public ClockState ClockState { get; set; }
        public double EffectiveSampleRateHz { get; set; }
        public double EstimatedSkewPpm { get; set; }
        public double ClockResidualMs { get; set; }
        /// <summary>兼容字段；V2.10 起等同于 LastControlProcessedMonotonicTicks。</summary>
        public long LastArrivalMonotonicTicks { get; set; }
        public double AgeMs { get; set; }
        public bool IsFresh { get; set; }
        public long LastCallbackMonotonicTicks { get; set; }
        public long LastControlEnqueuedMonotonicTicks { get; set; }
        public long LastControlProcessedMonotonicTicks { get; set; }
        public double CallbackAgeMs { get; set; }
        public double ControlEnqueueAgeMs { get; set; }
        public double ControlProcessedAgeMs { get; set; }
        public DateTime ProcessedSampleUtc { get; set; }
    }

    /// <summary>DAQ 回调入队后到后台开始处理的积压证据。</summary>
    public sealed class DaqProcessingSnapshot
    {
        public string Device { get; set; } = string.Empty;
        public int QueueDepth { get; set; }
        public double OldestBatchAgeMs { get; set; }
        public DateTime ObservedUtc { get; set; }
    }

    public sealed class DaqControlSnapshot
    {
        internal DaqControlSnapshot(
            string device,
            string queueType,
            long generation,
            int queueDepth,
            int queueCapacity,
            double oldestBatchAgeMs,
            double lastBatchProcessMs,
            double subscriberMaxMs,
            DateTime processedSampleUtc,
            DateTime observedUtc)
        {
            Device = device;
            QueueType = queueType;
            Generation = generation;
            QueueDepth = queueDepth;
            QueueCapacity = queueCapacity;
            OldestBatchAgeMs = oldestBatchAgeMs;
            LastBatchProcessMs = lastBatchProcessMs;
            SubscriberMaxMs = subscriberMaxMs;
            ProcessedSampleUtc = processedSampleUtc;
            ObservedUtc = observedUtc;
        }

        public string Device { get; }
        public string QueueType { get; }
        public long Generation { get; }
        public int QueueDepth { get; }
        public int QueueCapacity { get; }
        public double OldestBatchAgeMs { get; }
        public double LastBatchProcessMs { get; }
        public double SubscriberMaxMs { get; }
        public DateTime ProcessedSampleUtc { get; }
        public DateTime ObservedUtc { get; }
    }

    public sealed class DaqRecoveryResult
    {
        public string Device { get; set; } = string.Empty;
        public bool Recovered { get; set; }
        public long PreviousGeneration { get; set; }
        public long RecoveredGeneration { get; set; }
        public long FirstVerifiedSequence { get; set; }
        public long LastVerifiedSequence { get; set; }
        public int FreshCallbacks { get; set; }
        public int RequiredFreshCallbacks { get; set; }
        public int ElapsedMs { get; set; }
        public string FailureReason { get; set; } = string.Empty;
        public string FailureKind { get; set; } = string.Empty;
        public int? NativeErrorCode { get; set; }
        public FaultClassification Classification { get; set; } = FaultClassification.SoftwareTransient;
        public HardwareEvidence[] HardwareEvidence { get; set; } = Array.Empty<HardwareEvidence>();
    }

    public sealed class DaqDeviceFault
    {
        public string Device { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public long Generation { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string QueueKind { get; set; } = string.Empty;
        public string QueueType { get; set; } = string.Empty;
        public int QueueDepth { get; set; }
        public int QueueCapacity { get; set; }
        public double OldestBatchAgeMs { get; set; }
        public DateTime LastProcessedSampleUtc { get; set; }
        public int? NativeErrorCode { get; set; }
        public FaultClassification Classification { get; set; } = FaultClassification.SoftwareTransient;
    }

    public sealed class DaqTimingRecord
    {
        public DateTime TimestampUtc { get; set; }
        public string Device { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public long Generation { get; set; }
        public int BatchSize { get; set; }
        public int QueueDepth { get; set; }
        public double CallbackIntervalMs { get; set; }
        public double EndReadMs { get; set; }
        public double RearmMs { get; set; }
        public double QueueAgeMs { get; set; }
        public double ProcessingMs { get; set; }
        public double ConvertMs { get; set; }
        public double FilterMs { get; set; }
        public double PeakMs { get; set; }
        public double DiskBatchBuildMs { get; set; }
        public double DiskDispatchMs { get; set; }
        public double UiNotifyMs { get; set; }
        public double PersistenceWaitMs { get; set; }
        public double RingWriteMs { get; set; }
        public double SqliteMs { get; set; }
        public int Gc0 { get; set; }
        public int Gc1 { get; set; }
        public int Gc2 { get; set; }
        public long ManagedMemoryBytes { get; set; }
        public int WorkerThreadsAvailable { get; set; }
        public int IoThreadsAvailable { get; set; }
        public string GcEtwStatus { get; set; } = string.Empty;
        public double GcPauseDurationMs { get; set; }
        public DateTime GcPauseUtc { get; set; }
        public long GcPauseCount { get; set; }
        public int ControlQueueCapacity { get; set; }
        public double SubscriberMaxMs { get; set; }
        public double DriftMs { get; set; }
        public DateTime ProcessedSampleUtc { get; set; }
        public long BatchSequence { get; set; }
        public int ProducerThreadId { get; set; }
        public long ProducerReentryCount { get; set; }
        public double SampleLeadMs { get; set; }
        public double EffectiveSampleRateHz { get; set; }
        public double EstimatedSkewPpm { get; set; }
        public double ClockResidualMs { get; set; }
        public double ClockWindowSeconds { get; set; }
        public double ClockCorrectionPpm { get; set; }
        public string ClockState { get; set; } = string.Empty;
        public string QualityFlags { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    public readonly struct DaqDiskChannelBatch
    {
        internal DaqDiskChannelBatch(int epbId, double[] currents)
        {
            EpbId = epbId;
            Currents = currents;
        }

        public int EpbId { get; }
        public double[] Currents { get; }
    }

    /// <summary>
    /// 独占所有权的持久化批次。订阅者必须在消费完成或拒绝批次时调用 Dispose。
    /// </summary>
    public sealed class DaqDiskBatch : IDisposable
    {
        private const int MaximumPooledBatchObjects = 1024;
        private static readonly ConcurrentBag<DaqDiskBatch> ObjectPool = new();
        private static int _pooledBatchObjectCount;
        private int _disposed;
        private bool _channelsArrayPooled;
        private bool _returnObjectToPool;

        private DaqDiskBatch()
        {
        }

        internal DaqDiskBatch(
            string device,
            long generation,
            long sequence,
            int sampleCount,
            DateTime[] timestampsUtc,
            DaqDiskChannelBatch[] channels,
            double[] pressureGroup1,
            double[] pressureGroup2,
            long enqueuedMonotonicTicks)
        {
            Initialize(
                device,
                generation,
                sequence,
                sampleCount,
                timestampsUtc,
                channels,
                channels?.Length ?? 0,
                pressureGroup1,
                pressureGroup2,
                enqueuedMonotonicTicks,
                0,
                ClockState.WarmingUp,
                0,
                0,
                0,
                channelsArrayPooled: false,
                returnObjectToPool: false);
        }

        internal static DaqDiskBatch Rent(
            string device,
            long generation,
            long sequence,
            int sampleCount,
            DateTime[] timestampsUtc,
            DaqDiskChannelBatch[] channels,
            int channelCount,
            double[] pressureGroup1,
            double[] pressureGroup2,
            long enqueuedMonotonicTicks)
        {
            return Rent(
                device,
                generation,
                sequence,
                sampleCount,
                timestampsUtc,
                channels,
                channelCount,
                pressureGroup1,
                pressureGroup2,
                enqueuedMonotonicTicks,
                0,
                ClockState.WarmingUp,
                0,
                0,
                0);
        }

        internal static DaqDiskBatch Rent(
            string device,
            long generation,
            long sequence,
            int sampleCount,
            DateTime[] timestampsUtc,
            DaqDiskChannelBatch[] channels,
            int channelCount,
            double[] pressureGroup1,
            double[] pressureGroup2,
            long enqueuedMonotonicTicks,
            double effectiveSampleRateHz,
            ClockState clockState,
            double estimatedSkewPpm,
            double clockResidualMs,
            double clockWindowSeconds)
        {
            if (!ObjectPool.TryTake(out var batch))
                batch = new DaqDiskBatch();
            else
                Interlocked.Decrement(ref _pooledBatchObjectCount);
            batch.Initialize(
                device,
                generation,
                sequence,
                sampleCount,
                timestampsUtc,
                channels,
                channelCount,
                pressureGroup1,
                pressureGroup2,
                enqueuedMonotonicTicks,
                effectiveSampleRateHz,
                clockState,
                estimatedSkewPpm,
                clockResidualMs,
                clockWindowSeconds,
                channelsArrayPooled: true,
                returnObjectToPool: true);
            return batch;
        }

        private void Initialize(
            string device,
            long generation,
            long sequence,
            int sampleCount,
            DateTime[] timestampsUtc,
            DaqDiskChannelBatch[] channels,
            int channelCount,
            double[] pressureGroup1,
            double[] pressureGroup2,
            long enqueuedMonotonicTicks,
            double effectiveSampleRateHz,
            ClockState clockState,
            double estimatedSkewPpm,
            double clockResidualMs,
            double clockWindowSeconds,
            bool channelsArrayPooled,
            bool returnObjectToPool)
        {
            Volatile.Write(ref _disposed, 0);
            Device = device;
            Generation = generation;
            Sequence = sequence;
            SampleCount = sampleCount;
            TimestampsUtc = timestampsUtc;
            Channels = channels ?? Array.Empty<DaqDiskChannelBatch>();
            ChannelCount = Math.Max(0, Math.Min(channelCount, Channels.Length));
            PressureGroup1 = pressureGroup1;
            PressureGroup2 = pressureGroup2;
            EnqueuedMonotonicTicks = enqueuedMonotonicTicks;
            EnqueuedUtc = DateTime.UtcNow;
            EffectiveSampleRateHz = effectiveSampleRateHz;
            ClockState = clockState;
            EstimatedSkewPpm = estimatedSkewPpm;
            ClockResidualMs = clockResidualMs;
            ClockWindowSeconds = clockWindowSeconds;
            _channelsArrayPooled = channelsArrayPooled;
            _returnObjectToPool = returnObjectToPool;
        }

        public string Device { get; private set; }
        public long Generation { get; private set; }
        public long Sequence { get; private set; }
        public int SampleCount { get; private set; }
        public DateTime[] TimestampsUtc { get; private set; }
        public DaqDiskChannelBatch[] Channels { get; private set; }
        public int ChannelCount { get; private set; }
        public double[] PressureGroup1 { get; private set; }
        public double[] PressureGroup2 { get; private set; }
        public long EnqueuedMonotonicTicks { get; private set; }
        public DateTime EnqueuedUtc { get; private set; }
        public double EffectiveSampleRateHz { get; private set; }
        public ClockState ClockState { get; private set; }
        public double EstimatedSkewPpm { get; private set; }
        public double ClockResidualMs { get; private set; }
        public double ClockWindowSeconds { get; private set; }

        public double AgeMs => EnqueuedMonotonicTicks <= 0
            ? 0
            : (Stopwatch.GetTimestamp() - EnqueuedMonotonicTicks) * 1000.0 / Stopwatch.Frequency;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (TimestampsUtc != null) ArrayPool<DateTime>.Shared.Return(TimestampsUtc, clearArray: false);
            for (var i = 0; i < ChannelCount; i++)
            {
                if (Channels[i].Currents != null)
                    ArrayPool<double>.Shared.Return(Channels[i].Currents, clearArray: false);
                Channels[i] = default;
            }
            if (PressureGroup1 != null) ArrayPool<double>.Shared.Return(PressureGroup1, clearArray: false);
            if (PressureGroup2 != null) ArrayPool<double>.Shared.Return(PressureGroup2, clearArray: false);
            if (_channelsArrayPooled && Channels.Length > 0)
                ArrayPool<DaqDiskChannelBatch>.Shared.Return(Channels, clearArray: false);

            Device = null;
            TimestampsUtc = null;
            Channels = Array.Empty<DaqDiskChannelBatch>();
            ChannelCount = 0;
            PressureGroup1 = null;
            PressureGroup2 = null;
            EffectiveSampleRateHz = 0;
            ClockState = ClockState.WarmingUp;
            EstimatedSkewPpm = 0;
            ClockResidualMs = 0;
            ClockWindowSeconds = 0;
            var returnObjectToPool = _returnObjectToPool;
            _channelsArrayPooled = false;
            _returnObjectToPool = false;
            if (!returnObjectToPool) return;
            if (Interlocked.Increment(ref _pooledBatchObjectCount) <= MaximumPooledBatchObjects)
                ObjectPool.Add(this);
            else
                Interlocked.Decrement(ref _pooledBatchObjectCount);
        }
    }

    public sealed class PeakCaptureToken
    {
        public Guid CaptureId { get; set; }
        public Guid TestRunId { get; set; }
        public int Channel { get; set; }
        public int CycleNumber { get; set; }
        public DateTime StartUtc { get; set; }
    }

    public sealed class PeakCaptureResult
    {
        public PeakCaptureToken Token { get; set; }
        public TwoDeviceAiAcquirer.EpbCurrentPeak Peak { get; set; }
        public bool IsMatched { get; set; }
        /// <summary>本次证据窗被冻结的逻辑截止时刻（UTC）。</summary>
        public DateTime LogicalCutoffUtc { get; set; }
        /// <summary>全速率处理线程已经处理到的样本水印（UTC）。</summary>
        public DateTime ProcessedThroughUtc { get; set; }
        /// <summary>封口等待结束时刻（UTC）。</summary>
        public DateTime DrainCompletedUtc { get; set; }
        /// <summary>等待在途全速率样本覆盖逻辑截止点所花的墙钟时间，仅用于诊断。</summary>
        public double DrainElapsedMs { get; set; }
        /// <summary>全速率处理水印是否已经越过逻辑截止点。</summary>
        public bool IsCutoffCovered { get; set; }
        public string QualityReason { get; set; } = string.Empty;
    }

    /// <summary>
    ///     双设备（Dev1/Dev2）AI 连续采样管理器：
    ///     - DAQ 回调线程提供低时延的“最后一个样本”快速工程值（未滤波），并写入 _lastFastValue；
    ///     - 后台处理线程负责将整批数据转换为工程值并滤波，然后写入 _lastFilteredValue；
    ///     - 提供明确的读取接口以区分快/慢数据的用途（控制用 vs UI/统计用）。
    /// </summary>
    public sealed class TwoDeviceAiAcquirer : IDisposable
    {
        // —— 对接控制逻辑 —— //
        public delegate double ReadCurrentDelegate(int epbChannel);

        // 参数名 -> 本设备内列索引
        private readonly Dictionary<string, int> _colIndexDev1 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _colIndexDev2 = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new();
        private readonly string[] _dev1Channels, _dev2Channels;
        private readonly List<AiConfigDetailRecord> _enabled;

        // 最近的工程值快照（拆成两份以避免语义冲突）
        // 1) 低时延快照（在 DAQ 回调线程中写入）：未滤波、用于控制逻辑/紧急读数
        private readonly ConcurrentDictionary<string, double> _lastFastValue = new();
        private readonly ConcurrentDictionary<int, FastEpbCurrentSample> _lastFastEpbSample = new();

        // 2) 滤波后快照（在后台线程中写入）：已滤波、用于 UI / 统计 / 报表
        private readonly ConcurrentDictionary<string, double> _lastFilteredValue = new();
        private readonly ConcurrentDictionary<string, PressureSample> _lastPressureSample = new();

        private readonly ILogger _log;
        private readonly int _medianLens;

        // 两块采集卡分别处理，避免任一设备的滤波/落盘/UI订阅拖住另一块卡。
        private readonly ConcurrentQueue<Item> _queueDev1 = new();
        private readonly ConcurrentQueue<Item> _queueDev2 = new();
        private readonly SemaphoreSlim _queueSignalDev1 = new(0);
        private readonly SemaphoreSlim _queueSignalDev2 = new(0);
        private readonly ControlBatchRing _controlRingDev1;
        private readonly ControlBatchRing _controlRingDev2;
        private readonly AutoResetEvent _controlSignalDev1 = new(false);
        private readonly AutoResetEvent _controlSignalDev2 = new(false);
        private readonly DaqDiagnosticRing _timingDiagnostics = new(DiagnosticCapacity);
        private readonly int _processingQueueCapacity;
        private const int DiagnosticCapacity = 12000;
        private int _queueCountDev1;
        private int _queueCountDev2;
        private long _queueFaultGenerationDev1 = -1;
        private long _queueFaultGenerationDev2 = -1;
        private long _controlFullFaultGenerationDev1 = -1;
        private long _controlFullFaultGenerationDev2 = -1;
        private long _controlLatencyFaultGenerationDev1 = -1;
        private long _controlLatencyFaultGenerationDev2 = -1;
        private long _controlInvariantFaultGenerationDev1 = -1;
        private long _controlInvariantFaultGenerationDev2 = -1;
        private long _clockFaultGenerationDev1 = -1;
        private long _clockFaultGenerationDev2 = -1;
        private long _controlFilterResetEpochDev1;
        private long _controlFilterResetEpochDev2;
        private long _lastProcessingLagLogTicksDev1;
        private long _lastProcessingLagLogTicksDev2;
        private long _lastRuntimeProbeTicksDev1;
        private long _lastRuntimeProbeTicksDev2;
        private long _lastControlWarningTicksDev1;
        private long _lastControlWarningTicksDev2;
        private long _lastControlBatchProcessMsBitsDev1;
        private long _lastControlBatchProcessMsBitsDev2;
        private long _subscriberMaxMsBitsDev1;
        private long _subscriberMaxMsBitsDev2;
        private readonly int _controlQueueCapacity;
        private readonly double _controlWarningAgeMs;
        private readonly double _controlHardFaultAgeMs;
        private readonly ClockDisciplineOptions _clockDisciplineOptions;
        private readonly PeriodicDispatchGate _uiDispatchGateDev1;
        private readonly PeriodicDispatchGate _uiDispatchGateDev2;
        private Func<string, bool> _controlActivityProvider;
        private readonly double _sampleRate;
        private readonly int _samplesPerChannel;
        public double SampleRate => _sampleRate;

        // 时间戳（模仿 FrmMainMonitor）
        private readonly HighResolutionSampleClock _sampleClock = new();
        private readonly Task _workerDev1;
        private readonly Task _workerDev2;
        private readonly Task _rawPublicationWorker;
        private readonly Task _uiPublicationWorker;
        private static readonly Lazy<ClrGcPauseMonitor> SharedGcPauseMonitor =
            new Lazy<ClrGcPauseMonitor>(() => new ClrGcPauseMonitor(NLogger.Instance), true);
        private readonly ClrGcPauseMonitor _gcPauseMonitor;
        private readonly ConcurrentQueue<OwnedDaqRawBatch> _rawPublicationQueue = new();
        private readonly SemaphoreSlim _rawPublicationSignal = new(0);
        private readonly SemaphoreSlim _uiPublicationSignal = new(0);
        private const int RawPublicationCapacity = 256;
        private int _rawPublicationCount;
        private int _rawPublicationInFlight;
        private UiPublication _latestUiDev1;
        private UiPublication _latestUiDev2;
        private readonly Thread _controlThreadDev1;
        private readonly Thread _controlThreadDev2;
        private DateTime _lastTs = DateTime.Now;
        private AnalogMultiChannelReader _reader1, _reader2;
        private DeviceReadState _readState1, _readState2;
        private DateTime _t0 = DateTime.Now;


        // NI 任务
        private NIDaqTask _task1, _task2;
        private readonly object _taskGateDev1 = new();
        private readonly object _taskGateDev2 = new();
        private long _generationDev1;
        private long _generationDev2;
        private long _batchSequenceDev1;
        private long _batchSequenceDev2;
        private long _diskPublishedSequenceDev1;
        private long _diskPublishedSequenceDev2;
        private long _rawTransferredSequenceDev1;
        private long _rawTransferredSequenceDev2;
        private double _aiMin = -10;
        private double _aiMax = 10;
        private AITerminalConfiguration _terminalConfiguration = AITerminalConfiguration.Rse;
        private int _disposed;

        private sealed class UiPublication
        {
            internal string Device;
            internal double[,] Values;
            internal DateTime Current;
            internal DateTime Last;
        }

        // 配置排序只做一次。原实现每个回调/批次都 Where+OrderBy+ToList，
        // 在 200Hz 批处理下会形成持续 GC 压力。
        private readonly AiConfigDetailRecord[] _dev1Records;
        private readonly AiConfigDetailRecord[] _dev2Records;

        private sealed class DeviceReadState
        {
            public DeviceReadState(ClockDisciplineOptions clockOptions)
            {
                Timeline = new ClockDisciplinedSampleTimeline(clockOptions);
            }

            public string Device { get; set; }
            public long Generation { get; set; }
            public NIDaqTask Task { get; set; }
            public AnalogMultiChannelReader Reader { get; set; }
            public ManualResetEventSlim Quiesced { get; } = new(false);
            public ClockDisciplinedSampleTimeline Timeline { get; }
            public double NominalSampleRateHz { get; set; }
            public DaqCallbackProducerGate ProducerGate { get; } = new DaqCallbackProducerGate();
        }

        // 动态置零偏移（参数名 -> offset，工程值单位）
        private readonly ConcurrentDictionary<string, double> _zeroOffsets = new(StringComparer.OrdinalIgnoreCase);


        // 以设备处理矩阵 channels × samples 为例
        private ClsDataFilter.MedianStreamCausal _dev1MedianCausal;         // 因果，无延迟（控制/实时）
        private ClsDataFilter.MedianStreamCausal _dev2MedianCausal;         // 因果，无延迟（控制/实时）
        private ClsDataFilter.MedianStreamSymmetric _medianSymmetric;   // 对称，有延迟（显示/报表）

        // 你的配置：单侧点数（等同 UI 的 “Max. smoothing width on one side”）
        private readonly int _medianHalfWidth = 31;
        // 你的通道数（与 eng 矩阵第 0 维一致）
        private readonly int _channels;

        /// <summary>fast 分支的低时延滤波策略。</summary>
        private readonly FastFilter _fastFilterDev1 = new FastFilter(
            medianK: 9,      // 3 或 5，推荐 5
            ewmaAlpha: 0.4,  // 0.3~0.6 之间调
            maxSlewAperSec: 0 // 每秒最大电流变化（A/s），依硬件调
        );
        private readonly FastFilter _fastFilterDev2 = new FastFilter(
            medianK: 9,
            ewmaAlpha: 0.4,
            maxSlewAperSec: 0);
        private readonly FastCurrentEvidenceHistory _fastEvidence;


        /// <summary>
        ///     DAQ 回调节拍诊断状态（按设备维度）。
        /// </summary>
        private sealed class CallbackTimingDiag
        {
            /// <summary>上一次回调进入时刻（Stopwatch Tick）。</summary>
            public long LastCallbackEntrySwTick;

            /// <summary>上一次控制批成功入队时刻（Stopwatch Tick）。</summary>
            public long LastControlEnqueuedSwTick;

            /// <summary>上一次控制批完成所有订阅者处理时刻（Stopwatch Tick）。安全新鲜度只读取该值。</summary>
            public long LastSampleCommitSwTick;

            /// <summary>上一次完成处理的控制样本 UTC ticks。</summary>
            public long LastProcessedSampleUtcTicks;

            /// <summary>上一次输出诊断日志的时刻（Stopwatch Tick）。用于限频。</summary>
            public long LastLogSwTick;

            /// <summary>最近一次回调间隔（ms，double bits 形式存储以便原子读写）。</summary>
            public long LastCbIntervalMsBits;

            /// <summary>最近一次到达延迟（ms，double bits 形式存储以便原子读写）。</summary>
            public long LastArrivalDelayMsBits;

            /// <summary>
            /// 最近一次到达延迟（相对批首，ms，double bits 形式存储以便原子读写）。
            /// </summary>
            public long LastArrivalDelayToStartMsBits;

            /// <summary>最近一次批大小（每通道样本数）。</summary>
            public int LastBatchN;

            /// <summary>最近一次采样率（Hz，四舍五入）。</summary>
            public int LastFs;
            public long LastBatchSequence;
            public long LastProcessedSequence;
            public long ControlDiscontinuityCount;
            public long LastControlDiscontinuitySequence;
            public long LastGeneration;
            public long ProducerReentryCount;
            public long LastSampleLeadMsBits;
            public long EffectiveSampleRateHzBits;
            public long EstimatedSkewPpmBits;
            public long ClockResidualMsBits;
            public long ClockWindowSecondsBits;
            public long ClockCorrectionPpmBits;
            public int ClockState;
        }

        /// <summary>
        ///     DAQ 回调节拍诊断状态表：key 为设备名（如 Dev1/Dev2）。
        /// </summary>
        private readonly ConcurrentDictionary<string, CallbackTimingDiag> _callbackTimingDiag = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _recoveryGates = new();

        // EPB 通道 -> 设备（Dev1/Dev2）映射：用于把“触发时回调节拍”关联到具体 EPB 通道
        private readonly Dictionary<int, string> _epbChannelToDevice = new();


        /// <summary>
        ///     DAQ 回调节拍日志输出模式。
        /// </summary>
        private enum DaqTimingLogMode
        {
            /// <summary>不输出日志（默认）。仅保留最近一次节拍快照供断电同屏关联。</summary>
            Off,

            /// <summary>仅在节拍明显异常时输出（推荐用于现场排障）。</summary>
            AnomalyOnly,

            /// <summary>按限频输出全部节拍日志（仅限短时间定位）。</summary>
            All
        }

        /// <summary>
        ///     DAQ 回调节拍日志输出模式（来自 App.config appSettings）。
        ///     <para>key: DaqCallbackTimingLog，取值：off | anomaly | all（不区分大小写）。默认 anomaly。</para>
        /// </summary>
        private readonly DaqTimingLogMode _daqTimingLogMode;

        /// <summary>
        ///     DAQ 回调节拍日志的最小输出间隔（秒）。
        ///     <para>key: DaqCallbackTimingLogMinIntervalSec，默认 10 秒。</para>
        /// </summary>
        private readonly double _daqTimingLogMinIntervalSec;

        /// <summary>
        ///     DAQ 回调节拍“异常判定”阈值倍率（cbIntervalMs 超过 expectBatchMs * factor 认为异常）。
        ///     <para>key: DaqCallbackTimingAnomalyFactor，默认 1.5。</para>
        /// </summary>
        private readonly double _daqTimingAnomalyFactor;

        private static DaqTimingLogMode ParseDaqTimingLogMode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return DaqTimingLogMode.AnomalyOnly;
            s = s.Trim();
            if (s.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Equals("0")) return DaqTimingLogMode.Off;
            if (s.Equals("anomaly", StringComparison.OrdinalIgnoreCase) || s.Equals("warn", StringComparison.OrdinalIgnoreCase))
                return DaqTimingLogMode.AnomalyOnly;
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase) || s.Equals("1")) return DaqTimingLogMode.All;
            return DaqTimingLogMode.AnomalyOnly;
        }

        private static double ParseDoubleOrDefault(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            return double.TryParse(s.Trim(), out var v) &&
                   !double.IsNaN(v) &&
                   !double.IsInfinity(v)
                ? v
                : fallback;
        }

        private static string SafeGetAppSetting(string key)
        {
            try
            {
                return ConfigurationManager.AppSettings[key];
            }
            catch
            {
                return null;
            }
        }


        private void InitTimeBase()
        {
            _t0 = DateTime.Now;
            _sampleClock.Reset(_t0);
            _lastTs = _t0;
        }

        /// <summary>
        ///     记录 DAQ 回调的“批大小/回调间隔/到达延迟”诊断信息到 ErrorLog（限频）。
        /// </summary>
        /// <param name="device">设备标识（例如 "Dev1"/"Dev2"）。</param>
        /// <param name="batchSampleCount">本次回调 EndRead 得到的样本点数（每通道）。</param>
        /// <param name="batchTimeUtc">
        /// 本批数据对应的时间戳（UTC）。注意：该时间戳是本系统按采样率推进/纠偏得到的“数据时间”，
        /// 与“回调进入时刻”不同；二者差值可用于量化调度/缓冲造成的到达延迟。
        /// </param>
        /// <param name="arrivalUtc">回调进入时刻（UTC），用 <see cref="DateTime.UtcNow"/> 取得。</param>
        /// <param name="driftMs">主机实测时间与理想推进时间的偏差（ms）。用于观察 jitter/漂移。</param>
        /// <remarks>
        /// 设计约束：
        /// <list type="bullet">
        /// <item>回调线程必须尽可能轻量，避免影响下一批 BeginRead 的节拍；因此这里做“每设备每秒最多 1 条”限频。</item>
        /// <item>日志级别使用 Error，是为了进入 ErrorLog 文件，便于与当前“峰值打印”同屏对比。</item>
        /// </list>
        /// 输出字段解释：
        /// <list type="bullet">
        /// <item>期望批间隔(ms)≈N/Fs：硬下限，主要由 samplesPerChannel 决定；</item>
        /// <item>回调间隔(ms)：回调进入时刻之间的间隔，反映调度/阻塞/GC 影响；</item>
        /// <item>到达延迟(ms)=arrivalUtc-batchTimeUtc：反映“数据时间”到“处理到达”的滞后；</item>
        /// </list>
        /// </remarks>
        private long MarkCallbackEntry(string device, long callbackEntrySwTick)
        {
            var diag = _callbackTimingDiag.GetOrAdd(device, _ => new CallbackTimingDiag());
            return Interlocked.Exchange(ref diag.LastCallbackEntrySwTick, callbackEntrySwTick);
        }

        private void MarkControlEnqueued(string device, long enqueuedSwTick)
        {
            var diag = _callbackTimingDiag.GetOrAdd(device, _ => new CallbackTimingDiag());
            Interlocked.Exchange(ref diag.LastControlEnqueuedSwTick, enqueuedSwTick);
        }

        private void MarkControlProcessed(
            string device,
            long processedSwTick,
            DateTime sampleUtc,
            long generation,
            long sequence)
        {
            var diag = _callbackTimingDiag.GetOrAdd(device, _ => new CallbackTimingDiag());
            Interlocked.Exchange(ref diag.LastProcessedSampleUtcTicks, sampleUtc.ToUniversalTime().Ticks);
            Interlocked.Exchange(ref diag.LastGeneration, generation);
            Interlocked.Exchange(ref diag.LastProcessedSequence, sequence);
            Interlocked.Exchange(ref diag.LastSampleCommitSwTick, processedSwTick);
        }

        private void TryLogDaqCallbackTiming(string device, long generation, int batchSampleCount,
            DateTime batchTimeUtc, DateTime arrivalUtc, long callbackEntrySwTick,
            long previousCallbackEntrySwTick, double endReadMs, double rearmMs, double driftMs)
        {
            var diag = _callbackTimingDiag.GetOrAdd(device, _ => new CallbackTimingDiag());

            var expectBatchMs = batchSampleCount <= 0 ? 0 : (batchSampleCount * 1000.0 / _sampleRate);
            var cbIntervalMs = previousCallbackEntrySwTick == 0
                ? 0
                : (callbackEntrySwTick - previousCallbackEntrySwTick) * 1000.0 / Stopwatch.Frequency;

            // batchTimeUtc 是按采样率推进的“数据时间”（更接近批尾时刻）。拆成“相对批尾/相对批首”两种延迟，避免负数被误读。
            var batchEndUtc = batchTimeUtc;
            var batchStartUtc = batchSampleCount <= 0
                ? batchEndUtc
                : batchEndUtc.AddSeconds(-batchSampleCount / _sampleRate);

            var arrivalDelayToEndMs = (arrivalUtc - batchEndUtc).TotalMilliseconds;
            var arrivalDelayToStartMs = (arrivalUtc - batchStartUtc).TotalMilliseconds;

            // —— 保存“最近一次回调节拍”，供断电触发点/截断值日志同屏关联 ——
            Interlocked.Exchange(ref diag.LastCbIntervalMsBits, BitConverter.DoubleToInt64Bits(cbIntervalMs));
            Interlocked.Exchange(ref diag.LastArrivalDelayMsBits, BitConverter.DoubleToInt64Bits(arrivalDelayToEndMs));
            Interlocked.Exchange(ref diag.LastArrivalDelayToStartMsBits,
                BitConverter.DoubleToInt64Bits(arrivalDelayToStartMs));
            diag.LastBatchN = batchSampleCount;
            diag.LastFs = (int)Math.Round(_sampleRate);
            var queueDepth = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? Volatile.Read(ref _queueCountDev1)
                : Volatile.Read(ref _queueCountDev2);
            AppendDiagnostic(new DaqTimingValue
            {
                TimestampUtc = arrivalUtc,
                Device = device,
                Kind = "Callback",
                Generation = generation,
                BatchSize = batchSampleCount,
                QueueDepth = queueDepth,
                CallbackIntervalMs = cbIntervalMs,
                EndReadMs = endReadMs,
                RearmMs = rearmMs,
                DriftMs = driftMs,
                BatchSequence = Interlocked.Read(ref diag.LastBatchSequence),
                ProducerThreadId = Thread.CurrentThread.ManagedThreadId,
                ProducerReentryCount = Interlocked.Read(ref diag.ProducerReentryCount),
                SampleLeadMs = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.LastSampleLeadMsBits)),
                EffectiveSampleRateHz = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EffectiveSampleRateHzBits)),
                EstimatedSkewPpm = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EstimatedSkewPpmBits)),
                ClockResidualMs = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockResidualMsBits)),
                ClockWindowSeconds = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockWindowSecondsBits)),
                ClockCorrectionPpm = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockCorrectionPpmBits)),
                ClockState = ((ClockState)Volatile.Read(ref diag.ClockState)).ToString(),
                QualityFlags = FastSignalQualityFlags.None.ToString()
            });

            // 默认不输出节拍日志（测试期避免刷屏）；但仍保留最近一次快照
            if (_daqTimingLogMode == DaqTimingLogMode.Off) return;

            if (_daqTimingLogMode == DaqTimingLogMode.AnomalyOnly)
            {
                // 以“回调间隔”异常为主，辅以 drift 的绝对偏差（避免仅凭 arrivalDelay 误报）
                var expect = expectBatchMs;
                var cbTooSlow = expect > 0 && cbIntervalMs > expect * _daqTimingAnomalyFactor;
                var driftTooBig = Math.Abs(driftMs) > Math.Max(10.0, expect * 0.5);

                if (!cbTooSlow && !driftTooBig) return;
            }

            // 每设备每秒最多 1 条，避免刷屏
            var lastLogSw = Volatile.Read(ref diag.LastLogSwTick);
            if (lastLogSw != 0)
            {
                var sinceLogSec = (callbackEntrySwTick - lastLogSw) / (double)Stopwatch.Frequency;
                if (sinceLogSec < _daqTimingLogMinIntervalSec) return;
            }

            Volatile.Write(ref diag.LastLogSwTick, callbackEntrySwTick);

            var catchUpText = string.Empty;
            try
            {
                // 假设：当出现明显“积压”（delayToEnd 较大）且 cbInterval 远小于期望批间隔时，多半是驱动/线程池在追赶积压数据。
                if (expectBatchMs > 0 && arrivalDelayToEndMs > expectBatchMs * 2 && cbIntervalMs > 0 && cbIntervalMs < expectBatchMs * 0.2)
                    catchUpText = " catch-up";
            }
            catch
            {
            }

            // 这是限频的节拍诊断，并不等同于采集失败；完整数据仍写入诊断记录。
            // 使用 Info 避免正常的线程调度抖动污染 error.log / warning.log。
            _ = Task.Run(() => _log?.Info(
                $"[AI][{device}] DAQ回调节拍：N={batchSampleCount} (cfgN={_samplesPerChannel}) Fs={_sampleRate:F0}Hz" +
                $" 期望批间隔≈{expectBatchMs:F2}ms 回调间隔≈{cbIntervalMs:F2}ms" +
                $" 到达延迟(尾)≈{arrivalDelayToEndMs:F2}ms 到达延迟(首)≈{arrivalDelayToStartMs:F2}ms" +
                $" EndRead={endReadMs:F2}ms Rearm={rearmMs:F2}ms Queue={queueDepth}" +
                $" drift={driftMs:F2}ms{catchUpText}",
                "AI"));
        }

        private void AppendDiagnostic(DaqTimingRecord record)
        {
            if (record == null) return;
            AppendDiagnostic(DaqTimingValue.FromRecord(record));
        }

        private void AppendDiagnostic(DaqTimingValue record)
        {
            // GC/内存/线程池探针只在后台处理/恢复线程中每设备约1秒采一次；
            // DAQ回调只追加轻量时序字段，不能因诊断本身扩大回调抖动。
            if (!string.Equals(record.Kind, "Callback", StringComparison.OrdinalIgnoreCase))
            {
                var nowTicks = Stopwatch.GetTimestamp();
                ref var lastProbe = ref string.Equals(record.Device, "Dev1", StringComparison.OrdinalIgnoreCase)
                    ? ref _lastRuntimeProbeTicksDev1
                    : ref _lastRuntimeProbeTicksDev2;
                var previous = Interlocked.Read(ref lastProbe);
                if (previous == 0 ||
                    (nowTicks - previous) * 1000.0 / Stopwatch.Frequency >= 1000)
                {
                    Interlocked.Exchange(ref lastProbe, nowTicks);
                    ThreadPool.GetAvailableThreads(out var worker, out var io);
                    var gcPause = _gcPauseMonitor.Snapshot();
                    EnqueueDiagnostic(new DaqTimingValue
                    {
                        TimestampUtc = record.TimestampUtc,
                        Device = record.Device,
                        Kind = "Runtime",
                        Generation = record.Generation,
                        Gc0 = GC.CollectionCount(0),
                        Gc1 = GC.CollectionCount(1),
                        Gc2 = GC.CollectionCount(2),
                        ManagedMemoryBytes = GC.GetTotalMemory(false),
                        WorkerThreadsAvailable = worker,
                        IoThreadsAvailable = io,
                        GcEtwStatus = gcPause.Status,
                        GcPauseDurationMs = gcPause.LastPauseDurationMs,
                        GcPauseUtc = gcPause.LastPauseUtc,
                        GcPauseCount = gcPause.PauseCount
                    });
                }
            }
            EnqueueDiagnostic(record);
        }

        private void EnqueueDiagnostic(DaqTimingRecord record)
        {
            if (record != null) _timingDiagnostics.Append(DaqTimingValue.FromRecord(record));
        }

        private void EnqueueDiagnostic(DaqTimingValue record)
        {
            _timingDiagnostics.Append(record);
        }

        public string[] ExportDiagnostics(string directory, IEnumerable<string> devices, TimeSpan window)
        {
            return CaptureDiagnostics(devices, window).WriteTo(directory);
        }

        public DaqDiagnosticsCapture CaptureDiagnostics(IEnumerable<string> devices, TimeSpan window)
        {
            var deviceSet = new HashSet<string>(
                devices ?? new[] { "Dev1", "Dev2" },
                StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.UtcNow - (window <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : window);
            var records = _timingDiagnostics.Snapshot()
                .Where(x => x.TimestampUtc >= cutoff && deviceSet.Contains(x.Device))
                .OrderBy(x => x.TimestampUtc)
                .ToArray();
            return new DaqDiagnosticsCapture { Records = records };
        }

        public string ExportFastCurrentEvidence(string directory, int epbChannel, TimeSpan window)
        {
            Directory.CreateDirectory(directory);
            var effectiveWindow = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : window;
            var cutoff = Stopwatch.GetTimestamp() - (long)Math.Round(
                effectiveWindow.TotalSeconds * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero);
            var records = _fastEvidence.Snapshot(epbChannel, cutoff);
            var path = Path.Combine(directory, "fast-current-evidence.csv");
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.Write(
                    "SampleUtc,CallbackArrivalUtc,Device,Channel,Generation,BatchSequence," +
                    "ProducerThreadId,CaptureMonotonicTicks,EnqueuedMonotonicTicks,SampleLeadMs," +
                    "EffectiveSampleRateHz,ClockState,EstimatedSkewPpm,ClockResidualMs,ClockWindowSeconds," +
                    "RepresentativeA,FilteredA,QualityFlags");
                for (var i = 0; i < ControlBatchRing.RawTailCapacity; i++)
                    writer.Write(",RawTailV" + i);
                writer.WriteLine();
                foreach (var record in records)
                {
                    writer.Write(
                        $"{record.SampleUtc:O},{record.CallbackArrivalUtc:O},{Csv(record.Device)},{record.Channel}," +
                        $"{record.Generation},{record.BatchSequence},{record.ProducerThreadId}," +
                        $"{record.CaptureMonotonicTicks},{record.EnqueuedMonotonicTicks}," +
                        $"{record.SampleLeadMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{record.EffectiveSampleRateHz.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{Csv(record.ClockState.ToString())}," +
                        $"{record.EstimatedSkewPpm.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{record.ClockResidualMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{record.ClockWindowSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{record.RepresentativeA.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}," +
                        $"{record.FilteredA.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}," +
                        Csv(record.QualityFlags.ToString()));
                    for (var i = 0; i < ControlBatchRing.RawTailCapacity; i++)
                    {
                        writer.Write(',');
                        if (i < record.RawTailVolts.Length)
                            writer.Write(record.RawTailVolts[i].ToString(
                                "R",
                                System.Globalization.CultureInfo.InvariantCulture));
                    }
                    writer.WriteLine();
                }
            }
            return path;
        }

        public void RecordExternalDiagnostic(DaqTimingRecord record)
        {
            AppendDiagnostic(record);
        }

        public void RecordPersistenceTiming(
            string device,
            long generation,
            int batchSize,
            int queueDepth,
            double waitMs,
            double processingMs)
        {
            AppendDiagnostic(new DaqTimingValue
            {
                TimestampUtc = DateTime.UtcNow,
                Device = device,
                Kind = "Persistence",
                Generation = generation,
                BatchSize = batchSize,
                QueueDepth = queueDepth,
                PersistenceWaitMs = waitMs,
                ProcessingMs = processingMs
            });
        }

        private static string Csv(string value)
        {
            value ??= string.Empty;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }


        /// <summary>
        ///     获取指定 EPB 通道对应设备的“最近一次 DAQ 回调节拍”（增强版：同时返回相对批首/批尾的到达延迟）。
        /// </summary>
        public bool TryGetLastDaqCallbackTimingForEpbChannelEx(
            int epbChannel,
            out double callbackIntervalMs,
            out double arrivalDelayToEndMs,
            out double arrivalDelayToStartMs,
            out string device,
            out int batchN,
            out int fs)
        {
            callbackIntervalMs = 0;
            arrivalDelayToEndMs = 0;
            arrivalDelayToStartMs = 0;
            batchN = 0;
            fs = 0;
            device = null;

            if (!_epbChannelToDevice.TryGetValue(epbChannel, out device)) return false;
            if (!_callbackTimingDiag.TryGetValue(device, out var diag)) return false;

            callbackIntervalMs = BitConverter.Int64BitsToDouble(Interlocked.Read(ref diag.LastCbIntervalMsBits));
            arrivalDelayToEndMs = BitConverter.Int64BitsToDouble(Interlocked.Read(ref diag.LastArrivalDelayMsBits));
            arrivalDelayToStartMs = BitConverter.Int64BitsToDouble(
                Interlocked.Read(ref diag.LastArrivalDelayToStartMsBits));
            batchN = diag.LastBatchN;
            fs = diag.LastFs;
            return true;
        }


        /// <summary>fast 值的来源。</summary>
        private enum FastSource
        {
            /// <summary>在 DAQ 回调中：取当前批“尾部代表值”（未必滤波），用于最低延迟。</summary>
            DaqCallback,

            /// <summary>在后台 ProcessLoop：以“滤波后工程值”的最后一个样本为 fast 值。</summary>
            ProcessLoopFilteredLast,

            /// <summary>在后台 ProcessLoop：以“滤波后工程值”的批内最大值为 fast 值。</summary>
            ProcessLoopFilteredMax,

            /// <summary>在后台 ProcessLoop：以“滤波后工程值”的批内中位数为 fast 值。</summary>
            ProcessLoopFilteredMedian
        }

        /// <summary>
        /// 当前 fast 值来源选择。
        /// <para>
        /// 默认采用 <see cref="FastSource.DaqCallback"/>：由 DAQ 回调线程提供低时延快照，
        /// 用于控制/阈值判定等对时效性敏感的逻辑。
        /// </para>
        /// </summary>
        private readonly FastSource _fastSource = FastSource.DaqCallback;


        public TwoDeviceAiAcquirer(
            AiConfigDetail cfg,
            double sampleRate,
            int samplesPerChannel,
            int medianLens, // 走 ClsDataFilter 的中值窗长（用你全局配置传入）
            ILogger log = null)
        {
            _enabled = cfg.Enabled();
            _sampleRate = sampleRate;
            _samplesPerChannel = samplesPerChannel;
            _medianLens = Math.Max(1, medianLens);
            _log = log ?? NLogger.Instance;
            _gcPauseMonitor = SharedGcPauseMonitor.Value;
            var evidenceCapacity = Math.Max(
                64,
                (int)Math.Ceiling(5.0 * Math.Max(1, sampleRate) / Math.Max(1, samplesPerChannel)) + 2);
            _fastEvidence = new FastCurrentEvidenceHistory(evidenceCapacity);

            // 现场默认只记录异常节拍；需要短时深度排障时可在 App.config 改为 all。
            _daqTimingLogMode = ParseDaqTimingLogMode(SafeGetAppSetting("DaqCallbackTimingLog"));
            _daqTimingLogMinIntervalSec = Math.Max(0.2, ParseDoubleOrDefault(SafeGetAppSetting("DaqCallbackTimingLogMinIntervalSec"), 10.0));
            _daqTimingAnomalyFactor = Math.Max(1.1, ParseDoubleOrDefault(SafeGetAppSetting("DaqCallbackTimingAnomalyFactor"), 1.5));
            _processingQueueCapacity = Math.Max(1, Math.Min(1024,
                (int)ParseDoubleOrDefault(SafeGetAppSetting("DaqProcessingQueueCapacity"), 64)));
            var configuredControlCapacity =
                ParseDoubleOrDefault(SafeGetAppSetting("DaqControlQueueCapacity"), 64);
            _controlQueueCapacity = configuredControlCapacity >= 16 && configuredControlCapacity <= 256
                ? (int)configuredControlCapacity
                : 64;
            var configuredHardFaultAge =
                ParseDoubleOrDefault(SafeGetAppSetting("DaqControlHardFaultAgeMs"), 100);
            _controlHardFaultAgeMs = configuredHardFaultAge >= 80 && configuredHardFaultAge <= 100
                ? configuredHardFaultAge
                : 100;
            var configuredWarningAge =
                ParseDoubleOrDefault(SafeGetAppSetting("DaqControlWarningAgeMs"), 50);
            _controlWarningAgeMs = configuredWarningAge >= 10 &&
                                   configuredWarningAge < _controlHardFaultAgeMs
                ? configuredWarningAge
                : Math.Min(50, _controlHardFaultAgeMs - 1);
            _clockDisciplineOptions = new ClockDisciplineOptions(
                estimatorWindowSeconds: (int)ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockEstimatorWindowSec"), 60),
                warmupSeconds: (int)ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockWarmupSec"), 30),
                maxAbsSkewPpm: ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockMaxAbsSkewPpm"), 250),
                maxCorrectionPpmPerUpdate: ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockMaxCorrectionPpmPerUpdate"), 5),
                residualHardLimitMs: ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockResidualHardLimitMs"), 100),
                invalidConfirmations: (int)ParseDoubleOrDefault(
                    SafeGetAppSetting("DaqClockInvalidConfirmations"), 10));
            var configuredUiRateHz =
                ParseDoubleOrDefault(SafeGetAppSetting("DaqUiDispatchRateHz"), 25);
            var uiRateHz = configuredUiRateHz >= 5 && configuredUiRateHz <= 50
                ? configuredUiRateHz
                : 25;
            _uiDispatchGateDev1 = new PeriodicDispatchGate(uiRateHz);
            _uiDispatchGateDev2 = new PeriodicDispatchGate(uiRateHz);
            // 未注册活动状态提供器时按带电处理，保证失效安全。
            _controlActivityProvider = _ => true;
            _callbackTimingDiag.TryAdd("Dev1", new CallbackTimingDiag());
            _callbackTimingDiag.TryAdd("Dev2", new CallbackTimingDiag());

            _dev1Channels = _enabled.Where(r => r.物理通道.StartsWith("Dev1/")).Select(r => r.物理通道).ToArray();
            _dev2Channels = _enabled.Where(r => r.物理通道.StartsWith("Dev2/")).Select(r => r.物理通道).ToArray();
            _dev1Records = _enabled.Where(r => r.物理通道.StartsWith("Dev1/"))
                .OrderBy(r => r.序号).ToArray();
            _dev2Records = _enabled.Where(r => r.物理通道.StartsWith("Dev2/"))
                .OrderBy(r => r.序号).ToArray();

            // 构建 EPB 通道 -> Dev1/Dev2 的映射（用于把回调节拍关联到具体 EPB）
            foreach (var rec in _enabled)
            {
                var epbCh = TryParseEpbChannel(rec.参数名);
                if (epbCh < 1) continue;

                if (rec.物理通道 != null && rec.物理通道.StartsWith("Dev1/", StringComparison.OrdinalIgnoreCase))
                {
                    _epbChannelToDevice[epbCh] = "Dev1";
                }
                else if (rec.物理通道 != null && rec.物理通道.StartsWith("Dev2/", StringComparison.OrdinalIgnoreCase))
                {
                    _epbChannelToDevice[epbCh] = "Dev2";
                }
            }

            BuildColumnIndex(_enabled, "Dev1", _dev1Channels, _colIndexDev1);
            BuildColumnIndex(_enabled, "Dev2", _dev2Channels, _colIndexDev2);

            _controlRingDev1 = new ControlBatchRing(
                _controlQueueCapacity,
                Math.Max(1, _dev1Records.Length));
            _controlRingDev2 = new ControlBatchRing(
                _controlQueueCapacity,
                Math.Max(1, _dev2Records.Length));

            _dev1MedianCausal = new ClsDataFilter.MedianStreamCausal(_dev1Channels.Length, _medianHalfWidth,
                MedianSelectPointsMode.OnlyPrevious, _samplesPerChannel); // 控制用因果滤波
                
            _dev2MedianCausal = new ClsDataFilter.MedianStreamCausal(_dev2Channels.Length, _medianHalfWidth,
                MedianSelectPointsMode.OnlyPrevious, _samplesPerChannel); // 控制用因果滤波

            _workerDev1 = Task.Run(
                () => ProcessLoop("Dev1", _queueDev1, _queueSignalDev1), _cts.Token);
            _workerDev2 = Task.Run(
                () => ProcessLoop("Dev2", _queueDev2, _queueSignalDev2), _cts.Token);
            _rawPublicationWorker = Task.Run(RawPublicationLoop, _cts.Token);
            _uiPublicationWorker = Task.Run(UiPublicationLoop, _cts.Token);
            _controlThreadDev1 = CreateControlThread("Dev1", _controlRingDev1, _controlSignalDev1);
            _controlThreadDev2 = CreateControlThread("Dev2", _controlRingDev2, _controlSignalDev2);
            _controlThreadDev1.Start();
            _controlThreadDev2.Start();
        }

        private Thread CreateControlThread(
            string device,
            ControlBatchRing ring,
            AutoResetEvent signal)
        {
            return new Thread(() => ControlLoop(device, ring, signal))
            {
                IsBackground = true,
                Name = $"EPB-Control-{device}",
                Priority = ThreadPriority.AboveNormal
            };
        }

        /// <summary>
        ///     停止采集与后台处理，并释放资源。
        /// </summary>
        /// <remarks>
        ///     <para>
        ///     注意：本类内部可能触发 NI/DAQmx 的 COM/驱动调用；若在 WinForms UI 线程（STA）中同步等待后台任务结束，
        ///     调试期容易触发 MDA：<c>ContextSwitchDeadlock</c>，并造成界面卡顿。
        ///     </para>
        ///     <para>
        ///     因此此处在 UI 线程上不做阻塞等待，而是把等待放到线程池中“尽力回收”。
        ///     </para>
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _cts.Cancel();
            TrySignal(_queueSignalDev1);
            TrySignal(_queueSignalDev2);
            TrySignal(_rawPublicationSignal);
            TrySignal(_uiPublicationSignal);
            TrySignal(_controlSignalDev1);
            TrySignal(_controlSignalDev2);
            try
            {
                // UI 线程（STA）避免同步等待；否则可能阻塞消息泵并触发 ContextSwitchDeadlock。
                var sc = SynchronizationContext.Current;
                var scType = sc?.GetType().FullName;
                var isWinFormsUiContext = string.Equals(scType, "System.Windows.Forms.WindowsFormsSynchronizationContext",
                    StringComparison.Ordinal);

                if (isWinFormsUiContext)
                {
                    var workers = new[] { _workerDev1, _workerDev2, _rawPublicationWorker, _uiPublicationWorker };
                    if (workers.Any(w => w != null))
                    {
                        Task.Run(() =>
                        {
                            try { Task.WaitAll(workers.Where(w => w != null).ToArray(), 1000); } catch { }
                        });
                    }
                }
                else
                {
                    Task.WaitAll(new[] { _workerDev1, _workerDev2, _rawPublicationWorker, _uiPublicationWorker }, 1000);
                }
            }
            catch
            {
            }

            try { _controlThreadDev1?.Join(500); } catch { }
            try { _controlThreadDev2?.Join(500); } catch { }

            _queueSignalDev1.Dispose();
            _queueSignalDev2.Dispose();
            _rawPublicationSignal.Dispose();
            _uiPublicationSignal.Dispose();
            _controlSignalDev1.Dispose();
            _controlSignalDev2.Dispose();
            _cts.Dispose();
        }

        private static void TrySignal(SemaphoreSlim signal)
        {
            try { signal?.Release(); } catch (ObjectDisposedException) { }
        }

        private static void TrySignal(AutoResetEvent signal)
        {
            try { signal?.Set(); } catch (ObjectDisposedException) { }
        }


        /// <summary>将指定 EPB 通道（1..12）的“当前值”设为零点。</summary>
        public void ZeroEpbChannel(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            // 优先用 fast 值（低延迟）
            if (_lastFastValue.TryGetValue(key, out var v))
            {
                _zeroOffsets[key] = v;
                _log?.Info($"EPB 通道 {epbChannel} 已置零：offset={v:F4}", "AI");
            }
        }

        /// <summary>清除指定 EPB 通道的动态零点。</summary>
        public void ClearZeroEpbChannel(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            _zeroOffsets.TryRemove(key, out _);
        }

        /// <summary>将指定“参数名”的当前值设为零点（通用：可用于压力等非 EPB 通道）。</summary>
        public void ZeroByParamName(string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            if (_lastFastValue.TryGetValue(paramName, out var v))
            {
                _zeroOffsets[paramName] = v;
                _log?.Info($"参数 {paramName} 已置零：offset={v:F4}", "AI");
            }
        }

        /// <summary>清除指定“参数名”的动态零点。</summary>
        public void ClearZeroByParamName(string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            _zeroOffsets.TryRemove(paramName, out _);
        }

        /// <summary>将“所有已知参数”的当前值设为零点（对每个 _lastFastValue 的键）。</summary>
        public void ZeroAllChannels()
        {
            foreach (var kv in _lastFastValue)
                _zeroOffsets[kv.Key] = kv.Value;
            _log?.Warn("已对所有通道执行置零（基于 fast 快照）。", "AI");
        }

        /// <summary>清除全部动态零点。</summary>
        public void ClearAllZeros()
        {
            _zeroOffsets.Clear();
            _log?.Warn("已清除全部通道的动态置零。", "AI");
        }


        // —— 供窗体订阅的两个回调 —— //
        // 原始电压数据（未标定、未滤波）：订阅者仅在同步回调期间获得只读借用，
        // 不得修改或在回调返回后保留数组引用；随后后台会原地换算该数组。
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnRawBatch;

        /// <summary>池化原始批次所有权转移事件；订阅者负责最终 Dispose。</summary>
        public event Action<OwnedDaqRawBatch> OwnedRawBatchReady;

        public void ReportRawPersistenceQueueFull(string device, int depth, int capacity)
        {
            PublishQueueFullFault(
                device,
                GetCurrentGeneration(device),
                "DaqPersistenceQueueFull",
                "RawPersistence",
                depth,
                capacity,
                reasonOverride: $"Device={device} 原始数据落盘队列达到上限 {capacity} 批。");
        }

        // 工程值（标定 + 滤波 后的全通道矩阵）：给 UI 或调试可选使用
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnEngBatch;

        /// <summary>
        /// 写盘批次事件（后台线程触发）：
        /// device: "Dev1" | "Dev2"
        /// tsUtc:   本批次每个样本的 UTC 时间戳（长度 = n）
        /// currentsByEpb: 字典，键为 1..12 的 EPB 通道号，值为该通道的电流数组（长度 = n）
        /// pressureGroup1: 组1（EPB1..6）对应的压力数组（长度 = n；若不存在则为 null）
        /// pressureGroup2: 组2（EPB7..12）对应的压力数组（长度 = n；若不存在则为 null）
        /// </summary>
        public event Action<DaqDiskBatch> DiskBatchReady;

        [Obsolete("Use DiskBatchReady. The legacy event allocates compatibility copies.")]
        public event Action<string, DateTime[], Dictionary<int, double[]>, double[], double[]> OnDiskBatch;



        /// <summary>
        ///     低时延电流样本事件：在 DAQ 回调线程中触发，报告“当前批次最后一个样本”的工程值（未滤波）。
        ///     用于实时控制，不建议做重活（如 IO/磁盘/复杂计算）。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道号（1..12）。</param>
        /// <param name="amps">电流（A，已做零漂/比例/偏置换算）。</param>
        /// <param name="ts">样本时间戳。</param>
        public event Action<int, double, DateTime> OnFastEpbCurrent;
        /// <summary>
        /// 带真实单调时钟、批次身份和质量标志的快速电流事件。新控制逻辑必须订阅此事件；
        /// 旧事件仅为二进制/源码兼容保留。
        /// </summary>
        public event Action<FastEpbCurrentSample> OnFastEpbCurrentSample;
        public event Action<DaqProcessingSnapshot> ProcessingLagDetected;
        public event Action<DaqDeviceFault> DeviceFaultDetected;
        /// <summary>
        /// 同步安全处理和诊断入环完成后触发；订阅者只能在后台发布日志、UI和快照。
        /// </summary>
        public event Action<DaqDeviceFault> DeviceFaultPublicationRequested;

        public void SetControlActivityProvider(Func<string, bool> provider)
        {
            Interlocked.Exchange(ref _controlActivityProvider, provider ?? (_ => true));
        }

        /// <summary>
        /// After the owning DAQ group has been de-energized, discard stale control history and
        /// retain only the newest batch. This is never permitted while any mapped channel is
        /// energized because skipped batches may contain safety evidence.
        /// </summary>
        public int ResynchronizeInactiveControlToLatest(string device)
        {
            if (!string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(device, "Dev2", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentOutOfRangeException(nameof(device));
            if (IsDeviceControlActive(device))
                throw new InvalidOperationException(
                    $"Device={device} 仍有通道带电，禁止丢弃控制批次。");

            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var ring = isDev1 ? _controlRingDev1 : _controlRingDev2;
            var discarded = ring.DiscardAllButLatest();
            if (isDev1)
            {
                Interlocked.Increment(ref _controlFilterResetEpochDev1);
                Interlocked.Exchange(ref _queueFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlFullFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlLatencyFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlInvariantFaultGenerationDev1, -1);
            }
            else
            {
                Interlocked.Increment(ref _controlFilterResetEpochDev2);
                Interlocked.Exchange(ref _queueFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlFullFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlLatencyFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlInvariantFaultGenerationDev2, -1);
            }
            TrySignal(isDev1 ? _controlSignalDev1 : _controlSignalDev2);
            AppendDiagnostic(new DaqTimingValue
            {
                TimestampUtc = DateTime.UtcNow,
                Device = device,
                Kind = "InactiveFastResync",
                Generation = GetCurrentGeneration(device),
                QueueDepth = ring.Depth,
                ControlQueueCapacity = ring.Capacity,
                Detail = $"Discarded={discarded}; RetainedLatest={ring.Depth > 0}"
            });
            return discarded;
        }

        /// <summary>显式开始新运行时清除控制积压和本代次故障锁存。</summary>
        public void ResetControlSafetyLatch(string device)
        {
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var ring = isDev1 ? _controlRingDev1 : _controlRingDev2;
            ring.Reset();
            if (isDev1)
            {
                _uiDispatchGateDev1.Reset();
                Interlocked.Increment(ref _controlFilterResetEpochDev1);
                Interlocked.Exchange(ref _queueFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlFullFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlLatencyFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _controlInvariantFaultGenerationDev1, -1);
                Interlocked.Exchange(ref _lastControlWarningTicksDev1, 0);
                Interlocked.Exchange(ref _lastControlBatchProcessMsBitsDev1, 0);
                Interlocked.Exchange(ref _subscriberMaxMsBitsDev1, 0);
            }
            else
            {
                _uiDispatchGateDev2.Reset();
                Interlocked.Increment(ref _controlFilterResetEpochDev2);
                Interlocked.Exchange(ref _queueFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlFullFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlLatencyFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _controlInvariantFaultGenerationDev2, -1);
                Interlocked.Exchange(ref _lastControlWarningTicksDev2, 0);
                Interlocked.Exchange(ref _lastControlBatchProcessMsBitsDev2, 0);
                Interlocked.Exchange(ref _subscriberMaxMsBitsDev2, 0);
            }
            TrySignal(isDev1 ? _controlSignalDev1 : _controlSignalDev2);
        }

        public DaqControlSnapshot GetControlSnapshot(string device)
        {
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var ring = isDev1 ? _controlRingDev1 : _controlRingDev2;
            var oldestTick = ring.OldestEnqueuedMonotonicTicks;
            var nowTick = Stopwatch.GetTimestamp();
            var processedUtcTicks = 0L;
            if (_callbackTimingDiag.TryGetValue(isDev1 ? "Dev1" : "Dev2", out var diag))
                processedUtcTicks = Interlocked.Read(ref diag.LastProcessedSampleUtcTicks);
            return new DaqControlSnapshot(
                isDev1 ? "Dev1" : "Dev2",
                "PreallocatedSpscRing",
                GetCurrentGeneration(isDev1 ? "Dev1" : "Dev2"),
                ring.Depth,
                ring.Capacity,
                AgeMs(oldestTick, nowTick),
                BitConverter.Int64BitsToDouble(Interlocked.Read(
                    ref isDev1 ? ref _lastControlBatchProcessMsBitsDev1 : ref _lastControlBatchProcessMsBitsDev2)),
                BitConverter.Int64BitsToDouble(Interlocked.Read(
                    ref isDev1 ? ref _subscriberMaxMsBitsDev1 : ref _subscriberMaxMsBitsDev2)),
                processedUtcTicks > 0 ? new DateTime(processedUtcTicks, DateTimeKind.Utc) : default,
                DateTime.UtcNow);
        }

        private static double AgeMs(long tick, long nowTick)
        {
            return tick <= 0 || nowTick < tick
                ? 0
                : (nowTick - tick) * 1000.0 / Stopwatch.Frequency;
        }

        public long GetCurrentGeneration(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? Interlocked.Read(ref _generationDev1)
                : Interlocked.Read(ref _generationDev2);

        public long GetLastProducedSequence(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? Interlocked.Read(ref _batchSequenceDev1)
                : Interlocked.Read(ref _batchSequenceDev2);

        public long GetLastDiskPublishedSequence(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? Interlocked.Read(ref _diskPublishedSequenceDev1)
                : Interlocked.Read(ref _diskPublishedSequenceDev2);


        /// <summary>
        ///     从参数名（例如 "EPB9_current"）中提取 EPB 通道号（9）。
        ///     不匹配返回 -1。
        /// </summary>
        private static int TryParseEpbChannel(string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) return -1;
            // 形如 EPB1_current / EPB12_current
            var s = paramName;
            var i = s.IndexOf("EPB", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += 3;
            var j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (j == i) return -1;
            if (int.TryParse(s.Substring(i, j - i), out var ch)) return ch;
            return -1;
        }

        /// <summary>
        ///     读取 EPB 电流的“常用”接口（向后兼容）。
        ///     语义：优先返回低延迟快照（fast），若没有则返回滤波后值（filtered），否则返回 0.0。
        ///     建议：控制逻辑应显式调用 <see cref="ReadCurrentFast" /> 或订阅 <see cref="OnFastEpbCurrent" />；
        ///     UI/统计应使用 <see cref="ReadCurrentFiltered" /> 或订阅 <see cref="OnEngBatch" />.
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>电流（A）。</returns>
        public double ReadCurrent(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            if (_lastFastValue.TryGetValue(key, out var vFast)) return vFast;
            if (_lastFilteredValue.TryGetValue(key, out var vFilt)) return vFilt;
            return 0.0;
        }

        /// <summary>
        ///     明确读取“低延迟 / 未滤波”的 EPB 电流快照（由 DAQ 回调线程写入）。
        ///     若不存在返回 0.0。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>低延迟电流（A），或 0.0。</returns>
        public double ReadCurrentFast(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            return _lastFastValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        public FastCurrentSnapshot ReadCurrentFastSample(int epbChannel)
        {
            if (!_lastFastEpbSample.TryGetValue(epbChannel, out var sample))
                return new FastCurrentSnapshot(default, double.PositiveInfinity, false);
            var now = Stopwatch.GetTimestamp();
            var ageMs = sample.CaptureMonotonicTicks <= 0 || now < sample.CaptureMonotonicTicks
                ? double.PositiveInfinity
                : (now - sample.CaptureMonotonicTicks) * 1000.0 / Stopwatch.Frequency;
            return new FastCurrentSnapshot(sample, ageMs, true);
        }

        /// <summary>
        ///     明确读取“滤波后 / 平滑”的 EPB 电流快照（由后台线程写入）。
        ///     若不存在返回 0.0。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>滤波后电流（A），或 0.0。</returns>
        public double ReadCurrentFiltered(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            return _lastFilteredValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     读取压力（默认行为同 ReadCurrent：优先 fast，再 filtered）。
        ///     压力通常不会出现在 fast 分支；此方法保持兼容性并返回合适的值。
        /// </summary>
        /// <param name="id">压力编号（例如 1/2）。</param>
        /// <returns>压力值（单位由配置定义），或 0.0。</returns>
        public double ReadPressure(int id)
        {
            var key = $"Pressure_{id}";
            if (_lastFastValue.TryGetValue(key, out var vFast)) return vFast;
            if (_lastFilteredValue.TryGetValue(key, out var vFilt)) return vFilt;
            return 0.0;
        }

        /// <summary>读取实际压力及其采集新鲜度；尚未收到样本时返回无效快照。</summary>
        public PressureSample ReadPressureSample(int id)
        {
            var key = $"Pressure_{id}";
            return _lastPressureSample.TryGetValue(key, out var sample)
                ? sample
                : new PressureSample(id, double.NaN, DateTime.MinValue, 0);
        }

        /// <summary>
        ///     明确读取滤波后的压力值（UI/统计推荐使用）。
        /// </summary>
        public double ReadPressureFiltered(int id)
        {
            var key = $"Pressure_{id}";
            return _lastFilteredValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     明确读取（若存在）由 fast 分支写入的压力值（一般不会有）。
        /// </summary>
        public double ReadPressureFast(int id)
        {
            var key = $"Pressure_{id}";
            return _lastFastValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     获取指定 EPB 通道对应设备的“最近一次 DAQ 回调节拍”。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <param name="callbackIntervalMs">
        ///     最近一次回调间隔（ms）。
        ///     <para>说明：这是“回调进入时刻”之间的间隔，反映调度/阻塞/GC 等因素。</para>
        /// </param>
        /// <param name="arrivalDelayMs">
        ///     最近一次到达延迟（ms）。
        ///     <para>说明：arrivalUtc - batchTimeUtc；用于量化“数据时间”到“处理到达”的滞后。</para>
        /// </param>
        /// <param name="device">设备名（Dev1/Dev2）；若未知返回 null。</param>
        /// <param name="batchN">最近一次批大小（每通道样本数）；若未知返回 0。</param>
        /// <param name="fs">最近一次采样率（Hz，四舍五入）；若未知返回 0。</param>
        /// <returns>
        ///     若能定位到该 EPB 通道所属设备，且存在回调节拍记录则返回 true；否则返回 false。
        /// </returns>
        /// <remarks>
        ///     <para>
        ///     线程模型：回调线程写入，控制/日志线程读取；内部采用 double->long bits + Interlocked 读写以避免撕裂。
        ///     </para>
        ///     <para>
        ///     注意：该值反映“最近一次回调”的节拍，并不保证严格对齐到某一条具体 EPB 样本；
        ///     但足以用于判断“过冲是否伴随回调间隔尖峰”。
        ///     </para>
        /// </remarks>
        public bool TryGetLastDaqCallbackTimingForEpbChannel(
            int epbChannel,
            out double callbackIntervalMs,
            out double arrivalDelayMs,
            out string device,
            out int batchN,
            out int fs)
        {
            callbackIntervalMs = 0;
            arrivalDelayMs = 0;
            batchN = 0;
            fs = 0;
            device = null;

            if (!_epbChannelToDevice.TryGetValue(epbChannel, out device)) return false;
            if (!_callbackTimingDiag.TryGetValue(device, out var diag)) return false;

            callbackIntervalMs = BitConverter.Int64BitsToDouble(Interlocked.Read(ref diag.LastCbIntervalMsBits));
            arrivalDelayMs = BitConverter.Int64BitsToDouble(Interlocked.Read(ref diag.LastArrivalDelayMsBits));
            batchN = diag.LastBatchN;
            fs = diag.LastFs;
            return true;
        }

        public string GetDeviceForEpbChannel(int epbChannel)
        {
            return _epbChannelToDevice.TryGetValue(epbChannel, out var device) ? device : null;
        }

        public DaqFreshnessSnapshot GetDaqFreshnessSnapshot(string device, double maxAgeMs = 100)
        {
            if (string.IsNullOrWhiteSpace(device) ||
                !_callbackTimingDiag.TryGetValue(device, out var diag))
                return new DaqFreshnessSnapshot
                {
                    Device = device ?? string.Empty,
                    AgeMs = double.PositiveInfinity,
                    IsFresh = false
                };

            var callbackTick = Interlocked.Read(ref diag.LastCallbackEntrySwTick);
            var enqueuedTick = Interlocked.Read(ref diag.LastControlEnqueuedSwTick);
            var processedTick = Interlocked.Read(ref diag.LastSampleCommitSwTick);
            var nowTick = Stopwatch.GetTimestamp();
            var ageMs = processedTick <= 0
                ? double.PositiveInfinity
                : (nowTick - processedTick) * 1000.0 / Stopwatch.Frequency;
            var processedUtcTicks = Interlocked.Read(ref diag.LastProcessedSampleUtcTicks);
            return new DaqFreshnessSnapshot
            {
                Device = device,
                Generation = Interlocked.Read(ref diag.LastGeneration),
                LastProducedSequence = Interlocked.Read(ref diag.LastBatchSequence),
                LastProcessedSequence = Interlocked.Read(ref diag.LastProcessedSequence),
                ControlDiscontinuityCount = Interlocked.Read(ref diag.ControlDiscontinuityCount),
                LastControlDiscontinuitySequence = Interlocked.Read(
                    ref diag.LastControlDiscontinuitySequence),
                ClockState = (ClockState)Volatile.Read(ref diag.ClockState),
                EffectiveSampleRateHz = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EffectiveSampleRateHzBits)),
                EstimatedSkewPpm = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EstimatedSkewPpmBits)),
                ClockResidualMs = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockResidualMsBits)),
                LastArrivalMonotonicTicks = processedTick,
                AgeMs = ageMs,
                IsFresh = processedTick > 0 && ageMs <= Math.Max(1, maxAgeMs),
                LastCallbackMonotonicTicks = callbackTick,
                LastControlEnqueuedMonotonicTicks = enqueuedTick,
                LastControlProcessedMonotonicTicks = processedTick,
                CallbackAgeMs = callbackTick <= 0
                    ? double.PositiveInfinity
                    : (nowTick - callbackTick) * 1000.0 / Stopwatch.Frequency,
                ControlEnqueueAgeMs = enqueuedTick <= 0
                    ? double.PositiveInfinity
                    : (nowTick - enqueuedTick) * 1000.0 / Stopwatch.Frequency,
                ControlProcessedAgeMs = ageMs,
                ProcessedSampleUtc = processedUtcTicks > 0
                    ? new DateTime(processedUtcTicks, DateTimeKind.Utc)
                    : default
            };
        }

        /// <summary>串行重建指定EPB所属DAQ，并等待连续新鲜回调。</summary>
        public async Task<DaqRecoveryResult> RecoverForEpbAsync(
            int epbChannel,
            int timeoutMs = 3000,
            int requiredFreshCallbacks = 10,
            int maxAgeMs = 100,
            CancellationToken token = default)
        {
            var device = GetDeviceForEpbChannel(epbChannel);
            if (string.IsNullOrWhiteSpace(device))
                return new DaqRecoveryResult { FailureReason = $"EPB[{epbChannel}]未映射DAQ设备" };

            return await RecoverDeviceAsync(
                    device, timeoutMs, requiredFreshCallbacks, maxAgeMs, token)
                .ConfigureAwait(false);
        }

        /// <summary>串行重建指定 DAQ 设备，并以新 generation 的回调作为恢复证据。</summary>
        public async Task<DaqRecoveryResult> RecoverDeviceAsync(
            string device,
            int timeoutMs = 3000,
            int requiredFreshCallbacks = 10,
            int maxAgeMs = 100,
            CancellationToken token = default,
            bool forceRecreate = false)
        {
            if (!string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(device, "Dev2", StringComparison.OrdinalIgnoreCase))
                return new DaqRecoveryResult
                {
                    Device = device ?? string.Empty,
                    FailureReason = "DaqDeviceUnknown"
                };

            var gate = _recoveryGates.GetOrAdd(device, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var previousGeneration = GetCurrentGeneration(device);
                AppendDiagnostic(new DaqTimingValue
                {
                    TimestampUtc = DateTime.UtcNow,
                    Device = device,
                    Kind = "RecoveryStart",
                    Generation = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                        ? Interlocked.Read(ref _generationDev1)
                        : Interlocked.Read(ref _generationDev2),
                    Detail = $"RequiredFresh={requiredFreshCallbacks}; MaxAgeMs={maxAgeMs}; TimeoutMs={timeoutMs}"
                });
                var clock = Stopwatch.StartNew();
                // 可能已有另一条恢复请求刚刚完成。进入串行门后若设备已恢复新鲜，
                // 直接复用其结果，避免紧接着再次 Stop/Start。
                var current = GetDaqFreshnessSnapshot(device, maxAgeMs);
                if (!forceRecreate)
                {
                    var stableGeneration = GetCurrentGeneration(device);
                    var verifier = new DaqRecoveryFreshnessVerifier(
                        stableGeneration,
                        requiredFreshCallbacks);
                    if (current.IsFresh && current.Generation == stableGeneration)
                        verifier.Seed(current);
                    while (clock.ElapsedMilliseconds <= Math.Min(500, Math.Max(50, timeoutMs)))
                    {
                        token.ThrowIfCancellationRequested();
                        var next = GetDaqFreshnessSnapshot(device, maxAgeMs);
                        if (verifier.Observe(next))
                        {
                            return new DaqRecoveryResult
                            {
                                Device = device,
                                Recovered = true,
                                PreviousGeneration = previousGeneration,
                                RecoveredGeneration = stableGeneration,
                                FirstVerifiedSequence = verifier.FirstVerifiedSequence,
                                LastVerifiedSequence = verifier.LastVerifiedSequence,
                                FreshCallbacks = verifier.FreshCallbacks,
                                RequiredFreshCallbacks = requiredFreshCallbacks,
                                ElapsedMs = (int)clock.ElapsedMilliseconds
                            };
                        }
                        if (next.Generation != stableGeneration) break;
                        await Task.Delay(5, token).ConfigureAwait(false);
                    }
                }
                try
                {
                    StopDevice(device);
                    token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(TwoDeviceAiAcquirer));
                    StartDevice(device);
                    AppendDiagnostic(new DaqTimingValue
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Device = device,
                        Kind = "RecoveryRebuilt",
                        Generation = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                            ? Interlocked.Read(ref _generationDev1)
                            : Interlocked.Read(ref _generationDev2)
                    });
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    throw new OperationCanceledException("DAQ采集器正在释放。", token);
                }
                catch (Exception ex)
                {
                    _log.Error($"重建 {device} 失败：{ex.Message}", "AI", ex);
                    var nativeError = GetNativeErrorCode(ex);
                    return new DaqRecoveryResult
                    {
                        Device = device,
                        Recovered = false,
                        PreviousGeneration = previousGeneration,
                        RecoveredGeneration = GetCurrentGeneration(device),
                        RequiredFreshCallbacks = requiredFreshCallbacks,
                        ElapsedMs = (int)clock.ElapsedMilliseconds,
                        FailureReason = $"DaqTaskRecreateFailed: {ex.Message}",
                        FailureKind = IsExplicitDeviceMissing(ex)
                            ? "DaqDeviceMissing"
                            : "DaqTaskRecreateFailed",
                        NativeErrorCode = nativeError,
                        Classification = FaultClassification.SystemFault
                    };
                }

                var recoveredGeneration = GetCurrentGeneration(device);
                var recoveryVerifier = new DaqRecoveryFreshnessVerifier(
                    recoveredGeneration,
                    requiredFreshCallbacks);
                while (clock.ElapsedMilliseconds <= Math.Max(1, timeoutMs))
                {
                    token.ThrowIfCancellationRequested();
                    var snapshot = GetDaqFreshnessSnapshot(device, maxAgeMs);
                    if (recoveryVerifier.Observe(snapshot))
                    {
                        return new DaqRecoveryResult
                        {
                            Device = device,
                            Recovered = true,
                            PreviousGeneration = previousGeneration,
                            RecoveredGeneration = recoveredGeneration,
                            FirstVerifiedSequence = recoveryVerifier.FirstVerifiedSequence,
                            LastVerifiedSequence = recoveryVerifier.LastVerifiedSequence,
                            FreshCallbacks = recoveryVerifier.FreshCallbacks,
                            RequiredFreshCallbacks = requiredFreshCallbacks,
                            ElapsedMs = (int)clock.ElapsedMilliseconds
                        };
                    }
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
                return new DaqRecoveryResult
                {
                    Device = device,
                    Recovered = false,
                    PreviousGeneration = previousGeneration,
                    RecoveredGeneration = recoveredGeneration,
                    FirstVerifiedSequence = recoveryVerifier.FirstVerifiedSequence,
                    LastVerifiedSequence = recoveryVerifier.LastVerifiedSequence,
                    FreshCallbacks = recoveryVerifier.FreshCallbacks,
                    RequiredFreshCallbacks = requiredFreshCallbacks,
                    ElapsedMs = (int)clock.ElapsedMilliseconds,
                    FailureReason = "DaqRecoveryFreshnessTimeout",
                    FailureKind = "DaqRecoveryFreshnessTimeout",
                    Classification = FaultClassification.SystemFault
                };
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// 启动试验前确认所有选中通道所属设备在持续产生新鲜回调；不新鲜时先自动重建。
        /// </summary>
        public async Task<DaqRecoveryResult[]> EnsureChannelsReadyAsync(
            IEnumerable<int> epbChannels,
            int timeoutMs = 3000,
            int requiredFreshCallbacks = 3,
            int maxAgeMs = 100,
            CancellationToken token = default)
        {
            var channels = (epbChannels ?? Enumerable.Empty<int>()).Distinct().ToArray();
            var results = channels
                .Where(ch => string.IsNullOrWhiteSpace(GetDeviceForEpbChannel(ch)))
                .Select(ch => new DaqRecoveryResult
                {
                    Device = string.Empty,
                    Recovered = false,
                    FailureReason = $"EPB[{ch}]未映射DAQ设备"
                })
                .ToList();
            var devices = channels
                .Select(GetDeviceForEpbChannel)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var device in devices)
            {
                token.ThrowIfCancellationRequested();
                var snapshot = GetDaqFreshnessSnapshot(device, maxAgeMs);
                if (snapshot.IsFresh)
                {
                    var verifyClock = Stopwatch.StartNew();
                    var generation = snapshot.Generation;
                    var verifier = new DaqRecoveryFreshnessVerifier(
                        generation,
                        requiredFreshCallbacks);
                    verifier.Seed(snapshot);
                    while (verifyClock.ElapsedMilliseconds <= Math.Min(500, Math.Max(50, timeoutMs)))
                    {
                        token.ThrowIfCancellationRequested();
                        var next = GetDaqFreshnessSnapshot(device, maxAgeMs);
                        if (verifier.Observe(next)) break;
                        if (next.Generation != generation) break;
                        await Task.Delay(5, token).ConfigureAwait(false);
                    }
                    if (verifier.IsSatisfied)
                    {
                        results.Add(new DaqRecoveryResult
                        {
                            Device = device,
                            Recovered = true,
                            PreviousGeneration = generation,
                            RecoveredGeneration = generation,
                            FirstVerifiedSequence = verifier.FirstVerifiedSequence,
                            LastVerifiedSequence = verifier.LastVerifiedSequence,
                            FreshCallbacks = verifier.FreshCallbacks,
                            RequiredFreshCallbacks = requiredFreshCallbacks,
                            ElapsedMs = (int)verifyClock.ElapsedMilliseconds
                        });
                        continue;
                    }
                }

                results.Add(await RecoverDeviceAsync(
                        device, timeoutMs, requiredFreshCallbacks, maxAgeMs, token)
                    .ConfigureAwait(false));
            }
            return results.ToArray();
        }

        public void Start(double aiMin = -10, double aiMax = 10,
            AITerminalConfiguration term = AITerminalConfiguration.Rse)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TwoDeviceAiAcquirer));
            Stop();
            _aiMin = aiMin;
            _aiMax = aiMax;
            _terminalConfiguration = term;
            // 每次启动都重新建立单调时钟与墙钟的对应关系，避免继承上一次采集的起点。
            InitTimeBase();


            if (_dev1Channels.Length > 0) StartDevice("Dev1");

            // 暂时注释dev2
            if (_dev2Channels.Length > 0) StartDevice("Dev2");

            _log.Info(
                $"AI 采集启动：Dev1[{_dev1Channels.Length}] Dev2[{_dev2Channels.Length}] Fs={_sampleRate}Hz N={_samplesPerChannel}",
                "AI");
        }

        public void Stop()
        {
            StopDevice("Dev1");
            StopDevice("Dev2");
        }

        /// <summary>停止新采样，并等待已接收批次完成工程处理、持久化投递和 Raw 所有权移交。</summary>
        public async Task<bool> StopAndDrainAsync(int timeoutMs)
        {
            Stop();
            var dev1Boundary = GetLastProducedSequence("Dev1");
            var dev2Boundary = GetLastProducedSequence("Dev2");
            return await WaitForBackgroundPipelinesAsync(
                    dev1Boundary,
                    dev2Boundary,
                    timeoutMs,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 不停止DAQ，仅捕获当前生产边界并等待该边界之前的工程处理、Raw发布和所有权移交完成。
        /// 用于优雅暂停：电机已关闭后把暂停点以前的数据完整交给上层写盘队列，同时保持采集健康。
        /// </summary>
        public Task<bool> DrainBackgroundPipelinesAsync(
            int timeoutMs,
            CancellationToken token = default)
        {
            var dev1Boundary = GetLastProducedSequence("Dev1");
            var dev2Boundary = GetLastProducedSequence("Dev2");
            return WaitForBackgroundPipelinesAsync(dev1Boundary, dev2Boundary, timeoutMs, token);
        }

        private async Task<bool> WaitForBackgroundPipelinesAsync(
            long dev1Boundary,
            long dev2Boundary,
            int timeoutMs,
            CancellationToken token)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (IsBackgroundPipelineDrained(
                        GetLastDiskPublishedSequence("Dev1"),
                        dev1Boundary,
                        GetLastDiskPublishedSequence("Dev2"),
                        dev2Boundary,
                        Interlocked.Read(ref _rawTransferredSequenceDev1),
                        Interlocked.Read(ref _rawTransferredSequenceDev2)))
                    return true;
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            return false;
        }

        internal static bool IsBackgroundPipelineDrained(
            long publishedSequenceDev1,
            long boundarySequenceDev1,
            long publishedSequenceDev2,
            long boundarySequenceDev2,
            long rawTransferredSequenceDev1,
            long rawTransferredSequenceDev2)
        {
            return publishedSequenceDev1 >= boundarySequenceDev1 &&
                   publishedSequenceDev2 >= boundarySequenceDev2 &&
                   rawTransferredSequenceDev1 >= boundarySequenceDev1 &&
                   rawTransferredSequenceDev2 >= boundarySequenceDev2;
        }

        // —— DAQ 回调：只负责 EndRead + 入队 + 立刻发起下一次 BeginRead —— //
        private void Dev1Callback(IAsyncResult ar)
        {
            OnAiBatch(ar, _colIndexDev1, Dev1Callback);
        }

        private void Dev2Callback(IAsyncResult ar)
        {
            OnAiBatch(ar, _colIndexDev2, Dev2Callback);
        }


        private void OnAiBatch(IAsyncResult ar,
            Dictionary<string, int> colIndex,
            AsyncCallback again)
        {
            var state = ar.AsyncState as DeviceReadState;
            var device = state?.Device ?? "Unknown";
            var generation = state?.Generation ?? -1;
            var rearmed = false;
            var callbackEntrySwTick = Stopwatch.GetTimestamp();
            var previousCallbackEntrySwTick = MarkCallbackEntry(device, callbackEntrySwTick);
            try
            {
                if (state?.Reader is null || state.Task is null) return;
                var reader = state.Reader;

                // 回调进入时刻：用于计算“回调间隔/到达延迟”（与数据时间 current 区分）
                var arrivalUtc = DateTime.UtcNow;

                var endReadStartSwTick = Stopwatch.GetTimestamp();
                var raw = reader.EndReadMultiSample(ar); // [ch, n]
                var endReadMs = (Stopwatch.GetTimestamp() - endReadStartSwTick) * 1000.0 / Stopwatch.Frequency;
                // Stop/重建会先推进 generation；迟到的旧回调只负责 EndRead 释放，
                // 不得写快照、入队或给新任务 re-arm。
                if (!IsCurrentGeneration(device, generation)) return;
                int n = raw.GetLength(1);

                // 同设备生产区必须严格串行。下一次读取只在控制批入环后 re-arm，
                // 从结构上保证 SPSC 控制环只有一个活动生产者。
                if (!state.ProducerGate.TryEnter())
                {
                    var reentry = state.ProducerGate.ReentryCount;
                    if (_callbackTimingDiag.TryGetValue(device, out var reentryDiag))
                        Interlocked.Exchange(ref reentryDiag.ProducerReentryCount, reentry);
                    PublishQueueFullFault(
                        device,
                        generation,
                        "ControlProducerReentry",
                        "Control",
                        0,
                        string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                            ? _controlRingDev1.Capacity
                            : _controlRingDev2.Capacity,
                        0,
                        $"FastPathSignalInvalid Cause=ControlProducerReentry Device={device} " +
                        $"Generation={generation} DAQ快速控制生产区发生回调重入，已拒绝该批次。Count={reentry}");
                    return;
                }

                var producerHeld = true;
                DateTime current;
                DateTime last;
                double driftMs;
                long sequence;
                FastSignalQualityFlags qualityFlags = FastSignalQualityFlags.None;
                try
                {
                    var timeline = state.Timeline.Advance(
                        n,
                        arrivalUtc,
                        callbackEntrySwTick);
                    current = timeline.BatchEndUtc;
                    last = timeline.PreviousBatchEndUtc;
                    driftMs = timeline.ArrivalDelayMs;
                    sequence = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                        ? Interlocked.Increment(ref _batchSequenceDev1)
                        : Interlocked.Increment(ref _batchSequenceDev2);
                    if (_callbackTimingDiag.TryGetValue(device, out var sequenceDiag))
                    {
                        Interlocked.Exchange(ref sequenceDiag.LastBatchSequence, sequence);
                        Interlocked.Exchange(
                            ref sequenceDiag.LastSampleLeadMsBits,
                            BitConverter.DoubleToInt64Bits(timeline.SampleLeadMs));
                        Interlocked.Exchange(ref sequenceDiag.LastGeneration, generation);
                        Interlocked.Exchange(
                            ref sequenceDiag.EffectiveSampleRateHzBits,
                            BitConverter.DoubleToInt64Bits(timeline.EffectiveSampleRateHz));
                        Interlocked.Exchange(
                            ref sequenceDiag.EstimatedSkewPpmBits,
                            BitConverter.DoubleToInt64Bits(timeline.EstimatedSkewPpm));
                        Interlocked.Exchange(
                            ref sequenceDiag.ClockResidualMsBits,
                            BitConverter.DoubleToInt64Bits(timeline.ResidualMs));
                        Interlocked.Exchange(
                            ref sequenceDiag.ClockWindowSecondsBits,
                            BitConverter.DoubleToInt64Bits(timeline.EstimatorWindowSeconds));
                        Interlocked.Exchange(
                            ref sequenceDiag.ClockCorrectionPpmBits,
                            BitConverter.DoubleToInt64Bits(timeline.CorrectionPpm));
                        Volatile.Write(ref sequenceDiag.ClockState, (int)timeline.ClockState);
                    }

                    if (timeline.StateChanged || timeline.RequiresRecovery)
                    {
                        AppendDiagnostic(new DaqTimingValue
                        {
                            TimestampUtc = arrivalUtc,
                            Device = device,
                            Kind = timeline.RequiresRecovery ? "ClockInvalid" : "ClockState",
                            Generation = generation,
                            BatchSize = n,
                            DriftMs = driftMs,
                            BatchSequence = sequence,
                            SampleLeadMs = timeline.SampleLeadMs,
                            EffectiveSampleRateHz = timeline.EffectiveSampleRateHz,
                            EstimatedSkewPpm = timeline.EstimatedSkewPpm,
                            ClockResidualMs = timeline.ResidualMs,
                            ClockWindowSeconds = timeline.EstimatorWindowSeconds,
                            ClockCorrectionPpm = timeline.CorrectionPpm,
                            ClockState = timeline.ClockState.ToString(),
                            Detail = $"Sequence={sequence}; State={timeline.ClockState}; " +
                                     $"RateHz={timeline.EffectiveSampleRateHz:F6}; " +
                                     $"SkewPpm={timeline.EstimatedSkewPpm:F3}; " +
                                     $"ResidualMs={timeline.ResidualMs:F3}"
                        });
                    }
                    if (timeline.RequiresRecovery)
                    {
                        PublishQueueFullFault(
                            device,
                            generation,
                            "DaqClockModelInvalid",
                            "Control",
                            0,
                            string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                                ? _controlRingDev1.Capacity
                                : _controlRingDev2.Capacity,
                            0,
                            $"Device={device} Generation={generation} DAQ时钟模型连续不可用，" +
                            $"将安全暂停并自动重建。State={timeline.ClockState} " +
                            $"RateHz={timeline.EffectiveSampleRateHz:F6} " +
                            $"SkewPpm={timeline.EstimatedSkewPpm:F3} " +
                            $"ResidualMs={timeline.ResidualMs:F3}。"
                        );
                    }

                    if (_fastSource == FastSource.DaqCallback)
                        {
                            var devRecs = GetDeviceRecords(device);
                            var chCount = Math.Min(raw.GetLength(0), devRecs.Length);
                            var lastCol = raw.GetLength(1) - 1;
                            Span<FastControlSampleValue> controlSamples =
                                stackalloc FastControlSampleValue[Math.Max(1, chCount)];
                            var controlSampleCount = 0;
                            if (lastCol >= 0)
                            {
                                for (var c = 0; c < chCount; c++)
                                {
                                    var rec = devRecs[c];
                                    var epbCh = TryParseEpbChannel(rec.参数名);
                                    var isPressure = TryParsePressureId(rec.参数名, out var pressureId);
                                    if ((epbCh < 1 || epbCh > 12) && !isPressure) continue;
                                    var representative = ComputeFastRepresentative(raw, c, lastCol, rec);
                                    var sampleQuality = FastSignalQualityFlags.None;
                                    if (double.IsNaN(representative) || double.IsInfinity(representative))
                                        sampleQuality |= FastSignalQualityFlags.NonFinite;
                                    var rawTailStart = Math.Max(0, lastCol - ControlBatchRing.RawTailCapacity + 1);
                                    for (var col = rawTailStart; col <= lastCol; col++)
                                    {
                                        if (Math.Abs(raw[c, col]) >= Math.Max(Math.Abs(_aiMin), Math.Abs(_aiMax)) * 0.95)
                                        {
                                            sampleQuality |= FastSignalQualityFlags.AdcNearRail;
                                            break;
                                        }
                                    }
                                    controlSamples[controlSampleCount++] = new FastControlSampleValue(
                                        c,
                                        epbCh,
                                        isPressure ? pressureId : 0,
                                        representative,
                                        sampleQuality);
                                }
                            }

                            var controlEnqueuedTick = Stopwatch.GetTimestamp();
                            var metadata = new FastControlBatchMetadata(
                                generation,
                                sequence,
                                current,
                                arrivalUtc,
                                callbackEntrySwTick,
                                controlEnqueuedTick,
                                timeline.SampleLeadMs,
                                timeline.EffectiveSampleRateHz,
                                timeline.ClockState,
                                timeline.EstimatedSkewPpm,
                                timeline.ResidualMs,
                                timeline.EstimatorWindowSeconds,
                                Thread.CurrentThread.ManagedThreadId,
                                qualityFlags);
                            if (EnqueueForControl(
                                    device,
                                    metadata,
                                    controlSamples.Slice(0, controlSampleCount),
                                    raw))
                                MarkControlEnqueued(device, controlEnqueuedTick);
                        }

                        // raw 在此之前仍由 DAQ 回调独占。后台 ProcessLoop 会对 Item.Raw
                        // 执行 ConvertToEngineeringInPlace，因此必须等快速代表值计算、
                        // 质量检查和控制环 RawTail 深复制全部完成后，才发布同一数组引用。
                        // 此调用是所有权转移点；调用后本回调不得再读写 raw。
                        EnqueueForProcessing(new Item(
                            device,
                            generation,
                            sequence,
                            raw,
                            current,
                            last,
                            callbackEntrySwTick,
                            timeline.EffectiveSampleRateHz,
                            timeline.ClockState,
                            timeline.EstimatedSkewPpm,
                            timeline.ResidualMs,
                            timeline.EstimatorWindowSeconds));

                    // 所有生产者工作到此结束；清门后才允许下一批回调进入。
                    state.ProducerGate.Exit();
                    producerHeld = false;

                    if (!IsCurrentGeneration(device, generation)) return;
                    var rearmStartSwTick = Stopwatch.GetTimestamp();
                    reader.BeginReadMultiSample(_samplesPerChannel, again, state);
                    var rearmMs = (Stopwatch.GetTimestamp() - rearmStartSwTick) * 1000.0 / Stopwatch.Frequency;
                    rearmed = true;

                    TryLogDaqCallbackTiming(
                        device, generation, n, current, arrivalUtc,
                        callbackEntrySwTick, previousCallbackEntrySwTick,
                        endReadMs, rearmMs, driftMs);
                }
                finally
                {
                    if (producerHeld)
                        state.ProducerGate.Exit();
                }


                // 下一轮
                //reader.BeginReadMultiSample(_samplesPerChannel, again, task);
                //_lastTs = current;

                // ⑧ 时钟更新已在上面的 lock 块中完成

            }
            catch (DaqException ex)
            {
                if (!IsCurrentGeneration(device, generation)) return;
                ScheduleDeviceRecovery(device, generation, ex);
            }
            catch (Exception ex)
            {
                if (!IsCurrentGeneration(device, generation)) return;
                ScheduleDeviceRecovery(device, generation, ex);
            }
            finally
            {
                if (state != null && !rearmed && !state.ProducerGate.IsActive)
                    state.Quiesced.Set();
            }
        }

        #region  fast 快照的批内聚合选项。

        /// <summary>
        /// fast 快照的批内聚合选项。
        /// </summary>
        private sealed class FastSnapshotOptions
        {
            /// <summary>聚合模式。</summary>
            public enum ModeKind { LastSample, TailMedian, TailTrimmedMean }

            /// <summary>使用的聚合模式（默认 TailMedian）。</summary>
            public ModeKind Mode { get; set; } = ModeKind.TailMedian;

            /// <summary>
            /// 尾部参与聚合的样本数 K（建议奇数 3/5/7/9）。
            /// 仅当 Mode=TailMedian 或 TailTrimmedMean 时生效。
            /// </summary>
            public int TailCount { get; set; } = 5;

            /// <summary>
            /// 截尾比例（0..0.45），仅对 TailTrimmedMean 生效。
            /// 例如 0.2 表示两端各截去 20% 再求均值。
            /// </summary>
            public double TrimRatio { get; set; } = 0.2;
        }

        /// <summary>fast 快照批内聚合选项（可按需改默认值）。</summary>
        private readonly FastSnapshotOptions _fastSnap = new FastSnapshotOptions
        {
            Mode = FastSnapshotOptions.ModeKind.TailMedian,
            TailCount = 5,
            TrimRatio = 0.2
        };

        /// <summary>
        /// 计算某通道在“当前批次”上的鲁棒代表值：
        /// - LastSample：取最后一个样本；
        /// - TailMedian：取批尾 K 点中值；
        /// - TailTrimmedMean：批尾 K 点按比例截尾后的均值；
        /// 然后由单生产者控制线程中的 FastFilter 做因果平滑与限速。
        /// </summary>
        /// <param name="raw">当前批次原始电压数组 [ch, n]。</param>
        /// <param name="ch">通道索引。</param>
        /// <param name="lastCol">最后一列索引（n-1）。</param>
        /// <param name="rec">该通道的配置记录（用于电压→工程值）。</param>
        /// <param name="now">当前主机时间戳，用于 fastFilter 的 dt。</param>
        /// <returns>批内聚合后的工程值代表。</returns>
        private double ComputeFastRepresentative(
            double[,] raw,
            int ch,
            int lastCol,
            AiConfigDetailRecord rec)
        {
            if (_fastSnap.Mode == FastSnapshotOptions.ModeKind.LastSample || lastCol < 0)
            {
                // 仅最后一个样本（几乎零延迟）
                return ConvertFastVoltageToEngineering(raw[ch, lastCol], rec);
            }

            // 参与聚合的尾部窗口 [startCol..lastCol]
            int k = Math.Max(1, Math.Min(9, _fastSnap.TailCount));
            int startCol = Math.Max(0, lastCol - k + 1);
            int count = lastCol - startCol + 1;

            // 固定小窗口使用栈内存，避免每通道每批创建数组。
            Span<double> buf = stackalloc double[9];
            for (int j = 0, col = startCol; col <= lastCol; col++, j++)
                buf[j] = ConvertFastVoltageToEngineering(raw[ch, col], rec);

            for (var i = 1; i < count; i++)
            {
                var value = buf[i];
                var j = i - 1;
                while (j >= 0 && buf[j] > value)
                {
                    buf[j + 1] = buf[j];
                    j--;
                }
                buf[j + 1] = value;
            }

            if (_fastSnap.Mode == FastSnapshotOptions.ModeKind.TailMedian)
            {
                return buf[count / 2];             // 中值（奇数严格中位；偶数取上中位）
            }
            else // TailTrimmedMean
            {
                int trim = (int)Math.Round(count * Math.Min(0.45, Math.Max(0.0, _fastSnap.TrimRatio)));
                int s = trim;
                int e = count - trim;              // [s, e) 保留
                if (e <= s) { s = 0; e = count; }  // 太短就退化为普通均值

                double sum = 0;
                for (int i = s; i < e; i++) sum += buf[i];
                return sum / (e - s);
            }
        }

        private double ConvertFastVoltageToEngineering(double voltage, AiConfigDetailRecord rec)
        {
            var engineering =
                (voltage - rec.零位漂移) * rec.变换斜率 + rec.变换截距;
            if (_zeroOffsets.TryGetValue(rec.参数名, out var zeroOffset))
                engineering -= zeroOffset;
            return engineering;
        }

        #endregion



        // —— 后台线程：转工程值 + 滤波 + 更新快照 + 可选回调 —— //
        private void ControlLoop(
            string workerDevice,
            ControlBatchRing ring,
            AutoResetEvent signal)
        {
            var samples = new FastControlSampleValue[ring.MaximumSamplesPerBatch];
            var rawTail = new double[ring.MaximumSamplesPerBatch * ControlBatchRing.RawTailCapacity];
            var fastFilter = string.Equals(workerDevice, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? _fastFilterDev1
                : _fastFilterDev2;
            var deviceRecords = GetDeviceRecords(workerDevice);
            var isDev1Worker = string.Equals(workerDevice, "Dev1", StringComparison.OrdinalIgnoreCase);
            var observedResetEpoch = -1L;
            var identityValidator = new ControlBatchIdentityValidator();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    signal.WaitOne(20);
                    var resetEpoch = Interlocked.Read(
                        ref isDev1Worker
                            ? ref _controlFilterResetEpochDev1
                            : ref _controlFilterResetEpochDev2);
                    if (resetEpoch != observedResetEpoch)
                    {
                        fastFilter.Reset();
                        identityValidator.Reset();
                        observedResetEpoch = resetEpoch;
                    }
                    EvaluateControlLatency(workerDevice, ring, GetCurrentGeneration(workerDevice));
                    while (ring.TryDequeue(
                               samples,
                               rawTail,
                               out var count,
                               out var rawTailCount,
                               out var metadata))
                    {
                        if (!IsCurrentGeneration(workerDevice, metadata.Generation)) continue;
                        var quality = metadata.QualityFlags;
                        var identity = identityValidator.Validate(metadata);
                        if (identity == ControlBatchIdentityResult.GenerationChanged)
                            fastFilter.Reset();
                        else if (identity != ControlBatchIdentityResult.Accepted)
                        {
                            if (_callbackTimingDiag.TryGetValue(workerDevice, out var continuityDiag))
                            {
                                Interlocked.Exchange(
                                    ref continuityDiag.LastControlDiscontinuitySequence,
                                    metadata.SourceSequence);
                                Interlocked.Increment(ref continuityDiag.ControlDiscontinuityCount);
                            }
                            var active = IsDeviceControlActive(workerDevice);
                            if (identity == ControlBatchIdentityResult.Gap && !active)
                            {
                                fastFilter.Reset();
                                identityValidator.Accept(metadata);
                            }
                            else
                            {
                                var faultCode = "ControlSequenceDiscontinuity";
                                if (identity == ControlBatchIdentityResult.Duplicate)
                                {
                                    quality |= FastSignalQualityFlags.DuplicateBatch;
                                    faultCode = "ControlBatchDuplicate";
                                }
                                else if (identity == ControlBatchIdentityResult.OutOfOrder)
                                {
                                    quality |= FastSignalQualityFlags.OutOfOrderBatch;
                                    faultCode = "ControlBatchOutOfOrder";
                                }
                                else if (identity == ControlBatchIdentityResult.MonotonicTickInvalid)
                                {
                                    quality |= FastSignalQualityFlags.MonotonicTickInvalid;
                                    faultCode = "ControlMonotonicTickInvalid";
                                }
                                else
                                {
                                    quality |= FastSignalQualityFlags.SequenceDiscontinuity;
                                }
                                AppendDiagnostic(new DaqTimingValue
                                {
                                    TimestampUtc = DateTime.UtcNow,
                                    Device = workerDevice,
                                    Kind = active ? "HardFault" : "RejectedControlBatch",
                                    Generation = metadata.Generation,
                                    BatchSequence = metadata.SourceSequence,
                                    ProducerThreadId = metadata.ProducerThreadId,
                                    SampleLeadMs = metadata.SampleLeadMs,
                                    QualityFlags = quality.ToString(),
                                    Detail = $"{faultCode} Identity={identity}"
                                });
                                if (active)
                                {
                                PublishQueueFullFault(
                                    workerDevice,
                                    metadata.Generation,
                                        faultCode,
                                    "Control",
                                    ring.Depth,
                                    ring.Capacity,
                                    0,
                                        $"FastPathSignalInvalid Cause={faultCode} Device={workerDevice} " +
                                        $"Generation={metadata.Generation} Batch={metadata.SourceSequence} " +
                                        $"Identity={identity}，已拒绝该控制批次。"
                                );
                                }
                                continue;
                            }
                        }
                        if ((quality & (FastSignalQualityFlags.SequenceDiscontinuity |
                                        FastSignalQualityFlags.ProducerReentry |
                                        FastSignalQualityFlags.GenerationMismatch |
                                        FastSignalQualityFlags.DuplicateBatch |
                                        FastSignalQualityFlags.OutOfOrderBatch |
                                        FastSignalQualityFlags.MonotonicTickInvalid |
                                        FastSignalQualityFlags.FilterStateInvalid)) != 0)
                            continue;

                        var batchStarted = Stopwatch.GetTimestamp();
                        var subscriberMaxMs = 0.0;
                        for (var i = 0; i < count; i++)
                        {
                            var parameterName = deviceRecords[samples[i].SourceRow].参数名;
                            double filtered;
                            var sampleQuality = quality | samples[i].QualityFlags;
                            try
                            {
                                filtered = fastFilter.Update(
                                    parameterName,
                                    samples[i].RepresentativeA,
                                    metadata.CaptureMonotonicTicks);
                            }
                            catch (Exception ex)
                            {
                                sampleQuality |= FastSignalQualityFlags.FilterStateInvalid;
                                PublishQueueFullFault(
                                    workerDevice,
                                    metadata.Generation,
                                    "FastPathFilterStateInvalid",
                                    "Control",
                                    ring.Depth,
                                    ring.Capacity,
                                    0,
                                    $"FastPathSignalInvalid Cause=FastPathFilterStateInvalid " +
                                    $"Device={workerDevice} Generation={metadata.Generation} " +
                                    $"Batch={metadata.SourceSequence} Parameter={parameterName} Error={ex.Message}");
                                continue;
                            }
                            if (double.IsNaN(filtered) || double.IsInfinity(filtered))
                                sampleQuality |= FastSignalQualityFlags.NonFinite;
                            _lastFastValue[parameterName] = filtered;

                            if (samples[i].PressureId > 0)
                                _lastPressureSample[parameterName] = new PressureSample(
                                    samples[i].PressureId,
                                    filtered,
                                    metadata.SampleUtc,
                                    metadata.CaptureMonotonicTicks);

                            if (samples[i].Channel < 1 || samples[i].Channel > 12) continue;
                            var fastSample = new FastEpbCurrentSample(
                                samples[i].Channel,
                                filtered,
                                samples[i].RepresentativeA,
                                metadata.SampleUtc,
                                metadata.CaptureMonotonicTicks,
                                metadata.Generation,
                                metadata.SourceSequence,
                                sampleQuality);
                            _lastFastEpbSample[samples[i].Channel] = fastSample;
                            _fastEvidence.Append(
                                workerDevice,
                                new FastControlBatchMetadata(
                                    metadata.Generation,
                                    metadata.SourceSequence,
                                    metadata.SampleUtc,
                                    metadata.CallbackArrivalUtc,
                                    metadata.CaptureMonotonicTicks,
                                    metadata.EnqueuedMonotonicTicks,
                                    metadata.SampleLeadMs,
                                    metadata.ProducerThreadId,
                                    sampleQuality),
                                samples[i],
                                filtered,
                                rawTail,
                                i * ControlBatchRing.RawTailCapacity,
                                rawTailCount);
                            var subscriberStarted = Stopwatch.GetTimestamp();
                            try
                            {
                                OnFastEpbCurrentSample?.Invoke(fastSample);
                                OnFastEpbCurrent?.Invoke(
                                    fastSample.Channel,
                                    fastSample.CurrentA,
                                    fastSample.SampleUtc);
                            }
                            catch (Exception ex)
                            {
                                _ = Task.Run(() => _log.Warn(
                                    $"{workerDevice} 控制样本订阅者异常（已隔离）：{ex.Message}",
                                    "AI"));
                            }
                            var subscriberMs = AgeMs(subscriberStarted, Stopwatch.GetTimestamp());
                            if (subscriberMs > subscriberMaxMs) subscriberMaxMs = subscriberMs;
                        }

                        var processedTicks = Stopwatch.GetTimestamp();
                        MarkControlProcessed(
                            workerDevice,
                            processedTicks,
                            metadata.SampleUtc,
                            metadata.Generation,
                            metadata.SourceSequence);
                        var processMs = AgeMs(batchStarted, processedTicks);
                        Interlocked.Exchange(
                            ref isDev1Worker ? ref _lastControlBatchProcessMsBitsDev1 : ref _lastControlBatchProcessMsBitsDev2,
                            BitConverter.DoubleToInt64Bits(processMs));
                        Interlocked.Exchange(
                            ref isDev1Worker ? ref _subscriberMaxMsBitsDev1 : ref _subscriberMaxMsBitsDev2,
                            BitConverter.DoubleToInt64Bits(subscriberMaxMs));

                        var queueAgeMs = AgeMs(metadata.EnqueuedMonotonicTicks, batchStarted);
                        if (queueAgeMs >= _controlWarningAgeMs || processMs >= _controlWarningAgeMs)
                            TryRecordControlTiming(
                                workerDevice,
                                metadata.Generation,
                                ring,
                                queueAgeMs,
                                processMs,
                                subscriberMaxMs,
                                "ControlDispatch");
                    }
                }
            }
            catch (Exception ex)
            {
                _ = Task.Run(() =>
                    _log.Error($"{workerDevice} DAQ控制工作线程异常：{ex}", "AI", ex));
            }
        }

        private bool IsDeviceControlActive(string device)
        {
            try
            {
                var provider = Volatile.Read(ref _controlActivityProvider);
                return provider == null || provider(device);
            }
            catch
            {
                return true;
            }
        }

        private void EvaluateControlLatency(string device, ControlBatchRing ring, long generation)
        {
            var oldestTicks = ring.OldestEnqueuedMonotonicTicks;
            if (oldestTicks <= 0) return;
            var ageMs = AgeMs(oldestTicks, Stopwatch.GetTimestamp());
            var action = ControlLatencyPolicy.Evaluate(
                ageMs,
                IsDeviceControlActive(device),
                _controlWarningAgeMs,
                _controlHardFaultAgeMs);
            if (action == ControlLatencyAction.Healthy) return;

            if (action == ControlLatencyAction.ResynchronizeInactive)
            {
                var discarded = ring.DiscardAllButLatest();
                if (discarded > 0)
                    TryRecordControlTiming(
                        device,
                        generation,
                        ring,
                        ageMs,
                        0,
                        0,
                        $"InactiveResync Discarded={discarded}");
                return;
            }

            if (action == ControlLatencyAction.HardFault)
            {
                PublishQueueFullFault(
                    device,
                    generation,
                    "ControlLatencyExceeded",
                    "Control",
                    ring.Depth,
                    ring.Capacity,
                    ageMs);
                ring.DiscardAllButLatest();
                return;
            }

            TryRecordControlTiming(device, generation, ring, ageMs, 0, 0, "ControlLatencyWarning");
        }

        private void TryRecordControlTiming(
            string device,
            long generation,
            ControlBatchRing ring,
            double queueAgeMs,
            double processMs,
            double subscriberMaxMs,
            string detail)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            ref var last = ref string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? ref _lastControlWarningTicksDev1
                : ref _lastControlWarningTicksDev2;
            var previous = Interlocked.Read(ref last);
            if (previous != 0 && AgeMs(previous, nowTicks) < 1000) return;
            Interlocked.Exchange(ref last, nowTicks);
            AppendDiagnostic(new DaqTimingValue
            {
                TimestampUtc = DateTime.UtcNow,
                Device = device,
                Kind = "Control",
                Generation = generation,
                QueueDepth = ring.Depth,
                QueueAgeMs = queueAgeMs,
                ProcessingMs = processMs,
                ControlQueueCapacity = ring.Capacity,
                SubscriberMaxMs = subscriberMaxMs,
                Detail = detail
            });
            _ = Task.Run(() => _log.Warn(
                $"{device} 控制链时序异常：Depth={ring.Depth}/{ring.Capacity} " +
                $"Oldest={queueAgeMs:F1}ms Process={processMs:F1}ms SubscriberMax={subscriberMaxMs:F1}ms " +
                $"Detail={detail}",
                "AI"));
        }

        private void PublishRawSnapshot(Item item)
        {
            if (OwnedRawBatchReady == null && OnRawBatch == null)
            {
                MarkRawTransferred(item.Device, item.Sequence);
                return;
            }
            var snapshot = OwnedDaqRawBatch.CopyFrom(
                item.Device,
                item.Raw,
                item.Current,
                item.Last,
                item.Sequence);
            var count = Interlocked.Increment(ref _rawPublicationCount);
            if (count > RawPublicationCapacity)
            {
                Interlocked.Decrement(ref _rawPublicationCount);
                snapshot.Dispose();
                PublishQueueFullFault(
                    item.Device,
                    item.Generation,
                    "RawPersistenceQueueFull",
                    "RawPersistence",
                    count,
                    RawPublicationCapacity,
                    reasonOverride: $"Device={item.Device} 原始数据异步发布队列达到上限 {RawPublicationCapacity} 批。");
                return;
            }
            _rawPublicationQueue.Enqueue(snapshot);
            TrySignal(_rawPublicationSignal);
        }

        private async Task RawPublicationLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _rawPublicationSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                    while (_rawPublicationQueue.TryDequeue(out var batch))
                    {
                        Interlocked.Decrement(ref _rawPublicationCount);
                        Interlocked.Increment(ref _rawPublicationInFlight);
                        var transferred = false;
                        try
                        {
                            var ownedHandler = OwnedRawBatchReady;
                            if (ownedHandler != null)
                            {
                                ownedHandler(batch);
                                transferred = true;
                            }
                            var legacy = OnRawBatch;
                            if (legacy != null)
                            {
                                var matrix = new double[batch.ChannelCount, batch.SampleCount];
                                Buffer.BlockCopy(
                                    batch.Values,
                                    0,
                                    matrix,
                                    0,
                                    batch.ChannelCount * batch.SampleCount * sizeof(double));
                                legacy(batch.Device, matrix, batch.Current, batch.Last);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"{batch.Device} 原始批次异步订阅者异常：{ex.Message}", "AI");
                        }
                        finally
                        {
                            MarkRawTransferred(batch.Device, batch.Sequence);
                            if (!transferred) batch.Dispose();
                            Interlocked.Decrement(ref _rawPublicationInFlight);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                while (_rawPublicationQueue.TryDequeue(out var batch)) batch.Dispose();
                Interlocked.Exchange(ref _rawPublicationCount, 0);
                Interlocked.Exchange(ref _rawPublicationInFlight, 0);
            }
        }

        private void PublishLatestUi(string device, double[,] values, DateTime current, DateTime last)
        {
            var publication = new UiPublication
            {
                Device = device,
                Values = values,
                Current = current,
                Last = last
            };
            if (string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref _latestUiDev1, publication);
            else
                Interlocked.Exchange(ref _latestUiDev2, publication);
            TrySignal(_uiPublicationSignal);
        }

        private async Task UiPublicationLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _uiPublicationSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                    var dev1 = Interlocked.Exchange(ref _latestUiDev1, null);
                    var dev2 = Interlocked.Exchange(ref _latestUiDev2, null);
                    var handler = OnEngBatch;
                    if (handler == null) continue;
                    foreach (var item in new[] { dev1, dev2 })
                    {
                        if (item == null) continue;
                        try { handler(item.Device, item.Values, item.Current, item.Last); }
                        catch (Exception ex)
                        {
                            _log.Warn($"{item.Device} UI批次异步订阅者异常：{ex.Message}", "AI");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private void MarkRawTransferred(string device, long sequence)
        {
            if (string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref _rawTransferredSequenceDev1, sequence);
            else if (string.Equals(device, "Dev2", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref _rawTransferredSequenceDev2, sequence);
        }

        private async Task ProcessLoop(
            string workerDevice,
            ConcurrentQueue<Item> queue,
            SemaphoreSlim signal)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                    if (!queue.TryDequeue(out var item)) continue;
                    if (string.Equals(workerDevice, "Dev1", StringComparison.OrdinalIgnoreCase))
                        DaqQueueAdmission.Release(ref _queueCountDev1);
                    else
                        DaqQueueAdmission.Release(ref _queueCountDev2);
                    // 重启前积压的批次不再参与快照、峰值或落盘，避免旧代次数据
                    // 在新任务恢复后倒灌成“新数据”。
                    if (!IsCurrentGeneration(item.Device, item.Generation))
                    {
                        if (string.Equals(item.Device, "Dev1", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Exchange(ref _diskPublishedSequenceDev1, item.Sequence);
                        else
                            Interlocked.Exchange(ref _diskPublishedSequenceDev2, item.Sequence);
                        continue;
                    }

                    var processStartTicks = Stopwatch.GetTimestamp();
                    var queueAgeMs =
                        (processStartTicks - item.EnqueuedMonotonicTicks) * 1000.0 /
                        Stopwatch.Frequency;

                    PublishRawSnapshot(item);
                    if (queueAgeMs > 100)
                    {
                        var lastLog = workerDevice == "Dev1"
                            ? Interlocked.Read(ref _lastProcessingLagLogTicksDev1)
                            : Interlocked.Read(ref _lastProcessingLagLogTicksDev2);
                        if (lastLog <= 0 ||
                            (processStartTicks - lastLog) * 1000.0 / Stopwatch.Frequency >= 1000)
                        {
                            if (workerDevice == "Dev1")
                                Interlocked.Exchange(ref _lastProcessingLagLogTicksDev1, processStartTicks);
                            else
                                Interlocked.Exchange(ref _lastProcessingLagLogTicksDev2, processStartTicks);
                            var snapshot = new DaqProcessingSnapshot
                            {
                                Device = item.Device,
                                QueueDepth = queue.Count,
                                OldestBatchAgeMs = queueAgeMs,
                                ObservedUtc = DateTime.UtcNow
                            };
                            _log?.Warn(
                                $"DAQ后台处理积压：Device={snapshot.Device} " +
                                $"QueueDepth={snapshot.QueueDepth} " +
                                $"OldestBatchAge={snapshot.OldestBatchAgeMs:F1}ms。",
                                "AI");
                            try { ProcessingLagDetected?.Invoke(snapshot); }
                            catch { /* 诊断订阅者不得影响采集处理。 */ }
                        }
                    }

                    var convertStartedTicks = Stopwatch.GetTimestamp();
                    // 转工程值（使用配置）
                    ConvertToEngineeringInPlace(item.Raw, item.Device);
                    var eng = item.Raw;
                    var convertMs = (Stopwatch.GetTimestamp() - convertStartedTicks) * 1000.0 / Stopwatch.Frequency;
                    // 滤波（每通道独立中值/降点，与你项目一致）
                    //var engFiltered = MedianFilterEachChannel(eng, _medianLens); // 暂时去掉滤波
                    //var engFiltered = eng;


                    // 因果中值滤波（无延迟，控制用）
                    var filterStartedTicks = Stopwatch.GetTimestamp();
                    if (item.Device.Equals("Dev1"))
                        _dev1MedianCausal.ProcessInPlace(eng);
                    else
                        _dev2MedianCausal.ProcessInPlace(eng);

                    var engFiltered = eng;
                    var filterMs = (Stopwatch.GetTimestamp() - filterStartedTicks) * 1000.0 / Stopwatch.Frequency;
                    var diskBuildStartedTicks = Stopwatch.GetTimestamp();
                    var peakMs = 0.0;
                    var diskDispatchMs = 0.0;


                    // 刷新“最近值”供控制逻辑查询（**改动：写入 _lastFilteredValue**）
                    UpdateLastSnapshot(engFiltered, item.Device, item.Current.ToUniversalTime());

                    // —— fast 快照语义 ——
                    // - 当 fast 来源为 DaqCallback：fast 由 DAQ 回调线程更新，后台线程不得覆盖；
                    // - 当 fast 来源为 ProcessLoopFiltered*：fast 由后台线程从滤波矩阵提升生成。
                    if (_fastSource != FastSource.DaqCallback)
                    {
                        PromoteFilteredToFastForCurrents(engFiltered, item.Device, item.Current);
                    }

                    #region 生成“落盘批次”并触发 OnDiskBatch（使用 engFiltered，不取绝对值） On 2025.09.16 

                    // ====== 生成“落盘批次”并触发 OnDiskBatch（使用 engFiltered，不取绝对值） ======
                    DateTime[] pooledTimestamps = null;
                    DaqDiskChannelBatch[] pooledCurrents = null;
                    var pooledCurrentCount = 0;
                    double[] pooledPressure1 = null;
                    double[] pooledPressure2 = null;
                    DaqDiskBatch ownedDiskBatch = null;
                    try
                    {
                        // 1) 计算时间戳数组（以本批最后一个样本对齐 item.Current，向前按 Fs 均匀回推）
                        var n = engFiltered.GetLength(1);
                        var tsUtc = pooledTimestamps = ArrayPool<DateTime>.Shared.Rent(n);
                        var archiveSampleRate = item.EffectiveSampleRateHz > 0 &&
                                                !double.IsNaN(item.EffectiveSampleRateHz) &&
                                                !double.IsInfinity(item.EffectiveSampleRateHz)
                            ? item.EffectiveSampleRateHz
                            : _sampleRate;
                        HighResolutionSampleClock.FillBatchTimestamps(
                            item.Current.ToUniversalTime(), tsUtc, n, archiveSampleRate);

                        // 2) 构建“每 EPB 通道”的电流数组（从当前 device 的工程值矩阵提取）
                        var devRecs = GetDeviceRecords(item.Device);
                        var currentsByEpb = pooledCurrents =
                            ArrayPool<DaqDiskChannelBatch>.Shared.Rent(Math.Max(1, devRecs.Length));
                        var chCount = engFiltered.GetLength(0);
                        for (int c = 0; c < chCount; c++)
                        {
                            var name = devRecs[c].参数名; // 形如 EPB1_current / Pressure_1
                            int epb = TryParseEpbChannel(name);
                            if (epb >= 1 && epb <= 12)
                            {
                                var arr = ArrayPool<double>.Shared.Rent(n);
                                for (int i = 0; i < n; i++) arr[i] = engFiltered[c, i]; // 不取绝对值
                                currentsByEpb[pooledCurrentCount++] = new DaqDiskChannelBatch(epb, arr);
                            }
                        }

                        // 3) 提取两组压力（多名称兜底：Pressure_1/2、Hyd1/2_pressure、P1/2）
                        int colP1 = FindColumnIndex(devRecs, "Pressure_1", "Hyd1_pressure", "P1", "Pressure1");
                        int colP2 = FindColumnIndex(devRecs, "Pressure_2", "Hyd2_pressure", "P2", "Pressure2");

                        double[] pressure1 = null, pressure2 = null;
                        if (colP1 >= 0)
                        {
                            pressure1 = pooledPressure1 = ArrayPool<double>.Shared.Rent(n);
                            for (int i = 0; i < n; i++) pressure1[i] = engFiltered[colP1, i];
                        }
                        if (colP2 >= 0)
                        {
                            pressure2 = pooledPressure2 = ArrayPool<double>.Shared.Rent(n);
                            for (int i = 0; i < n; i++) pressure2[i] = engFiltered[colP2, i];
                        }


                        #region 获取一段时间内的最大值

                        // === 基于“全数据”的峰值捕获：逐样本扫描（仅对处于捕获状态的通道进行） ===
                        var peakStartedTicks = Stopwatch.GetTimestamp();
                        try
                        {
                            if (AnyPeakArmed && tsUtc != null && tsUtc.Length > 0)
                            {
                                for (var currentIndex = 0; currentIndex < pooledCurrentCount; currentIndex++)
                                {
                                    var kv = currentsByEpb[currentIndex];
                                    int epb = kv.EpbId;              // 1..12
                                    var data = kv.Currents;           // double[n]
                                    PeakTracker tracker;
                                    if (!_peakTrackers.TryGetValue(epb, out tracker)) continue;

                                    // 仅对“已开始捕获”的通道更新
                                    bool active;
                                    lock (tracker.Sync) active = tracker.Active;
                                    if (!active) continue;

                                    // 逐样本纳入峰值统计（时间转为本地时间）
                                    for (int i = 0; i < n; i++)
                                    {
                                        // tsUtc 与 data 一一对应
                                        var tLocal = tsUtc[i].ToLocalTime();
                                        var amp = data[i];
                                        lock (tracker.Sync)
                                        {
                                            if (tracker.Active) tracker.Update(Math.Abs(amp), tLocal);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn($"全数据峰值捕获更新异常（已忽略）：{ex.Message}", "AI");
                        }
                        peakMs = (Stopwatch.GetTimestamp() - peakStartedTicks) * 1000.0 / Stopwatch.Frequency;

                        #endregion



                        // 4) 所有权转移给独立持久化队列；当前线程只做 O(1) 投递。
                        for (var c = 0; c < pooledCurrentCount; c++)
                        {
                            var data = currentsByEpb[c].Currents;
                            for (var i = 0; i < n; i++) data[i] = Math.Abs(data[i]);
                        }
                        var diskBatch = ownedDiskBatch = DaqDiskBatch.Rent(
                            item.Device,
                            item.Generation,
                            item.Sequence,
                            n,
                            tsUtc,
                            currentsByEpb,
                            pooledCurrentCount,
                            pressure1,
                            pressure2,
                            Stopwatch.GetTimestamp(),
                            archiveSampleRate,
                            item.ClockState,
                            item.EstimatedSkewPpm,
                            item.ClockResidualMs,
                            item.ClockWindowSeconds);
                        var transferred = false;
                        try
                        {
                            var dispatchStartedTicks = Stopwatch.GetTimestamp();
                            // 兼容订阅者必须在独占所有权转移前复制；持久化消费者可能在
                            // DiskBatchReady 返回后立即 Dispose 并归还池化数组。
                            var legacy = OnDiskBatch;
                            if (legacy != null)
                            {
                                var legacyTs = new DateTime[n];
                                Array.Copy(tsUtc, legacyTs, n);
                                var legacyCurrents = new Dictionary<int, double[]>();
                                for (var channelIndex = 0; channelIndex < pooledCurrentCount; channelIndex++)
                                {
                                    var channel = currentsByEpb[channelIndex];
                                    var copy = new double[n];
                                    Array.Copy(channel.Currents, copy, n);
                                    legacyCurrents[channel.EpbId] = copy;
                                }
                                double[] p1 = null, p2 = null;
                                if (pressure1 != null) { p1 = new double[n]; Array.Copy(pressure1, p1, n); }
                                if (pressure2 != null) { p2 = new double[n]; Array.Copy(pressure2, p2, n); }
                                legacy(item.Device, legacyTs, legacyCurrents, p1, p2);
                            }

                            var handler = DiskBatchReady;
                            if (handler != null)
                            {
                                handler(diskBatch);
                                transferred = true;
                            }
                            diskDispatchMs = (Stopwatch.GetTimestamp() - dispatchStartedTicks) * 1000.0 / Stopwatch.Frequency;
                        }
                        finally
                        {
                            if (string.Equals(item.Device, "Dev1", StringComparison.OrdinalIgnoreCase))
                                Interlocked.Exchange(ref _diskPublishedSequenceDev1, item.Sequence);
                            else
                                Interlocked.Exchange(ref _diskPublishedSequenceDev2, item.Sequence);
                            if (!transferred) diskBatch.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ownedDiskBatch == null)
                        {
                            if (pooledTimestamps != null)
                                ArrayPool<DateTime>.Shared.Return(pooledTimestamps, clearArray: false);
                            if (pooledCurrents != null)
                            {
                                for (var i = 0; i < pooledCurrentCount; i++)
                                    if (pooledCurrents[i].Currents != null)
                                        ArrayPool<double>.Shared.Return(pooledCurrents[i].Currents, clearArray: false);
                                ArrayPool<DaqDiskChannelBatch>.Shared.Return(pooledCurrents, clearArray: true);
                            }
                            if (pooledPressure1 != null)
                                ArrayPool<double>.Shared.Return(pooledPressure1, clearArray: false);
                            if (pooledPressure2 != null)
                                ArrayPool<double>.Shared.Return(pooledPressure2, clearArray: false);
                        }
                        _log?.Warn($"生成写盘批次时出现异常（已忽略）：{ex.Message}", "AI");
                    }
                    finally
                    {
                        if (string.Equals(item.Device, "Dev1", StringComparison.OrdinalIgnoreCase))
                            Interlocked.Exchange(ref _diskPublishedSequenceDev1, item.Sequence);
                        else
                            Interlocked.Exchange(ref _diskPublishedSequenceDev2, item.Sequence);
                    }
                    

                    #endregion
                    var diskBatchBuildMs =
                        (Stopwatch.GetTimestamp() - diskBuildStartedTicks) * 1000.0 / Stopwatch.Frequency;

                    var uiStartedTicks = Stopwatch.GetTimestamp();
                    var uiGate = string.Equals(item.Device, "Dev1", StringComparison.OrdinalIgnoreCase)
                        ? _uiDispatchGateDev1
                        : _uiDispatchGateDev2;
                    // UI 只需要 20~30 Hz 的最新趋势。仅在真正发布时创建绝对值矩阵，
                    // 避免两台设备每 10 ms 各分配一个二维数组并推动全代 GC。
                    if (OnEngBatch != null && uiGate.TryAcquire(uiStartedTicks))
                    {
                        var uiEng = MakeEngineeringAbsoluteCopy(engFiltered);
                        PublishLatestUi(item.Device, uiEng, item.Current, item.Last);
                    }
                    var uiNotifyMs = (Stopwatch.GetTimestamp() - uiStartedTicks) * 1000.0 / Stopwatch.Frequency;

                    AppendDiagnostic(new DaqTimingValue
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Device = item.Device,
                        Kind = "Processing",
                        Generation = item.Generation,
                        BatchSize = item.Raw.GetLength(1),
                        QueueDepth = queue.Count,
                        QueueAgeMs = queueAgeMs,
                        ProcessingMs = (Stopwatch.GetTimestamp() - processStartTicks) * 1000.0 / Stopwatch.Frequency,
                        ConvertMs = convertMs,
                        FilterMs = filterMs,
                        PeakMs = peakMs,
                        DiskBatchBuildMs = diskBatchBuildMs,
                        DiskDispatchMs = diskDispatchMs,
                        UiNotifyMs = uiNotifyMs
                    });

                    //OnFastEpbCurrent?.Invoke(epbCh, eng, item.Current);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"AI 后台处理异常：{ex}", "AI", ex);
            }
        }


        /// <summary>
        /// 将“滤波后的工程值矩阵（engFiltered）”中的 EPB 电流，提升为“fast 快照”
        /// （以最后一个样本为 fast 值），并触发 <see cref="OnFastEpbCurrent"/>。
        /// 仅对 EPB 电流生效（参数名形如 EPB#_current）；其它参数名不修改 fast。
        /// </summary>
        /// <param name="engFiltered">滤波后的工程值矩阵（channels x samples）。</param>
        /// <param name="device">设备名（"Dev1" / "Dev2"）。</param>
        /// <param name="ts">此批的代表时间戳（通常为批尾对齐时间）。</param>
        private void PromoteFilteredToFastForCurrentsOld(double[,] engFiltered, string device, DateTime ts)
        {
            if (engFiltered == null) return;

            // 本 device 的通道描述：与 UpdateLastSnapshot 同样的枚举顺序
            var devRecs = GetDeviceRecords(device);

            int ch = engFiltered.GetLength(0);
            int n = engFiltered.GetLength(1);
            if (n <= 0) return;

            // 仅 EPB 电流：提升为 fast 值 = 滤波后的最后一个样本，并触发 OnFastEpbCurrent
            for (int c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                int epb = TryParseEpbChannel(rec.参数名);
                if (epb >= 1 && epb <= 12)
                {
                    double v = engFiltered[c, n - 1]; // 最后一个样本
                    _lastFastValue[rec.参数名] = v;   // 用 filtered 覆盖 fast

                    // 与回调线程版本保持一致：上报“低时延事件”，但现在是“滤波后”的快照
                    try { OnFastEpbCurrent?.Invoke(epb, v, ts); } catch { /* 忽略订阅侧异常 */ }
                }
            }
        }


        /// <summary>
        /// 将“滤波后的工程值矩阵（engFiltered）”中的 EPB 电流，提升为“fast 快照”。
        /// 代表值的获取策略由 <see cref="_fastSource"/> 决定：最后样本 / 批内最大值 / 批内中位数。
        /// 仅对 EPB 电流（形如 EPB#_current）生效，其他通道不改动。
        /// </summary>
        private void PromoteFilteredToFastForCurrents(double[,] engFiltered, string device, DateTime ts)
        {
            if (engFiltered == null) return;

            var devRecs = GetDeviceRecords(device);

            int ch = engFiltered.GetLength(0);
            int n = engFiltered.GetLength(1);
            if (n <= 0) return;

            for (int c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                int epb = TryParseEpbChannel(rec.参数名);
                if (epb < 1 || epb > 12) continue; // 只针对 EPB 电流

                double v = GetRepresentative(engFiltered, c, n, _fastSource);

                _lastFastValue[rec.参数名] = v;

                try { OnFastEpbCurrent?.Invoke(epb, v, ts); } catch { /* 忽略订阅侧异常 */ }
            }
        }

        /// <summary>
        /// 按 <paramref name="source"/> 选取当前批（长度 n）的代表值：
        /// - ProcessLoopFilteredLast   : 最后一个样本；
        /// - ProcessLoopFilteredMax    : 批内最大值；
        /// - ProcessLoopFilteredMedian : 批内中位数（偶数个样本取中间两数平均）；
        /// - 其余（如 DaqCallback）    : 回退到最后一个样本（以免空值）。
        /// </summary>
        /// <param name="engFiltered">滤波后矩阵（channels x samples）。</param>
        /// <param name="row">通道索引（行）。</param>
        /// <param name="n">本批样本数。</param>
        /// <param name="source">代表值策略。</param>
        private static double GetRepresentative(double[,] engFiltered, int row, int n, FastSource source)
        {
            switch (source)
            {
                case FastSource.ProcessLoopFilteredMax:
                    {
                        double max = double.NegativeInfinity;
                        for (int i = 0; i < n; i++)
                        {
                            double x = engFiltered[row, i];
                            if (x > max) max = x;
                        }
                        return max;
                    }

                case FastSource.ProcessLoopFilteredMedian:
                    {
                        // 为避免每批都分配新数组，使用 ArrayPool<double>
                        var pool = ArrayPool<double>.Shared;
                        double[] buf = null;
                        try
                        {
                            buf = pool.Rent(n);
                            for (int i = 0; i < n; i++)
                                buf[i] = engFiltered[row, i];

                            // 只对前 n 个元素排序
                            Array.Sort(buf, 0, n);

                            if ((n & 1) == 1) // 奇数
                                return buf[n / 2];
                            else              // 偶数：取中间两数平均
                                return 0.5 * (buf[n / 2 - 1] + buf[n / 2]);
                        }
                        finally
                        {
                            if (buf != null) pool.Return(buf, clearArray: false);
                        }
                    }

                case FastSource.ProcessLoopFilteredLast:
                default:
                    return engFiltered[row, n - 1];
            }
        }





        /// <summary>
        /// 在 devRecs（本 device 的通道描述）中按多个“候选参数名”查找列索引；找不到返回 -1。
        /// </summary>
        private static int FindColumnIndex(IReadOnlyList<AiConfigDetailRecord> devRecs, params string[] candidateNames)
        {
            if (devRecs == null || candidateNames == null) return -1;
            for (int c = 0; c < devRecs.Count; c++)
            {
                var name = devRecs[c].参数名;
                foreach (var key in candidateNames)
                {
                    if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }
            return -1;
        }


        /// <summary>
        ///     返回一个新的二维数组，该数组为源数组元素的绝对值副本，源数组不被修改。
        ///     适用于 channels x samples 的 double[,] 格式数据。
        /// </summary>
        /// <param name="eng">源工程值数组（channels x samples），允许为 null。</param>
        /// <returns>
        ///     新的二维数组（与源数组维度相同）或 null（当源为 null 时）。
        /// </returns>
        private static double[,] MakeEngineeringAbsoluteCopy(double[,] eng)
        {
            if (eng == null) return null;

            var dim0 = eng.GetLength(0);
            var dim1 = eng.GetLength(1);
            var copy = new double[dim0, dim1];
            // 显示工程值保持原始行为：仅取绝对值，不根据输出状态强制置零。
            for (var i = 0; i < dim0; i++)
                for (var j = 0; j < dim1; j++)
                    copy[i, j] = Math.Abs(eng[i, j]);

            return copy;
        }

        // —— 工程值转换 & 快照 —— //
        private void ConvertToEngineeringInPlace(double[,] raw, string device)
        {
            var ch = raw.GetLength(0);
            var n = raw.GetLength(1);

            var devRecs = GetDeviceRecords(device);

            for (var c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                var scale = rec.变换斜率;
                var offs = rec.变换截距;
                var zero = rec.零位漂移;
                _zeroOffsets.TryGetValue(rec.参数名, out var dynamicZero);

                for (var i = 0; i < n; i++)
                {
                    var v = raw[c, i];
                    raw[c, i] = (v - zero) * scale + offs;

                    // —— 新增：应用动态置零（工程值维度）——
                    raw[c, i] -= dynamicZero;
                }
            }
        }

        private double[,] MedianFilterEachChannel(double[,] src, int medianLens)
        {
            var ch = src.GetLength(0);
            var n = src.GetLength(1);
            var dst = new double[ch, n];

            for (var c = 0; c < ch; c++)
            {
                // 拆出 1 列
                var buf = new double[n];
                for (var i = 0; i < n; i++) buf[i] = src[c, i];

                // 走你项目里的滤波（ClsDataFilter）
                var filt = ClsDataFilter.MakeMedianFilterReducePoint(ref buf, medianLens);

                // 填回
                var copyLen = Math.Min(filt.Length, n);
                for (var i = 0; i < copyLen; i++) dst[c, i] = filt[i];
                // 若降点长度变短，尾部补最后一个样本
                for (var i = copyLen; i < n; i++) dst[c, i] = filt[copyLen - 1];
            }

            return dst;
        }

        /// <summary>
        ///     将已滤波的工程值最后一个样本写入到 _lastFilteredValue（UI/统计用）。
        ///     与以前不同：不再覆盖 _lastFastValue（以避免破坏控制用的低延迟读数）。
        /// </summary>
        /// <param name="engFiltered">滤波后的工程值矩阵（channels x samples）。</param>
        /// <param name="device">设备名（"Dev1" 或 "Dev2"）。</param>
        private void UpdateLastSnapshot(double[,] engFiltered, string device, DateTime batchUtc)
        {
            var devRecs = GetDeviceRecords(device);

            var ch = engFiltered.GetLength(0);
            var n = engFiltered.GetLength(1);
            for (var c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                // 写入滤波后的快照（不覆盖 fast）
                var value = engFiltered[c, n - 1];
                _lastFilteredValue[rec.参数名] = value;
                // 压力安全快照只允许由 DAQ 回调低时延路径刷新。后台队列可能积压，
                // 不能把处理时刻冒充采样新鲜度。
            }
        }

        private static bool TryParsePressureId(string parameterName, out int pressureId)
        {
            pressureId = 0;
            if (string.IsNullOrWhiteSpace(parameterName)) return false;
            const string prefix = "Pressure_";
            return parameterName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   int.TryParse(parameterName.Substring(prefix.Length), out pressureId) &&
                   pressureId > 0;
        }

        // —— 工具 —— //
        private NIDaqTask CreateAiTask(string name, string[] channels, double aiMin, double aiMax,
            AITerminalConfiguration term)
        {
            var task = new NIDaqTask(name);
            foreach (var ch in channels)
                task.AIChannels.CreateVoltageChannel(ch, "", term, aiMin, aiMax, AIVoltageUnits.Volts);

            task.Timing.ConfigureSampleClock("", _sampleRate, SampleClockActiveEdge.Rising,
                SampleQuantityMode.ContinuousSamples, _samplesPerChannel);
            //task.Stream.ConfigureInputBuffer(0);

            task.Control(TaskAction.Verify);
            return task;
        }

        private void BuildColumnIndex(IEnumerable<AiConfigDetailRecord> all, string dev, string[] physicals,
            Dictionary<string, int> dict)
        {
            var devRecs = all.Where(r => r.物理通道.StartsWith(dev + "/")).OrderBy(r => r.序号).ToList();
            for (var i = 0; i < physicals.Length; i++)
            {
                var rec = devRecs[i];
                dict[rec.参数名] = i;
            }
        }

        private AiConfigDetailRecord[] GetDeviceRecords(string device)
        {
            return string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? _dev1Records
                : _dev2Records;
        }

        private bool IsCurrentGeneration(string device, long generation)
        {
            return string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                ? Interlocked.Read(ref _generationDev1) == generation
                : string.Equals(device, "Dev2", StringComparison.OrdinalIgnoreCase) &&
                  Interlocked.Read(ref _generationDev2) == generation;
        }

        private bool EnqueueForProcessing(Item item)
        {
            if (string.Equals(item.Device, "Dev1", StringComparison.OrdinalIgnoreCase))
            {
                if (!DaqQueueAdmission.TryEnter(ref _queueCountDev1, _processingQueueCapacity))
                {
                    var depth = Volatile.Read(ref _queueCountDev1);
                    var oldestAgeMs = _queueDev1.TryPeek(out var oldest)
                        ? AgeMs(oldest.EnqueuedMonotonicTicks, Stopwatch.GetTimestamp())
                        : 0;
                    PublishQueueFullFault(
                        item.Device,
                        item.Generation,
                        "BackgroundQueueFull",
                        "Background",
                        depth,
                        _processingQueueCapacity,
                        oldestAgeMs);
                    return false;
                }
                _queueDev1.Enqueue(item);
                TrySignal(_queueSignalDev1);
            }
            else
            {
                if (!DaqQueueAdmission.TryEnter(ref _queueCountDev2, _processingQueueCapacity))
                {
                    var depth = Volatile.Read(ref _queueCountDev2);
                    var oldestAgeMs = _queueDev2.TryPeek(out var oldest)
                        ? AgeMs(oldest.EnqueuedMonotonicTicks, Stopwatch.GetTimestamp())
                        : 0;
                    PublishQueueFullFault(
                        item.Device,
                        item.Generation,
                        "BackgroundQueueFull",
                        "Background",
                        depth,
                        _processingQueueCapacity,
                        oldestAgeMs);
                    return false;
                }
                _queueDev2.Enqueue(item);
                TrySignal(_queueSignalDev2);
            }
            return true;
        }

        private bool EnqueueForControl(
            string device,
            FastControlBatchMetadata metadata,
            ReadOnlySpan<FastControlSampleValue> samples,
            double[,] raw)
        {
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var ring = isDev1 ? _controlRingDev1 : _controlRingDev2;
            if (!ring.TryEnqueue(metadata, samples, raw))
            {
                if (!IsDeviceControlActive(device))
                {
                    ring.DiscardAllButLatest();
                    if (!ring.TryEnqueue(metadata, samples, raw))
                        return false;
                    TryRecordControlTiming(
                        device,
                        metadata.Generation,
                        ring,
                        _controlHardFaultAgeMs,
                        0,
                        0,
                        "InactiveCapacityResync");
                }
                else
                {
                    PublishQueueFullFault(
                        device,
                        metadata.Generation,
                        "ControlQueueFull",
                        "Control",
                        ring.Depth,
                        ring.Capacity,
                        AgeMs(ring.OldestEnqueuedMonotonicTicks, Stopwatch.GetTimestamp()));
                    return false;
                }
            }
            TrySignal(isDev1 ? _controlSignalDev1 : _controlSignalDev2);
            EvaluateControlLatency(device, ring, metadata.Generation);
            return true;
        }

        private void PublishQueueFullFault(
            string device,
            long generation,
            string code,
            string queueKind = "Background",
            int queueDepth = 0,
            int queueCapacity = 0,
            double oldestBatchAgeMs = 0,
            string reasonOverride = null)
        {
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            ref var latchedGeneration = ref isDev1
                ? ref _queueFaultGenerationDev1
                : ref _queueFaultGenerationDev2;
            if (string.Equals(code, "ControlQueueFull", StringComparison.OrdinalIgnoreCase))
                latchedGeneration = ref isDev1
                    ? ref _controlFullFaultGenerationDev1
                    : ref _controlFullFaultGenerationDev2;
            else if (string.Equals(code, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase))
                latchedGeneration = ref isDev1
                    ? ref _controlLatencyFaultGenerationDev1
                    : ref _controlLatencyFaultGenerationDev2;
            else if (IsControlInvariantFault(code))
                latchedGeneration = ref isDev1
                    ? ref _controlInvariantFaultGenerationDev1
                    : ref _controlInvariantFaultGenerationDev2;
            else if (string.Equals(code, "DaqClockModelInvalid", StringComparison.OrdinalIgnoreCase))
                latchedGeneration = ref isDev1
                    ? ref _clockFaultGenerationDev1
                    : ref _clockFaultGenerationDev2;
            if (Interlocked.Exchange(ref latchedGeneration, generation) == generation) return;
            if (queueCapacity <= 0) queueCapacity = _processingQueueCapacity;
            if (queueDepth <= 0)
                queueDepth = string.Equals(queueKind, "Control", StringComparison.OrdinalIgnoreCase)
                    ? (string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                        ? _controlRingDev1.Depth
                        : _controlRingDev2.Depth)
                    : (string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase)
                        ? Volatile.Read(ref _queueCountDev1)
                        : Volatile.Read(ref _queueCountDev2));
            var processedUtc = default(DateTime);
            var diagnosticBatchSequence = 0L;
            var diagnosticProducerReentryCount = 0L;
            var diagnosticSampleLeadMs = 0.0;
            if (_callbackTimingDiag.TryGetValue(device, out var diag))
            {
                var ticks = Interlocked.Read(ref diag.LastProcessedSampleUtcTicks);
                if (ticks > 0) processedUtc = new DateTime(ticks, DateTimeKind.Utc);
                diagnosticBatchSequence = Interlocked.Read(ref diag.LastBatchSequence);
                diagnosticProducerReentryCount = Interlocked.Read(ref diag.ProducerReentryCount);
                diagnosticSampleLeadMs = BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.LastSampleLeadMsBits));
            }
            var reason = !string.IsNullOrWhiteSpace(reasonOverride)
                ? reasonOverride
                : string.Equals(code, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase)
                ? $"Device={device} Generation={generation} 控制消费延迟超过{_controlHardFaultAgeMs:F0}ms。"
                : string.Equals(queueKind, "Control", StringComparison.OrdinalIgnoreCase)
                    ? $"Device={device} Generation={generation} DAQ实时控制有界队列已满，禁止静默丢弃。"
                    : $"Device={device} Generation={generation} DAQ后台有界队列已满，禁止静默丢弃。";
            var fault = new DaqDeviceFault
            {
                Device = device,
                Code = code,
                Reason = reason,
                Generation = generation,
                TimestampUtc = DateTime.UtcNow,
                QueueKind = queueKind,
                QueueType = string.Equals(queueKind, "Control", StringComparison.OrdinalIgnoreCase)
                    ? "PreallocatedSpscRing"
                    : "BoundedConcurrentQueue",
                QueueDepth = queueDepth,
                QueueCapacity = queueCapacity,
                OldestBatchAgeMs = oldestBatchAgeMs,
                LastProcessedSampleUtc = processedUtc,
                Classification = FaultClassification.SoftwareTransient
            };
            // 安全处理器必须先于日志、诊断和快照同步收到故障。
            try { DeviceFaultDetected?.Invoke(fault); } catch { }
            AppendDiagnostic(new DaqTimingValue
            {
                TimestampUtc = fault.TimestampUtc,
                Device = device,
                Kind = "SoftwareFault",
                Generation = generation,
                QueueDepth = queueDepth,
                QueueAgeMs = oldestBatchAgeMs,
                ControlQueueCapacity = string.Equals(queueKind, "Control", StringComparison.OrdinalIgnoreCase)
                    ? queueCapacity
                    : 0,
                BatchSequence = diagnosticBatchSequence,
                ProducerThreadId = Thread.CurrentThread.ManagedThreadId,
                ProducerReentryCount = diagnosticProducerReentryCount,
                SampleLeadMs = diagnosticSampleLeadMs,
                EffectiveSampleRateHz = diag == null ? 0 : BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EffectiveSampleRateHzBits)),
                EstimatedSkewPpm = diag == null ? 0 : BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.EstimatedSkewPpmBits)),
                ClockResidualMs = diag == null ? 0 : BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockResidualMsBits)),
                ClockWindowSeconds = diag == null ? 0 : BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockWindowSecondsBits)),
                ClockCorrectionPpm = diag == null ? 0 : BitConverter.Int64BitsToDouble(
                    Interlocked.Read(ref diag.ClockCorrectionPpmBits)),
                ClockState = diag == null
                    ? string.Empty
                    : ((ClockState)Volatile.Read(ref diag.ClockState)).ToString(),
                QualityFlags = GetInvariantQualityFlag(code).ToString(),
                Detail = fault.Reason
            });
            // 后续事故归并、日志、UI 与快照均转移到后台，控制线程到此即可返回。
            _ = Task.Run(() =>
            {
                try { DeviceFaultPublicationRequested?.Invoke(fault); } catch { }
                _log.Error(fault.Reason, "AI");
            });
        }

        private static bool IsControlInvariantFault(string code)
        {
            return string.Equals(code, "ControlProducerReentry", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "ControlSequenceDiscontinuity", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "ControlBatchDuplicate", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "ControlBatchOutOfOrder", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "ControlMonotonicTickInvalid", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "FastPathFilterStateInvalid", StringComparison.OrdinalIgnoreCase);
        }

        private static FastSignalQualityFlags GetInvariantQualityFlag(string code)
        {
            if (string.Equals(code, "ControlProducerReentry", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.ProducerReentry;
            if (string.Equals(code, "ControlBatchDuplicate", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.DuplicateBatch;
            if (string.Equals(code, "ControlBatchOutOfOrder", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.OutOfOrderBatch;
            if (string.Equals(code, "ControlMonotonicTickInvalid", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.MonotonicTickInvalid;
            if (string.Equals(code, "FastPathFilterStateInvalid", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.FilterStateInvalid;
            if (string.Equals(code, "ControlSequenceDiscontinuity", StringComparison.OrdinalIgnoreCase))
                return FastSignalQualityFlags.SequenceDiscontinuity;
            return FastSignalQualityFlags.None;
        }

        private void ResetFreshness(string device)
        {
            if (!_callbackTimingDiag.TryGetValue(device, out var diag)) return;
            Interlocked.Exchange(ref diag.LastCallbackEntrySwTick, 0);
            Interlocked.Exchange(ref diag.LastControlEnqueuedSwTick, 0);
            Interlocked.Exchange(ref diag.LastSampleCommitSwTick, 0);
            Interlocked.Exchange(ref diag.LastProcessedSampleUtcTicks, 0);
            Interlocked.Exchange(ref diag.LastCbIntervalMsBits, 0);
            Interlocked.Exchange(ref diag.LastArrivalDelayMsBits, 0);
            Interlocked.Exchange(ref diag.LastBatchSequence, 0);
            Interlocked.Exchange(ref diag.LastProcessedSequence, 0);
            Interlocked.Exchange(ref diag.ControlDiscontinuityCount, 0);
            Interlocked.Exchange(ref diag.LastControlDiscontinuitySequence, 0);
            Interlocked.Exchange(ref diag.LastGeneration, 0);
            Interlocked.Exchange(ref diag.ProducerReentryCount, 0);
            Interlocked.Exchange(ref diag.LastSampleLeadMsBits, 0);
            Interlocked.Exchange(ref diag.EffectiveSampleRateHzBits, 0);
            Interlocked.Exchange(ref diag.EstimatedSkewPpmBits, 0);
            Interlocked.Exchange(ref diag.ClockResidualMsBits, 0);
            Interlocked.Exchange(ref diag.ClockWindowSecondsBits, 0);
            Interlocked.Exchange(ref diag.ClockCorrectionPpmBits, 0);
            Volatile.Write(ref diag.ClockState, (int)ClockState.WarmingUp);
        }

        private void StartDevice(string device)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TwoDeviceAiAcquirer));
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var channels = isDev1 ? _dev1Channels : _dev2Channels;
            if (channels.Length == 0) return;
            var gate = isDev1 ? _taskGateDev1 : _taskGateDev2;
            lock (gate)
            {
                var generation = isDev1
                    ? Interlocked.Increment(ref _generationDev1)
                    : Interlocked.Increment(ref _generationDev2);
                (isDev1 ? _controlRingDev1 : _controlRingDev2).Reset();
                ResetFreshness(device);
                var taskName = $"{device}_AI_g{generation}";
                NIDaqTask task = null;
                try
                {
                    task = CreateAiTask(
                        taskName, channels, _aiMin, _aiMax, _terminalConfiguration);
                    // 每台设备在真正启动硬件前建立独立时间原点。累计样本时间只从该原点推进，
                    // 回调追赶不得再把样本时间贴到主机当前时间后继续向未来累加。
                    var acquisitionStartTick = Stopwatch.GetTimestamp();
                    var acquisitionStartUtc = DateTime.UtcNow;
                    task.Start();
                    var nominalSampleRate = _sampleRate;
                    try
                    {
                        var coercedRate = task.Timing.SampleClockRate;
                        if (coercedRate > 0 && !double.IsNaN(coercedRate) && !double.IsInfinity(coercedRate))
                            nominalSampleRate = coercedRate;
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"{device} 无法读取DAQ强制采样率，使用配置值 {_sampleRate:F6}Hz：{ex.Message}", "AI");
                    }
                    var reader = new AnalogMultiChannelReader(task.Stream)
                    {
                        SynchronizeCallbacks = false
                    };
                    var state = new DeviceReadState(_clockDisciplineOptions)
                    {
                        Device = device,
                        Generation = generation,
                        Task = task,
                        Reader = reader,
                        NominalSampleRateHz = nominalSampleRate
                    };
                    state.Timeline.Reset(acquisitionStartUtc, acquisitionStartTick, nominalSampleRate);
                    AppendDiagnostic(new DaqTimingValue
                    {
                        TimestampUtc = acquisitionStartUtc,
                        Device = device,
                        Kind = "ClockStart",
                        Generation = generation,
                        EffectiveSampleRateHz = nominalSampleRate,
                        ClockState = ClockState.WarmingUp.ToString(),
                        Detail = $"ConfiguredRateHz={_sampleRate:F6}; CoercedRateHz={nominalSampleRate:F6}"
                    });
                    if (isDev1)
                    {
                        _task1 = task;
                        _reader1 = reader;
                        _readState1 = state;
                    }
                    else
                    {
                        _task2 = task;
                        _reader2 = reader;
                        _readState2 = state;
                    }
                    reader.BeginReadMultiSample(
                        _samplesPerChannel,
                        isDev1 ? Dev1Callback : Dev2Callback,
                        state);
                }
                catch
                {
                    if (isDev1) { _task1 = null; _reader1 = null; _readState1 = null; }
                    else { _task2 = null; _reader2 = null; _readState2 = null; }
                    try { task?.Dispose(); } catch { }
                    throw;
                }
            }
        }

        private void StopDevice(string device)
        {
            var isDev1 = string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase);
            var gate = isDev1 ? _taskGateDev1 : _taskGateDev2;
            NIDaqTask task;
            DeviceReadState state;
            lock (gate)
            {
                if (isDev1)
                {
                    Interlocked.Increment(ref _generationDev1);
                    task = _task1;
                    state = _readState1;
                    _task1 = null;
                    _reader1 = null;
                    _readState1 = null;
                }
                else
                {
                    Interlocked.Increment(ref _generationDev2);
                    task = _task2;
                    state = _readState2;
                    _task2 = null;
                    _reader2 = null;
                    _readState2 = null;
                }
                (isDev1 ? _controlRingDev1 : _controlRingDev2).Reset();
                ResetFreshness(device);
            }

            if (task == null) return;
            try { task.Stop(); }
            catch (Exception ex) { _log.Warn($"停止 {device} DAQ任务异常：{ex.Message}", "AI"); }
            try { task.Dispose(); }
            catch (Exception ex) { _log.Warn($"释放 {device} DAQ任务异常：{ex.Message}", "AI"); }
            if (state != null && !state.Quiesced.Wait(500))
                _log.Warn($"{device} 旧DAQ回调在500ms内未退出；新任务将使用独立代次和唯一名称。", "AI");
        }

        private void ScheduleDeviceRecovery(string device, long generation, Exception cause)
        {
            if (!IsCurrentGeneration(device, generation)) return;
            // 采集层只发布事实；Stop/Start 和恢复终态全部交给 EpbManager 的唯一恢复状态机。
            PublishQueueFullFault(
                device,
                generation,
                "DaqCallbackException",
                "Callback",
                reasonOverride: $"Device={device} DAQ回调异常：{cause?.Message}");
        }

        private static int? GetNativeErrorCode(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                foreach (var propertyName in new[] { "Error", "ErrorCode", "NativeErrorCode" })
                {
                    try
                    {
                        var value = current.GetType().GetProperty(propertyName)?.GetValue(current, null);
                        if (value != null)
                            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch { }
                }
            }
            return null;
        }

        private static bool IsExplicitDeviceMissing(Exception exception)
        {
            var code = GetNativeErrorCode(exception);
            if (code == -200220 || code == -88705) return true;
            var message = exception?.ToString() ?? string.Empty;
            return message.IndexOf("device is not present", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("device identifier is invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("device was removed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("cannot be found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("找不到设备", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // —— 后台处理队列，避免在 DAQ 回调里阻塞 —— //
        // Raw 在 EnqueueForProcessing 成功发布后归后台独占。后台会原地进行电压到
        // 工程值换算；发布者不得再访问，OnRawBatch 订阅者也只能在同步调用期间只读借用。
        private readonly struct Item
        {
            public Item(
                string device,
                long generation,
                long sequence,
                double[,] raw,
                DateTime current,
                DateTime last,
                long enqueuedMonotonicTicks,
                double effectiveSampleRateHz,
                ClockState clockState,
                double estimatedSkewPpm,
                double clockResidualMs,
                double clockWindowSeconds)
            {
                Device = device;
                Generation = generation;
                Sequence = sequence;
                Raw = raw;
                Current = current;
                Last = last;
                EnqueuedMonotonicTicks = enqueuedMonotonicTicks;
                EffectiveSampleRateHz = effectiveSampleRateHz;
                ClockState = clockState;
                EstimatedSkewPpm = estimatedSkewPpm;
                ClockResidualMs = clockResidualMs;
                ClockWindowSeconds = clockWindowSeconds;
            }

            public string Device { get; }
            public long Generation { get; }
            public long Sequence { get; }
            public double[,] Raw { get; }
            public DateTime Current { get; }
            public DateTime Last { get; }
            public long EnqueuedMonotonicTicks { get; }
            public double EffectiveSampleRateHz { get; }
            public ClockState ClockState { get; }
            public double EstimatedSkewPpm { get; }
            public double ClockResidualMs { get; }
            public double ClockWindowSeconds { get; }
        }

        #region 获取最大值相关的类和字段

        /// <summary>
        /// EPB 电流峰值结果摘要（基于“全数据”捕获）。
        /// </summary>
        public struct EpbCurrentPeak
        {
            /// <summary>EPB 物理通道（1..12）。</summary>
            public int Channel;

            /// <summary>峰值电流（A）。若期间无样本则为 0。</summary>
            public double MaxAmp;

            /// <summary>峰值发生时刻（本地时间）。</summary>
            public DateTime MaxAt;

            /// <summary>捕获开始时刻（本地时间）。</summary>
            public DateTime StartAt;

            /// <summary>捕获结束时刻（本地时间）。</summary>
            public DateTime EndAt;

            /// <summary>最近一个实际纳入证据的全速率样本时刻（本地时间）。</summary>
            public DateTime LastSampleAt;

            /// <summary>期间累计样本数（用于判断是否有有效样本）。</summary>
            public long SampleCount;

            /// <summary>是否仍在捕获中。</summary>
            public bool IsActive;
        }

        /// <summary> 单通道峰值跟踪器（线程安全，基于“全数据批处理”逐样本更新）。 </summary>
        private sealed class PeakTracker
        {
            public readonly object Sync = new object();
            public bool Active;
            public DateTime StartAt;
            public DateTime EndAt;
            public DateTime LastSampleAt;
            public DateTime MaxAt;
            public double MaxAmp;
            public long SampleCount;
            public PeakCaptureToken Token;
            public DateTime ProcessedThroughAt;
            public TaskCompletionSource<bool> CutoffCoveredSignal;

            // 逻辑截止时间用于“等待封口但不扩大统计窗口”。
            public DateTime? CutoffLocal; // 仅纳入 tsLocal <= CutoffLocal 的样本


            /// <summary>进入捕获状态并复位统计。</summary>
            public void Arm(DateTime t0, PeakCaptureToken token = null)
            {
                Active = true;
                StartAt = t0;
                EndAt = t0;
                MaxAmp = double.NegativeInfinity;
                MaxAt = t0;
                LastSampleAt = DateTime.MinValue;
                ProcessedThroughAt = DateTime.MinValue;
                SampleCount = 0;
                CutoffLocal = null;
                CutoffCoveredSignal = NewCutoffCoveredSignal();
                Token = token;
            }

            public Task FreezeCutoff(DateTime cutoffLocal)
            {
                if (!CutoffLocal.HasValue)
                    CutoffLocal = cutoffLocal;
                if (ProcessedThroughAt >= CutoffLocal.Value)
                    CutoffCoveredSignal.TrySetResult(true);
                return CutoffCoveredSignal.Task;
            }

            /// <summary>纳入一个样本（全数据逐点）。</summary>
            public void Update(double amp, DateTime tsLocal)
            {
                // 捕获开始前已在后台队列中的历史样本不得混入本次输出证据。
                if (tsLocal < StartAt)
                    return;
                if (tsLocal > ProcessedThroughAt)
                    ProcessedThroughAt = tsLocal;
                if (CutoffLocal.HasValue && ProcessedThroughAt >= CutoffLocal.Value)
                    CutoffCoveredSignal.TrySetResult(true);
                // 若设置了逻辑截止时间，则仅接受截止内样本
                if (CutoffLocal.HasValue && tsLocal > CutoffLocal.Value)
                    return;

                SampleCount++;
                if (amp > MaxAmp || SampleCount == 1)
                {
                    MaxAmp = amp;
                    MaxAt = tsLocal;
                }
                LastSampleAt = tsLocal;
                EndAt = tsLocal; // 批内最后一个样本的时间
            }

            /// <summary>结束捕获。</summary>
            public void Finish(DateTime tEndLocal)
            {
                Active = false;
                if (SampleCount == 0)
                {
                    MaxAmp = 0.0;
                    MaxAt = StartAt;
                    EndAt = CutoffLocal ?? EndAt; // 没有样本时，EndAt 以 Cutoff 或 StartAt 标注
                }
                else
                {
                    // 若设置了 Cutoff，但最后一个样本早于 Cutoff，EndAt 保持为最后样本时间；
                    // 若没有样本（上面已处理），或希望强制以 Cutoff 作为段尾，可按需覆盖：
                    if (CutoffLocal.HasValue && EndAt < CutoffLocal.Value)
                        EndAt = CutoffLocal.Value;
                }
            }

            /// <summary>生成快照。</summary>
            public EpbCurrentPeak Snapshot(int ch)
            {
                return new EpbCurrentPeak
                {
                    Channel = ch,
                    MaxAmp = double.IsNegativeInfinity(MaxAmp) ? 0.0 : MaxAmp,
                    MaxAt = MaxAt,
                    StartAt = StartAt,
                    EndAt = EndAt,
                    LastSampleAt = LastSampleAt,
                    SampleCount = SampleCount,
                    IsActive = Active
                };
            }

            private static TaskCompletionSource<bool> NewCutoffCoveredSignal()
            {
                return new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private sealed class PeakFinalizationResult
        {
            public EpbCurrentPeak Peak;
            public DateTime LogicalCutoffUtc;
            public DateTime ProcessedThroughUtc;
            public DateTime DrainCompletedUtc;
            public double DrainElapsedMs;
            public bool IsCutoffCovered;
            public bool IdentityMatched = true;
        }



        // —— 字段：每个 EPB 通道一个峰值跟踪器 —— //
        private readonly ConcurrentDictionary<int, PeakTracker> _peakTrackers =
            new ConcurrentDictionary<int, PeakTracker>();

        /// <summary>是否存在任意处于捕获状态的通道（用于快速短路）。</summary>
        private bool AnyPeakArmed
        {
            get
            {
                foreach (var kv in _peakTrackers)
                {
                    var t = kv.Value;
                    lock (t.Sync)
                    {
                        if (t.Active) return true;
                    }
                }
                return false;
            }
        }

        #endregion


        #region 获取最大值的相关的公共方法

        /// <summary>
        /// 开始对指定 EPB 通道（1..12）进行“正向上电段”的电流峰值捕获（基于全数据）。
        /// 建议在“下达正向上电指令”后立刻调用。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道（1..12）。</param>
        public void BeginEpbCurrentPeak(int epbChannel)
        {
            if (epbChannel < 1 || epbChannel > 12) return;
            var t = _peakTrackers.GetOrAdd(epbChannel, _ => new PeakTracker());
            lock (t.Sync)
            {
                t.Arm(DateTime.Now);
            }
            _log?.Info($"EPB[{epbChannel}]（全数据）峰值捕获开始。", "AI");
        }

        /// <summary>以运行/圈身份开始峰值捕获；同通道新捕获会明确覆盖并复位旧状态。</summary>
        public PeakCaptureToken BeginEpbCurrentPeak(int epbChannel, Guid testRunId, int cycleNumber)
        {
            if (epbChannel < 1 || epbChannel > 12)
                throw new ArgumentOutOfRangeException(nameof(epbChannel));
            var token = new PeakCaptureToken
            {
                CaptureId = Guid.NewGuid(),
                TestRunId = testRunId,
                Channel = epbChannel,
                CycleNumber = cycleNumber,
                StartUtc = DateTime.UtcNow
            };
            var tracker = _peakTrackers.GetOrAdd(epbChannel, _ => new PeakTracker());
            lock (tracker.Sync) tracker.Arm(DateTime.Now, token);
            _log?.Info(
                $"EPB[{epbChannel}] 峰值捕获开始 CaptureId={token.CaptureId:N} " +
                $"Run={testRunId:N} Cycle={cycleNumber}",
                "AI");
            return token;
        }

        public async Task<PeakCaptureResult> EndEpbCurrentPeakAsync(
            PeakCaptureToken token,
            int delayMs,
            bool cutoffAfterDelay,
            CancellationToken cancellationToken = default)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (!_peakTrackers.TryGetValue(token.Channel, out var tracker))
                return new PeakCaptureResult { Token = token, IsMatched = false, QualityReason = "TrackerMissing" };
            lock (tracker.Sync)
            {
                if (!IsPeakCaptureIdentityMatch(tracker.Token, token))
                    return new PeakCaptureResult
                    {
                        Token = token,
                        Peak = tracker.Snapshot(token.Channel),
                        IsMatched = false,
                        QualityReason = "CaptureIdentityMismatch"
                    };
            }

            var finalized = await FinalizePeakCaptureAsync(
                    token.Channel,
                    tracker,
                    delayMs,
                    cutoffAfterDelay,
                    cancellationToken,
                    token)
                .ConfigureAwait(false);
            var peak = finalized.Peak;
            if (!finalized.IdentityMatched)
                return new PeakCaptureResult
                {
                    Token = token,
                    Peak = peak,
                    IsMatched = false,
                    LogicalCutoffUtc = finalized.LogicalCutoffUtc,
                    ProcessedThroughUtc = finalized.ProcessedThroughUtc,
                    DrainCompletedUtc = finalized.DrainCompletedUtc,
                    DrainElapsedMs = finalized.DrainElapsedMs,
                    IsCutoffCovered = finalized.IsCutoffCovered,
                    QualityReason = "CaptureIdentityMismatchDuringDrain"
                };
            var timeMatched = peak.StartAt.ToUniversalTime() >= token.StartUtc.AddMilliseconds(-50) &&
                              peak.LastSampleAt != DateTime.MinValue &&
                              peak.LastSampleAt.ToUniversalTime() >= token.StartUtc;
            var matched = timeMatched && peak.SampleCount > 0;
            return new PeakCaptureResult
            {
                Token = token,
                Peak = peak,
                IsMatched = matched,
                LogicalCutoffUtc = finalized.LogicalCutoffUtc,
                ProcessedThroughUtc = finalized.ProcessedThroughUtc,
                DrainCompletedUtc = finalized.DrainCompletedUtc,
                DrainElapsedMs = finalized.DrainElapsedMs,
                IsCutoffCovered = finalized.IsCutoffCovered,
                QualityReason = !matched
                    ? "CaptureWindowInvalid"
                    : !finalized.IsCutoffCovered
                        ? "CutoffNotCovered"
                        : "Qualified"
            };
        }

        public PeakCaptureResult PeekEpbCurrentPeak(PeakCaptureToken token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (!_peakTrackers.TryGetValue(token.Channel, out var tracker))
                return new PeakCaptureResult
                {
                    Token = token,
                    IsMatched = false,
                    QualityReason = "TrackerMissing"
                };
            lock (tracker.Sync)
            {
                var matched = IsPeakCaptureIdentityMatch(tracker.Token, token);
                return new PeakCaptureResult
                {
                    Token = token,
                    Peak = tracker.Snapshot(token.Channel),
                    IsMatched = matched,
                    QualityReason = matched ? "Qualified" : "CaptureIdentityMismatch"
                };
            }
        }

        /// <summary>无分配地读取与令牌匹配的当前全速率峰值。</summary>
        public bool TryPeekEpbCurrentPeak(PeakCaptureToken token, out EpbCurrentPeak peak)
        {
            peak = default;
            if (token == null) return false;
            if (!_peakTrackers.TryGetValue(token.Channel, out var tracker)) return false;
            lock (tracker.Sync)
            {
                if (!IsPeakCaptureIdentityMatch(tracker.Token, token)) return false;
                peak = tracker.Snapshot(token.Channel);
                return peak.SampleCount > 0;
            }
        }

        public bool CancelEpbCurrentPeak(PeakCaptureToken token)
        {
            if (token == null) return false;
            if (!_peakTrackers.TryGetValue(token.Channel, out var tracker)) return false;
            lock (tracker.Sync)
            {
                if (!IsPeakCaptureIdentityMatch(tracker.Token, token)) return false;
                tracker.Active = false;
                tracker.SampleCount = 0;
                tracker.MaxAmp = 0.0;
                tracker.Token = null;
            }
            // 正常圈结束、取消或安全停机都会走令牌取消，不属于报警。
            _log?.Info(
                $"EPB[{token.Channel}] 峰值捕获已按令牌取消 CaptureId={token.CaptureId:N}。",
                "AI");
            return true;
        }

        public static bool IsPeakCaptureIdentityMatch(PeakCaptureToken active, PeakCaptureToken requested)
        {
            return active != null && requested != null &&
                   active.CaptureId == requested.CaptureId &&
                   active.TestRunId == requested.TestRunId &&
                   active.Channel == requested.Channel &&
                   active.CycleNumber == requested.CycleNumber;
        }

        /// <summary>
        /// 结束对指定 EPB 通道的峰值捕获，并返回本段期间的峰值结果（基于全数据）。
        /// 建议在“检测到断电/结束指令”后调用。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道（1..12）。</param>
        /// <returns>峰值结果（若期间无样本，MaxAmp=0，SampleCount=0）。</returns>
        public EpbCurrentPeak EndEpbCurrentPeak(int epbChannel)
        {
            var res = new EpbCurrentPeak { Channel = epbChannel };
            PeakTracker t;
            if (!_peakTrackers.TryGetValue(epbChannel, out t)) return res;

            lock (t.Sync)
            {
                if (t.Active)
                {
                    // 若在两批之间结束，就用当前本地时刻封口
                    t.Finish(DateTime.Now);
                }
                res = t.Snapshot(epbChannel);
            }
            _log?.Info($"EPB[{epbChannel}]（全数据）峰值捕获结束：Max={res.MaxAmp:F3}A @{res.MaxAt:HH:mm:ss.fff}，Samples={res.SampleCount}", "AI");
            return res;
        }


        /// <summary>
        /// （异步）结束对指定 EPB 通道的峰值捕获：
        /// 1) 立即记录“逻辑截止时刻”（调用当下的本地时间）；
        /// 2) 异步等待 delayMs 毫秒（给后台管线时间把已在路上的数据处理完）；
        /// 3) 仅接受 ≤ 截止时刻 的样本；
        /// 4) 完成封口并返回峰值结果；
        /// 5) 如提供 onCompleted 则在后台线程回调结果（不切回 UI 线程）。
        /// </summary>
        public async Task<EpbCurrentPeak> EndEpbCurrentPeakAsync(
            int epbChannel,
            int delayMs,
            CancellationToken token = default(CancellationToken),
            Action<EpbCurrentPeak> onCompleted = null)
        {
            return await EndEpbCurrentPeakAsync(
                    epbChannel,
                    delayMs,
                    cutoffAfterDelay: false,
                    token,
                    onCompleted)
                .ConfigureAwait(false);
        }


        public async Task<EpbCurrentPeak> EndEpbCurrentPeakAsync(
            int epbChannel,
            int delayMs,
            bool cutoffAfterDelay = false,// 新增：true=延时后截断；false=调用时截断（默认）
            CancellationToken token = default,
            Action<EpbCurrentPeak> onCompleted = null)  
        {
            if (!_peakTrackers.TryGetValue(epbChannel, out var t))
            {
                var empty = new EpbCurrentPeak { Channel = epbChannel };
                onCompleted?.Invoke(empty);
                return empty;
            }

            var finalized = await FinalizePeakCaptureAsync(
                    epbChannel,
                    t,
                    delayMs,
                    cutoffAfterDelay,
                    token)
                .ConfigureAwait(false);
            var res = finalized.Peak;

            try { onCompleted?.Invoke(res); } catch { }
            return res;
        }

        private async Task<PeakFinalizationResult> FinalizePeakCaptureAsync(
            int epbChannel,
            PeakTracker tracker,
            int delayMs,
            bool cutoffAfterDelay,
            CancellationToken token,
            PeakCaptureToken expectedToken = null)
        {
            Task coveredTask = Task.CompletedTask;
            DateTime cutoffLocal = DateTime.MinValue;

            if (cutoffAfterDelay && delayMs > 0)
            {
                try { await Task.Delay(delayMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            var startedTicks = Stopwatch.GetTimestamp();

            lock (tracker.Sync)
            {
                if (expectedToken != null &&
                    !IsPeakCaptureIdentityMatch(tracker.Token, expectedToken))
                    return BuildPeakFinalizationResult(
                        epbChannel,
                        tracker,
                        startedTicks,
                        identityMatched: false);

                cutoffLocal = DateTime.Now;
                coveredTask = tracker.FreezeCutoff(cutoffLocal);
            }

            // cutoffAfterDelay=true 的 delay 是统计窗口本身；冻结窗口后只追加最多100ms
            // 的在途覆盖等待，避免把500/1000ms统计窗口再次完整等待一遍。
            var drainWaitMs = cutoffAfterDelay
                ? Math.Min(100, Math.Max(0, delayMs))
                : Math.Max(0, delayMs);
            if (drainWaitMs > 0 && !coveredTask.IsCompleted)
            {
                try
                {
                    var timeoutTask = Task.Delay(drainWaitMs, token);
                    await Task.WhenAny(coveredTask, timeoutTask).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }

            lock (tracker.Sync)
            {
                if (expectedToken != null &&
                    !IsPeakCaptureIdentityMatch(tracker.Token, expectedToken))
                    return BuildPeakFinalizationResult(
                        epbChannel,
                        tracker,
                        startedTicks,
                        identityMatched: false);

                tracker.Finish(tracker.CutoffLocal ?? cutoffLocal);
                return BuildPeakFinalizationResult(
                    epbChannel,
                    tracker,
                    startedTicks,
                    identityMatched: true);
            }
        }

        private static PeakFinalizationResult BuildPeakFinalizationResult(
            int epbChannel,
            PeakTracker tracker,
            long startedTicks,
            bool identityMatched)
        {
            var cutoffLocal = tracker.CutoffLocal ?? DateTime.MinValue;
            var processedLocal = tracker.ProcessedThroughAt;
            var completedUtc = DateTime.UtcNow;
            return new PeakFinalizationResult
            {
                Peak = tracker.Snapshot(epbChannel),
                LogicalCutoffUtc = cutoffLocal == DateTime.MinValue
                    ? DateTime.MinValue
                    : cutoffLocal.ToUniversalTime(),
                ProcessedThroughUtc = processedLocal == DateTime.MinValue
                    ? DateTime.MinValue
                    : processedLocal.ToUniversalTime(),
                DrainCompletedUtc = completedUtc,
                DrainElapsedMs = (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 /
                                 Stopwatch.Frequency,
                IsCutoffCovered = cutoffLocal != DateTime.MinValue &&
                                  processedLocal >= cutoffLocal,
                IdentityMatched = identityMatched
            };
        }



        /// <summary>
        /// 不结束捕获，实时窥视当前峰值（基于全数据已处理到的样本）。
        /// </summary>
        public EpbCurrentPeak PeekEpbCurrentPeak(int epbChannel)
        {
            PeakTracker t;
            if (!_peakTrackers.TryGetValue(epbChannel, out t))
                return new EpbCurrentPeak { Channel = epbChannel };

            lock (t.Sync) return t.Snapshot(epbChannel);
        }

        /// <summary>
        /// 取消并清除当前峰值捕获（本段数据作废）。
        /// </summary>
        public void CancelEpbCurrentPeak(int epbChannel)
        {
            PeakTracker t;
            if (_peakTrackers.TryGetValue(epbChannel, out t))
            {
                lock (t.Sync)
                {
                    t.Active = false;
                    t.SampleCount = 0;
                    t.MaxAmp = 0.0;
                }
            }
            _log?.Warn($"EPB[{epbChannel}]（全数据）峰值捕获已取消。", "AI");
        }

        #endregion







    }




}
