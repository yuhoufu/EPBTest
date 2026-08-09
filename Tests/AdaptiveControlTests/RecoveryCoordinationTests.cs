using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using IO.NI;

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
            Run("基础设施恢复永不拖停健康DAQ组", InfrastructureRecoveryRemainsLocal, ref passed);
            Run("确定性处理空洞第3次升级无人值守整批回收", DeterministicGapEscalatesAtThirdAttempt, ref passed);
            Run("final reject永久空洞禁止DAQ同进程恢复提交", PermanentGapBlocksDaqRecoveryCommit, ref passed);
            Run("基础设施恢复次数按RunEpoch隔离", InfrastructureAttemptsAreRunScoped, ref passed);
            Run("旧Run受影响组清场不得阻塞或修改新Run", AffectedGroupResetIsRunScoped, ref passed);
            Run("基础设施重试冻结完整液压组成员", InfrastructureCohortSurvivesRuntimeRemoval, ref passed);
            Run("压力证据陈旧只重启所属液压组DAQ采样", PressureEvidenceRearmIsGroupScoped, ref passed);
            Run("DAQ停止后取消仍必须完成重新启动", CancellationCannotSplitDaqRestart, ref passed);
            Run("进程回收失败按5秒/15秒退避且受RunId与三次预算门禁", ProcessRestartRetryIsBoundedAndRunScoped, ref passed);
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

        private static void ProcessRestartRetryIsBoundedAndRunScoped()
        {
            const string run1 = "11111111111111111111111111111111";
            const string run2 = "22222222222222222222222222222222";
            Assert(EpbManager.SelectUnattendedProcessRestartRetryDelayMs(1) == 5_000,
                "第一次交接失败没有使用5秒快速退避");
            Assert(EpbManager.SelectUnattendedProcessRestartRetryDelayMs(2) == 15_000,
                "第二次交接失败没有使用15秒退避");
            Assert(EpbManager.ShouldRepeatProcessRestartSafetyTeardown(handoffReady: false) &&
                   !EpbManager.ShouldRepeatProcessRestartSafetyTeardown(handoffReady: true),
                "子进程首次创建失败后仍会重复访问已释放的DAQ/持久化对象");
            Assert(EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                    nonceReleased: true,
                    armed: true,
                    checkpointRunId: run1,
                    expectedRunId: run1,
                    attemptsInWindow: 1) &&
                   EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                       nonceReleased: true,
                       armed: true,
                       checkpointRunId: run1,
                       expectedRunId: run1,
                       attemptsInWindow: 2),
                "同一RunId预算内失败未获主动重试资格");
            Assert(!EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                       nonceReleased: false,
                       armed: true,
                       checkpointRunId: run1,
                       expectedRunId: run1,
                       attemptsInWindow: 1) &&
                   !EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                       nonceReleased: true,
                       armed: false,
                       checkpointRunId: run1,
                       expectedRunId: run1,
                       attemptsInWindow: 1) &&
                   !EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                       nonceReleased: true,
                       armed: true,
                       checkpointRunId: run1,
                       expectedRunId: run2,
                       attemptsInWindow: 1) &&
                   !EpbManager.ShouldScheduleUnattendedProcessRestartRetry(
                       nonceReleased: true,
                       armed: true,
                       checkpointRunId: run1,
                       expectedRunId: run1,
                       attemptsInWindow: EpbManager.UnattendedProcessRestartBudget),
                "旧nonce、人工撤权、旧RunId或预算耗尽仍可复活进程回收");
        }

        private static void InfrastructureRecoveryRemainsLocal()
        {
            Assert(EpbManager.ShouldKeepSoftwareRecoveryLocal(
                    "DaqSelfMaintenance",
                    "DaqCallbackStale"),
                "DAQ自维护仍可能升级全局StopAll");
            Assert(!EpbManager.ShouldKeepSoftwareRecoveryLocal(
                     "DaqSelfMaintenance",
                     "BackgroundWorkerFault") &&
                   !EpbManager.ShouldKeepSoftwareRecoveryLocal(
                    "DaqSelfMaintenance",
                    "RawPersistencePermanentFault"),
                "确定性处理空洞仍被伪装成可无限局部重试的瞬时抖动");
            Assert(EpbManager.IsDeterministicProcessingGap("BackgroundWorkerFault") &&
                   EpbManager.IsDeterministicProcessingGap("BackgroundQueueFull") &&
                   EpbManager.IsDeterministicProcessingGap("RawPersistencePermanentFault") &&
                   EpbManager.IsDeterministicProcessingGap("DaqPersistenceWriteStall") &&
                   !EpbManager.IsDeterministicProcessingGap("BackgroundProcessingStale") &&
                   !EpbManager.IsDeterministicProcessingGap("BackgroundBatchTransferFault") &&
                   !EpbManager.IsDeterministicProcessingGap("RawPersistenceTransferFault"),
                "确定性处理空洞与可恢复队列积压分类错误");
            Assert(EpbManager.ShouldKeepSoftwareRecoveryLocal("IsolatedInfrastructureRecovery"),
                "外部基础设施恢复仍可能升级全局StopAll");
            Assert(EpbManager.ShouldKeepSoftwareRecoveryLocal(
                    "IsolatedInfrastructureRecoveryException"),
                "基础设施恢复异常仍可能升级全局StopAll");
            Assert(!EpbManager.ShouldKeepSoftwareRecoveryLocal("TimerRuntime"),
                "非基础设施故障被意外改写为无限局部重试");
        }

        private static void DeterministicGapEscalatesAtThirdAttempt()
        {
            foreach (var code in new[]
                     {
                         "BackgroundQueueFull",
                         "BackgroundWorkerFault",
                         "DaqDataContinuityGap",
                         "RawPersistencePermanentFault"
                     })
            {
                Assert(!EpbManager.ShouldPublishUnattendedBatchRecycle(
                           2,
                           "IsolatedInfrastructureRecovery",
                           code) &&
                       EpbManager.ShouldPublishUnattendedBatchRecycle(
                           3,
                           "IsolatedInfrastructureRecovery",
                           code),
                    $"确定性空洞{code}没有在第3次从局部循环升级");
            }

            Assert(!EpbManager.ShouldPublishUnattendedBatchRecycle(
                       300,
                       "IsolatedInfrastructureRecovery",
                       "DaqCallbackStale"),
                "可恢复DAQ采样抖动被错误升级为进程回收");

            Assert(EpbManager.RequiresImmediateProcessRecycle("DaqPersistenceWriteStall") &&
                   EpbManager.ShouldPublishUnattendedBatchRecycle(
                       1,
                       "DaqSelfMaintenance",
                       "DaqPersistenceWriteStall") &&
                   EpbManager.ShouldEnterSoftwareRecoveryCircuitOpen(
                       1,
                       "DaqSelfMaintenance",
                       "DaqPersistenceWriteStall"),
                "同步持久化写卡死仍需等待三轮局部恢复，无法有界交接进程");

            var runId = Guid.NewGuid();
            var fault = EpbManager.CreateSoftwareRecoveryCircuitFault(
                runId,
                new[] { 4, 5 },
                "Injected deterministic gap",
                Guid.NewGuid());
            Assert(fault.RunId == runId &&
                   fault.RecoveryPolicy == FaultRecoveryPolicy.UnattendedBatchRecycle,
                "确定性空洞升级未发布携带真实RunId的UnattendedBatchRecycle故障");
        }

        private static void PermanentGapBlocksDaqRecoveryCommit()
        {
            Assert(EpbManager.MustBlockDaqRecoveryCommitForContinuityGap(true) &&
                   !EpbManager.MustBlockDaqRecoveryCommitForContinuityGap(false),
                "TryComplete/RejoinAndCommit未把永久序号空洞作为硬门禁");
            Assert(EpbManager.IsDeterministicProcessingGap("DaqDataContinuityGap") &&
                   EpbManager.ShouldPublishUnattendedBatchRecycle(
                       EpbManager.SoftwareRecoveryEscalationAttempts,
                       "DaqRecoveryDataContinuityGap",
                       "DaqDataContinuityGap"),
                "final reject空洞仍会等待普通局部恢复而非立即进程回收");
        }

        private static void InfrastructureAttemptsAreRunScoped()
        {
            var run1Group1 = EpbManager.GetInfrastructureRecoveryAttemptKey(41, 1);
            var run1Group2 = EpbManager.GetInfrastructureRecoveryAttemptKey(41, 2);
            var run2Group1 = EpbManager.GetInfrastructureRecoveryAttemptKey(42, 1);
            Assert(run1Group1 != run1Group2 &&
                   run1Group1 != run2Group1 &&
                   run1Group2 != run2Group1,
                "不同RunEpoch或液压组复用了同一个恢复次数键");

            var scheduled = new ConcurrentDictionary<long, byte>();
            Assert(scheduled.TryAdd(run1Group1, 0) &&
                   scheduled.TryAdd(run2Group1, 0),
                "旧run的同液压组调度锁吞掉了新run恢复任务");
            scheduled.TryRemove(run1Group1, out _);
            Assert(scheduled.ContainsKey(run2Group1),
                "旧run finally错误删除了新run同液压组调度锁");
        }

        private static void AffectedGroupResetIsRunScoped()
        {
            var oldRunId = Guid.NewGuid();
            var newRunId = Guid.NewGuid();
            const long oldEpoch = 90;
            const long newEpoch = 91;
            var oldKey = EpbManager.GetAffectedGroupResetKey(oldEpoch, 1);
            var newKey = EpbManager.GetAffectedGroupResetKey(newEpoch, 1);
            var inProgress = new ConcurrentDictionary<long, byte>();
            var admitted = 0;
            Parallel.Invoke(
                () => { if (inProgress.TryAdd(oldKey, 0)) Interlocked.Increment(ref admitted); },
                () => { if (inProgress.TryAdd(newKey, 0)) Interlocked.Increment(ref admitted); });
            Assert(admitted == 2 && oldKey != newKey,
                "旧Run同液压组清场锁仍吞掉新Run任务");

            Assert(!EpbManager.IsAffectedGroupResetRunCurrent(
                       oldRunId,
                       oldEpoch,
                       newRunId,
                       newEpoch) &&
                   !EpbManager.IsAffectedGroupResetRunCurrent(
                       newRunId,
                       oldEpoch,
                       newRunId,
                       newEpoch) &&
                   EpbManager.IsAffectedGroupResetRunCurrent(
                       newRunId,
                       newEpoch,
                       newRunId,
                       newEpoch),
                "旧RunId/RunEpoch仍可通过组清场副作用门禁");

            inProgress.TryRemove(oldKey, out _);
            Assert(inProgress.ContainsKey(newKey),
                "旧Run finally删除了新Run受影响组清场锁");
        }

        private static void PressureEvidenceRearmIsGroupScoped()
        {
            var selected = EpbManager.SelectPressureEvidenceRearmChannels(
                1,
                Enumerable.Range(1, 12),
                channel => channel == 2 ? string.Empty : (channel <= 6 ? "Dev1" : "Dev2"));
            Assert(selected.SequenceEqual(new[] { 1, 3, 4, 5, 6 }),
                "压力采样再武装越过液压组或包含未映射通道");
        }

        private static void InfrastructureCohortSurvivesRuntimeRemoval()
        {
            var frozen = EpbManager.MergeInfrastructureRecoveryCohort(
                1,
                new[] { 5 },
                new[] { 4, 5 },
                Array.Empty<int>());
            // 模拟首轮失败已删除timer/runner；重试使用首轮冻结数组而非重新从空字典推导。
            var removedRuntimeSource = Array.Empty<int>();
            Assert(frozen.SequenceEqual(new[] { 4, 5 }) &&
                   removedRuntimeSource.Length == 0,
                "首轮清场后兄弟EPB4从重试cohort中丢失");
        }

        private static void CancellationCannotSplitDaqRestart()
        {
            using var cancellation = new CancellationTokenSource();
            var stopped = false;
            var started = false;
            TwoDeviceAiAcquirer.RestartDeviceAtomically(
                () =>
                {
                    stopped = true;
                    cancellation.Cancel();
                },
                () => started = true,
                () => false,
                cancellation.Token);
            Assert(stopped && started,
                "StopDevice内发生取消后未完成StartDevice，DAQ会遗留停止态");
            Assert(cancellation.IsCancellationRequested,
                "故障注入未在Stop→Start临界区产生取消");
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
