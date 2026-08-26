using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class RecoveryLifecycleIsolationTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("预警overlay不修改Recovering生命周期身份", WarningOverlayDoesNotMutateLifecycle, ref passed);
            Run("V19学习落盘恢复报警时间线保持原owner", V19TimelineKeepsLearningOwner, ref passed);
            Run("Recovering必须携带当前run结构化owner", ExplicitRecoveryOwnerIsRequired, ref passed);
            Run("预正式孤儿恢复永不选择正式组重入", PreFormalRecoveryNeverSelectsFormalReset, ref passed);
            Run("学习软预警只能返回批次owner不能进入正式重入", LearningWarningCannotRouteToFormalRejoin, ref passed);
            Run("受影响组重入只允许已提交正式阶段", AffectedGroupResetRequiresFormalCommit, ref passed);
            Run("孤儿恢复同Revision去重且新Revision可处理", RecoveryIncidentDedupeUsesStateRevision, ref passed);
            Run("终态撤权使全部旧RunnerTimer许可失效", TerminalFenceInvalidatesStalePermits, ref passed);
            Run("Watchdog拒绝全局计数冒充本通道恢复owner", WatchdogRequiresPerChannelRecoveryOwner, ref passed);
            Run("首发Recovering严格拒绝缺失或错代owner", FirstRecoveryPublicationGateRejectsMalformedOwner, ref passed);
            Run("Recovery合同快照身份与通道集合不可变", RecoveryContractSnapshotIsImmutable, ref passed);
            Run("Recovery lease先Reserve再Bind且终态幂等释放", RecoveryLeaseReserveBindIsTwoPhase, ref passed);
            Run("Recovery registry快照独立于迟到Bind和终态清理", RecoveryRegistrySnapshotIsStable, ref passed);
            Run("Heartbeat只复用稳定不可变聚合快照", HeartbeatAggregateCloneIsStableUnderConcurrency, ref passed);
            return passed;
        }

        private static void WarningOverlayDoesNotMutateLifecycle()
        {
            var runId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var lifecycle = new ChannelRuntimeStateStore();
            var warnings = new ChannelWarningOverlayStore();
            var recovering = lifecycle.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 4,
                State = ChannelRuntimeState.Recovering,
                ReasonCode = "LearningPersistenceSelfHealing",
                CorrelationId = runId,
                RunId = runId,
                RunEpoch = 19,
                Enabled = true,
                RecoveryOwnerKind = RecoveryOwnerKind.BatchLearning,
                RecoveryOwnerId = ownerId,
                RecoveryOwnerGeneration = 19,
                RecoveryTargetPhase = RecoveryTargetPhase.Learning
            });

            warnings.Publish(new ChannelWarningOverlayChangedEvent
            {
                Channel = 4,
                Active = true,
                WarningCode = "RapidLoadRise",
                WarningText = "快速负载软预警",
                RunId = runId,
                RunEpoch = 19
            });

            var after = lifecycle.Get(4);
            Assert(after.Revision == recovering.Revision &&
                   after.State == ChannelRuntimeState.Recovering &&
                   after.ReasonCode == recovering.ReasonCode &&
                   after.CorrelationId == recovering.CorrelationId &&
                   after.RecoveryOwnerId == ownerId &&
                   after.RecoveryTargetPhase == RecoveryTargetPhase.Learning,
                "预警写入改变了生命周期Revision、原因、关联号或恢复owner");
        }

        private static void V19TimelineKeepsLearningOwner()
        {
            var lifecycle = new ChannelRuntimeStateStore();
            var overlays = new ChannelWarningOverlayStore();
            var run = Guid.NewGuid();
            var state = lifecycle.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 10,
                State = ChannelRuntimeState.Recovering,
                ReasonCode = "LearningPersistenceSelfHealing",
                CorrelationId = run,
                RunId = run,
                RunEpoch = 103,
                Enabled = true,
                FormalPhaseCommitted = false,
                RecoveryOwnerKind = RecoveryOwnerKind.BatchLearning,
                RecoveryOwnerId = run,
                RecoveryOwnerGeneration = 103,
                RecoveryTargetPhase = RecoveryTargetPhase.Learning
            });
            overlays.Publish(new ChannelWarningOverlayChangedEvent
            {
                Channel = 10,
                Active = true,
                WarningCode = "RapidLoadRiseBeforeRise",
                CorrelationId = Guid.NewGuid(),
                RunId = run,
                RunEpoch = 103
            });

            var replayed = lifecycle.Get(10);
            Assert(replayed.Revision == state.Revision &&
                   RecoveryOwnershipPolicy.IsOwnerCurrent(replayed) &&
                   replayed.ReasonCode == "LearningPersistenceSelfHealing" &&
                   replayed.RecoveryOwnerKind == RecoveryOwnerKind.BatchLearning,
                "V19现场顺序回放后学习恢复被软预警改成未知/正式恢复");
        }

        private static void ExplicitRecoveryOwnerIsRequired()
        {
            var valid = new ChannelRuntimeStateChangedEvent
            {
                State = ChannelRuntimeState.Recovering,
                RunEpoch = 7,
                RecoveryOwnerKind = RecoveryOwnerKind.DaqRecovery,
                RecoveryOwnerId = Guid.NewGuid(),
                RecoveryOwnerGeneration = 7,
                RecoveryTargetPhase = RecoveryTargetPhase.Formal
            };
            Assert(RecoveryOwnershipPolicy.IsOwnerCurrent(valid),
                "完整结构化恢复owner未被接受");
            valid.RecoveryOwnerGeneration = 6;
            Assert(!RecoveryOwnershipPolicy.IsOwnerCurrent(valid),
                "上一RunEpoch的恢复owner仍被认为当前有效");
            valid.RecoveryOwnerGeneration = 7;
            valid.RecoveryOwnerKind = RecoveryOwnerKind.Unknown;
            Assert(!RecoveryOwnershipPolicy.HasExplicitOwner(valid),
                "Unknown owner仍被当作有效覆盖");
        }

        private static void PreFormalRecoveryNeverSelectsFormalReset()
        {
            Assert(EpbManager.SelectRecoveryInvariantEscalationAction(
                       RecoveryTargetPhase.Learning, false, true, false) ==
                   RecoveryInvariantEscalationAction.ReturnToBatchOwner,
                "学习恢复的短暂审计缺口未归还批次owner");
            Assert(EpbManager.SelectRecoveryInvariantEscalationAction(
                       RecoveryTargetPhase.Startup, false, false, true) ==
                   RecoveryInvariantEscalationAction.FailSafeTerminal,
                "启动硬期限异常错误选择了正式组重入");
            Assert(EpbManager.SelectRecoveryInvariantEscalationAction(
                       RecoveryTargetPhase.Formal, true, false, true) ==
                   RecoveryInvariantEscalationAction.FormalGroupReset,
                "已提交正式阶段未选择正式组清场");
        }

        private static void AffectedGroupResetRequiresFormalCommit()
        {
            Assert(!EpbManager.CanExecuteAffectedGroupReset(RecoveryTargetPhase.Startup, true) &&
                   !EpbManager.CanExecuteAffectedGroupReset(RecoveryTargetPhase.Formal, false) &&
                   EpbManager.CanExecuteAffectedGroupReset(RecoveryTargetPhase.Formal, true),
                "受影响组正式重入阶段门禁不严格");
            Assert(!EpbManager.CanExecuteAffectedGroupReset(
                       RecoveryTargetPhase.Formal, true, false, true) &&
                   !EpbManager.CanExecuteAffectedGroupReset(
                       RecoveryTargetPhase.Formal, true, true, false),
                "旧RunId/RunEpoch或旧owner代次仍能进入正式组重入");
        }

        private static void LearningWarningCannotRouteToFormalRejoin()
        {
            var learning = new ChannelRuntimeStateChangedEvent
            {
                State = ChannelRuntimeState.Learning,
                FormalPhaseCommitted = false,
                RecoveryOwnerKind = RecoveryOwnerKind.BatchLearning,
                RecoveryTargetPhase = RecoveryTargetPhase.Learning
            };
            var formal = new ChannelRuntimeStateChangedEvent
            {
                State = ChannelRuntimeState.Running,
                FormalPhaseCommitted = true,
                RecoveryTargetPhase = RecoveryTargetPhase.Formal
            };
            Assert(!EpbManager.CanRouteRecoverableWarningToFormalRejoin(learning, false),
                "学习阶段软预警仍可进入正式节拍重入");
            Assert(EpbManager.CanRouteRecoverableWarningToFormalRejoin(formal, true),
                "正式运行阶段软预警被错误拒绝");
        }

        private static void RecoveryIncidentDedupeUsesStateRevision()
        {
            var runId = Guid.NewGuid();
            var first = EpbManager.GetRecoveryInvariantIncidentKey(
                runId, 9, 1, 42, RecoveryTargetPhase.Formal);
            Assert(first == EpbManager.GetRecoveryInvariantIncidentKey(
                       runId, 9, 1, 42, RecoveryTargetPhase.Formal),
                "同一恢复事故没有稳定去重键");
            Assert(first != EpbManager.GetRecoveryInvariantIncidentKey(
                       runId, 9, 1, 43, RecoveryTargetPhase.Formal) &&
                   first != EpbManager.GetRecoveryInvariantIncidentKey(
                       runId, 10, 1, 42, RecoveryTargetPhase.Formal) &&
                   first != EpbManager.GetRecoveryInvariantIncidentKey(
                       Guid.NewGuid(), 9, 1, 42, RecoveryTargetPhase.Formal) &&
                   first != EpbManager.GetRecoveryInvariantIncidentKey(
                       runId, 9, 1, 42, RecoveryTargetPhase.Learning),
                "新状态Revision、RunId、RunEpoch或目标阶段仍被旧事故锁存吞掉");
        }

        private static void TerminalFenceInvalidatesStalePermits()
        {
            var fence = new ChannelExecutionFence();
            var permit = fence.Authorize(4, 12);
            Assert(fence.IsCurrent(permit), "新运行许可未生效");
            var staleCopies = Enumerable.Repeat(permit, 1000).ToArray();
            fence.Revoke(4);
            Assert(permit.RevocationToken.IsCancellationRequested,
                "终态撤权未取消进行中的硬件副作用令牌");
            Parallel.ForEach(staleCopies, stale =>
            {
                if (fence.IsCurrent(stale))
                    throw new InvalidOperationException("终态后旧permit仍可创建运行对象");
            });
            Assert(!ChannelExecutionFence.CanCreateRuntime(
                       new ChannelRuntimeStateChangedEvent
                       {
                           State = ChannelRuntimeState.StartBlocked
                       },
                       true,
                       false),
                "StartBlocked仍允许创建Runner/Timer");
            var fresh = fence.Authorize(4, 13);
            Assert(fence.IsCurrent(fresh) && !fence.IsCurrent(permit),
                "新run授权未切换代际或意外复活旧permit");
        }

        private static void WatchdogRequiresPerChannelRecoveryOwner()
        {
            var progress = new WatchdogChannelProgress
            {
                Channel = 4,
                State = "Recovering",
                RecoveryOwned = true,
                RecoveryOwnerKind = "FormalTimer",
                RecoveryOwnerId = Guid.NewGuid().ToString("N"),
                RecoveryOwnerGeneration = 31,
                RecoveryTargetPhase = "Formal"
            };
            Assert(WatchdogRuntimeContractPolicy.HasExplicitRecoveryOwner(progress, 31),
                "Watchdog拒绝了本通道当前代结构化owner");
            progress.RecoveryOwnerId = string.Empty;
            Assert(!WatchdogRuntimeContractPolicy.HasExplicitRecoveryOwner(progress, 31),
                   "仅有RecoveryOwned/全局计数仍冒充本通道owner");
        }

        private static void FirstRecoveryPublicationGateRejectsMalformedOwner()
        {
            var run = Guid.NewGuid();
            var owner = Guid.NewGuid();
            Assert(EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.FormalTimer,
                       RecoveryTargetPhase.Formal, owner, 7),
                "完整的当前Run恢复owner被错误拒绝");
            Assert(!EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.None,
                       RecoveryTargetPhase.Formal, owner, 7) &&
                   !EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.Unknown,
                       RecoveryTargetPhase.Formal, owner, 7) &&
                   !EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.FormalTimer,
                       RecoveryTargetPhase.Formal, Guid.Empty, 7) &&
                   !EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.FormalTimer,
                       RecoveryTargetPhase.None, owner, 7) &&
                   !EpbManager.IsValidFirstRecoveryPublication(
                       run, 7, RecoveryOwnerKind.FormalTimer,
                       RecoveryTargetPhase.Formal, owner, 6),
                "首发Recovering仍接受None/Unknown/空owner/空目标/错代次");
        }

        private static void RecoveryContractSnapshotIsImmutable()
        {
            var ownedChannels = new[] { 11, 12 };
            var safetyAffectedChannels = new[] { 10, 11, 12 };
            var run = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var started = DateTime.UtcNow;
            var contract = new RecoveryContractSnapshot(
                Guid.NewGuid(),
                run,
                3,
                owner,
                RecoveryOwnerKind.FormalTimer,
                RecoveryTargetPhase.Formal,
                "TimerRuntimeSelfHealing",
                started,
                started.AddSeconds(45),
                ownedChannels,
                safetyAffectedChannels);
            ownedChannels[0] = 99;
            safetyAffectedChannels[0] = 99;
            var clone = contract.Clone();
            Assert(contract.Channels.SequenceEqual(new[] { 11, 12 }) &&
                   contract.OwnedChannels.SequenceEqual(new[] { 11, 12 }) &&
                   contract.SafetyAffectedChannels.SequenceEqual(new[] { 10, 11, 12 }) &&
                   clone.OwnedChannels.SequenceEqual(new[] { 11, 12 }) &&
                   clone.SafetyAffectedChannels.SequenceEqual(new[] { 10, 11, 12 }) &&
                   contract.IncidentId == clone.IncidentId &&
                   contract.OwnerId == owner &&
                   contract.RunEpoch == 3,
                "Recovery合同快照暴露了可变身份或通道集合");
        }

        private static void RecoveryLeaseReserveBindIsTwoPhase()
        {
            var registry = new RecoveryTaskRegistry();
            var lease = registry.Reserve(
                "TimerRuntimeSelfHealing",
                11,
                4,
                4,
                10);
            Assert(registry.ActiveCount == 1 &&
                   lease.IsActive &&
                   !lease.IsBound &&
                   !lease.ReservationTask.IsCompleted,
                "Reserve没有先建立未绑定的事故租约");

            var worker = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert(lease.TryBind(worker.Task) && lease.IsBound,
                "Bind没有把唯一wrapper绑定到已存在租约");
            Assert(lease.CompleteAfterTerminal() &&
                   !lease.CompleteAfterTerminal() &&
                   registry.ActiveCount == 0,
                "终态清理不是先发布后幂等释放，或租约仍残留");

            var rejected = registry.Reserve("TimerRuntimeSelfHealing", 11, 4);
            Assert(rejected.CompleteAfterTerminal() &&
                   !rejected.TryBind(Task.CompletedTask) &&
                   registry.ActiveCount == 0,
                "Bind失败后已终止租约仍可重新挂接worker");
        }

        private static void RecoveryRegistrySnapshotIsStable()
        {
            var registry = new RecoveryTaskRegistry();
            var channels = new[] { 10, 4, 10 };
            var lease = registry.Reserve(
                "LegacyPublicationRecovery:PowerStartSelfHealing",
                27,
                channels);
            var reserved = registry.CaptureSnapshot();
            Assert(reserved.Length == 1 &&
                   reserved[0].Id == lease.Id &&
                   reserved[0].RunEpoch == 27 &&
                   reserved[0].Channels.SequenceEqual(new[] { 4, 10 }) &&
                   !reserved[0].IsBound &&
                   !reserved[0].IsTerminal,
                "registry快照未固化Reserve阶段的owner/通道身份");

            Assert(lease.TryBind(Task.CompletedTask), "迟到Bind未绑定唯一wrapper");
            var bound = registry.CaptureSnapshot();
            Assert(bound.Length == 1 && bound[0].IsBound &&
                   bound[0].Channels.SequenceEqual(reserved[0].Channels),
                "Bind后registry快照改变了通道身份或未反映绑定");

            Assert(lease.CompleteAfterTerminal() &&
                   registry.CaptureSnapshot().Length == 0,
                "终态发布后的registry快照仍残留owner/lease");

            var child = registry.Reserve(
                "HydraulicSelfHealingQueuedLeaseAbort",
                27,
                10);
            var childWorker = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert(child.TryBind(childWorker.Task) &&
                   registry.FindBoundWorkerForChannels(27, new[] { 10 }) == null,
                "安全释放子任务被错误冒充为Recovery worker");
            child.CompleteAfterTerminal();

            var body = registry.Reserve(
                "HydraulicSoftwareRecovery",
                27,
                4,
                10);
            var bodyWorker = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Assert(body.TryBind(bodyWorker.Task) &&
                   ReferenceEquals(
                       registry.FindBoundWorkerForChannels(27, new[] { 4, 10 }),
                       bodyWorker.Task),
                "恢复组真实body未被按完整cohort绑定");
            body.CompleteAfterTerminal();
        }

        private static void HeartbeatAggregateCloneIsStableUnderConcurrency()
        {
            var run = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var state = new ChannelRuntimeStateChangedEvent
            {
                Channel = 10,
                State = ChannelRuntimeState.Recovering,
                RunId = run,
                RunEpoch = 42,
                Enabled = true,
                RecoveryOwnerKind = RecoveryOwnerKind.FormalTimer,
                RecoveryTargetPhase = RecoveryTargetPhase.Formal,
                RecoveryOwnerId = owner,
                RecoveryOwnerGeneration = 42,
                CorrelationId = owner
            };
            var aggregate = new WatchdogRecoveryAggregateSnapshot(
                7001,
                DateTime.UtcNow.AddMilliseconds(-25),
                new[] { state },
                new[]
                {
                    new RecoveryContractSnapshot(
                        owner,
                        run,
                        42,
                        owner,
                        RecoveryOwnerKind.FormalTimer,
                        RecoveryTargetPhase.Formal,
                        "TimerRuntimeSelfHealing",
                        DateTime.UtcNow.AddSeconds(-1),
                        DateTime.UtcNow.AddSeconds(44),
                        new[] { 10 })
                },
                new[]
                {
                    new WatchdogRecoveryLeaseSnapshot(
                        9,
                        "TimerRuntimeSelfHealing",
                        42,
                        new[] { 10 },
                        isBound: true,
                        isTerminal: false)
                },
                0,
                "",
                0,
                default,
                default,
                new LogicalQuiescenceSnapshot(),
                new StopSafetyProgressSnapshot(),
                isStable: true);
            var snapshot = new WatchdogRecoverySnapshot
            {
                RunId = run,
                RunEpoch = 42,
                ActiveRecovery = true,
                Aggregate = aggregate
            };

            var failures = 0;
            Parallel.For(0, 100000, _ =>
            {
                var clone = snapshot.CloneForHeartbeat();
                var cloneAggregate = clone.Aggregate;
                if (cloneAggregate == null ||
                    !cloneAggregate.IsStable ||
                    cloneAggregate.Version != 7001 ||
                    cloneAggregate.ChannelStates.Count != 1 ||
                    cloneAggregate.ChannelStates[0].State != ChannelRuntimeState.Recovering ||
                    cloneAggregate.ChannelStates[0].RecoveryOwnerId != owner ||
                    cloneAggregate.RegistryLeases.Count != 1 ||
                    !cloneAggregate.RegistryLeases[0].IsBound)
                    Interlocked.Increment(ref failures);
            });

            Assert(failures == 0,
                "100000次并发heartbeat克隆出现版本回退、Recovering owner/task缺失或不稳定快照");
            Assert(aggregate.Version == 7001 && aggregate.IsStable,
                "并发heartbeat克隆改变了last-known-stable快照身份");
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
