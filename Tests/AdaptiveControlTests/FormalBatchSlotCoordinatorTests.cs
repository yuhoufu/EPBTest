using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Config;
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
            Run("退休请求在安全栅栏确认前不释放槽位",
                RetiredParticipantDoesNotBlockBarrier, ref passed);
            Run("退休安全证据失败必须阻断槽位",
                UnsafeRetirementFailsClosed, ref passed);
            Run("SafeAborted可靠封圈释放槽位且不计机械完成",
                SafeAbortedReleasesSlot, ref passed);
            Run("耐久作废映射SafeAborted且半提交保持SafetyUnproven",
                DurableAbortDispositionIsExplicit, ref passed);
            Run("SafetyUnproven形式终态必须阻断后续槽位",
                SafetyUnprovenBlocksSlot, ref passed);
            Run("正式槽先等待并行安全闭合再提交终态",
                PendingSafetyClosureWaitsForRealEvidence, ref passed);
            Run("重复OFF不得覆盖首次物理成功证据",
                RepeatedOffPreservesFirstSuccess, ref passed);
            Run("墙钟边界计算禁止补跑历史槽",
                FutureBoundaryNeverCatchesUp, ref passed);
            Run("迟到进入下一槽不会被二次顺延",
                LateNextSlotEntryDoesNotAddAnotherPeriod, ref passed);
            Run("等待上一槽时取消会撤销尚未获准的占位",
                CancellationBeforeAdmissionWithdrawsParticipant, ref passed);
            Run("同一通道同一槽只允许一次准入",
                DuplicateParticipantAdmissionIsRejected, ref passed);
            Run("已确认退休事实关闭迟到创建的旧成员槽",
                ConfirmedRetirementClosesLateStaleSlot, ref passed);
            Run("旧代退休确认不能污染正式重入的新参与者",
                LateRetirementCannotPoisonNewParticipantGeneration, ref passed);
            Run("恢复重入与正常通道共同闭合槽55并释放槽56",
                RejoinedAndNormalParticipantsCloseSharedSlot, ref passed);
            Run("正式槽终态缺失按超时快照失败关闭",
                MissingTerminalTimesOutWithExactSnapshot, ref passed);
            Run("所有恢复重入只使用统一正式执行器",
                RejoinUsesUnifiedFormalExecutor, ref passed);
            Run("正式槽关闭超时配置兼容旧项目并可往返保存",
                FormalSlotClosureTimeoutConfigRoundTrips, ref passed);
            Run("周期超限策略区分普通连续与硬截止",
                PeriodOverrunPolicyClassifiesBoundaries, ref passed);
            Run("周期硬截止只隔离本次运行且不调用项目永久禁用",
                PeriodHardLimitDoesNotPersistProjectDisable, ref passed);
            Run("2倍周期的瞬时OFF不能伪装动作终止",
                HardDeadlineRequiresTerminalGenerationAndOff, ref passed);
            Run("屏障等待是合法状态且仍要求正式运行资源",
                WaitingStateHasLegalHealthContracts, ref passed);
            return passed;
        }

        private static void PendingSafetyClosureWaitsForRealEvidence()
        {
            var observations = 0;
            var delays = 0;
            var result = FormalSafetyClosurePolicy.AwaitAsync(
                    () =>
                    {
                        var current = Interlocked.Increment(ref observations);
                        return new FormalSafetyClosureObservation
                        {
                            MotorOffConfirmed = current >= 2,
                            HydraulicReleased = current >= 3,
                            PersistenceRequired = true,
                            PersistenceClosed = current >= 4
                        };
                    },
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None,
                    (duration, token) =>
                    {
                        Interlocked.Increment(ref delays);
                        return Task.CompletedTask;
                    })
                .GetAwaiter().GetResult();

            Assert(result.State == FormalSafetyClosureState.Closed &&
                   result.MotorOffConfirmed && result.HydraulicReleased &&
                   result.PersistenceClosed && observations == 4 && delays == 3,
                "安全闭合证据尚在到达时被提前升级为SafetyUnproven，或未在证据齐备后结束等待。");
        }

        private static void RepeatedOffPreservesFirstSuccess()
        {
            Assert(OffCommandEvidencePolicy.Resolve(false, true),
                "首次OFF成功没有形成物理关闭证据。");
            Assert(OffCommandEvidencePolicy.Resolve(true, false),
                "后续重复OFF失败覆盖了首次物理成功证据。");
            Assert(!OffCommandEvidencePolicy.Resolve(false, false),
                "从未成功的OFF被错误证明为已关闭。");
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
                () => Interlocked.Increment(ref waitingSignals), CancellationToken.None,
                previousSlotClosureTimeoutMs: 500);
            var nextB = coordinator.EnterAsync(
                runId, 1, 2, new[] { 1, 2 }, anchor, periodMs,
                () => Interlocked.Increment(ref waitingSignals), CancellationToken.None,
                previousSlotClosureTimeoutMs: 500);

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
            var next = coordinator.EnterAsync(
                    runId, 1, 1, new[] { 1 }, anchor, 30, null, CancellationToken.None)
                ;
            Assert(coordinator.RequestRetirement(runId, 2, "PermanentAlarm"),
                "首次退休请求未登记");
            Thread.Sleep(20);
            Assert(!next.IsCompleted, "仅登记退休请求就伪造安全证据释放了下一槽");
            coordinator.ConfirmRetirement(runId, 2, SafeTerminal(2));

            var nextScope = next.GetAwaiter().GetResult();
            nextScope.Complete(SafeTerminal(1));
            coordinator.ClearRun(runId);
        }

        private static void UnsafeRetirementFailsClosed()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var first = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1, 2 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            first.Complete(SafeTerminal(1));
            var next = coordinator.EnterAsync(
                runId, 1, 1, new[] { 1 }, anchor, 30, null, CancellationToken.None);
            coordinator.RequestRetirement(runId, 2, "FenceFailed");
            var unsafeTerminal = SafeTerminal(2);
            unsafeTerminal.MotorOffConfirmed = false;
            coordinator.ConfirmRetirement(runId, 2, unsafeTerminal);

            var failedClosed = false;
            try { next.GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { failedClosed = true; }
            Assert(failedClosed, "退休电机OFF证据失败仍释放了槽位");
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

        private static void SafeAbortedReleasesSlot()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var first = coordinator.EnterAsync(
                    runId, 0, 4, new[] { 4 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            var abortedTerminal = new FormalBatchParticipantTerminal
            {
                Channel = 4,
                Disposition = FormalParticipantDisposition.SafeAborted,
                MotorOffConfirmed = true,
                HydraulicMemberReleased = true,
                ControlSucceeded = false,
                PersistenceBoundaryRequired = true,
                PersistenceCommitted = true,
                ExecutionPermitRevoked = true,
                MechanicalCycleCompleted = false,
                Result = "SafeAborted",
                CompletedUtc = DateTime.UtcNow
            };
            first.Complete(abortedTerminal);
            Assert(abortedTerminal.ClosureReceipt != null &&
                   abortedTerminal.ClosureReceipt.RunId == runId &&
                   abortedTerminal.ClosureReceipt.RunEpoch == 1 &&
                   abortedTerminal.ClosureReceipt.Channel == 4 &&
                   abortedTerminal.ClosureReceipt.FormalSlot == 0 &&
                   abortedTerminal.ClosureReceipt.Disposition ==
                       CycleAttemptClosureDisposition.Aborted &&
                   abortedTerminal.ClosureReceipt.Durable &&
                   !string.IsNullOrWhiteSpace(
                       abortedTerminal.ClosureReceipt.DurabilityEvidence),
                "SafeAborted没有生成带Run/Epoch/Slot/耐久证据的关闭回执");

            var next = coordinator.EnterAsync(
                    runId, 1, 4, new[] { 4 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            next.Complete(SafeTerminal(4));
            coordinator.ClearRun(runId);
        }

        private static void DurableAbortDispositionIsExplicit()
        {
            var aborted = new CycleAttemptClosureReceipt
            {
                Disposition = CycleAttemptClosureDisposition.Aborted,
                Durable = true,
                PersistenceVersion = 42,
                DurabilityEvidence = "AbortReceiptCommitted"
            };
            Assert(EpbManager.ResolveFormalSlotDisposition(
                       true,
                       true,
                       true,
                       aborted) == FormalParticipantDisposition.SafeAborted,
                "耐久作废没有映射为SafeAborted");

            var halfCommitted = new CycleAttemptClosureReceipt
            {
                Disposition = CycleAttemptClosureDisposition.Aborted,
                Durable = false,
                PersistenceVersion = 0,
                DurabilityEvidence = "AbortReceiptNotDurable"
            };
            Assert(EpbManager.ResolveFormalSlotDisposition(
                       true,
                       true,
                       true,
                       halfCommitted) == FormalParticipantDisposition.SafetyUnproven,
                "半提交作废错误释放了正式槽");
            Assert(EpbManager.ResolveFormalSlotDisposition(
                       false,
                       true,
                       true,
                       aborted) == FormalParticipantDisposition.SafetyUnproven,
                "物理OFF未证明时错误释放了正式槽");
        }

        private static void SafetyUnprovenBlocksSlot()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var first = coordinator.EnterAsync(
                    runId, 0, 5, new[] { 5 }, anchor, 30, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            first.Complete(new FormalBatchParticipantTerminal
            {
                Channel = 5,
                Disposition = FormalParticipantDisposition.SafetyUnproven,
                MotorOffConfirmed = true,
                HydraulicMemberReleased = true,
                PersistenceBoundaryRequired = true,
                PersistenceCommitted = false,
                ExecutionPermitRevoked = true,
                Result = "PersistenceUnknown",
                CompletedUtc = DateTime.UtcNow
            });

            var failed = false;
            try
            {
                coordinator.EnterAsync(
                        runId, 1, 5, new[] { 5 }, anchor, 30, null, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed, "SafetyUnproven仍放行了后续正式槽。");
            coordinator.ClearRun(runId);
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
                "第8次连续超限没有进入本次运行隔离");
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(30000, 15000, 1) ==
                   Controller.Adaptive.PeriodOverrunKind.HardLimitReached,
                "2倍周期没有进入硬截止本次运行隔离");
        }

        private static void PeriodHardLimitDoesNotPersistProjectDisable()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var isolation = typeof(EpbManager).GetMethod(
                "LatchCurrentRunPeriodIsolationAsync",
                flags);
            var forbidden = typeof(EpbManager).GetMethod(
                "PersistentlyDisableChannels",
                flags);
            Assert(isolation != null && forbidden != null,
                "无法定位周期隔离或历史永久禁用生产方法");

            var stateMachine = isolation.GetCustomAttribute<AsyncStateMachineAttribute>();
            var body = stateMachine?.StateMachineType
                .GetMethod("MoveNext", flags | BindingFlags.Public)
                ?.GetMethodBody()
                ?.GetILAsByteArray();
            Assert(body != null && body.Length > 0,
                "无法读取周期隔离生产状态机IL");

            var forbiddenToken = forbidden.MetadataToken;
            for (var index = 0; index + 4 < body.Length; index++)
            {
                if (body[index] != 0x28 && body[index] != 0x6F) continue;
                var calledToken = BitConverter.ToInt32(body, index + 1);
                Assert(calledToken != forbiddenToken,
                    "周期硬截止仍调用PersistentlyDisableChannels并污染项目Enabled");
            }
        }

        private static void HardDeadlineRequiresTerminalGenerationAndOff()
        {
            Assert(!EpbManager.IsSharedCoordinationWaitAtHardDeadline(false, false),
                "Hold/换向间隙的瞬时OFF被误当作共享收口");
            Assert(!EpbManager.IsSharedCoordinationWaitAtHardDeadline(true, true),
                "动作已结束但输出仍上电时被误放行");
            Assert(EpbManager.IsSharedCoordinationWaitAtHardDeadline(true, false),
                "本代次动作已结束且OFF时被误报警");
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

        private static void CancellationBeforeAdmissionWithdrawsParticipant()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            const int periodMs = 40;
            var firstA = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1, 2 }, anchor, periodMs, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            var firstB = coordinator.EnterAsync(
                    runId, 0, 2, new[] { 1, 2 }, anchor, periodMs, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            using var canceled = new CancellationTokenSource();
            var canceledEntry = coordinator.EnterAsync(
                runId, 1, 1, new[] { 1, 2 }, anchor, periodMs, null,
                canceled.Token,
                previousSlotClosureTimeoutMs: 1000);
            canceled.Cancel();
            var cancellationObserved = false;
            try { canceledEntry.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Assert(cancellationObserved, "等待上一槽的取消未传播给调用方");

            var healthyEntry = coordinator.EnterAsync(
                runId, 1, 2, new[] { 1, 2 }, anchor, periodMs, null,
                CancellationToken.None);
            firstA.Complete(SafeTerminal(1));
            firstB.Complete(SafeTerminal(2));
            var healthyScope = healthyEntry.GetAwaiter().GetResult();
            healthyScope.Complete(SafeTerminal(2));

            var next = coordinator.EnterAsync(
                    runId, 2, 2, new[] { 2 }, anchor, periodMs, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            next.Complete(SafeTerminal(2));
            coordinator.ClearRun(runId);
        }

        private static void DuplicateParticipantAdmissionIsRejected()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow;
            var first = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1 }, anchor, 40, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            var rejected = false;
            try
            {
                coordinator.EnterAsync(
                        runId, 0, 1, new[] { 1 }, anchor, 40, null,
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { rejected = true; }

            Assert(rejected, "重复通道取得了同一正式槽的第二个scope");
            first.Complete(SafeTerminal(1));
            coordinator.ClearRun(runId);
        }

        private static void ConfirmedRetirementClosesLateStaleSlot()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            Assert(coordinator.RequestRetirement(runId, 2, "PermanentAlarm"),
                "退休请求未登记");
            coordinator.ConfirmRetirement(runId, 2, SafeTerminal(2));

            // 模拟退休确认后，迟到回调仍携带冻结于退休前的 [1,2] 成员快照。
            var healthy = coordinator.EnterAsync(
                    runId, 0, 1, new[] { 1, 2 }, anchor, 40, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            healthy.Complete(SafeTerminal(1));

            var next = coordinator.EnterAsync(
                    runId, 1, 1, new[] { 1 }, anchor, 40, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            next.Complete(SafeTerminal(1));

            var retiredRejected = false;
            try
            {
                coordinator.EnterAsync(
                        runId, 2, 2, new[] { 2 }, anchor, 40, null,
                        CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { retiredRejected = true; }
            Assert(retiredRejected, "已确认退休通道仍可通过迟到槽重新准入");
            coordinator.ClearRun(runId);
        }

        private static void LateRetirementCannotPoisonNewParticipantGeneration()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            const long runEpoch = 9;
            var firstChannel = coordinator.RegisterParticipant(runId, runEpoch, 1);
            var retiredGeneration = coordinator.RegisterParticipant(runId, runEpoch, 2);
            Assert(coordinator.RequestRetirement(retiredGeneration, "OldGenerationRetirement"),
                "旧代退休请求未登记");

            // 正式重入先发布新代次，随后才到达的旧安全栅栏确认必须被拒绝。
            var rejoinedGeneration = coordinator.RegisterParticipant(runId, runEpoch, 2);
            coordinator.ConfirmRetirement(retiredGeneration, SafeTerminal(2));

            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var first = coordinator.EnterAsync(
                    firstChannel,
                    0,
                    new[] { firstChannel, rejoinedGeneration },
                    anchor,
                    40,
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            var rejoined = coordinator.EnterAsync(
                    rejoinedGeneration,
                    0,
                    new[] { firstChannel, rejoinedGeneration },
                    anchor,
                    40,
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            first.Complete(SafeTerminal(1));
            rejoined.Complete(SafeTerminal(2));
            coordinator.ClearRun(runId);
        }

        private static void RejoinedAndNormalParticipantsCloseSharedSlot()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            const long runEpoch = 5;
            var channels = new[] { 4, 5, 7, 8, 9, 12 };
            var initial = channels.ToDictionary(
                channel => channel,
                channel => coordinator.RegisterParticipant(runId, runEpoch, channel));
            var anchor = DateTime.UtcNow.AddMilliseconds(-10);
            var slot54 = channels.Select(channel => coordinator.EnterAsync(
                    initial[channel],
                    54,
                    initial.Values,
                    anchor,
                    10,
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult()).ToArray();
            foreach (var scope in slot54)
                scope.Complete(SafeTerminal(scope.Channel));

            // EPB4/5 模拟液压自愈后取得新参与者代次；其余通道继续原代次。
            initial[4] = coordinator.RegisterParticipant(runId, runEpoch, 4);
            initial[5] = coordinator.RegisterParticipant(runId, runEpoch, 5);
            var slot55 = channels.Select(channel => coordinator.EnterAsync(
                    initial[channel],
                    55,
                    initial.Values,
                    anchor,
                    10,
                    null,
                    CancellationToken.None)
                .GetAwaiter().GetResult()).ToArray();
            foreach (var scope in slot55)
                scope.Complete(SafeTerminal(scope.Channel));

            var slot56 = channels.Select(channel => coordinator.EnterAsync(
                    initial[channel],
                    56,
                    initial.Values,
                    anchor,
                    10,
                    null,
                    CancellationToken.None,
                    previousSlotClosureTimeoutMs: 200)
                .GetAwaiter().GetResult()).ToArray();
            foreach (var scope in slot56)
                scope.Complete(SafeTerminal(scope.Channel));
            coordinator.ClearRun(runId);
        }

        private static void MissingTerminalTimesOutWithExactSnapshot()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var runId = Guid.NewGuid();
            var anchor = DateTime.UtcNow.AddMilliseconds(-5);
            var slot55Epb4 = coordinator.EnterAsync(
                    runId, 55, 4, new[] { 4, 5, 7 }, anchor, 10, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            coordinator.EnterAsync(
                    runId, 55, 5, new[] { 4, 5, 7 }, anchor, 10, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            var slot55Epb7 = coordinator.EnterAsync(
                    runId, 55, 7, new[] { 4, 5, 7 }, anchor, 10, null,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            slot55Epb4.Complete(SafeTerminal(4));
            slot55Epb7.Complete(SafeTerminal(7));

            FormalBatchSlotWaitSnapshot observed = null;
            FormalBatchSlotClosureTimeoutException timeout = null;
            try
            {
                coordinator.EnterAsync(
                        runId, 56, 7, new[] { 4, 5, 7 }, anchor, 10, null,
                        CancellationToken.None,
                        previousSlotClosureTimeoutMs: 40,
                        waitingDetailsCallback: snapshot => observed = snapshot)
                    .GetAwaiter().GetResult();
            }
            catch (FormalBatchSlotClosureTimeoutException ex)
            {
                timeout = ex;
            }

            var snapshot = timeout?.Snapshot;
            Assert(snapshot != null && observed != null,
                "正式槽终态缺失没有产生等待快照和专用超时异常");
            Assert(snapshot.RunId == runId && snapshot.PreviousSlot == 55 &&
                   snapshot.WaitingSlot == 56 && snapshot.WaitingChannel == 7 &&
                   snapshot.Participants.SequenceEqual(new[] { 4, 5, 7 }) &&
                   snapshot.Completed.SequenceEqual(new[] { 4, 7 }) &&
                   snapshot.Pending.SequenceEqual(new[] { 5 }) &&
                   snapshot.TimeoutMs == 40,
                "正式槽超时快照没有准确锁定槽55缺失的EPB5终态");
            coordinator.ClearRun(runId);
        }

        private static void RejoinUsesUnifiedFormalExecutor()
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var unified = typeof(EpbManager).GetMethod("StartFormalPhaseTimers", flags);
            var rejoin = typeof(EpbManager).GetMethod(
                "RejoinFormalChannelsAtSharedFutureSlot",
                flags);
            var legacy = typeof(EpbManager).GetMethod("StartRejoinedFormalChannel", flags);
            Assert(unified != null && rejoin != null && legacy == null,
                "恢复旁路仍存在，或无法定位统一正式执行器");
            var body = rejoin.GetMethodBody()?.GetILAsByteArray();
            Assert(body != null && CallsMethod(body, unified.MetadataToken),
                "恢复重入没有调用统一正式执行器");
        }

        private static void FormalSlotClosureTimeoutConfigRoundTrips()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "epb-formal-slot-timeout-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(
                    path,
                    "<TestConfig><Basic><TestCycle>15</TestCycle></Basic>" +
                    "<Timer><OverrunPolicy>AlignToWallClock</OverrunPolicy></Timer>" +
                    "<Hydraulics/><ElectricalGroups/><EpbRecords/></TestConfig>");
                var legacy = ConfigLoader.LoadTest(path, null);
                Assert(legacy.FormalSlotClosureTimeoutMs == 0 &&
                       legacy.EffectiveFormalSlotClosureTimeoutMs == 30000,
                    "旧项目缺少正式槽超时节点时没有使用30秒自动值");
                legacy.FormalSlotClosureTimeoutMs = 45000;
                ConfigLoader.SaveTest(path, legacy);
                var reloaded = ConfigLoader.LoadTest(path, null);
                Assert(reloaded.FormalSlotClosureTimeoutMs == 45000 &&
                       reloaded.EffectiveFormalSlotClosureTimeoutMs == 45000,
                    "正式槽超时配置没有正确往返保存");
                reloaded.FormalSlotClosureTimeoutMs = 700000;
                Assert(reloaded.EffectiveFormalSlotClosureTimeoutMs == 600000,
                    "正式槽超时没有应用10分钟上限");
                reloaded.FormalSlotClosureTimeoutMs = 1000;
                Assert(reloaded.EffectiveFormalSlotClosureTimeoutMs == 30000,
                    "显式配置错误缩短了自动安全窗口");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static bool CallsMethod(byte[] body, int metadataToken)
        {
            for (var index = 0; index + 4 < body.Length; index++)
            {
                if (body[index] != 0x28 && body[index] != 0x6F) continue;
                if (BitConverter.ToInt32(body, index + 1) == metadataToken)
                    return true;
            }
            return false;
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
                Disposition = FormalParticipantDisposition.SafeCommitted,
                MotorOffConfirmed = true,
                HydraulicMemberReleased = true,
                ControlSucceeded = true,
                PersistenceBoundaryRequired = true,
                PersistenceCommitted = true,
                ExecutionPermitRevoked = true,
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
