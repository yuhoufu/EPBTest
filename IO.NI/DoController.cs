using System;
using System.Collections;
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
    /// Optional final physical batch-write boundary.  It is deliberately
    /// owned by IO.NI; Controller cannot replace the high-priority worker or
    /// its admission/receipt lifecycle.
    /// </summary>
    internal interface IHighPriorityOffPhysicalWriter
    {
        bool TryWrite(
            string deviceName,
            IReadOnlyList<int> channels,
            bool[] nextStates);
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

        internal enum DoCommandPriority
        {
            Pressure = 0,
            DirectionChange = 1,
            ChannelOff = 2,
            EmergencyGroupOff = 3
        }

        /// <summary>
        /// 固定容量、无链表节点分配的四级命令环。所有方法由所属设备的准入门串行调用。
        /// 已租出的槽位（包含正在执行的命令）也占用硬容量，直到完成登记全部封口才归还。
        /// </summary>
        internal sealed class PreallocatedDoCommandRing
        {
            private const int PriorityCount = 4;
            private readonly int _capacity;
            private readonly int[] _slotsByPriority;
            private readonly int[] _heads = new int[PriorityCount];
            private readonly int[] _tails = new int[PriorityCount];
            private readonly int[] _counts = new int[PriorityCount];
            private readonly int[] _freeSlots;
            private readonly byte[] _slotStates;
            private int _freeCount;
            private int _queuedCount;

            internal PreallocatedDoCommandRing(int capacity)
            {
                if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
                _capacity = capacity;
                _slotsByPriority = new int[capacity * PriorityCount];
                _freeSlots = new int[capacity];
                _slotStates = new byte[capacity];
                _freeCount = capacity;
                for (var index = 0; index < capacity; index++)
                    _freeSlots[index] = capacity - index - 1;
            }

            internal int Capacity => _capacity;
            internal int QueuedCount => _queuedCount;
            internal int LeasedCount => _capacity - _freeCount;

            internal bool TryRent(out int slotIndex)
            {
                if (_freeCount == 0)
                {
                    slotIndex = -1;
                    return false;
                }

                slotIndex = _freeSlots[--_freeCount];
                _slotStates[slotIndex] = 1;
                return true;
            }

            internal bool TryEnqueue(int slotIndex, DoCommandPriority priority)
            {
                var priorityIndex = (int)priority;
                if ((uint)slotIndex >= (uint)_capacity ||
                    (uint)priorityIndex >= PriorityCount ||
                    _slotStates[slotIndex] != 1 ||
                    _counts[priorityIndex] >= _capacity)
                    return false;

                var tail = _tails[priorityIndex];
                _slotsByPriority[(priorityIndex * _capacity) + tail] = slotIndex;
                _tails[priorityIndex] = tail + 1 == _capacity ? 0 : tail + 1;
                _counts[priorityIndex]++;
                _queuedCount++;
                _slotStates[slotIndex] = 2;
                return true;
            }

            internal bool TryDequeueHighest(
                out int slotIndex,
                out DoCommandPriority priority)
            {
                for (var priorityIndex = PriorityCount - 1; priorityIndex >= 0; priorityIndex--)
                {
                    if (TryDequeue((DoCommandPriority)priorityIndex, out slotIndex))
                    {
                        priority = (DoCommandPriority)priorityIndex;
                        return true;
                    }
                }

                slotIndex = -1;
                priority = DoCommandPriority.Pressure;
                return false;
            }

            internal bool TryDequeue(DoCommandPriority priority, out int slotIndex)
            {
                var priorityIndex = (int)priority;
                if ((uint)priorityIndex >= PriorityCount || _counts[priorityIndex] == 0)
                {
                    slotIndex = -1;
                    return false;
                }

                var head = _heads[priorityIndex];
                slotIndex = _slotsByPriority[(priorityIndex * _capacity) + head];
                _heads[priorityIndex] = head + 1 == _capacity ? 0 : head + 1;
                _counts[priorityIndex]--;
                _queuedCount--;
                _slotStates[slotIndex] = 1;
                return true;
            }

            internal bool Return(int slotIndex)
            {
                if ((uint)slotIndex >= (uint)_capacity || _slotStates[slotIndex] != 1)
                    return false;
                _slotStates[slotIndex] = 0;
                _freeSlots[_freeCount++] = slotIndex;
                return true;
            }
        }

        /// <summary>
        ///     每物理 DO 设备独占的最高优先级 Worker。物理写固定按
        ///     EmergencyGroupOff、ChannelOff、DirectionChange、Pressure 顺序出队。
        /// </summary>
        internal sealed class HighPriorityDoWorker : IDisposable
        {
            private const int MaxPendingWorkItems = 64;
            private const int MaxRegistrationsPerWorkItem = 64;
            private const int MaxPendingCompletionItems = 4096;
            private const double NonBlockingAdmissionBudgetMs = 10.0;

            private enum RegistrationAttempt
            {
                Retry,
                Accepted,
                Full
            }

            private sealed class WorkItem
            {
                internal WorkItem(int slotIndex)
                {
                    SlotIndex = slotIndex;
                    Registrations = new List<CompletionRegistration>(
                        MaxRegistrationsPerWorkItem);
                }

                public readonly int SlotIndex;
                public int Channel;
                public DoCommandPriority Priority;
                public long PendingKey;
                public int PendingIndexed;
                public Func<IReadOnlyList<int>, DoWriteTiming, bool> BatchWork;
                public long DequeuedTicks;
                public long CompletedTicks;
                public bool Result;
                public Exception Error;
                public readonly object RegistrationGate = new object();
                public readonly List<CompletionRegistration> Registrations;
                public bool CompletionClosed;

                internal void Prepare(
                    int channel,
                    DoCommandPriority priority,
                    long pendingKey,
                    Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                    CompletionRegistration registration)
                {
                    Channel = channel;
                    Priority = priority;
                    PendingKey = pendingKey;
                    PendingIndexed = pendingKey == 0 ? 0 : 1;
                    BatchWork = batchWork;
                    DequeuedTicks = 0;
                    CompletedTicks = 0;
                    Result = false;
                    Error = null;
                    CompletionClosed = false;
                    Registrations.Clear();
                    Registrations.Add(registration);
                }

                internal void ClearForReuse()
                {
                    Channel = 0;
                    Priority = DoCommandPriority.Pressure;
                    PendingKey = 0;
                    PendingIndexed = 0;
                    BatchWork = null;
                    DequeuedTicks = 0;
                    CompletedTicks = 0;
                    Result = false;
                    Error = null;
                    CompletionClosed = true;
                    Registrations.Clear();
                }
            }

            private sealed class CompletionRegistration
            {
                public Guid CommandId;
                public TaskCompletionSource<bool> Done;
                public Action<HighPriorityDoTelemetry> Completion;
                public long EnqueuedTicks;
                public int QueueDepthAtEnqueue;
                public int TimeoutMs;
                public int CallerTimedOut;
                public int CompletionPublished;
                public int CompletionSlotReserved;
            }

            private sealed class ChannelBatchView : IReadOnlyList<int>
            {
                private readonly int[] _channels;
                private int _count;

                internal ChannelBatchView(int capacity)
                {
                    _channels = new int[capacity];
                }

                public int Count => _count;

                public int this[int index]
                {
                    get
                    {
                        if ((uint)index >= (uint)_count)
                            throw new ArgumentOutOfRangeException(nameof(index));
                        return _channels[index];
                    }
                }

                internal void Reset() => _count = 0;

                internal void AddDistinct(int channel)
                {
                    for (var index = 0; index < _count; index++)
                    {
                        if (_channels[index] == channel) return;
                    }
                    _channels[_count++] = channel;
                }

                public IEnumerator<int> GetEnumerator()
                {
                    for (var index = 0; index < _count; index++)
                        yield return _channels[index];
                }

                IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            }

            private readonly PreallocatedDoCommandRing _commandRing =
                new PreallocatedDoCommandRing(MaxPendingWorkItems);
            private readonly WorkItem[] _workItems = new WorkItem[MaxPendingWorkItems];
            private readonly WorkItem[] _batchScratch = new WorkItem[MaxPendingWorkItems];
            private readonly WorkItem[] _disposeBatchScratch = new WorkItem[MaxPendingWorkItems];
            private readonly ChannelBatchView _channelBatch =
                new ChannelBatchView(MaxPendingWorkItems);
            private readonly ConcurrentQueue<Action> _completionQueue = new ConcurrentQueue<Action>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly AutoResetEvent _completionSignal = new AutoResetEvent(false);
            private readonly object _startGate = new object();
            private readonly object _admissionStopGate = new object();
            private readonly string _workerName;
            private readonly bool _combineDistinctChannels;

            private volatile bool _stopping;
            private int _pendingWorkItems;
            private int _pendingCompletionItems;
            private int _reservedCompletionSlots;
            private int _completionProducerCount = 1;
            private int _disposeProducerReleased;
            private long _coalescedRequests;
            private Thread _thread;
            private Thread _completionThread;

            // 仅供并发回归在“索引登记后、物理环入队前”建立确定性交叉点。
            internal Action AdmissionIndexedTestHook { get; set; }

            internal HighPriorityDoWorker(string workerName, bool combineDistinctChannels = true)
            {
                _workerName = string.IsNullOrWhiteSpace(workerName) ? "Unknown" : workerName.Trim();
                _combineDistinctChannels = combineDistinctChannels;
                for (var slotIndex = 0; slotIndex < _workItems.Length; slotIndex++)
                    _workItems[slotIndex] = new WorkItem(slotIndex);
            }

            internal int PendingWorkItems => Math.Max(0, Volatile.Read(ref _pendingWorkItems));
            internal long CoalescedRequests => Interlocked.Read(ref _coalescedRequests);

            public void StartIfNeeded()
            {
                if (_thread != null) return;
                lock (_startGate)
                {
                    if (_thread != null || _stopping) return;
                    var t = new Thread(Loop)
                    {
                        IsBackground = true,
                        Name = "DO-HP-" + _workerName,
                        Priority = ThreadPriority.Highest
                    };

                    _thread = t;
                    Interlocked.Increment(ref _completionProducerCount);
                    _completionThread = new Thread(CompletionLoop)
                    {
                        IsBackground = true,
                        Name = "DO-Completion-" + _workerName,
                        Priority = ThreadPriority.Normal
                    };
                    _completionThread.Start();
                    t.Start();
                }
            }

            public bool InvokeHi(
                int channel,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                int timeoutMs,
                Action<HighPriorityDoTelemetry> completion)
            {
                return InvokeCommand(
                    channel,
                    DoCommandPriority.ChannelOff,
                    batchWork,
                    timeoutMs,
                    completion);
            }

            internal bool InvokeCommand(
                int channel,
                DoCommandPriority priority,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                int timeoutMs,
                Action<HighPriorityDoTelemetry> completion)
            {
                if (channel <= 0 || batchWork == null || !IsValidPriority(priority) || _stopping)
                    return false;
                if (Thread.CurrentThread == _thread) return false;

                var registration = NewRegistration(timeoutMs, completion, createWaiter: true);
                if (!TryEnqueue(channel, priority, batchWork, registration)) return false;
                return WaitForCompletion(registration, timeoutMs);
            }

            internal bool TryPostHi(
                int channel,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                Action<HighPriorityDoTelemetry> completion,
                out Guid commandId)
            {
                return TryPostCommand(
                    channel,
                    DoCommandPriority.ChannelOff,
                    batchWork,
                    completion,
                    out commandId);
            }

            internal bool TryPostCommand(
                int channel,
                DoCommandPriority priority,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                Action<HighPriorityDoTelemetry> completion,
                out Guid commandId)
            {
                var registration = NewRegistration(0, completion, createWaiter: false);
                commandId = registration.CommandId;
                if (channel <= 0 || batchWork == null || completion == null ||
                    !IsValidPriority(priority) || _stopping)
                    return false;
                return TryEnqueue(channel, priority, batchWork, registration);
            }

            private static bool IsValidPriority(DoCommandPriority priority)
                => (uint)priority < 4;

            private static bool CanCoalesce(DoCommandPriority priority)
                => priority == DoCommandPriority.ChannelOff ||
                   priority == DoCommandPriority.EmergencyGroupOff;

            private static long BuildPendingKey(DoCommandPriority priority, int channel)
                => ((long)(int)priority << 32) | (uint)channel;

            private static CompletionRegistration NewRegistration(
                int timeoutMs,
                Action<HighPriorityDoTelemetry> completion,
                bool createWaiter)
            {
                return new CompletionRegistration
                {
                    CommandId = Guid.NewGuid(),
                    Done = createWaiter
                        ? new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously)
                        : null,
                    Completion = completion,
                    EnqueuedTicks = Stopwatch.GetTimestamp(),
                    TimeoutMs = Math.Max(0, timeoutMs)
                };
            }

            private bool TryEnqueue(
                int channel,
                DoCommandPriority priority,
                Func<IReadOnlyList<int>, DoWriteTiming, bool> batchWork,
                CompletionRegistration registration)
            {
                if (!TryReserveCompletionSlot(registration)) return false;
                var accepted = false;
                try
                {
                    StartIfNeeded();
                    var admissionStarted = Stopwatch.GetTimestamp();
                    var pendingKey = CanCoalesce(priority)
                        ? BuildPendingKey(priority, channel)
                        : 0;

                    while (!_stopping &&
                           ElapsedMs(admissionStarted, Stopwatch.GetTimestamp()) <
                           NonBlockingAdmissionBudgetMs)
                    {
                        var gateTaken = false;
                        try
                        {
                            Monitor.TryEnter(_admissionStopGate, 0, ref gateTaken);
                            if (!gateTaken)
                            {
                                Thread.Yield();
                                continue;
                            }
                            if (_stopping) return false;

                            if (pendingKey != 0 && TryFindPending(pendingKey, out var existing))
                            {
                                var registrationAttempt = TryRegister(
                                    existing,
                                    pendingKey,
                                    registration);
                                if (registrationAttempt == RegistrationAttempt.Accepted)
                                {
                                    registration.QueueDepthAtEnqueue =
                                        Math.Max(1, PendingWorkItems);
                                    Interlocked.Increment(ref _coalescedRequests);
                                    accepted = true;
                                    return true;
                                }
                                if (registrationAttempt == RegistrationAttempt.Full)
                                    return false;

                                Thread.Yield();
                                continue;
                            }

                            if (!_commandRing.TryRent(out var slotIndex)) return false;
                            var pending = Interlocked.Increment(ref _pendingWorkItems);
                            registration.QueueDepthAtEnqueue = pending;
                            var item = _workItems[slotIndex];
                            item.Prepare(channel, priority, pendingKey, batchWork, registration);

                            try { AdmissionIndexedTestHook?.Invoke(); }
                            catch { /* 测试钩子不得改变生产准入语义 */ }

                            if (!_commandRing.TryEnqueue(slotIndex, priority))
                            {
                                item.ClearForReuse();
                                _commandRing.Return(slotIndex);
                                Interlocked.Decrement(ref _pendingWorkItems);
                                return false;
                            }

                            _signal.Set();
                            accepted = true;
                            return true;
                        }
                        finally
                        {
                            if (gateTaken) Monitor.Exit(_admissionStopGate);
                        }
                    }

                    return false;
                }
                finally
                {
                    if (!accepted) ReleaseCompletionSlot(registration);
                }
            }

            private bool TryFindPending(long pendingKey, out WorkItem item)
            {
                for (var slotIndex = 0; slotIndex < _workItems.Length; slotIndex++)
                {
                    var candidate = _workItems[slotIndex];
                    if (Volatile.Read(ref candidate.PendingIndexed) != 0 &&
                        Volatile.Read(ref candidate.PendingKey) == pendingKey)
                    {
                        item = candidate;
                        return true;
                    }
                }

                item = null;
                return false;
            }

            private static RegistrationAttempt TryRegister(
                WorkItem item,
                long expectedPendingKey,
                CompletionRegistration registration)
            {
                var gateTaken = false;
                try
                {
                    Monitor.TryEnter(item.RegistrationGate, 0, ref gateTaken);
                    if (!gateTaken) return RegistrationAttempt.Retry;
                    if (item.CompletionClosed || item.PendingIndexed == 0 ||
                        item.PendingKey != expectedPendingKey)
                        return RegistrationAttempt.Retry;
                    if (item.Registrations.Count >= MaxRegistrationsPerWorkItem)
                        return RegistrationAttempt.Full;
                    item.Registrations.Add(registration);
                    return RegistrationAttempt.Accepted;
                }
                finally
                {
                    if (gateTaken) Monitor.Exit(item.RegistrationGate);
                }
            }

            private bool TryReserveCompletionSlot(CompletionRegistration registration)
            {
                if (registration?.Completion == null) return true;
                var reserved = Interlocked.Increment(ref _reservedCompletionSlots);
                if (reserved > MaxPendingCompletionItems)
                {
                    Interlocked.Decrement(ref _reservedCompletionSlots);
                    return false;
                }
                Volatile.Write(ref registration.CompletionSlotReserved, 1);
                return true;
            }

            private void ReleaseCompletionSlot(CompletionRegistration registration)
            {
                if (registration == null ||
                    Interlocked.Exchange(ref registration.CompletionSlotReserved, 0) == 0)
                    return;
                Interlocked.Decrement(ref _reservedCompletionSlots);
            }

            private static bool WaitForCompletion(
                CompletionRegistration registration,
                int timeoutMs)
            {
                if (registration?.Done == null) return false;
                var boundedTimeoutMs = Math.Max(1, timeoutMs);
                try
                {
                    if (!registration.Done.Task.Wait(boundedTimeoutMs))
                    {
                        Interlocked.Exchange(ref registration.CallerTimedOut, 1);
                        return false;
                    }
                    return registration.Done.Task.Status == TaskStatus.RanToCompletion &&
                           registration.Done.Task.Result;
                }
                catch
                {
                    return false;
                }
            }

            private bool TryTakeBatch(
                WorkItem[] scratch,
                out WorkItem first,
                out int batchCount)
            {
                first = null;
                batchCount = 0;
                lock (_admissionStopGate)
                {
                    if (!_commandRing.TryDequeueHighest(out var slotIndex, out var priority))
                        return false;

                    first = _workItems[slotIndex];
                    scratch[batchCount++] = first;
                    if (!_combineDistinctChannels || !CanCoalesce(priority)) return true;

                    while (batchCount < scratch.Length &&
                           _commandRing.TryDequeue(priority, out slotIndex))
                        scratch[batchCount++] = _workItems[slotIndex];
                    return true;
                }
            }

            private void Loop()
            {
                var timing = new DoWriteTiming();
                try
                {
                    while (!_stopping)
                    {
                        if (!TryTakeBatch(_batchScratch, out var item, out var batchCount))
                        {
                            _signal.WaitOne(50);
                            continue;
                        }

                        var batchResult = false;
                        Exception batchError = null;
                        timing.LockWaitMs = 0;
                        timing.NiWriteMs = 0;
                        var dequeuedTicks = Stopwatch.GetTimestamp();
                        try
                        {
                            _channelBatch.Reset();
                            for (var index = 0; index < batchCount; index++)
                                _channelBatch.AddDistinct(_batchScratch[index].Channel);
                            batchResult = item.BatchWork(_channelBatch, timing);
                        }
                        catch (Exception ex)
                        {
                            batchError = ex;
                            batchResult = false;
                        }
                        finally
                        {
                            var completedTicks = Stopwatch.GetTimestamp();
                            var completedUtc = DateTime.UtcNow;
                            for (var index = 0; index < batchCount; index++)
                            {
                                var completedItem = _batchScratch[index];
                                _batchScratch[index] = null;
                                completedItem.DequeuedTicks = dequeuedTicks;
                                completedItem.CompletedTicks = completedTicks;
                                completedItem.Error = batchError;
                                completedItem.Result = batchResult;
                                CloseRegistrations(completedItem);
                                Interlocked.Decrement(ref _pendingWorkItems);
                                CompleteRegistrations(completedItem, timing, completedUtc);
                                ReturnWorkItem(completedItem);
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _completionProducerCount);
                    try { _completionSignal.Set(); } catch { /* Dispose race */ }
                }
            }

            private static void CloseRegistrations(WorkItem item)
            {
                lock (item.RegistrationGate)
                {
                    item.PendingIndexed = 0;
                    item.CompletionClosed = true;
                }
            }

            private void CompleteRegistrations(
                WorkItem item,
                DoWriteTiming timing,
                DateTime completedUtc)
            {
                var registrationCount = item.Registrations.Count;
                for (var index = 0; index < registrationCount; index++)
                {
                    var registration = item.Registrations[index];
                    registration.Done?.TrySetResult(item.Result);
                    if (registration.Completion == null) continue;
                    var telemetry = new HighPriorityDoTelemetry
                    {
                        CommandId = registration.CommandId,
                        Channel = item.Channel,
                        TimeoutMs = registration.TimeoutMs,
                        QueueDepthAtEnqueue = registration.QueueDepthAtEnqueue,
                        CallerTimedOut = Volatile.Read(ref registration.CallerTimedOut) != 0,
                        Result = item.Result,
                        HardwareCompletedUtc = completedUtc,
                        QueueWaitMs = ElapsedMs(
                            registration.EnqueuedTicks,
                            item.DequeuedTicks),
                        WorkerExecutionMs = ElapsedMs(
                            item.DequeuedTicks,
                            item.CompletedTicks),
                        TotalMs = ElapsedMs(
                            registration.EnqueuedTicks,
                            item.CompletedTicks),
                        LockWaitMs = timing?.LockWaitMs ?? 0,
                        NiWriteMs = timing?.NiWriteMs ?? 0
                    };
                    QueueCompletion(registration, telemetry);
                }
                item.Registrations.Clear();
            }

            private void ReturnWorkItem(WorkItem item)
            {
                lock (_admissionStopGate)
                {
                    var slotIndex = item.SlotIndex;
                    item.ClearForReuse();
                    if (!_commandRing.Return(slotIndex))
                        throw new InvalidOperationException("DO命令环槽位被重复归还。");
                }
            }

            private void QueueCompletion(
                CompletionRegistration registration,
                HighPriorityDoTelemetry telemetry)
            {
                if (Interlocked.Exchange(ref registration.CompletionPublished, 1) != 0) return;
                Action callback = () =>
                {
                    try { registration.Completion(telemetry); }
                    catch
                    {
                        // 完成观察者不得影响专用 DO 线程和安全命令结果。
                    }
                    finally { ReleaseCompletionSlot(registration); }
                };
                Interlocked.Increment(ref _pendingCompletionItems);
                _completionQueue.Enqueue(callback);
                _completionSignal.Set();
            }

            private void CompletionLoop()
            {
                while (Volatile.Read(ref _completionProducerCount) > 0 ||
                       !_completionQueue.IsEmpty)
                {
                    if (!_completionQueue.TryDequeue(out var callback))
                    {
                        _completionSignal.WaitOne(50);
                        continue;
                    }

                    Interlocked.Decrement(ref _pendingCompletionItems);
                    callback();
                }
            }

            private static double ElapsedMs(long startedTicks, long completedTicks)
            {
                if (startedTicks <= 0 || completedTicks < startedTicks) return 0;
                return (completedTicks - startedTicks) * 1000.0 / Stopwatch.Frequency;
            }

            public void Dispose()
            {
                lock (_admissionStopGate)
                    _stopping = true;
                try { _signal.Set(); } catch { /* ignore */ }
                try { _completionSignal.Set(); } catch { /* ignore */ }
                var thread = _thread;
                var workerExited = thread == null;
                if (thread != null && Thread.CurrentThread != thread)
                {
                    try { workerExited = thread.Join(1000); } catch { /* ignore */ }
                }

                var disposeTiming = new DoWriteTiming();
                while (TryTakeBatch(_disposeBatchScratch, out _, out var batchCount))
                {
                    var completedTicks = Stopwatch.GetTimestamp();
                    var completedUtc = DateTime.UtcNow;
                    for (var index = 0; index < batchCount; index++)
                    {
                        var pending = _disposeBatchScratch[index];
                        _disposeBatchScratch[index] = null;
                        pending.Result = false;
                        pending.DequeuedTicks = completedTicks;
                        pending.CompletedTicks = completedTicks;
                        CloseRegistrations(pending);
                        Interlocked.Decrement(ref _pendingWorkItems);
                        CompleteRegistrations(pending, disposeTiming, completedUtc);
                        ReturnWorkItem(pending);
                    }
                }

                if (Interlocked.Exchange(ref _disposeProducerReleased, 1) == 0)
                    Interlocked.Decrement(ref _completionProducerCount);
                try { _completionSignal.Set(); } catch { /* ignore */ }
                var completionThread = _completionThread;
                var completionExited = completionThread == null;
                if (completionThread != null && Thread.CurrentThread != completionThread)
                {
                    try { completionExited = completionThread.Join(1000); } catch { /* ignore */ }
                }
                if (workerExited)
                {
                    try { _signal.Dispose(); } catch { /* ignore */ }
                }
                if (completionExited)
                {
                    try { _completionSignal.Dispose(); } catch { /* ignore */ }
                }
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
        /// Acceptance-only seam at the final batch-write boundary.  The
        /// production stop path still enters the real high-priority worker and
        /// this method; the seam is intentionally below Controller so tests
        /// cannot replace admission, registration, completion or receipt
        /// handling in EpbManager.
        /// </summary>
        internal IHighPriorityOffPhysicalWriter HighPriorityOffPhysicalWriter { get; set; }

        /// <summary>
        /// Returns the configured EPB scope used by the same DO controller
        /// that owns physical OFF admission.  It is deliberately not a
        /// manager/test supplied affected-channel list.
        /// </summary>
        /// <summary>
        /// Installs a logical device/index context for acceptance replay. It
        /// creates the same device maps, per-device worker and state vectors
        /// as Initialize, but deliberately does not touch NI hardware. The
        /// final physical writer hook is still invoked only after mapping,
        /// WriteGate acquisition and next-state construction.
        /// </summary>
        internal void ConfigureLogicalDeviceContextForAcceptance()
        {
            lock (_doTaskLock)
            {
                foreach (var device in _devices.Values)
                {
                    try { device.HighPriorityWorker.Dispose(); } catch { }
                }
                _devices.Clear();
                _epbIndex.Clear();
                if (_cfg?.Epb == null) return;

                foreach (var record in _cfg.Epb
                             .Where(item => item != null && item.Enabled &&
                                            item.Channel >= 1 && item.Channel <= 12)
                             .OrderBy(item => item.Channel))
                {
                    var deviceName = GetDeviceName(record.Pos);
                    if (string.IsNullOrWhiteSpace(deviceName) ||
                        !string.Equals(deviceName, GetDeviceName(record.Neg),
                                       StringComparison.OrdinalIgnoreCase))
                        continue;
                    var device = EnsureDevice(deviceName);
                    var posIndex = device.Lines.Count;
                    device.Lines.Add(record.Pos ?? string.Empty);
                    device.DefaultStates.Add(
                        string.Equals(record.Default, "正",
                                      StringComparison.OrdinalIgnoreCase));
                    var negIndex = device.Lines.Count;
                    device.Lines.Add(record.Neg ?? string.Empty);
                    device.DefaultStates.Add(
                        string.Equals(record.Default, "反",
                                      StringComparison.OrdinalIgnoreCase));
                    device.States = device.DefaultStates.ToArray();
                    _epbIndex[record.Channel] =
                        (deviceName, posIndex, negIndex);
                }
            }
        }

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
        /// 将 EPB 终态 OFF 非阻塞提交到所属设备的最高优先级专用 worker。
        /// </summary>
        /// <remarks>
        /// 返回 true 只表示有界 worker 队列已经接纳；不得据此宣称物理 DO 已关闭。
        /// 每次成功接纳（包括同通道合并请求）都会以独立 CommandId 精确回调一次。
        /// NI 写入仍只在专用 worker 中执行，没有使用 Task.Run 包装同步写入。
        /// </remarks>
        public bool TrySubmitEpbOffHighPriority(
            int channelNo,
            Action<HighPriorityDoTelemetry> completion,
            out Guid commandId)
        {
            var worker = _uninitializedHiWorker;
            string expectedDevice = null;
            if (_epbIndex.TryGetValue(channelNo, out var map) &&
                _devices.TryGetValue(map.dev, out var dev))
            {
                worker = dev.HighPriorityWorker;
                expectedDevice = map.dev;
            }

            return worker.TryPostHi(
                channelNo,
                (channels, timing) => SetEpbOffBatchCore(expectedDevice, channels, timing),
                telemetry =>
                {
                    try { completion?.Invoke(telemetry); }
                    catch
                    {
                        // 调用者完成处理不得破坏全局 DO 性能事件或 worker 生命周期。
                    }
                    // 先让安全状态机提交物理完成，再发布可能阻塞的诊断/日志观察者；
                    // 避免诊断延迟把已经完成的 NI 写误判成100ms硬超时。
                    PublishHighPriorityOffTelemetry(channelNo, telemetry);
                },
                out commandId);
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
                    var physicalWriter = HighPriorityOffPhysicalWriter;
                    if (physicalWriter != null)
                    {
                        try
                        {
                            if (!physicalWriter.TryWrite(
                                    targetDevice.Name,
                                    channels,
                                    toWrite))
                                return false;
                        }
                        catch (Exception ex)
                        {
                            QueueLog(() => LogError(
                                "EPB批量关闭注入写入失败：" + ex.Message,
                                "DO操作",
                                ex));
                            return false;
                        }
                    }
                    else
                    {
                        targetDevice.Writer.WriteSingleSampleSingleLine(true, toWrite);
                    }
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

        /// <summary>
        /// 冷启动安全基线：无条件销毁并重建 DO 任务，然后再次写入全零。
        /// 该入口不得在电源输出未确认关闭时调用，避免重建任务期间 XML 默认值
        /// 或设备残留状态短暂驱动继电器。
        /// </summary>
        public bool EstablishColdStartAllOffBaseline()
        {
            lock (_doTaskLock)
            {
                if (!Initialize())
                {
                    LogError("冷启动安全基线失败：DO 任务无法重新初始化。", "DO初始化");
                    return false;
                }

                if (!AllOff())
                {
                    LogError("冷启动安全基线失败：DO 全零写入未确认。", "DO操作");
                    return false;
                }

                LogInfo("冷启动安全基线已建立：DO任务已重建且所有EPB/压力路线均为OFF。", "DO操作");
                return true;
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
                // Acceptance replay supplies a logical device/task state and
                // replaces only the final NI writer call. Mapping, worker,
                // WriteGate and state vectors remain real.
                if (HighPriorityOffPhysicalWriter != null &&
                    dev.States != null && dev.Lines.Count > 0)
                    continue;
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
