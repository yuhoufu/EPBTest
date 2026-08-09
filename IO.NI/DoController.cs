using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using NationalInstruments.DAQmx;

// 避免与 System.Threading.Tasks.Task 混淆，做别名
using NIDaqTask = NationalInstruments.DAQmx.Task;
using NIChannelLineGrouping = NationalInstruments.DAQmx.ChannelLineGrouping;
using NITaskAction = NationalInstruments.DAQmx.TaskAction;


using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;


namespace IO.NI
{
    public sealed class HighPriorityDoTelemetry
    {
        public Guid CommandId { get; internal set; }
        public int Channel { get; internal set; }
        public int TimeoutMs { get; internal set; }
        public int QueueDepthAtEnqueue { get; internal set; }
        public bool CallerTimedOut { get; internal set; }
        public bool Result { get; internal set; }
        public bool LateHardwareSuccess => CallerTimedOut && Result;
        public DateTime HardwareCompletedUtc { get; internal set; }
        public double QueueWaitMs { get; internal set; }
        public double WorkerExecutionMs { get; internal set; }
        public double TotalMs { get; internal set; }
        public double LockWaitMs { get; internal set; }
        public double NiWriteMs { get; internal set; }
    }

    internal sealed class DoWriteTiming
    {
        internal double LockWaitMs { get; set; }
        internal double NiWriteMs { get; set; }
    }

    /// <summary>
    /// DO 控制器：基于 <see cref="DoConfig"/>（EPB 与 Pressure）统一管理多个数字输出。
    /// - 与 AoController 一致，按“配置对象”而非“读取XML”初始化。
    /// - 支持 EPB 正/反互斥输出、Pressure 点位开/关。
    /// - 线程安全：所有对 NI 任务的构建与写入均有锁保护。
    /// </summary>
    public class DoController : IDisposable
    {
        #region 内部类型与字段

        /// <summary>
        ///     DO 写入专用高优先级 Worker。
        ///     设计目的：将“触发后断电”等关键 DO 写入从线程池/多线程锁竞争中剥离出来，
        ///     以更稳定的调度优先级执行写入，减少尾部抖动。
        /// </summary>
        internal sealed class HighPriorityDoWorker : IDisposable
        {
            private const int MaxPendingWorkItems = 64;

            private sealed class WorkItem
            {
                public Guid CommandId;
                public int Channel;
                public Func<IReadOnlyList<int>, DoWriteTiming, bool> BatchWork;
                public TaskCompletionSource<bool> Done;
                public Action<HighPriorityDoTelemetry> Completion;
                public long EnqueuedTicks;
                public long DequeuedTicks;
                public long CompletedTicks;
                public int QueueDepthAtEnqueue;
                public int TimeoutMs;
                public int CallerTimedOut;
                public bool Result;
                public Exception Error;
            }

            private readonly ConcurrentQueue<WorkItem> _hiQueue = new ConcurrentQueue<WorkItem>();
            private readonly ConcurrentDictionary<int, WorkItem> _pendingByChannel =
                new ConcurrentDictionary<int, WorkItem>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly string _workerName;
            private readonly bool _combineDistinctChannels;

            private volatile bool _stopping;
            private int _pendingWorkItems;
            private long _coalescedRequests;
            private Thread _thread;

            internal HighPriorityDoWorker(string workerName, bool combineDistinctChannels = true)
            {
                _workerName = string.IsNullOrWhiteSpace(workerName) ? "Unknown" : workerName.Trim();
                _combineDistinctChannels = combineDistinctChannels;
            }

            internal int PendingWorkItems => Math.Max(0, Volatile.Read(ref _pendingWorkItems));

            internal long CoalescedRequests => Interlocked.Read(ref _coalescedRequests);

            /// <summary>
            ///     启动高优先级 worker 线程。
            /// </summary>
            /// <remarks>
            ///     线程模型：
            ///     <list type="bullet">
            ///         <item>使用专用 <see cref="Thread"/>，不占用线程池。</item>
            ///         <item>线程优先级设为 <see cref="ThreadPriority.Highest"/>。</item>
            ///     </list>
            /// </remarks>
            public void StartIfNeeded()
            {
                if (_thread != null) return;

                var t = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "DO-HP-" + _workerName,
                    Priority = ThreadPriority.Highest
                };

                _thread = t;
                t.Start();
            }

            /// <summary>
            ///     在高优先级 worker 线程中执行一个 DO 写入任务，并同步等待完成。
            /// </summary>
            /// <param name="work">具体写入逻辑；应为短任务（单次 NI 写入）。</param>
            /// <param name="timeoutMs">
            ///     等待超时（毫秒）。超时后调用方必须进入组级隔离，禁止在调用线程直接写DO。
            /// </param>
            public bool InvokeHi(
                int channel,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                int timeoutMs,
                Action<HighPriorityDoTelemetry> completion)
            {
                if (channel <= 0 || batchWork == null || _stopping) return false;

                // 重入时不能在worker线程内再次同步执行NI写入；返回失败交给上层
                // 电源隔离/重试，避免日志或观察者回调形成递归阻塞。
                if (Thread.CurrentThread == _thread)
                    return false;

                StartIfNeeded();

                while (!_stopping)
                {
                    if (_pendingByChannel.TryGetValue(channel, out var existing))
                    {
                        Interlocked.Increment(ref _coalescedRequests);
                        return WaitForCompletion(existing, timeoutMs);
                    }

                    var pending = Interlocked.Increment(ref _pendingWorkItems);
                    if (pending > MaxPendingWorkItems)
                    {
                        Interlocked.Decrement(ref _pendingWorkItems);
                        return false;
                    }

                    var item = new WorkItem
                    {
                        CommandId = Guid.NewGuid(),
                        Channel = channel,
                        BatchWork = batchWork,
                        Done = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously),
                        Completion = completion,
                        EnqueuedTicks = Stopwatch.GetTimestamp(),
                        QueueDepthAtEnqueue = pending,
                        TimeoutMs = Math.Max(1, timeoutMs)
                    };

                    if (!_pendingByChannel.TryAdd(channel, item))
                    {
                        Interlocked.Decrement(ref _pendingWorkItems);
                        continue;
                    }

                    _hiQueue.Enqueue(item);
                    _signal.Set();
                    return WaitForCompletion(item, timeoutMs);
                }

                return false;
            }

            private static bool WaitForCompletion(WorkItem item, int timeoutMs)
            {
                if (item == null) return false;
                var boundedTimeoutMs = Math.Max(1, timeoutMs);
                try
                {
                    if (!item.Done.Task.Wait(boundedTimeoutMs))
                    {
                        Interlocked.Exchange(ref item.CallerTimedOut, 1);
                        return false;
                    }
                    return item.Done.Task.Status == TaskStatus.RanToCompletion && item.Done.Task.Result;
                }
                catch
                {
                    return false;
                }
            }

            private void Loop()
            {
                while (!_stopping)
                {
                    if (!_hiQueue.TryDequeue(out var item))
                    {
                        _signal.WaitOne(50);
                        continue;
                    }

                    var batch = new List<WorkItem> { item };
                    if (_combineDistinctChannels)
                    {
                        while (_hiQueue.TryDequeue(out var additional))
                            batch.Add(additional);
                    }

                    var batchResult = false;
                    Exception batchError = null;
                    var timing = new DoWriteTiming();
                    var dequeuedTicks = Stopwatch.GetTimestamp();
                    try
                    {
                        var channels = batch.Select(workItem => workItem.Channel).Distinct().ToArray();
                        batchResult = item.BatchWork(channels, timing);
                    }
                    catch (Exception ex)
                    {
                        batchError = ex;
                        batchResult = false;
                    }
                    finally
                    {
                        var completedTicks = Stopwatch.GetTimestamp();
                        foreach (var completedItem in batch)
                        {
                            completedItem.DequeuedTicks = dequeuedTicks;
                            completedItem.CompletedTicks = completedTicks;
                            completedItem.Error = batchError;
                            completedItem.Result = batchResult;
                            completedItem.Done.TrySetResult(batchResult);
                            RemovePending(completedItem);
                            Interlocked.Decrement(ref _pendingWorkItems);
                            var completion = completedItem.Completion;
                            if (completion == null) continue;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    completion(new HighPriorityDoTelemetry
                                    {
                                        CommandId = completedItem.CommandId,
                                        Channel = completedItem.Channel,
                                        TimeoutMs = completedItem.TimeoutMs,
                                        QueueDepthAtEnqueue = completedItem.QueueDepthAtEnqueue,
                                        CallerTimedOut = Volatile.Read(ref completedItem.CallerTimedOut) != 0,
                                        Result = completedItem.Result,
                                        HardwareCompletedUtc = DateTime.UtcNow,
                                        QueueWaitMs = ElapsedMs(
                                            completedItem.EnqueuedTicks,
                                            completedItem.DequeuedTicks),
                                        WorkerExecutionMs = ElapsedMs(
                                            completedItem.DequeuedTicks,
                                            completedItem.CompletedTicks),
                                        TotalMs = ElapsedMs(
                                            completedItem.EnqueuedTicks,
                                            completedItem.CompletedTicks),
                                        LockWaitMs = timing.LockWaitMs,
                                        NiWriteMs = timing.NiWriteMs
                                    });
                                }
                                catch
                                {
                                    // 诊断观察者不得影响专用DO线程和安全命令结果。
                                }
                            });
                        }
                    }
                }
            }

            private static double ElapsedMs(long startedTicks, long completedTicks)
            {
                if (startedTicks <= 0 || completedTicks < startedTicks) return 0;
                return (completedTicks - startedTicks) * 1000.0 / Stopwatch.Frequency;
            }

            public void Dispose()
            {
                _stopping = true;
                try { _signal.Set(); } catch { /* ignore */ }
                var thread = _thread;
                if (thread != null && Thread.CurrentThread != thread)
                {
                    try { thread.Join(1000); } catch { /* ignore */ }
                }
                while (_hiQueue.TryDequeue(out var pending))
                {
                    RemovePending(pending);
                    Interlocked.Decrement(ref _pendingWorkItems);
                    pending.Result = false;
                    pending.Done.TrySetResult(false);
                }
                try { _signal.Dispose(); } catch { /* ignore */ }
            }

            private void RemovePending(WorkItem item)
            {
                if (item == null) return;
                ((ICollection<KeyValuePair<int, WorkItem>>)_pendingByChannel).Remove(
                    new KeyValuePair<int, WorkItem>(item.Channel, item));
            }
        }

        /// <summary>每个 NI 设备的上下文。</summary>
        private sealed class DoDevice
        {
            public DoDevice(string name)
            {
                Name = name;
                HighPriorityWorker = new HighPriorityDoWorker(name);
            }

            public string Name;
            public NIDaqTask Task;
            public DigitalMultiChannelWriter Writer;
            public readonly object WriteGate = new object();
            public readonly HighPriorityDoWorker HighPriorityWorker;

            // 每设备独立的通道与默认值表
            public readonly List<string> Lines = new List<string>();
            public readonly List<bool> DefaultStates = new List<bool>();

            // 当前实际状态（与 Lines 一一对应）
            public bool[] States;
        }

        private readonly object _doTaskLock = new object();
        private int _disposed;

        // 设备名 -> 设备上下文
        private readonly ConcurrentDictionary<string, DoDevice> _devices =
            new ConcurrentDictionary<string, DoDevice>(StringComparer.OrdinalIgnoreCase);

        // EPB: 通道号 -> (设备名, 正Idx, 反Idx) —— 索引是该设备 DefaultStates/States 的索引
        private readonly ConcurrentDictionary<int, (string dev, int posIdx, int negIdx)> _epbIndex
            = new ConcurrentDictionary<int, (string, int, int)>();

        // 压力: 编号 -> (设备名, 索引)
        private readonly ConcurrentDictionary<int, (string dev, int idx)> _pressureIndex
            = new ConcurrentDictionary<int, (string, int)>();

        // 兼容旧接口：不再使用，但保留以免外部调用报错
        private string _configPath;

        private readonly ILogger _log;

        // ★新增：高优先级 DO worker（用于“触发后断电”等关键写入）
        private readonly HighPriorityDoWorker _uninitializedHiWorker =
            new HighPriorityDoWorker("Uninitialized", combineDistinctChannels: false);

        internal const int HighPriorityOffTimeoutMs = 100;
        private const double HighPriorityOffSlowLogThresholdMs = 20.0;

        // 新增：保存配置对象（来源于外部的 cfgDo）
        private readonly DoConfig _cfg;

        #endregion

        #region 构造与配置

        /// <summary>
        /// 构造 DO 控制器。
        /// </summary>
        /// <param name="cfgDo">数字输出配置（EPB 与 Pressure）。必填。</param>
        /// <param name="logger">可选日志器。</param>
        public DoController(DoConfig cfgDo, ILogger logger = null)
        {
            _cfg = cfgDo ?? throw new ArgumentNullException(nameof(cfgDo));
            _log = logger ?? NLogger.Instance;
        }

        public event Action<HighPriorityDoTelemetry> HighPriorityOffCompleted;

        /// <summary>
        /// 兼容旧接口：设置 XML 路径（本实现不会再读取 XML，仅为保持方法签名不变）。
        /// </summary>
        /// <param name="xmlPath">历史遗留参数，忽略。</param>
        public void SetConfigPath(string xmlPath) => _configPath = xmlPath;

        #endregion

        #region 初始化

        /// <summary>
        /// 初始化所有 DO 通道：根据 <see cref="DoConfig"/> 构建任务、创建通道、索引映射，并下发默认值。
        /// </summary>
        /// <returns>成功/失败。</returns>
        public bool Initialize()
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            lock (_doTaskLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                try
                {
                    ResetAllDevices(clearMaps: true);

                    // ===== EPB：正/反两路，隶属同一设备且互斥 =====
                    if (_cfg?.Epb != null)
                    {
                        foreach (var r in _cfg.Epb)
                        {
                            if (!(r?.Enabled ?? false)) continue;
                            if (r.Channel <= 0) continue;
                            if (string.IsNullOrWhiteSpace(r.Pos) || string.IsNullOrWhiteSpace(r.Neg)) continue;

                            string devNamePos = GetDeviceName(r.Pos);
                            string devNameNeg = GetDeviceName(r.Neg);
                            if (!devNamePos.Equals(devNameNeg, StringComparison.OrdinalIgnoreCase))
                            {
                                LogError($"EPB{r.Channel} 的正/反分属不同设备（{devNamePos} / {devNameNeg}），不支持跨设备互斥，已跳过。", "DO初始化");
                                continue;
                            }

                            var dev = EnsureDevice(devNamePos);
                            string def = string.IsNullOrWhiteSpace(r.Default) ? "全关" : r.Default.Trim();

                            // 正
                            dev.Task.DOChannels.CreateChannel(r.Pos, $"EPB{r.Channel}-正", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(r.Pos);
                            dev.DefaultStates.Add(def == "正");
                            int posIdx = dev.DefaultStates.Count - 1;

                            // 反
                            dev.Task.DOChannels.CreateChannel(r.Neg, $"EPB{r.Channel}-反", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(r.Neg);
                            dev.DefaultStates.Add(def == "反");
                            int negIdx = dev.DefaultStates.Count - 1;

                            if (def == "全关")
                            {
                                dev.DefaultStates[posIdx] = false;
                                dev.DefaultStates[negIdx] = false;
                            }

                            _epbIndex[r.Channel] = (devNamePos, posIdx, negIdx);
                        }
                    }

                    // ===== Pressure：单点 DO，开/关 =====
                    if (_cfg?.Pressure != null)
                    {
                        foreach (var p in _cfg.Pressure)
                        {
                            if (!(p?.Enabled ?? false)) continue;
                            if (p.Id <= 0) continue;
                            if (string.IsNullOrWhiteSpace(p.Physical)) continue;

                            string devName = GetDeviceName(p.Physical);
                            var dev = EnsureDevice(devName);

                            bool defVal = p.DefaultValue == 1;

                            dev.Task.DOChannels.CreateChannel(p.Physical, $"Pressure-{p.Id}", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(p.Physical);
                            dev.DefaultStates.Add(defVal);
                            int idx = dev.DefaultStates.Count - 1;

                            _pressureIndex[p.Id] = (devName, idx);
                        }
                    }

                    if (_devices.Count == 0)
                    {
                        LogError("DO 初始化失败：未创建任何设备任务（配置可能为空或全部禁用）", "DO初始化");
                        ResetAllDevices(clearMaps: true);
                        return false;
                    }

                    // 逐设备 Verify + Writer + 下发默认值
                    int totalLines = 0;
                    foreach (var dev in _devices.Values)
                    {
                        if (dev.Lines.Count == 0) continue;

                        dev.Task.Control(NITaskAction.Verify);
                        dev.Writer = new DigitalMultiChannelWriter(dev.Task.Stream);
                        dev.States = dev.DefaultStates.ToArray();

                        // 下发默认
                        dev.Writer.WriteSingleSampleSingleLine(true, dev.States);
                        totalLines += dev.Lines.Count;
                    }

                    LogInfo($"DO 初始化完成：设备数={_devices.Count}，总线数={totalLines}，EPB组={_epbIndex.Count}，压力点={_pressureIndex.Count}", "DO初始化");
                    return true;
                }
                catch (DaqException ex)
                {
                    LogError("DO 初始化失败（DAQ）：" + ex.Message, "DO初始化", ex);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
                catch (Exception ex2)
                {
                    LogError($"DO 初始化失败（未知）：{ex2}", "DO初始化", ex2);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
            }
        }

        #endregion

        #region 写入：EPB 与 Pressure（保持原有方法名/签名）

        /// <summary>
        /// 设置 EPB 通道的方向。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <param name="directionIsForward">true=正，false=反。</param>
        /// <returns>成功/失败。</returns>
        public bool SetEpb(int channelNo, bool directionIsForward)
        {
            const int maxRetries = 1;
            var attempts = 0;

            while (attempts <= maxRetries)
            {
                try
                {
                    if (!EnsureReady()) { attempts++; continue; }

                    if (!_epbIndex.TryGetValue(channelNo, out var map))
                    {
                        LogError($"EPB 通道号未找到：{channelNo}", "DO操作");
                        return false;
                    }

                    if (!_devices.TryGetValue(map.dev, out var dev))
                    {
                        LogError($"EPB[{channelNo}] 所属设备未就绪：{map.dev}", "DO操作");
                        return false;
                    }

                    lock (dev.WriteGate)
                    {
                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.posIdx] = directionIsForward;
                        toWrite[map.negIdx] = !directionIsForward;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;
                    }
                    LogInfo($"EPB[{channelNo}]@{map.dev} => {(directionIsForward ? "正" : "反")}", "DO操作");
                    return true;
                }
                catch (DaqException ex)
                {
                    attempts++;
                    LogError($"EPB 写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                    if (attempts <= maxRetries) Initialize();
                }
                catch (Exception ex2)
                {
                    attempts++;
                    LogError($"EPB 写入异常：{ex2}", "DO操作", ex2);
                    if (attempts <= maxRetries) Initialize();
                }
            }

            LogError("EPB 写入失败：超过最大重试次数", "DO操作");
            return false;
        }

        /// <summary>
        /// 关闭指定 EPB 通道（正/反全关）。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <returns>成功/失败。</returns>
        public bool SetEpbOff(int channelNo)
        {
            return SetEpbOffCore(channelNo, null);
        }

        private bool SetEpbOffCore(int channelNo, DoWriteTiming timing)
        {
            var lockRequestedTicks = Stopwatch.GetTimestamp();
            long lockAcquiredTicks = 0;
            long niWriteStartedTicks = 0;
            long niWriteCompletedTicks = 0;
            try
            {
                if (!EnsureReady()) return false;

                if (!_epbIndex.TryGetValue(channelNo, out var map))
                {
                    QueueLog(() => LogError($"EPB 通道号未找到：{channelNo}", "DO操作"));
                    return false;
                }
                if (!_devices.TryGetValue(map.dev, out var dev))
                {
                    QueueLog(() => LogError(
                        $"EPB[{channelNo}] 所属设备未就绪：{map.dev}",
                        "DO操作"));
                    return false;
                }

                lock (dev.WriteGate)
                {
                    lockAcquiredTicks = Stopwatch.GetTimestamp();
                    var toWrite = (bool[])dev.States.Clone();
                    toWrite[map.posIdx] = false;
                    toWrite[map.negIdx] = false;
                    niWriteStartedTicks = Stopwatch.GetTimestamp();
                    dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                    niWriteCompletedTicks = Stopwatch.GetTimestamp();
                    dev.States = toWrite;
                }
                // 安全 Worker 的完成边界到此为止。日志和观察者不计入100ms期限。
                QueueLog(() => LogInfo($"EPB[{channelNo}]@{map.dev} => 全关", "DO操作"));
                return true;
            }
            catch (Exception ex)
            {
                QueueLog(() => LogError("EPB 关闭失败：" + ex.Message, "DO操作", ex));
                return false;
            }
            finally
            {
                if (timing != null)
                {
                    if (niWriteStartedTicks > 0 && niWriteCompletedTicks == 0)
                        niWriteCompletedTicks = Stopwatch.GetTimestamp();
                    timing.LockWaitMs = lockAcquiredTicks > 0
                        ? (lockAcquiredTicks - lockRequestedTicks) * 1000.0 / Stopwatch.Frequency
                        : 0;
                    timing.NiWriteMs = niWriteStartedTicks > 0 && niWriteCompletedTicks >= niWriteStartedTicks
                        ? (niWriteCompletedTicks - niWriteStartedTicks) * 1000.0 / Stopwatch.Frequency
                        : 0;
                }
            }
        }

        /// <summary>
        ///     高优先级关闭指定 EPB 通道（正/反全关）。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <returns>成功/失败。</returns>
        /// <remarks>
        ///     线程模型：
        ///     <list type="bullet">
        ///         <item>通过专用高优先级 worker 线程执行，减少线程池调度/锁竞争带来的尾部抖动；</item>
        ///         <item>若 worker 等待超时则立即返回失败，由上层切断电源组并持续重试；禁止调用线程同步直写。</item>
        ///     </list>
        ///     注意：该方法仍会进入 <see cref="SetEpbOff"/> 的锁保护，
        ///     但因为关键路径集中到单线程，整体竞争通常显著降低。
        /// </remarks>
        public bool SetEpbOffHighPriority(int channelNo)
        {
            // 只允许专用worker执行NI同步写。worker超时后若在调用线程再次直写，
            // 会等待同一个_doTaskLock/NI调用，曾使DAQ恢复协程阻塞二十余分钟。
            // 超时返回false后，上层会保持电源组隔离并由看门狗持续重试；已排队的
            // OFF命令仍会在worker恢复后执行，因此不会把软件阻塞扩散到状态机。
            var worker = _uninitializedHiWorker;
            string expectedDevice = null;
            if (_epbIndex.TryGetValue(channelNo, out var map) &&
                _devices.TryGetValue(map.dev, out var dev))
            {
                worker = dev.HighPriorityWorker;
                expectedDevice = map.dev;
            }
            return worker.InvokeHi(
                channelNo,
                (channels, timing) => SetEpbOffBatchCore(expectedDevice, channels, timing),
                HighPriorityOffTimeoutMs,
                telemetry => PublishHighPriorityOffTelemetry(channelNo, telemetry));
        }

        /// <summary>
        ///     在同一物理设备上把同时排队的多个 OFF 合并为一次位图写。
        ///     任何通道映射缺失或跨设备混入都拒绝整批，禁止报告部分成功。
        /// </summary>
        private bool SetEpbOffBatchCore(
            string expectedDevice,
            IReadOnlyList<int> channels,
            DoWriteTiming timing)
        {
            if (channels == null || channels.Count == 0) return false;
            var lockRequestedTicks = Stopwatch.GetTimestamp();
            long lockAcquiredTicks = 0;
            long niWriteStartedTicks = 0;
            long niWriteCompletedTicks = 0;
            try
            {
                if (!EnsureReady()) return false;

                DoDevice targetDevice = null;
                var maps = new List<(int channel, int posIdx, int negIdx)>(channels.Count);
                foreach (var channel in channels.Distinct())
                {
                    if (!_epbIndex.TryGetValue(channel, out var map) ||
                        !_devices.TryGetValue(map.dev, out var device))
                    {
                        QueueLog(() => LogError($"EPB[{channel}] 高优先级OFF映射不存在。", "DO操作"));
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(expectedDevice) &&
                        !string.Equals(expectedDevice, map.dev, StringComparison.OrdinalIgnoreCase))
                    {
                        QueueLog(() => LogError(
                            $"高优先级OFF批次混入其他设备：Expected={expectedDevice} Actual={map.dev} EPB={channel}",
                            "DO操作"));
                        return false;
                    }
                    if (targetDevice != null && !ReferenceEquals(targetDevice, device))
                    {
                        QueueLog(() => LogError("高优先级OFF批次跨越物理设备，已拒绝整批。", "DO操作"));
                        return false;
                    }
                    targetDevice = device;
                    maps.Add((channel, map.posIdx, map.negIdx));
                }

                if (targetDevice == null) return false;
                lock (targetDevice.WriteGate)
                {
                    lockAcquiredTicks = Stopwatch.GetTimestamp();
                    var toWrite = (bool[])targetDevice.States.Clone();
                    foreach (var map in maps)
                    {
                        toWrite[map.posIdx] = false;
                        toWrite[map.negIdx] = false;
                    }
                    niWriteStartedTicks = Stopwatch.GetTimestamp();
                    targetDevice.Writer.WriteSingleSampleSingleLine(true, toWrite);
                    niWriteCompletedTicks = Stopwatch.GetTimestamp();
                    targetDevice.States = toWrite;
                }

                var channelText = string.Join(",", maps.Select(map => map.channel));
                QueueLog(() => LogInfo(
                    $"EPB[{channelText}]@{targetDevice.Name} => 合并全关，一次位图写入",
                    "DO操作"));
                return true;
            }
            catch (Exception ex)
            {
                QueueLog(() => LogError("EPB 合并关闭失败：" + ex.Message, "DO操作", ex));
                return false;
            }
            finally
            {
                if (timing != null)
                {
                    if (niWriteStartedTicks > 0 && niWriteCompletedTicks == 0)
                        niWriteCompletedTicks = Stopwatch.GetTimestamp();
                    timing.LockWaitMs = lockAcquiredTicks > 0
                        ? (lockAcquiredTicks - lockRequestedTicks) * 1000.0 / Stopwatch.Frequency
                        : 0;
                    timing.NiWriteMs = niWriteStartedTicks > 0 && niWriteCompletedTicks >= niWriteStartedTicks
                        ? (niWriteCompletedTicks - niWriteStartedTicks) * 1000.0 / Stopwatch.Frequency
                        : 0;
                }
            }
        }

        private void PublishHighPriorityOffTelemetry(
            int channelNo,
            HighPriorityDoTelemetry telemetry)
        {
            if (telemetry == null) return;
            telemetry.Channel = channelNo;

            if (telemetry.CallerTimedOut || telemetry.TotalMs >= HighPriorityOffSlowLogThresholdMs)
            {
                LogWarn(
                    $"EPB[{channelNo}] 高优先级DO耗时诊断：CommandId={telemetry.CommandId:N} " +
                    $"Timeout={telemetry.TimeoutMs}ms CallerTimedOut={telemetry.CallerTimedOut} " +
                    $"FinalResult={telemetry.Result} QueueDepth={telemetry.QueueDepthAtEnqueue} " +
                    $"QueueWait={telemetry.QueueWaitMs:F3}ms LockWait={telemetry.LockWaitMs:F3}ms " +
                    $"NIWrite={telemetry.NiWriteMs:F3}ms Worker={telemetry.WorkerExecutionMs:F3}ms " +
                    $"Total={telemetry.TotalMs:F3}ms",
                    "DO性能");
            }

            try { HighPriorityOffCompleted?.Invoke(telemetry); }
            catch
            {
                // 性能诊断观察者不得改变DO结果。
            }
        }

        private static void QueueLog(Action write)
        {
            if (write == null) return;
            try
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { write(); }
                    catch
                    {
                        // 日志观察者永远不能改变 DO 命令结果或阻塞安全 Worker。
                    }
                });
            }
            catch
            {
                // 线程池不可用时宁可丢失本条诊断，也不能回退为同步日志。
            }
        }

        /// <summary>
        /// 设置压力点位开/关。
        /// </summary>
        /// <param name="id">压力点位编号。</param>
        /// <param name="start">true=启动；false=停止。</param>
        /// <returns>成功/失败。</returns>
        public bool SetPressure(int id, bool start)
        {
            const int maxRetries = 1;
            var attempts = 0;

            while (attempts <= maxRetries)
            {
                try
                {
                    if (!EnsureReady()) { attempts++; continue; }

                    if (!_pressureIndex.TryGetValue(id, out var map))
                    {
                        LogError($"压力编号未找到：{id}", "DO操作");
                        return false;
                    }

                    if (!_devices.TryGetValue(map.dev, out var dev))
                    {
                        LogError($"压力[{id}] 所属设备未就绪：{map.dev}", "DO操作");
                        return false;
                    }

                    lock (dev.WriteGate)
                    {
                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.idx] = start;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;
                    }
                    LogInfo($"压力[{id}]@{map.dev} => {(start ? "启动" : "停止")}", "DO操作");
                    return true;
                }
                catch (DaqException ex)
                {
                    attempts++;
                    LogError($"压力写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                    if (attempts <= maxRetries) Initialize();
                }
                catch (Exception ex2)
                {
                    attempts++;
                    LogError($"压力写入异常：{ex2}", "DO操作", ex2);
                    if (attempts <= maxRetries) Initialize();
                }
            }

            LogError("压力写入失败：超过最大重试次数", "DO操作");
            return false;
        }

        #endregion

        #region 便捷方法（保持原名）

        /// <summary>将所有 DO 路线全部关闭（所有设备）。</summary>
        public bool AllOff()
        {
            lock (_doTaskLock)
            {
                try
                {
                    if (!EnsureReady()) return false;

                    foreach (var dev in _devices.Values)
                    {
                        lock (dev.WriteGate)
                        {
                            var zeros = new bool[dev.States.Length];
                            dev.Writer.WriteSingleSampleSingleLine(true, zeros);
                            dev.States = zeros;
                        }
                    }

                    LogInfo("DO 全部关闭（所有设备）", "DO操作");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError("全部关闭失败：" + ex.Message, "DO操作", ex);
                    return false;
                }
            }
        }

        /// <summary>方向友好名称封装，兼容旧调用：设为正向。</summary>
        public bool SetEpbForward(int channelNo) => SetEpb(channelNo, true);

        /// <summary>方向友好名称封装，兼容旧调用：设为反向。</summary>
        public bool SetEpbReverse(int channelNo) => SetEpb(channelNo, false);

        /// <summary>压力友好名称封装：启动。</summary>
        public bool PressureOn(int id) => SetPressure(id, true);

        /// <summary>压力友好名称封装：停止。</summary>
        public bool PressureOff(int id) => SetPressure(id, false);

        #endregion

        #region 内部工具与清理

        /// <summary>确保设备上下文存在，不存在则创建。</summary>
        private DoDevice EnsureDevice(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var dev))
            {
                dev = new DoDevice(deviceName)
                {
                    Task = new NIDaqTask("DO_" + deviceName)
                };
                _devices[deviceName] = dev;
            }
            return dev;
        }

        /// <summary>从物理线名取设备名，如 "Dev1/port0/line0" -> "Dev1"。</summary>
        private static string GetDeviceName(string physicalLine)
        {
            if (string.IsNullOrWhiteSpace(physicalLine)) return "";
            int slash = physicalLine.IndexOf('/');
            return slash > 0 ? physicalLine.Substring(0, slash) : physicalLine;
        }

        /// <summary>确保当前对象已完成初始化，必要时调用 <see cref="Initialize"/>。</summary>
        private bool EnsureReady()
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (_devices.Count == 0) return Initialize();

            foreach (var dev in _devices.Values)
            {
                if (dev.Task == null || dev.Writer == null || dev.States == null)
                    return Initialize();
            }
            return true;
        }

        /// <summary>释放并清空所有设备任务、索引等。</summary>
        private void ResetAllDevices(bool clearMaps)
        {
            foreach (var dev in _devices.Values)
            {
                lock (dev.WriteGate)
                {
                    try { dev.Task?.Dispose(); } catch { /* ignore */ }
                    dev.Task = null;
                    dev.Writer = null;
                    dev.Lines.Clear();
                    dev.DefaultStates.Clear();
                    dev.States = null;
                }
                if (clearMaps)
                {
                    try { dev.HighPriorityWorker.Dispose(); } catch { /* ignore */ }
                }
            }
            if (clearMaps)
            {
                _devices.Clear();
                _epbIndex.Clear();
                _pressureIndex.Clear();
            }
        }

        private void LogInfo(string message, string category = null)
            => _log?.Info(message, category ?? "DO");

        private void LogWarn(string message, string category = null)
            => _log?.Warn(message, category ?? "DO");

        private void LogError(string message, string category = null, Exception ex = null)
            => _log?.Error(message, category ?? "DO", ex);

        /// <summary>
        ///     释放所有 NI 资源，并停止内部 DO 写入 worker。
        /// </summary>
        /// <remarks>
        ///     线程模型：该方法会停止专用 worker，并在锁保护下释放 NI Task/Writer。
        /// </remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _uninitializedHiWorker.Dispose(); } catch { /* ignore */ }

            lock (_doTaskLock)
            {
                try { ResetAllDevices(clearMaps: true); }
                catch { /* ignore */ }
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
