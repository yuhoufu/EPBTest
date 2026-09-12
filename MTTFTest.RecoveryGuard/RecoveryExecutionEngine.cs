using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal enum RecoveryActionOutcome { Pending, Completed, Blocked, RetryableFailure }

    internal sealed class RecoveryActionResult
    {
        internal RecoveryActionOutcome Outcome;
        internal string Evidence;
        internal string OperationId;
        internal RecoveryProcessIdentity Process;
    }

    internal interface IRecoveryExecutionActions
    {
        // Includes transport timeout and cleanup. Implementations must honor
        // cancellation, and reconcile durable action identity before retrying.
        int MaximumCallSeconds { get; }
        Task<RecoveryActionResult> ExecuteAsync(RecoveryControlState context, CancellationToken cancellationToken);
    }

    internal sealed class RecoveryExecutionStep
    {
        internal string Code;
        internal bool StopWorker;
        internal bool ActionDispatched;
        internal int NextDelaySeconds;
    }

    // Runs in the persistent execution process, not inside the minute task.
    // Production transport is supplied explicitly: there is no simulated
    // hardware implementation or success fallback in this executable.
    internal sealed class RecoveryExecutionEngine
    {
        private readonly RecoveryControlStore _store;
        private readonly RecoveryGuardSettings _settings;
        private readonly RecoveryProcessIdentity _owner;
        private readonly IRecoveryExecutionActions _actions;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<RecoveryProcessIdentity, ProcessObservation> _probe;
        private readonly Func<bool> _maintenance;
        private readonly RecoveryCommissioningScope _commissioning;

        internal RecoveryExecutionEngine(RecoveryControlStore store, RecoveryGuardSettings settings,
            RecoveryProcessIdentity owner, IRecoveryExecutionActions actions, Func<DateTime> utcNow,
            Func<RecoveryProcessIdentity, ProcessObservation> probe, Func<bool> maintenance,
            RecoveryCommissioningScope commissioning = null)
        {
            settings.Validate();
            if (owner?.IsValid() != true || actions == null || actions.MaximumCallSeconds < 1 ||
                actions.MaximumCallSeconds * 2L >= settings.OwnerLeaseSeconds ||
                actions.MaximumCallSeconds > settings.StageTimeoutSeconds)
                throw new ArgumentException("RecoveryExecutionActionBudgetInvalid");
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings;
            _owner = owner;
            _actions = actions;
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
            _commissioning = commissioning;
        }

        internal async Task RunAsync(Action<RecoveryExecutionStep> report, CancellationToken cancellationToken)
        {
            while (true)
            {
                var step = await StepAsync(cancellationToken).ConfigureAwait(false);
                report?.Invoke(step);
                if (step.StopWorker) return;
                await Task.Delay(TimeSpan.FromSeconds(step.NextDelaySeconds), cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<RecoveryExecutionStep> StepAsync(CancellationToken cancellationToken)
        {
            try { return await StepBoundAsync(cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (ex.Message == "RecoveryActionSessionSuperseded")
            {
                return Result("SessionChanged;ReconcileCurrentSession");
            }
        }

        private async Task<RecoveryExecutionStep> StepBoundAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = _store.Read();
            var now = _utcNow();
            _commissioning?.Demand(before, now);
            var maintenance = _maintenance();
            var process = before.Intent == null ? ProcessObservation.Unknown : _probe(before.Intent.MainProcess);
            var snapshot = _store.ReadSnapshot();
            var decision = _store.Observe(snapshot, process, _owner.BootId, now, _settings, maintenance);
            var state = _store.Read();
            _commissioning?.Demand(state, _utcNow());
            // A released historical transaction must not prevent a later
            // failure of this run (or a newly authorized run) from being claimed.
            var transaction = state.Transaction?.OwnershipReleased == true ? null : state.Transaction;
            if (_settings.Mode == RecoveryGuardMode.ObserveOnly)
                return Result(decision.Code, true);
            if (state.Intent?.DesiredState != RecoveryDesiredState.Run || state.Observation?.Expired == true)
                return Result(decision.Code, true);
            if (transaction?.Stage == RecoveryStage.Complete)
            {
                if (!transaction.OwnershipReleased && transaction.Owner?.Matches(_owner) == true)
                    _store.ReleaseCompletedTakeover(transaction.TransactionId, transaction.Epoch, _owner);
                else if (!maintenance && transaction.LeaseUntilUtcTicks <= now.Ticks &&
                    _probe(transaction.Owner) == ProcessObservation.Exited &&
                    _probe(state.Intent.MainProcess) == ProcessObservation.ExactAlive)
                {
                    _store.ReconcileCompletedTakeover(transaction.TransactionId, transaction.Epoch,
                        transaction.Owner, ProcessObservation.Exited, state.Intent.MainProcess,
                        ProcessObservation.ExactAlive, now);
                    return Result("CompletedOwnershipReconciled", true);
                }
                return Result(transaction.OwnershipReleased || transaction.Owner?.Matches(_owner) == true
                    ? "RecoveryCompleted" : "CompletedOwnershipNeedsReconciliation", true);
            }
            if (transaction?.Stage == RecoveryStage.Cancelled)
                return Result("CancelledOwnershipNeedsReconciliation", true);
            if (transaction?.Active == true)
            {
                if (transaction.Owner?.Matches(_owner) != true)
                {
                    if (maintenance || transaction.LeaseUntilUtcTicks > now.Ticks ||
                        _probe(transaction.Owner) != ProcessObservation.Exited)
                        return Result("RecoveryOwnedByAnotherProcess", true);
                    if (RecoveryDecisionPolicy.HasFreshVerificationEvidence(state, now) &&
                        (transaction.OrphanVerificationUntilUtcTicks == 0 || now.Ticks < transaction.OrphanVerificationUntilUtcTicks) &&
                        _probe(state.Intent.MainProcess) == ProcessObservation.ExactAlive)
                    {
                        _store.ReconcileVerifiedTakeover(transaction.TransactionId, transaction.Epoch,
                            transaction.Owner, ProcessObservation.Exited, state.Intent.MainProcess,
                            ProcessObservation.ExactAlive, now);
                        return Result("VerifiedOwnershipReconciled", true);
                    }
                    if (transaction.Stage == RecoveryStage.Verify &&
                        _probe(state.Intent.MainProcess) == ProcessObservation.ExactAlive &&
                        _store.TryObserveOrphanVerification(transaction.TransactionId, transaction.Epoch, transaction.Owner,
                            ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, now))
                        return Result("WaitingForOrphanVerificationEvidence");
                    var adopted = _store.Adopt(transaction.TransactionId, transaction.Epoch, _owner,
                        ProcessObservation.Exited, now, _settings);
                    // Adoption fences earlier launch tokens. Do not count their
                    // previous Verify evidence as success for the new epoch.
                    _store.Advance(adopted.TransactionId, adopted.Epoch, _owner, adopted.Stage,
                        RecoveryStage.Cooldown, "OwnerReplaced;ReconcileBeforeRetry", now, _settings);
                    return Result("OwnerAdoptedForReconciliation");
                }
                if (transaction.WorkerRetirementRequestedUtcTicks > 0)
                    return Result("WorkerRetirementRequested", true);
                if (transaction.LeaseUntilUtcTicks <= now.Ticks)
                    return Result("WorkerLeaseExpired", true);
                // Cooldown scans must retain ownership and continuous
                // supervision without extending any stage deadline.
                _store.RenewOwner(transaction.TransactionId, transaction.Epoch, _owner, now, _settings);
            }
            if (maintenance || (!decision.CanClaim && !decision.CanContinue))
                return Result(decision.Code, transaction?.Active != true);
            if (decision.CanClaim)
            {
                _store.Claim(snapshot, process, _owner, now, _settings, _maintenance());
                return Result("ClaimCommitted");
            }
            state = _store.Read();
            transaction = state.Transaction;
            if (transaction.Stage == RecoveryStage.Cooldown || transaction.Stage == RecoveryStage.Blocked)
            {
                if (transaction.Stage == RecoveryStage.Cooldown && transaction.RetryActionEpoch == transaction.Epoch &&
                    transaction.RetryActionStage.HasValue)
                {
                    _store.ResumeRecoveryAction(transaction.TransactionId, transaction.Epoch, _owner, now, _settings);
                    return Result("RecoveryActionResumedForReconciliation");
                }
                Advance(transaction, transaction.Stage == RecoveryStage.Blocked ? RecoveryStage.Cooldown : RecoveryStage.Claim,
                    transaction.Stage == RecoveryStage.Blocked ? "BlockedRecheckCooldown" : "CooldownElapsed");
                return Result("RecoveryRetryScheduled");
            }
            if (now.Ticks >= transaction.StageDeadlineUtcTicks)
            {
                Advance(transaction, RecoveryStage.Cooldown, "StageDeadlineExceeded;ReconcileBeforeRetry");
                return Result("StageTimedOut");
            }
            if (transaction.Stage == RecoveryStage.Claim)
            {
                Advance(transaction, RecoveryStage.SafeStop, "TakeoverIdentityCommitted");
                return Result("SafeStopRequested");
            }
            if (transaction.Stage == RecoveryStage.Verify)
            {
                if (!RecoveryDecisionPolicy.HasFreshVerificationEvidence(state, now))
                    return Result("WaitingForBusinessCommit");
                Advance(transaction, RecoveryStage.Complete, "VerifiedParticipatingChannelCommits");
                return Result("RecoveryCompleted", true);
            }
            if (transaction.Stage != RecoveryStage.SafeStop && transaction.Stage != RecoveryStage.Retire && transaction.Stage != RecoveryStage.Launch)
                throw new InvalidOperationException("RecoveryExecutionStageUnsupported");
            if (_settings.Mode == RecoveryGuardMode.RecoverExited && transaction.Stage != RecoveryStage.Launch &&
                _probe(state.Intent.MainProcess) != ProcessObservation.Exited)
                return Result("RecoverExitedWaitingForMainExit");

            RecoveryActionResult action;
            // Recheck immediately before transport; a new trial must not inherit
            // the one-time commissioning session while the worker is running.
            _commissioning?.Demand(_store.Read(), _utcNow());
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var remaining = TimeSpan.FromTicks(transaction.StageDeadlineUtcTicks - now.Ticks);
                bounded.CancelAfter(remaining < TimeSpan.FromSeconds(_actions.MaximumCallSeconds)
                    ? remaining : TimeSpan.FromSeconds(_actions.MaximumCallSeconds));
                try
                {
                    action = await _actions.ExecuteAsync(state, bounded.Token).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bounded.IsCancellationRequested)
                        action = new RecoveryActionResult { Outcome = RecoveryActionOutcome.RetryableFailure, Evidence = "ActionDeadlineExceeded;ReconcileBeforeRetry" };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    action = new RecoveryActionResult { Outcome = RecoveryActionOutcome.RetryableFailure, Evidence = "ActionDeadlineExceeded;ReconcileBeforeRetry" };
                }
                catch (IOException ex)
                {
                    action = new RecoveryActionResult { Outcome = RecoveryActionOutcome.RetryableFailure, Evidence = "ActionTransportUncertain:" + ex.Message };
                }
            }
            if (action == null || !Enum.IsDefined(typeof(RecoveryActionOutcome), action.Outcome) || string.IsNullOrWhiteSpace(action.Evidence))
                throw new InvalidDataException("RecoveryActionEvidenceMissing");
            _commissioning?.Demand(_store.Read(), _utcNow());
            var currentAttempt = _store.Read().Transaction;
            if (currentAttempt?.TransactionId != transaction.TransactionId || currentAttempt.Epoch != transaction.Epoch)
                return Result("RecoveryAttemptChanged;ReconcileCurrentEpoch", false, true);
            if (_maintenance()) return Result("MaintenanceAfterAction;ReconcileBeforeRetry", false, true);
            if (action.Outcome == RecoveryActionOutcome.Pending) return Result(action.Evidence, false, true);
            if (action.Outcome == RecoveryActionOutcome.Blocked || action.Outcome == RecoveryActionOutcome.RetryableFailure)
            {
                Advance(transaction, action.Outcome == RecoveryActionOutcome.Blocked ? RecoveryStage.Blocked : RecoveryStage.Cooldown,
                    action.Evidence, state.Intent.WatchdogSessionId);
                return Result(action.Evidence, false, true);
            }
            if (transaction.Stage == RecoveryStage.Retire && _probe(state.Intent.MainProcess) != ProcessObservation.Exited)
            {
                Advance(transaction, RecoveryStage.Blocked, "RetireExitIdentityUnproven", state.Intent.WatchdogSessionId);
                return Result("RetireExitIdentityUnproven", false, true);
            }
            if (transaction.Stage == RecoveryStage.Launch)
            {
                var current = _store.Read();
                var launch = current.Launches.SingleOrDefault(l => l.OperationId == action.OperationId);
                if (launch?.State != "Started" || !current.Matches(launch.Authorization) ||
                    action.Process?.Matches(launch.Process) != true || _probe(action.Process) != ProcessObservation.ExactAlive)
                {
                    Advance(transaction, RecoveryStage.Blocked, "LaunchCreationEvidenceUnproven", state.Intent.WatchdogSessionId);
                    return Result("LaunchCreationEvidenceUnproven", false, true);
                }
            }
            Advance(transaction, transaction.Stage == RecoveryStage.SafeStop ? RecoveryStage.Retire :
                transaction.Stage == RecoveryStage.Retire ? RecoveryStage.Launch : RecoveryStage.Verify, action.Evidence, state.Intent.WatchdogSessionId);
            return Result("ActionCompleted:" + transaction.Stage, false, true);
        }

        private void Advance(RecoveryTakeoverTransaction transaction, RecoveryStage next, string evidence, string expectedSessionId = null) =>
            _store.Advance(transaction.TransactionId, transaction.Epoch, _owner, transaction.Stage, next, evidence,
                _utcNow(), _settings, releaseCompletedOwnership: next == RecoveryStage.Complete, expectedSessionId: expectedSessionId);

        private RecoveryExecutionStep Result(string code, bool stop = false, bool dispatched = false) => new RecoveryExecutionStep
        {
            Code = code, StopWorker = stop, ActionDispatched = dispatched,
            NextDelaySeconds = code == "WaitingForBusinessCommit"
                ? Math.Min(_settings.ScanSeconds, Math.Max(1, _settings.StageTimeoutSeconds / 4))
                : code == "Cooldown" || code == "MaintenanceInhibited" ? _settings.ScanSeconds : 1
        };
    }
}
