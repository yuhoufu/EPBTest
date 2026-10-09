using System;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Timing;

namespace AdaptiveControlTests
{
    internal static class FormalRetirementBoundaryTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("旧Timer已Stop但回调未退出不能退休", StoppedTimerMustFinishCallback, ref passed);
            Run("已暂停的旧参与者可退休且不会撤销新参与者", PausedReceiptClosesOnlyOldGeneration, ref passed);
            Run("换代后未取得边界不得读取新通道IO", ReplacementCannotSupplyOldEvidence, ref passed);
            Run("执行退出耐久落盘与OFF释压缺一不可", EverySafetyBoundaryIsRequired, ref passed);
            Run("退休请求后出现的旧执行仍须闭合", LateOldAttemptMustClose, ref passed);
            Run("原通道执行许可撤销仍可形成退休证据", RevokedPermitStillWorks, ref passed);
            return passed;
        }

        private static void StoppedTimerMustFinishCallback()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var lease = coordinator.RegisterParticipant(Guid.NewGuid(), 1, 8);
            var fence = new ChannelExecutionFence();
            var permit = fence.Authorize(8, 1);
            var timer = new HighPrecisionTimer(15, OverrunPolicy.AlignToWallClock);
            var entered = NewSignal();
            var finish = NewSignal();
            var worker = timer.StartCoordinatedAsync(1, async (_, token) =>
            {
                entered.TrySetResult(true);
                await finish.Task.ConfigureAwait(false);
                return true;
            });
            var boundary = new EpbManager.FormalParticipantRetirementBoundary(lease);
            boundary.BindTimer(timer);
            boundary.Request(permit, null, null);
            try
            {
                Wait(entered.Task);
                timer.Stop();
                Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped && timer.IsRunning,
                    "测试未覆盖Stop后旧回调仍在运行的窗口。");
                Assert(!Capture(boundary, lease, out _),
                    "仅凭Stopped发布就把未退出的旧回调认作隔离。");
                finish.TrySetResult(true);
                Wait(worker);
                Assert(Capture(boundary, lease, out var receipt) &&
                       receipt.ParticipantExecutionFenced && !receipt.ExecutionPermitRevoked,
                    "旧Timer退出后没有形成独立于通道许可的精确参与者边界。");
            }
            finally
            {
                finish.TrySetResult(true);
                timer.Stop();
                Wait(worker);
            }
        }

        private static void PausedReceiptClosesOnlyOldGeneration()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var run = Guid.NewGuid();
            var healthy = coordinator.RegisterParticipant(run, 1, 7);
            var old = coordinator.RegisterParticipant(run, 1, 8);
            var anchor = DateTime.UtcNow.AddMilliseconds(-1);
            var first = coordinator.EnterAsync(healthy, 0, new[] { healthy, old },
                anchor, 10, null, CancellationToken.None).GetAwaiter().GetResult();
            var fence = new ChannelExecutionFence();
            var timer = new HighPrecisionTimer(10, OverrunPolicy.AlignToWallClock);
            var entered = NewSignal();
            var finish = NewSignal();
            var worker = timer.StartCoordinatedAsync(null, async (_, token) =>
            {
                entered.TrySetResult(true);
                await finish.Task.ConfigureAwait(false);
                return true;
            });
            var boundary = new EpbManager.FormalParticipantRetirementBoundary(old);
            boundary.BindTimer(timer);
            boundary.Request(fence.Authorize(8, 1), null, null);
            try
            {
                Wait(entered.Task);
                var pause = timer.PauseAfterCurrentCycleAsync("RetirementRegression");
                finish.TrySetResult(true);
                Wait(pause);
                Assert(timer.IsPaused && fence.Capture(8).Authorized,
                    "测试未建立通道仍授权但精确旧Timer已暂停的边界。");
                Assert(Capture(boundary, old, out var receipt), "安全暂停不能形成退休证据。");
                coordinator.RequestRetirement(old, "Paused");
                var replacement = coordinator.RegisterParticipant(run, 1, 8);
                var reads = 0;
                Assert(boundary.TryCapture(replacement, null, null,
                    () => { reads++; return false; }, () => { reads++; return false; },
                    "LateReceipt", out var late) && ReferenceEquals(receipt, late) && reads == 0,
                    "迟到确认没有复用旧安全receipt，或读取了新代次的实时IO。");
                coordinator.ConfirmRetirement(old, late);
                first.Complete(SafeTerminal(7));
                var nextHealthy = coordinator.EnterAsync(healthy, 1,
                    new[] { healthy, replacement }, anchor, 10, null,
                    CancellationToken.None).GetAwaiter().GetResult();
                var nextReplacement = coordinator.EnterAsync(replacement, 1,
                    new[] { healthy, replacement }, anchor, 10, null,
                    CancellationToken.None).GetAwaiter().GetResult();
                nextHealthy.Complete(SafeTerminal(7));
                var later = coordinator.EnterAsync(healthy, 2,
                    new[] { healthy, replacement }, anchor, 10, null,
                    CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
                Assert(!later.IsCompleted, "旧代退休receipt提前关闭了新代参与者。");
                nextReplacement.Complete(SafeTerminal(8));
                var laterScope = later.GetAwaiter().GetResult();
                laterScope.Complete(SafeTerminal(7));
            }
            finally
            {
                finish.TrySetResult(true);
                timer.Stop();
                Wait(worker);
                coordinator.ClearRun(run);
            }
        }

        private static void ReplacementCannotSupplyOldEvidence()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var run = Guid.NewGuid();
            var old = coordinator.RegisterParticipant(run, 1, 8);
            var boundary = StoppedBoundary(old);
            var replacement = coordinator.RegisterParticipant(run, 1, 8);
            var reads = 0;
            Assert(!boundary.TryCapture(replacement, null, null,
                () => { reads++; return true; }, () => { reads++; return true; },
                "NoOldReceipt", out _) && reads == 0,
                "旧代尚无安全证据时读取了替换代次的OFF/释压状态。");
        }

        private static void EverySafetyBoundaryIsRequired()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var lease = coordinator.RegisterParticipant(Guid.NewGuid(), 1, 8);
            var attempt = NewAttempt(lease);
            var boundary = StoppedBoundary(lease, attempt);
            Assert(!Capture(boundary, lease, out _), "尚未执行退出与耐久落盘即退休。");
            attempt.MarkExecutionCompleted();
            Assert(!Capture(boundary, lease, out _), "未耐久落盘即退休。");
            attempt.AbortOnce(() => true, null);
            Assert(!boundary.TryCapture(lease, null, null, () => false, () => true,
                "MotorOn", out _), "电机仍开启即退休。");
            Assert(!boundary.TryCapture(lease, null, null, () => true, () => false,
                "HydraulicHeld", out _), "液压成员未释放即退休。");
            Assert(Capture(boundary, lease, out var receipt) &&
                   receipt.ClosureReceipt?.AttemptId == attempt.AttemptId &&
                   receipt.ClosureReceipt.Durable,
                "安全退休没有保留精确旧attempt的耐久receipt。");
        }

        private static void LateOldAttemptMustClose()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var lease = coordinator.RegisterParticipant(Guid.NewGuid(), 1, 8);
            var boundary = StoppedBoundary(lease);
            var late = NewAttempt(lease);
            Assert(!boundary.TryCapture(lease, late, late, () => true, () => true,
                "LateOldAttempt", out _), "请求后出现的旧attempt被遗漏。");
            late.AbortOnce(() => true, null);
            late.MarkExecutionCompleted();
            Assert(boundary.TryCapture(lease, late, late, () => true, () => true,
                "LateOldAttemptClosed", out _), "旧attempt闭合后仍无法形成边界。");
        }

        private static void RevokedPermitStillWorks()
        {
            var coordinator = new FormalBatchSlotCoordinator();
            var lease = coordinator.RegisterParticipant(Guid.NewGuid(), 1, 8);
            var fence = new ChannelExecutionFence();
            var boundary = new EpbManager.FormalParticipantRetirementBoundary(lease);
            boundary.Request(fence.Authorize(8, 1), null, null);
            Assert(!Capture(boundary, lease, out _), "没有Timer隔离或许可撤销仍获退休授权。");
            fence.Revoke(8);
            Assert(Capture(boundary, lease, out var receipt) && receipt.ExecutionPermitRevoked &&
                   !receipt.ParticipantExecutionFenced,
                "现有执行许可撤销路径失效，或伪造了Timer隔离证明。");
        }

        private static EpbManager.FormalParticipantRetirementBoundary StoppedBoundary(
            FormalBatchParticipantLease lease, CycleAttemptContext attempt = null)
        {
            var timer = new HighPrecisionTimer(10, OverrunPolicy.AlignToWallClock);
            timer.Stop();
            var boundary = new EpbManager.FormalParticipantRetirementBoundary(lease);
            boundary.BindTimer(timer);
            boundary.Request(new ChannelExecutionFence().Authorize(lease.Channel, lease.RunEpoch),
                attempt, attempt);
            return boundary;
        }

        private static CycleAttemptContext NewAttempt(FormalBatchParticipantLease lease) =>
            new CycleAttemptContext(lease.RunId, lease.RunEpoch, "Dev2", lease.Channel,
                1, CycleAttemptKind.FormalBatch, 1);

        private static bool Capture(EpbManager.FormalParticipantRetirementBoundary boundary,
            FormalBatchParticipantLease lease, out FormalBatchParticipantTerminal receipt) =>
            boundary.TryCapture(lease, null, null, () => true, () => true, "Regression", out receipt);

        private static FormalBatchParticipantTerminal SafeTerminal(int channel) =>
            new FormalBatchParticipantTerminal
            {
                Channel = channel,
                MotorOffConfirmed = true,
                HydraulicMemberReleased = true,
                ControlSucceeded = true,
                PersistenceCommitted = true
            };

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void Wait(Task task)
        {
            if (Task.WhenAny(task, Task.Delay(3000)).GetAwaiter().GetResult() != task)
                throw new TimeoutException("退休边界测试等待超时。");
            task.GetAwaiter().GetResult();
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
