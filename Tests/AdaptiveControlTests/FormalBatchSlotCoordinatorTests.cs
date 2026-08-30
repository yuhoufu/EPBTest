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
            Run("周期超限策略区分普通连续与硬截止",
                PeriodOverrunPolicyClassifiesBoundaries, ref passed);
            Run("2倍周期的瞬时OFF不能伪装动作终止",
                HardDeadlineRequiresTerminalGenerationAndOff, ref passed);
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
                "第8次连续超限没有进入永久报警");
            Assert(EpbManager.ClassifyCompletedPeriodOverrun(30000, 15000, 1) ==
                   Controller.Adaptive.PeriodOverrunKind.HardLimitReached,
                "2倍周期没有进入硬截止永久报警");
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
                canceled.Token);
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
