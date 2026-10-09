using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;

namespace AdaptiveControlTests
{
    internal static class FormalMonotonicScheduleTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("UTC累计错位不改变三卡钳连续15秒节拍", UtcPhaseShiftDoesNotChangeCadence, ref passed);
            Run("真实16秒安全收尾跳调度槽且逻辑安全链连续", RealOverrunSkipsScheduleSlotOnly, ref passed);
            Run("上一逻辑槽缺失不能绕过安全屏障", MissingLogicalSlotIsRejected, ref passed);
            Run("UTC前跳后跳不能放行尚未安全闭合的成员", ClockStepsCannotReleasePendingMembers, ref passed);
            Run("恢复预约追随已创建逻辑槽并允许取消后重试", RejoinReservationUsesLogicalSlots, ref passed);
            Run("初槽单调等待不受UTC在等待中变化影响", FirstGrantWaitIgnoresUtcStep, ref passed);
            Run("已有调度锚点不能被恢复时UTC重建", ExistingScheduleIsNotReanchored, ref passed);
            Run("安全收尾完成后只发布计划周期等待", ClosedSlotUsesPlannedWaitingCallback, ref passed);
            Run("取消形成的空槽不能绕过更早未闭合的槽", CanceledEmptySlotPreservesPredecessorBarrier, ref passed);
            Run("退休形成的空槽承接前驱安全闭合和失败", RetiredEmptySlotPreservesPredecessorBarrier, ref passed);
            Run("新代参与者不能借旧代冻结成员表进入旧槽", RejoinedLeaseCannotEnterFrozenOldGeneration, ref passed);
            Run("旧代迟到退休只关闭匹配旧槽并保留安全失败", LateRetirementClosesOnlyMatchingGeneration, ref passed);
            Run("旧代执行隔离仍要求完整安全收尾且不污染新代", FencedParticipantRequiresCompleteSafety, ref passed);
            return passed;
        }

        private static void UtcPhaseShiftDoesNotChangeCadence()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var leases = new[] { 7, 8, 9 }.Select(channel =>
                coordinator.RegisterParticipant(run, 1, channel)).ToArray();
            long? previousRelease = null;
            for (var slot = 0; slot < 40; slot++)
            {
                // The field defect appears when this phase error exceeds the
                // 4.6 s slack. Also exercise a later backward wall-clock step.
                time.UtcOffsetMs = slot < 20 ? slot * 300 : 5350 - (slot - 20) * 800;
                var scopes = leases.Select(lease => coordinator.EnterAsync(
                    lease, slot, leases, null, CancellationToken.None)
                    .GetAwaiter().GetResult()).ToArray();
                var grant = scopes[0].Grant;
                Assert(scopes.All(scope => ReferenceEquals(scope.Grant, grant)),
                    "同一槽的三卡钳没有共用同一个不可变调度授权。");
                if (previousRelease.HasValue)
                    Assert(grant.ReleaseTimestamp - previousRelease.Value == 15000,
                        "UTC相位变化使15秒周期变长或触发重复排程。");
                Assert(grant.LogicalSlot == slot && grant.ScheduleSlot == slot,
                    "未超时的圈错误跳过逻辑槽或调度槽。");
                Assert(time.Timestamp == grant.ReleaseTimestamp,
                    "单调计划时刻尚未到达就放行。");
                previousRelease = grant.ReleaseTimestamp;
                time.Advance(10400);
                foreach (var scope in scopes) scope.Complete(SafeTerminal(scope.Channel));
            }
            coordinator.ClearRun(run);
        }

        private static void RealOverrunSkipsScheduleSlotOnly()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var first = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            time.Advance(16000);
            first.Complete(SafeTerminal(7));
            var second = coordinator.EnterAsync(lease, 1, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(second.Grant.LogicalSlot == 1 && second.Grant.ScheduleSlot == 2 &&
                   second.Grant.ReleaseTimestamp - first.Grant.ReleaseTimestamp == 30000,
                "真实超期没有跳到未来调度槽，或跳过了逻辑安全收尾链。");
            second.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
        }

        private static void MissingLogicalSlotIsRejected()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var first = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            first.Complete(SafeTerminal(7));
            var rejected = false;
            try
            {
                coordinator.EnterAsync(lease, 2, new[] { lease }, null,
                    CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.Contains("FormalLogicalSlotGap");
            }
            Assert(rejected, "缺少上一逻辑槽时仍获准执行，绕过了安全屏障。");
            coordinator.ClearRun(run);
        }

        private static void ClockStepsCannotReleasePendingMembers()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var leases = new[] { 7, 8 }.Select(channel =>
                coordinator.RegisterParticipant(run, 1, channel)).ToArray();
            var first = leases.Select(lease => coordinator.EnterAsync(lease, 0,
                leases, null, CancellationToken.None).GetAwaiter().GetResult()).ToArray();
            var next = coordinator.EnterAsync(leases[0], 1, leases, null,
                CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
            time.UtcOffsetMs = 3600000;
            first[0].Complete(SafeTerminal(7));
            Assert(!next.IsCompleted, "UTC前跳绕过了另一成员尚未闭合的安全终态。");
            time.UtcOffsetMs = -3600000;
            time.Advance(10400);
            first[1].Complete(SafeTerminal(8));
            var nextFirst = next.GetAwaiter().GetResult();
            var nextSecond = coordinator.EnterAsync(leases[1], 1, leases, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(nextFirst.Grant.ReleaseTimestamp - first[0].Grant.ReleaseTimestamp == 15000 &&
                   ReferenceEquals(nextFirst.Grant, nextSecond.Grant),
                "全部成员安全闭合后UTC后跳改变了共同单调放行时刻。");
            nextFirst.Complete(SafeTerminal(7));
            nextSecond.Complete(SafeTerminal(8));
            coordinator.ClearRun(run);
        }

        private static void RejoinReservationUsesLogicalSlots()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var healthy = coordinator.RegisterParticipant(run, 1, 7);
            var first = coordinator.EnterAsync(healthy, 0, new[] { healthy }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            var ahead = coordinator.EnterAsync(healthy, 1, new[] { healthy }, null,
                CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
            Assert(coordinator.ReserveNextLogicalSlot(run, 1) == 2,
                "重入加入了已冻结的下一等待槽。");
            Assert(coordinator.ReserveNextLogicalSlot(run, 1) == 2,
                "重复预约或取消后的重试制造了逻辑槽空洞。");
            time.Advance(10400);
            first.Complete(SafeTerminal(7));
            var second = ahead.GetAwaiter().GetResult();
            second.Complete(SafeTerminal(7));
            time.Advance(600000);
            time.UtcOffsetMs = 3600000;
            var resumedSlot = coordinator.ReserveNextLogicalSlot(run, 1);
            var beforeResume = time.Timestamp;
            var rejoined = coordinator.RegisterParticipant(run, 1, 8);
            var members = new[] { healthy, rejoined };
            var resumedA = coordinator.EnterAsync(healthy, resumedSlot, members, null,
                CancellationToken.None).GetAwaiter().GetResult();
            var resumedB = coordinator.EnterAsync(rejoined, resumedSlot, members, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(resumedSlot == 2 && resumedA.Grant.ReleaseTimestamp > beforeResume &&
                   ReferenceEquals(resumedA.Grant, resumedB.Grant),
                "长暂停后重入使用UTC槽号脱离逻辑链，或未选取共同未来单调边界。");
            resumedA.Complete(SafeTerminal(7));
            resumedB.Complete(SafeTerminal(8));
            coordinator.ClearRun(run);
        }

        private static void FirstGrantWaitIgnoresUtcStep()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            var before = time.Timestamp;
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc.AddSeconds(10));
            time.UtcShiftOnNextDelay = 3600000;
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var scope = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(time.Timestamp - before == 10000 && scope.Grant.ReleaseTimestamp == time.Timestamp,
                "UTC在初槽等待中变化使单调放行提前或延迟。");
            scope.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
        }

        private static void ExistingScheduleIsNotReanchored()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            var original = time.Timestamp;
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            time.UtcOffsetMs = 5350;
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc.AddMinutes(1));
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var scope = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Assert(scope.Grant.AnchorTimestamp == original,
                "恢复时的UTC被用于重建已有正式运行的单调锚点。");
            scope.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
        }

        private static void ClosedSlotUsesPlannedWaitingCallback()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var first = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            time.Advance(10400);
            first.Complete(SafeTerminal(7));
            var safetyWaits = 0;
            var plannedWaits = 0;
            var second = coordinator.EnterAsync(lease, 1, new[] { lease },
                () => safetyWaits++, CancellationToken.None,
                plannedWaitingCallback: () => plannedWaits++).GetAwaiter().GetResult();
            Assert(safetyWaits == 0 && plannedWaits == 1,
                "前槽已安全闭合，界面仍被发布为等待同槽而非等待计划周期。");
            second.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
        }

        private static void CanceledEmptySlotPreservesPredecessorBarrier()
        {
            CheckEmptySlotPredecessor(safe: true, retirePendingMember: false);
            CheckEmptySlotPredecessor(safe: false, retirePendingMember: false);
        }

        private static void RetiredEmptySlotPreservesPredecessorBarrier()
        {
            CheckEmptySlotPredecessor(safe: true, retirePendingMember: true);
            CheckEmptySlotPredecessor(safe: false, retirePendingMember: true);
        }

        private static void CheckEmptySlotPredecessor(bool safe, bool retirePendingMember)
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var lease = coordinator.RegisterParticipant(run, 1, 7);
            var first = coordinator.EnterAsync(lease, 0, new[] { lease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            var nextLease = retirePendingMember
                ? coordinator.RegisterParticipant(run, 1, 8)
                : lease;
            var nextMembers = retirePendingMember ? new[] { lease, nextLease } : new[] { lease };
            using var canceled = new CancellationTokenSource();
            var abandoned = coordinator.EnterAsync(nextLease, 1, nextMembers, null,
                canceled.Token, previousSlotClosureTimeoutMs: 1000);
            canceled.Cancel();
            try { abandoned.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            if (retirePendingMember)
            {
                Assert(coordinator.RequestRetirement(lease, "Paused"), "未能登记待准入成员退休。");
                coordinator.ConfirmRetirement(lease, SafeTerminal(7));
            }
            var next = coordinator.EnterAsync(nextLease, 2, new[] { nextLease }, null,
                CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
            Assert(!next.IsCompleted,
                "全部取消的空槽提前完成，下一逻辑槽绕过了更早仍在执行的槽。");
            time.Advance(10400);
            var terminal = SafeTerminal(7);
            terminal.MotorOffConfirmed = safe;
            first.Complete(terminal);
            if (!safe)
            {
                var failedClosed = false;
                try { next.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { failedClosed = true; }
                Assert(failedClosed, "取消形成的空槽吞掉了前驱的安全关闭失败。");
                coordinator.ClearRun(run);
                return;
            }
            var resumed = next.GetAwaiter().GetResult();
            Assert(resumed.Grant.ReleaseTimestamp - first.Grant.ReleaseTimestamp == 15000,
                "空槽承接安全链后没有使用真实前驱关闭时刻的未来边界。");
            resumed.Complete(SafeTerminal(nextLease.Channel));
            coordinator.ClearRun(run);
        }

        private static void RejoinedLeaseCannotEnterFrozenOldGeneration()
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var healthy = coordinator.RegisterParticipant(run, 1, 7);
            var oldLease = coordinator.RegisterParticipant(run, 1, 8);
            var oldMembers = new[] { healthy, oldLease };
            var first = coordinator.EnterAsync(healthy, 0, oldMembers, null,
                CancellationToken.None).GetAwaiter().GetResult();
            var rejoined = coordinator.RegisterParticipant(run, 1, 8);
            var rejected = false;
            try
            {
                var invalidScope = coordinator.EnterAsync(rejoined, 0, oldMembers, null,
                    CancellationToken.None).GetAwaiter().GetResult();
                invalidScope.Complete(SafeTerminal(8));
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.Contains("FormalBatchParticipantLeaseMismatch");
            }
            first.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
            Assert(rejected, "新代参与者使用旧代冻结成员表取得了旧槽授权。");
        }

        private static void LateRetirementClosesOnlyMatchingGeneration()
        {
            CheckLateRetirementGeneration(safe: true);
            CheckLateRetirementGeneration(safe: false);
        }

        private static void FencedParticipantRequiresCompleteSafety()
        {
            CheckLateRetirementGeneration(safe: true, participantExecutionFenced: true);
            CheckLateRetirementGeneration(safe: false, participantExecutionFenced: true);
        }

        private static void CheckLateRetirementGeneration(bool safe, bool participantExecutionFenced = false)
        {
            var time = new ManualScheduleTime();
            var coordinator = new FormalBatchSlotCoordinator(time.Clock);
            var run = Guid.NewGuid();
            coordinator.EnsureRunSchedule(run, 1, 15000, time.Clock.NowUtc);
            var healthy = coordinator.RegisterParticipant(run, 1, 7);
            var oldLease = coordinator.RegisterParticipant(run, 1, 8);
            var first = coordinator.EnterAsync(healthy, 0, new[] { healthy, oldLease }, null,
                CancellationToken.None).GetAwaiter().GetResult();
            first.Complete(SafeTerminal(7));
            Assert(coordinator.RequestRetirement(oldLease, "Paused"), "旧代退休请求未登记。");
            var rejoined = coordinator.RegisterParticipant(run, 1, 8);
            var newMembers = new[] { healthy, rejoined };
            var next = coordinator.EnterAsync(healthy, 1, newMembers, null,
                CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
            var terminal = SafeTerminal(8);
            terminal.MotorOffConfirmed = safe;
            terminal.ParticipantExecutionFenced = participantExecutionFenced;
            terminal.ExecutionPermitRevoked = !participantExecutionFenced;
            if (participantExecutionFenced)
                terminal.Disposition = FormalParticipantDisposition.LegacyUnknown;
            coordinator.ConfirmRetirement(oldLease, terminal);
            if (!safe)
            {
                var safetyFailure = false;
                try { next.GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex)
                {
                    safetyFailure = ex.Message.Contains("FormalSlotSafetyBoundaryFailed") &&
                        (!participantExecutionFenced || ex.Message.Contains("ParticipantExecutionFenced=True"));
                }
                coordinator.ClearRun(run);
                Assert(safetyFailure, "旧代迟到退休的安全失败被忽略或被当作安全闭合。");
                return;
            }
            Assert(Task.WhenAny(next, Task.Delay(500)).GetAwaiter().GetResult() == next,
                "新代登记后忽略了已请求的旧代退休确认，旧槽无法安全闭合。");
            var nextHealthy = next.GetAwaiter().GetResult();
            nextHealthy.Complete(SafeTerminal(7));
            var after = coordinator.EnterAsync(healthy, 2, newMembers, null,
                CancellationToken.None, previousSlotClosureTimeoutMs: 1000);
            coordinator.ConfirmRetirement(oldLease, SafeTerminal(8));
            Assert(!after.IsCompleted, "旧代退休确认关闭了新代成员仍未进入的槽。");
            var nextRejoined = coordinator.EnterAsync(rejoined, 1, newMembers, null,
                CancellationToken.None).GetAwaiter().GetResult();
            nextRejoined.Complete(SafeTerminal(8));
            var afterHealthy = after.GetAwaiter().GetResult();
            afterHealthy.Complete(SafeTerminal(7));
            coordinator.ClearRun(run);
        }

        private sealed class ManualScheduleTime
        {
            private long _timestamp = 1000000;
            private long _utcOffsetMs;
            private readonly DateTime _utc = new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc);

            internal ManualScheduleTime()
            {
                Clock = new FormalScheduleClock(() => Timestamp, 1000,
                    () => _utc.AddMilliseconds(Timestamp - 1000000 + UtcOffsetMs),
                    (milliseconds, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        UtcOffsetMs += UtcShiftOnNextDelay;
                        UtcShiftOnNextDelay = 0;
                        Advance(milliseconds);
                        return Task.CompletedTask;
                    });
            }

            internal FormalScheduleClock Clock { get; }
            internal long Timestamp => Interlocked.Read(ref _timestamp);
            internal long UtcOffsetMs
            {
                get => Interlocked.Read(ref _utcOffsetMs);
                set => Interlocked.Exchange(ref _utcOffsetMs, value);
            }
            internal long UtcShiftOnNextDelay { get; set; }
            internal void Advance(long milliseconds) => Interlocked.Add(ref _timestamp, milliseconds);
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
                Result = "Completed"
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
