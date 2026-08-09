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
        public long Sequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public Guid CorrelationId { get; set; }
        public long SuppressedBatchCount { get; set; }
        public long DiscardedGenerationBatchCount { get; set; }
        public long OverCapacityDroppedBatchCount { get; set; }
        public bool DurabilityBlocked { get; set; }
        public int EpbId { get; set; }
        public int CycleNumber { get; set; }
        public int RecordLimit { get; set; }
    }

    internal sealed class DaqPersistenceCoordinator : IDisposable
    {
        private sealed class DeviceQueue
        {
            public readonly ConcurrentQueue<DaqDiskBatch> Queue = new();
            public readonly SemaphoreSlim Signal = new(0);
            public int Count;
            public int PauseLatched;
            public int FreshAfterLowWater;
            public int PendingFreshWhileWrite;
            public long LastPersistedSequence;
            public long AcceptedGeneration;
            public long LastLagLogTicks;
            public long InFlightEnqueuedTicks;
            public long InFlightSequence;
            public int WriteInFlight;
            public int UnresolvedWriteFailure;
            public int FailureTimedOut;
            public int QueueFullLatched;
            public DateTime? SuppressAfterUtc;
            public Guid CorrelationId;
            public long SuppressedBatchCount;
            public long DiscardedGenerationBatchCount;
            public long OverCapacityDroppedBatchCount;
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
        private int _disposed;

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
            _pauseDepth = Math.Max(1, Math.Min(_capacity - 1, pauseDepth));
            _resumeDepth = Math.Max(0, Math.Min(_pauseDepth - 1, resumeDepth));
            _pauseAgeMs = Math.Max(100, pauseAgeMs);
            _resumeAgeMs = Math.Max(1, Math.Min(_pauseAgeMs, resumeAgeMs));
            _recoveryTimeoutMs = Math.Max(1000, recoveryTimeoutMs);
            _requiredFreshBatches = Math.Max(1, requiredFreshBatches);
            // 两块 DAQ 各自排队、各自写入。一个设备的慢盘/重试不再阻塞另一设备；
            // recorder 内部仍用通道锁和低频 SQLite 事务保证数据一致性。
            _dev1Worker = Task.Run(() => WorkerLoop("Dev1", _dev1));
            _dev2Worker = Task.Run(() => WorkerLoop("Dev2", _dev2));
        }

        internal event Action<DaqPersistenceStateChanged> StateChanged;

        internal bool Enqueue(DaqDiskBatch batch)
        {
            if (batch == null) return false;
            if (Volatile.Read(ref _disposed) != 0)
            {
                batch.Dispose();
                return false;
            }

            var q = GetQueue(batch.Device);
            var acceptedGeneration = Interlocked.Read(ref q.AcceptedGeneration);
            if (batch.Generation < acceptedGeneration)
            {
                Interlocked.Increment(ref q.DiscardedGenerationBatchCount);
                batch.Dispose();
                return true;
            }
            if (batch.Generation > acceptedGeneration)
                Interlocked.Exchange(ref q.AcceptedGeneration, batch.Generation);

            var suppressAfter = q.SuppressAfterUtc;
            if (suppressAfter.HasValue && batch.SampleCount > 0 &&
                batch.TimestampsUtc[0].ToUniversalTime() > suppressAfter.Value)
            {
                Interlocked.Increment(ref q.SuppressedBatchCount);
                MarkPersisted(q, batch.Sequence);
                EvaluateRecovery(batch.Device, q, batch);
                batch.Dispose();
                return true;
            }

            var count = Interlocked.Increment(ref q.Count);
            if (count > _capacity)
            {
                Interlocked.Decrement(ref q.Count);
                Interlocked.Increment(ref q.OverCapacityDroppedBatchCount);
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
                        "已锁存安全暂停，同一拥塞事件后续批次只计数不重复发布。",
                        correlation);
                }
                batch.Dispose();
                return false;
            }

            q.Queue.Enqueue(batch);
            q.Signal.Release();
            EvaluateLag(batch.Device, q, batch);
            return true;
        }

        internal void SuppressAfter(string device, DateTime cutoffUtc, Guid correlationId)
        {
            var q = GetQueue(device);
            q.SuppressAfterUtc = cutoffUtc.ToUniversalTime();
            Interlocked.Exchange(ref q.SuppressedBatchCount, 0);
            Interlocked.Exchange(ref q.DiscardedGenerationBatchCount, 0);
            if (correlationId != Guid.Empty) q.CorrelationId = correlationId;
        }

        internal void ResumeAdmission(string device)
        {
            var q = GetQueue(device);
            q.SuppressAfterUtc = null;
            q.CorrelationId = Guid.Empty;
            Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
            Interlocked.Exchange(ref q.PendingFreshWhileWrite, 0);
            Interlocked.Exchange(ref q.PauseLatched, 0);
            Interlocked.Exchange(ref q.QueueFullLatched, 0);
        }

        internal void AcceptGeneration(string device, long generation)
        {
            var q = GetQueue(device);
            Interlocked.Exchange(ref q.AcceptedGeneration, generation);
            while (q.Queue.TryPeek(out var batch) && batch.Generation != generation)
            {
                if (!q.Queue.TryDequeue(out batch)) break;
                Interlocked.Decrement(ref q.Count);
                Interlocked.Increment(ref q.DiscardedGenerationBatchCount);
                MarkPersisted(q, batch.Sequence);
                batch.Dispose();
            }
        }

        internal DaqPersistenceStateChanged GetSnapshot(string device)
        {
            var q = GetQueue(device);
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
                    ? "DaqPersistenceRecoveryTimeout"
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
                CorrelationId = q.CorrelationId,
                SuppressedBatchCount = Interlocked.Read(ref q.SuppressedBatchCount),
                DiscardedGenerationBatchCount = Interlocked.Read(ref q.DiscardedGenerationBatchCount),
                OverCapacityDroppedBatchCount = Interlocked.Read(ref q.OverCapacityDroppedBatchCount),
                DurabilityBlocked = Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                                    Volatile.Read(ref q.FailureTimedOut) != 0
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
        /// 越过目标序号，且期间不得发生代次切换、准入抑制或未解决写故障。
        /// </summary>
        internal async Task<bool> WaitForDurablePrefixAsync(
            string device,
            long sequence,
            int timeoutMs,
            CancellationToken token)
        {
            if (sequence <= 0) return true;
            var q = GetQueue(device);
            var expectedGeneration = Interlocked.Read(ref q.AcceptedGeneration);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                if (IsDurablePrefix(q, sequence, expectedGeneration)) return true;
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            return IsDurablePrefix(q, sequence, expectedGeneration);
        }

        private static bool IsDurablePrefix(
            DeviceQueue q,
            long sequence,
            long expectedGeneration)
        {
            if (q.SuppressAfterUtc.HasValue ||
                Interlocked.Read(ref q.AcceptedGeneration) != expectedGeneration ||
                Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                Volatile.Read(ref q.FailureTimedOut) != 0 ||
                Volatile.Read(ref q.QueueFullLatched) != 0 ||
                Interlocked.Read(ref q.LastPersistedSequence) < sequence)
                return false;

            var inFlight = Interlocked.Read(ref q.InFlightSequence);
            if (inFlight > 0 && inFlight <= sequence) return false;
            // ConcurrentQueue.TryPeek 与 worker 并发安全。批次若恰好在读取后被归还
            // 对象池，Sequence 可能变为 0；这只会保守地多等一轮，不会提前放行。
            if (q.Queue.TryPeek(out var pending) && pending.Sequence <= sequence) return false;
            return true;
        }

        internal async Task<bool> DrainAsync(int timeoutMs)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000.0 * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                if (Volatile.Read(ref _dev1.Count) == 0 && Volatile.Read(ref _dev2.Count) == 0 &&
                    Volatile.Read(ref _dev1.WriteInFlight) == 0 && Volatile.Read(ref _dev2.WriteInFlight) == 0)
                    return true;
                await Task.Delay(10).ConfigureAwait(false);
            }
            return Volatile.Read(ref _dev1.Count) == 0 && Volatile.Read(ref _dev2.Count) == 0 &&
                   Volatile.Read(ref _dev1.WriteInFlight) == 0 && Volatile.Read(ref _dev2.WriteInFlight) == 0;
        }

        internal async Task<bool> ShutdownAsync(int timeoutMs)
        {
            var drained = await DrainAsync(timeoutMs).ConfigureAwait(false);
            // A timeout is not permission to discard the accepted FIFO prefix. Keep the
            // workers alive so storage recovery can finish the original batches; the caller
            // must remain safely stopped and retry shutdown later.
            if (!drained) return false;
            Dispose();
            return true;
        }

        private async Task WorkerLoop(string device, DeviceQueue q)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await q.Signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                    if (!q.Queue.TryDequeue(out var batch)) continue;
                    Interlocked.Decrement(ref q.Count);
                    var batchDevice = batch.Device;
                    var boundaryHandled = false;
                    try
                    {
                        Interlocked.Exchange(ref q.InFlightSequence, batch.Sequence);
                        if (batch.Generation != Interlocked.Read(ref q.AcceptedGeneration))
                        {
                            Interlocked.Increment(ref q.DiscardedGenerationBatchCount);
                            boundaryHandled = true;
                            continue;
                        }
                        Interlocked.Exchange(ref q.InFlightEnqueuedTicks, batch.EnqueuedMonotonicTicks);
                        Interlocked.Exchange(ref q.WriteInFlight, 1);
                        await WriteWithRetryAsync(batch, q).ConfigureAwait(false);
                        boundaryHandled = true;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref q.WriteInFlight, 0);
                        Interlocked.Exchange(ref q.InFlightEnqueuedTicks, 0);
                        // 只有真实写入成功、活动圈上限已显式报告，或旧代次被明确淘汰时
                        // 才推进处理边界。进程退出/取消发生在失败重试中时不得伪装成已落盘。
                        if (boundaryHandled) MarkPersisted(q, batch.Sequence);
                        Interlocked.Exchange(ref q.InFlightSequence, 0);
                        batch.Dispose();
                    }
                    // batch 已归还数组池，不能再读取其 Device/Generation/Sequence。
                    // 发布恢复状态使用已捕获的设备号和队列的最新持久化快照。
                    EvaluateRecovery(batchDevice, q, null);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Error($"{device} DAQ持久化工作线程异常：{ex.Message}", "落盘", ex);
            }
            finally
            {
                DisposeQueue(q);
            }
        }

        private async Task WriteWithRetryAsync(DaqDiskBatch batch, DeviceQueue q)
        {
            var start = Stopwatch.GetTimestamp();
            var attempt = 0;
            var mappingRecoveryAttempted = false;
            while (true)
            {
                try
                {
                    var writeStarted = Stopwatch.GetTimestamp();
                    WriteBatch(batch);
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
                        _log?.Warn(
                            $"DAQ持久化时序观察者异常已隔离：{ex.Message}",
                            "落盘");
                    }
                    return;
                }
                catch (ActiveCycleDataLimitExceededException ex)
                {
                    var correlation = EnsureCorrelation(q);
                    Publish(batch, q, DaqPersistenceState.Failed, "ActiveCycleDataLimitExceeded",
                        $"活动圈数据达到安全上限。EPB={ex.EpbId} " +
                        $"Cycle={ex.CycleNumber} Limit={ex.Limit}。",
                        correlation,
                        ex.EpbId,
                        ex.CycleNumber,
                        ex.Limit);
                    return;
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
                        _log?.Warn(
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
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private void WriteBatch(DaqDiskBatch batch)
        {
            var recorder = _recorder();
            if (recorder == null) return;
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
                $"{device} 持久化延迟。Depth={depth}, Oldest={age:F1}ms。", q.CorrelationId);
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
                    Publish(batch, q, DaqPersistenceState.Recovered, "DaqPersistenceRecovered",
                        $"{device} 持久化队列已恢复。Depth={depth}, Oldest={age:F1}ms, " +
                        $"OverCapacityDropped={Interlocked.Read(ref q.OverCapacityDroppedBatchCount)}。",
                        q.CorrelationId);
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
            int recordLimit = 0)
        {
            var update = new DaqPersistenceStateChanged
            {
                Device = batch?.Device ?? (ReferenceEquals(q, _dev1) ? "Dev1" : "Dev2"),
                State = state,
                Code = code,
                Reason = reason,
                QueueDepth = Volatile.Read(ref q.Count),
                OldestBatchAgeMs = GetOldestAge(q),
                Generation = batch?.Generation ?? Interlocked.Read(ref q.AcceptedGeneration),
                Sequence = batch?.Sequence ?? Interlocked.Read(ref q.LastPersistedSequence),
                TimestampUtc = DateTime.UtcNow,
                CorrelationId = correlationId,
                SuppressedBatchCount = Interlocked.Read(ref q.SuppressedBatchCount),
                DiscardedGenerationBatchCount = Interlocked.Read(ref q.DiscardedGenerationBatchCount),
                OverCapacityDroppedBatchCount = Interlocked.Read(ref q.OverCapacityDroppedBatchCount),
                DurabilityBlocked = Volatile.Read(ref q.UnresolvedWriteFailure) != 0 ||
                                    Volatile.Read(ref q.FailureTimedOut) != 0,
                EpbId = epbId,
                CycleNumber = cycleNumber,
                RecordLimit = recordLimit
            };
            if (state == DaqPersistenceState.Lagging)
                _log?.Warn(reason, "落盘");
            else if (state == DaqPersistenceState.Failed)
                _log?.Error(reason, "落盘");
            else
                _log?.Warn(reason, "落盘");
            NonCriticalObserver.Invoke(
                StateChanged,
                update,
                ex => _log?.Warn(
                    $"DAQ持久化状态观察者异常已隔离：{ex.Message}",
                    "落盘"));
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
                    Detail = $"Code={code}; CorrelationId={correlationId:N}; {reason}"
                });
            }
            catch (Exception ex)
            {
                _log?.Warn($"DAQ诊断观察者异常已隔离：{ex.Message}", "落盘");
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

        private static Guid EnsureCorrelation(DeviceQueue q)
        {
            if (q.CorrelationId == Guid.Empty) q.CorrelationId = Guid.NewGuid();
            return q.CorrelationId;
        }

        private static double GetOldestAge(DeviceQueue q)
        {
            var queuedAge = q.Queue.TryPeek(out var oldest) ? oldest.AgeMs : 0;
            var inFlight = Interlocked.Read(ref q.InFlightEnqueuedTicks);
            var inFlightAge = inFlight <= 0
                ? 0
                : (Stopwatch.GetTimestamp() - inFlight) * 1000.0 / Stopwatch.Frequency;
            return Math.Max(queuedAge, inFlightAge);
        }

        private DeviceQueue GetQueue(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) ? _dev1 : _dev2;

        private static string NormalizeDevice(string device)
            => string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) ? "Dev1" : "Dev2";

        private static void DisposeQueue(DeviceQueue q)
        {
            while (q.Queue.TryDequeue(out var batch)) batch.Dispose();
            Interlocked.Exchange(ref q.Count, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            try { _dev1.Signal.Release(); } catch { }
            try { _dev2.Signal.Release(); } catch { }
            try { Task.WaitAll(new[] { _dev1Worker, _dev2Worker }, 10000); } catch { }
            _dev1.Signal.Dispose();
            _dev2.Signal.Dispose();
            _cts.Dispose();
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
