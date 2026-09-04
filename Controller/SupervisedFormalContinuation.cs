using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace Controller
{
    // Process-local execution continuation, never a recovery Owner or a durable permit.
    // Qualification can only prepare it; the Supervisor's next command consumes it once.
    internal sealed class SupervisedFormalContinuation
    {
        private readonly object _gate = new object();
        private long _version;
        private RecoveryIdentity _identity;
        private string _owner;
        private string _target;
        private long _sequence;
        private long _qualificationDeadline;
        private bool _qualificationCompleted;
        private Func<Action, CancellationToken, Task<int[]>> _commit;

        internal long BeginQualification(RecoveryCommand command)
        {
            Validate(command, RecoveryCommandKind.RunQualificationCycle);
            lock (_gate)
            {
                if (_identity != null) throw new InvalidOperationException("QualificationContinuationAlreadyExists");
                _identity = command.Identity.Clone();
                _owner = command.OwnerId;
                _target = command.TargetResource;
                _sequence = command.CommandSequence;
                _qualificationDeadline = command.DeadlineUtcTicks;
                _qualificationCompleted = false;
                return ++_version;
            }
        }

        internal void CompleteQualification(long version, Func<Action, CancellationToken, Task<int[]>> commit)
        {
            if (commit == null) throw new ArgumentNullException(nameof(commit));
            lock (_gate)
            {
                EnsureCurrent(version);
                if (_qualificationCompleted) throw new InvalidOperationException("QualificationAlreadyPrepared");
                if (_qualificationDeadline <= DateTime.UtcNow.Ticks)
                    throw new InvalidOperationException("QualificationCompletionExpired");
                _qualificationCompleted = true;
                _commit = commit;
            }
        }

        internal async Task<int[]> CommitAsync(RecoveryCommand command, CancellationToken token)
        {
            Validate(command, RecoveryCommandKind.ResumeFormalRun);
            Func<Action, CancellationToken, Task<int[]>> commit;
            long version;
            lock (_gate)
            {
                if (_commit == null || !Matches(command))
                    throw new InvalidOperationException("QualifiedFormalContinuationMissingOrStale");
                version = _version;
                commit = _commit;
                _commit = null; // Consume before executing, including failures and cancellation.
            }
            Action ensureCurrent = () =>
            {
                token.ThrowIfCancellationRequested();
                Validate(command, RecoveryCommandKind.ResumeFormalRun);
                EnsureCurrent(version);
                lock (_gate)
                    if (!Matches(command)) throw new InvalidOperationException("QualifiedFormalCommandBindingChanged");
            };
            ensureCurrent();
            var channels = await commit(ensureCurrent, token).ConfigureAwait(false);
            ensureCurrent();
            return channels;
        }

        internal void Invalidate()
        {
            lock (_gate)
            {
                _version++;
                _identity = null;
                _commit = null;
                _owner = null;
                _target = null;
            }
        }

        private void EnsureCurrent(long version)
        {
            lock (_gate)
                if (_identity == null || _version != version)
                    throw new InvalidOperationException("QualificationInvalidatedByStopOrFault");
        }

        private bool Matches(RecoveryCommand command)
        {
            var identity = command.Identity;
            return _identity != null && _owner == command.OwnerId && _target == command.TargetResource &&
                identity.SessionId == _identity.SessionId && identity.RunId == _identity.RunId &&
                identity.RunEpoch == _identity.RunEpoch && identity.IncidentId == _identity.IncidentId &&
                identity.ResourceScope == _identity.ResourceScope && identity.Generation == _identity.Generation &&
                identity.Revision >= _identity.Revision && command.CommandSequence > _sequence;
        }

        private static void Validate(RecoveryCommand command, RecoveryCommandKind kind)
        {
            if (command?.IsStructurallyValid() != true || command.Kind != kind ||
                command.DeadlineUtcTicks <= DateTime.UtcNow.Ticks)
                throw new InvalidOperationException("SupervisedFormalCommandInvalidOrExpired");
        }
    }
}
