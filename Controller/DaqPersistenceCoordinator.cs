using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;
using IO.NI;
using IAppLogger = Config.IAppLogger;

namespace Controller
{
    public enum DaqPersistenceState
    {
        Lagging,
        Paused,
        Recovering,
        Recovered,
        Failed
    }

    public sealed class DaqPersistenceStateChanged
    {
        public string Device { get; set; } = string.Empty;
        public DaqPersistenceState State { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public int QueueDepth { get; set; }
        public double OldestBatchAgeMs { get; set; }
        public long Generation { get; set; }
        /// <summary>Latest sequence whose batch physically completed the configured recorder write.</summary>
        public long Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public Guid CorrelationId { get; set; }
        public Guid RunId { get; set; }
        public long RunEpoch { get; set; }
        public long SuppressedBatchCount { get; set; }
        public long CumulativeSuppressedBatchCount { get; set; }
        public long LastTerminallyHandledSequence { get; set; }
        public long SuppressAfterSequence { get; set; }
        public long SuppressThroughSequence { get; set; }
        public long FirstSuppressedSequence { get; set; }
        public long LastSuppressedSequence { get; set; }
        public long SuppressedRangeCount { get; set; }
        public long DiscardedGenerationBatchCount { get; set; }
        public long OverCapacityDroppedBatchCount { get; set; }
        public bool DurabilityBlocked { get; set; }
        public long PendingHeadSequence { get; set; }
        public long InFlightSequence { get; set; }
        public int EpbId { get; set; }
        public int CycleNumber { get; set; }
        public int RecordLimit { get; set; }
    }

    internal sealed class DaqDurablePrefixResult
    {
        public string Device { get; set; } = string.Empty;
        public long Boundary { get; set; }
        public bool Completed { get; set; }
        public long Persisted { get; set; }
        public long PendingHeadSequence { get; set; }
        public long InFlightSequence { get; set; }
        public bool DurabilityBlocked { get; set; }
        public string PendingPredicate { get; set; } = string.Empty;
    }

    internal sealed class DaqPersistenceCoordinator : IDisposable
    {
        private sealed class DeviceQueue
        {
            public readonly ConcurrentQueue<DaqDiskBatch> Queue = new();
            public readonly SemaphoreSlim Signal = new(0);
            public readonly object WriteStallGate = new();
            public readonly object SuppressionGate = new();
            public readonly HashSet<string> ActiveCycleLimitFaults = new(StringComparer.Ordinal);
            public SemaphoreSlim Slots;
            // Single-consumer ownership slot. Once a batch leaves Queue it remains here until
            // the write has reached an explicit terminal boundary. The supervisor therefore
            // restarts the same batch after an unexpected worker fault instead of losing it.
            public DaqDiskBatch CurrentBatch;
            public int Count;
            public int PauseLatched;
            public int FreshAfterLowWater;
            public int PendingFreshWhileWrite;
            // Latest physically written sequence. This is deliberately not a synthetic
            // "terminally handled" watermark: explicitly excluded tails are audited separately.
            public long LastPersistedSequence;
            public long LastTerminallyHandledSequence;
            public long AcceptedGeneration;
            public long LastLagLogTicks;
            public long InFlightEnqueuedTicks;
            public long InFlightSequence;
            public long InFlightGeneration;
            public long WriteCallStartedTicks;
            public int WriteInFlight;
            public int WriteStallLatched;
            public int UnresolvedWriteFailure;
            public int FailureTimedOut;
            public int QueueFullLatched;
            // DateTime/Nullable<DateTime> reads are not atomic in the 32-bit production process.
            // Zero means admission is open; all other values are UTC DateTime ticks.
            public long SuppressAfterUtcTicks;
            public long SuppressAfterSequence;
            // long.MaxValue means the recovery tail is still open. ResumeAdmission freezes a
            // finite upper bound so delayed old-generation batches remain excluded while the
            // first strictly newer sequence reopens physical persistence.
            public long SuppressThroughSequence;
            // Guid is 16 bytes and can tear in the 32-bit production process. Publish an
            // immutable reference instead so every reader observes either the old or new
            // complete identity.
            public CorrelationIdentity Correlation;
            public long SuppressedBatchCount;
            public long CumulativeSuppressedBatchCount;
            public long FirstSuppressedSequence;
            public long LastSuppressedSequence;
            public long SuppressedRangeCount;
            public long DiscardedGenerationBatchCount;
            public long OverCapacityDroppedBatchCount;
        }

        private sealed class CorrelationIdentity
        {
            internal CorrelationIdentity(Guid value, Guid runId = default, long runEpoch = 0)
            {
                Value = value;
                RunId = runId;
                RunEpoch = runEpoch;
            }

            internal Guid Value { get; }
            internal Guid RunId { get; }
            internal long RunEpoch { get; }
        }

        private readonly Func<IEpbCycleRecorder> _recorder;
        private readonly IAppLogger _log;
        private readonly Action<DaqTimingRecord> _diagnostic;
        private readonly Action<string, long, int, int, double, double> _persistenceTiming;
        private static readonly int[] RetryDelaysMs = { 25, 50, 100, 250 };
        private readonly int _capacity;
        private readonly int _pauseDepth;
        private readonly int _resumeDepth;
        private readonly double _pauseAgeMs;
        private readonly double _resumeAgeMs;
        private readonly int _recoveryTimeoutMs;
        private readonly int _requiredFreshBatches;
        private readonly DeviceQueue _dev1 = new();
        private readonly DeviceQueue _dev2 = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _dev1Worker;
        private readonly Task _dev2Worker;
        private readonly Task _writeStallWatchdog;
        private readonly object _disposeGate = new();
        private int _disposed;
        private int _resourcesDisposed;
        private int _deferredDisposeScheduled;
        private int _enqueueInFlight;

        // Deterministic concurrency seam used only by the focused coordinator tests. It runs
        // after dequeue ownership is published and before the write starts.
        internal Action<string, long> BatchClaimedForTest;
        internal Action<string, long> AdmissionCommitBarrierForTest;

        internal DaqPersistenceCoordinator(
            Func<IEpbCycleRecorder> recorder,
            IAppLogger log,
            int capacity,
            int pauseDepth,
            int resumeDepth,
            double pauseAgeMs,
            double resumeAgeMs,
            int recoveryTimeoutMs,
            int requiredFreshBatches,
            Action<DaqTimingRecord> diagnostic = null,
            Action<string, long, int, int, double, double> persistenceTiming = null)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _log = log;
            _diagnostic = diagnostic;
            _persistenceTiming = persistenceTiming;
            _capacity = Math.Max(2, capacity);
            _dev1.Slots = new SemaphoreSlim(_capacity, _capacity);
            _dev2.Slots = new SemaphoreSlim(_capacity, _capacity);
            _pauseDepth = Math.Max(1, Math.Min(_capacity - 1, pauseDepth));
            _resumeDepth = Math.Max(0, Math.Min(_pauseDepth - 1, resumeDepth));
            _pauseAgeMs = Math.Max(100, pauseAgeMs);
            _resumeAgeMs = Math.Max(1, Math.Min(_pauseAgeMs, resumeAgeMs));
            _recoveryTimeoutMs = Math.Max(1000, recoveryTimeoutMs);
            _requiredFreshBatches = Math.Max(1, requiredFreshBatches);
            // 两块 DAQ 各自排队、各自写入。一个设备的慢盘/重试不再阻塞另一设备；
            // recorder 内部仍用通道锁和低频 SQLite 事务保证数据一致性。
            _dev1Worker = StartPersistenceWorker("Dev1", _dev1);
            _dev2Worker = StartPersistenceWorker("Dev2", _dev2);
            _writeStallWatchdog = StartWriteStallWatchdog();
        }

        private Task StartPersistenceWorker(string device, DeviceQueue queue)
        {
            return Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        if (Thread.CurrentThread.Name == null)
                            Thread.CurrentThread.Name = "EPB-Persist-" + device;
                    }
                    catch
                    {
                        // 线程命名只是诊断增强。
                    }
                    SuperviseWorkerLoop(device, queue);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private Task StartWriteStallWatchdog()
        {
            return Task.Factory.StartNew(
                WriteStallWatchdogLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        internal event Action<DaqPersistenceStateChanged> StateChanged;

        internal bool Enqueue(DaqDiskBatch batch)
        {
            if (batch == null) return false;
            Interlocked.Increment(ref _enqueueInFlight);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }

                var q = GetQueue(batch.Device);
                UpdateAcceptedGeneration(q, batch.Generation);

                // Cheap early handling avoids consuming a persistence slot for an already-known
                // excluded tail. The same predicate is checked again under the commit gate after
                // a slot is obtained; this early check alone is not the linearization point.
                lock (q.SuppressionGate)
                {
                    if (TryHandleSuppressionUnderGate(
                            q,
                            batch.Sequence,
                            closeFiniteWindow: false))
                    {
                        batch.Dispose();
                        return true;
                    }
                }

                var slotAcquired = false;
                try
                {
                    slotAcquired = q.Slots.Wait(0);
                    if (!slotAcquired)
                    {
                        // 容量满是一次状态跃迁，不是每个后续采样批次各自一个新故障。
                        // 旧实现对每个拒绝批次都发布 Failed，现场 20 秒内产生 1491 条
                        // 错误、1491 个 UI 更新和大量重复恢复任务，反过来继续拖慢写盘。
                        // 这里先同步锁存安全暂停，再仅由首个批次发布恢复事件。
                        Interlocked.Exchange(ref q.PauseLatched, 1);
                        if (Interlocked.CompareExchange(ref q.QueueFullLatched, 1, 0) == 0)
                        {
                            var correlation = EnsureCorrelation(q);
                            Publish(batch, q, DaqPersistenceState.Failed, "DaqPersistenceQueueFull",
                                $"{batch.Device} 持久化队列达到硬上限 {_capacity} 批；" +
                                "已锁存安全暂停，同一拥塞事件不重复发布故障。",
                                correlation);
                        }
                        // 当前调用位于 DAQ 后台工程线程，不是 NI 回调线程。容量用尽后允许
                        // 有界背压传回上游安全暂停，但绝不能丢弃已经接收、已分配池化所有权的批次。
                        q.Slots.Wait(_cts.Token);
                        slotAcquired = true;
                    }
                }
                catch
                {
                    // Enqueue(false) never transfers pooled-buffer ownership to this coordinator.
                    return false;
                }

                if (Volatile.Read(ref _disposed) != 0)
                {
                    if (slotAcquired)
                    {
                        try { q.Slots.Release(); }
                        catch { }
                    }
                    return false;
                }

                // Deterministic barrier for the final-check -> queue-publication shutdown race.
                // Drain observes _enqueueInFlight while this pre-commit seam is blocked.
                try
                {
                    AdmissionCommitBarrierForTest?.Invoke(batch.Device, batch.Sequence);
                }
                catch
                {
                    try { q.Slots.Release(); } catch { }
                    return false;
                }
                var suppressedAtCommit = false;
                lock (q.SuppressionGate)
                {
                    // SuppressAfter uses this same gate. A batch that observed admission-open,
                    // then waited for a slot or a test seam while cutoff was installed, must be
                    // reclassified as an explicit excluded tail before FIFO publication.
                    suppressedAtCommit = TryHandleSuppressionUnderGate(
                        q,
                        batch.Sequence,
                        closeFiniteWindow: true);
                    if (!suppressedAtCommit)
                    {
                        // Admission linearizes when the accepted batch becomes part of the
                        // counted FIFO while cutoff mutation is excluded by the same gate.
                        Interlocked.Increment(ref q.Count);
                        q.Queue.Enqueue(batch);
                    }
                }
                if (suppressedAtCommit)
                {
                    try { q.Slots.Release(); } catch { }
                    batch.Dispose();
                    return true;
                }
                q.Signal.Release();
                EvaluateLag(batch.Device, q, batch);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _enqueueInFlight);
            }
        }

        internal void SuppressAfter(
            string device,
            DateTime cutoffUtc,
            long lastSequenceThatMustBePhysicallyPersisted,
            Guid correlationId,
            Guid runId = default,
            long runEpoch = 0)
        {
            var q = GetQueue(device);
            lock (q.SuppressionGate)
            {
                Interlocked.Exchange(
                    ref q.SuppressAfterSequence,
                    Math.Max(0, lastSequenceThatMustBePhysicallyPersisted));
                Interlocked.Exchange(ref q.SuppressThroughSequence, long.MaxValue);
                Interlocked.Exchange(
                    ref q.SuppressAfterUtcTicks,
                    cutoffUtc.ToUniversalTime().Ticks);
                Interlocked.Exchange(ref q.SuppressedBatchCount, 0);
                var previous = GetCorrelationIdentity(q);
                var effectiveCorrelation = correlationId != Guid.Empty
                    ? correlationId
                    : previous?.Value ?? Guid.NewGuid();
                var effectiveRunId = runId != Guid.Empty
                    ? runId
                    : previous?.RunId ?? Guid.Empty;
                var effectiveRunEpoch = runEpoch > 0
                    ? runEpoch
                    : previous?.RunEpoch ?? 0;
                SetCorrelation(
                    q,
                    effectiveCorrelation,
                    effectiveRunId,
                    effectiveRunEpoch);
            }
        }

        internal void ResumeAdmission(string device, long closeSuppressionThroughSequence)
        {
            var q = GetQueue(device);
            lock (q.SuppressionGate)
            {
                if (Interlocked.Read(ref q.SuppressAfterUtcTicks) != 0)
                {
                    var fromExclusive = Interlocked.Read(ref q.SuppressAfterSequence);
                    Interlocked.Exchange(
                        ref q.SuppressThroughSequence,
                        Math.Max(fromExclusive, closeSuppressionThroughSequence));
                }
                else
                {
                    Interlocked.Exchange(ref q.SuppressAfterSequence, 0);
                    Interlocked.Exchange(ref q.SuppressThroughSequence, 0);
                    SetCorrelation(q, Guid.Empty);
                }
            }
            Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
            Interlocked.Exchange(ref q.PendingFreshWhileWrite, 0);
            Interlocked.Exchange(ref q.PauseLatched, 0);
            Interlocked.Exchange(ref q.QueueFullLatched, 0);
        }

        internal void AcceptGeneration(string device, long generation)
        {
            var q = GetQueue(device);
            // generation 只用于恢复身份和诊断。已进入持久化 FIFO 的旧代次批次仍必须
            // 真实写入，不能以“代次已更新”为由伪装成已持久化。
            UpdateAcceptedGeneration(q, generation);
        }

        internal DaqPersistenceStateChanged GetSnapshot(string device)
        {
            var q = GetQueue(device);
            CorrelationIdentity identity;
            long suppressedBatchCount;
            long cumulativeSuppressedBatchCount;
            long lastTerminallyHandledSequence;
            long suppressAfterSequence;
            long suppressThroughSequence;
            long firstSuppressedSequence;
            long lastSuppressedSequence;
            long suppressedRangeCount;
            lock (q.SuppressionGate)
            {
                identity = GetCorrelationIdentity(q);
                suppressedBatchCount = Interlocked.Read(ref q.SuppressedBatchCount);
                cumulativeSuppressedBatchCount = Interlocked.Read(
                    ref q.CumulativeSuppressedBatchCount);
                lastTerminallyHandledSequence = Interlocked.Read(
                    ref q.LastTerminallyHandledSequence);
                suppressAfterSequence = Interlocked.Read(ref q.SuppressAfterSequence);
                suppressThroughSequence = Interlocked.Read(ref q.SuppressThroughSequence);
                firstSuppressedSequence = Interlocked.Read(ref q.FirstSuppressedSequence);
                lastSuppressedSequence = Interlocked.Read(ref q.LastSuppressedSequence);
                suppressedRangeCount = Interlocked.Read(ref q.SuppressedRangeCount);
            }
            var pendingHeadSequence = q.Queue.TryPeek(out var pendingHead)
                ? pendingHead.Sequence
                : 0;
            return new DaqPersistenceStateChanged
            {
                Device = NormalizeDevice(device),
                State = Volatile.Read(ref q.PauseLatched) != 0
                    ? (Volatile.Read(ref q.FailureTimedOut) != 0 ||
                       Volatile.Read(ref q.QueueFullLatched) != 0
                        ? DaqPersistenceState.Failed
                        : DaqPersistenceState.Paused)
                    : DaqPersistenceState.Recovered,
                Code = Volatile.Read(ref q.FailureTimedOut) != 0
                    ? (Volatile.Read(ref q.WriteStallLatched) != 0
                        ? "DaqPersistenceWriteStall"
                        : "DaqPersistenceRecoveryTimeout")
                    : (Volatile.Read(ref q.QueueFullLatched) != 0
                        ? "DaqPersistenceQueueFull"
                        : (Volatile.Read(ref q.PauseLatched) != 0
                            ? "DaqPersistenceLag"
                            : "Healthy")),
                QueueDepth = Volatile.Read(ref q.Count),
                OldestBatchAgeMs = GetOldestAge(q),
                Generation = Interlocked.Read(ref q.AcceptedGeneration),
                Sequence = Interlocked.Read(ref q.LastPersistedSequence),
                TimestampUtc = DateTime.UtcNow,
                CorrelationId = identity?.Value ?? Guid.Empty,
                RunId = identity?.RunId ?? Guid.Empty,
                RunEpoch = identity?.RunEpoch ?? 0,
                SuppressedBatchCount = suppressedBatchCount,
                CumulativeSuppressedBatchCount = cumulativeSuppressedBatchCount,
                LastTerminallyHandledSequence = lastTerminallyHandledSequence,
                SuppressAfterSequence = suppressAfterSequence,
                SuppressThroughSequence = suppressThroughSequence,
                FirstSuppressedSequence = firstSuppressedSequence,
                LastSuppressedSequence = lastSuppressedSequence,
                SuppressedRangeCount = suppressedRangeCount,
                DiscardedGenerationBatchCount = Interlocked.Read(ref q.DiscardedGenerationBatchCount),
                OverCapacityDroppedBatchCount = Interlocked.Read(ref q.OverCapacityDroppedBatchCount),
                DurabilityBlocked = Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                                    Volatile.Read(ref q.FailureTimedOut) != 0,
                PendingHeadSequence = pendingHeadSequence,
                InFlightSequence = Interlocked.Read(ref q.InFlightSequence)
            };
        }

        internal async Task<bool> WaitForPersistedAsync(
            string device,
            long sequence,
            int timeoutMs,
            CancellationToken token)
        {
            if (sequence <= 0) return true;
            var q = GetQueue(device);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                if (Interlocked.Read(ref q.LastPersistedSequence) >= sequence) return true;
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            return Interlocked.Read(ref q.LastPersistedSequence) >= sequence;
        }

        /// <summary>
        /// 等待指定序号真实完成写入且不存在未解决写故障。仅比较 LastPersistedSequence
        /// 不足以作为封圈边界：旧实现可能在写失败后仍推进序号。
        /// </summary>
        internal async Task<bool> WaitForDurableBoundaryAsync(
            string device,
            long sequence,
            int timeoutMs,
            CancellationToken token)
        {
            if (sequence <= 0) return true;
            var q = GetQueue(device);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                if (Interlocked.Read(ref q.LastPersistedSequence) >= sequence &&
                    Volatile.Read(ref q.UnresolvedWriteFailure) == 0 &&
                    Volatile.Read(ref q.FailureTimedOut) == 0 &&
                    Volatile.Read(ref q.Count) == 0 &&
                    Volatile.Read(ref q.WriteInFlight) == 0)
                    return true;
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            return Interlocked.Read(ref q.LastPersistedSequence) >= sequence &&
                   Volatile.Read(ref q.UnresolvedWriteFailure) == 0 &&
                   Volatile.Read(ref q.FailureTimedOut) == 0 &&
                   Volatile.Read(ref q.Count) == 0 &&
                   Volatile.Read(ref q.WriteInFlight) == 0;
        }

        /// <summary>
        /// 等待指定生产序号之前的已接纳批次全部真实写入。与整设备截止使用的
        /// <see cref="WaitForDurableBoundaryAsync"/> 不同，本门禁允许同一设备上的健康通道
        /// 继续产生更高序号批次，因此不要求整条队列为零；但队头和在途批次都必须已经
        /// 越过目标序号，且期间不得发生未解决写故障。SuppressAfter 只拒绝截止点之后的
        /// 新准入，不撤销截止点之前已经接收并真实写入的 FIFO 前缀；generation 切换同样
        /// 不撤销已接收 FIFO 的耐久义务，因此二者都不能作为拒绝前缀的条件。
        /// </summary>
        internal async Task<bool> WaitForDurablePrefixAsync(
            string device,
            long sequence,
            int timeoutMs,
            CancellationToken token)
            => (await WaitForDurablePrefixDetailedAsync(
                    device,
                    sequence,
                    timeoutMs,
                    token)
                .ConfigureAwait(false)).Completed;

        internal async Task<DaqDurablePrefixResult> WaitForDurablePrefixDetailedAsync(
            string device,
            long sequence,
            int timeoutMs,
            CancellationToken token)
        {
            var q = GetQueue(device);
            if (sequence <= 0) return CaptureDurablePrefix(q, device, sequence);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                var current = CaptureDurablePrefix(q, device, sequence);
                if (current.Completed) return current;
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            return CaptureDurablePrefix(q, device, sequence);
        }

        private static DaqDurablePrefixResult CaptureDurablePrefix(
            DeviceQueue q,
            string device,
            long sequence)
        {
            var persisted = Interlocked.Read(ref q.LastPersistedSequence);
            var inFlight = Interlocked.Read(ref q.InFlightSequence);
            // ConcurrentQueue.TryPeek 与 worker 并发安全。批次若恰好在读取后被归还
            // 对象池，Sequence 可能变为 0；这只会保守地多等一轮，不会提前放行。
            var pendingHead = q.Queue.TryPeek(out var pending) ? pending.Sequence : 0;
            var durabilityBlocked = Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                                    Volatile.Read(ref q.FailureTimedOut) != 0;
            string predicate;
            if (durabilityBlocked) predicate = "DurabilityBlocked";
            else if (Volatile.Read(ref q.QueueFullLatched) != 0) predicate = "QueueFullLatched";
            else if (persisted < sequence) predicate = "PersistedBelowBoundary";
            else if (inFlight > 0 && inFlight <= sequence) predicate = "InFlightAtOrBelowBoundary";
            else if (pendingHead > 0 && pendingHead <= sequence) predicate = "PendingHeadAtOrBelowBoundary";
            else predicate = string.Empty;
            return new DaqDurablePrefixResult
            {
                Device = NormalizeDevice(device),
                Boundary = Math.Max(0, sequence),
                Completed = predicate.Length == 0,
                Persisted = persisted,
                PendingHeadSequence = pendingHead,
                InFlightSequence = inFlight,
                DurabilityBlocked = durabilityBlocked,
                PendingPredicate = predicate
            };
        }

        internal async Task<bool> DrainAsync(int timeoutMs)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (Volatile.Read(ref _enqueueInFlight) == 0 &&
                    IsDrained(_dev1) && IsDrained(_dev2))
                    return true;
                await Task.Delay(10).ConfigureAwait(false);
            }
            return Volatile.Read(ref _enqueueInFlight) == 0 &&
                   IsDrained(_dev1) && IsDrained(_dev2);
        }

        internal async Task<bool> ShutdownAsync(int timeoutMs)
        {
            if (Volatile.Read(ref _resourcesDisposed) != 0) return true;
            var drained = await DrainAsync(timeoutMs).ConfigureAwait(false);
            // A timeout is not permission to discard the accepted FIFO prefix. Keep the
            // workers alive so storage recovery can finish the original batches; the caller
            // must remain safely stopped and retry shutdown later.
            if (!drained) return false;

            // Preserve the historical retry contract: a timed-out ShutdownAsync does not close
            // admission. Once a clean drain is observed, close admission and check again to cover
            // the narrow producer race between the first observation and the close.
            Interlocked.Exchange(ref _disposed, 1);
            if (!await DrainAsync(timeoutMs).ConfigureAwait(false)) return false;
            return TryCompleteDispose(Math.Max(1000, timeoutMs));
        }

        private void SuperviseWorkerLoop(string device, DeviceQueue q)
        {
            var restartCount = 0;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    WorkerLoopCore(device, q);
                    return;
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    restartCount++;
                    Interlocked.Exchange(ref q.UnresolvedWriteFailure, 1);
                    Interlocked.Exchange(ref q.FailureTimedOut, 1);
                    Interlocked.Exchange(ref q.PauseLatched, 1);
                    var correlation = EnsureCorrelation(q);
                    Publish(
                        null,
                        q,
                        DaqPersistenceState.Failed,
                        "DaqPersistenceWorkerFault",
                        $"{device} 持久化专用线程异常，100ms后监督重启；" +
                        $"Restart={restartCount} Error={ex.Message}",
                        correlation);
                    if (_cts.Token.WaitHandle.WaitOne(100)) return;
                }
            }
        }

        private void WriteStallWatchdogLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    CheckWriteStall("Dev1", _dev1);
                    CheckWriteStall("Dev2", _dev2);
                    if (_cts.Token.WaitHandle.WaitOne(
                            Math.Max(10, Math.Min(100, _recoveryTimeoutMs / 10))))
                        return;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Normal coordinator shutdown.
            }
            catch (Exception ex)
            {
                SafeWarn($"DAQ持久化卡死看门狗异常：{ex.Message}", "落盘");
            }
        }

        private void CheckWriteStall(string device, DeviceQueue q)
        {
            lock (q.WriteStallGate)
            {
                if (Volatile.Read(ref q.WriteInFlight) == 0) return;
                var started = Interlocked.Read(ref q.WriteCallStartedTicks);
                if (started <= 0) return;
                var elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (elapsed < _recoveryTimeoutMs ||
                    Interlocked.CompareExchange(ref q.WriteStallLatched, 1, 0) != 0)
                    return;

                Interlocked.Exchange(ref q.UnresolvedWriteFailure, 1);
                Interlocked.Exchange(ref q.FailureTimedOut, 1);
                Interlocked.Exchange(ref q.PauseLatched, 1);
                var correlation = EnsureCorrelation(q);
                var sequence = Interlocked.Read(ref q.InFlightSequence);
                var generation = Interlocked.Read(ref q.InFlightGeneration);
                Publish(
                    null,
                    q,
                    DaqPersistenceState.Failed,
                    "DaqPersistenceWriteStall",
                    $"{device} 持久化同步写调用连续 {elapsed:F0}ms 未返回；" +
                    $"Sequence={sequence}，已锁存进程回收故障，禁止并行重写当前批次。",
                    correlation,
                    generationOverride: generation,
                    sequenceOverride: sequence);
            }
        }

        private void WorkerLoopCore(string device, DeviceQueue q)
        {
            while (!_cts.IsCancellationRequested)
            {
                var batch = Volatile.Read(ref q.CurrentBatch);
                if (batch == null)
                {
                    q.Signal.Wait(_cts.Token);

                    // Publish in-flight ownership while the batch is still visible at the queue
                    // head. Readers therefore observe either Queue.Peek or InFlight, never the
                    // old dequeue->publish empty window.
                    if (!q.Queue.TryPeek(out var pending)) continue;
                    Interlocked.Exchange(ref q.InFlightSequence, pending.Sequence);
                    Interlocked.Exchange(ref q.InFlightGeneration, pending.Generation);
                    Interlocked.Exchange(
                        ref q.InFlightEnqueuedTicks,
                        pending.EnqueuedMonotonicTicks);
                    Interlocked.Exchange(ref q.WriteInFlight, 1);

                    if (!q.Queue.TryDequeue(out batch))
                    {
                        ClearInFlight(q);
                        continue;
                    }
                    Volatile.Write(ref q.CurrentBatch, batch);
                    Interlocked.Decrement(ref q.Count);
                    q.Slots.Release();
                    BatchClaimedForTest?.Invoke(device, batch.Sequence);
                }

                // No finally is allowed to dispose this batch. If anything outside the normal
                // write retry loop faults, q.CurrentBatch remains owned by the supervisor and is
                // retried before any later FIFO item when WorkerLoopCore is entered again.
                WriteWithRetry(batch, q);

                var batchDevice = batch.Device;
                var sequence = batch.Sequence;
                MarkTerminallyHandled(q, sequence);
                MarkPersisted(q, sequence);
                Volatile.Write(ref q.CurrentBatch, null);
                ClearInFlight(q);
                batch.Dispose();

                // batch has been returned to the pool and must not be read again.
                EvaluateRecovery(batchDevice, q, null);
            }
        }

        private void WriteWithRetry(DaqDiskBatch batch, DeviceQueue q)
        {
            var start = Stopwatch.GetTimestamp();
            var attempt = 0;
            var mappingRecoveryAttempted = false;
            while (true)
            {
                try
                {
                    var writeStarted = Stopwatch.GetTimestamp();
                    lock (q.WriteStallGate)
                        Interlocked.Exchange(ref q.WriteCallStartedTicks, writeStarted);
                    try
                    {
                        WriteBatch(batch);
                    }
                    finally
                    {
                        lock (q.WriteStallGate)
                            Interlocked.Exchange(ref q.WriteCallStartedTicks, 0);
                    }
                    q.ActiveCycleLimitFaults.Clear();
                    Interlocked.Exchange(ref q.UnresolvedWriteFailure, 0);
                    Interlocked.Exchange(ref q.FailureTimedOut, 0);
                    try
                    {
                        _persistenceTiming?.Invoke(
                            batch.Device,
                            batch.Generation,
                            batch.SampleCount,
                            Volatile.Read(ref q.Count),
                            batch.AgeMs,
                            (Stopwatch.GetTimestamp() - writeStarted) * 1000.0 / Stopwatch.Frequency);
                    }
                    catch (Exception ex)
                    {
                        SafeWarn(
                            $"DAQ持久化时序观察者异常已隔离：{ex.Message}",
                            "落盘");
                    }
                    return;
                }
                catch (ActiveCycleDataLimitExceededException ex)
                {
                    // EpbDiskWriter has already latched the offending channel and rolled back the
                    // device-batch accounting. Retain this exact CurrentBatch and retry it: the
                    // next attempt skips the latched channel but still writes every healthy peer.
                    // Returning here would let WorkerLoopCore mark the whole device batch as
                    // persisted even though the writer rolled it back.
                    var faultIdentity = ex.EpbId + ":" + ex.CycleNumber;
                    if (q.ActiveCycleLimitFaults.Add(faultIdentity))
                    {
                        var correlation = EnsureCorrelation(q);
                        Publish(batch, q, DaqPersistenceState.Failed, "ActiveCycleDataLimitExceeded",
                            $"活动圈数据达到安全上限。EPB={ex.EpbId} " +
                            $"Cycle={ex.CycleNumber} Limit={ex.Limit}；" +
                            "保留当前整设备批次原序重试，健康通道真实写入后才推进耐久边界。",
                            correlation,
                            ex.EpbId,
                            ex.CycleNumber,
                            ex.Limit);
                    }
                    var delay = RetryDelaysMs[Math.Min(attempt++, RetryDelaysMs.Length - 1)];
                    if (_cts.Token.WaitHandle.WaitOne(delay))
                        _cts.Token.ThrowIfCancellationRequested();
                }
                catch (Exception ex)
                {
                    // 地址空间或已关闭访问器属于写盘器内部的可恢复状态。
                    // 先在进程内释放短视图并立即重试，成功时不触发 DAQ 停机恢复链。
                    var recoverable = _recorder() as IRecoverableCycleRecorder;
                    if (!mappingRecoveryAttempted && recoverable != null &&
                        recoverable.TryRecoverStorage(ex, out var recoveryDetail))
                    {
                        mappingRecoveryAttempted = true;
                        SafeWarn(
                            $"DAQ写盘映射已进程内自愈，立即重试当前批次：" +
                            $"Device={batch.Device} Sequence={batch.Sequence} {recoveryDetail} " +
                            $"Cause={ex.GetType().Name}: {ex.Message}",
                            "落盘");
                        continue;
                    }

                    Interlocked.Exchange(ref q.UnresolvedWriteFailure, 1);
                    var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    var correlation = EnsureCorrelation(q);
                    if (Interlocked.CompareExchange(ref q.PauseLatched, 1, 0) == 0)
                        Publish(batch, q, DaqPersistenceState.Paused, "DaqPersistenceLag",
                            $"写盘异常，已进入安全暂停并重试：{ex.Message}", correlation);
                    if (elapsed >= _recoveryTimeoutMs &&
                        Interlocked.CompareExchange(ref q.FailureTimedOut, 1, 0) == 0)
                    {
                        Publish(batch, q, DaqPersistenceState.Failed,
                            "DaqPersistenceRecoveryTimeout",
                            $"写盘连续 {_recoveryTimeoutMs}ms 未恢复，已安全停机但保留当前批次继续重试：" +
                            ex.Message,
                            correlation);
                    }
                    // 超过恢复时限只触发控制层安全停机，不能丢弃当前批次。worker 保留
                    // 该批次并以最大 250ms 退避持续重试；磁盘恢复后按原 FIFO 顺序写入。
                    var delay = RetryDelaysMs[Math.Min(attempt++, RetryDelaysMs.Length - 1)];
                    if (_cts.Token.WaitHandle.WaitOne(delay))
                        _cts.Token.ThrowIfCancellationRequested();
                }
            }
        }

        private void WriteBatch(DaqDiskBatch batch)
        {
            var recorder = _recorder();
            if (recorder == null)
                throw new InvalidOperationException(
                    "DAQ持久化记录器暂不可用；保留当前批次并等待记录器恢复。");
            var batched = recorder as IBatchedEpbCycleRecorder;
            if (batched != null)
            {
                double[] zeroPressure = null;
                EpbChannelDiskBatch[] pooledChannels = null;
                try
                {
                    if (batch.PressureGroup1 == null || batch.PressureGroup2 == null)
                        zeroPressure = ArrayPoolZeroCache.Get(batch.SampleCount);
                    var channels = pooledChannels =
                        ArrayPool<EpbChannelDiskBatch>.Shared.Rent(Math.Max(1, batch.ChannelCount));
                    for (var i = 0; i < batch.ChannelCount; i++)
                    {
                        var source = batch.Channels[i];
                        channels[i] = new EpbChannelDiskBatch(
                            source.EpbId,
                            source.Currents,
                            (source.EpbId <= 6
                                ? batch.PressureGroup1
                                : batch.PressureGroup2) ?? zeroPressure);
                    }

                    if (batched is ICountedBatchedEpbCycleRecorder counted)
                    {
                        counted.WriteDeviceBatch(
                            batch.TimestampsUtc,
                            channels,
                            batch.ChannelCount,
                            batch.SampleCount);
                    }
                    else
                    {
                        // Compatibility path for third-party recorders that only implement the
                        // original interface. The built-in writer uses the counted pooled path.
                        var exact = new EpbChannelDiskBatch[batch.ChannelCount];
                        Array.Copy(channels, exact, batch.ChannelCount);
                        batched.WriteDeviceBatch(batch.TimestampsUtc, exact, batch.SampleCount);
                    }
                    return;
                }
                finally
                {
                    if (pooledChannels != null)
                    {
                        Array.Clear(pooledChannels, 0, batch.ChannelCount);
                        ArrayPool<EpbChannelDiskBatch>.Shared.Return(pooledChannels, clearArray: false);
                    }
                    if (zeroPressure != null) ArrayPoolZeroCache.Return(zeroPressure);
                }
            }
            for (var i = 0; i < batch.ChannelCount; i++)
            {
                var channel = batch.Channels[i];
                var pressure = channel.EpbId <= 6 ? batch.PressureGroup1 : batch.PressureGroup2;
                if (pressure == null)
                {
                    pressure = ArrayPoolZeroCache.Get(batch.SampleCount);
                    try
                    {
                        WriteCompatibility(recorder, channel.EpbId, batch, channel.Currents, pressure);
                    }
                    finally { ArrayPoolZeroCache.Return(pressure); }
                }
                else
                    WriteCompatibility(recorder, channel.EpbId, batch, channel.Currents, pressure);
            }
        }

        private static void WriteCompatibility(
            IEpbCycleRecorder recorder,
            int epbId,
            DaqDiskBatch batch,
            double[] currents,
            double[] pressure)
        {
            var ts = new DateTime[batch.SampleCount];
            var cur = new double[batch.SampleCount];
            var pr = new double[batch.SampleCount];
            Array.Copy(batch.TimestampsUtc, ts, batch.SampleCount);
            Array.Copy(currents, cur, batch.SampleCount);
            Array.Copy(pressure, pr, batch.SampleCount);
            recorder.WriteBatch(epbId, ts, cur, pr);
        }

        private void EvaluateLag(string device, DeviceQueue q, DaqDiskBatch batch)
        {
            var depth = Volatile.Read(ref q.Count);
            var age = GetOldestAge(q);
            if (depth >= _pauseDepth || age >= _pauseAgeMs)
            {
                var correlation = EnsureCorrelation(q);
                if (Interlocked.CompareExchange(ref q.PauseLatched, 1, 0) == 0)
                    Publish(batch, q, DaqPersistenceState.Paused, "DaqPersistenceLag",
                        $"{device} 持久化积压触发安全暂停。Depth={depth}, Oldest={age:F1}ms。", correlation);
                return;
            }
            if (age < 100) return;
            var now = Stopwatch.GetTimestamp();
            var last = Interlocked.Read(ref q.LastLagLogTicks);
            if (last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency < 1000) return;
            Interlocked.Exchange(ref q.LastLagLogTicks, now);
            Publish(batch, q, DaqPersistenceState.Lagging, "DaqPersistenceLag",
                $"{device} 持久化延迟。Depth={depth}, Oldest={age:F1}ms。", GetCorrelation(q));
        }

        private void EvaluateRecovery(string device, DeviceQueue q, DaqDiskBatch batch)
        {
            if (Volatile.Read(ref q.PauseLatched) == 0) return;
            var depth = Volatile.Read(ref q.Count);
            var age = GetOldestAge(q);
            if (Volatile.Read(ref q.WriteInFlight) != 0)
            {
                if (depth <= _resumeDepth && age <= _resumeAgeMs)
                    Interlocked.Increment(ref q.PendingFreshWhileWrite);
                else
                {
                    Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
                    Interlocked.Exchange(ref q.PendingFreshWhileWrite, 0);
                }
                return;
            }
            if (Volatile.Read(ref q.UnresolvedWriteFailure) != 0) return;
            if (depth <= _resumeDepth && age <= _resumeAgeMs)
            {
                var credit = 1 + Interlocked.Exchange(ref q.PendingFreshWhileWrite, 0);
                if (Interlocked.Add(ref q.FreshAfterLowWater, credit) >= _requiredFreshBatches)
                {
                    Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
                    Interlocked.Exchange(ref q.PauseLatched, 0);
                    Interlocked.Exchange(ref q.QueueFullLatched, 0);
                    Interlocked.Exchange(ref q.WriteStallLatched, 0);
                    Publish(batch, q, DaqPersistenceState.Recovered, "DaqPersistenceRecovered",
                        $"{device} 持久化队列已恢复。Depth={depth}, Oldest={age:F1}ms, " +
                        $"OverCapacityDropped={Interlocked.Read(ref q.OverCapacityDroppedBatchCount)}。",
                        GetCorrelation(q));
                }
            }
            else
            {
                Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
                Interlocked.Exchange(ref q.PendingFreshWhileWrite, 0);
            }
        }

        private void Publish(
            DaqDiskBatch batch,
            DeviceQueue q,
            DaqPersistenceState state,
            string code,
            string reason,
            Guid correlationId,
            int epbId = 0,
            int cycleNumber = 0,
            int recordLimit = 0,
            long? generationOverride = null,
            long? sequenceOverride = null)
        {
            var identity = GetCorrelationIdentity(q);
            var matchingIdentity = identity != null && identity.Value == correlationId
                ? identity
                : null;
            var update = new DaqPersistenceStateChanged
            {
                Device = batch?.Device ?? (ReferenceEquals(q, _dev1) ? "Dev1" : "Dev2"),
                State = state,
                Code = code,
                Reason = reason,
                QueueDepth = Volatile.Read(ref q.Count),
                OldestBatchAgeMs = GetOldestAge(q),
                Generation = generationOverride ??
                             batch?.Generation ??
                             Interlocked.Read(ref q.AcceptedGeneration),
                Sequence = sequenceOverride ??
                           batch?.Sequence ??
                           Interlocked.Read(ref q.LastPersistedSequence),
                TimestampUtc = DateTime.UtcNow,
                CorrelationId = correlationId,
                RunId = matchingIdentity?.RunId ?? Guid.Empty,
                RunEpoch = matchingIdentity?.RunEpoch ?? 0,
                SuppressedBatchCount = Interlocked.Read(ref q.SuppressedBatchCount),
                CumulativeSuppressedBatchCount = Interlocked.Read(
                    ref q.CumulativeSuppressedBatchCount),
                LastTerminallyHandledSequence = Interlocked.Read(
                    ref q.LastTerminallyHandledSequence),
                SuppressAfterSequence = Interlocked.Read(ref q.SuppressAfterSequence),
                SuppressThroughSequence = Interlocked.Read(ref q.SuppressThroughSequence),
                FirstSuppressedSequence = Interlocked.Read(ref q.FirstSuppressedSequence),
                LastSuppressedSequence = Interlocked.Read(ref q.LastSuppressedSequence),
                SuppressedRangeCount = Interlocked.Read(ref q.SuppressedRangeCount),
                DiscardedGenerationBatchCount = Interlocked.Read(ref q.DiscardedGenerationBatchCount),
                OverCapacityDroppedBatchCount = Interlocked.Read(ref q.OverCapacityDroppedBatchCount),
                DurabilityBlocked = Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                                    Volatile.Read(ref q.FailureTimedOut) != 0,
                PendingHeadSequence = q.Queue.TryPeek(out var pendingHead)
                    ? pendingHead.Sequence
                    : 0,
                InFlightSequence = Interlocked.Read(ref q.InFlightSequence),
                EpbId = epbId,
                CycleNumber = cycleNumber,
                RecordLimit = recordLimit
            };
            try
            {
                if (state == DaqPersistenceState.Lagging)
                    _log?.Warn(reason, "落盘");
                else if (state == DaqPersistenceState.Failed)
                    _log?.Error(reason, "落盘");
                else
                    _log?.Warn(reason, "落盘");
            }
            catch
            {
                // State publication is part of the persistence supervisor and must be no-throw.
            }
            try
            {
                NonCriticalObserver.Invoke(
                    StateChanged,
                    update,
                    ex =>
                    {
                        try
                        {
                            _log?.Warn(
                                $"DAQ持久化状态观察者异常已隔离：{ex.Message}",
                                "落盘");
                        }
                        catch { }
                    });
            }
            catch
            {
                // Defensive boundary in case an observer dispatcher itself fails.
            }
            try
            {
                _diagnostic?.Invoke(new DaqTimingRecord
                {
                    TimestampUtc = update.TimestampUtc,
                    Device = update.Device,
                    Kind = state == DaqPersistenceState.Failed ? "SystemFault" : "Lag",
                    Generation = update.Generation,
                    QueueDepth = update.QueueDepth,
                    QueueAgeMs = update.OldestBatchAgeMs,
                    Detail = $"Code={code}; CorrelationId={correlationId:N}; " +
                             $"RunId={update.RunId:N}; RunEpoch={update.RunEpoch}; {reason}"
                });
            }
            catch (Exception ex)
            {
                try
                {
                    _log?.Warn($"DAQ诊断观察者异常已隔离：{ex.Message}", "落盘");
                }
                catch { }
            }
        }

        private static void MarkPersisted(DeviceQueue q, long sequence)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref q.LastPersistedSequence);
                if (current >= sequence) return;
            } while (Interlocked.CompareExchange(ref q.LastPersistedSequence, sequence, current) != current);
        }

        private static void MarkTerminallyHandled(DeviceQueue q, long sequence)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref q.LastTerminallyHandledSequence);
                if (current >= sequence) return;
            } while (Interlocked.CompareExchange(
                         ref q.LastTerminallyHandledSequence,
                         sequence,
                         current) != current);
        }

        // Caller holds q.SuppressionGate.
        private static bool TryHandleSuppressionUnderGate(
            DeviceQueue q,
            long sequence,
            bool closeFiniteWindow)
        {
            var suppressAfterTicks = Interlocked.Read(ref q.SuppressAfterUtcTicks);
            if (suppressAfterTicks <= 0) return false;
            var suppressAfterSequence = Interlocked.Read(ref q.SuppressAfterSequence);
            var suppressThroughSequence = Interlocked.Read(ref q.SuppressThroughSequence);
            if (suppressAfterSequence >= 0 &&
                sequence > suppressAfterSequence &&
                (suppressThroughSequence == long.MaxValue ||
                 sequence <= suppressThroughSequence))
            {
                // Explicitly excluded batches are terminally handled but not physically written.
                RecordSuppressedUnderGate(q, sequence);
                MarkTerminallyHandled(q, sequence);
                return true;
            }

            if (closeFiniteWindow &&
                suppressThroughSequence != long.MaxValue &&
                sequence > suppressThroughSequence)
            {
                // Per-device processing is FIFO. The first sequence beyond the finite resume
                // floor closes the active window; cumulative evidence remains intact.
                Interlocked.Exchange(ref q.SuppressAfterUtcTicks, 0);
                Interlocked.Exchange(ref q.SuppressAfterSequence, 0);
                Interlocked.Exchange(ref q.SuppressThroughSequence, 0);
                SetCorrelation(q, Guid.Empty);
            }
            return false;
        }

        // Caller holds q.SuppressionGate.
        private static void RecordSuppressedUnderGate(DeviceQueue q, long sequence)
        {
            Interlocked.Increment(ref q.SuppressedBatchCount);
            Interlocked.Increment(ref q.CumulativeSuppressedBatchCount);
            var previous = Interlocked.Read(ref q.LastSuppressedSequence);
            if (Interlocked.Read(ref q.FirstSuppressedSequence) == 0)
                Interlocked.Exchange(ref q.FirstSuppressedSequence, sequence);
            if (previous == 0 || sequence != previous + 1)
                Interlocked.Increment(ref q.SuppressedRangeCount);
            if (sequence > previous)
                Interlocked.Exchange(ref q.LastSuppressedSequence, sequence);
        }

        private static void UpdateAcceptedGeneration(DeviceQueue q, long generation)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref q.AcceptedGeneration);
                if (generation <= current) return;
            } while (Interlocked.CompareExchange(ref q.AcceptedGeneration, generation, current) != current);
        }

        private static Guid EnsureCorrelation(DeviceQueue q)
        {
            var current = Volatile.Read(ref q.Correlation);
            if (current != null) return current.Value;
            var created = new CorrelationIdentity(Guid.NewGuid());
            current = Interlocked.CompareExchange(ref q.Correlation, created, null);
            return (current ?? created).Value;
        }

        private static Guid GetCorrelation(DeviceQueue q)
            => Volatile.Read(ref q.Correlation)?.Value ?? Guid.Empty;

        private static CorrelationIdentity GetCorrelationIdentity(DeviceQueue q)
            => Volatile.Read(ref q.Correlation);

        private static void SetCorrelation(
            DeviceQueue q,
            Guid correlationId,
            Guid runId = default,
            long runEpoch = 0)
            => Volatile.Write(
                ref q.Correlation,
                correlationId == Guid.Empty
                    ? null
                    : new CorrelationIdentity(correlationId, runId, runEpoch));

        private static double GetOldestAge(DeviceQueue q)
        {
            var queuedAge = q.Queue.TryPeek(out var oldest) ? oldest.AgeMs : 0;
            var inFlight = Interlocked.Read(ref q.InFlightEnqueuedTicks);
            var inFlightAge = inFlight <= 0
                ? 0
                : (Stopwatch.GetTimestamp() - inFlight) * 1000.0 / Stopwatch.Frequency;
            return Math.Max(queuedAge, inFlightAge);
        }

        private static void ClearInFlight(DeviceQueue q)
        {
            Interlocked.Exchange(ref q.InFlightSequence, 0);
            Interlocked.Exchange(ref q.InFlightGeneration, 0);
            Interlocked.Exchange(ref q.InFlightEnqueuedTicks, 0);
            lock (q.WriteStallGate)
                Interlocked.Exchange(ref q.WriteCallStartedTicks, 0);
            // WriteInFlight is the release marker and must be cleared last.
            Interlocked.Exchange(ref q.WriteInFlight, 0);
        }

        private static bool IsDrained(DeviceQueue q)
        {
            return Volatile.Read(ref q.Count) == 0 &&
                   q.Queue.IsEmpty &&
                   Volatile.Read(ref q.WriteInFlight) == 0 &&
                   Volatile.Read(ref q.CurrentBatch) == null;
        }

        private DeviceQueue GetQueue(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) ? _dev1 : _dev2;

        private static string NormalizeDevice(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) ? "Dev1" : "Dev2";

        internal async Task<bool> DisposeAfterDrainAsync(int timeoutMs)
        {
            if (Volatile.Read(ref _resourcesDisposed) != 0) return true;
            Interlocked.Exchange(ref _disposed, 1);
            if (!await DrainAsync(timeoutMs).ConfigureAwait(false)) return false;
            return TryCompleteDispose(Math.Max(1000, timeoutMs));
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _resourcesDisposed) != 0) return;
            var drained = false;
            try
            {
                drained = DisposeAfterDrainAsync(10000).GetAwaiter().GetResult();
            }
            catch
            {
                // The deferred disposer below retains all queues/resources and retries safely.
            }

            if (drained) return;

            // IDisposable cannot report a timeout. Keep the live workers and every accepted batch
            // intact, then finish resource release only after a later safe drain.
            ScheduleDeferredDispose();
        }

        private bool TryCompleteDispose(int workerTimeoutMs)
        {
            lock (_disposeGate)
            {
                if (Volatile.Read(ref _resourcesDisposed) != 0) return true;
                if (Volatile.Read(ref _enqueueInFlight) != 0 ||
                    !IsDrained(_dev1) || !IsDrained(_dev2))
                    return false;

                try { _cts.Cancel(); } catch { }
                try { _dev1.Signal.Release(); } catch { }
                try { _dev2.Signal.Release(); } catch { }

                var workersStopped = false;
                try
                {
                    workersStopped = Task.WaitAll(
                        new[] { _dev1Worker, _dev2Worker, _writeStallWatchdog },
                        Math.Max(1000, workerTimeoutMs));
                }
                catch
                {
                    workersStopped = _dev1Worker.IsCompleted &&
                                     _dev2Worker.IsCompleted &&
                                     _writeStallWatchdog.IsCompleted;
                }
                if (!workersStopped) return false;

                // Never clear a queue as a disposal shortcut. A true drain is the only state in
                // which pooled batches and synchronization primitives may be released.
                if (Volatile.Read(ref _enqueueInFlight) != 0 ||
                    !IsDrained(_dev1) || !IsDrained(_dev2))
                    return false;
                _dev1.Signal.Dispose();
                _dev2.Signal.Dispose();
                _dev1.Slots.Dispose();
                _dev2.Slots.Dispose();
                _cts.Dispose();
                Interlocked.Exchange(ref _resourcesDisposed, 1);
                return true;
            }
        }

        private void SafeWarn(string message, string category)
        {
            try { _log?.Warn(message, category); }
            catch { }
        }

        private void ScheduleDeferredDispose()
        {
            if (Volatile.Read(ref _resourcesDisposed) != 0 ||
                Interlocked.CompareExchange(ref _deferredDisposeScheduled, 1, 0) != 0)
                return;

            Task.Run(async () =>
            {
                try
                {
                    while (Volatile.Read(ref _resourcesDisposed) == 0)
                    {
                        if (await DrainAsync(1000).ConfigureAwait(false) &&
                            TryCompleteDispose(10000))
                            return;
                        await Task.Delay(250).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        _log?.Warn(
                            $"DAQ持久化延迟释放任务异常，将继续重试：{ex.Message}",
                            "落盘");
                    }
                    catch
                    {
                        // Disposal diagnostics must not terminate the retry contract.
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _deferredDisposeScheduled, 0);
                    if (Volatile.Read(ref _disposed) != 0 &&
                        Volatile.Read(ref _resourcesDisposed) == 0)
                        ScheduleDeferredDispose();
                }
            });
        }

        private static class ArrayPoolZeroCache
        {
            internal static double[] Get(int count)
            {
                var buffer = System.Buffers.ArrayPool<double>.Shared.Rent(Math.Max(1, count));
                Array.Clear(buffer, 0, count);
                return buffer;
            }

            internal static void Return(double[] buffer)
                => System.Buffers.ArrayPool<double>.Shared.Return(buffer, clearArray: false);
        }
    }
}
