using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Production retention acceptance.  RuntimeShutdownRetentionCoordinator,
    /// the Runtime ownership CAS adapter and the real callback-pipeline close
    /// adapter are exercised directly.  Only the Engine receipt/exception,
    /// mark outcome and journal I/O edges are controlled.
    /// </summary>
    internal static class WatchdogRuntimeShutdownRetentionProductionTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            var failures = new List<string>();
            Run("64并发shutdown只生成一个终态", ConcurrentShutdownUsesSingleTerminal,
                ref passed, failures);
            Run("Engine throw同owner重试且终态后不再调用", EngineThrowRetriesSameOwner,
                ref passed, failures);
            Run("Engine null同owner重试", EngineNullRetriesSameOwner,
                ref passed, failures);
            Run("Engine incomplete同owner重试", EngineIncompleteRetriesSameOwner,
                ref passed, failures);
            Run("真实pipeline drain timeout只重试pipeline", PipelineDrainTimeoutRetriesOnlyPipeline,
                ref passed, failures);
            Run("pipeline throw只重试pipeline", PipelineThrowRetriesOnlyPipeline,
                ref passed, failures);
            Run("flush失败只重试flush", FlushFailureRetriesOnlyFlush,
                ref passed, failures);
            Run("dispose失败只重试dispose", DisposeFailureRetriesOnlyDispose,
                ref passed, failures);
            Run("IdentityMismatch不调用Engine或pipeline", IdentityMismatchIsSticky,
                ref passed, failures);
            Run("墓碑持久化失败阻止DetachedRetained退出", TombstonePersistenceFailureBlocksDetachedExit,
                ref passed, failures);
            Run("active replacement在owner期间被阻止", ActiveReplacementIsBlocked,
                ref passed, failures);
            Run("version递增且身份冻结", VersionsIncreaseAndIdentityStaysFrozen,
                ref passed, failures);
            Run("previous gate在owner完成前拒绝启动", PreviousGateIsFailClosed,
                ref passed, failures);
            Run("normal/recovery/emergency共用retention gate", AllEntryModesUseSharedGate,
                ref passed, failures);
            Run("真实Runtime与Sidecar两轮Closing等待StopCompleted后终态",
                RealRuntimeClosingFenceWaitsForStopCompletedAcrossTwoRounds,
                ref passed, failures);
            if (failures.Count != 0)
                throw new InvalidOperationException(
                    "Runtime shutdown retention专项失败: " + string.Join("; ", failures));
            return passed;
        }

        private static void RealRuntimeClosingFenceWaitsForStopCompletedAcrossTwoRounds()
        {
            var journal = Path.Combine(
                Path.GetTempPath(),
                "MTTFTest.RuntimeClosingFence." + Guid.NewGuid().ToString("N"));
            var sessions = new List<string>();
            Directory.CreateDirectory(journal);
            try
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.ConfigureJournalExportPath(journal);
                var currentRunId = Guid.Empty;
                WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat
                {
                    Phase = "RuntimeClosingFenceAcceptance",
                    RunId = currentRunId == Guid.Empty
                        ? string.Empty
                        : currentRunId.ToString("N"),
                    RunEpoch = 1,
                    RunActive = true,
                    EnabledChannels = new[] { 4 }
                });

                for (var round = 1; round <= 2; round++)
                {
                    currentRunId = Guid.NewGuid();
                    var transactionId = Guid.NewGuid();
                    var start = WatchdogRuntime.StartSessionAsync(new[] { 4 })
                        .GetAwaiter().GetResult();
                    Assert(start != null && start.Attached,
                        $"第{round}轮真实Runtime/Sidecar未达到Exact Attached：" +
                        (start?.Warning ?? "<null>"));
                    sessions.Add(start.SessionId);

                    var before = WatchdogRuntime.CaptureTransportSnapshot();
                    Assert(before != null && before.IsStable && before.Context != null &&
                           before.Engine != null && before.Engine.IsAttached,
                        $"第{round}轮缺少稳定Attached复合快照");
                    var context = before.Context;
                    var authorityPid = before.Engine.AuthorityProcessId;
                    Assert(authorityPid > 0 && IsProcessAlive(authorityPid),
                        $"第{round}轮Sidecar权威进程身份无效");

                    WatchdogRuntime.NotifyManualStop(
                        $"RuntimeClosingFenceRound{round}");
                    var fence = WatchdogRuntime.BeginSessionCloseExact(
                        context,
                        "ManualStopIntent",
                        transactionId,
                        currentRunId,
                        round,
                        round);
                    Assert(fence != null && fence.IsIrreversible &&
                           fence.MarkOutcome == RuntimeShutdownMarkOutcome.Marked &&
                           fence.Tombstone?.State == WatchdogClosingTombstoneState.Closing &&
                           fence.Tombstone.StateVersion == 1,
                        $"第{round}轮未建立原子Closing围栏：" +
                        (fence?.Error ?? "<null>"));

                    var reused = WatchdogRuntime.BeginSessionCloseExact(
                        context,
                        "ManualStopIntent",
                        transactionId,
                        currentRunId,
                        round,
                        round);
                    Assert(reused != null && reused.IsIrreversible &&
                           reused.Tombstone?.State == WatchdogClosingTombstoneState.Closing &&
                           reused.Tombstone.StateVersion == 1,
                        $"第{round}轮同一停止事务未幂等复用Closing围栏");
                    Assert(!WatchdogControlMarker.IsRevoked(journal, context.SessionId),
                        $"第{round}轮普通Closing错误写入legacy revoked");
                    WatchdogRuntime.NotifyRunStopped(new WatchdogStopSummary
                    {
                        Detail = $"RuntimeClosingFenceRound{round}:RunStopped"
                    });

                    var sidecarEvents = Path.Combine(
                        journal,
                        "session-" + WatchdogJournalPaths.SafeName(context.SessionId) +
                        ".sidecar-events.jsonl");
                    Assert(WaitUntilWithDelay(
                            () => WatchdogJournalStore.ReadValidEvents(sidecarEvents)
                                .Any(item => string.Equals(
                                    item.EventType,
                                    "ClosingFenceObserved",
                                    StringComparison.Ordinal)),
                            3000),
                        $"第{round}轮Sidecar未观察到Closing抑制重拉事件");
                    Thread.Sleep(1000);
                    var duringClosing = WatchdogRuntime.CaptureTransportSnapshot();
                    Assert(duringClosing?.Engine?.SessionActive == true &&
                           duringClosing.Engine.IsClosing &&
                           duringClosing.Engine.AuthorityProcessId == authorityPid &&
                           IsProcessAlive(authorityPid),
                        $"第{round}轮Sidecar在StopCompleted前提前退出或被替代");
                    Assert(!WatchdogControlMarker.IsRevoked(journal, context.SessionId),
                        $"第{round}轮延迟窗口内出现legacy revoked");

                    WatchdogRuntime.NotifyPhysicalStopConfirmed(
                        $"RuntimeClosingFenceRound{round}");
                    Assert(WatchdogRuntime.AdvanceSessionCloseSafety(
                            context,
                            new StopSafetyResult
                            {
                                Outcome = StopSafetyOutcome.CompletedSafe,
                                LastStage = StopSafetyStage.Completed,
                                SafetyTransactionId = transactionId,
                                RunId = currentRunId,
                                RunEpoch = round,
                                SafetyBoundaryGeneration = round,
                                MotorOffCommandSucceeded = true,
                                PowerOffConfirmed = true,
                                PressureSafeConfirmed = true,
                                PersistenceBoundaryConfirmed = true,
                                LogicalQuiescenceConfirmed = true
                            }),
                        $"第{round}轮未能推进v2完整安全证明");
                    WatchdogRuntime.NotifyStopCompleted(
                        new WatchdogStopSummary
                        {
                            Detail = $"RuntimeClosingFenceRound{round}:PhysicalStopConfirmed"
                        },
                        "ManualStopCompleted");

                    var manifest = Path.Combine(
                        journal,
                        "session-" + WatchdogJournalPaths.SafeName(context.SessionId) +
                        ".manifest.json");
                    Assert(WaitUntilWithDelay(
                            () => File.Exists(manifest) &&
                                  File.ReadAllText(manifest).Contains("ManualStopCompleted"),
                            5000),
                        $"第{round}轮Sidecar journal缺少ManualStopCompleted终态");

                    var terminal = WatchdogRuntime.ShutdownRuntimeWithReceipt(
                        RuntimeShutdownIntent.SessionClose);
                    Assert(terminal != null && terminal.IsTerminal &&
                           terminal.SessionId == context.SessionId &&
                           terminal.SessionGeneration == context.SessionGeneration &&
                           terminal.SessionLease == context.SessionLease &&
                           !string.Equals(
                               terminal.TerminalReason,
                               "ShutdownIdentityMismatch",
                               StringComparison.Ordinal),
                        $"第{round}轮Runtime二阶段关闭未正常收口：" +
                        (terminal?.TerminalReason ?? "<null>") + ";" +
                        WatchdogRuntime.DescribeSessionCloseIdentity(context));
                    Assert(WatchdogClosingTombstoneStore.TryRead(
                               journal,
                               context.SessionId,
                               out var tombstone) &&
                           tombstone.State == WatchdogClosingTombstoneState.Terminal &&
                           tombstone.StateVersion == 3 &&
                           string.Equals(
                               tombstone.StopSafetyTransactionId,
                               transactionId.ToString("N"),
                               StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(
                               tombstone.StopRunId,
                               currentRunId.ToString("N"),
                               StringComparison.OrdinalIgnoreCase),
                        $"第{round}轮Closing tombstone未从ClosingIntent推进至schema v2 Terminal");
                    Assert(WatchdogControlMarker.IsRevoked(journal, context.SessionId),
                        $"第{round}轮Terminal后未发布legacy兼容撤权");
                }

                Assert(sessions.Count == 2 &&
                       !string.Equals(sessions[0], sessions[1], StringComparison.Ordinal),
                    "连续两轮启动复用了旧Session授权");
            }
            finally
            {
                try { WatchdogRuntime.ShutdownRuntimeWithReceipt(); } catch { }
                WatchdogRuntime.SetHeartbeatProvider(null);
                WatchdogRuntime.ConfigureJournalExportPath(null);
                foreach (var sessionId in sessions)
                    DeleteLocalSessionCloseArtifacts(sessionId);
                try
                {
                    if (Directory.Exists(journal)) Directory.Delete(journal, true);
                }
                catch { }
            }
        }

        private static void ConcurrentShutdownUsesSingleTerminal()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                var tasks = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => session.ShutdownOrRetry()))
                    .ToArray();
                Task.WaitAll(tasks);
                var first = tasks[0].Result;
                Assert(first != null && first.IsTerminal && !first.Retained,
                    "并发shutdown未得到Retained=false终态");
                Assert(!first.PipelineNotCreatedNoWork &&
                       string.Equals(first.TerminalReason, "PipelineDrained",
                           StringComparison.Ordinal),
                    "并发验收必须走真实PipelineDrained路径，实际=" + first.TerminalReason);
                Assert(first.StopAllReceipt != null && first.CallbackReceipt != null &&
                       first.StopAllReceipt.AllResourcesReleased &&
                       first.CallbackReceipt.AllResourcesReleased,
                    "真实pipeline receipt或资源证据缺失");
                Assert(tasks.All(task => object.ReferenceEquals(first, task.Result)),
                    "并发调用没有共享同一个immutable终态receipt");
                Assert(session.TransportCalls == 1,
                    "Engine调用次数应为1，实际=" + session.TransportCalls);
                Assert(session.PipelineCalls == 1,
                    "pipeline调用次数应为1，实际=" + session.PipelineCalls);
                Assert(session.JournalDisposeCalls == 1,
                    "journal dispose次数应为1，实际=" + session.JournalDisposeCalls);
                Assert(session.CaptureOwner() == null,
                    "终态完成后owner slot必须为空");
            }
        }

        private static void EngineThrowRetriesSameOwner()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            using (var replacement = new RuntimeCallbackPipelineTestSession())
            {
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Throw;
                var failed = session.ShutdownOrRetry();
                Assert(!failed.IsTerminal && session.CaptureOwner() != null,
                    "Engine throw后没有保留同owner");
                Assert(!failed.SafeExitAllowed &&
                       failed.Disposition == RuntimeShutdownDisposition.BlockingFailure,
                    "Engine安全证据不完整时错误允许DetachedRetained退出");
                Assert(session.PipelineCalls == 0, "Engine失败不应进入pipeline");
                var ownerIdentity = session.CaptureOwner();
                Assert(!session.TryInstallReplacement(replacement.Context),
                    "active已detach但retained owner存在时不应安装replacement");

                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Complete;
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && !terminal.Retained,
                    "Engine retry没有收敛到终态");
                Assert(session.TransportCalls == 2 && session.PipelineCalls == 1,
                    "Engine retry阶段调用错: engine=" + session.TransportCalls +
                    ", pipeline=" + session.PipelineCalls);
                Assert(session.CaptureOwner() == null,
                    "terminal后不应保留owner");
                var again = session.ShutdownOrRetry();
                Assert(object.ReferenceEquals(terminal, again) && session.TransportCalls == 2,
                    "terminal缓存被重复Engine调用覆盖");
                Assert(ownerIdentity.SessionId == terminal.SessionId,
                    "retry改变了冻结SessionId");
                Assert(session.TryInstallReplacement(replacement.Context),
                    "terminal后应允许replacement安装");
                WatchdogRuntime.RemoveShutdownRetentionTestingContext(replacement.Context);
            }
        }

        private static void EngineNullRetriesSameOwner()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.ReturnNull;
                var failed = session.ShutdownOrRetry();
                Assert(!failed.IsTerminal && session.CaptureOwner() != null,
                    "Engine null后没有保留owner");
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Complete;
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 2 &&
                       session.PipelineCalls == 1,
                    "Engine null retry阶段不正确");
            }
        }

        private static void EngineIncompleteRetriesSameOwner()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Incomplete;
                var incomplete = session.ShutdownOrRetry();
                Assert(!incomplete.IsTerminal && session.CaptureOwner() != null,
                    "Engine incomplete后没有保留owner");
                Assert(session.PipelineCalls == 0, "Engine incomplete不应进入pipeline");
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Complete;
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 2 &&
                       session.PipelineCalls == 1,
                    "Engine incomplete retry阶段不正确");
            }
        }

        private static void PipelineDrainTimeoutRetriesOnlyPipeline()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.HoldPipelineCompletions = true;
                session.PipelineCloseBudget = TimeSpan.FromMilliseconds(50);
                Assert(session.PublishStopRequest(), "真实StopAll发布失败");
                Assert(WaitUntil(() => session.PipelinePostCalls > 0, 1000),
                    "真实pipeline没有进入post target");
                var first = session.ShutdownOrRetry();
                Assert(!first.IsTerminal && session.CaptureOwner() != null,
                    "真实pipeline drain timeout没有保留owner");
                Assert(session.TransportCalls == 1 && session.PipelineCalls == 1,
                    "真实pipeline首阶段调用数不正确");
                Assert(first.StopAllReceipt != null && first.CallbackReceipt != null &&
                       !first.IsTerminal,
                    "真实drain timeout没有返回完整nonterminal receipt");
                session.HoldPipelineCompletions = false;
                Assert(session.ReleasePipelineCompletions() > 0,
                    "没有释放真实post target completion");
                session.PipelineCloseBudget = TimeSpan.FromSeconds(2);
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 1 &&
                       session.PipelineCalls == 2,
                    "真实pipeline retry不应重调Engine");
            }
        }

        private static void PipelineThrowRetriesOnlyPipeline()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.ThrowNextPipeline();
                var first = session.ShutdownOrRetry();
                Assert(!first.IsTerminal && session.CaptureOwner() != null,
                    "pipeline throw没有保留owner");
                Assert(session.TransportCalls == 1 && session.PipelineCalls == 1,
                    "pipeline throw首阶段调用数不正确");
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 1 &&
                       session.PipelineCalls == 2,
                    "pipeline throw retry重调了Engine或未重调pipeline");
            }
        }

        private static void FlushFailureRetriesOnlyFlush()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.FlushFailuresRemaining = 1;
                var first = session.ShutdownOrRetry();
                Assert(!first.IsTerminal && session.CaptureOwner() != null,
                    "flush failure没有保留owner");
                Assert(first.SafeExitAllowed &&
                       first.Disposition == RuntimeShutdownDisposition.DetachedRetained &&
                       first.IsCloseAuthorized,
                    "仅日志flush失败但安全证据完整时未允许DetachedRetained退出");
                Assert(session.TransportCalls == 1 && session.PipelineCalls == 1 &&
                       session.JournalFlushCalls == 1 && session.JournalDisposeCalls == 0,
                    "flush failure阶段调用错误");
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 1 &&
                       session.PipelineCalls == 1 && session.JournalFlushCalls == 2 &&
                       session.JournalDisposeCalls == 1,
                    "flush retry错误地重做前序阶段");
            }
        }

        private static void DisposeFailureRetriesOnlyDispose()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.DisposeFailuresRemaining = 1;
                var first = session.ShutdownOrRetry();
                Assert(!first.IsTerminal && session.CaptureOwner() != null,
                    "dispose failure没有保留owner");
                Assert(first.SafeExitAllowed &&
                       first.Disposition == RuntimeShutdownDisposition.DetachedRetained &&
                       first.IsCloseAuthorized,
                    "仅日志dispose失败但安全证据完整时未允许DetachedRetained退出");
                Assert(session.TransportCalls == 1 && session.PipelineCalls == 1 &&
                       session.JournalFlushCalls == 1 && session.JournalDisposeCalls == 1,
                    "dispose failure首阶段调用错误");
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.TransportCalls == 1 &&
                       session.PipelineCalls == 1 && session.JournalFlushCalls == 1 &&
                       session.JournalDisposeCalls == 2,
                    "dispose retry重做了flush或前序阶段");
            }
        }

        private static void IdentityMismatchIsSticky()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.MarkOutcome = RuntimeShutdownMarkOutcome.IdentityMismatch;
                var first = session.ShutdownOrRetry();
                Assert(!first.IsTerminal && first.IsStickyBlockingFailure &&
                       session.CaptureOwner() != null,
                    "IdentityMismatch应保留sticky owner");
                Assert(session.TransportCalls == 0 && session.PipelineCalls == 0,
                    "IdentityMismatch不允许调用Engine/pipeline");
                var second = session.ShutdownOrRetry();
                Assert(object.ReferenceEquals(first, second) && session.TransportCalls == 0 &&
                       session.PipelineCalls == 0,
                    "IdentityMismatch retry被错误放行");
                Assert(!session.EnsurePreviousTerminal(),
                    "IdentityMismatch owner不应被previous gate放行");
            }
        }

        private static void TombstonePersistenceFailureBlocksDetachedExit()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.MarkOutcome = RuntimeShutdownMarkOutcome.TombstonePersistenceFailed;
                var receipt = session.ShutdownOrRetry();
                Assert(receipt != null && !receipt.IsCloseAuthorized &&
                       !receipt.IsStickyBlockingFailure &&
                       !receipt.SafeExitAllowed &&
                       receipt.Disposition == RuntimeShutdownDisposition.BlockingFailure,
                    "关闭墓碑未持久化时错误授权DetachedRetained退出");
                Assert(session.TransportCalls == 0 && session.PipelineCalls == 0,
                    "关闭墓碑未持久化时不应继续执行Engine或pipeline关闭");
            }
        }

        private static void ActiveReplacementIsBlocked()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            using (var replacement = new RuntimeCallbackPipelineTestSession())
            {
                Assert(!session.TryInstallReplacement(replacement.Context),
                    "active context存在时不应允许replacement");
                var receipt = session.ShutdownOrRetry();
                Assert(receipt.IsTerminal, "释放active context前shutdown未完成");
                Assert(session.TryInstallReplacement(replacement.Context),
                    "terminal后应允许新的active context进入CAS");
                WatchdogRuntime.RemoveShutdownRetentionTestingContext(replacement.Context);
            }
        }

        private static void VersionsIncreaseAndIdentityStaysFrozen()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Throw;
                session.ShutdownOrRetry();
                var firstOwner = session.CaptureOwner();
                Assert(firstOwner != null, "首个失败阶段缺少owner");
                var firstVersion = firstOwner.Version;
                var sessionId = firstOwner.SessionId;
                var generation = firstOwner.SessionGeneration;
                var lease = firstOwner.SessionLease;

                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Complete;
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && terminal.RetentionVersion > firstVersion,
                    "retention version没有严格递增");
                Assert(terminal.SessionId == sessionId &&
                       terminal.SessionGeneration == generation &&
                       terminal.SessionLease == lease,
                    "owner重试改变了冻结身份");
                Assert(session.CaptureOwner() == null && !terminal.Retained,
                    "terminal receipt或owner slot状态错误");
                AssertStageProgress(session.CaptureStageSnapshots());
            }
        }

        private static void PreviousGateIsFailClosed()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Throw;
                session.ShutdownOrRetry();
                Assert(!session.EnsurePreviousTerminal(),
                    "未完成owner不应允许Start/Recovery/Emergency previous gate");
                session.TransportMode = RuntimeShutdownRetentionTestTransportMode.Complete;
                var terminal = session.ShutdownOrRetry();
                Assert(terminal.IsTerminal && session.EnsurePreviousTerminal(),
                    "终态后previous gate未放行");
            }
        }

        private static void AllEntryModesUseSharedGate()
        {
            using (var session = new RuntimeShutdownRetentionProductionTestSession())
            {
                Assert(session.ShutdownOrRetry().IsTerminal,
                    "共享gate测试前置终态失败");
                var before = WatchdogRuntime.CaptureRetentionInstallGateCounters();
                using (var normal = new RuntimeCallbackPipelineTestSession(
                           null, RuntimeJournalMode.CreateNewInitial))
                using (var recovery = new RuntimeCallbackPipelineTestSession(
                           null, RuntimeJournalMode.OpenExistingRecovery))
                using (var emergency = new RuntimeCallbackPipelineTestSession(
                           null, RuntimeJournalMode.EmergencyRecoveryReject))
                {
                    Assert(WatchdogRuntime.InstallShutdownRetentionTestingContext(
                               normal.Context, session.Coordinator),
                        "normal shared retention gate rejected");
                    Assert(WatchdogRuntime.RemoveShutdownRetentionTestingContext(normal.Context),
                        "normal shared retention gate cleanup failed");
                    Assert(WatchdogRuntime.InstallShutdownRetentionTestingContext(
                               recovery.Context, session.Coordinator),
                        "recovery shared retention gate rejected");
                    Assert(WatchdogRuntime.RemoveShutdownRetentionTestingContext(recovery.Context),
                        "recovery shared retention gate cleanup failed");
                    Assert(WatchdogRuntime.InstallShutdownRetentionTestingContext(
                               emergency.Context, session.Coordinator),
                        "emergency shared retention gate rejected");
                    Assert(WatchdogRuntime.RemoveShutdownRetentionTestingContext(emergency.Context),
                        "emergency shared retention gate cleanup failed");
                }
                var after = WatchdogRuntime.CaptureRetentionInstallGateCounters();
                Assert(after.Calls >= before.Calls + 3 &&
                       after.CreateNewInitial >= before.CreateNewInitial + 1 &&
                       after.OpenExistingRecovery >= before.OpenExistingRecovery + 1 &&
                       after.EmergencyRecoveryReject >= before.EmergencyRecoveryReject + 1,
                    "三入口未经过同一production retention gate core");
            }
        }

        private static void Run(string name, Action action, ref int passed,
            List<string> failures)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures.Add(name + ":" + ex.GetBaseException().Message);
                Console.WriteLine("FAIL " + name + ":" + ex.GetBaseException().Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static bool WaitUntilWithDelay(Func<bool> predicate, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < Math.Max(1, timeoutMs))
            {
                try
                {
                    if (predicate()) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Thread.Sleep(25);
            }
            try { return predicate(); }
            catch { return false; }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch { return false; }
        }

        private static void DeleteLocalSessionCloseArtifacts(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            foreach (var path in new[]
                     {
                         WatchdogJournalPaths.LocalClosingPath(sessionId),
                         WatchdogJournalPaths.LocalRevocationPath(sessionId),
                         WatchdogJournalPaths.LocalRecoveryCommitPath(sessionId)
                     })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
            }
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            var deadline = Environment.TickCount + Math.Max(1, timeoutMs);
            while (Environment.TickCount - deadline < 0)
            {
                if (predicate()) return true;
                Thread.Yield();
            }
            return predicate();
        }

        private static void AssertStageProgress(RuntimeShutdownRetentionOwner[] stages)
        {
            Assert(stages != null && stages.Length >= 4,
                "owner阶段观察点不足: " + (stages?.Length ?? 0));
            long previousVersion = 0;
            string sessionId = null;
            long generation = 0;
            long lease = 0;
            foreach (var stage in stages)
            {
                Assert(stage.Version > previousVersion,
                    "owner Version未严格递增: " + stage.Phase);
                previousVersion = stage.Version;
                if (sessionId == null)
                {
                    sessionId = stage.SessionId;
                    generation = stage.SessionGeneration;
                    lease = stage.SessionLease;
                }
                Assert(stage.SessionId == sessionId &&
                       stage.SessionGeneration == generation &&
                       stage.SessionLease == lease,
                    "owner阶段身份发生变化: " + stage.Phase);
                if (stage.Phase == RuntimeShutdownRetentionPhase.PipelineShutdown &&
                    stage.RuntimeReceipt != null)
                {
                    Assert(stage.ClosingAttempt == stage.RuntimeReceipt.ClosingAttempt,
                        "PipelineShutdown owner未使用runtime receipt ClosingAttempt");
                }
            }
        }
    }
}
