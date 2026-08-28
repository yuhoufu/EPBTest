using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using DataOperation;
using IO.NI;
using MTEmbTest;

namespace AdaptiveControlTests
{
    internal static class RecoveryCoordinationTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("EPB8从Slot506起才进入液压成员快照", FutureSlotEligibilityIsAtomic, ref passed);
            Run("液压超时与DAQ恢复只有一个所有者", HigherRecoveryPreemptsAndWaitsForHydraulic, ref passed);
            Run("双DAQ批次对同一液压组共享引用计数所有权", SameDaqBatchSharesHydraulicOwnership, ref passed);
            Run("同优先级不同电源恢复排队且不得互相取消", SamePriorityOwnersQueueWithoutCancellation, ref passed);
            Run("硬件确认同步取消当前恢复所有者", ConfirmedHardwareCancelsRecoveryOwner, ref passed);
            Run("恢复任务登记覆盖全部受影响通道并在终态清除", RecoveryTaskRegistryTracksAffectedChannels, ref passed);
            Run("恢复任务按RunEpoch/Incident/Owner/通道精确覆盖并刷新进展", RecoveryTaskRegistryExactIdentityAndProgress, ref passed);
            Run("TaskCovered且owner投影暂缺时从不可变契约自修复", CoveredTaskRepairsMissingOwnerProjection, ref passed);
            Run("冗余断电矩阵只在DO成功且电源新鲜低电流时放行", RedundantPowerOffProofMatrixIsFailSafe, ref passed);
            Run("电源应急latch旧incident不得清除新generation", EmergencyPowerLatchRemovalIsExact, ref passed);
            Run("同进程恢复持续进展越过30秒且仅60秒停滞或300秒总限接管", InProcessRecoveryLeaseUsesMaterialProgress, ref passed);
            Run("已完成未终态恢复任务有界返回且绝不热循环", CompletedWorkerWithoutTerminalReturnsImmediately, ref passed);
            Run("恢复任务清退取消令牌可到达内部等待", RecoveryDrainCancellationIsBounded, ref passed);
            Run("x86恢复内存熔断按600与800MiB分级", RecoveryMemoryCircuitBreakerIsDeterministic, ref passed);
            Run("液压硬件确认仅永久禁用故障组所选卡钳", ConfirmedHydraulicDisableIsScoped, ref passed);
            Run("PSU4硬件确认仅永久禁用EPB10/11且EPB9继续", ConfirmedPowerDisableIsScoped, ref passed);
            Run("硬件锁存到达后旧软件恢复不得再次使能", HardwareLatchTerminatesSoftwareRecovery, ref passed);
            Run("硬件确认先OFF和持久禁用再发布诊断", ConfirmedHardwareIsolationOrderIsSafetyFirst, ref passed);
            Run("启动组级硬件隔离后仅健康通道继续", StartupInfrastructureIsolationKeepsHealthyChannels, ref passed);
            Run("恢复阶段忽略取消仍受硬期限约束", IgnoredCancellationCannotHoldRecoveryStage, ref passed);
            Run("DAQ恢复先到必须等待整组截止且重入后才能提交", RecoveryWaitsForCutoffAndRejoin, ref passed);
            Run("迟到旧代清理不得删除新代液压参与状态", LateCleanupCannotTouchNewParticipantVersion, ref passed);
            Run("连续100次恢复故障无所有权和Failure=1残留", HundredFaultsLeaveNoOwnerOrResetLoop, ref passed);
            Run("活动圈上限不触发DAQ任务重建", ActiveCycleLimitIsNotADaqTaskFault, ref passed);
            Run("基础设施恢复前两次局部处理且第3次有界整批回收", InfrastructureRecoveryRemainsLocal, ref passed);
            Run("软件与确定性DAQ故障第3次升级无人值守整批回收", DeterministicGapEscalatesAtThirdAttempt, ref passed);
            Run("final reject永久空洞禁止DAQ同进程恢复提交", PermanentGapBlocksDaqRecoveryCommit, ref passed);
            Run("基础设施恢复次数按RunEpoch隔离", InfrastructureAttemptsAreRunScoped, ref passed);
            Run("旧Run受影响组清场不得阻塞或修改新Run", AffectedGroupResetIsRunScoped, ref passed);
            Run("基础设施重试冻结完整液压组成员", InfrastructureCohortSurvivesRuntimeRemoval, ref passed);
            Run("压力证据陈旧只重启所属液压组DAQ采样", PressureEvidenceRearmIsGroupScoped, ref passed);
            Run("DAQ停止后取消仍必须完成重新启动", CancellationCannotSplitDaqRestart, ref passed);
            Run("双DAQ同一扫描恢复在两台均验证前禁止单边重入", SimultaneousDaqRecoveryUsesBatchBarrier, ref passed);
            Run("进程回收失败按5秒/15秒退避且受RunId与三次预算门禁", ProcessRestartRetryIsBoundedAndRunScoped, ref passed);
            Run("自动恢复保留根RunId且多通道Starting幂等", UnattendedRunChainIdentityIsStable, ref passed);
            Run("并发清场后学习链身份仍回退到冻结RunId", ClearedLearningChainFallsBackToRunId, ref passed);
            Run("恢复RunEpoch不会固定为0且单调", RecoveryRunEpochIsNonZeroAndMonotonic, ref passed);
            Run("基础设施异常重新登记仍沿用首次60秒硬期限", InfrastructureRecoveryDeadlineIsMonotonic, ref passed);
            Run("DAQ重新使能或重入提交异常必须立即安全回滚", DaqRejoinFailureRequiresImmediateRollback, ref passed);
            Run("自动重启子进程必须确认全部授权通道已启动", UnattendedChildStartRequiresCompleteCohort, ref passed);
            Run("自动重启按耐久成功圈续跑且不得重启已完成通道", UnattendedRestartUsesDurableRemainingCycles, ref passed);
            Run("DAQ恢复阶段完整单调序列并在SafeIdle后拒绝回退", DaqRecoveryPhaseSequenceIsMonotonic, ref passed);
            Run("启动熔断只隔离有通道身份的失败卡钳", BatchStartCircuitFailureIsChannelScoped, ref passed);
            return passed;
        }

        private static void BatchStartCircuitFailureIsChannelScoped()
        {
            var selected = new[] { 4, 5, 11, 12 };
            var channelFailure = new SoftwareSelfHealingExhaustedException(
                "StartupPositioning",
                3,
                new InvalidOperationException("Positioning failed"),
                12);
            Assert(EpbManager.ResolveBatchStartFailureChannels(
                       channelFailure,
                       Array.Empty<ChannelStartFault>(),
                       selected).SequenceEqual(new[] { 12 }),
                "EPB12启动定位熔断仍扩大为整批启动受阻");

            var recordedFaults = new[]
            {
                new ChannelStartFault(
                    11,
                    "Learning",
                    "Channel learning failed",
                    FaultScope.Channel)
            };
            Assert(EpbManager.ResolveBatchStartFailureChannels(
                       null,
                       recordedFaults,
                       selected).SequenceEqual(new[] { 11 }),
                "已有通道级启动故障证据仍扩大为整批失败");

            var globalFailure = new SoftwareSelfHealingExhaustedException(
                "DaqStartPreflight",
                3,
                new InvalidOperationException("DAQ unavailable"));
            Assert(EpbManager.ResolveBatchStartFailureChannels(
                       globalFailure,
                       Array.Empty<ChannelStartFault>(),
                       selected).SequenceEqual(selected),
                "无通道身份的DAQ基础设施故障未保持整批作用域");
        }

        private static void DaqRecoveryPhaseSequenceIsMonotonic()
        {
            var outOfOrder = new DaqRecoveryPhaseGate();
            Assert(outOfOrder.TryAdvance(DaqRecoveryPhase.StaleDetected) &&
                   !outOfOrder.TryAdvance(DaqRecoveryPhase.PowerOffConfirmed) &&
                   outOfOrder.Current == DaqRecoveryPhase.StaleDetected,
                "DAQ阶段门错误接受了跳过DO/电源提交的错序发布");

            var gate = new DaqRecoveryPhaseGate();
            var sequence = new[]
            {
                DaqRecoveryPhase.StaleDetected,
                DaqRecoveryPhase.DoOffSubmitted,
                DaqRecoveryPhase.DoOffConfirmed,
                DaqRecoveryPhase.PowerOffSubmitted,
                DaqRecoveryPhase.PowerOffConfirmed,
                DaqRecoveryPhase.CutoffCompleted,
                DaqRecoveryPhase.DaqRestartStarted,
                DaqRecoveryPhase.FirstFreshBatch,
                DaqRecoveryPhase.PressureRevalidated,
                DaqRecoveryPhase.PersistenceBoundaryClosed,
                DaqRecoveryPhase.Validating,
                DaqRecoveryPhase.Rejoining,
                DaqRecoveryPhase.Committed
            };
            foreach (var stage in sequence)
                Assert(gate.TryAdvance(stage) && gate.Current == stage,
                    $"DAQ阶段未按单调序列推进：Expected={stage};Actual={gate.Current}");
            Assert(!gate.MarkSafeIdle(),
                "已提交的恢复不应再次转入SafeIdle");
            Assert(gate.MarkTerminal() && gate.Current == DaqRecoveryPhase.Terminal,
                "Committed后未发布Terminal终态");
            Assert(!gate.TryAdvance(DaqRecoveryPhase.Rejoining),
                "Terminal后仍允许恢复阶段回退");

            var safeIdle = new DaqRecoveryPhaseGate();
            Assert(safeIdle.TryAdvance(DaqRecoveryPhase.StaleDetected) &&
                   safeIdle.MarkSafeIdle() &&
                   safeIdle.Current == DaqRecoveryPhase.SafeIdle,
                "永久失联未进入SafeIdle");
            Assert(!safeIdle.TryAdvance(DaqRecoveryPhase.Validating),
                "SafeIdle后迟到验证仍可回退到Validating");
            Assert(safeIdle.MarkTerminal() &&
                   safeIdle.Current == DaqRecoveryPhase.Terminal,
                "SafeIdle后未进入Terminal");
        }

        private static void RecoveryRunEpochIsNonZeroAndMonotonic()
        {
            Assert(EpbManager.NormalizeRecoveryRunEpoch(0, 0) == 1,
                "恢复RunEpoch在初始值时仍为0");
            Assert(EpbManager.NormalizeRecoveryRunEpoch(4, 2) == 4,
                "恢复RunEpoch未保留更大的已授权代次");
            Assert(EpbManager.NormalizeRecoveryRunEpoch(2, 9) == 9,
                "恢复RunEpoch未保持当前代次单调性");
        }

        private static void ClearedLearningChainFallsBackToRunId()
        {
            var runId = Guid.NewGuid();
            Assert(EpbManager.ResolveLearningChainId(null, runId) == runId,
                "活动学习链被并发清空后未回退到已冻结的执行RunId");

            var rootId = Guid.NewGuid();
            var identity = new RunChainIdentity(runId, rootId, Guid.Empty, 2, 7);
            Assert(EpbManager.ResolveLearningChainId(identity, runId) == rootId,
                "恢复执行未保留不可变的根学习链身份");
        }

        private static void UnattendedRestartUsesDurableRemainingCycles()
        {
            var checkpoint = new Dictionary<string, int>
            {
                ["1"] = 12,
                ["4"] = 0,
                ["6"] = 8
            };
            var durable = new Dictionary<int, int>
            {
                [1] = 11, // DB 已提交，但进程在检查点事件前退出：允许采用更小值
                [4] = 0,  // 已完成通道必须从恢复启动集合移除
                [6] = 8
            };
            var plan = EpbManager.BuildUnattendedRemainingCyclePlan(
                new[] { 1, 4, 6 },
                checkpoint,
                durable);
            Assert(plan.IsValid &&
                   plan.Channels.SequenceEqual(new[] { 1, 6 }) &&
                   plan.RemainingCycles[1] == 11 &&
                   plan.RemainingCycles[4] == 0,
                "耐久进度领先或已完成通道的恢复计划错误");

            durable[6] = 9;
            var regressed = EpbManager.BuildUnattendedRemainingCyclePlan(
                new[] { 1, 4, 6 },
                checkpoint,
                durable);
            Assert(!regressed.IsValid &&
                   regressed.Error.Contains("UnattendedProgressRegression") &&
                   regressed.Error.Contains("EPB=6"),
                "SQLite/XML成功圈数相对检查点倒退时仍允许自动恢复");

            checkpoint.Remove("1");
            var missing = EpbManager.BuildUnattendedRemainingCyclePlan(
                new[] { 1, 4 },
                checkpoint,
                new Dictionary<int, int> { [1] = 0, [4] = 0 });
            Assert(!missing.IsValid &&
                   missing.Error.Contains("UnattendedCheckpointRemainingMissing"),
                "缺失通道检查点进度时仍允许自动恢复");
        }

        private static void UnattendedChildStartRequiresCompleteCohort()
        {
            var runId = Guid.NewGuid();
            var complete = new BatchStartResult(
                runId,
                new[] { 6, 1, 4 },
                Array.Empty<ChannelStartFault>());
            Assert(string.IsNullOrEmpty(EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 1, 4, 6 },
                       complete)),
                "全部授权通道已启动仍被拒绝");

            var completedDuringLearning = new BatchStartResult(
                runId,
                new[] { 1 },
                Array.Empty<ChannelStartFault>(),
                new[] { 4, 6 });
            Assert(string.IsNullOrEmpty(EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 1, 4, 6 },
                       completedDuringLearning)),
                "恢复学习/资格阶段已达到机械目标的通道仍被误判为启动缺失");

            var partial = new BatchStartResult(
                runId,
                new[] { 1, 4 },
                new[]
                {
                    new ChannelStartFault(
                        6,
                        "DaqStartPreflight",
                        "DaqRecoveryFreshnessTimeout",
                        FaultScope.DaqGroup)
                });
            var partialError = EpbManager.ValidateUnattendedBatchStartResult(
                new[] { 1, 4, 6 },
                partial);
            Assert(partialError.Contains("Missing=[6]") &&
                   partialError.Contains("EPB6:DaqStartPreflight"),
                "自动重启子进程错误接受了部分通道启动");

            var hardwarePartial = new BatchStartResult(
                runId,
                new[] { 4, 5, 9 },
                new[]
                {
                    new ChannelStartFault(
                        10,
                        "PowerProtectionHardwareConfirmed",
                        "PSU4 protection",
                        FaultScope.ElectricalGroup),
                    new ChannelStartFault(
                        11,
                        "PowerProtectionHardwareConfirmed",
                        "PSU4 protection",
                        FaultScope.ElectricalGroup)
                });
            Assert(string.IsNullOrEmpty(EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 4, 5, 9, 10, 11 },
                       hardwarePartial)),
                "已由实时硬件证据永久禁用的PSU4成员仍触发整批恢复失败");

            var allHardwareIsolated = new BatchStartResult(
                runId,
                Array.Empty<int>(),
                new[]
                {
                    new ChannelStartFault(
                        9,
                        "HydraulicHardwareConfirmed",
                        "Hydraulic2 failed",
                        FaultScope.HydraulicGroup)
                });
            Assert(EpbManager.IsInfrastructureHardwareOnlyStartResult(allHardwareIsolated) &&
                   string.IsNullOrEmpty(EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 9 },
                       allHardwareIsolated)),
                "全部剩余通道硬件永久隔离后仍会进入无意义进程重启循环");

            var persistenceFailed = new BatchStartResult(
                runId,
                new[] { 4, 5 },
                new[]
                {
                    new ChannelStartFault(
                        9,
                        "InfrastructureHardwareIsolationPersistenceFailed",
                        "Enabled=false atomic replace failed",
                        FaultScope.HydraulicGroup)
                });
            var persistenceError = EpbManager.ValidateUnattendedBatchStartResult(
                new[] { 4, 5, 9 },
                persistenceFailed);
            Assert(EpbManager.HasInfrastructureHardwareIsolationPersistenceFailure(
                       persistenceFailed) &&
                   !EpbManager.IsInfrastructureHardwareOnlyStartResult(persistenceFailed) &&
                   persistenceError.Contains("Missing=[9]") &&
                   persistenceError.Contains("InfrastructureHardwareIsolationPersistenceFailed"),
                "项目禁用未耐久保存时仍被误认为永久隔离并允许无人值守重启");
            Assert(EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 1 },
                       null) == "UnattendedStartResultMissing" &&
                   EpbManager.ValidateUnattendedBatchStartResult(
                       new[] { 1 },
                       new BatchStartResult(
                           Guid.Empty,
                           new[] { 1 },
                           Array.Empty<ChannelStartFault>())) == "UnattendedStartRunIdMissing",
                "无人值守启动缺少结果或RunId时未拒绝");
        }

        private static void DaqRejoinFailureRequiresImmediateRollback()
        {
            Assert(EpbManager.RequiresImmediateDaqRejoinSafetyRollback(
                       "PowerEnableThenMechanicalRelease") &&
                   EpbManager.RequiresImmediateDaqRejoinSafetyRollback(
                       "RejoinAndCommit") &&
                   !EpbManager.RequiresImmediateDaqRejoinSafetyRollback(
                       "PersistenceFreshness") &&
                   !EpbManager.RequiresImmediateDaqRejoinSafetyRollback(
                       "EmergencyPowerOffBarrier"),
                "DAQ重入后异常与仍保持全断能的验证异常分类错误");
        }

        private static void InfrastructureRecoveryDeadlineIsMonotonic()
        {
            const long frequency = 10_000;
            const long started = 50_000;
            Assert(!EpbManager.IsSoftwareRecoveryHardDeadlineElapsed(
                       started,
                       started + 599_999,
                       frequency,
                       hardDeadlineMs: 60_000) &&
                   EpbManager.IsSoftwareRecoveryHardDeadlineElapsed(
                       started,
                       started + 600_000,
                       frequency,
                       hardDeadlineMs: 60_000),
                "60秒硬期限边界计算错误");
            Assert(EpbManager.IsSoftwareRecoveryHardDeadlineElapsed(
                    started,
                    started + 610_000,
                    frequency,
                    hardDeadlineMs: 60_000),
                "异常重登记后错误从新时间起点重新计算硬期限");
        }

        private static void UnattendedRunChainIdentityIsStable()
        {
            var root = Guid.NewGuid().ToString("N");
            var recovered = Guid.NewGuid().ToString("N");
            var continuation = EpbManager.SelectUnattendedRunChainTransition(
                root,
                root,
                string.Empty,
                0,
                armed: true,
                recoveryChainPendingStart: true,
                inProcessRecoveryPending: false,
                incomingRunId: recovered);
            Assert(continuation.RecoveryContinuation &&
                   continuation.ProcessRestartContinuation &&
                   continuation.RecoveryMode == "ProcessRestart" &&
                   !continuation.NewAuthorizationChain &&
                   continuation.RootRunId == root &&
                   continuation.ParentRunId == root &&
                   continuation.CurrentRunId == recovered &&
                   continuation.RestartGeneration == 1,
                "自动恢复新执行覆盖了首次人工授权的根RunId");

            var repeatedStarting = EpbManager.SelectUnattendedRunChainTransition(
                continuation.CurrentRunId,
                continuation.RootRunId,
                continuation.ParentRunId,
                continuation.RestartGeneration,
                armed: true,
                recoveryChainPendingStart: false,
                inProcessRecoveryPending: false,
                incomingRunId: recovered);
            Assert(repeatedStarting.SameRun &&
                   !repeatedStarting.NewAuthorizationChain &&
                   repeatedStarting.RootRunId == root &&
                   repeatedStarting.ParentRunId == root &&
                   repeatedStarting.RestartGeneration == 1,
                "同一批次多个Starting事件重置了恢复链或重启代次");

            var manual = Guid.NewGuid().ToString("N");
            var newAuthorization = EpbManager.SelectUnattendedRunChainTransition(
                recovered,
                root,
                root,
                1,
                armed: true,
                recoveryChainPendingStart: false,
                inProcessRecoveryPending: false,
                incomingRunId: manual);
            Assert(newAuthorization.NewAuthorizationChain &&
                   newAuthorization.RootRunId == manual &&
                   newAuthorization.ParentRunId.Length == 0 &&
                   newAuthorization.RestartGeneration == 0,
                "显式新人工启动没有建立新的授权链和独立重启预算");

            var inProcess = Guid.NewGuid().ToString("N");
            var inProcessContinuation = EpbManager.SelectUnattendedRunChainTransition(
                recovered,
                root,
                root,
                1,
                armed: true,
                recoveryChainPendingStart: false,
                inProcessRecoveryPending: true,
                incomingRunId: inProcess);
            Assert(inProcessContinuation.RecoveryContinuation &&
                   !inProcessContinuation.ProcessRestartContinuation &&
                   inProcessContinuation.RecoveryMode == "InProcess" &&
                   !inProcessContinuation.NewAuthorizationChain &&
                   inProcessContinuation.RootRunId == root &&
                   inProcessContinuation.ParentRunId == recovered &&
                   inProcessContinuation.CurrentRunId == inProcess &&
                   inProcessContinuation.RestartGeneration == 2,
                "同进程自动恢复被误判为新人工授权或丢失恢复链身份");
            Assert(EpbManager.ShouldRestartAfterRecoveryStartupFailure(false) &&
                   !EpbManager.ShouldRestartAfterRecoveryStartupFailure(true),
                "恢复提交后的观察性异常仍会再次回收健康新批次");
            Assert(EpbManager.CanCommitUnattendedRecovery(
                       armed: true,
                       processRecoveryPending: false,
                       inProcessRecoveryPending: true,
                       revocationObserved: false) &&
                   !EpbManager.CanCommitUnattendedRecovery(
                       armed: true,
                       processRecoveryPending: true,
                       inProcessRecoveryPending: false,
                       revocationObserved: true),
                "人工停止的同步撤权屏障仍允许并发自动恢复重新授权");
        }

        private static void SimultaneousDaqRecoveryUsesBatchBarrier()
        {
            RunAsync(async () =>
            {
                var batchCorrelation = Guid.NewGuid();
                var dev1Correlation = Guid.NewGuid();
                var dev2Correlation = Guid.NewGuid();
                var aliases = new Dictionary<Guid, Guid>
                {
                    [dev1Correlation] = batchCorrelation,
                    [dev2Correlation] = batchCorrelation
                };
                Assert(EpbManager.ResolveDaqRecoveryBatchCorrelation(
                           dev1Correlation,
                           aliases) == batchCorrelation &&
                       EpbManager.ResolveDaqRecoveryBatchCorrelation(
                           dev2Correlation,
                           aliases) == batchCorrelation,
                    "先形成的双DAQ独立关联号没有合并到同一批次恢复所有者");
                var now = DateTime.UtcNow;
                var gapTick = Stopwatch.GetTimestamp();
                Assert(EpbManager.ShouldMergeDaqRecoveryEvents(
                           now,
                           now.AddMilliseconds(20),
                           gapTick,
                           gapTick + Stopwatch.Frequency / 100,
                           Stopwatch.Frequency) &&
                       !EpbManager.ShouldMergeDaqRecoveryEvents(
                           now,
                           now.AddSeconds(2),
                           gapTick,
                           gapTick + Stopwatch.Frequency * 2,
                           Stopwatch.Frequency),
                    "双DAQ同步故障批次合并窗口错误地拆分同时事件或合并独立事件");
                var barrier = new DaqRecoveryBatchBarrier(
                    Guid.NewGuid(),
                    17,
                    new[] { "Dev1", "Dev2" });
                using var timeout = new CancellationTokenSource(2000);
                var dev1 = barrier.SignalReadyAndWaitAsync("Dev1", timeout.Token);
                await Task.Delay(20).ConfigureAwait(false);
                Assert(!dev1.IsCompleted, "Dev1在Dev2尚未验证时提前通过共同恢复屏障");
                var dev2 = barrier.SignalReadyAndWaitAsync("Dev2", timeout.Token);
                Assert(await dev1.ConfigureAwait(false) &&
                       await dev2.ConfigureAwait(false),
                    "双DAQ均完成验证后共同恢复屏障仍未释放");
                Assert(!barrier.MarkTerminal("Dev1") &&
                       barrier.MarkTerminal("Dev2"),
                    "双DAQ批次屏障没有等待两台设备分别形成终态");
            });
        }

        private static void SameDaqBatchSharesHydraulicOwnership()
        {
            RunAsync(async () =>
            {
                var coordinator = new HydraulicRecoveryOwnershipCoordinator();
                const string owner = "DAQ_BATCH:shared";
                var dev1 = await coordinator.AcquireAsync(
                    1, owner, RecoveryOwnerPriority.Daq, 1000, CancellationToken.None);
                var dev2 = await coordinator.AcquireAsync(
                    1, owner, RecoveryOwnerPriority.Daq, 1000, CancellationToken.None);
                Assert(coordinator.ActiveCount == 1 &&
                       !dev1.Token.IsCancellationRequested &&
                       !dev2.Token.IsCancellationRequested,
                    "同一双DAQ批次的第二个设备错误抢占并取消第一个设备");
                dev1.Dispose();
                Assert(coordinator.ActiveCount == 1,
                    "共享所有权首个租约释放后提前移除了批次所有者");
                dev2.Dispose();
                Assert(coordinator.ActiveCount == 0,
                    "共享所有权全部租约释放后仍残留所有者");
            });
        }

        private static void SamePriorityOwnersQueueWithoutCancellation()
        {
            RunAsync(async () =>
            {
                var coordinator = new HydraulicRecoveryOwnershipCoordinator();
                var first = await coordinator.AcquireAsync(
                    2,
                    "POWER:3",
                    RecoveryOwnerPriority.PowerSupply,
                    1000,
                    CancellationToken.None);
                var secondTask = coordinator.AcquireAsync(
                    2,
                    "POWER:4",
                    RecoveryOwnerPriority.PowerSupply,
                    1000,
                    CancellationToken.None);
                await Task.Delay(50).ConfigureAwait(false);
                Assert(!first.Token.IsCancellationRequested,
                    "同优先级第二个电源恢复取消了第一个所有者");
                Assert(!secondTask.IsCompleted && coordinator.GetOwner(2) == "POWER:3",
                    "同优先级第二个电源恢复没有等待现有所有者明确退出");

                first.Dispose();
                using (var second = await secondTask.ConfigureAwait(false))
                {
                    Assert(coordinator.GetOwner(2) == "POWER:4" &&
                           !second.Token.IsCancellationRequested,
                        "首个所有者释放后排队恢复未取得所有权");
                }
                Assert(coordinator.ActiveCount == 0, "同优先级排队完成后残留恢复所有者");
            });
        }

        private static void ConfirmedHardwareCancelsRecoveryOwner()
        {
            RunAsync(async () =>
            {
                var coordinator = new HydraulicRecoveryOwnershipCoordinator();
                using (var owner = await coordinator.AcquireAsync(
                           2,
                           "POWER:4",
                           RecoveryOwnerPriority.PowerSupply,
                           1000,
                           CancellationToken.None))
                {
                    Assert(coordinator.CancelGroup(2),
                        "硬件确认未找到当前组恢复所有者");
                    Assert(owner.Token.IsCancellationRequested,
                        "硬件确认未同步取消旧软件恢复令牌");
                }
                Assert(!coordinator.CancelGroup(2) && coordinator.ActiveCount == 0,
                    "所有者退出后仍报告取消成功或残留所有权");
            });
        }

        private static void RecoveryTaskRegistryTracksAffectedChannels()
        {
            var registry = new RecoveryTaskRegistry();
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            registry.Track(
                completion.Task,
                "PowerSupplySoftwareRecovery",
                17,
                10,
                11);
            Assert(registry.HasActiveTaskForChannel(10, 17) &&
                   registry.HasActiveTaskForChannel(11, 17) &&
                   !registry.HasActiveTaskForChannel(9, 17) &&
                   registry.CaptureActiveChannels(17).SequenceEqual(new[] { 10, 11 }),
                "恢复任务没有按完整受影响通道登记");
            completion.TrySetResult(true);
            SpinWait.SpinUntil(() => registry.ActiveCount == 0, 1000);
            Assert(!registry.HasActiveTaskForChannel(10, 17) && registry.ActiveCount == 0,
                "恢复任务终态后通道登记未原子清除");
        }

        private static void RecoveryTaskRegistryExactIdentityAndProgress()
        {
            var registry = new RecoveryTaskRegistry();
            var incidentId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var contract = new RecoveryContractSnapshot(
                incidentId,
                runId,
                31,
                ownerId,
                RecoveryOwnerKind.HydraulicGroupRecovery,
                RecoveryTargetPhase.Formal,
                "FormalHydraulicRecovery",
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(3),
                new[] { 4, 5 });
            var lease = registry.Reserve(contract);
            Assert(registry.TryGetActiveIncidentCoverage(4, 31, incidentId, out var exact) &&
                   exact.RunId == runId && exact.OwnerId == ownerId &&
                   exact.TargetPhase == RecoveryTargetPhase.Formal &&
                   !registry.TryGetActiveIncidentCoverage(4, 31, Guid.NewGuid(), out _) &&
                   !registry.TryGetActiveIncidentCoverage(6, 31, incidentId, out _),
                "恢复任务覆盖仍使用宽松epoch/通道匹配");
            lease.ReportProgress("MechanicalCycle:1");
            Assert(registry.TryGetActiveIncidentCoverage(5, 31, incidentId, out var progressed) &&
                   progressed.ProgressVersion > exact.ProgressVersion &&
                   progressed.ProgressStage == "MechanicalCycle:1" &&
                   progressed.LastProgressUtc >= exact.LastProgressUtc,
                "恢复任务材料进展未刷新续租令牌");
            lease.CompleteAfterTerminal();
        }

        private static void CoveredTaskRepairsMissingOwnerProjection()
        {
            var registry = new RecoveryTaskRegistry();
            var store = new ChannelRuntimeStateStore();
            var incidentId = Guid.NewGuid();
            var runId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var contract = new RecoveryContractSnapshot(
                incidentId,
                runId,
                41,
                ownerId,
                RecoveryOwnerKind.BatchLearning,
                RecoveryTargetPhase.Learning,
                "LearningRecovery",
                DateTime.UtcNow,
                DateTime.UtcNow.AddMinutes(3),
                new[] { 4 });
            var lease = registry.Reserve(contract);
            var missingOwner = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.Recovering,
                RunId = runId,
                RunEpoch = 41,
                CorrelationId = incidentId,
                Enabled = true,
                RecoveryOwnerKind = RecoveryOwnerKind.None,
                RecoveryTargetPhase = RecoveryTargetPhase.None
            });
            Assert(registry.TryGetActiveIncidentCoverage(4, 41, incidentId, out var coverage) &&
                   store.TryRepairRecoveryOwnerIfCurrent(
                       4,
                       missingOwner.Revision,
                       runId,
                       41,
                       incidentId,
                       coverage,
                       out var repaired) &&
                   RecoveryOwnershipPolicy.IsOwnerCurrent(repaired) &&
                   repaired.RecoveryOwnerId == ownerId &&
                   repaired.RecoveryTargetPhase == RecoveryTargetPhase.Learning,
                "有效Task覆盖未能修复短暂缺失的owner投影");
            lease.CompleteAfterTerminal();
        }

        private static void RedundantPowerOffProofMatrixIsFailSafe()
        {
            Assert(EpbManager.IsRedundantOffProofSatisfied(true, true, 0.0, 0.5),
                "DO成功+新鲜0A未确认为断电");
            Assert(!EpbManager.IsRedundantOffProofSatisfied(false, true, 0.0, 0.5) &&
                   !EpbManager.IsRedundantOffProofSatisfied(true, false, 0.0, 0.5) &&
                   !EpbManager.IsRedundantOffProofSatisfied(true, true, 0.501, 0.5) &&
                   !EpbManager.IsRedundantOffProofSatisfied(true, true, double.NaN, 0.5),
                "DO失败、遥测陈旧或高电流被错误证明为断电");
        }

        private static void EmergencyPowerLatchRemovalIsExact()
        {
            var latch = new EmergencyPowerGroupLatch();
            var firstIncident = Guid.NewGuid();
            var first = latch.Register(4, firstIncident, DateTime.UtcNow);
            Assert(!latch.TryRemove(4, Guid.NewGuid(), first.Generation) &&
                   latch.ContainsKey(4) &&
                   latch.TryRemove(4, firstIncident, first.Generation),
                "错误incident可清除活动电源latch，或精确身份无法释放");
            var secondIncident = Guid.NewGuid();
            var second = latch.Register(4, secondIncident, DateTime.UtcNow);
            Assert(second.Generation > first.Generation &&
                   !latch.TryRemove(4, firstIncident, first.Generation) &&
                   latch.ContainsKey(4) &&
                   latch.TryRemove(4, secondIncident, second.Generation),
                "旧incident/generation清除了新事务建立的电源latch");
        }

        private static void InProcessRecoveryLeaseUsesMaterialProgress()
        {
            var started = new DateTime(638000000000000000, DateTimeKind.Utc);
            Assert(string.IsNullOrEmpty(InProcessRecoveryLeasePolicy.SelectHandoffBoundary(
                       started,
                       started.AddSeconds(40),
                       started.AddSeconds(45),
                       60000,
                       300000)),
                "学习持续产生机械圈时仍被固定30秒反杀");
            Assert(InProcessRecoveryLeasePolicy.SelectHandoffBoundary(
                       started,
                       started.AddSeconds(20),
                       started.AddSeconds(80),
                       60000,
                       300000) == "NoMaterialProgress",
                "连续60秒无材料进展未进入安全外部接管");
            Assert(InProcessRecoveryLeasePolicy.SelectHandoffBoundary(
                       started,
                       started.AddSeconds(299),
                       started.AddSeconds(300),
                       60000,
                       300000) == "MaxTotal",
                "恢复总时间达到300秒仍未进入安全外部接管");
        }

        private static void CompletedWorkerWithoutTerminalReturnsImmediately()
        {
            var registry = new RecoveryTaskRegistry();
            var lease = registry.Reserve("TimerRuntimeSelfHealing", 21, 4, 5);
            Assert(lease.TryBind(Task.CompletedTask), "无法绑定已完成恢复worker");
            var started = Stopwatch.StartNew();
            var result = registry.DrainThroughEpochAsync(
                    21,
                    5000,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            started.Stop();
            Assert(!result.Drained && !result.Cancelled &&
                   result.Residues.Count == 1 &&
                   result.Residues[0].Kind ==
                       RecoveryTaskRegistry.DrainResidueKind.WorkerCompletedWithoutTerminal &&
                   started.ElapsedMilliseconds < 500,
                "已完成未终态worker没有立即作为结构化残留返回");
            Assert(lease.CompleteAfterTerminal(), "测试终态租约未释放");
            Assert(registry.DrainThroughEpochAsync(21, 100, CancellationToken.None)
                       .GetAwaiter().GetResult().Drained,
                "终态释放后registry仍有残留");
        }

        private static void RecoveryDrainCancellationIsBounded()
        {
            var registry = new RecoveryTaskRegistry();
            var lease = registry.Reserve("PowerSupplySoftwareRecovery", 22, 11, 12);
            using (var cancellation = new CancellationTokenSource(40))
            {
                var started = Stopwatch.StartNew();
                var result = registry.DrainThroughEpochAsync(
                        22,
                        5000,
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
                started.Stop();
                Assert(!result.Drained && result.Cancelled &&
                       result.Residues.Single().Kind ==
                           RecoveryTaskRegistry.DrainResidueKind.ReservationOnly &&
                       started.ElapsedMilliseconds < 1000,
                    "外层取消没有到达registry reservation等待");
            }
            lease.CompleteAfterTerminal();
        }

        private static void RecoveryMemoryCircuitBreakerIsDeterministic()
        {
            var breaker = new RecoveryMemoryCircuitBreaker();
            var origin = Stopwatch.GetTimestamp();
            var normal = breaker.Evaluate(400L * 1024 * 1024, origin);
            var warning = breaker.Evaluate(
                RecoveryMemoryCircuitBreaker.WarningBytes,
                origin + 15L * Stopwatch.Frequency);
            var rejected = breaker.Evaluate(
                RecoveryMemoryCircuitBreaker.RejectBytes,
                origin + 30L * Stopwatch.Frequency);
            Assert(!normal.Warning && !normal.Reject &&
                   warning.Warning && !warning.Reject &&
                   rejected.Warning && rejected.Reject &&
                   rejected.GrowthBytes30Seconds >= 400L * 1024 * 1024,
                "32位恢复内存分级或30秒增长证据错误");
        }

        private static void ConfirmedHydraulicDisableIsScoped()
        {
            var disabled = EpbManager.SelectConfirmedGroupDisableChannels(
                new[] { 11, 9, 10, 10 },
                channel => channel != 10);
            Assert(disabled.SequenceEqual(new[] { 9, 11 }),
                "液压硬件确认没有仅选择故障事件内仍启用的通道");
            Assert(!disabled.Contains(4) && !disabled.Contains(5),
                "液压2硬件故障牵连了健康液压1的卡钳");
        }

        private static void ConfirmedPowerDisableIsScoped()
        {
            var disabled = EpbManager.SelectConfirmedGroupDisableChannels(
                new[] { 10, 11 },
                _ => true);
            Assert(disabled.SequenceEqual(new[] { 10, 11 }) && !disabled.Contains(9),
                "PSU4硬件确认错误禁用了不属于电气组4的EPB9");
        }

        private static void HardwareLatchTerminatesSoftwareRecovery()
        {
            Assert(EpbManager.CanContinueSoftwareRecovery(
                    new[] { 10, 11 },
                    _ => false,
                    _ => false),
                "无终态锁存的软件恢复被错误终止");
            Assert(!EpbManager.CanContinueSoftwareRecovery(
                    new[] { 10, 11 },
                    channel => channel == 11,
                    _ => false),
                "硬件锁存后旧软件恢复仍可继续");
            Assert(!EpbManager.CanContinueSoftwareRecovery(
                    new[] { 10, 11 },
                    _ => false,
                    channel => channel == 10),
                "报警停机后旧软件恢复仍可继续");
        }

        private static void ConfirmedHardwareIsolationOrderIsSafetyFirst()
        {
            var order = new List<string>();
            EpbManager.ExecuteConfirmedInfrastructureIsolationOrder(
                () => order.Add("latch-freeze-cancel"),
                () => order.Add("off"),
                () => order.Add("power-off"),
                () => order.Add("off-fallback"),
                () => order.Add("persist-disable"),
                () => order.Add("diagnostics"));
            Assert(order.SequenceEqual(new[]
                {
                    "latch-freeze-cancel",
                    "off",
                    "power-off",
                    "off-fallback",
                    "persist-disable",
                    "diagnostics"
                }),
                "硬件确认的日志/UI/报警发布抢在OFF或耐久禁用之前");
        }

        private static void StartupInfrastructureIsolationKeepsHealthyChannels()
        {
            var eligible = EpbManager.SelectEligibleStartChannelsAfterInfrastructureIsolation(
                new[] { 4, 5, 9, 10, 11 },
                new[] { 10, 11 },
                channel => channel != 12);
            Assert(eligible.SequenceEqual(new[] { 4, 5, 9 }),
                "PSU4故障隔离后健康EPB4/5/9未保留在启动集合");
            Assert(EpbManager.IsInfrastructureHardwareStartFailureCode(
                       "HydraulicHardwareConfirmed") &&
                   EpbManager.IsInfrastructureHardwareStartFailureCode(
                       "PowerProtectionHardwareConfirmed") &&
                   !EpbManager.IsInfrastructureHardwareStartFailureCode(
                       "StartupPositioningException"),
                "启动故障组级/通道级分类错误");
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

                Assert(!gate.CompleteCutoff(), "截止辅助方法错误跳过DO/电源确认阶段");
                Assert(!gate.TryAdvance(DaqRecoveryPhase.DoOffConfirmed),
                    "未提交真实DO OFF前错误进入确认阶段");
                Assert(gate.TryAdvance(DaqRecoveryPhase.DoOffSubmitted),
                    "真实DO OFF提交阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.DoOffConfirmed),
                    "DO OFF确认阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.PowerOffSubmitted),
                    "电源OFF提交阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.PowerOffConfirmed),
                    "电源OFF确认阶段未提交");
                Assert(gate.CompleteCutoff(), "未能提交截止完成");
                await recoveredEvent;
                var validationReady = gate.WaitForValidationReadyAsync(CancellationToken.None);
                await Task.Delay(20);
                Assert(!validationReady.IsCompleted, "DAQ重建完成前进入了恢复验证");
                Assert(!gate.TryBeginValidation(), "ValidationReady前进入了验证阶段");
                Assert(gate.TryAdvance(DaqRecoveryPhase.DaqRestartStarted),
                    "DAQ重建开始阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.FirstFreshBatch),
                    "首批新鲜数据阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.PressureRevalidated),
                    "压力复核阶段未提交");
                Assert(gate.TryAdvance(DaqRecoveryPhase.PersistenceBoundaryClosed),
                    "持久化边界阶段未提交");
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
                       2,
                       "IsolatedInfrastructureRecovery",
                       "DaqCallbackStale") &&
                   EpbManager.ShouldPublishUnattendedBatchRecycle(
                       3,
                       "IsolatedInfrastructureRecovery",
                       "DaqCallbackStale"),
                "可恢复DAQ采样故障没有在两次局部尝试后有界升级");

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
