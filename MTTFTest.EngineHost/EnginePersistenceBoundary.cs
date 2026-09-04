using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MTTFTest.EngineHost
{
    /// <summary>The mandatory acquisition-publish boundary, also when continuous Raw is disabled.</summary>
    internal sealed class EnginePersistenceBoundary
    {
        private readonly Func<long, long, int, CancellationToken, Task<bool>> _drain;
        private readonly int _timeoutMs;

        internal EnginePersistenceBoundary(Func<long, long, int, CancellationToken, Task<bool>> drain,
            int timeoutMs = 10000)
        {
            _drain = drain ?? throw new ArgumentNullException(nameof(drain));
            if (timeoutMs < 1 || timeoutMs > 10000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            _timeoutMs = timeoutMs;
        }

        internal async Task FlushAsync(IReadOnlyDictionary<string, long> boundaries, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (boundaries == null || !boundaries.TryGetValue("Dev1", out var dev1) ||
                !boundaries.TryGetValue("Dev2", out var dev2) || dev1 < 0 || dev2 < 0)
                throw new InvalidOperationException("PersistenceBoundaryMissing");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_timeoutMs);
            var watch = Stopwatch.StartNew();
            var drainToken = deadline.Token;
            // A synchronous prefix inside a driver adapter must not prevent the
            // caller from reaching the deadline race.
            var drain = Task.Run(() => _drain(dev1, dev2, _timeoutMs, drainToken), CancellationToken.None);
            var winner = await Task.WhenAny(drain, Task.Delay(_timeoutMs, token)).ConfigureAwait(false);
            if (winner != drain || token.IsCancellationRequested)
            {
                deadline.Cancel();
                _ = drain.ContinueWith(task => { var observed = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("PersistencePublisherDrainDeadline");
            }
            if (!await drain.ConfigureAwait(false) || watch.ElapsedMilliseconds > _timeoutMs)
                throw new TimeoutException("PersistencePublisherBoundaryNotReached");
            // Controller subsequently waits for the recorder's durable prefix
            // and seals the cycle. Publishing alone is not a physical safety proof.
        }
    }
}
