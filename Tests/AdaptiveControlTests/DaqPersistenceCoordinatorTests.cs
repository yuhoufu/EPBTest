using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Controller;
using DataOperation;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class DaqPersistenceCoordinatorTests
    {
        internal static void PauseAndRecoverAfterLowWater()
        {
            var recorder = new BlockingRecorder(150);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 2, 1, 100, 100, 2000, 2);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;
            coordinator.Enqueue(NewBatch("Dev1", 1));
            coordinator.Enqueue(NewBatch("Dev1", 2));
            coordinator.Enqueue(NewBatch("Dev1", 3));
            WaitUntil(() => states.Any(x => x.State == DaqPersistenceState.Paused), 2000,
                "持久化积压未进入安全暂停");
            var paused = states.First(x => x.State == DaqPersistenceState.Paused);
            coordinator.SuppressAfter("Dev1", DateTime.UtcNow.AddSeconds(-1), paused.CorrelationId);
            WaitUntil(() => recorder.WriteCount >= 3, 3000,
                "持久化队列未排空");
            coordinator.Enqueue(NewBatch("Dev1", 4));
            coordinator.Enqueue(NewBatch("Dev1", 5));
            WaitUntil(() => states.Any(x => x.State == DaqPersistenceState.Recovered), 2000,
                "低水位和连续新鲜批次满足后未自动恢复");
        }

        internal static void HardCapacityKeepsRealFaultCode()
        {
            var recorder = new BlockingRecorder(500);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                2, 1, 0, 1000, 100, 2000, 1);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;
            for (var sequence = 1; sequence <= 8; sequence++)
                coordinator.Enqueue(NewBatch("Dev1", sequence));
            WaitUntil(
                () => states.Any(x => x.State == DaqPersistenceState.Failed && x.Code == "DaqPersistenceQueueFull"),
                2000,
                "持久化硬容量故障未使用独立 DaqPersistenceQueueFull 分类");
        }

        internal static void DiagnosticObserverFailureDoesNotRetryWrite()
        {
            var recorder = new BlockingRecorder(0);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8,
                4,
                1,
                1000,
                100,
                2000,
                1,
                persistenceTiming: (_, _, _, _, _, _) =>
                    throw new InvalidOperationException("diagnostic failed"));

            coordinator.Enqueue(NewBatch("Dev1", 1));
            WaitUntil(() => Volatile.Read(ref recorder.WriteCount) >= 1, 2000,
                "诊断观察者异常后数据没有完成写盘");
            Thread.Sleep(300);
            Assert(Volatile.Read(ref recorder.WriteCount) == 1,
                "诊断观察者异常被误判为写盘失败并重复写入");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static DaqDiskBatch NewBatch(string device, long sequence)
        {
            var now = DateTime.UtcNow;
            var timestamps = ArrayPool<DateTime>.Shared.Rent(1);
            var currents = ArrayPool<double>.Shared.Rent(1);
            var pressures = ArrayPool<double>.Shared.Rent(1);
            timestamps[0] = now;
            currents[0] = 1;
            pressures[0] = 10;
            return new DaqDiskBatch(
                device,
                0,
                sequence,
                1,
                timestamps,
                new[] { new DaqDiskChannelBatch(device == "Dev1" ? 4 : 8, currents) },
                device == "Dev1" ? pressures : null,
                device == "Dev2" ? pressures : null,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private static void WaitUntil(Func<bool> predicate, int timeoutMs, string message)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate()) return;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException(message);
        }

        private sealed class BlockingRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder
        {
            private readonly int _delayMs;
            internal BlockingRecorder(int delayMs) { _delayMs = delayMs; }
            internal int WriteCount;

            public void WriteDeviceBatch(DateTime[] timestampsUtc, IReadOnlyList<EpbChannelDiskBatch> channels, int count)
            {
                Thread.Sleep(_delayMs);
                Interlocked.Increment(ref WriteCount);
            }
            public void WriteBatch(int epbId, DateTime[] timestampsUtc, double[] currents, double[] pressures, int count)
                => Thread.Sleep(_delayMs);
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures)
                => Thread.Sleep(_delayMs);
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }
    }
}
