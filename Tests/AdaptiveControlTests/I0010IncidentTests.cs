using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using Timing;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class I0010IncidentTests
    {
        internal static int RunAll()
        {
            foreach (var channels in new[] { new[] { 4 }, new[] { 4, 5 }, new[] { 4, 5, 7, 8, 9, 12 } })
                I0009IncidentTests.ScopeCompetition(channels, "LearningPersistenceSelfHealing");
            ScopeWait().GetAwaiter().GetResult();
            ScopeDenialAndDeadline();
            FormalPublication();
            ExitReceiptReplay();
            SnapshotPublication();
            ReplacementMonitoring();
            V216StabilityTests.PendingStartupEscalationKeepsExactIdentity();
            Console.WriteLine("PASS I0010 学习恢复单/部分/全通道所有权、等待取消与截止、计时器交接、退出回执重放");
            return 10;
        }

        private static async Task ScopeWait()
        {
            var attempts = 0;
            var retired = false;
            var acquired = false;
            using (var cancellation = new CancellationTokenSource())
            {
                var waiting = EpbManager.WaitForRecoveryScopeAsync(() =>
                {
                    attempts++;
                    if (!retired) return false;
                    acquired = true;
                    return true;
                }, () => true, () => { }, 2000, cancellation.Token);
                Check(!waiting.IsCompleted && !acquired, "原owner未退役时提前取得清理许可");
                retired = true;
                await waiting;
                Check(acquired && attempts >= 2, "原owner退役后没有重新取得许可");
                var cancelled = EpbManager.WaitForRecoveryScopeAsync(() => false, () => true,
                    () => { }, 2000, cancellation.Token);
                cancellation.Cancel();
                try { await cancelled; throw new Exception("取消仍放行"); }
                catch (OperationCanceledException) { }
            }
        }

        private static void ScopeDenialAndDeadline()
        {
            try
            {
                EpbManager.WaitForRecoveryScopeAsync(() => false, () => false, () => { },
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("真实登记拒绝被当作范围竞争");
            }
            catch (InvalidOperationException) { }
            try
            {
                EpbManager.WaitForRecoveryScopeAsync(() => false, () => true, () => { },
                    1, CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("范围占用无限等待");
            }
            catch (TimeoutException) { }
            var acquired = false;
            try
            {
                EpbManager.WaitForRecoveryScopeAsync(() => acquired = true, () => true,
                    () => { throw new OperationCanceledException("RunEpochChanged"); },
                    1000, CancellationToken.None).GetAwaiter().GetResult();
                throw new Exception("撤权后仍准入");
            }
            catch (OperationCanceledException) { }
            Check(!acquired, "身份变化后触碰登记入口");
        }

        private static void FormalPublication()
        {
            var timer = new HighPrecisionTimer(50, OverrunPolicy.AlignToWallClock);
            var state = ChannelRuntimeState.Learning;
            var batchRunning = false;
            var cycles = 0;
            Task running = null;
            try
            {
                EpbManager.StartFormalRuntime(() =>
                {
                    // 模拟 GetTimer 已可见而读取历史计数尚未返回的现场窗口。
                    Check(timer.RuntimeState == HighPrecisionTimerRuntimeState.Created,
                        "没有覆盖Created窗口");
                    Check(EpbManager.EvaluateTimerRuntimeHealth(state, timer, DateTime.UtcNow, 30000).Action ==
                        TimerRuntimeHealthAction.None, "创建中的计时器误触发自恢复");
                    running = timer.StartAsync(null, 100, (_, __) =>
                    { Interlocked.Increment(ref cycles); return Task.FromResult(true); });
                }, () => batchRunning = true, () =>
                {
                    Check(batchRunning && timer.IsRunning, "Running早于计时器和批次准入");
                    state = ChannelRuntimeState.Running;
                    Check(EpbManager.EvaluateTimerRuntimeHealth(state, timer, DateTime.UtcNow, 30000).Action ==
                        TimerRuntimeHealthAction.None, "交接后误触发自恢复");
                });
                Check(SpinWait.SpinUntil(() => Volatile.Read(ref cycles) >= 2, 3000), "计时器没有继续执行周期");
            }
            finally { timer.Stop(); if (running != null) Check(running.Wait(3000), "计时器未退役"); }
            var published = false;
            try
            {
                EpbManager.StartFormalRuntime(() => { throw new InvalidOperationException("history read failed"); },
                    () => published = true, () => published = true);
            }
            catch (InvalidOperationException) { }
            Check(!published, "计时器建立失败仍发布运行成功");
        }

        private static void ExitReceiptReplay()
        {
            foreach (DurableRelaunchPermitState state in Enum.GetValues(typeof(DurableRelaunchPermitState)))
            {
                var permit = new DurableRelaunchPermitRecord { Generation = 1, State = state };
                for (var replay = 0; replay < 1000; replay++)
                    Check(WatchdogHost.CanObserveApprovedExitPermit(permit, 1) ==
                        (state == DurableRelaunchPermitState.Approved), "旧退出回执复活已消费或闭锁许可：" + state);
                Check(!WatchdogHost.CanObserveApprovedExitPermit(permit, 2), "旧代回执混入新许可");
            }
            Check(!WatchdogHost.CanObserveApprovedExitPermit(null, 1), "缺失权威仍接管");
        }

        private static void ReplacementMonitoring()
        {
            var record = new DurableRelaunchPermitRecord
            { Generation = 1, State = DurableRelaunchPermitState.Attached, ProcessId = 23736, ProcessStartUtcTicks = 10 };
            for (var repeat = 0; repeat < 1000; repeat++)
                Check(WatchdogHost.CanMonitorReplacementPastExitFence(record, 1, true, 23736, 10),
                    "已附着新进程被旧退出终态遮蔽监督");
            Check(!WatchdogHost.CanMonitorReplacementPastExitFence(record, 1, false, 23736, 10), "未附着即恢复监督");
            Check(!WatchdogHost.CanMonitorReplacementPastExitFence(record, 1, true, 23736, 11), "PID复用错过代际校验");
            Check(!WatchdogHost.CanMonitorReplacementPastExitFence(record, 2, true, 23736, 10), "跳过未来代际安全终态");
            record.State = DurableRelaunchPermitState.Approved;
            Check(!WatchdogHost.CanMonitorReplacementPastExitFence(record, 1, true, 23736, 10), "跳过当前未消费停止许可");
            record.Generation = 2;
            Check(WatchdogHost.CanMonitorReplacementPastExitFence(record, 1, true, 23736, 10), "旧代终态遮蔽新代恢复监督");
        }

        private static void SnapshotPublication()
        {
            var calls = 0;
            WatchdogSafetyConfigSnapshotStore.PublishSnapshotDirectory(() =>
            {
                if (++calls == 1) throw new IOException("directory sharing", unchecked((int)0x80070005));
            }, () => true);
            Check(calls == 2, "未发布目录未重试已复现瞬态错误");
            foreach (var unpublished in new[] { true, false })
            {
                calls = 0;
                try
                {
                    WatchdogSafetyConfigSnapshotStore.PublishSnapshotDirectory(() =>
                    { calls++; throw new IOException("denied", unchecked((int)0x80070005)); }, () => unpublished);
                    throw new Exception("永久失败被误报成功");
                }
                catch (IOException) { }
                Check(calls == (unpublished ? 5 : 1), "永久失败未有界或已发布目录仍被重写");
            }
            calls = 0;
            try
            {
                WatchdogSafetyConfigSnapshotStore.PublishSnapshotDirectory(() =>
                { calls++; throw new UnauthorizedAccessException("ACL denied"); }, () => true);
                throw new Exception("ACL拒绝被忽略");
            }
            catch (UnauthorizedAccessException) { }
            Check(calls == 1, "不得将权限异常泛化为瞬态重试");
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
