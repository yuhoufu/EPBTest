using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Timing;

namespace AdaptiveControlTests
{
    internal static class CoordinatedTimerTests
    {
        public static int RunAll()
        {
            var passed = 0;
            Run("协调模式不叠加外层固定周期等待", DoesNotAddPeriodicDelay, ref passed);
            foreach (OverrunPolicy policy in Enum.GetValues(typeof(OverrunPolicy)))
            {
                var selectedPolicy = policy;
                Run("协调等待不触发旧超时策略：" + policy,
                    () => ExternalWaitDoesNotApplyOverrunPolicy(selectedPolicy), ref passed);
            }
            Run("协调模式优雅暂停保留当前圈及状态事件", GracefulPausePreservesLifecycle, ref passed);
            Run("协调模式安全暂停不同步调用观察者", SafetyPauseFencesNextCycle, ref passed);
            Run("协调模式恢复不使用旧相对延迟",
                () => ResumeIgnoresLegacyDelay(timer => timer.ResumeAtNextBoundary(5000)), ref passed);
            Run("协调模式恢复不使用旧UTC延迟",
                () => ResumeIgnoresLegacyDelay(timer => timer.ResumeAtUtcBoundary(DateTime.UtcNow.AddSeconds(5))),
                ref passed);
            Run("协调模式停止取消外部等待", StopCancelsExternalWait, ref passed);
            Run("协调模式停止后收尾不得覆盖Stopped",
                () => StopDuringGracefulPauseRemainsStopped(true), ref passed);
            Run("原入口停止后收尾不得覆盖Stopped",
                () => StopDuringGracefulPauseRemainsStopped(false), ref passed);
            Run("协调模式回调失败保留连续序号与异常记录", WorkFailuresKeepExistingContract, ref passed);
            Run("协调模式运行故障发布Faulted", RuntimeFailurePublishesFaulted, ref passed);
            Run("协调准入等待期间暂停不会再启动动作", PauseCancelsAdmissionBeforeAction, ref passed);
            Run("协调动作开始后暂停不取消当前动作", PauseAfterAdmissionKeepsActionAlive, ref passed);
            Run("暂停已登记时协调动作不得开始", PendingPauseRejectsActionAdmission, ref passed);
            Run("快速恢复不能重新放行已取消的旧准入", ResumeDoesNotReopenCanceledAdmission, ref passed);
            Run("协调动作准入与暂停竞争保持一致", AdmissionAndPauseRaceRemainsConsistent, ref passed);
            Run("安全暂停不调用协调准入取消观察者", SafetyPauseDoesNotCancelAdmissionInline, ref passed);
            Run("原入口仍保留周期等待和超时策略", LegacyStartKeepsScheduling, ref passed);
            return passed;
        }

        private static void DoesNotAddPeriodicDelay()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.AlignToWallClock);
            var cycles = new List<int>();
            var run = timer.StartCoordinatedAsync(3, (cycle, token) =>
            {
                cycles.Add(cycle);
                return Task.FromResult(true);
            });
            try
            {
                Await(run, "外部协调回调已完成，但仍等待定时器自身的固定周期。", 1000);
                Assert(cycles.SequenceEqual(new[] { 1, 2, 3 }), "协调回调序号不连续。");
                Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.Completed && !timer.IsRunning,
                    "有限次数完成后未发布Completed或仍标记运行。");
                Assert(timer.StartedUtc.HasValue && timer.LastCycleStartedUtc.HasValue &&
                       timer.LastCycleCompletedUtc >= timer.LastCycleStartedUtc,
                    "协调模式未维护运行和圈起止时间。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void ExternalWaitDoesNotApplyOverrunPolicy(OverrunPolicy policy)
        {
            var logger = new RecordingLogger();
            var timer = new HighPrecisionTimer(5, policy, logger);
            var cycles = new List<int>();
            var run = timer.StartCoordinatedAsync(3, async (cycle, token) =>
            {
                cycles.Add(cycle);
                await Task.Delay(40, token);
                return true;
            });
            try
            {
                Await(run, "外部协调等待被旧超时策略阻断。");
                Assert(cycles.SequenceEqual(new[] { 1, 2, 3 }),
                    "外部协调等待超过period后被二次跳槽，实际序号=" + string.Join(",", cycles));
                Assert(logger.Warnings.Count == 0 && logger.Errors.Count == 0,
                    "外部协调等待被错误记录为定时器超时。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void GracefulPausePreservesLifecycle()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.AlignToWallClock);
            var entered = Signal();
            var release = Signal();
            var cycles = 0;
            var states = new List<HighPrecisionTimerStateChangedEvent>();
            timer.StateChanged += update => { lock (states) states.Add(update); };
            var run = timer.StartCoordinatedAsync(2, async (cycle, token) =>
            {
                Interlocked.Increment(ref cycles);
                if (cycle == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }
                return true;
            });
            try
            {
                Await(entered.Task, "协调首圈未进入。");
                var pause = timer.PauseAfterCurrentCycleAsync("CoordinatedGracefulPause");
                Assert(!pause.IsCompleted && timer.RuntimeState == HighPrecisionTimerRuntimeState.PausePending,
                    "尚未完成当前圈便确认暂停。");
                release.TrySetResult(true);
                Await(pause, "当前圈完成后未确认暂停。");
                Assert(SpinWait.SpinUntil(() => timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused, 1000),
                    "未发布Paused状态。");
                Thread.Sleep(40);
                Assert(timer.IsPaused && Volatile.Read(ref cycles) == 1 &&
                       timer.PauseReason == "CoordinatedGracefulPause" && timer.PauseUtc.HasValue,
                    "优雅暂停没有封住下一圈或遗漏暂停元数据。");
                Await(timer.PauseAfterCurrentCycleAsync("CoordinatedGracefulPause"), "重复暂停未完成。");
                timer.Resume();
                Await(run, "协调模式恢复后仍等待旧固定周期。", 1000);
                lock (states)
                {
                    var paused = states.FirstOrDefault(update => update.State == HighPrecisionTimerRuntimeState.Paused);
                    Assert(states.Any(update => update.State == HighPrecisionTimerRuntimeState.PausePending) &&
                           paused != null && paused.IsPaused && paused.IsRunning &&
                           paused.LastCycleCompletedUtc.HasValue &&
                           states.Any(update => update.State == HighPrecisionTimerRuntimeState.Running &&
                                                update.Reason == "Resume") &&
                           states.Last().State == HighPrecisionTimerRuntimeState.Completed,
                        "协调模式丢失优雅暂停、恢复或结束状态事件契约。");
                }
                Assert(Volatile.Read(ref cycles) == 2 && timer.PauseReason == string.Empty && !timer.PauseUtc.HasValue,
                    "恢复未继续下一圈或未清理暂停原因。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void SafetyPauseFencesNextCycle()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.AlignToWallClock);
            var entered = Signal();
            var release = Signal();
            var cycles = 0;
            var notifications = 0;
            var run = timer.StartCoordinatedAsync(2, async (cycle, token) =>
            {
                Interlocked.Increment(ref cycles);
                if (cycle == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }
                return true;
            });
            try
            {
                Await(entered.Task, "安全暂停测试首圈未进入。");
                timer.StateChanged += update => Interlocked.Increment(ref notifications);
                timer.RequestSafetyPauseNonBlocking("CoordinatedSafetyPause");
                Assert(Volatile.Read(ref notifications) == 0 &&
                       timer.RuntimeState == HighPrecisionTimerRuntimeState.PausePending,
                    "安全暂停入口同步调用了观察者或遗漏PausePending。");
                release.TrySetResult(true);
                Assert(SpinWait.SpinUntil(() => timer.IsPaused, 1000), "安全暂停未在当前圈结束后封住下一圈。");
                Thread.Sleep(40);
                Assert(Volatile.Read(ref cycles) == 1, "安全暂停后仍进入下一圈。");
                timer.Stop();
                Await(run, "已暂停的协调定时器未能停止。");
                Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped && !timer.IsRunning,
                    "暂停后停止未保留Stopped状态。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void ResumeIgnoresLegacyDelay(Action<HighPrecisionTimer> resume)
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.AlignToWallClock);
            var entered = Signal();
            var release = Signal();
            var cycles = new List<int>();
            var run = timer.StartCoordinatedAsync(2, async (cycle, token) =>
            {
                cycles.Add(cycle);
                if (cycle == 1)
                {
                    entered.TrySetResult(true);
                    await release.Task;
                }
                return true;
            });
            try
            {
                Await(entered.Task, "恢复测试首圈未进入。");
                var pause = timer.PauseAfterCurrentCycleAsync("ResumeBoundaryTest");
                release.TrySetResult(true);
                Await(pause, "恢复测试未暂停。");
                Assert(SpinWait.SpinUntil(() => timer.RuntimeState == HighPrecisionTimerRuntimeState.Paused, 1000),
                    "恢复测试未发布Paused。");
                resume(timer);
                Await(run, "协调模式仍应用旧resumeDelay，叠加了外部协调器的边界等待。", 1000);
                Assert(cycles.SequenceEqual(new[] { 1, 2 }), "恢复后重复或跳过圈序号。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void StopCancelsExternalWait()
        {
            var timer = new HighPrecisionTimer(5, OverrunPolicy.Throw);
            var entered = Signal();
            var canceled = Signal();
            var cycles = 0;
            var run = timer.StartCoordinatedAsync(null, async (cycle, token) =>
            {
                Interlocked.Increment(ref cycles);
                entered.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    canceled.TrySetResult(true);
                    throw;
                }
                return true;
            });
            try
            {
                Await(entered.Task, "停止测试未进入外部等待。");
                timer.Stop();
                Await(canceled.Task, "Stop没有取消传给外部协调回调的token。");
                Await(run, "Stop未结束外部等待。");
                Assert(Volatile.Read(ref cycles) == 1 && !timer.IsRunning &&
                       timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped,
                    "取消后继续下一圈或覆盖了Stopped状态。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void WorkFailuresKeepExistingContract()
        {
            var logger = new RecordingLogger();
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw, logger);
            var cycles = new List<int>();
            var failure = new InvalidOperationException("CoordinatedWorkFailure");
            var run = timer.StartCoordinatedAsync(3, (cycle, token) =>
            {
                cycles.Add(cycle);
                if (cycle == 2) throw failure;
                return Task.FromResult(cycle == 3);
            });
            try
            {
                Await(run, "失败回调阻断了既有继续执行契约。", 1000);
                Assert(cycles.SequenceEqual(new[] { 1, 2, 3 }) &&
                       timer.RuntimeState == HighPrecisionTimerRuntimeState.Completed &&
                       logger.Warnings.Count == 2 && logger.Errors.Count == 1 &&
                       ReferenceEquals(logger.Errors[0], failure),
                    "失败或异常回调的序号、记录及最终状态与旧入口不一致。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void StopDuringGracefulPauseRemainsStopped(bool coordinated)
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.AlignToWallClock);
            var entered = Signal();
            var release = Signal();
            var states = new List<HighPrecisionTimerRuntimeState>();
            timer.StateChanged += update => { lock (states) states.Add(update.State); };
            Func<int, CancellationToken, Task<bool>> work = async (cycle, token) =>
            {
                entered.TrySetResult(true);
                await release.Task;
                return true;
            };
            var run = coordinated
                ? timer.StartCoordinatedAsync(2, work)
                : timer.StartAsync(2, 0, work);
            try
            {
                Await(entered.Task, "停止收尾测试首圈未进入。");
                var pause = timer.PauseAfterCurrentCycleAsync("StopDuringGracefulPause");
                timer.Stop();
                Await(pause, "停止未释放待确认的优雅暂停任务。");
                Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped,
                    "Stop返回时未公开Stopped。");
                release.TrySetResult(true);
                Await(run, "停止后当前回调未能收尾。");
                lock (states)
                {
                    Assert(timer.RuntimeState == HighPrecisionTimerRuntimeState.Stopped &&
                           !timer.IsRunning && states.Last() == HighPrecisionTimerRuntimeState.Stopped,
                        "Stop后的优雅暂停收尾把Stopped覆盖成了Paused。");
                }
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void RuntimeFailurePublishesFaulted()
        {
            var failure = new InvalidOperationException("CoordinatedRuntimeFailure");
            var logger = new RecordingLogger { WarningFailure = failure };
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw, logger);
            var states = new List<HighPrecisionTimerRuntimeState>();
            timer.StateChanged += update => states.Add(update.State);
            var run = timer.StartCoordinatedAsync(1, (cycle, token) => Task.FromResult(false));
            try
            {
                Exception caught = null;
                try { Await(run, "运行故障未结束协调定时器。"); }
                catch (InvalidOperationException ex) { caught = ex; }
                Assert(ReferenceEquals(caught, failure) && !timer.IsRunning &&
                       timer.RuntimeState == HighPrecisionTimerRuntimeState.Faulted &&
                       states.Last() == HighPrecisionTimerRuntimeState.Faulted,
                    "循环本身的故障没有传播异常并保留Faulted状态。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void LegacyStartKeepsScheduling()
        {
            var timer = new HighPrecisionTimer(120, OverrunPolicy.AlignToWallClock);
            var starts = new List<long>();
            var clock = Stopwatch.StartNew();
            var run = timer.StartAsync(2, 80, (cycle, token) =>
            {
                starts.Add(clock.ElapsedMilliseconds);
                return Task.FromResult(true);
            });
            try
            {
                Await(run, "旧入口未正常完成。");
                Assert(starts.Count == 2 && starts[0] >= 60 && starts[1] - starts[0] >= 90,
                    "新增协调模式改变了旧入口的首延迟或固定周期。");
            }
            finally { StopAndDrain(timer, run); }

            var overrun = new HighPrecisionTimer(5, OverrunPolicy.Throw);
            var failedRun = overrun.StartAsync(1, 0, async (cycle, token) =>
            {
                await Task.Delay(40, token);
                return true;
            });
            try
            {
                var faulted = false;
                try { Await(failedRun, "旧入口超时未结束。"); }
                catch (TimeoutException) { faulted = true; }
                Assert(faulted && overrun.RuntimeState == HighPrecisionTimerRuntimeState.Faulted,
                    "新增协调模式禁用了旧入口的Throw超时策略。");
            }
            finally { StopAndDrain(overrun, failedRun); }
        }

        private static void PauseCancelsAdmissionBeforeAction()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
            var waiting = Signal();
            var actions = 0;
            var originalToken = CancellationToken.None;
            var run = timer.StartCoordinatedAsync(2, async (cycle, token) =>
            {
                originalToken = token;
                using (var admission = CancellationTokenSource.CreateLinkedTokenSource(
                           token, timer.CoordinatedAdmissionToken))
                {
                    if (cycle == 1)
                    {
                        waiting.TrySetResult(true);
                        await Task.Delay(5000, admission.Token);
                    }
                }
                if (!timer.TryBeginCoordinatedAction()) return false;
                Interlocked.Increment(ref actions);
                return true;
            }, pauseBeforeAdmission: true);
            try
            {
                Await(waiting.Task, "准入等待未开始。");
                Await(timer.PauseAfterCurrentCycleAsync("PauseBeforeAdmission"),
                    "尚未开始动作的下一槽等待没有被暂停及时取消。", 1000);
                Assert(Volatile.Read(ref actions) == 0 && !originalToken.IsCancellationRequested,
                    "准入暂停多执行了一圈，或错误取消了计时器运行令牌。");
                timer.Resume();
                Await(run, "准入暂停恢复后未能启动新回调。");
                Assert(Volatile.Read(ref actions) == 1, "准入暂停恢复后动作次数错误。");
            }
            finally { StopAndDrain(timer, run); }
        }

        private static void PauseAfterAdmissionKeepsActionAlive()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
            var entered = Signal();
            var release = Signal();
            var originalToken = CancellationToken.None;
            var admissionToken = CancellationToken.None;
            var run = timer.StartCoordinatedAsync(null, async (cycle, token) =>
            {
                originalToken = token;
                admissionToken = timer.CoordinatedAdmissionToken;
                Assert(timer.TryBeginCoordinatedAction(), "无暂停请求时动作准入被拒绝。");
                entered.TrySetResult(true);
                await release.Task;
                return true;
            }, pauseBeforeAdmission: true);
            try
            {
                Await(entered.Task, "动作未能开始。");
                var pause = timer.PauseAfterCurrentCycleAsync("PauseAfterAdmission");
                Assert(!pause.IsCompleted && !originalToken.IsCancellationRequested &&
                       !admissionToken.IsCancellationRequested,
                    "动作已经开始，优雅暂停却提前确认或取消了当前动作。");
                release.TrySetResult(true);
                Await(pause, "已获准动作自然结束后未确认暂停。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void PendingPauseRejectsActionAdmission()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
            var waiting = Signal();
            var release = Signal();
            var actions = 0;
            var run = timer.StartCoordinatedAsync(null, async (cycle, token) =>
            {
                waiting.TrySetResult(true);
                await release.Task;
                if (!timer.TryBeginCoordinatedAction()) return false;
                Interlocked.Increment(ref actions);
                return true;
            }, pauseBeforeAdmission: true);
            try
            {
                Await(waiting.Task, "准入竞争测试未开始。");
                timer.Pause("PauseBeforeTryBegin");
                release.TrySetResult(true);
                Await(timer.PauseAfterCurrentCycleAsync("PauseBeforeTryBegin"), "暂停未确认。");
                Assert(Volatile.Read(ref actions) == 0, "已经登记的暂停仍允许新动作开始。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void AdmissionAndPauseRaceRemainsConsistent()
        {
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
                var waiting = Signal();
                var race = Signal();
                var actionDecision = Signal();
                var pauseRequested = Signal();
                var release = Signal();
                var admissionToken = CancellationToken.None;
                var run = timer.StartCoordinatedAsync(null, async (cycle, token) =>
                {
                    admissionToken = timer.CoordinatedAdmissionToken;
                    waiting.TrySetResult(true);
                    await race.Task;
                    var admitted = timer.TryBeginCoordinatedAction();
                    actionDecision.TrySetResult(admitted);
                    await release.Task;
                    return admitted;
                }, pauseBeforeAdmission: true);
                Task pauseWorker = null;
                try
                {
                    Await(waiting.Task, "准入竞争回调未开始。");
                    pauseWorker = Task.Run(async () =>
                    {
                        await race.Task;
                        var pause = timer.PauseAfterCurrentCycleAsync("ConcurrentAdmissionPause");
                        pauseRequested.TrySetResult(true);
                        await pause;
                    });
                    race.TrySetResult(true);
                    Await(actionDecision.Task, "准入竞争没有作出动作决定。");
                    Await(pauseRequested.Task, "准入竞争没有登记暂停。");
                    Assert(actionDecision.Task.Result != admissionToken.IsCancellationRequested,
                        "暂停与动作开始未原子决策：已经开始却被取消，或被拒绝却没有取消准入。");
                    release.TrySetResult(true);
                    Await(pauseWorker, "竞争后的暂停未完成。");
                }
                finally
                {
                    race.TrySetResult(true);
                    release.TrySetResult(true);
                    StopAndDrain(timer, run);
                    if (pauseWorker != null) Await(pauseWorker, "竞争测试暂停任务未回收。");
                }
            }
        }

        private static void ResumeDoesNotReopenCanceledAdmission()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
            var waiting = Signal();
            var release = Signal();
            var actions = 0;
            var run = timer.StartCoordinatedAsync(1, async (cycle, token) =>
            {
                waiting.TrySetResult(true);
                await release.Task;
                if (!timer.TryBeginCoordinatedAction()) return false;
                Interlocked.Increment(ref actions);
                return true;
            }, pauseBeforeAdmission: true);
            try
            {
                Await(waiting.Task, "快速恢复测试未进入准入等待。");
                timer.Pause("PauseBeforeFastResume");
                timer.Resume();
                release.TrySetResult(true);
                Await(run, "快速恢复测试未结束。");
                Assert(Volatile.Read(ref actions) == 0, "恢复重新放行了暂停时已经撤销的旧动作准入。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static void SafetyPauseDoesNotCancelAdmissionInline()
        {
            var timer = new HighPrecisionTimer(5000, OverrunPolicy.Throw);
            var waiting = Signal();
            var release = Signal();
            var canceled = 0;
            var actions = 0;
            var run = timer.StartCoordinatedAsync(null, async (cycle, token) =>
            {
                using (timer.CoordinatedAdmissionToken.Register(() => Interlocked.Increment(ref canceled)))
                {
                    waiting.TrySetResult(true);
                    await release.Task;
                    if (timer.TryBeginCoordinatedAction()) Interlocked.Increment(ref actions);
                }
                return true;
            }, pauseBeforeAdmission: true);
            try
            {
                Await(waiting.Task, "安全暂停准入测试未开始。");
                timer.RequestSafetyPauseNonBlocking("SafetyPauseDuringAdmission");
                Assert(Volatile.Read(ref canceled) == 0, "安全暂停同步执行了准入取消观察者。");
                release.TrySetResult(true);
                Assert(SpinWait.SpinUntil(() => timer.IsPaused, 1000), "安全暂停未封住新动作。");
                Assert(Volatile.Read(ref actions) == 0, "安全暂停标志被动作准入忽略。");
            }
            finally
            {
                release.TrySetResult(true);
                StopAndDrain(timer, run);
            }
        }

        private static TaskCompletionSource<bool> Signal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static void Await(Task task, string message, int timeoutMs = 2000)
        {
            Assert(ReferenceEquals(Task.WhenAny(task, Task.Delay(timeoutMs)).GetAwaiter().GetResult(), task), message);
            task.GetAwaiter().GetResult();
        }

        private static void StopAndDrain(HighPrecisionTimer timer, Task run)
        {
            if (!timer.IsRunning) return;
            timer.Stop();
            Assert(ReferenceEquals(Task.WhenAny(run, Task.Delay(2000)).GetAwaiter().GetResult(), run),
                "测试清理时定时器未停止。");
            try { run.GetAwaiter().GetResult(); }
            catch (Exception) { /* 故障断言由测试主体负责；此处只确认任务已终止。 */ }
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("[PASS] " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class RecordingLogger : IAppLogger
        {
            public readonly List<string> Warnings = new List<string>();
            public readonly List<Exception> Errors = new List<Exception>();
            public Exception WarningFailure;

            public void Info(string message, string category = null) { }
            public void Warn(string message, string category = null)
            {
                if (WarningFailure != null) throw WarningFailure;
                Warnings.Add(message);
            }
            public void Error(string message, string category = null, Exception ex = null) => Errors.Add(ex);
        }
    }
}
