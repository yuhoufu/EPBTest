using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog.Protocol;
using Timing;

namespace AdaptiveControlTests
{
    internal static class FormalBatchSlotCoordinatorTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("全局正式槽等待全部参与者并滚动未来边界",
                WaitsForAllParticipantsAndFutureBoundary, ref passed);
            Run("永久隔离成员可从活动槽安全退休",
                RetiredParticipantDoesNotBlockBarrier, ref passed);
            Run("墙钟边界计算禁止补跑历史槽",
                FutureBoundaryNeverCatchesUp, ref passed);
            Run("迟到进入下一槽不会被二次顺延",
                LateNextSlotEntryDoesNotAddAnotherPeriod, ref passed);
            Run("周期超限策略区分普通连续与硬截止",
                PeriodOverrunPolicyClassifiesBoundaries, ref passed);
            Run("屏障等待是合法状态且仍要求正式运行资源",
                WaitingStateHasLegalHealthContracts, ref passed);
            return passed;
        }

        private static void WaitsForAllParticipantsAndFutureBoundary()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-20);
            const int periodMs = 120;
            var slot0A = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1, 2 }, anchor, periodMs, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var slot0B = coordinator.EnterAsync(
                    runId, 0, 2, new[] { 1, 2 }, anchor, periodMs, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            var waitingSignals = 0;
            var stopwatch = Stopwatch.StartNew();
            var nextA = coordinator.EnterAsync(
                runId, 1, 1, new[] { 1, 2 }, anchor, periodMs,
                () => Interlocked.Increment(ref waitingSignals), CancellationToken.None);
            var nextB = coordinator.EnterAsync(
                runId, 1, 2, new[] { 1, 2 }, anchor, periodMs,
                () => Interlocked.Increment(ref waitingSignals), CancellationToken.None);

            slot0A.Complete(SafeTerminal(1));
            Thread.Sleep(20);
            Assert(!nextA.IsCompleted && !nextB.IsCompleted,
                "仅一个参与者完成时错误释放了下一槽");
            slot0B.Complete(SafeTerminal(2));

            var slot1A = nextA.GetAwaiter().GetResult();
            var slot1B = nextB.GetAwaiter().GetResult();
            Assert(waitingSignals > 0, "等待上一槽时未发布合法等待状态");
            Assert(stopwatch.ElapsedMilliseconds >= 45,
                "上一槽完成后没有滚动到首个未来墙钟边界");
            slot1A.Complete(SafeTerminal(1));
            slot1B.Complete(SafeTerminal(2));
            coordinator.ClearRun(runId);
        }

        private static void RetiredParticipantDoesNotBlockBarrier()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var first = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1, 2 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            first.Complete(SafeTerminal(1));
            coordinator.RetireParticipant(runId, 2, "PermanentAlarm");

            var next = coordinator.EnterAsync(
                    runId, 1, 1, new[] { 1 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            next.Complete(SafeTerminal(1));
            coordinator.ClearRun(runId);
        }

        private static void FutureBoundaryNeverCatchesUp()
        {
            var anchor = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
            var after = anchor.AddMilliseconds(450);
            var boundary = FormalBatchSlotCoordinator.CalculateFirstFutureBoundary(
                anchor,
                after,
                100);
            Assert(boundary == anchor.AddMilliseconds(500) && boundary > after,
                "边界计算返回了当前或历史槽，可能补跑");
        }

        private static void PeriodOverrunPolicyClassifiesBoundaries()
        {
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(15000, 15000, 7) ==
                   Controller.Adaptive.PeriodOverrunKind.None,
                "等于周期被误判为超限");
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(16000, 15000, 7) ==
                   Controller.Adaptive.PeriodOverrunKind.CompletedOverrun,
                "第7次普通超限被误隔离");
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(16000, 15000, 8) ==
                   Controller.Adaptive.PeriodOverrunKind.ConsecutiveLimitReached,
                "第8次连续超限没有进入永久报警");
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(30000, 15000, 1) ==
                   Controller.Adaptive.PeriodOverrunKind.HardLimitReached,
                "2倍周期没有进入硬截止永久报警");
        }

        private static void LateNextSlotEntryDoesNotAddAnotherPeriod()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            const int periodMs = 60;
            var first = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1 }, anchor, periodMs, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            first.Complete(SafeTerminal(1));

            // 模拟高精度定时器已经等待到协调器固定的首个未来边界后才进入。
            Thread.Sleep(90);
            var stopwatch = Stopwatch.StartNew();
            var next = coordinator.EnterAsync(
                    runId, 1, 1, new[] { 1 }, anchor, periodMs, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(stopwatch.ElapsedMilliseconds < 35,
                "下一槽迟到进入后又被额外顺延了一个完整周期");
            next.Complete(SafeTerminal(1));
            coordinator.ClearRun(runId);
        }

        private static void WaitingStateHasLegalHealthContracts()
        {
            var contract = WatchdogRuntimeContractPolicy.Resolve(
                ChannelRuntimeState.WaitingForSlotBarrier.ToString());
            Assert(contract.TimerRequired && contract.RunnerRequired &&
                   !contract.MechanicalProgressExpected &&
                   !contract.ResourcesMustBeInactive,
                "Watchdog没有把屏障等待识别为需要Timer/Runner的合法非机械阶段");

            var decision = EpbManager.EvaluateTimerRuntimeHealth(
                ChannelRuntimeState.WaitingForSlotBarrier,
                new HighPrecisionTimer(15000, OverrunPolicy.AlignToWallClock),
                DateTime.UtcNow.AddMinutes(1),
                1000);
            Assert(decision.Action == TimerRuntimeHealthAction.None,
                "Timer健康检查把合法屏障等待误判为Timer丢失或陈旧");
        }

        private static FormalBatchParticipantTerminal SafeTerminal(int channel) =>
            new FormalBatchParticipantTerminal
            {
                Channel = channel,
                MotorOffConfirmed = true,
                HydraulicMemberReleased = true,
                ControlSucceeded = true,
                PersistenceBoundaryRequired = true,
                PersistenceCommitted = true,
                MechanicalCycleCompleted = true,
                Result = "Completed",
                CompletedUtc = DateTime.UtcNow
            };

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
