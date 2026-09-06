using System;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// Production retry owner for one DAQ recovery terminal barrier.
    ///
    /// The supervisor deliberately knows nothing about hardware or context
    /// dictionaries.  It owns only the important lifecycle invariant: a
    /// failed terminal publication is retried by the same incident owner and
    /// cannot create a second worker.  EpbManager and deterministic seam tests
    /// use this exact type; tests do not reproduce a private retry loop.
    /// </summary>
    internal sealed class DaqRecoveryTerminalSupervisor
    {
        private int _retryScheduled;
        private Task _retryTask = Task.CompletedTask;

        internal bool RetryScheduled => Volatile.Read(ref _retryScheduled) != 0;

        internal Task RetryTask => Volatile.Read(ref _retryTask) ?? Task.CompletedTask;

        internal bool TryCompleteOrSchedule(
            Func<bool> tryComplete,
            Func<bool> isCurrent,
            Action onCompleted,
            Action<string> onExhausted,
            CancellationToken cancellationToken,
            string exhaustionReason,
            int maxAttempts = 8,
            int initialDelayMs = 100,
            int maxDelayMs = 2000,
            Action onFinished = null)
        {
            if (tryComplete == null || isCurrent == null)
            {
                try { onFinished?.Invoke(); } catch { }
                return false;
            }
            if (!isCurrent())
            {
                try { onFinished?.Invoke(); } catch { }
                return false;
            }

            bool completed;
            try { completed = tryComplete(); }
            catch { completed = false; }
            if (completed)
            {
                try { onCompleted?.Invoke(); }
                finally
                {
                    try { onFinished?.Invoke(); } catch { }
                }
                return true;
            }

            if (!isCurrent())
            {
                try { onFinished?.Invoke(); } catch { }
                return false;
            }
            if (Interlocked.CompareExchange(ref _retryScheduled, 1, 0) != 0)
                return false;

            maxAttempts = Math.Max(1, maxAttempts);
            initialDelayMs = Math.Max(1, initialDelayMs);
            maxDelayMs = Math.Max(initialDelayMs, maxDelayMs);
            _retryTask = Task.Run(async () =>
            {
                var delayMs = initialDelayMs;
                try
                {
                    for (var attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        if (cancellationToken.IsCancellationRequested || !isCurrent())
                            return;
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested || !isCurrent())
                            return;

                        bool retryCompleted;
                        try { retryCompleted = tryComplete(); }
                        catch { retryCompleted = false; }
                        if (retryCompleted)
                        {
                            onCompleted?.Invoke();
                            return;
                        }
                        delayMs = Math.Min(delayMs * 2, maxDelayMs);
                    }

                    if (isCurrent())
                        onExhausted?.Invoke(exhaustionReason ?? "DaqRecoveryTerminalRetryExhausted");
                }
                catch (OperationCanceledException)
                {
                    // Cancellation belongs to the same incident lifecycle;
                    // it is not a second recovery or process-restart signal.
                }
                finally
                {
                    Volatile.Write(ref _retryScheduled, 0);
                    try { onFinished?.Invoke(); } catch { }
                }
            });
            return false;
        }
    }
}
