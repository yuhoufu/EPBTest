using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class DurableRelaunchCoordinatorTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("schema4同一scope64并发只批准一个permit", ConcurrentApproveIsSinglePermit, ref passed);
            Run("schema4每个崩溃点从最后成功JSON恢复", CrashPointsRecoverFromLastSuccessfulJson, ref passed);
            Run("LaunchIntent重启后立即阻断", LaunchIntentAfterRestartBlocks, ref passed);
            Run("Started存活不重复计数且死亡只计一次", StartedAliveAndDeadAreReconciledOnce, ref passed);
            Run("PID复用与OutcomeUnknown故障闭锁", PidReuseAndUnknownOutcomeBlock, ref passed);
            Run("同步持久化失败不返回动作并内存闭锁", CommitFailureFailsClosed, ref passed);
            Run("schema2/schema3与缺失损坏非法记录安全迁移", LegacyAndInvalidJournalMigration, ref passed);
            Run("预算达到边界后token变化仍阻断", BudgetBoundaryIgnoresTokenChange, ref passed);
            return passed;
        }

        private static void ConcurrentApproveIsSinglePermit()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-concurrent", store, 3);
            var request = Request("fp-concurrent", "token-1", 3);
            var results = new DurableRelaunchResult[64];
            Parallel.For(0, results.Length, index =>
            {
                results[index] = coordinator.ApproveOrGetExisting(request);
            });
            Assert(results.Count(result => result.Succeeded && !result.Existing) == 1,
                "64并发没有线性化为一个Approved permit");
            Assert(results.All(result => result.Succeeded) &&
                   results.Select(result => result.Record.PermitId).Distinct().Count() == 1,
                "并发approve返回了多个permit identity");

            var identity = coordinator.Snapshot.Identity;
            var begin = new DurableRelaunchResult[64];
            Parallel.For(0, begin.Length, index =>
            {
                begin[index] = coordinator.BeginLaunch(identity);
            });
            Assert(begin.Count(result => result.ActionAllowed) == 1 &&
                   begin.All(result => result.Succeeded),
                "64并发BeginLaunch重复执行了真实启动动作");

            var close = new DurableRelaunchResult[64];
            Parallel.For(0, close.Length, index =>
            {
                close[index] = coordinator.ClosePermitAndRegisterFailure(
                    identity,
                    "ConcurrentFailure",
                    "same incident");
            });
            Assert(close.Count(result => result.Succeeded && !result.Existing) == 1 &&
                   close.All(result => result.Succeeded) &&
                   coordinator.Snapshot.State == DurableRelaunchPermitState.Failed &&
                   coordinator.Snapshot.ConsecutiveFailures == 1,
                "64并发Close没有线性化为一次失败登记");
        }

        private static void CrashPointsRecoverFromLastSuccessfulJson()
        {
            var store = new InMemoryDurableRelaunchStore();
            var request = Request("fp-crash", "token-crash", 4);
            var coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            var approved = coordinator.ApproveOrGetExisting(request);
            Assert(approved.ActionAllowed, "Approve未提交成功");
            var identity = approved.Record.Identity;

            coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            Assert(coordinator.BeginLaunch(identity).ActionAllowed, "LaunchIntent未提交成功");
            coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            Assert(coordinator.CommitStarted(identity, 701, 7001).ActionAllowed,
                "Started未提交成功");
            coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            Assert(coordinator.CommitAttached(identity, 701, 7001).ActionAllowed,
                "Attached未提交成功");
            coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            Assert(coordinator.CommitRecoveryBatch(identity, "run-crash", "token-commit", 1).ActionAllowed,
                "Committed未提交成功");
            coordinator = new DurableRelaunchCoordinator("session-crash", store, 4);
            Assert(coordinator.Snapshot.State == DurableRelaunchPermitState.Committed &&
                   coordinator.Snapshot.RecoveryCommitGeneration == 1,
                "最后成功JSON重建后没有保留Committed状态");
        }

        private static void LaunchIntentAfterRestartBlocks()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-intent", store, 3);
            var approved = coordinator.ApproveOrGetExisting(Request("fp-intent", "token", 3));
            Assert(coordinator.BeginLaunch(approved.Record.Identity).ActionAllowed,
                "LaunchIntent没有成功落盘");
            var restarted = new DurableRelaunchCoordinator("session-intent", store, 3);
            var result = restarted.RecoverAfterRestart((pid, ticks) =>
                DurableRelaunchProcessObservation.Unknown);
            Assert(result.Blocked &&
                   restarted.Snapshot.State == DurableRelaunchPermitState.Blocked,
                "LaunchIntent重启后没有按OutcomeUnknown闭锁");
        }

        private static void StartedAliveAndDeadAreReconciledOnce()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-started", store, 3);
            var approved = coordinator.ApproveOrGetExisting(Request("fp-started", "token", 3));
            coordinator.BeginLaunch(approved.Record.Identity);
            coordinator.CommitStarted(approved.Record.Identity, 702, 7002);

            var alive = new DurableRelaunchCoordinator("session-started", store, 3);
            Assert(alive.RecoverAfterRestart((pid, ticks) =>
                       DurableRelaunchProcessObservation.Alive).Succeeded &&
                   alive.Snapshot.State == DurableRelaunchPermitState.Started &&
                   alive.Snapshot.ConsecutiveFailures == 0,
                "Started存活探测错误计数或改变状态");

            var dead = new DurableRelaunchCoordinator("session-started", store, 3);
            Assert(dead.RecoverAfterRestart((pid, ticks) =>
                       DurableRelaunchProcessObservation.Dead).Succeeded &&
                   dead.Snapshot.State == DurableRelaunchPermitState.Failed &&
                   dead.Snapshot.ConsecutiveFailures == 1,
                "Started死亡没有只计一次失败");
            var repeated = new DurableRelaunchCoordinator("session-started", store, 3);
            repeated.RecoverAfterRestart((pid, ticks) =>
                DurableRelaunchProcessObservation.Dead);
            Assert(repeated.Snapshot.State == DurableRelaunchPermitState.Failed &&
                   repeated.Snapshot.ConsecutiveFailures == 1,
                "同一死亡结果重启后重复增加失败预算");
        }

        private static void PidReuseAndUnknownOutcomeBlock()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-reuse", store, 3);
            var approved = coordinator.ApproveOrGetExisting(Request("fp-reuse", "token", 3));
            coordinator.BeginLaunch(approved.Record.Identity);
            coordinator.CommitStarted(approved.Record.Identity, 703, 7003);
            var result = coordinator.RecoverAfterRestart((pid, ticks) =>
                DurableRelaunchProcessObservation.IdentityMismatch);
            Assert(result.Blocked && coordinator.Snapshot.State == DurableRelaunchPermitState.Blocked,
                "PID复用没有直接进入Blocked");

            var unknownStore = new InMemoryDurableRelaunchStore();
            var unknown = new DurableRelaunchCoordinator("session-unknown", unknownStore, 3);
            var unknownApproval = unknown.ApproveOrGetExisting(Request("fp-unknown", "token", 3));
            unknown.BeginLaunch(unknownApproval.Record.Identity);
            unknown.CommitStarted(unknownApproval.Record.Identity, 704, 7004);
            var unknownResult = unknown.RecoverAfterRestart((pid, ticks) =>
                DurableRelaunchProcessObservation.Unknown);
            Assert(unknownResult.Blocked && unknown.Snapshot.State == DurableRelaunchPermitState.Blocked,
                "OutcomeUnknown没有闭锁");
        }

        private static void CommitFailureFailsClosed()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-fail", store, 3);
            store.FailNextCommit();
            var result = coordinator.ApproveOrGetExisting(Request("fp-fail", "token", 3));
            Assert(!result.Succeeded && result.Blocked && !result.ActionAllowed &&
                   coordinator.Snapshot.State == DurableRelaunchPermitState.Blocked,
                "Approve持久化失败后仍返回了可执行动作");
        }

        private static void LegacyAndInvalidJournalMigration()
        {
            var v2Blocked = WatchdogJournalV4Validator.ValidateAndMigrate(
                "{\"SchemaVersion\":2,\"SessionId\":\"legacy\",\"RecoveryBlocked\":true,\"RelaunchGeneration\":9}",
                "session-migrate");
            Assert(v2Blocked.Migrated && v2Blocked.Blocked &&
                   v2Blocked.Record.SchemaVersion == WatchdogJournalPolicy.CurrentSchemaVersion,
                "schema2 blocked迁移丢失阻断状态");

            var v3Pristine = WatchdogJournalV4Validator.ValidateAndMigrate(
                "{\"SchemaVersion\":3,\"SessionId\":\"legacy\",\"RecoveryAttempt\":0}",
                "session-migrate");
            Assert(v3Pristine.Migrated &&
                   v3Pristine.Record.State == DurableRelaunchPermitState.None,
                "pristine schema3没有迁移到None");

            var v3Ambiguous = WatchdogJournalV4Validator.ValidateAndMigrate(
                "{\"SchemaVersion\":3,\"SessionId\":\"legacy\",\"RecoveryAttempt\":1,\"CurrentPid\":77}",
                "session-migrate");
            Assert(v3Ambiguous.Blocked &&
                   v3Ambiguous.Record.LastFailureCode == "AmbiguousLegacy",
                "带启动迹象的legacy没有闭锁");

            Assert(WatchdogJournalV4Validator.ValidateAndMigrate(null, "session-migrate").Record.State ==
                   DurableRelaunchPermitState.None, "缺失journal没有安全初始化None");
            Assert(WatchdogJournalV4Validator.ValidateAndMigrate("{bad", "session-migrate").Blocked,
                "损坏JSON没有闭锁");
            Assert(WatchdogJournalV4Validator.ValidateAndMigrate(
                       "{\"SchemaVersion\":5}", "session-migrate").Blocked,
                "未知schema没有闭锁");
            Assert(WatchdogJournalV4Validator.ValidateAndMigrate(
                       "{\"SchemaVersion\":4,\"SessionId\":\"other\",\"State\":0}",
                       "session-migrate").Blocked,
                "Session mismatch没有闭锁");
            Assert(WatchdogJournalV4Validator.ValidateAndMigrate(
                       "{\"SchemaVersion\":4,\"SessionId\":\"session-migrate\",\"State\":99}",
                       "session-migrate").Blocked,
                "非法state没有闭锁");
        }

        private static void BudgetBoundaryIgnoresTokenChange()
        {
            var store = new InMemoryDurableRelaunchStore();
            var coordinator = new DurableRelaunchCoordinator("session-budget", store, 1);
            var first = coordinator.ApproveOrGetExisting(Request("fp-budget", "token-1", 1));
            coordinator.BeginLaunch(first.Record.Identity);
            var failure = coordinator.ClosePermitAndRegisterFailure(
                first.Record.Identity, "StartupFailed", "first");
            Assert(failure.Blocked && coordinator.Snapshot.State == DurableRelaunchPermitState.Blocked,
                "预算边界失败没有阻断");
            var changed = coordinator.ApproveOrGetExisting(Request("fp-budget", "token-2", 1));
            Assert(changed.Blocked && !changed.ActionAllowed,
                "token变化错误地重新打开了已耗尽预算");
        }

        private static DurableRelaunchRequest Request(
            string fingerprint,
            string token,
            int maximum) => new DurableRelaunchRequest
            {
                Fingerprint = fingerprint,
                ProgressToken = token,
                ProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = "run-" + fingerprint,
                RecoveryStage = "Startup",
                MaximumProcessRelaunches = maximum
            };

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
