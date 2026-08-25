using System;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;

namespace AdaptiveControlTests
{
    internal static class WatchdogAttachWaitCoordinatorTests
    {
        private sealed class Snapshot
        {
            internal bool Exact { get; set; }
            internal string Name { get; set; }
        }

        private sealed class ManualDeadlineScheduler : IAttachDeadlineScheduler
        {
            private readonly TaskCompletionSource<bool> _due =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int Calls { get; private set; }
            internal TimeSpan LastTimeout { get; private set; }

            public Task Delay(TimeSpan timeout)
            {
                Calls++;
                LastTimeout = timeout;
                return _due.Task;
            }

            internal void Release() => _due.TrySetResult(true);
        }

        internal static int RunAll()
        {
            var passed = 0;
            Run("附加等待允许Start延后但先接受exact Attached", DeferredStartAcceptsAttached, ref passed);
            Run("Start先完成时仍等待Attached", CompletedStartStillWaitsForAttached, ref passed);
            Run("附加失败原样保留异常对象", TypedFailureIsPreserved, ref passed);
            Run("deadline与Attached同时完成时只做一次最终快照", DeadlineAttachedUsesOneFinalCapture, ref passed);
            Run("deadline先到且最终快照非Attached时超时", DeadlineNonExactIsTimeout, ref passed);
            Run("超时后的迟到Attached不能反转决定", LateAttachedCannotReverseTimeout, ref passed);
            return passed;
        }

        private static void DeferredStartAcceptsAttached()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            var exact = new Snapshot { Exact = true, Name = "attached" };
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () => exact,
                snapshot => snapshot != null && snapshot.Exact);

            attached.SetResult(exact);
            Assert(ReferenceEquals(wait.GetAwaiter().GetResult(), exact),
                "Start尚未完成时exact Attached未返回");
            Assert(scheduler.Calls == 1 && scheduler.LastTimeout == TimeSpan.FromSeconds(5),
                "附加deadline scheduler未按单一入口调用");
        }

        private static void CompletedStartStillWaitsForAttached()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            start.SetResult(true);
            var exact = new Snapshot { Exact = true, Name = "after-start" };
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () => exact,
                snapshot => snapshot != null && snapshot.Exact);

            Assert(!wait.IsCompleted, "Start完成后错误地把非Attached视为成功");
            attached.SetResult(exact);
            Assert(ReferenceEquals(wait.GetAwaiter().GetResult(), exact),
                "Start先完成后未等待exact Attached");
        }

        private static void TypedFailureIsPreserved()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            var expected = new InvalidOperationException("typed attach failure");
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () => new Snapshot { Exact = true },
                snapshot => snapshot != null && snapshot.Exact);

            failure.SetResult(expected);
            try
            {
                wait.GetAwaiter().GetResult();
                throw new InvalidOperationException("typed failure未传播");
            }
            catch (InvalidOperationException actual)
            {
                Assert(ReferenceEquals(actual, expected),
                    "typed failure未原样传播");
            }
        }

        private static void DeadlineAttachedUsesOneFinalCapture()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            start.SetResult(true);
            var captures = 0;
            var exact = new Snapshot { Exact = true, Name = "final-exact" };
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () =>
                {
                    captures++;
                    return exact;
                },
                snapshot => snapshot != null && snapshot.Exact);

            scheduler.Release();
            attached.SetResult(new Snapshot { Exact = true, Name = "attached-race" });
            Assert(ReferenceEquals(wait.GetAwaiter().GetResult(), exact),
                "deadline/Attached竞争未以最终exact快照为准");
            Assert(captures == 1, "deadline/Attached竞争最终快照读取次数不是1");
        }

        private static void DeadlineNonExactIsTimeout()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            start.SetResult(true);
            var captures = 0;
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () =>
                {
                    captures++;
                    return new Snapshot { Exact = false, Name = "not-attached" };
                },
                snapshot => snapshot != null && snapshot.Exact);

            scheduler.Release();
            try
            {
                wait.GetAwaiter().GetResult();
                throw new InvalidOperationException("非Attached最终快照未超时");
            }
            catch (TimeoutException) { }
            Assert(captures == 1, "deadline非Attached最终快照读取次数不是1");
        }

        private static void LateAttachedCannotReverseTimeout()
        {
            var scheduler = new ManualDeadlineScheduler();
            var coordinator = new WatchdogAttachWaitCoordinator(scheduler);
            var attached = NewTcs<Snapshot>();
            var failure = NewTcs<Exception>();
            var start = NewTcs<bool>();
            start.SetResult(true);
            var wait = coordinator.WaitAsync(
                attached.Task,
                failure.Task,
                start.Task,
                TimeSpan.FromSeconds(5),
                () => new Snapshot { Exact = false, Name = "timed-out" },
                snapshot => snapshot != null && snapshot.Exact);

            scheduler.Release();
            try
            {
                wait.GetAwaiter().GetResult();
                throw new InvalidOperationException("超时结果未保留");
            }
            catch (TimeoutException) { }
            attached.SetResult(new Snapshot { Exact = true, Name = "late" });
            Assert(wait.IsFaulted && wait.Exception.InnerException is TimeoutException,
                "迟到Attached反转了既定超时结果");
        }

        private static TaskCompletionSource<T> NewTcs<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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
