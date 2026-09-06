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
            Run("Begin阻塞期间终态不触碰Recorder且禁止Runner启动", BeginBlockedTerminalDoesNotTouchRecorder, ref passed);
            Run("外部Abort后旧Runner未退出则拒绝新attempt", ExternalAbortKeepsExecutionTombstone, ref passed);
            Run("旧Runner退出前异步等待且退出后允许新attempt", ExecutionQuiescenceWaitsThenAllowsNextAttempt, ref passed);
            Run("旧execution迟到完成不得清除新tombstone", StaleExecutionCompletionCannotClearNewTombstone, ref passed);
            Run("启动供电与恢复预释放均受execution门保护", HardwareActionsWaitForExecutionQuiescence, ref passed);
            Run("已开始圈的异常fallback必须等待耐久封圈",
                FormalFallbackRequiresDurableTerminal, ref passed);
            Run("圈尝试冻结开始时DAQ代次与序号", AttemptFreezesDaqIdentity, ref passed);
            return passed;
        }

        private static void AttemptFreezesDaqIdentity()
        {
            using var context = new CycleAttemptContext(
                Guid.NewGuid(),
                9,
                "Dev2",
                17,
                24680,
                11,
                7001,
                CycleAttemptKind.FormalBatch,
                321);
            Assert(context.DaqGeneration == 17 && context.DaqBeginSequence == 24680,
                "圈尝试没有保留开始时DAQ身份，恢复换代后可能用新代次封旧圈");
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
