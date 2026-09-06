using System;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTEmbTest;
using MtEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class I0046AlignmentTests
    {
        internal static int RunAll()
        {
            SharedClosePreservesOwner();
            LegacyCloseRequiresExactLivePermit();
            BlockedCancellationFollowsSafetySubmission();
            QueueCapacityDoesNotOverrideRecoveryOwner();
            Console.WriteLine("PASS I0046 alignment: 4 cases");
            return 4;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void SharedClosePreservesOwner()
        {
            foreach (var owner in new[] { RuntimeShutdownIntent.WatchdogTakeoverExit,
                         RuntimeShutdownIntent.WatchdogRecoveryExit })
            {
                Assert(Main_Frm.ResolveSharedShutdownIntent(RuntimeShutdownIntent.SessionClose, true, owner) == owner,
                    "MDI普通会话关闭覆盖了已确定的看门狗退出类型");
                Assert(Main_Frm.ResolveSharedShutdownIntent(RuntimeShutdownIntent.ApplicationExit, true, owner) ==
                       RuntimeShutdownIntent.ApplicationExit, "显式人工退出被恢复意图覆盖");
                Assert(Main_Frm.ResolveSharedShutdownIntent(RuntimeShutdownIntent.SessionClose, false, owner) ==
                       RuntimeShutdownIntent.SessionClose, "没有退出所有者时错误保留旧意图");
            }
        }

        private static void LegacyCloseRequiresExactLivePermit()
        {
            var now = DateTime.UtcNow.Ticks;
            var receipt = new WatchdogApplicationExitReceipt
            {
                SessionId = "i0046", SessionGeneration = 2, SessionLease = 3,
                ExitIntentId = Guid.NewGuid().ToString("N"), Revision = 1,
                TakeoverTransactionId = Guid.NewGuid().ToString("N"),
                ExitDisposition = WatchdogExitDisposition.TakeoverReplacementExit,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                RelaunchPermitGeneration = 4, RelaunchPermitId = Guid.NewGuid().ToString("N"),
                RelaunchPermitNonceSha256 = new string('a', 64),
                MainProcessId = 123, MainProcessStartUtcTicks = now - TimeSpan.TicksPerMinute,
                RequestedUtcTicks = now, DiagnosticDeadlineUtcTicks = now + TimeSpan.TicksPerSecond,
                HardDeadlineUtcTicks = now + TimeSpan.FromSeconds(10).Ticks,
                State = WatchdogApplicationExitState.Requested
            };
            var closing = new WatchdogClosingTombstone
            {
                SessionId = receipt.SessionId, SessionGeneration = receipt.SessionGeneration,
                SessionLease = receipt.SessionLease, StateVersion = 1,
                State = WatchdogClosingTombstoneState.Closing,
                TakeoverTransactionId = receipt.TakeoverTransactionId,
                ExitDisposition = receipt.ExitDisposition, RelaunchDisposition = receipt.RelaunchDisposition,
                RelaunchPermitGeneration = receipt.RelaunchPermitGeneration,
                RelaunchPermitId = receipt.RelaunchPermitId,
                RelaunchPermitNonceSha256 = receipt.RelaunchPermitNonceSha256
            };
            bool Preserve(bool manual = false, bool revoked = false, bool matches = true, int pid = 123,
                long start = 0) => WatchdogExitDispositionPolicy.ShouldPreserveReplacementOnLegacyClose(
                manual, revoked, receipt, closing, pid, start == 0 ? receipt.MainProcessStartUtcTicks : start, matches);
            Assert(Preserve(), "有效接管被晚到普通关闭撤销");
            Assert(!Preserve(manual: true) && !Preserve(revoked: true) && !Preserve(matches: false) &&
                   !Preserve(pid: 124) && !Preserve(start: now), "人工停止、撤权、旧许可或PID复用未阻止接管");
            closing.SessionLease++;
            Assert(!Preserve(), "旧租约保留了许可");
            closing.SessionLease--;
            closing.RelaunchPermitNonceSha256 = new string('b', 64);
            Assert(!Preserve(), "许可nonce不匹配仍被接受");
            closing.RelaunchPermitNonceSha256 = receipt.RelaunchPermitNonceSha256;
            closing.TakeoverTransactionId = Guid.NewGuid().ToString("N");
            Assert(!Preserve(), "跨接管事务仍被接受");
        }

        private static void BlockedCancellationFollowsSafetySubmission()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var source = new CancellationTokenSource();
            using var registration = source.Token.Register(() => { entered.Set(); release.Wait(); });
            var off = false;
            var power = false;
            string timing = null;
            var worker = Task.Run(() => EpbManager.ExecuteDaqCutoffBeforeOwnershipWait(
                () => { }, () => { }, () => { }, () => off = true, () => power = true,
                cancelCyclesAfterSafetySubmission: () => source.Cancel(), timingCompleted: value => timing = value));
            try
            {
                Assert(entered.Wait(3000), "未进入取消回调");
                Assert(off && power, "阻塞取消回调先于物理安全投递");
            }
            finally { release.Set(); worker.Wait(3000); }
            Assert(timing != null && timing.Contains("SubmitOffMs=") && timing.Contains("CancelCyclesMs="),
                "截止阶段缺少可归因计时");
        }

        private static void QueueCapacityDoesNotOverrideRecoveryOwner()
        {
            Assert(EpbManager.KeepPersistenceFailureWithRecoveryOwner("DaqPersistenceQueueFull", false),
                "相关队列容量事件重复升级当前恢复");
            Assert(!EpbManager.KeepPersistenceFailureWithRecoveryOwner("DaqPersistenceQueueFull", true) &&
                   !EpbManager.KeepPersistenceFailureWithRecoveryOwner("DaqPersistenceRecoveryTimeout", false) &&
                   !EpbManager.KeepPersistenceFailureWithRecoveryOwner("DaqSequenceGap", false),
                "耐久阻断、无进展超时或真实缺口被容量归并吞掉");
        }
    }
}
