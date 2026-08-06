using System;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>表示本次尝试只因软件/证据瞬态失败，可以安全作废后重做。</summary>
    internal sealed class SoftwareSelfHealingRetryException : Exception
    {
        public SoftwareSelfHealingRetryException(string message) : base(message) { }
        public SoftwareSelfHealingRetryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// 软件瞬态持续自愈循环。只捕获明确的 SoftwareSelfHealingRetryException；
    /// 硬件、配置和控制异常保持原异常语义，停止令牌可以在尝试、回调或退避期间立即退出。
    /// </summary>
    internal static class SoftwareSelfHealingLoop
    {
        public static async Task<int> RunAsync(
            Func<int, CancellationToken, Task> attemptAsync,
            Func<int, SoftwareSelfHealingRetryException, CancellationToken, Task> beforeRetryAsync,
            Func<int, int> delayMsSelector,
            CancellationToken token)
        {
            if (attemptAsync == null) throw new ArgumentNullException(nameof(attemptAsync));
            if (delayMsSelector == null) throw new ArgumentNullException(nameof(delayMsSelector));

            var attempt = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    await attemptAsync(attempt, token).ConfigureAwait(false);
                    return attempt;
                }
                catch (SoftwareSelfHealingRetryException ex)
                {
                    token.ThrowIfCancellationRequested();
                    if (beforeRetryAsync != null)
                        await beforeRetryAsync(attempt, ex, token).ConfigureAwait(false);

                    var delayMs = Math.Max(0, delayMsSelector(attempt));
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
            }
        }
    }
}
