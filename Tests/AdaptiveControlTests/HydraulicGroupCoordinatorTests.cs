using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;

namespace AdaptiveControlTests
{
    internal static class HydraulicGroupCoordinatorTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("液压非末成员等待全组低压确认", NonLastMemberWaitsForSafePressure, ref passed);
            Run("液压释放超时产生指定硬故障", ReleaseTimeoutIsExplicit, ref passed);
            Run("报警电源组仅在无兄弟通道活动时关闭", PowerGroupIdlePredicateIsScoped, ref passed);
            return passed;
        }

        private static void NonLastMemberWaitsForSafePressure()
        {
            var pressure = 80.0;
            var releaseCount = 0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                async () =>
                {
                    Interlocked.Increment(ref releaseCount);
                    await Task.Delay(30).ConfigureAwait(false);
                    Volatile.Write(ref pressure, 4.0);
                },
                stableMs: 40,
                timeoutMs: 500);

            coordinator.EnterElectricalPhaseAsync(8, CancellationToken.None).GetAwaiter().GetResult();
            coordinator.EnterElectricalPhaseAsync(9, CancellationToken.None).GetAwaiter().GetResult();

            var first = coordinator.MarkVoltageReleaseAsync(8);
            Thread.Sleep(30);
            Assert(!first.IsCompleted, "非末成员在液压实际释放前已越过屏障。");
            Assert(releaseCount == 0, "尚有成员在途时提前触发了液压释放。");

            var clock = Stopwatch.StartNew();
            var last = coordinator.MarkVoltageReleaseAsync(9);
            Task.WaitAll(first, last);
            Assert(releaseCount == 1, "同一液压轮次应只执行一次释放动作。");
            Assert(clock.ElapsedMilliseconds >= 60, "未等待压力低于阈值并连续稳定。");
        }

        private static void ReleaseTimeoutIsExplicit()
        {
            var coordinator = NewCoordinator(
                () => 42,
                () => Task.CompletedTask,
                stableMs: 20,
                timeoutMs: 80);
            coordinator.EnterElectricalPhaseAsync(10, CancellationToken.None).GetAwaiter().GetResult();

            try
            {
                coordinator.MarkVoltageReleaseAsync(10).GetAwaiter().GetResult();
                throw new InvalidOperationException("压力未下降时未触发液压释放超时。");
            }
            catch (HydraulicReleaseTimeoutException ex)
            {
                Assert(
                    ex.Message.Contains("HydraulicReleaseTimeout"),
                    "液压释放超时未保留稳定故障代码。");
            }
        }

        private static HydraulicGroupCoordinator NewCoordinator(
            Func<double> readPressure,
            Func<Task> release,
            int stableMs,
            int timeoutMs)
        {
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = stableMs,
                ReleaseTimeoutMs = timeoutMs
            };
            hydraulic.Members.Add(8);
            hydraulic.Members.Add(9);
            hydraulic.Members.Add(10);
            config.Hydraulics.Add(hydraulic);
            return new HydraulicGroupCoordinator(
                config,
                _ => readPressure(),
                _ => release(),
                NullLogger.Instance);
        }

        private static void PowerGroupIdlePredicateIsScoped()
        {
            var members = new[] { 10, 11, 12 };
            Assert(
                !EpbManager.IsPowerGroupIdle(
                    members,
                    channel => channel == 11,
                    channel => false),
                "兄弟通道仍参与液压时错误判定为空闲电源组。");
            Assert(
                !EpbManager.IsPowerGroupIdle(
                    members,
                    channel => false,
                    channel => channel == 12),
                "兄弟通道计时器仍运行时错误判定为空闲电源组。");
            Assert(
                EpbManager.IsPowerGroupIdle(
                    members,
                    channel => false,
                    channel => false),
                "组内已无活动通道时未判定为空闲。");
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
