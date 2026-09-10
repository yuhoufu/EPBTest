using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;

namespace AdaptiveControlTests
{
    internal static class CycleAttemptLifecycleTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Recorder Begin返回前圈尝试已可见", AttemptVisibleBeforeRecorderBeginReturns, ref passed);
            Run("百路Complete Abort Alarm竞争只提交一次", HundredTerminalRacersCommitOnce, ref passed);
            Run("旧尝试迟到终态不得移除新尝试", StaleTerminalCannotRemoveNewAttempt, ref passed);
            Run("旧峰值取消不得影响下一尝试令牌", PeakAbortDoesNotAffectNextAttempt, ref passed);
            Run("持久化失败保留context并允许恢复重试", PersistenceFailureKeepsContext, ref passed);
            Run("完成回调内作废被拒绝且返回失败后可精确作废", AbortAfterFailedCompleteReleasesClaim, ref passed);
            Run("Manager完成入口按原attempt匹配作废标记", ManagerCompletionUsesOriginalAbortIdentity, ref passed);
            Run("重入诊断复位失败也不得清除正式恢复记录", RestartDiagnosticResetKeepsRecoveryEvidence, ref passed);
            Run("耐久收口仅消费匹配运行和圈号的等待记录", DurableTerminalRemovesOnlyMatchingPendingCycle, ref passed);
            Run("恢复计数仅由后继尝试提交消费且保留并发新故障", RecoveryCountersRespectAttemptOrder, ref passed);
            Run("状态拒绝或故障替换时保留恢复事务证据", RecoveryRecordCommitRequiresAcceptedState, ref passed);
            Run("Stop兼容封圈在持久化期间换运行不污染新运行标记", StopFallbackKeepsCapturedRunIdentity, ref passed);
            Run("退休投影清理与新注册发布互斥且Begin不持锁", ProjectionRetirementSerializesWithRegistration, ref passed);
            Run("投影发布或清理失败保留原身份供重试", ProjectionFailureKeepsRecoverableIdentity, ref passed);
            Run("学习圈发布直接返回本次身份不回读共享值", LearningPublicationReturnsOwnIdentity, ref passed);
            Run("真实建圈拒绝旧运行且Begin跨代返回不放行", BeginAttemptRejectsChangedRun, ref passed);
            Run("兼容作废失败不把旧身份拼接到新投影", FallbackAbortFailurePreservesNewProjection, ref passed);
            Run("Begin阻塞期间终态不触碰Recorder且禁止Runner启动", BeginBlockedTerminalDoesNotTouchRecorder, ref passed);
            Run("外部Abort后旧Runner未退出则拒绝新attempt", ExternalAbortKeepsExecutionTombstone, ref passed);
            Run("旧Runner退出前异步等待且退出后允许新attempt", ExecutionQuiescenceWaitsThenAllowsNextAttempt, ref passed);
            Run("旧execution迟到完成不得清除新tombstone", StaleExecutionCompletionCannotClearNewTombstone, ref passed);
            Run("启动供电与恢复预释放均受execution门保护", HardwareActionsWaitForExecutionQuiescence, ref passed);
            Run("已开始圈的异常fallback必须等待耐久封圈",
                FormalFallbackRequiresDurableTerminal, ref passed);
            Run("圈尝试冻结开始时DAQ及电源身份", AttemptFreezesDaqIdentity, ref passed);
            Run("正式圈结果快照不共享可变字段", OutcomeSnapshotIsIndependent, ref passed);
            Run("生产Runner提交使用原圈而非最新结果", RunnerCommitsSuppliedOutcome, ref passed);
            Run("通道状态存储拒绝旧代次覆盖新代次", StateStoreRejectsOlderEpoch, ref passed);
            return passed;
        }

        private static void OutcomeSnapshotIsIndependent()
        {
            var original = new Controller.Adaptive.EpbCycleOutcome
            {
                Kind = Controller.Adaptive.EpbCycleOutcomeKind.Success,
                PeakCurrentA = 12.5,
                ForwardLowPlateauCandidate = true,
                Reason = "original"
            };
            var snapshot = original.Snapshot();
            Assert(!ReferenceEquals(original, snapshot), "快照仍是同一结果对象");
            foreach (var field in typeof(Controller.Adaptive.EpbCycleOutcome).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public))
            {
                Assert(field.FieldType.IsValueType || field.FieldType == typeof(string),
                    "新增引用字段需要深复制审查：" + field.Name);
                Assert(Equals(field.GetValue(original), field.GetValue(snapshot)),
                    "快照遗漏字段：" + field.Name);
            }
            original.PeakCurrentA = 99;
            original.ForwardLowPlateauCandidate = false;
            original.Reason = "next";
            Assert(snapshot.PeakCurrentA == 12.5 && snapshot.ForwardLowPlateauCandidate &&
                   snapshot.Reason == "original", "后续结果变更污染了原圈快照");
            snapshot.CallbackElapsedMs = 500;
            Assert(original.CallbackElapsedMs == 0, "提交侧统计反写了 Runner 结果");
        }

        private static void RunnerCommitsSuppliedOutcome()
        {
            // 只构造提交方法需要的内存状态，不启动采集、计时器或真实硬件。
            var type = typeof(EpbCycleRunner);
            var runner = (EpbCycleRunner)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(type);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            type.GetField("_adaptiveGate", flags).SetValue(runner, new object());
            type.GetField("_adaptiveProfile", flags).SetValue(runner, new Config.EpbAdaptiveProfile { Channel = 1 });
            type.GetField("_adaptiveForwardStallConfirmCycles", flags).SetValue(runner, 1);
            type.GetField("_adaptiveOvershootConfirmCycles", flags).SetValue(runner, 1);
            type.GetProperty("LastCycleOutcome").SetValue(runner,
                Controller.Adaptive.EpbCycleOutcome.Canceled(
                    Controller.Adaptive.EpbCurrentStage.Idle, "later-cycle"));
            var runId = Guid.NewGuid();
            var committed = new Controller.Adaptive.EpbCycleOutcome
            {
                Kind = Controller.Adaptive.EpbCycleOutcomeKind.Success,
                ForwardLowPlateauCandidate = true,
                PeakCurrentA = 6.25
            };
            var result = runner.CommitFormalCycleFaultEvidence(runId, 17, committed);
            Assert(result.ShouldLatchAlarm && result.ForwardLowPlateauStreak == 1 &&
                   result.AlarmReason.Contains(runId.ToString("N")) &&
                   result.AlarmReason.Contains("Cycle=17"),
                "生产提交未使用原圈候选及原运行身份");
            Assert(runner.LastCycleOutcome.Reason == "later-cycle",
                "历史提交反写了最新 Runner 结果");
            type.GetProperty("LastCycleOutcome").SetValue(runner, committed);
            var normal = new Controller.Adaptive.EpbCycleOutcome
            {
                Kind = Controller.Adaptive.EpbCycleOutcomeKind.Success
            };
            var normalResult = runner.CommitFormalCycleFaultEvidence(runId, 18, normal);
            Assert(!normalResult.ShouldLatchAlarm && normalResult.ForwardLowPlateauStreak == 0,
                "最新异常候选污染了原正常圈提交");
            var rejectedMissingOutcome = false;
            try { runner.CommitFormalCycleFaultEvidence(runId, 19, null); }
            catch (ArgumentNullException) { rejectedMissingOutcome = true; }
            Assert(rejectedMissingOutcome, "缺失原圈结果时回退读取了最新结果");
        }

        private static void StateStoreRejectsOlderEpoch()
        {
            var store = new ChannelRuntimeStateStore();
            var currentRun = Guid.NewGuid();
            var current = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = currentRun, RunEpoch = 9,
                State = ChannelRuntimeState.Running
            });
            var rejected = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = Guid.NewGuid(), RunEpoch = 8,
                State = ChannelRuntimeState.StartBlocked
            }, allowTerminalReset: true, allowSystemFaultReset: true);
            Assert(rejected.RunId == currentRun && rejected.Revision == current.Revision &&
                   store.Get(1).State == ChannelRuntimeState.Running,
                "旧代次覆盖了新运行或推进了版本");
            var terminal = store.Publish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = currentRun, RunEpoch = 9,
                State = ChannelRuntimeState.StartBlocked
            });
            Assert(terminal.State == ChannelRuntimeState.StartBlocked &&
                   terminal.Revision == current.Revision + 1,
                "同代合法安全终态被拒绝");
            Assert(!store.TryPublish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = currentRun, RunEpoch = 9,
                State = ChannelRuntimeState.Running
            }, out var held) && held.Revision == terminal.Revision,
                "锁存终态拒绝被报告为成功或推进版本");
            Assert(!store.TryPublish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = Guid.NewGuid(), RunEpoch = 8,
                State = ChannelRuntimeState.Running
            }, out _, allowTerminalReset: true), "旧代次被报告为接受");
            Assert(store.TryPublish(new ChannelRuntimeStateChangedEvent
            {
                Channel = 1, RunId = currentRun, RunEpoch = 9,
                State = ChannelRuntimeState.Running
            }, out var resumed, allowTerminalReset: true) && resumed.Revision == terminal.Revision + 1,
                "显式终态复位未返回真实成功和递增版本");
            resumed.ReasonCode = "caller mutation";
            Assert(store.Get(1).ReasonCode != "caller mutation", "返回对象反写存储");
        }

        private static void AttemptFreezesDaqIdentity()
        {
            var powerId = Guid.NewGuid();
            using var context = new CycleAttemptContext(
                Guid.NewGuid(),
                9,
                "Dev2",
                17,
                24680,
                11,
                7001,
                CycleAttemptKind.FormalBatch,
                321,
                powerOperationId: powerId,
                powerOperationEpoch: 27);
            Assert(context.DaqGeneration == 17 && context.DaqBeginSequence == 24680,
                "圈尝试没有保留开始时DAQ身份，恢复换代后可能用新代次封旧圈");
            Assert(context.PowerOperationId == powerId && context.PowerOperationEpoch == 27,
                "圈尝试没有冻结开始时电源身份");
        }

        private static void FormalFallbackRequiresDurableTerminal()
        {
            Assert(EpbManager.ResolveFormalFallbackPersistence(null, out var notStartedRequired) &&
                   !notStartedRequired,
                "尚未开始圈的安全退出被误要求持久化");

            using var started = NewContext(channel: 4, attemptId: 9001, cycle: 77);
            Assert(!EpbManager.ResolveFormalFallbackPersistence(started, out var startedRequired) &&
                   startedRequired,
                "已开始但未封圈的attempt通过了fallback");
            Assert(started.AbortOnce(() => true, _ => { }), "测试attempt未能持久作废");
            Assert(EpbManager.ResolveFormalFallbackPersistence(started, out startedRequired) &&
                   startedRequired,
                "已明确耐久作废的attempt仍阻塞fallback");
        }

        private static void AttemptVisibleBeforeRecorderBeginReturns()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(channel: 4, attemptId: 1, cycle: 101);
            using var beginEntered = new ManualResetEventSlim(false);
            using var releaseBegin = new ManualResetEventSlim(false);

            var beginTask = Task.Run(() => registry.TryRegisterBeforeBegin(context, () =>
            {
                beginEntered.Set();
                if (!releaseBegin.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("测试未释放模拟Recorder.BeginCycle。");
                context.MarkBeginSucceeded();
            }));

            Assert(beginEntered.Wait(TimeSpan.FromSeconds(2)), "模拟 BeginCycle 未进入阻塞点");
            Assert(registry.TryGetCurrent(4, out var visible), "Begin返回前registry不可见");
            Assert(ReferenceEquals(visible, context), "Begin返回前观察到错误attempt");
            Assert(visible.BeginState == CycleAttemptBeginState.Registered,
                "Begin返回前不应提前标成Begun");
            releaseBegin.Set();
            Assert(beginTask.GetAwaiter().GetResult(), "Begin完成后注册结果错误");
            Assert(context.BeginState == CycleAttemptBeginState.Begun, "Begin完成状态未提交");
            registry.MarkExecutionCompleted(context);
            Assert(registry.TryRemoveExact(context), "测试清理旧attempt失败");
        }

        private static void HundredTerminalRacersCommitOnce()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(channel: 5, attemptId: 2, cycle: 202);
            Assert(registry.TryRegister(context), "初始attempt注册失败");
            var commits = 0;
            var winners = 0;
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            {
                start.Wait();
                Func<bool> persist = () =>
                {
                    Interlocked.Increment(ref commits);
                    return true;
                };
                bool won;
                switch (index % 3)
                {
                    case 0:
                        won = context.CompleteOnce(persist, item => registry.TryRemoveExact(item));
                        break;
                    case 1:
                        won = context.AbortOnce(persist, item => registry.TryRemoveExact(item));
                        break;
                    default:
                        won = context.AlarmOnce(persist, item => registry.TryRemoveExact(item));
                        break;
                }
                if (won) Interlocked.Increment(ref winners);
            })).ToArray();
            start.Set();
            Assert(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)), "百路终态竞争超时");
            Assert(commits == 1, $"终态持久化执行{commits}次，应为1次");
            Assert(winners == 1, $"终态胜者{winners}个，应为1个");
            Assert(context.IsDurablyCommitted, "胜出终态未标记耐久提交");
            Assert(registry.Count == 0, "耐久终态后registry未精确清理");
            registry.MarkExecutionCompleted(context);
        }

        private static void StaleTerminalCannotRemoveNewAttempt()
        {
            var registry = new CycleAttemptRegistry();
            using var oldAttempt = NewContext(channel: 8, attemptId: 30, cycle: 300);
            using var newAttempt = NewContext(channel: 8, attemptId: 31, cycle: 301);
            Assert(registry.TryRegister(oldAttempt), "旧attempt注册失败");
            registry.MarkExecutionCompleted(oldAttempt);
            Assert(registry.TryRemoveExact(oldAttempt), "旧attempt预清理失败");
            Assert(registry.TryRegister(newAttempt), "新attempt注册失败");

            Assert(oldAttempt.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                "旧attempt自身终态未提交");
            Assert(registry.TryGetCurrent(8, out var current) && ReferenceEquals(current, newAttempt),
                "旧attempt迟到回调删除了新attempt");
            Assert(registry.TryRemoveExact(newAttempt), "新attempt测试清理失败");
            registry.MarkExecutionCompleted(newAttempt);
        }

        private static void PeakAbortDoesNotAffectNextAttempt()
        {
            var owner = new ExactTokenLeaseOwner<string>();
            var canceled = new ConcurrentQueue<string>();
            var first = owner.TryReserve();
            Assert(first != null, "首个峰值租约预留失败");
            Assert(ReferenceEquals(owner.DetachCurrent(), first), "未精确撤下首个租约");
            first.CancelWhenPublished(canceled.Enqueue);

            var second = owner.TryReserve();
            Assert(second != null, "下一attempt峰值租约预留失败");
            Assert(second.TryPublish("new-token", canceled.Enqueue), "新令牌发布失败");
            Assert(!first.TryPublish("old-token", canceled.Enqueue), "已撤销旧令牌不应重新发布");
            Assert(canceled.Count == 1 && canceled.TryPeek(out var canceledToken) &&
                   canceledToken == "old-token", "旧租约未只取消自身精确令牌");
            Assert(owner.TryPeek(out var currentToken) && currentToken == "new-token",
                "旧租约取消污染了下一attempt令牌");

            var detached = owner.DetachCurrent();
            Assert(ReferenceEquals(detached, second) && detached.TryTakePublished(out var finalToken) &&
                   finalToken == "new-token", "新令牌无法由自身租约精确封口");
        }

        private static void LearningPublicationReturnsOwnIdentity()
        {
            var store = new ChannelRuntimeStore<object>();
            var oldRuntime = store.GetOrCreate(1, () => new object(), null, out _);
            store.Remove(1);
            var newRuntime = store.GetOrCreate(1, () => new object(), null, out _);
            var deactivated = 0;
            Assert(!store.RemoveExact(1, oldRuntime, _ => deactivated++) &&
                ReferenceEquals(store.Active[1], newRuntime) && deactivated == 0,
                "旧实例精确移除影响了新运行对象");
            Assert(store.RemoveExact(1, newRuntime, _ => deactivated++) &&
                store.Active.IsEmpty && store.Cache.IsEmpty && deactivated == 1,
                "精确移除未同时清理两表或重复执行解绑");
            store.Active[1] = oldRuntime;
            store.Cache[1] = newRuntime;
            Assert(store.RemoveExact(1, oldRuntime, _ => deactivated++) &&
                !store.Active.ContainsKey(1) && ReferenceEquals(store.Cache[1], newRuntime) &&
                deactivated == 2, "精确移除错误清理了不同实例的缓存");
            Assert(!store.RemoveExact(1, oldRuntime, _ => deactivated++) && deactivated == 2,
                "重复精确移除再次执行了旧实例清理");
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var ids = new ConcurrentDictionary<int, long>();
            var cycles = new ConcurrentDictionary<int, int>();
            var registry = new CycleAttemptRegistry();
            type.GetField("_cycleAttempts", flags).SetValue(manager, registry);
            type.GetField("_currentAttemptIdByChannel", flags).SetValue(manager, ids);
            type.GetField("_currentCycleNumberByChannel", flags).SetValue(manager, cycles);
            var publish = type.GetMethod("MarkCurrentCycleNumber", flags);
            var first = (long)publish.Invoke(manager, new object[] { 1, 10 });
            var second = (long)publish.Invoke(manager, new object[] { 1, 11 });
            Assert(first > 0 && second > first && ids[1] == second && cycles[1] == 11,
                "学习圈发布未返回独立递增身份或投影错误");
            Assert(first != ids[1], "原圈身份错误回读为后续共享身份");
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            using var started = new ManualResetEventSlim(false);
            var holder = Task.Run(() => registry.WithChannelProjectionGate(1, () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("投影锁未释放");
                return true;
            }));
            Task<long> concurrent = null;
            try
            {
                Assert(entered.Wait(TimeSpan.FromSeconds(5)), "投影锁未取得");
                concurrent = Task.Run(() =>
                {
                    started.Set();
                    return (long)publish.Invoke(manager, new object[] { 1, 12 });
                });
                Assert(started.Wait(TimeSpan.FromSeconds(5)), "并发发布未启动");
                Assert(!concurrent.Wait(100) && ids[1] == second && cycles[1] == 11,
                    "学习投影发布绕过统一通道锁");
            }
            finally
            {
                release.Set();
                holder.GetAwaiter().GetResult();
                if (concurrent != null) concurrent.GetAwaiter().GetResult();
            }
            Assert(ids[1] > second && cycles[1] == 12, "释放锁后学习投影未发布");
            var pending = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            pending[$"{Guid.Empty:N}:0:1"] = new EpbManager.FormalPendingCycle(12, 50);
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pending);
            type.GetField("_log", flags).SetValue(manager, Config.NullLogger.Instance);
            var clear = type.GetMethod("ClearCurrentCycleNumber", flags);
            clear.Invoke(manager, new object[] { 1 });
            Assert(ids.ContainsKey(1) && cycles[1] == 12, "通用清理绕过待封圈保护");
            pending.Clear();
            using var clearStarted = new ManualResetEventSlim(false);
            Task clearTask = null;
            try
            {
                registry.WithChannelProjectionGate(1, () =>
                {
                    clearTask = Task.Run(() =>
                    {
                        clearStarted.Set();
                        clear.Invoke(manager, new object[] { 1 });
                    });
                    Assert(clearStarted.Wait(TimeSpan.FromSeconds(5)), "并发清理未启动");
                    Assert(!clearTask.Wait(100) && ids.ContainsKey(1) && cycles[1] == 12,
                        "通用清理绕过统一投影锁");
                    return true;
                });
            }
            finally { if (clearTask != null) clearTask.GetAwaiter().GetResult(); }
            Assert(!ids.ContainsKey(1) && !cycles.ContainsKey(1), "通用清理未一起删除两项投影");
            var clearExact = type.GetMethod("ClearCurrentCycleNumberForAttempt", flags);
            var oldAttempt = (long)publish.Invoke(manager, new object[] { 1, -10 });
            var newAttempt = (long)publish.Invoke(manager, new object[] { 1, -10 });
            Assert(!(bool)clearExact.Invoke(manager, new object[] { 1, -10, oldAttempt }) &&
                ids[1] == newAttempt && cycles[1] == -10,
                "旧学习封存清除了同圈新尝试");
            Assert(!(bool)clearExact.Invoke(manager, new object[] { 1, -11, newAttempt }) &&
                !(bool)clearExact.Invoke(manager, new object[] { 1, -10, 0L }),
                "圈号不符或未知尝试获准清理");
            pending[$"{Guid.Empty:N}:0:1"] = new EpbManager.FormalPendingCycle(-10, 50);
            Assert(!(bool)clearExact.Invoke(manager, new object[] { 1, -10, newAttempt }) &&
                ids[1] == newAttempt, "精确清理绕过耐久等待保护");
            pending.Clear();
            Assert((bool)clearExact.Invoke(manager, new object[] { 1, -10, newAttempt }) &&
                !ids.ContainsKey(1) && !cycles.ContainsKey(1), "原尝试终态未清理成对投影");
            var waitingAttempt = (long)publish.Invoke(manager, new object[] { 1, -20 });
            using var exactStarted = new ManualResetEventSlim(false);
            Task<bool> waitingClear = null;
            long replacementAttempt = 0;
            try
            {
                registry.WithChannelProjectionGate(1, () =>
                {
                    waitingClear = Task.Run(() =>
                    {
                        exactStarted.Set();
                        return (bool)clearExact.Invoke(manager, new object[] { 1, -20, waitingAttempt });
                    });
                    Assert(exactStarted.Wait(TimeSpan.FromSeconds(5)), "旧封存清理任务未启动");
                    Assert(!waitingClear.Wait(100), "精确清理未等待统一通道锁");
                    replacementAttempt = (long)publish.Invoke(manager, new object[] { 1, -20 });
                    return true;
                });
            }
            finally { if (waitingClear != null) waitingClear.GetAwaiter().GetResult(); }
            Assert(!waitingClear.Result && ids[1] == replacementAttempt && cycles[1] == -20,
                "旧封存等待锁后未重验身份，误清同圈新尝试");
        }

        private static void ProjectionFailureKeepsRecoverableIdentity()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(2, 62, 601);
            var began = false;
            var publishFailed = false;
            try
            {
                registry.TryRegisterBeforeBegin(context,
                    () => { throw new InvalidOperationException("InjectedPublishFailure"); },
                    () => began = true);
            }
            catch (InvalidOperationException ex) when (ex.Message == "InjectedPublishFailure")
            {
                publishFailed = true;
            }
            Assert(publishFailed && !began && registry.IsCurrent(context),
                "投影发布失败后执行Begin或遗失原身份");
            var cleanupFailed = false;
            try
            {
                registry.TryRemoveExact(context,
                    () => { throw new InvalidOperationException("InjectedCleanupFailure"); });
            }
            catch (InvalidOperationException ex) when (ex.Message == "InjectedCleanupFailure")
            {
                cleanupFailed = true;
            }
            Assert(cleanupFailed && registry.IsCurrent(context), "清理失败后提前移除了原身份");
            var cleaned = 0;
            Assert(registry.TryRemoveExact(context, () => cleaned++), "清理失败后无法重试");
            Assert(!registry.TryRemoveExact(context, () => cleaned++), "迟到清理再次执行");
            Assert(cleaned == 1 && registry.Count == 0, "清理未且仅一次完成");
            registry.MarkExecutionCompleted(context);
        }

        private static void ProjectionRetirementSerializesWithRegistration()
        {
            var registry = new CycleAttemptRegistry();
            using var old = NewContext(1, 60, 600);
            using var next = NewContext(1, 61, 600);
            long projectedAttempt = old.AttemptId;
            var projectedCycle = old.Cycle;
            Assert(registry.TryRegister(old), "旧圈注册失败");
            registry.MarkExecutionCompleted(old);
            using var cleanupEntered = new ManualResetEventSlim();
            using var releaseCleanup = new ManualResetEventSlim();
            using var registrationStarted = new ManualResetEventSlim();
            using var beginEntered = new ManualResetEventSlim();
            using var releaseBegin = new ManualResetEventSlim();
            var retire = Task.Run(() => registry.TryRemoveExact(old, () =>
            {
                projectedAttempt = 0;
                cleanupEntered.Set();
                if (!releaseCleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("清理未释放");
                projectedCycle = 0;
            }));
            Task<bool> register = null;
            try
            {
                Assert(cleanupEntered.Wait(TimeSpan.FromSeconds(5)), "未进入投影清理");
                register = Task.Run(() =>
                {
                    registrationStarted.Set();
                    return registry.TryRegisterBeforeBegin(next, () =>
                    {
                        projectedAttempt = next.AttemptId;
                        projectedCycle = next.Cycle;
                    }, () =>
                    {
                        beginEntered.Set();
                        if (!releaseBegin.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Begin未释放");
                    });
                });
                Assert(registrationStarted.Wait(TimeSpan.FromSeconds(5)), "新注册未启动");
                Assert(!beginEntered.Wait(TimeSpan.FromMilliseconds(100)), "旧投影未清理完就发布新圈");
                releaseCleanup.Set();
                Assert(retire.GetAwaiter().GetResult(), "旧圈退休失败");
                Assert(beginEntered.Wait(TimeSpan.FromSeconds(5)), "新圈未进入Begin");
                Assert(projectedAttempt == next.AttemptId && projectedCycle == next.Cycle,
                    "旧清理删掉新圈投影");
                // Begin 尚未返回，另一线程应能拿到通道锁执行精确退休。
                var removeNext = Task.Run(() => registry.TryRemoveExact(next));
                Assert(removeNext.Wait(TimeSpan.FromSeconds(2)) && removeNext.Result,
                    "Recorder.Begin持有通道锁");
            }
            finally
            {
                releaseCleanup.Set();
                releaseBegin.Set();
                retire.GetAwaiter().GetResult();
                if (register != null) register.GetAwaiter().GetResult();
                registry.MarkExecutionCompleted(next);
            }
        }

        private static void ManagerCompletionUsesOriginalAbortIdentity()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var markers = new ConcurrentDictionary<string, byte>();
            type.GetField("_daqClockAbortedCycles", flags).SetValue(manager, markers);
            var newerRun = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, newerRun);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            using var attempt = NewContext(1, 55, 500);
            var mark = type.GetMethod("MarkDaqClockCycleAborted", flags);
            var complete = type.GetMethod("CompleteCycleAndScheduleEvidence", flags);
            var markSoftware = type.GetMethod("MarkSoftwareRecoveryAttemptAborted", flags);
            var consume = type.GetMethod("TryConsumeDaqClockCycleAbort", flags);
            markSoftware.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 54L });
            Assert((bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                "旧软件尝试标记拦截同圈号新尝试");
            Assert(!(bool)consume.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 55L }) &&
                markers.Count == 1, "新尝试消费了旧尝试的软件作废标记");
            Assert((bool)consume.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 54L }),
                "原软件尝试不能消费自身标记");
            markSoftware.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 55L });
            Assert(!(bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                "自身软件作废标记未阻止完成");
            mark.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500 });
            Assert(markers.Count == 2 &&
                (bool)consume.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 55L }) &&
                markers.Count == 1, "精确尝试消费同时移除了DAQ事故圈保护");
            Assert(!(bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                "软件标记消费后DAQ事故圈失去保护");
            Assert((bool)consume.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 55L }) &&
                markers.IsEmpty, "DAQ事故圈标记不能由原圈收尾消费");
            foreach (var missingIdentity in new[] { 0L, -1L })
            {
                markSoftware.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, missingIdentity });
                Assert(!(bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                    "兼容终态缺失身份时错误放行事故圈");
                Assert((bool)consume.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500, 55L }) &&
                    markers.IsEmpty, "兼容圈级标记不能收口");
            }
            markers.Clear();
            mark.Invoke(manager, new object[] { newerRun, 8L, 1, 500 });
            // 无 Recorder 的夹具只验证入口的身份门禁，不作为真实耐久提交证据。
            Assert((bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                "新运行同圈号作废标记错误拦截旧attempt");
            mark.Invoke(manager, new object[] { attempt.RunId, attempt.RunEpoch, 1, 500 });
            Assert(!(bool)complete.Invoke(manager, new object[] { null, attempt, 0, DateTime.UtcNow }),
                "原attempt作废标记未阻止完成");
            Assert(markers.Count == 2, "完成检查错误消费其他运行的事故证据");
            // 恢复字典/硬件字段未初始化；旧报告必须在触碰它们之前返回。
            type.GetMethod("ReportFormalPersistenceRecovery", flags).Invoke(manager,
                new object[] { 1, 500, "OldBegin", new InvalidOperationException("fixture"), attempt });
            type.GetMethod("ReportFormalControlSoftwareRecovery", flags).Invoke(manager,
                new object[] { 1, 500, "OldControl", attempt });
            var persistenceAttempts = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var controlAttempts = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var pendingCycles = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            var recoveryKey = $"{newerRun:N}:8:1";
            persistenceAttempts[recoveryKey] = new EpbManager.FormalRecoveryCounter(3, 60);
            controlAttempts[recoveryKey] = new EpbManager.FormalRecoveryCounter(2, 60);
            pendingCycles[recoveryKey] = new EpbManager.FormalPendingCycle(501, 50);
            type.GetField("_formalPersistenceRecoveryAttempts", flags).SetValue(manager, persistenceAttempts);
            type.GetField("_formalControlRecoveryAttempts", flags).SetValue(manager, controlAttempts);
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pendingCycles);
            type.GetMethod("CompleteFormalSoftwareRecoveryAfterCommit", flags).Invoke(manager,
                new object[] { 1, attempt });
            type.GetMethod("CompleteFormalSoftwareRecoveryAfterCommit", flags).Invoke(manager,
                new object[] { 1, null });
            using var wrongChannel = new CycleAttemptContext(newerRun, 8L, "Dev1", 2, 56,
                CycleAttemptKind.FormalBatch, 501);
            type.GetMethod("CompleteFormalSoftwareRecoveryAfterCommit", flags).Invoke(manager,
                new object[] { 1, wrongChannel });
            using var oldSameRun = new CycleAttemptContext(newerRun, 8L, "Dev1", 1, 59,
                CycleAttemptKind.FormalBatch, 500);
            type.GetMethod("CompleteFormalSoftwareRecoveryAfterCommit", flags).Invoke(manager,
                new object[] { 1, oldSameRun });
            Assert(persistenceAttempts.TryGetValue(recoveryKey, out var persistenceCount) && persistenceCount.Count == 3 &&
                controlAttempts.TryGetValue(recoveryKey, out var controlCount) && controlCount.Count == 2 &&
                pendingCycles.TryGetValue(recoveryKey, out var pendingCycle) && pendingCycle.Cycle == 501,
                "旧运行、缺失或错配身份提交清除了当前恢复计数或待封圈状态");
        }

        private sealed class RecorderInvocationProxy : System.Runtime.Remoting.Proxies.RealProxy
        {
            private readonly Action _abort;
            private readonly Action _begin;
            internal RecorderInvocationProxy(Action abort, Action begin = null) : base(typeof(DataOperation.IEpbCycleRecorder))
            { _abort = abort; _begin = begin; }
            public override System.Runtime.Remoting.Messaging.IMessage Invoke(
                System.Runtime.Remoting.Messaging.IMessage message)
            {
                var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)message;
                object result;
                if (call.MethodName == "GetCurrentCycleSampleCount") result = 4;
                else if (call.MethodName == "AbortCycle") { _abort(); result = null; }
                else if (call.MethodName == "BeginCycle" && _begin != null) { _begin(); result = null; }
                else throw new InvalidOperationException("Unexpected recorder call: " + call.MethodName);
                return new System.Runtime.Remoting.Messaging.ReturnMessage(result, null, 0,
                    call.LogicalCallContext, call);
            }
        }

        private static void FallbackAbortFailurePreservesNewProjection()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            foreach (var replacement in new[] { "none", "cycle", "attempt", "both", "run", "context" })
            {
                var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
                type.GetField("_activeBatchId", flags).SetValue(manager, Guid.NewGuid());
                type.GetField("_runEpoch", flags).SetValue(manager, 8L);
                type.GetField("_log", flags).SetValue(manager, Config.NullLogger.Instance);
                var registry = new CycleAttemptRegistry();
                using var replacementContext = new CycleAttemptContext(Guid.NewGuid(), 9L, "Dev1", 1, 51,
                    CycleAttemptKind.FormalBatch, 502);
                type.GetField("_cycleAttempts", flags).SetValue(manager, registry);
                var cycles = new ConcurrentDictionary<int, int>();
                var attempts = new ConcurrentDictionary<int, long>();
                cycles[1] = 501;
                attempts[1] = 50;
                type.GetField("_currentCycleNumberByChannel", flags).SetValue(manager, cycles);
                type.GetField("_currentAttemptIdByChannel", flags).SetValue(manager, attempts);
                var called = false;
                var replacementPublished = false;
                var proxy = new RecorderInvocationProxy(() =>
                {
                    called = true;
                    Assert(cycles.IsEmpty && attempts.IsEmpty, "持久化入口前未完整移除旧投影");
                    if (replacement == "context")
                        Assert(registry.TryRegister(replacementContext), "新 context 注入失败");
                    if (replacement == "run")
                    {
                        type.GetField("_activeBatchId", flags).SetValue(manager, Guid.NewGuid());
                        type.GetField("_runEpoch", flags).SetValue(manager, 9L);
                    }
                    var publishReplacement = Task.Run(() => registry.WithChannelProjectionGate(1, () =>
                    {
                        if (replacement == "cycle" || replacement == "both") cycles[1] = 502;
                        if (replacement == "attempt" || replacement == "both") attempts[1] = 51;
                        return true;
                    }));
                    Assert(publishReplacement.Wait(TimeSpan.FromSeconds(3)),
                        "Recorder 作废期间持有投影锁，其他线程无法接管");
                    publishReplacement.GetAwaiter().GetResult();
                    replacementPublished = true;
                    throw new InvalidOperationException("Injected abort failure");
                });
                type.GetField("_recorder", flags).SetValue(manager, proxy.GetTransparentProxy());
                var result = (bool)type.GetMethod("DiscardCurrentCycleForSoftwareRecovery", flags)
                    .Invoke(manager, new object[] { 1, DateTime.UtcNow, "fixture", (int?)501, true, "cancelled" });
                Assert(called && replacementPublished && !result,
                    "未完成跨线程接管注入、未实际进入作废失败路径或失败误报成功");
                if (replacement == "none")
                    Assert(cycles[1] == 501 && attempts[1] == 50, "无人接管时未恢复原投影");
                else if (replacement == "run" || replacement == "context")
                    Assert(cycles.IsEmpty && attempts.IsEmpty, "旧运行失败回调在新运行补回旧圈身份");
                else
                {
                    Assert(cycles.TryGetValue(1, out var cycle) == (replacement != "attempt") &&
                           (replacement == "attempt" || cycle == 502), "旧圈覆盖或补入新投影");
                    Assert(attempts.TryGetValue(1, out var attempt) == (replacement != "cycle") &&
                           (replacement == "cycle" || attempt == 51), "旧 attempt 覆盖或补入新投影");
                }
            }
        }

        private static void BeginAttemptRejectsChangedRun()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var run = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, run);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            foreach (var name in new[] { "_cycleAttempts", "_currentCycleNumberByChannel", "_currentAttemptIdByChannel" })
            {
                var field = type.GetField(name, flags);
                field.SetValue(manager, Activator.CreateInstance(field.FieldType, true));
            }
            var began = 0;
            var recorder = new RecorderInvocationProxy(() => { throw new Exception("Unexpected Abort"); }, () =>
            {
                began++;
                type.GetField("_activeBatchId", flags).SetValue(manager, Guid.NewGuid());
                type.GetField("_runEpoch", flags).SetValue(manager, 9L);
            }).GetTransparentProxy();
            var method = type.GetMethod("TryBeginFormalCycleAttempt", flags);
            var args = new object[] { recorder, 1, 501, DateTime.UtcNow, run, 7L,
                CycleAttemptKind.FormalBatch, CancellationToken.None, null };
            Assert(!(bool)method.Invoke(manager, args) && args[8] == null && began == 0,
                "旧运行入口调用了 Recorder 或创建身份");
            args[5] = 8L;
            Assert(!(bool)method.Invoke(manager, args) && began == 1,
                "Begin跨运行返回后仍允许Runner启动");
            using var context = (CycleAttemptContext)args[8];
            Assert(context.RunId == run && context.RunEpoch == 8L && context.AttemptCts.IsCancellationRequested,
                "原圈被拼入新代次或未撤销尝试");
        }

        private static void StopFallbackKeepsCapturedRunIdentity()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var oldRun = Guid.NewGuid();
            var nextRun = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, oldRun);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            type.GetField("_log", flags).SetValue(manager, Config.NullLogger.Instance);
            foreach (var name in new[] { "_alarmStopLatch", "_cycleAttempts", "_pendingWarningSnapshots" })
            {
                var field = type.GetField(name, flags);
                field.SetValue(manager, Activator.CreateInstance(field.FieldType, true));
            }
            var cycles = new ConcurrentDictionary<int, int>();
            cycles[1] = 501;
            var attempts = new ConcurrentDictionary<int, long>();
            attempts[1] = 50;
            var pending = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            pending[$"{oldRun:N}:8:1"] = new EpbManager.FormalPendingCycle(501, 50);
            pending[$"{nextRun:N}:9:1"] = new EpbManager.FormalPendingCycle(501, 50);
            var markers = new ConcurrentDictionary<string, byte>();
            type.GetField("_currentCycleNumberByChannel", flags).SetValue(manager, cycles);
            type.GetField("_currentAttemptIdByChannel", flags).SetValue(manager, attempts);
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pending);
            type.GetField("_daqClockAbortedCycles", flags).SetValue(manager, markers);
            var writes = 0;
            var proxy = new RecorderInvocationProxy(() =>
            {
                writes++;
                type.GetField("_activeBatchId", flags).SetValue(manager, nextRun);
                type.GetField("_runEpoch", flags).SetValue(manager, 9L);
            });
            type.GetField("_recorder", flags).SetValue(manager, proxy.GetTransparentProxy());
            // 模拟记录器返回，不代表真实磁盘耐久或硬件停止验收。
            type.GetMethod("TryFinalizeStoppedCyclesAfterDurableBoundary", flags).Invoke(manager,
                new object[] { new System.Collections.Generic.Dictionary<int, int> { [1] = 501 },
                    DateTime.UtcNow, "cancelled" });
            Assert(writes == 1 && markers.ContainsKey($"{oldRun:N}:8:1:501:Attempt=50") &&
                   !markers.ContainsKey($"{nextRun:N}:9:1:501:Attempt=50"), "旧封圈结果污染新运行作废标记");
            Assert(!pending.ContainsKey($"{oldRun:N}:8:1") && pending.ContainsKey($"{nextRun:N}:9:1"),
                "旧封圈清除了新运行待封圈记录");
            // 同 run、同圈号也可能已有新尝试，不能只比较 cycle。
            type.GetField("_activeBatchId", flags).SetValue(manager, oldRun);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            cycles[1] = 501;
            attempts[1] = 50;
            pending[$"{oldRun:N}:8:1"] = new EpbManager.FormalPendingCycle(501, 50);
            markers.Clear();
            var replacementProxy = new RecorderInvocationProxy(() => attempts[1] = 51);
            type.GetField("_recorder", flags).SetValue(manager, replacementProxy.GetTransparentProxy());
            var finalized = (bool)type.GetMethod("TryFinalizeStoppedCyclesAfterDurableBoundary", flags).Invoke(manager,
                new object[] { new System.Collections.Generic.Dictionary<int, int> { [1] = 501 },
                    DateTime.UtcNow, "cancelled" });
            Assert(!finalized && cycles[1] == 501 && attempts[1] == 51 &&
                   pending.ContainsKey($"{oldRun:N}:8:1") && markers.IsEmpty,
                "旧封圈覆盖新尝试、清除恢复证据或伪报全部收口");
        }

        private static void RecoveryRecordCommitRequiresAcceptedState()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var run = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, run);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            var key = $"{run:N}:8:1";
            var persistence = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var control = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var pending = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            var p = new EpbManager.FormalRecoveryCounter(3, 50);
            var c = new EpbManager.FormalRecoveryCounter(2, 50);
            persistence[key] = p;
            control[key] = c;
            pending[key] = new EpbManager.FormalPendingCycle(501, 50);
            type.GetField("_formalPersistenceRecoveryAttempts", flags).SetValue(manager, persistence);
            type.GetField("_formalControlRecoveryAttempts", flags).SetValue(manager, control);
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pending);
            using var attempt = new CycleAttemptContext(run, 8L, "Dev1", 1, 51,
                CycleAttemptKind.FormalBatch, 501);
            var commit = type.GetMethod("TryCommitFormalRecoveryRecords", flags);
            var store = new ChannelRuntimeStateStore();
            store.Publish(new ChannelRuntimeStateChangedEvent
            { Channel = 1, RunId = run, RunEpoch = 8, State = ChannelRuntimeState.StartBlocked });
            Func<bool> rejected = () => store.TryPublish(new ChannelRuntimeStateChangedEvent
            { Channel = 1, RunId = run, RunEpoch = 8, State = ChannelRuntimeState.Running }, out _);
            Assert(!(bool)commit.Invoke(manager, new object[] { attempt, p, c, rejected }),
                "锁存状态拒绝却提交了恢复事务");
            Assert(ReferenceEquals(persistence[key], p) && ReferenceEquals(control[key], c) && pending[key].Cycle == 501,
                "状态拒绝丢失了恢复证据");
            var replacementPending = new EpbManager.FormalPendingCycle(501, 50);
            Func<bool> reentrantReplacement = () => { pending[key] = replacementPending; return true; };
            Assert((bool)commit.Invoke(manager, new object[] { attempt, p, c, reentrantReplacement }) &&
                ReferenceEquals(pending[key], replacementPending) && persistence.IsEmpty && control.IsEmpty,
                "发布成功后反报失败，或旧提交消费了回调重入登记的新pending证据");
            persistence[key] = p;
            control[key] = c;
            EpbManager.RecordFormalRecoveryFault(control, key, 52);
            var called = false;
            Func<bool> shouldNotPublish = () => { called = true; return true; };
            Assert(!(bool)commit.Invoke(manager, new object[] { attempt, p, c, shouldNotPublish }) && !called,
                "故障被替换后仍尝试发布旧恢复状态");

            control[key] = c;
            pending[key] = new EpbManager.FormalPendingCycle(501, 50);
            Func<bool> rejectedAfterNewFault = () =>
            {
                EpbManager.RecordFormalRecoveryFault(control, key, 54);
                pending[key] = new EpbManager.FormalPendingCycle(501, 54);
                return false;
            };
            Assert(!(bool)commit.Invoke(manager, new object[] { attempt, p, c, rejectedAfterNewFault }) &&
                control[key].LastFaultAttemptId == 54 && pending[key].AttemptId == 54 &&
                persistence.TryGetValue(key, out var restoredPersistence) && ReferenceEquals(restoredPersistence, p),
                "发布拒绝后的旧证据恢复覆盖了重入登记的更新故障，或丢失未被替代的旧证据");
            using var later = new CycleAttemptContext(run, 8L, "Dev1", 1, 55,
                CycleAttemptKind.FormalBatch, 501);
            Func<bool> accepted = () => store.TryPublish(new ChannelRuntimeStateChangedEvent
            { Channel = 1, RunId = run, RunEpoch = 8, State = ChannelRuntimeState.Running }, out _, allowTerminalReset: true);
            Assert((bool)commit.Invoke(manager, new object[] { later, p, control[key], accepted }),
                "后继状态接受后不能收口恢复事务");
            Assert(persistence.IsEmpty && control.IsEmpty && pending.IsEmpty &&
                   store.Get(1).State == ChannelRuntimeState.Running, "状态接受后证据未一起消费");
        }

        private static void RecoveryCountersRespectAttemptOrder()
        {
            var records = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            const string key = "fixture";
            EpbManager.RecordFormalRecoveryFault(records, key, 20);
            EpbManager.RecordFormalRecoveryFault(records, key, 10);
            Assert(records[key].Count == 2 && records[key].LastFaultAttemptId == 20,
                "迟到旧故障使尝试边界倒退或丢失计数");
            Assert(!EpbManager.TryConsumeFormalRecoveryCounter(records, key, 19, out _) &&
                   !EpbManager.TryConsumeFormalRecoveryCounter(records, key, 20, out _),
                "旧尝试或故障尝试自己清除了恢复记录");
            Assert(EpbManager.TryConsumeFormalRecoveryCounter(records, key, 21, out var count) && count == 2,
                "后继业务提交不能消费既有恢复记录");
            for (var i = 0; i < 100; i++)
            {
                records.Clear();
                EpbManager.RecordFormalRecoveryFault(records, key, 1);
                using var go = new ManualResetEventSlim(false);
                var fault = Task.Run(() => { go.Wait(); EpbManager.RecordFormalRecoveryFault(records, key, 3); });
                var commit = Task.Run(() => { go.Wait(); EpbManager.TryConsumeFormalRecoveryCounter(records, key, 2, out _); });
                go.Set();
                Task.WaitAll(fault, commit);
                Assert(records.TryGetValue(key, out var remaining) && remaining.LastFaultAttemptId == 3,
                    "并发旧提交消费了更新的故障记录");
            }
        }

        private static void DurableTerminalRemovesOnlyMatchingPendingCycle()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var pending = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pending);
            var runId = Guid.NewGuid();
            var key = $"{runId:N}:8:1";
            pending[key] = new EpbManager.FormalPendingCycle(502, 50);
            var remove = type.GetMethod("RemoveFormalPendingCycleAfterDurableTerminal", flags);
            Assert(!(bool)remove.Invoke(manager, new object[] { runId, 8L, 1, 501, 50L }),
                "旧圈删除了较新圈的等待记录");
            Assert(!(bool)remove.Invoke(manager, new object[] { runId, 7L, 1, 502, 50L }),
                "旧运行代次删除了新运行的等待记录");
            Assert(pending[key].Cycle == 502, "错配清理改变了原记录");
            foreach (var wrongAttempt in new[] { 0L, 49L, 51L })
                Assert(!(bool)remove.Invoke(manager, new object[] { runId, 8L, 1, 502, wrongAttempt }),
                    "缺失或不同尝试身份清除了同圈号等待记录");
            var replacement = new EpbManager.FormalPendingCycle(502, 51);
            pending[key] = replacement;
            Assert(!(bool)remove.Invoke(manager, new object[] { runId, 8L, 1, 502, 50L }) &&
                ReferenceEquals(pending[key], replacement), "旧终态清除了后继同圈尝试");
            pending[key] = new EpbManager.FormalPendingCycle(502, 50);
            Assert((bool)remove.Invoke(manager, new object[] { runId, 8L, 1, 502, 50L }),
                "匹配耐久终态不能收口等待记录");
            Assert(!pending.ContainsKey(key), "匹配记录仍残留");
            Assert(!(bool)remove.Invoke(manager, new object[] { runId, 8L, 1, 502, 50L }),
                "同一记录被重复消费");
            for (var i = 0; i < 100; i++)
            {
                pending[key] = new EpbManager.FormalPendingCycle(502, 50);
                var next = new EpbManager.FormalPendingCycle(502, 51);
                using var go = new ManualResetEventSlim(false);
                var register = Task.Run(() =>
                {
                    go.Wait();
                    lock (pending) pending[key] = next;
                });
                var terminal = Task.Run(() =>
                {
                    go.Wait();
                    remove.Invoke(manager, new object[] { runId, 8L, 1, 502, 50L });
                });
                go.Set();
                Task.WaitAll(register, terminal);
                Assert(pending.TryGetValue(key, out var remaining) && ReferenceEquals(remaining, next),
                    "并发旧终态消费了同圈号的新尝试记录");
            }
            // 真实业务完成入口同样不得绕过该值匹配门禁。
            type.GetField("_activeBatchId", flags).SetValue(manager, runId);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            var priorFault = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            priorFault[key] = new EpbManager.FormalRecoveryCounter(2, 50);
            type.GetField("_formalPersistenceRecoveryAttempts", flags).SetValue(manager,
                priorFault);
            type.GetField("_formalControlRecoveryAttempts", flags).SetValue(manager,
                new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>());
            pending[key] = new EpbManager.FormalPendingCycle(502, 50);
            using var oldCycle = new CycleAttemptContext(runId, 8L, "Dev1", 1, 56,
                CycleAttemptKind.FormalBatch, 501);
            type.GetMethod("CompleteFormalSoftwareRecoveryAfterCommit", flags).Invoke(manager,
                new object[] { 1, oldCycle });
            Assert(pending[key].Cycle == 502, "旧圈业务提交清除了较新圈的待封圈保护");
            Assert(priorFault.TryGetValue(key, out var retained) && retained.Count == 2,
                "另一圈尚未耐久收口时消费了恢复计数");
            using var delayedFault = new CycleAttemptContext(runId, 8L, "Dev1", 1, 49,
                CycleAttemptKind.FormalBatch, 501);
            type.GetMethod("ReportFormalPersistenceRecovery", flags).Invoke(manager,
                new object[] { 1, 501, "DelayedFault", new InvalidOperationException("old"), delayedFault });
            Assert(pending[key].Cycle == 502 && ReferenceEquals(priorFault[key], retained),
                "迟到旧落盘故障覆盖了更新的等待圈或故障计数");
            type.GetMethod("ReportFormalControlSoftwareRecovery", flags).Invoke(manager,
                new object[] { 1, 501, "DelayedControl", delayedFault });
            var newerControl = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            newerControl[key] = new EpbManager.FormalRecoveryCounter(1, 60);
            type.GetField("_formalControlRecoveryAttempts", flags).SetValue(manager, newerControl);
            priorFault.Clear();
            type.GetMethod("ReportFormalPersistenceRecovery", flags).Invoke(manager,
                new object[] { 1, 501, "CrossKindDelayed", new InvalidOperationException("old"), delayedFault });
            type.GetMethod("ReportFormalControlSoftwareRecovery", flags).Invoke(manager,
                new object[] { 1, 501, "DelayedControl", delayedFault });
            Assert(priorFault.IsEmpty && newerControl[key].Count == 1 && pending[key].Cycle == 502,
                "跨故障类型的旧登记改变了更新恢复记录");
        }

        private static void RestartDiagnosticResetKeepsRecoveryEvidence()
        {
            var type = typeof(EpbManager);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var manager = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var runId = Guid.NewGuid();
            type.GetField("_activeBatchId", flags).SetValue(manager, runId);
            type.GetField("_runEpoch", flags).SetValue(manager, 8L);
            type.GetField("_log", flags).SetValue(manager, Config.NullLogger.Instance);
            var key = $"{runId:N}:8:1";
            var persistence = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var control = new ConcurrentDictionary<string, EpbManager.FormalRecoveryCounter>();
            var pending = new ConcurrentDictionary<string, EpbManager.FormalPendingCycle>();
            persistence[key] = new EpbManager.FormalRecoveryCounter(3, 60);
            control[key] = new EpbManager.FormalRecoveryCounter(2, 60);
            pending[key] = new EpbManager.FormalPendingCycle(501, 50);
            type.GetField("_formalPersistenceRecoveryAttempts", flags).SetValue(manager, persistence);
            type.GetField("_formalControlRecoveryAttempts", flags).SetValue(manager, control);
            type.GetField("_formalPersistenceRecoveryPendingCycles", flags).SetValue(manager, pending);
            // 不构造硬件；缺失 Runner 缓存使诊断复位走异常分支。
            // 无论准备是否成功，都不能提前当作真实业务提交。
            type.GetMethod("ResetTransientFaultStateForRestart", flags).Invoke(manager,
                new object[] { new[] { 1 }, "AffectedGroupResetRejoin" });
            Assert(persistence.TryGetValue(key, out var p) && p.Count == 3 &&
                   control.TryGetValue(key, out var c) && c.Count == 2 &&
                   pending.TryGetValue(key, out var cycle) && cycle.Cycle == 501,
                "重入准备在实际业务提交前清除了恢复计数或待封圈保护");
        }

        private static void AbortAfterFailedCompleteReleasesClaim()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(channel: 10, attemptId: 41, cycle: 401);
            Assert(registry.TryRegisterBeforeBegin(context, context.MarkBeginSucceeded), "圈开始失败");
            var abortWrites = 0;
            var cleanupCalls = 0;
            Action<CycleAttemptContext> cleanup = item =>
            {
                cleanupCalls++;
                registry.TryRemoveExact(item);
            };
            Assert(!context.CompleteRecorderOnce(() =>
            {
                Assert(!context.AbortRecorderOnce(() => { abortWrites++; return true; }, cleanup),
                    "完成占用期间允许重入作废");
                return false;
            }, cleanup), "作废圈错误提交完成");
            Assert(abortWrites == 0 && cleanupCalls == 0 && registry.IsCurrent(context) &&
                context.TerminalState == CycleAttemptTerminalState.Active, "失败完成未保留原圈并释放占用");
            Assert(context.AbortRecorderOnce(() => { abortWrites++; return true; }, cleanup),
                "释放完成占用后作废失败");
            Assert(abortWrites == 1 && cleanupCalls == 1 && context.IsDurablyCommitted &&
                context.TerminalState == CycleAttemptTerminalState.Aborted && registry.Count == 0,
                "原圈未且仅一次耐久作废");
            registry.MarkExecutionCompleted(context);
        }

        private static void PersistenceFailureKeepsContext()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(channel: 10, attemptId: 40, cycle: 400);
            Assert(registry.TryRegister(context), "attempt注册失败");
            Assert(!context.CompleteOnce(() => false, item => registry.TryRemoveExact(item)),
                "持久化失败不应报告终态成功");
            Assert(registry.IsCurrent(context), "持久化失败错误移除了context");
            Assert(context.TerminalState == CycleAttemptTerminalState.Active,
                "持久化失败后未释放终态声明供恢复封圈");

            Assert(context.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                "恢复Abort未能提交保留的context");
            Assert(context.TerminalState == CycleAttemptTerminalState.Aborted &&
                   context.IsDurablyCommitted, "恢复终态身份错误");
            Assert(registry.Count == 0, "恢复耐久提交后context仍残留");
            registry.MarkExecutionCompleted(context);
        }

        private static void BeginBlockedTerminalDoesNotTouchRecorder()
        {
            var registry = new CycleAttemptRegistry();
            using var context = NewContext(channel: 11, attemptId: 50, cycle: 500);
            using var beginEntered = new ManualResetEventSlim(false);
            using var releaseBegin = new ManualResetEventSlim(false);
            var recorderTerminalCalls = 0;
            var runnerStarts = 0;

            var beginTask = Task.Run(() => registry.TryRegisterBeforeBegin(context, () =>
            {
                beginEntered.Set();
                if (!releaseBegin.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("测试未释放阻塞Begin。");
                context.MarkBeginSucceeded();
                // 与 EpbManager Begin 返回后的二次所有权/取消检查保持一致。
                if (registry.IsCurrent(context) &&
                    context.TerminalState == CycleAttemptTerminalState.Active &&
                    !context.AttemptCts.IsCancellationRequested)
                    Interlocked.Increment(ref runnerStarts);
            }));

            Assert(beginEntered.Wait(TimeSpan.FromSeconds(2)), "模拟Begin未进入阻塞点");
            var prematureTerminal = context.AbortRecorderOnce(
                () =>
                {
                    Interlocked.Increment(ref recorderTerminalCalls);
                    return true;
                },
                item => registry.TryRemoveExact(item));
            Assert(!prematureTerminal, "Registered阶段不应提交Recorder终态");
            Assert(recorderTerminalCalls == 0, "Registered阶段错误调用了Recorder终态");
            Assert(context.AttemptCts.IsCancellationRequested, "Registered终态请求未撤销attempt");

            releaseBegin.Set();
            Assert(beginTask.GetAwaiter().GetResult(), "Begin注册任务返回异常");
            Assert(context.BeginState == CycleAttemptBeginState.Begun, "释放后Begin状态未提交");
            Assert(runnerStarts == 0, "已撤销attempt在Begin返回后仍启动Runner");
            registry.MarkExecutionCompleted(context);

            Assert(context.AbortRecorderOnce(
                    () =>
                    {
                        Interlocked.Increment(ref recorderTerminalCalls);
                        return true;
                    },
                    item => registry.TryRemoveExact(item)),
                "Begin返回后未能精确提交Abort终态");
            Assert(recorderTerminalCalls == 1, "Begin返回后的Recorder终态调用次数错误");
            Assert(registry.Count == 0, "精确收口后registry仍残留");
        }

        private static void ExternalAbortKeepsExecutionTombstone()
        {
            var registry = new CycleAttemptRegistry();
            using var oldAttempt = NewContext(channel: 4, attemptId: 60, cycle: 600);
            using var nextAttempt = NewContext(channel: 4, attemptId: 61, cycle: 601);
            Assert(registry.TryRegister(oldAttempt), "旧attempt注册失败");
            oldAttempt.MarkBeginSucceeded();
            Assert(oldAttempt.MarkExecutionStarted(), "旧attempt未进入执行区");
            Assert(oldAttempt.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                "外部Abort未耐久提交");
            Assert(registry.Count == 0, "终态提交后current身份未移除");
            Assert(registry.TryGetLastExecution(4, out var tombstone) &&
                   ReferenceEquals(tombstone, oldAttempt), "旧execution tombstone未保留");
            Assert(!registry.TryRegister(nextAttempt),
                "旧Runner未退出时错误接纳了新attempt");

            registry.MarkExecutionCompleted(oldAttempt);
            Assert(registry.TryRegister(nextAttempt), "旧Runner退出后仍拒绝新attempt");
            registry.MarkExecutionCompleted(nextAttempt);
            Assert(registry.TryRemoveExact(nextAttempt), "新attempt清理失败");
        }

        private static void ExecutionQuiescenceWaitsThenAllowsNextAttempt()
        {
            var registry = new CycleAttemptRegistry();
            using var oldAttempt = NewContext(channel: 5, attemptId: 70, cycle: 700);
            using var nextAttempt = NewContext(channel: 5, attemptId: 71, cycle: 701);
            Assert(registry.TryRegister(oldAttempt), "旧attempt注册失败");
            oldAttempt.MarkBeginSucceeded();
            Assert(oldAttempt.MarkExecutionStarted(), "旧attempt执行区启动失败");
            Assert(oldAttempt.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                "旧attempt外部终态失败");

            var wait = registry.WaitForPreviousExecutionAsync(
                5,
                2000,
                CancellationToken.None);
            Assert(!wait.Wait(50), "旧Runner门未释放时等待错误提前完成");
            Assert(!registry.TryRegister(nextAttempt),
                "等待期间错误创建新attempt（可能读取旧LastCycleOutcome）");

            registry.MarkExecutionCompleted(oldAttempt);
            Assert(wait.GetAwaiter().GetResult(), "旧Runner退出后异步等待未成功");
            Assert(registry.TryRegister(nextAttempt), "quiescence后新attempt未获准");
            nextAttempt.MarkBeginSucceeded();
            Assert(nextAttempt.MarkExecutionStarted(), "新attempt无法进入独立执行区");
            registry.MarkExecutionCompleted(nextAttempt);
            Assert(registry.TryRemoveExact(nextAttempt), "新attempt清理失败");
        }

        private static void StaleExecutionCompletionCannotClearNewTombstone()
        {
            var registry = new CycleAttemptRegistry();
            using var oldAttempt = NewContext(channel: 8, attemptId: 80, cycle: 800);
            using var nextAttempt = NewContext(channel: 8, attemptId: 81, cycle: 801);
            Assert(registry.TryRegister(oldAttempt), "旧attempt注册失败");
            oldAttempt.MarkBeginSucceeded();
            Assert(oldAttempt.MarkExecutionStarted(), "旧attempt执行区启动失败");
            Assert(oldAttempt.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                "旧attempt终态失败");

            // 先只发布完成，再让新attempt原子替换已完成tombstone；模拟旧finally迟到清理。
            oldAttempt.MarkExecutionCompleted();
            Assert(registry.TryRegister(nextAttempt), "已完成旧tombstone仍阻止新attempt");
            Assert(!registry.TryClearExecutionTombstoneExact(oldAttempt),
                "旧execution迟到清理错误删除了新tombstone");
            Assert(registry.TryGetLastExecution(8, out var current) &&
                   ReferenceEquals(current, nextAttempt), "新execution tombstone身份丢失");

            registry.MarkExecutionCompleted(nextAttempt);
            Assert(registry.TryRemoveExact(nextAttempt), "新attempt清理失败");
        }

        private static void HardwareActionsWaitForExecutionQuiescence()
        {
            AssertHardwareActionWaitsForExecution(channel: 6, attemptId: 90, "PowerReady");
            AssertHardwareActionWaitsForExecution(channel: 9, attemptId: 91, "PreRelease");
        }

        private static void AssertHardwareActionWaitsForExecution(
            int channel,
            long attemptId,
            string stage)
        {
            var registry = new CycleAttemptRegistry();
            using var oldAttempt = NewContext(channel, attemptId, (int)(900 + attemptId));
            Assert(registry.TryRegister(oldAttempt), $"{stage}旧attempt注册失败");
            oldAttempt.MarkBeginSucceeded();
            Assert(oldAttempt.MarkExecutionStarted(), $"{stage}旧execution启动失败");
            Assert(oldAttempt.AbortOnce(() => true, item => registry.TryRemoveExact(item)),
                $"{stage}外部Abort失败");

            var actionCalls = 0;
            var blocked = false;
            try
            {
                EpbManager.InvokeAfterCycleExecutionQuiescenceAsync(
                        new[] { channel },
                        (candidate, token) => registry.WaitForPreviousExecutionAsync(
                            candidate,
                            30,
                            token),
                        _ =>
                        {
                            Interlocked.Increment(ref actionCalls);
                            return Task.CompletedTask;
                        },
                        stage,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (InvalidOperationException ex)
            {
                blocked = ex.Message.Contains("CycleExecutionQuiescenceTimeout");
            }
            Assert(blocked, $"{stage}未在旧execution未退出时明确阻止硬件动作");
            Assert(actionCalls == 0, $"{stage}在旧execution未退出时调用了硬件委托");

            registry.MarkExecutionCompleted(oldAttempt);
            EpbManager.InvokeAfterCycleExecutionQuiescenceAsync(
                    new[] { channel },
                    (candidate, token) => registry.WaitForPreviousExecutionAsync(
                        candidate,
                        30,
                        token),
                    _ =>
                    {
                        Interlocked.Increment(ref actionCalls);
                        return Task.CompletedTask;
                    },
                    stage,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(actionCalls == 1, $"{stage}在旧execution退出后未且仅调用一次硬件委托");
        }

        private static CycleAttemptContext NewContext(int channel, long attemptId, int cycle)
        {
            return new CycleAttemptContext(
                Guid.NewGuid(),
                7,
                channel <= 6 ? "Dev1" : "Dev2",
                channel,
                attemptId,
                CycleAttemptKind.FormalBatch,
                cycle);
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
