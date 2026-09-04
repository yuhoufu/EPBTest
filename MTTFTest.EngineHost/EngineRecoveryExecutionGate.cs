using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // A process-local executor fence, never a recovery owner. The Supervisor's
    // journal chooses commands. At most one normal and one safety executor exist.
    internal sealed class EngineRecoveryExecutionGate
    {
        private readonly object _gate = new object();
        private long _highestSequence;
        private Lease _normal;
        private Lease _safety;
        private bool _initializationAdmitted;

        internal Lease BeginInitialization(CancellationToken token)
        {
            lock (_gate)
            {
                if (_initializationAdmitted || _highestSequence != 0 || _normal != null || _safety != null)
                    throw new InvalidOperationException("EngineInitializationAlreadyAdmittedOrSuperseded");
                _initializationAdmitted = true;
                // Bootstrap is an executor, not a second recovery owner. Register
                // before opening pipes so STOP can fence/join actual initialization.
                return _normal = new Lease(this, 0, false, null, token);
            }
        }

        internal Lease Begin(RecoveryCommand command, CancellationToken token)
        {
            if (command?.IsStructurallyValid() != true) throw new ArgumentException("RecoveryCommandInvalid");
            Lease lease;
            Lease interrupted;
            var safety = EngineHostProtocol.IsPrioritySafetyCommand(command.Kind);
            lock (_gate)
            {
                if (command.CommandSequence <= _highestSequence) throw new InvalidOperationException("RecoveryCommandRetiredByNewerRevision");
                if (safety ? _safety != null : _normal != null || _safety != null)
                    throw new InvalidOperationException("RecoveryExecutorStillOwned");
                interrupted = safety ? _normal : null;
                lease = new Lease(this, command.CommandSequence, safety, interrupted?.Completed, token);
                _highestSequence = command.CommandSequence;
                if (safety) _safety = lease; else _normal = lease;
            }
            // Cancellation callbacks are untrusted latency. They cannot delay the
            // safety executor's first hardware-off call or run under the fence lock.
            interrupted?.RequestCancellation();
            return lease;
        }

        internal sealed class Lease : IDisposable
        {
            private readonly EngineRecoveryExecutionGate _owner;
            private readonly long _sequence;
            private readonly bool _safety;
            private readonly CancellationTokenSource _cancel;
            private readonly TaskCompletionSource<bool> _completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;
            private int _cancelRequested;
            internal Task InterruptedExecutor { get; }
            internal bool HasInterruptedExecutor { get; }
            internal Task Completed => _completed.Task;
            internal CancellationToken Token => _cancel.Token;

            internal Lease(EngineRecoveryExecutionGate owner, long sequence, bool safety, Task interrupted, CancellationToken token)
            {
                _owner = owner; _sequence = sequence; _safety = safety;
                HasInterruptedExecutor = interrupted != null;
                InterruptedExecutor = interrupted ?? Task.CompletedTask;
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            }

            internal async Task<T> ExecuteWithQuiescentBoundaryAsync<T>(Func<CancellationToken, Task<T>> execute, CancellationToken token,
                Func<T, CancellationToken, Task> afterQuiescence = null)
            {
                // Issue OFF before joining an interrupted executor, even if its
                // cancellation callbacks or cleanup are slow/non-cooperative.
                var result = await execute(token).ConfigureAwait(false);
                if (HasInterruptedExecutor && !InterruptedExecutor.IsCompleted)
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (token.Register(() => cancelled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(InterruptedExecutor, cancelled.Task).ConfigureAwait(false) != InterruptedExecutor)
                            throw new OperationCanceledException("InterruptedExecutorStillOwnsHardware", token);
                    }
                }
                if (HasInterruptedExecutor)
                {
                    token.ThrowIfCancellationRequested();
                    // Always repeat OFF after old cleanup, even when it completed
                    // during the first OFF. Its finally may change outputs.
                    result = await execute(token).ConfigureAwait(false);
                }
                if (afterQuiescence != null)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrent) throw new InvalidOperationException("SafetyHandoffExecutorSuperseded");
                    await afterQuiescence(result, token).ConfigureAwait(false);
                }
                return result;
            }

            internal void Cancel() { try { _cancel.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }
            internal void RequestCancellation()
            {
                if (Interlocked.Exchange(ref _cancelRequested, 1) == 0) _ = Task.Run(Cancel);
            }
            internal bool IsCurrent { get { lock (_owner._gate) return _disposed == 0 && _sequence == _owner._highestSequence; } }

            // The callback must be memory-only. In particular, do not log or save
            // receipts here: a stuck disk must not delay admission of physical OFF.
            internal bool PublishIfCurrent(Action publish)
            {
                lock (_owner._gate)
                {
                    if (_disposed != 0 || _sequence != _owner._highestSequence) return false;
                    publish(); return true;
                }
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                    if (_safety && ReferenceEquals(_owner._safety, this)) _owner._safety = null;
                    if (!_safety && ReferenceEquals(_owner._normal, this)) _owner._normal = null;
                    _completed.TrySetResult(true);
                }
                _cancel.Dispose();
            }
        }
    }
}
