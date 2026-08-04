using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using PressureSample = IO.NI.PressureSample;

namespace AdaptiveControlTests
{
    internal static class HydraulicGroupCoordinatorTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("液压非末成员等待全组低压确认", NonLastMemberWaitsForSafePressure, ref passed);
            Run("液压释放超时产生指定硬故障", ReleaseTimeoutIsExplicit, ref passed);
            Run("液压同代次重复进入不重新登记已释放成员", SameGenerationReentryDoesNotReAddReleasedMember, ref passed);
            Run("液压代次屏障缺员在一个周期内超时", GenerationBarrierTimeoutIsBounded, ref passed);
            Run("液压样本无效或陈旧时资格判定失败", InvalidPressureSamplesAreRejected, ref passed);
            Run("保压持续下降触发液压组故障", SustainedPressureLossFaultsGeneration, ref passed);
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

        private static void SameGenerationReentryDoesNotReAddReleasedMember()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 10,
                timeoutMs: 300,
                barrierTimeoutMs: 300,
                targetBar: 70,
                holdDropConfirmMs: 50);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 42);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();

            var firstRelease = coordinator.MarkVoltageReleaseAsync(lease, 8);
            Thread.Sleep(20);
            Assert(!firstRelease.IsCompleted, "首成员释放后不应越过全组屏障。");

            var sameLease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(ReferenceEquals(lease.Qualification, sameLease.Qualification), "同代次未复用资格结果。");

            var secondRelease = coordinator.MarkVoltageReleaseAsync(sameLease, 11);
            Task.WaitAll(firstRelease, secondRelease);
        }

        private static void GenerationBarrierTimeoutIsBounded()
        {
            var coordinator = NewCoordinator(
                () => 80,
                () => Task.CompletedTask,
                stableMs: 10,
                timeoutMs: 200,
                barrierTimeoutMs: 80);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 7);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            try
            {
                coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
                throw new InvalidOperationException("缺少成员释放时未触发屏障超时。");
            }
            catch (HydraulicBarrierTimeoutException ex)
            {
                Assert(ex.Message.Contains("Pending=[11]"), "屏障超时未记录缺失成员。");
            }
        }

        private static void InvalidPressureSamplesAreRejected()
        {
            var now = Stopwatch.GetTimestamp();
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 0, DateTime.UtcNow, now), 70, 100),
                "0bar 被错误判为压力合格");
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, double.NaN, DateTime.UtcNow, now), 70, 100),
                "NaN 被错误判为压力合格");
            Assert(!HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 80, DateTime.UtcNow,
                        now - Stopwatch.Frequency), 70, 100),
                "陈旧压力样本被错误判为合格");
            Assert(HydraulicController.IsPressureSampleQualified(
                    new PressureSample(2, 80, DateTime.UtcNow, Stopwatch.GetTimestamp()), 70, 100),
                "新鲜达标压力未通过资格判定");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       default(PressureSample), 70, 100) ==
                   HydraulicPressureFailureReason.NoSample,
                "无压力样本未被识别为无样本");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       new PressureSample(2, 80, DateTime.UtcNow,
                           now - Stopwatch.Frequency), 70, 100) ==
                   HydraulicPressureFailureReason.StaleSample,
                "陈旧压力样本未被识别为样本过期");
            Assert(HydraulicController.ClassifyPressureSampleFailure(
                       new PressureSample(2, 60, DateTime.UtcNow,
                           Stopwatch.GetTimestamp()), 70, 100) ==
                   HydraulicPressureFailureReason.BelowMinimum,
                "新鲜低压样本未被识别为压力不足");
        }

        private static void SustainedPressureLossFaultsGeneration()
        {
            var pressure = 80.0;
            var coordinator = NewCoordinator(
                () => Volatile.Read(ref pressure),
                () =>
                {
                    Volatile.Write(ref pressure, 0.0);
                    return Task.CompletedTask;
                },
                stableMs: 10,
                timeoutMs: 300,
                barrierTimeoutMs: 300,
                targetBar: 70,
                holdDropConfirmMs: 50);
            var key = new HydraulicGenerationKey(Guid.NewGuid(), 2, HydraulicPhaseKind.Formal, 99);
            var lease = coordinator.EnterGenerationAsync(key, new[] { 8, 11 }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Volatile.Write(ref pressure, 50.0);
            Thread.Sleep(150);
            try
            {
                coordinator.MarkVoltageReleaseAsync(lease, 8).GetAwaiter().GetResult();
                throw new InvalidOperationException("保压持续下降未触发液压故障。");
            }
            catch (HydraulicPressureLostException)
            {
            }
        }

        private static HydraulicGroupCoordinator NewCoordinator(
            Func<double> readPressure,
            Func<Task> release,
            int stableMs,
            int timeoutMs,
            int barrierTimeoutMs = 0,
            int targetBar = 0,
            int holdDropConfirmMs = 100)
        {
            var config = new TestConfig();
            var hydraulic = new HydraulicItem
            {
                Id = 2,
                Enabled = true,
                ReleaseSafePressureBar = 5,
                ReleaseStableMs = stableMs,
                ReleaseTimeoutMs = timeoutMs,
                BarrierTimeoutMs = barrierTimeoutMs,
                BuildStableMs = 0,
                PressureThresholdBar = targetBar,
                HoldDropConfirmMs = holdDropConfirmMs
            };
            hydraulic.Members.Add(8);
            hydraulic.Members.Add(9);
            hydraulic.Members.Add(10);
            hydraulic.Members.Add(11);
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
