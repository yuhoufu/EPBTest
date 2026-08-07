using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;

namespace AdaptiveControlTests
{
    internal static class RecoveryCoordinationTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("EPB8从Slot506起才进入液压成员快照", FutureSlotEligibilityIsAtomic, ref passed);
            Run("液压超时与DAQ恢复只有一个所有者", HigherRecoveryPreemptsAndWaitsForHydraulic, ref passed);
            Run("恢复阶段忽略取消仍受硬期限约束", IgnoredCancellationCannotHoldRecoveryStage, ref passed);
            Run("DAQ恢复先到必须等待整组截止且重入后才能提交", RecoveryWaitsForCutoffAndRejoin, ref passed);
            Run("迟到旧代清理不得删除新代液压参与状态", LateCleanupCannotTouchNewParticipantVersion, ref passed);
            Run("连续100次恢复故障无所有权和Failure=1残留", HundredFaultsLeaveNoOwnerOrResetLoop, ref passed);
            Run("活动圈上限不触发DAQ任务重建", ActiveCycleLimitIsNotADaqTaskFault, ref passed);
            return passed;
        }

        private static void FutureSlotEligibilityIsAtomic()
        {
            var participants = new[] { 7, 8, 9 };
            var firstSlots = new Dictionary<int, long> { [8] = 506 };
            var slot505 = FormalSlotEligibility.Filter(participants, firstSlots, 505);
            var slot506 = FormalSlotEligibility.Filter(participants, firstSlots, 506);

            Assert(!slot505.Contains(8), "EPB8提前污染Slot505成员快照");
            Assert(slot506.SequenceEqual(new[] { 7, 8, 9 }),
                "EPB8未从Slot506原子加入成员快照");
        }

        private static void HigherRecoveryPreemptsAndWaitsForHydraulic()
        {
            RunAsync(async () =>
            {
                var coordinator = new HydraulicRecoveryOwnershipCoordinator();
                var hydraulic = await coordinator.AcquireAsync(
                    2, "HYDRAULIC:2", RecoveryOwnerPriority.Hydraulic, 1000, CancellationToken.None);
                var oldOwnerExited = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var hydraulicTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, hydraulic.Token);
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        hydraulic.Dispose();
                        oldOwnerExited.TrySetResult(true);
                    }
                });

                var clock = Stopwatch.StartNew();
                using (var daq = await coordinator.AcquireAsync(
                           2, "DAQ:Dev2", RecoveryOwnerPriority.Daq, 2000, CancellationToken.None))
                {
                    Assert(oldOwnerExited.Task.IsCompleted,
                        "DAQ取得所有权前未等待液压恢复明确退出");
                    Assert(coordinator.ActiveCount == 1,
                        "同一液压组出现多个恢复所有者");
                    Assert(coordinator.GetOwner(2) == "DAQ:Dev2",
                        "更高层DAQ恢复没有成为唯一所有者");
                }
                await hydraulicTask;
                Assert(clock.ElapsedMilliseconds < 2000,
                    "所有权移交超过有界等待时间");
                Assert(coordinator.ActiveCount == 0, "恢复完成后仍遗留所有者");
            });
        }

        private static void IgnoredCancellationCannotHoldRecoveryStage()
        {
            RunAsync(async () =>
            {
                var never = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var clock = Stopwatch.StartNew();
                try
                {
                    await RecoveryStageDeadline.RunAsync(
                        "HungMechanicalRelease",
                        50,
                        _ => never.Task,
                        CancellationToken.None);
                    throw new InvalidOperationException("忽略取消的恢复阶段未触发硬超时");
                }
                catch (RecoveryStageTimeoutException ex)
                {
                    Assert(ex.Stage == "HungMechanicalRelease", "超时阶段标识丢失");
                    Assert(clock.ElapsedMilliseconds < 1000,
                        "恢复阶段仍可能无限占用Completing");
                }
            });
        }

        private static void RecoveryWaitsForCutoffAndRejoin()
        {
            RunAsync(async () =>
            {
                var gate = new DaqRecoveryPhaseGate();
                Assert(gate.BeginCutoff(), "未能进入截止阶段");
                var recoveredEvent = gate.WaitForCutoffAsync(CancellationToken.None);
                await Task.Delay(20);
                Assert(!recoveredEvent.IsCompleted, "恢复事件越过了整组截止屏障");
                Assert(!gate.TryBeginValidation(), "截止完成前进入了验证阶段");

                Assert(gate.CompleteCutoff(), "未能提交截止完成");
                await recoveredEvent;
                var validationReady = gate.WaitForValidationReadyAsync(CancellationToken.None);
                await Task.Delay(20);
                Assert(!validationReady.IsCompleted, "DAQ重建完成前进入了恢复验证");
                Assert(!gate.TryBeginValidation(), "ValidationReady前进入了验证阶段");
                Assert(gate.EnableValidation(), "未能开放恢复验证");
                await validationReady;
                Assert(gate.TryBeginValidation(), "截止完成后未能进入验证");
                Assert(!gate.TryCommit(), "共同重入前错误提交了恢复成功");
                Assert(gate.TryBeginRejoin(), "未能进入共同重入阶段");
                Assert(!gate.TryCommit(), "共同重入完成前错误提交了恢复成功");
                Assert(gate.CompleteRejoin(), "未能提交共同重入完成");
                Assert(gate.TryCommit(), "共同重入完成后未能提交恢复成功");
            });
        }

        private static void LateCleanupCannotTouchNewParticipantVersion()
        {
            Assert(
                RecoveryEpochGuard.CanApplyParticipantCleanup(
                    participantExists: true,
                    expectedVersion: 41,
                    currentVersion: 41),
                "同代截止清理被错误拒绝");
            Assert(
                !RecoveryEpochGuard.CanApplyParticipantCleanup(
                    participantExists: true,
                    expectedVersion: 41,
                    currentVersion: 42),
                "迟到的旧代清理仍可删除新代 participant");
            Assert(
                RecoveryEpochGuard.CanApplyParticipantCleanup(
                    participantExists: false,
                    expectedVersion: 0,
                    currentVersion: 0),
                "不存在 participant 时清理未保持幂等");
        }

        private static void HundredFaultsLeaveNoOwnerOrResetLoop()
        {
            RunAsync(async () =>
            {
                var coordinator = new HydraulicRecoveryOwnershipCoordinator();
                var failures = new RecoveryFailureBackoffState();
                for (var injection = 1; injection <= 100; injection++)
                {
                    using (await coordinator.AcquireAsync(
                               2,
                               "INJECTION:" + injection,
                               RecoveryOwnerPriority.Daq,
                               1000,
                               CancellationToken.None))
                    {
                        Assert(failures.RecordFailure() == injection,
                            "连续失败计数在恢复完全提交前被错误清零");
                    }
                    Assert(coordinator.ActiveCount == 0,
                        "故障注入后仍有活动恢复所有者，需人工Stop才能清理");
                }

                Assert(failures.Current == 100,
                    "失败计数反复停留在Failure=1");
                failures.CommitSuccess();
                Assert(failures.Current == 0,
                    "完整恢复提交后失败计数未清零");
                Assert(EpbManager.RecoveryGroupHardDeadlineMs == 60_000,
                    "受影响组自动清场硬期限不是60秒");
            });
        }

        private static void ActiveCycleLimitIsNotADaqTaskFault()
        {
            Assert(!EpbManager.RequiresDaqTaskRecreate("ActiveCycleDataLimitExceeded"),
                "活动圈生命周期故障仍会重建健康DAQ任务");
        }

        private static void RunAsync(Func<Task> action)
        {
            action().GetAwaiter().GetResult();
        }

        private static void Run(string name, Action action, ref int passed)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
