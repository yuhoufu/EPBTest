using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog.Client
{
    /// <summary>
    /// Supplies the one deadline used by the attach gate.  Production uses
    /// Task.Delay; tests can provide a deterministic scheduler without
    /// replacing the attach arbitration algorithm.
    /// </summary>
    public interface IAttachDeadlineScheduler
    {
        Task Delay(TimeSpan timeout);
    }

    public sealed class TaskDelayAttachDeadlineScheduler : IAttachDeadlineScheduler
    {
        public Task Delay(TimeSpan timeout)
        {
            return Task.Delay(timeout);
        }
    }

    /// <summary>
    /// Owns the final race between an exact Attached result, a typed failure,
    /// the deferred Start task and the attach deadline.  The Runtime remains
    /// responsible for subscribing/unsubscribing Engine.StateChanged; this
    /// class only arbitrates the already-created tasks and performs the one
    /// final immutable snapshot read required by the deadline path.
    /// </summary>
    public sealed class WatchdogAttachWaitCoordinator
    {
        private readonly IAttachDeadlineScheduler _deadlineScheduler;

        public WatchdogAttachWaitCoordinator(
            IAttachDeadlineScheduler deadlineScheduler = null)
        {
            _deadlineScheduler = deadlineScheduler ?? new TaskDelayAttachDeadlineScheduler();
        }

        public async Task<TSnapshot> WaitAsync<TSnapshot>(
            Task<TSnapshot> attachedTask,
            Task<Exception> failureTask,
            Task startTask,
            TimeSpan deadline,
            Func<TSnapshot> captureFinalSnapshot,
            Func<TSnapshot, bool> isExactAttached)
        {
            if (attachedTask == null) throw new ArgumentNullException(nameof(attachedTask));
            if (failureTask == null) throw new ArgumentNullException(nameof(failureTask));
            if (startTask == null) throw new ArgumentNullException(nameof(startTask));
            if (captureFinalSnapshot == null) throw new ArgumentNullException(nameof(captureFinalSnapshot));
            if (isExactAttached == null) throw new ArgumentNullException(nameof(isExactAttached));
            if (deadline < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));

            var deadlineTask = _deadlineScheduler.Delay(deadline) ??
                               throw new InvalidOperationException(
                                   "Attach deadline scheduler returned a null task.");
            var startObserved = false;

            while (true)
            {
                var candidates = startObserved
                    ? new[] { (Task)attachedTask, failureTask, deadlineTask }
                    : new[] { (Task)attachedTask, failureTask, deadlineTask, startTask };
                var winner = await Task.WhenAny(candidates).ConfigureAwait(false);

                if (winner == attachedTask)
                {
                    // If deadline completion is concurrent with Attached,
                    // make one final immutable read.  The exact final state
                    // wins the race; a later/stale Attached task cannot
                    // reverse a timeout decision.
                    var attached = await attachedTask.ConfigureAwait(false);
                    if (deadlineTask.IsCompleted)
                        return ResolveFinalSnapshot(captureFinalSnapshot, isExactAttached);
                    return attached;
                }

                if (winner == failureTask)
                {
                    var failure = await failureTask.ConfigureAwait(false);
                    if (failure == null)
                        throw new InvalidOperationException("Watchdog attach failure task returned null.");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                    throw new InvalidOperationException("Unreachable attach failure path.");
                }

                if (winner == deadlineTask)
                    return ResolveFinalSnapshot(captureFinalSnapshot, isExactAttached);

                // Start is deliberately not a terminal success condition:
                // Start may finish before the Engine publishes exact Attached.
                // Observe its exception/result once, then continue waiting on
                // Attached/failure/deadline without re-registering Start.
                if (winner == startTask)
                {
                    await startTask.ConfigureAwait(false);
                    startObserved = true;
                    continue;
                }

                throw new InvalidOperationException("Unknown attach wait task completed.");
            }
        }

        private static TSnapshot ResolveFinalSnapshot<TSnapshot>(
            Func<TSnapshot> captureFinalSnapshot,
            Func<TSnapshot, bool> isExactAttached)
        {
            // This method is intentionally the only call site for the final
            // capture callback.  The caller invokes it at most once per wait.
            var finalSnapshot = captureFinalSnapshot();
            if (!Equals(finalSnapshot, null) && isExactAttached(finalSnapshot))
                return finalSnapshot;
            throw new TimeoutException("Watchdog精确Attached等待超过共享传输策略期限。");
        }
    }
}
