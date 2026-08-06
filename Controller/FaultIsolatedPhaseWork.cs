using System;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// Executes one channel's learning/qualification work without allowing a channel- or
    /// resource-scoped failure to fault the shared Task.WhenAll barrier. Only cancellation
    /// of the phase owner is allowed to escape and cancel the whole phase.
    /// </summary>
    internal static class FaultIsolatedPhaseWork
    {
        internal static async Task RunAsync(
            Func<Task> work,
            CancellationToken phaseToken,
            Action<Exception, bool> isolate)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            if (isolate == null) throw new ArgumentNullException(nameof(isolate));

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!phaseToken.IsCancellationRequested)
            {
                // A channel stop token or a resource-group fault canceled this member only.
                isolate(ex, true);
            }
            catch (OperationCanceledException)
            {
                // Manual stop/restart owns the phase token and must still terminate the phase.
                throw;
            }
            catch (Exception ex)
            {
                isolate(ex, false);
            }
        }
    }
}
