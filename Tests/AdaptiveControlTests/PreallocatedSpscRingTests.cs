using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class PreallocatedSpscRingTests
    {
        public static void RunAll()
        {
            CapacityFifoAndWrapAreExact();
            ConcurrentProducerConsumerAndObserverNeverSeeTornValues();
            SteadyStateOperationsDoNotAllocate();
        }

        private static void CapacityFifoAndWrapAreExact()
        {
            var ring = new PreallocatedSpscRing<int>(4);
            for (var value = 1; value <= 4; value++)
                Assert(ring.TryEnqueue(value), "容量内入队失败");
            Assert(!ring.TryEnqueue(5), "预分配环超过硬容量后仍接纳");
            Assert(ring.Count == 4, "满环深度错误");
            Assert(ring.TryPeek(out var head) && head == 1, "满环队头错误");
            Assert(ring.TryDequeue(out var one) && one == 1, "首项出队错误");
            Assert(ring.TryDequeue(out var two) && two == 2, "第二项出队错误");
            Assert(ring.TryEnqueue(5) && ring.TryEnqueue(6), "回绕入队失败");
            for (var expected = 3; expected <= 6; expected++)
                Assert(ring.TryDequeue(out var actual) && actual == expected, "回绕后FIFO顺序错误");
            Assert(ring.Count == 0 && !ring.TryDequeue(out _), "排空后深度或返回值错误");
        }

        private static void ConcurrentProducerConsumerAndObserverNeverSeeTornValues()
        {
            const int total = 100000;
            var ring = new PreallocatedSpscRing<StampedValue>(64);
            using var stop = new CancellationTokenSource();
            using var start = new Barrier(4);
            var failures = new ConcurrentQueue<Exception>();
            var deadline = new ProgressDeadlineState
            {
                StartedTicks = Stopwatch.GetTimestamp(),
                LastProgressTicks = Stopwatch.GetTimestamp()
            };
            var produced = 0;
            var consumed = 0;
            var observed = 0;

            void ReportFailure(Exception exception)
            {
                failures.Enqueue(exception);
                stop.Cancel();
            }

            bool CheckDeadline()
            {
                var now = Stopwatch.GetTimestamp();
                var noProgressMs = (now - Volatile.Read(ref deadline.LastProgressTicks)) *
                                   1000.0 / Stopwatch.Frequency;
                var totalMs = (now - deadline.StartedTicks) *
                              1000.0 / Stopwatch.Frequency;
                if (noProgressMs <= 5000 && totalMs <= 60000)
                    return stop.IsCancellationRequested;

                if (Interlocked.Exchange(ref deadline.TimeoutReported, 1) == 0)
                {
                    ReportFailure(new TimeoutException(
                        $"SPSC并发压力超过进度/总时限：noProgressMs={noProgressMs:F1} totalMs={totalMs:F1} " +
                        $"produced={Volatile.Read(ref produced)} consumed={Volatile.Read(ref consumed)}"));
                }
                return true;
            }

            void TouchProgress()
            {
                Interlocked.Exchange(ref deadline.LastProgressTicks, Stopwatch.GetTimestamp());
            }

            var producer = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(5000);
                    for (var value = 1; value <= total; value++)
                    {
                        var item = new StampedValue(value, ~value);
                        while (!stop.IsCancellationRequested)
                        {
                            if (ring.TryEnqueue(item))
                            {
                                Interlocked.Increment(ref produced);
                                TouchProgress();
                                break;
                            }

                            if (CheckDeadline()) return;
                            Thread.Yield();
                        }
                    }
                }
                catch (Exception exception)
                {
                    ReportFailure(exception);
                }
            }) { IsBackground = true, Name = "SpscProducer" };

            var consumer = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(5000);
                    for (var expected = 1; expected <= total; expected++)
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            if (ring.TryDequeue(out var item))
                            {
                                Assert(item.Value == expected && item.Complement == ~expected,
                                    "并发消费观察到乱序或撕裂值");
                                Interlocked.Increment(ref consumed);
                                TouchProgress();
                                break;
                            }

                            if (CheckDeadline()) return;
                            Thread.Yield();
                        }
                    }
                }
                catch (Exception exception)
                {
                    ReportFailure(exception);
                }
            }) { IsBackground = true, Name = "SpscConsumer" };

            var observer = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait(5000);
                    while (!stop.IsCancellationRequested)
                    {
                        if (ring.TryPeek(out var item))
                        {
                            Assert(item.Value > 0 && item.Value <= total &&
                                   item.Complement == ~item.Value,
                                "看门狗并发窥视观察到撕裂值");
                            Interlocked.Increment(ref observed);
                        }

                        if (Volatile.Read(ref produced) == total &&
                            Volatile.Read(ref consumed) == total)
                            return;
                        if (CheckDeadline()) return;
                        Thread.Yield();
                    }
                }
                catch (Exception exception)
                {
                    ReportFailure(exception);
                }
            }) { IsBackground = true, Name = "SpscObserver" };

            var threads = new[] { producer, consumer, observer };
            foreach (var thread in threads) thread.Start();
            try
            {
                start.SignalAndWait(5000);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }

            var joinDeadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 60.0);
            while (producer.IsAlive || consumer.IsAlive || observer.IsAlive)
            {
                if (CheckDeadline() || Stopwatch.GetTimestamp() >= joinDeadline)
                {
                    stop.Cancel();
                    break;
                }

                Thread.Sleep(1);
            }

            stop.Cancel();
            foreach (var thread in threads)
            {
                if (thread.IsAlive)
                    thread.Join(1000);
            }

            if (producer.IsAlive || consumer.IsAlive || observer.IsAlive)
                failures.Enqueue(new TimeoutException("SPSC并发线程未在有界时间内退出"));
            if (failures.TryPeek(out var firstFailure))
                throw new InvalidOperationException(
                    "SPSC并发压力失败：" + firstFailure.Message,
                    firstFailure);

            Assert(Volatile.Read(ref produced) == total &&
                   Volatile.Read(ref consumed) == total,
                $"SPSC生产/消费数量不完整：produced={produced} consumed={consumed} observed={observed}");
            Assert(ring.Count == 0, "并发压力结束后环未排空");
        }

        private static void SteadyStateOperationsDoNotAllocate()
        {
            var ring = new PreallocatedSpscRing<int>(8);
            for (var i = 0; i < 1000; i++)
            {
                Assert(ring.TryEnqueue(i), "预热入队失败");
                Assert(ring.TryDequeue(out _), "预热出队失败");
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++)
            {
                if (!ring.TryEnqueue(i) || !ring.TryDequeue(out var actual) || actual != i)
                    throw new InvalidOperationException("稳态SPSC顺序错误");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 256, $"稳态SPSC操作产生持续分配：{allocated} bytes");
        }

        private readonly struct StampedValue
        {
            public StampedValue(int value, int complement)
            {
                Value = value;
                Complement = complement;
            }

            public int Value { get; }
            public int Complement { get; }
        }

        private sealed class ProgressDeadlineState
        {
            public long StartedTicks;
            public long LastProgressTicks;
            public int TimeoutReported;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
