using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;
using MTTFTest.Watchdog;
using MTTFTest.RecoveryControl;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Deterministic host/bootstrap seam tests.  They exercise the production
    /// schema4 bootstrap and durable coordinator used by WatchdogHost; no
    /// duplicate recovery algorithm is maintained in the test.
    /// </summary>
    internal static class WatchdogHostIntegrationTests
    {
        private static void RetainedPermitRunSurvivesCleanup()
        {
            var run = new WatchdogVerifiedActiveRun
            {
                RunId = "2d6867ec8acb44329bbb9ea0aee2a78b", RunEpoch = 4,
                ProcessId = 336, ProcessStartUtcTicks = 639245054733541561
            };
            var journal = new WatchdogJournal
            {
                RunId = string.Empty, CurrentPid = 336,
                CurrentProcessStartUtcTicks = run.ProcessStartUtcTicks,
                LastVerifiedActiveRun = run
            };
            Assert(ReferenceEquals(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal), run),
                "清场丢失已验证重建Run");
            journal.CurrentPid++;
            Assert(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal) == null, "串PID被接纳");
            journal.CurrentPid = run.ProcessId;
            journal.CurrentProcessStartUtcTicks++;
            Assert(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal) == null, "PID复用被接纳");
            journal.CurrentProcessStartUtcTicks = run.ProcessStartUtcTicks;
            journal.ManualStopRequested = true;
            Assert(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal) == null, "人工停止被忽略");
            journal.ManualStopRequested = false;
            journal.RecoveryBlocked = true;
            Assert(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal) == null, "阻断被忽略");
            journal.RecoveryBlocked = false;
            journal.RunId = Guid.NewGuid().ToString("N");
            Assert(WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal) == null, "当前Run被旧记录替换");
        }

        private static void RetainedRunUsesProductionAuthorityAdmission()
        {
            var root = Path.Combine(Path.GetTempPath(), "RetainedPermit-" + Guid.NewGuid().ToString("N"));
            try
            {
                var now = DateTime.UtcNow;
                var process = RecoveryProcessProbe.Current();
                var run = Guid.NewGuid().ToString("N");
                var session = Guid.NewGuid().ToString("N");
                var store = new RecoveryControlStore(root);
                store.Register("retained-permit-test", process.ExecutablePath);
                var token = store.BeginManualRun(run, run, "config-hash", process, now);
                store.BindWatchdogSession(token, run, process, session, now);
                var journal = new WatchdogJournal
                {
                    RunId = string.Empty, CurrentPid = process.ProcessId,
                    CurrentProcessStartUtcTicks = process.StartUtcTicks,
                    LastVerifiedActiveRun = new WatchdogVerifiedActiveRun
                    {
                        RunId = run, RunEpoch = 4, ProcessId = process.ProcessId,
                        ProcessStartUtcTicks = process.StartUtcTicks
                    }
                };
                var calls = 0;
                Func<int> mutation = () => ++calls;
                void Reject(string requestedSession, string requestedRun, string reason)
                {
                    try { store.RunLegacyRecoveryAuthorityMutation(requestedSession, requestedRun, now, mutation); }
                    catch (InvalidOperationException ex)
                    {
                        Assert(ex.Message.Contains(reason), "许可拒绝原因不一致：" + ex.Message);
                        return;
                    }
                    throw new InvalidOperationException("错误身份进入许可写入");
                }
                Reject(session, journal.RunId, "Superseded");
                var retained = WatchdogRecoveryChannelIntentPolicy.GetRetainedPermitRun(journal);
                Assert(store.RunLegacyRecoveryAuthorityMutation(session, retained.RunId, now, mutation) == 1,
                    "清场后的同进程Run不能通过真实许可准入");
                Reject(Guid.NewGuid().ToString("N"), retained.RunId, "Superseded");
                Reject(session, Guid.NewGuid().ToString("N"), "Superseded");
                store.SetOperatorIntent(token.AuthorizationId, token.IntentVersion, RecoveryDesiredState.Stopped, "stop");
                Reject(session, retained.RunId, "Revoked");
                Assert(calls == 1, "拒绝路径写入了许可");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        internal static int RunAll()
        {
            var passed = 0;
            Run("清场后的许可Run沿用同进程已验证重建身份", RetainedPermitRunSurvivesCleanup, ref passed);
            Run("清场Run通过真实许可准入且人工停止和串会话仍拒绝", RetainedRunUsesProductionAuthorityAdmission, ref passed);
            Run("Host schema4 bootstrap先于Sidecar且只允许一次", BootstrapIsSchema4AndSingleUse, ref passed);
            Run("Host bootstrap非法身份故障闭锁", InvalidBootstrapFailsClosed, ref passed);
            Run("Host生产编排注入端口仍由durable permit唯一授权", ProductionOrchestratorUsesInjectedPorts, ref passed);
            Run("Host生产接管端到端严格Dump到Permit到Terminate到Launch",
                ProductionTakeoverPipelineIsMonotonic,
                ref passed);
            Run("Host生产接管阶段回退明确拒绝且无副作用",
                ProductionTakeoverRegressionIsObservable,
                ref passed);
            Run("Host恢复通道意图从初始进程冻结并跨Sidecar重载保持",
                RecoveryChannelIntentIsFrozenAndDurable,
                ref passed);
            Run("有效活动Run快照不被空RunId或恢复子进程心跳覆盖",
                LastVerifiedActiveRunSurvivesCleanupHeartbeat,
                ref passed);
            Run("Closing只撤销重拉且终态判定独立",
                ClosingFenceSuppressesRelaunchWithoutPrematureTerminal,
                ref passed);
            Run("旧进程只有Dead或IdentityMismatch且WaitForExit成功才形成退出证明",
                OldProcessExitProofFailsClosed,
                ref passed);
            Run("恢复安全证明要求完整StopCompleted或完整Handoff证据",
                RecoverySafetyProofRequiresCompleteEvidence,
                ref passed);
            Run("强杀恢复只在旧进程退出、种子和许可精确绑定后放行",
                CrashRecoveryRequiresExactSeedExitAndPermit,
                ref passed);
            Run("可执行文件SHA256比较跨大小写保持精确且拒绝内容变化",
                ExecutableSha256ComparisonIsCanonical,
                ref passed);
            Run("Supervisor权威项目根与WatchdogSessions目录规范化匹配",
                SupervisorAuthorityPathMatchesJournalProjectRoot,
                ref passed);
            Run("初次安全替换无预退避且使用5秒拉起15秒附着契约",
                RecoverySlaAndBackoffPolicyAreExact,
                ref passed);
            return passed;
        }

        private static void OldProcessExitProofFailsClosed()
        {
            Assert(
                WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(
                    DurableRelaunchProcessObservation.Dead),
                "Dead未形成旧进程退出证明");
            Assert(
                WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(
                    DurableRelaunchProcessObservation.IdentityMismatch),
                "PID复用的IdentityMismatch未形成旧进程退出证明");
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(
                    DurableRelaunchProcessObservation.Alive) &&
                !WatchdogRecoveryReadinessPolicy.IsOldProcessExitProven(
                    DurableRelaunchProcessObservation.Unknown),
                "Alive或Unknown错误放行第二主进程");
            Assert(
                !WatchdogRecoveryReadinessPolicy.CanAuthorizeAfterExitObservation(
                    DurableRelaunchProcessObservation.Dead,
                    waitForExitWasRequired: true,
                    waitForExitSucceeded: false),
                "WaitForExit=false仍允许消费Permit或拉起");
            Assert(
                WatchdogRecoveryReadinessPolicy.CanAuthorizeAfterExitObservation(
                    DurableRelaunchProcessObservation.Dead,
                    waitForExitWasRequired: true,
                    waitForExitSucceeded: true) &&
                WatchdogRecoveryReadinessPolicy.CanAuthorizeAfterExitObservation(
                    DurableRelaunchProcessObservation.IdentityMismatch,
                    waitForExitWasRequired: false,
                    waitForExitSucceeded: false),
                "已证明退出被错误阻断");
        }

        private static void CrashRecoveryRequiresExactSeedExitAndPermit()
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var seed = new WatchdogCrashRecoverySeed
            {
                SessionId = sessionId,
                SessionGeneration = 2,
                SessionLease = 3,
                SeedId = Guid.NewGuid().ToString("N"),
                Revision = 1,
                MainExecutablePath = "main.exe",
                MainExecutableSha256 = new string('a', 64),
                SafetyAgentExecutablePath = "agent.exe",
                SafetyAgentExecutableSha256 = new string('b', 64),
                ConfigSnapshotPath = "snapshot",
                ConfigSnapshotManifestPath = "manifest.json",
                ConfigSnapshotManifestSha256 = new string('c', 64),
                ConfigSnapshotSchemaVersion = 2
            };
            var authority = new DurableRelaunchPermitRecord
            {
                SessionId = sessionId,
                State = DurableRelaunchPermitState.Approved,
                Generation = 4,
                PermitId = Guid.NewGuid().ToString("N"),
                PermitNonce = Guid.NewGuid().ToString("N")
            };

            Assert(WatchdogRecoveryReadinessPolicy.CanPrepareCrashSafetyHandoff(
                    seed,
                    sessionId,
                    DurableRelaunchProcessObservation.Dead,
                    authority),
                "精确退出证明、恢复种子和许可未能授权安全代理");
            Assert(!WatchdogRecoveryReadinessPolicy.CanPrepareCrashSafetyHandoff(
                       seed,
                       sessionId,
                       DurableRelaunchProcessObservation.Alive,
                       authority) &&
                   !WatchdogRecoveryReadinessPolicy.CanPrepareCrashSafetyHandoff(
                       seed,
                       Guid.NewGuid().ToString("N"),
                       DurableRelaunchProcessObservation.Dead,
                       authority),
                "存活旧进程或错误会话仍能合成强杀恢复回执");

            var handoff = new WatchdogSafetyHandoffReceipt
            {
                SchemaVersion = 5,
                State = WatchdogSafetyHandoffState.Completed,
                Stage = WatchdogSafetyStage.Completed,
                MotorsOff = true,
                PowerOff = true,
                PressureSafe = true,
                PersistenceDrained = false,
                LogicalQuiescent = true,
                HardwareResourcesReleased = true,
                ExecutionAuthorizationRevoked = true,
                CallbacksIsolated = true,
                CrashRecovery = true,
                OldProcessExitProven = true,
                OldProcessId = 41,
                OldProcessStartUtcTicks = 4100,
                OldProcessObservation = DurableRelaunchProcessObservation.Dead,
                OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks,
                OldProcessExitEvidenceOwner = WatchdogSafetyEvidenceOwner.SupervisorService,
                OldProcessExitEvidenceSource = "TestSupervisor",
                DataAuditState = WatchdogDataAuditState.CrashRepairRequired
            };
            Assert(WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(handoff),
                "强杀恢复的物理安全证据未接受启动后数据修复");
            handoff.OldProcessExitProven = false;
            Assert(!WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(handoff),
                "缺少旧进程退出证明仍绕过了PersistenceDrained要求");
        }

        private static void ExecutableSha256ComparisonIsCanonical()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "mttftest-watchdog-hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "agent.exe");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
                var sha256 = SupervisorProtocol.ComputeSha256(path);
                Assert(WatchdogHost.ExecutableSha256Matches(path, sha256),
                    "大写SHA256未匹配原文件");
                Assert(WatchdogHost.ExecutableSha256Matches(
                        path,
                        sha256.ToLowerInvariant()),
                    "相同SHA256因十六进制大小写被误判为文件变化");
                File.AppendAllText(path, "changed");
                Assert(!WatchdogHost.ExecutableSha256Matches(path, sha256),
                    "内容变化仍通过SHA256校验");
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        private static void SupervisorAuthorityPathMatchesJournalProjectRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "mttftest-supervisor-root-" + Guid.NewGuid().ToString("N"));
            var journal = Path.Combine(root, "WatchdogSessions");
            var sibling = Path.Combine(
                Path.GetDirectoryName(root) ?? Path.GetTempPath(),
                "mttftest-supervisor-other-" + Guid.NewGuid().ToString("N"),
                "WatchdogSessions");
            Assert(
                SupervisorServiceRuntime.AreEquivalentProjectRoots(root, journal),
                "项目根与其WatchdogSessions目录被误判为不同授权路径");
            Assert(
                SupervisorServiceRuntime.AreEquivalentProjectRoots(
                    root.ToUpperInvariant(),
                    journal.ToLowerInvariant()),
                "同一项目根因路径大小写差异被误拒绝");
            Assert(
                !SupervisorServiceRuntime.AreEquivalentProjectRoots(root, sibling),
                "不同项目的WatchdogSessions目录错误通过授权路径校验");
        }

        private static void RecoverySafetyProofRequiresCompleteEvidence()
        {
            var closing = new WatchdogClosingTombstone
            {
                SchemaVersion = 5,
                ExitDisposition = WatchdogExitDisposition.TakeoverReplacementExit,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                TakeoverTransactionId = Guid.NewGuid().ToString("N"),
                RelaunchPermitGeneration = 9,
                RelaunchPermitId = Guid.NewGuid().ToString("N"),
                RelaunchPermitNonceSha256 = new string('A', 64),
                State = WatchdogClosingTombstoneState.Terminal,
                SafetyStage = WatchdogClosingSafetyStage.Terminal,
                FinalSafetyResultCommitted = true,
                MotorsOff = true,
                PowerOff = true,
                PressureSafe = true,
                PersistenceDrained = true,
                LogicalQuiescent = true,
                OldProcessExitProven = true,
                OldProcessId = 42,
                OldProcessStartUtcTicks = 4200,
                OldProcessObservation = DurableRelaunchProcessObservation.Dead,
                OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks,
                OldProcessExitEvidenceOwner = WatchdogSafetyEvidenceOwner.SupervisorService,
                OldProcessExitEvidenceSource = "TestSupervisor",
                DataAuditState = WatchdogDataAuditState.Drained
            };
            Assert(
                WatchdogRecoveryReadinessPolicy.IsCompleteStopProof(closing),
                "完整StopCompleted关闭围栏未被接受为无Handoff替代证明");
            WatchdogSafetyHandoffWaitOutcome outcome;
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    null,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: false,
                    configSnapshotValid: false,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.NotRequired,
                "未声明Handoff的完整StopCompleted未形成NotRequired结果");
            closing.PersistenceDrained = false;
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsCompleteStopProof(closing),
                "持久化未闭合的StopCompleted围栏错误放行");
            closing.PersistenceDrained = true;
            closing.SchemaVersion = 3;
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsCompleteStopProof(closing),
                "缺少强类型Permit身份的旧schema错误放行接管续跑");

            var handoff = new WatchdogSafetyHandoffReceipt
            {
                State = WatchdogSafetyHandoffState.Completed,
                Stage = WatchdogSafetyStage.Completed,
                MotorsOff = true,
                PowerOff = true,
                PressureSafe = true,
                PersistenceDrained = true,
                LogicalQuiescent = true,
                HardwareResourcesReleased = true,
                ExecutionAuthorizationRevoked = true,
                CallbacksIsolated = true
            };
            Assert(
                WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(handoff),
                "完整Handoff安全证明未放行");
            handoff.CallbacksIsolated = false;
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(handoff),
                "回调未隔离的Handoff错误放行");
            handoff.CallbacksIsolated = true;
            handoff.State = WatchdogSafetyHandoffState.Failed;
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(handoff),
                "Failed Handoff错误放行");

            closing.SchemaVersion = 5;
            closing.SafetyHandoffId = Guid.NewGuid().ToString("N");
            handoff.State = WatchdogSafetyHandoffState.Accepted;
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    null,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: false,
                    configSnapshotValid: false,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                "已声明Handoff但回执缺失未安全阻断");
            Assert(
                !WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: true,
                    configSnapshotValid: true,
                    timedOut: false,
                    out outcome) &&
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: true,
                    configSnapshotValid: true,
                    timedOut: true,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.TimedOut,
                "非终态Handoff未等待或90秒超时未结构化为TimedOut");
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: false,
                    configSnapshotValid: true,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                "Handoff身份不匹配未安全阻断");
            handoff.State = WatchdogSafetyHandoffState.Failed;
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: true,
                    configSnapshotValid: true,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.Failed,
                "Failed Handoff未形成Failed结果");
            handoff.State = WatchdogSafetyHandoffState.Completed;
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: true,
                    configSnapshotValid: false,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.MissingOrCorrupt,
                "配置快照损坏未安全阻断");
            Assert(
                WatchdogRecoveryReadinessPolicy.TryResolveSafetyPrerequisite(
                    closing,
                    handoff,
                    exactClosingPermitBinding: true,
                    exactReceiptIdentity: true,
                    configSnapshotValid: true,
                    timedOut: false,
                    out outcome) &&
                outcome == WatchdogSafetyHandoffWaitOutcome.Completed,
                "完整Handoff未形成Completed结果");
        }

        private static void RecoverySlaAndBackoffPolicyAreExact()
        {
            Assert(
                WatchdogRecoveryReadinessPolicy.InitialLaunchSlaSeconds == 5 &&
                WatchdogRecoveryReadinessPolicy.RecoveryAttachSlaSeconds == 15 &&
                WatchdogRecoveryReadinessPolicy.SafetyHandoffDeadlineSeconds == 90,
                "恢复SLA常量不是5秒拉起/15秒附着/90秒Handoff");
            Assert(
                !WatchdogRecoveryReadinessPolicy.ShouldApplyProcessBackoff(
                    initialSafetyReplacement: true,
                    launchOrdinal: 1) &&
                WatchdogRecoveryReadinessPolicy.ShouldApplyProcessBackoff(
                    initialSafetyReplacement: true,
                    launchOrdinal: 2) &&
                WatchdogRecoveryReadinessPolicy.ShouldApplyProcessBackoff(
                    initialSafetyReplacement: false,
                    launchOrdinal: 1),
                "初次安全替换或后续失败的退避策略错误");

            const long frequency = 1000;
            const long ready = 10000;
            Assert(
                !WatchdogRecoveryReadinessPolicy.IsSlaExceeded(
                    ready,
                    ready + 5000,
                    frequency,
                    WatchdogRecoveryReadinessPolicy.InitialLaunchSlaSeconds) &&
                WatchdogRecoveryReadinessPolicy.IsSlaExceeded(
                    ready,
                    ready + 5001,
                    frequency,
                    WatchdogRecoveryReadinessPolicy.InitialLaunchSlaSeconds) &&
                !WatchdogRecoveryReadinessPolicy.IsSlaExceeded(
                    ready,
                    ready + 15000,
                    frequency,
                    WatchdogRecoveryReadinessPolicy.RecoveryAttachSlaSeconds) &&
                WatchdogRecoveryReadinessPolicy.IsSlaExceeded(
                    ready,
                    ready + 15001,
                    frequency,
                    WatchdogRecoveryReadinessPolicy.RecoveryAttachSlaSeconds),
                "可注入单调时钟下的5秒/15秒边界判定错误");
        }

        private static void ClosingFenceSuppressesRelaunchWithoutPrematureTerminal()
        {
            var closing = new WatchdogClosingTombstone
            {
                SessionId = Guid.NewGuid().ToString("N"),
                SessionGeneration = 7,
                SessionLease = 11,
                StateVersion = 1,
                State = WatchdogClosingTombstoneState.Closing
            };
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: false,
                    closing: closing,
                    activeConnection: true,
                    currentProcessAlive: true) ==
                WatchdogCloseFenceAction.SuppressRelaunch,
                "活动NamedPipe和存活owner遇到Closing时错误发布终态");
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: false,
                    closing: closing,
                    activeConnection: false,
                    currentProcessAlive: true) ==
                WatchdogCloseFenceAction.SuppressRelaunch,
                "断管后的Closing意图被错误升级为无证明终态");
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: false,
                    closing: closing,
                    activeConnection: true,
                    currentProcessAlive: false) ==
                WatchdogCloseFenceAction.SuppressRelaunch,
                "owner退出后的Closing意图被错误升级为无证明终态");

            closing.State = WatchdogClosingTombstoneState.Terminal;
            closing.SafetyStage = WatchdogClosingSafetyStage.Terminal;
            closing.MotorsOff = true;
            closing.PowerOff = true;
            closing.PressureSafe = true;
            closing.PersistenceDrained = true;
            closing.LogicalQuiescent = true;
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: false,
                    closing: closing,
                    activeConnection: true,
                    currentProcessAlive: true) ==
                WatchdogCloseFenceAction.TerminateSession,
                "Terminal tombstone未结束Sidecar");
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: true,
                    closing: null,
                    activeConnection: true,
                    currentProcessAlive: true) ==
                WatchdogCloseFenceAction.TerminateSession,
                "legacy revocation未保持立即终态兼容语义");
            Assert(
                WatchdogHost.EvaluateCloseFenceAction(
                    legacyRevoked: false,
                    closing: null,
                    activeConnection: true,
                    currentProcessAlive: true) ==
                WatchdogCloseFenceAction.None,
                "无关闭围栏时错误抑制正常会话");
        }

        private static void LastVerifiedActiveRunSurvivesCleanupHeartbeat()
        {
            var runId = Guid.NewGuid().ToString("N");
            var journal = new WatchdogJournal
            {
                SessionId = "verified-active-run",
                CurrentPid = 701,
                CurrentProcessStartUtcTicks = 7001,
                LastCheckpointMirror = new WatchdogCheckpointMirror
                {
                    Armed = true,
                    SessionId = "verified-active-run",
                    RunId = runId,
                    RunEpoch = 17,
                    Revision = 9,
                    Sha256 = "checkpoint-sha",
                    SelectedChannels = new[] { 4, 5, 11, 12 }
                }
            };
            var active = new WatchdogHeartbeat
            {
                Sequence = 88,
                ProcessId = 701,
                ProcessStartUtcTicks = 7001,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = runId,
                RunEpoch = 17,
                RunActive = true,
                EnabledChannels = new[] { 4, 5, 11, 12 },
                RecoveryEligibleChannels = new[] { 4, 5, 11 },
                CompletedChannels = new[] { 12 },
                ManuallyDisabledChannels = new[] { 5 },
                PermanentAlarmedChannels = new[] { 11 }
            };
            Assert(WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       active,
                       identityValidated: true) &&
                   journal.LastVerifiedActiveRun.SelectedChannels.SequenceEqual(
                       new[] { 4, 5, 11, 12 }) &&
                   journal.LastVerifiedActiveRun.CheckpointRevision == 9,
                "有效InitialProcess活动运行没有保存匹配checkpoint身份");

            var cleanup = new WatchdogHeartbeat
            {
                Sequence = 89,
                ProcessId = 701,
                ProcessStartUtcTicks = 7001,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = string.Empty,
                RunEpoch = 0,
                RunActive = false
            };
            journal.LastHeartbeat = cleanup;
            Assert(!WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       cleanup,
                       identityValidated: true),
                "StopAll空RunId心跳覆盖了最后有效活动运行");
            var recoveryChild = new WatchdogHeartbeat
            {
                Sequence = 90,
                ProcessId = 702,
                ProcessStartUtcTicks = 7002,
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = runId,
                RunEpoch = 18,
                RunActive = true,
                ManuallyDisabledChannels = Enumerable.Range(1, 12).ToArray()
            };
            Assert(!WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       recoveryChild,
                       identityValidated: true),
                "Recovery child污染了InitialProcess活动运行快照");
            journal.CurrentPid = 702;
            journal.CurrentProcessStartUtcTicks = 7002;
            journal.LastHeartbeat = recoveryChild;
            var resolution = WatchdogRecoveryChannelIntentPolicy.Resolve(journal);
            Assert(resolution.Succeeded &&
                   resolution.ExcludedChannels.SequenceEqual(new[] { 5, 11, 12 }) &&
                   journal.FrozenExcludedSourceRunId == runId &&
                   journal.FrozenExcludedSourceRunEpoch == 17,
                "清场空RunId后首次拉起未优先使用LastVerifiedActiveRun");

            var fallback = new WatchdogJournal
            {
                SessionId = "checkpoint-fallback",
                CurrentPid = 801,
                CurrentProcessStartUtcTicks = 8001,
                LastCheckpointMirror = new WatchdogCheckpointMirror
                {
                    Armed = true,
                    SessionId = "checkpoint-fallback",
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 3,
                    SelectedChannels = new[] { 4, 5 }
                }
            };
            Assert(WatchdogRecoveryChannelIntentPolicy.Resolve(fallback).Succeeded,
                "无活动快照时未使用身份匹配且Armed的checkpoint mirror回退");
        }

        private static void ProductionOrchestratorUsesInjectedPorts()
        {
            var store = new InMemoryDurableRelaunchStore();
            var launcher = new CountingLauncher();
            var orchestrator = new HostRelaunchOrchestrator(
                "host-production-seam",
                store,
                3,
                launcher);
            var request = new DurableRelaunchRequest
            {
                Fingerprint = "host-fingerprint",
                ProgressToken = "daq-stage-1",
                ProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = "run-host",
                RecoveryStage = "DaqRecovery",
                MaximumProcessRelaunches = 3
            };
            var approvals = new DurableRelaunchResult[64];
            System.Threading.Tasks.Parallel.For(0, approvals.Length, index =>
            {
                approvals[index] = orchestrator.ApproveOrGetExisting(request);
            });
            Assert(approvals.Count(item => item.Succeeded && !item.Existing) == 1,
                "Host生产编排在并发failure下批准了多个permit");
            var approved = orchestrator.Snapshot;
            var begins = new DurableRelaunchResult[64];
            System.Threading.Tasks.Parallel.For(0, begins.Length, index =>
            {
                begins[index] = orchestrator.BeginLaunch(approved.Identity);
            });
            Assert(begins.Count(item => item.ActionAllowed) == 1,
                "Host生产编排重复释放了BeginLaunch动作");
            var startInfo = new ProcessStartInfo { FileName = "injected-test-launcher" };
            orchestrator.Start(startInfo);
            Assert(launcher.StartCount == 1, "注入Process launcher没有被生产编排使用");
            Assert(orchestrator.CommitStarted(approved.Identity, 301, 3001).ActionAllowed,
                "Host生产编排没有提交Started身份");
            Assert(orchestrator.CommitAttached(approved.Identity, 301, 3001).ActionAllowed,
                "Host生产编排没有提交Attached身份");
            Assert(orchestrator.CommitRecoveryBatch(
                       approved.Identity,
                       "run-host",
                       "daq-stage-2",
                       2).ActionAllowed &&
                   orchestrator.Snapshot.State == DurableRelaunchPermitState.Committed,
                "Host生产编排没有以RecoveryBatch提交Committed");
        }

        private static void RecoveryChannelIntentIsFrozenAndDurable()
        {
            var journal = new WatchdogJournal
            {
                CurrentPid = 101,
                CurrentProcessStartUtcTicks = 1001,
                RelaunchState = DurableRelaunchPermitState.Approved.ToString(),
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 101,
                    ProcessStartUtcTicks = 1001,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 7,
                    RunActive = true,
                    ManuallyDisabledChannels = new[] { 4, 2, 4 },
                    CompletedChannels = new[] { 1 },
                    PermanentAlarmedChannels = new[] { 12, 2 }
                }
            };
            var persistenceCalls = 0;
            var first = WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                journal,
                () =>
                {
                    persistenceCalls++;
                    return true;
                });
            Assert(first.Succeeded && first.NewlyFrozen &&
                   first.ExcludedChannels.SequenceEqual(new[] { 1, 2, 4, 12 }) &&
                   journal.RecoveryChannelIntentFrozen &&
                   journal.FrozenExcludedSourceProcessId == 101 &&
                   journal.FrozenExcludedSourceProcessStartUtcTicks == 1001 &&
                   journal.FrozenExcludedSourceRunEpoch == 7 &&
                   persistenceCalls == 1,
                "初始进程恢复通道意图未排序去重并冻结完整身份。");

            var serializer = new JavaScriptSerializer();
            var reloaded = serializer.Deserialize<WatchdogJournal>(
                serializer.Serialize(journal));
            reloaded.CurrentPid = 202;
            reloaded.CurrentProcessStartUtcTicks = 2002;
            reloaded.LastHeartbeat = new WatchdogHeartbeat
            {
                ProcessId = 202,
                ProcessStartUtcTicks = 2002,
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = journal.LastHeartbeat.RunId,
                RunEpoch = 8,
                ManuallyDisabledChannels = Enumerable.Range(1, 12).ToArray(),
                CompletedChannels = Enumerable.Range(1, 12).ToArray(),
                PermanentAlarmedChannels = Enumerable.Range(1, 12).ToArray()
            };
            var child = WatchdogRecoveryChannelIntentPolicy.Resolve(reloaded);
            Assert(child.Succeeded && !child.NewlyFrozen &&
                   child.ExcludedChannels.SequenceEqual(new[] { 1, 2, 4, 12 }),
                "恢复子进程全禁用心跳污染了冻结通道集合，或Sidecar重载丢失集合。");

            var empty = new WatchdogJournal
            {
                CurrentPid = 303,
                CurrentProcessStartUtcTicks = 3003,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 303,
                    ProcessStartUtcTicks = 3003,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 1,
                    RunActive = true
                }
            };
            var emptyResult = WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                empty,
                () => true);
            Assert(emptyResult.Succeeded && emptyResult.NewlyFrozen &&
                   empty.RecoveryChannelIntentFrozen &&
                   emptyResult.ExcludedChannels.Length == 0,
                "空排除集合没有用冻结标志持久区分。");

            var persistenceFailure = new WatchdogJournal
            {
                CurrentPid = 505,
                CurrentProcessStartUtcTicks = 5005,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 505,
                    ProcessStartUtcTicks = 5005,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 3,
                    RunActive = true
                }
            };
            var failedPersistenceCalls = 0;
            var failedPersistence =
                WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                    persistenceFailure,
                    () =>
                    {
                        failedPersistenceCalls++;
                        return false;
                    });
            Assert(!failedPersistence.Succeeded &&
                   failedPersistence.FailureReason ==
                   "FrozenExcludedChannelsPersistenceFailed" &&
                   failedPersistenceCalls == 1,
                "冻结集合同步持久化失败未阻断首个launch intent。");

            reloaded.FrozenExcludedChannels = new[] { 1, 1, 13 };
            Assert(!WatchdogRecoveryChannelIntentPolicy.Resolve(reloaded).Succeeded,
                "非法、重复或越界的冻结集合未fail closed。");

            var missing = new WatchdogJournal
            {
                CurrentPid = 404,
                CurrentProcessStartUtcTicks = 4004,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 404,
                    ProcessStartUtcTicks = 4004,
                    RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 2,
                    RunActive = true
                }
            };
            var missingResult = WatchdogRecoveryChannelIntentPolicy.Resolve(missing);
            Assert(!missingResult.Succeeded &&
                   missingResult.FailureReason ==
                   "FrozenExcludedChannelsInitialSourceInvalid",
                "恢复代次缺失冻结证据时未fail closed。");

            missing.LastHeartbeat.RecoveryProcessSource =
                RecoveryFailurePolicy.InitialProcessSource;
            missing.RelaunchState = DurableRelaunchPermitState.Started.ToString();
            var startedWithoutFreeze =
                WatchdogRecoveryChannelIntentPolicy.Resolve(missing);
            Assert(!startedWithoutFreeze.Succeeded &&
                   startedWithoutFreeze.FailureReason ==
                   "FrozenExcludedChannelsMissingAfterRecoveryStarted",
                "已进入Started代次但缺失冻结证据时未fail closed。");
        }

        private static void ProductionTakeoverPipelineIsMonotonic()
        {
            var coordinator = new TakeoverTransactionCoordinator();
            Assert(coordinator.TryBegin("host-takeover", "authority", out var lease),
                "Host生产接管事务无法创建");
            var events = new List<string>();
            var result = AutomaticTakeoverStageExecutor.ExecuteAsync(
                    coordinator,
                    lease,
                    () => coordinator.IsAuthorized(lease),
                    () => { events.Add("dump"); return System.Threading.Tasks.Task.CompletedTask; },
                    () => { events.Add("permit"); return 17; },
                    () => { events.Add("terminate"); return true; },
                    permit =>
                    {
                        events.Add("launch:" + permit);
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    (current, requested, failure) =>
                        events.Add($"rejected:{current}:{requested}:{failure}"))
                .GetAwaiter()
                .GetResult();
            Assert(result.Succeeded && result.PermitGeneration == 17 &&
                   lease.Stage == TakeoverTransactionStage.Relaunching &&
                   events.SequenceEqual(new[] { "dump", "permit", "terminate", "launch:17" }),
                "生产接管没有按Dump→Permit→Terminate→Launch执行：" +
                string.Join(",", events));
            coordinator.Complete(lease);
        }

        private static void ProductionTakeoverRegressionIsObservable()
        {
            var coordinator = new TakeoverTransactionCoordinator();
            Assert(coordinator.TryBegin("host-regression", "authority", out var lease) &&
                   coordinator.TryAdvance(lease, TakeoverTransactionStage.RelaunchPermit),
                "阶段回退测试无法建立错误前态");
            var actions = 0;
            TakeoverTransactionStage current = 0;
            TakeoverTransactionStage requested = 0;
            var result = AutomaticTakeoverStageExecutor.ExecuteAsync(
                    coordinator,
                    lease,
                    () => true,
                    () => { Interlocked.Increment(ref actions); return System.Threading.Tasks.Task.CompletedTask; },
                    () => { Interlocked.Increment(ref actions); return 1; },
                    () => { Interlocked.Increment(ref actions); return true; },
                    _ => { Interlocked.Increment(ref actions); return System.Threading.Tasks.Task.CompletedTask; },
                    (observed, attempted, _) =>
                    {
                        current = observed;
                        requested = attempted;
                    })
                .GetAwaiter()
                .GetResult();
            Assert(!result.Succeeded && actions == 0 &&
                   current == TakeoverTransactionStage.RelaunchPermit &&
                   requested == TakeoverTransactionStage.DumpCapture,
                "生产接管阶段30→20未明确拒绝或仍执行了副作用");
            coordinator.Complete(lease);
        }

        private sealed class CountingLauncher : ISystemRelaunchProcessLauncher
        {
            internal int StartCount;

            public Process Start(ProcessStartInfo startInfo)
            {
                Interlocked.Increment(ref StartCount);
                return null;
            }

            public void KillExact(Process process) { }
        }

        private static void BootstrapIsSchema4AndSingleUse()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "mttf-watchdog-bootstrap-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var startTicks = process.StartTime.ToUniversalTime().Ticks;
                    Assert(
                        WatchdogJournalBootstrap.TryCreateNew(
                            directory,
                            "session-bootstrap",
                            process.Id,
                            startTicks,
                            out var json,
                            out var error),
                        "schema4 bootstrap失败：" + error);
                    Assert(json.Contains("\"SchemaVersion\":" +
                               WatchdogJournalPolicy.CurrentSchemaVersion) &&
                           json.Contains("\"RelaunchState\":\"None\"") &&
                           json.Contains("\"BootstrapRevision\":1"),
                        "bootstrap没有生成schema4/None/revision1权威快照");
                    Assert(!WatchdogJournalBootstrap.TryCreateNew(
                               directory,
                               "session-bootstrap",
                               process.Id,
                               startTicks,
                               out _,
                               out error) &&
                           error == "BootstrapSessionAlreadyExists",
                        "同Session bootstrap允许覆盖既有快照");
                }
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static void InvalidBootstrapFailsClosed()
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory);
            Assert(!WatchdogJournalBootstrap.TryCreateNew(
                       root,
                       "session-invalid-bootstrap",
                       0,
                       0,
                       out _,
                       out var error) &&
                   error == "BootstrapIdentityInvalid",
                "非法bootstrap身份没有拒绝启动");
        }

        private static void Run(string name, Action test, ref int passed)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                throw;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
