using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    }

    internal sealed class DaqPersistenceCoordinator : IDisposable
    {
        private sealed class DeviceQueue
        {
            public readonly ConcurrentQueue<DaqDiskBatch> Queue = new();
            public int Count;
            public int PauseLatched;
            public int FreshAfterLowWater;
            public long LastPersistedSequence;
            public long AcceptedGeneration;
            public long LastLagLogTicks;
            public long InFlightEnqueuedTicks;
            public int WriteInFlight;
            public int UnresolvedWriteFailure;
            public DateTime? SuppressAfterUtc;
            public Guid CorrelationId;
        }

        private readonly Func<IEpbCycleRecorder> _recorder;
        private readonly IAppLogger _log;
        private readonly Action<DaqTimingRecord> _diagnostic;
        private readonly int _capacity;
        private readonly int _pauseDepth;
        private readonly int _resumeDepth;
        private readonly double _pauseAgeMs;
        private readonly double _resumeAgeMs;
        private readonly int _recoveryTimeoutMs;
        private readonly int _requiredFreshBatches;
        private readonly DeviceQueue _dev1 = new();
        private readonly DeviceQueue _dev2 = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private int _turn;
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
            Action<DaqTimingRecord> diagnostic = null)
        {
            _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _log = log;
            _diagnostic = diagnostic;
            _capacity = Math.Max(2, capacity);
            _pauseDepth = Math.Max(1, Math.Min(_capacity - 1, pauseDepth));
            _resumeDepth = Math.Max(0, Math.Min(_pauseDepth - 1, resumeDepth));
            _pauseAgeMs = Math.Max(100, pauseAgeMs);
            _resumeAgeMs = Math.Max(1, Math.Min(_pauseAgeMs, resumeAgeMs));
            _recoveryTimeoutMs = Math.Max(1000, recoveryTimeoutMs);
            _requiredFreshBatches = Math.Max(1, requiredFreshBatches);
            _worker = Task.Run(WorkerLoop);
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
                batch.Dispose();
                return true;
            }
            if (batch.Generation > acceptedGeneration)
                Interlocked.Exchange(ref q.AcceptedGeneration, batch.Generation);

            var suppressAfter = q.SuppressAfterUtc;
            if (suppressAfter.HasValue && batch.SampleCount > 0 &&
                batch.TimestampsUtc[0].ToUniversalTime() > suppressAfter.Value)
            {
                MarkPersisted(q, batch.Sequence);
                EvaluateRecovery(batch.Device, q, batch);
                batch.Dispose();
                return true;
            }

            var count = Interlocked.Increment(ref q.Count);
            if (count > _capacity)
            {
                Interlocked.Decrement(ref q.Count);
                var correlation = EnsureCorrelation(q);
                Publish(batch, q, DaqPersistenceState.Failed, "BackgroundQueueFull",
                    $"{batch.Device} 持久化队列达到硬上限 {_capacity} 批。", correlation);
                batch.Dispose();
                return false;
            }

            q.Queue.Enqueue(batch);
            _signal.Release();
            EvaluateLag(batch.Device, q, batch);
            return true;
        }

        internal void SuppressAfter(string device, DateTime cutoffUtc, Guid correlationId)
        {
            var q = GetQueue(device);
            q.SuppressAfterUtc = cutoffUtc.ToUniversalTime();
            if (correlationId != Guid.Empty) q.CorrelationId = correlationId;
        }

        internal void ResumeAdmission(string device)
        {
            var q = GetQueue(device);
            q.SuppressAfterUtc = null;
            q.CorrelationId = Guid.Empty;
            Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
            Interlocked.Exchange(ref q.PauseLatched, 0);
        }

        internal void AcceptGeneration(string device, long generation)
        {
            var q = GetQueue(device);
            Interlocked.Exchange(ref q.AcceptedGeneration, generation);
            while (q.Queue.TryPeek(out var batch) && batch.Generation != generation)
            {
                if (!q.Queue.TryDequeue(out batch)) break;
                Interlocked.Decrement(ref q.Count);
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
                    ? DaqPersistenceState.Paused
                    : DaqPersistenceState.Recovered,
                Code = Volatile.Read(ref q.PauseLatched) != 0 ? "DaqPersistenceLag" : "Healthy",
                QueueDepth = Volatile.Read(ref q.Count),
                OldestBatchAgeMs = GetOldestAge(q),
                Generation = Interlocked.Read(ref q.AcceptedGeneration),
                Sequence = Interlocked.Read(ref q.LastPersistedSequence),
                TimestampUtc = DateTime.UtcNow,
                CorrelationId = q.CorrelationId
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
            Dispose();
            return drained;
        }

        private async Task WorkerLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
                    if (!TryTake(out var batch, out var q)) continue;
                    Interlocked.Decrement(ref q.Count);
                    try
                    {
                        if (batch.Generation != Interlocked.Read(ref q.AcceptedGeneration))
                            continue;
                        Interlocked.Exchange(ref q.InFlightEnqueuedTicks, batch.EnqueuedMonotonicTicks);
                        Interlocked.Exchange(ref q.WriteInFlight, 1);
                        await WriteWithRetryAsync(batch, q).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref q.WriteInFlight, 0);
                        Interlocked.Exchange(ref q.InFlightEnqueuedTicks, 0);
                        MarkPersisted(q, batch.Sequence);
                        batch.Dispose();
                    }
                    EvaluateRecovery(batch.Device, q, batch);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log?.Error($"DAQ持久化工作线程异常：{ex.Message}", "落盘", ex);
            }
            finally
            {
                DisposeQueue(_dev1);
                DisposeQueue(_dev2);
            }
        }

        private bool TryTake(out DaqDiskBatch batch, out DeviceQueue q)
        {
            var first = Interlocked.Increment(ref _turn) % 2 == 0 ? _dev1 : _dev2;
            var second = ReferenceEquals(first, _dev1) ? _dev2 : _dev1;
            if (first.Queue.TryDequeue(out batch)) { q = first; return true; }
            if (second.Queue.TryDequeue(out batch)) { q = second; return true; }
            q = null;
            return false;
        }

        private async Task WriteWithRetryAsync(DaqDiskBatch batch, DeviceQueue q)
        {
            var start = Stopwatch.GetTimestamp();
            var delays = new[] { 25, 50, 100, 250 };
            var attempt = 0;
            while (true)
            {
                try
                {
                    var writeStarted = Stopwatch.GetTimestamp();
                    WriteBatch(batch);
                    Interlocked.Exchange(ref q.UnresolvedWriteFailure, 0);
                    _diagnostic?.Invoke(new DaqTimingRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        Device = batch.Device,
                        Kind = "Persistence",
                        Generation = batch.Generation,
                        BatchSize = batch.SampleCount,
                        QueueDepth = Volatile.Read(ref q.Count),
                        PersistenceWaitMs = batch.AgeMs,
                        ProcessingMs = (Stopwatch.GetTimestamp() - writeStarted) * 1000.0 / Stopwatch.Frequency,
                        Detail = $"Sequence={batch.Sequence}; Attempt={attempt}"
                    });
                    return;
                }
                catch (ActiveCycleDataLimitExceededException)
                {
                    var correlation = EnsureCorrelation(q);
                    Publish(batch, q, DaqPersistenceState.Failed, "ActiveCycleDataLimitExceeded",
                        "活动圈数据达到安全上限。", correlation);
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref q.UnresolvedWriteFailure, 1);
                    var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    var correlation = EnsureCorrelation(q);
                    if (Interlocked.CompareExchange(ref q.PauseLatched, 1, 0) == 0)
                        Publish(batch, q, DaqPersistenceState.Paused, "DaqPersistenceLag",
                            $"写盘异常，已进入安全暂停并重试：{ex.Message}", correlation);
                    if (elapsed >= _recoveryTimeoutMs)
                    {
                        Publish(batch, q, DaqPersistenceState.Failed,
                            "DaqPersistenceRecoveryTimeout",
                            $"写盘连续 {_recoveryTimeoutMs}ms 未恢复：{ex.Message}", correlation);
                        return;
                    }
                    var delay = delays[Math.Min(attempt++, delays.Length - 1)];
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
                try
                {
                    if (batch.PressureGroup1 == null || batch.PressureGroup2 == null)
                        zeroPressure = ArrayPoolZeroCache.Get(batch.SampleCount);
                    var channels = batch.Channels
                        .Select(channel => new EpbChannelDiskBatch
                        {
                            EpbId = channel.EpbId,
                            Currents = channel.Currents,
                            Pressures = (channel.EpbId <= 6
                                ? batch.PressureGroup1
                                : batch.PressureGroup2) ?? zeroPressure
                        })
                        .ToArray();
                    batched.WriteDeviceBatch(batch.TimestampsUtc, channels, batch.SampleCount);
                    return;
                }
                finally
                {
                    if (zeroPressure != null) ArrayPoolZeroCache.Return(zeroPressure);
                }
            }
            foreach (var channel in batch.Channels)
            {
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
            if (Volatile.Read(ref q.WriteInFlight) != 0 ||
                Volatile.Read(ref q.UnresolvedWriteFailure) != 0) return;
            var depth = Volatile.Read(ref q.Count);
            var age = GetOldestAge(q);
            if (depth <= _resumeDepth && age <= _resumeAgeMs)
            {
                if (Interlocked.Increment(ref q.FreshAfterLowWater) >= _requiredFreshBatches)
                {
                    Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
                    Interlocked.Exchange(ref q.PauseLatched, 0);
                    Publish(batch, q, DaqPersistenceState.Recovered, "DaqPersistenceRecovered",
                        $"{device} 持久化队列已恢复。Depth={depth}, Oldest={age:F1}ms。", q.CorrelationId);
                }
            }
            else
            {
                Interlocked.Exchange(ref q.FreshAfterLowWater, 0);
            }
        }

        private void Publish(
            DaqDiskBatch batch,
            DeviceQueue q,
            DaqPersistenceState state,
            string code,
            string reason,
            Guid correlationId)
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
                CorrelationId = correlationId
            };
            if (state == DaqPersistenceState.Lagging)
                _log?.Warn(reason, "落盘");
            else if (state == DaqPersistenceState.Failed)
                _log?.Error(reason, "落盘");
            else
                _log?.Warn(reason, "落盘");
            try { StateChanged?.Invoke(update); } catch { }
            _diagnostic?.Invoke(new DaqTimingRecord
            {
                TimestampUtc = update.TimestampUtc,
                Device = update.Device,
                Kind = state == DaqPersistenceState.Failed ? "HardFault" : "Lag",
                Generation = update.Generation,
                QueueDepth = update.QueueDepth,
                QueueAgeMs = update.OldestBatchAgeMs,
                Detail = $"Code={code}; CorrelationId={correlationId:N}; {reason}"
            });
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
            try { _signal.Release(); } catch { }
            try { _worker.Wait(10000); } catch { }
            _signal.Dispose();
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
