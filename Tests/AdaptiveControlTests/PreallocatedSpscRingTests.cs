using System;
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
            var producer = Task.Run(() =>
            {
                for (var value = 1; value <= total; value++)
                {
                    var item = new StampedValue(value, ~value);
                    while (!ring.TryEnqueue(item)) Thread.Yield();
                }
            });
            var consumer = Task.Run(() =>
            {
                for (var expected = 1; expected <= total; expected++)
                {
                    StampedValue item;
                    while (!ring.TryDequeue(out item)) Thread.Yield();
                    Assert(item.Value == expected && item.Complement == ~expected,
                        "并发消费观察到乱序或撕裂值");
                }
            });
            var observer = Task.Run(() =>
            {
                while (!producer.IsCompleted || !consumer.IsCompleted)
                {
                    if (ring.TryPeek(out var item))
                        Assert(item.Value > 0 && item.Complement == ~item.Value,
                            "看门狗并发窥视观察到撕裂值");
                    Thread.Yield();
                }
            });
            Assert(Task.WaitAll(new[] { producer, consumer, observer }, 10000),
                "SPSC并发压力测试超时");
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

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
