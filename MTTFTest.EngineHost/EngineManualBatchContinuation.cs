using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // In-memory executor ticket subordinate to the Supervisor's durable Owner.
    // It cannot survive a hardware fault, Stop, recompose, or process replacement.
    internal sealed class EngineManualBatchContinuation
    {
        private readonly object _gate = new object();
        private long _invalidation;
        private RecoveryIdentity _pausedIdentity;
        private string _owner;
        private string _engineInstance;
        private bool _batchPaused;
        private int _channels;
        internal bool IsPaused { get { lock (_gate) return _batchPaused; } }
        internal int ChannelMask { get { lock (_gate) return _channels; } }

        internal void Invalidate()
        {
            lock (_gate) { _invalidation++; _pausedIdentity = null; _owner = null; _engineInstance = null; _batchPaused = false; _channels = 0; }
        }

        internal async Task PauseAsync(RecoveryCommand command, Func<CancellationToken, Task> pause,
            Func<CancellationToken, Task> persist, CancellationToken token)
        {
            var channel = command?.Kind == RecoveryCommandKind.PauseChannelGracefully;
            Validate(command, channel ? RecoveryCommandKind.PauseChannelGracefully : RecoveryCommandKind.PauseBatchGracefully);
            var bit = channel ? 1 << (command.OperatorTransaction.ManualBatch.Channel - 1) : 0;
            long version;
            lock (_gate)
            {
                if (_batchPaused || (_channels & bit) != 0) throw new InvalidOperationException("ManualBatchAlreadyPaused");
                if (_pausedIdentity != null && !Matches(command)) throw new InvalidOperationException("ManualPauseOwnerConflict");
                version = _invalidation;
            }
            try
            {
                await pause(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                EnsureCurrent(version);
                await persist(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_invalidation != version) throw new InvalidOperationException("ManualPauseInvalidatedByStopOrFault");
                    _pausedIdentity = command.Identity.Clone(); _owner = command.OwnerId;
                    _engineInstance = command.OperatorTransaction.ManualBatch.EngineInstanceId;
                    if (channel) _channels |= bit;
                    else { _batchPaused = true; _channels = 0; }
                }
            }
            catch { Invalidate(); throw; }
        }

        internal async Task ResumeAsync(RecoveryCommand command, Func<CancellationToken, Task> resume,
            Func<CancellationToken, Task> persist, CancellationToken token)
        {
            var channel = command?.Kind == RecoveryCommandKind.ResumePausedChannel;
            Validate(command, channel ? RecoveryCommandKind.ResumePausedChannel : RecoveryCommandKind.ResumePausedBatch);
            var bit = channel ? 1 << (command.OperatorTransaction.ManualBatch.Channel - 1) : 0;
            long version;
            lock (_gate)
            {
                if (!Matches(command) || (channel ? _batchPaused || (_channels & bit) == 0 : !_batchPaused))
                    throw new InvalidOperationException("ManualResumeTicketMissingOrRetired");
                version = _invalidation;
                if (channel) _channels &= ~bit;
                else { _channels = 0; _batchPaused = false; }
                if (_channels == 0) _pausedIdentity = null; // Failed continuation cannot reuse the consumed ticket.
            }
            try
            {
                token.ThrowIfCancellationRequested();
                await resume(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                EnsureCurrent(version);
                await persist(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_gate)
                    if (_invalidation != version) throw new InvalidOperationException("ManualResumeInvalidatedByStopOrFault");
            }
            catch { Invalidate(); throw; }
        }

        private bool Matches(RecoveryCommand command)
        {
            var payload = command.OperatorTransaction.ManualBatch;
            return _pausedIdentity != null && command.Identity.SessionId == _pausedIdentity.SessionId &&
                command.Identity.RunId == _pausedIdentity.RunId && command.Identity.RunEpoch == _pausedIdentity.RunEpoch &&
                command.Identity.Generation == _pausedIdentity.Generation && command.Identity.IncidentId == _pausedIdentity.IncidentId &&
                command.OwnerId == _owner && payload.PauseOwnerId == _owner && payload.PauseIncidentId == _pausedIdentity.IncidentId &&
                payload.EngineInstanceId == _engineInstance;
        }

        private static void Validate(RecoveryCommand command, RecoveryCommandKind kind)
        {
            if (command?.IsStructurallyValid() != true || command.Kind != kind)
                throw new InvalidOperationException("ManualBatchCommandInvalid");
        }

        private void EnsureCurrent(long version)
        {
            lock (_gate)
                if (_invalidation != version) throw new InvalidOperationException("ManualBatchInvalidatedByStopOrFault");
        }
    }
}
