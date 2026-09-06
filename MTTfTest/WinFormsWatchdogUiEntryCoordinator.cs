using System;
using System.Threading.Tasks;

namespace MTEmbTest
{
    /// <summary>
    /// Production batch-entry gate.  The caller supplies the already
    /// evaluated exact Attached/Ready decision; this component is the only
    /// boundary that may invoke StartNewBatch, making a failed UI/transport
    /// gate structurally incapable of starting a batch.
    /// </summary>
    internal static class WinFormsWatchdogUiEntryCoordinator
    {
        internal static Task<T> StartIfAllowedAsync<T>(
            WinFormsWatchdogUiEntryDecision decision,
            Func<Task<T>> start)
        {
            if (decision == null)
                return Task.FromException<T>(
                    new InvalidOperationException("Watchdog UI入口策略缺失。"));
            if (!decision.Allowed)
                return Task.FromException<T>(
                    new InvalidOperationException(
                        "Watchdog UI入口策略拒绝启动批次：" + decision.Reason));
            if (start == null)
                return Task.FromException<T>(
                    new InvalidOperationException("Batch start callback缺失。"));
            return start();
        }
    }
}
