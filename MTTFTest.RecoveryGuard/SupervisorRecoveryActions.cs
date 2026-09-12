using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal interface IRecoverySupervisorTransport
    {
        Task<RecoveryGuardSafetyPrepareResponse> PrepareSafetyAsync(RecoveryGuardSafetyPrepareRequest request, CancellationToken token);
        Task<RecoveryGuardSafetyExecuteResponse> ExecuteSafetyAsync(RecoveryGuardSafetyExecuteRequest request, CancellationToken token);
        Task<RecoveryGuardLaunchPreparationResponse> PrepareLaunchAsync(RecoveryGuardLaunchPreparationRequest request, CancellationToken token);
        Task<RecoveryGuardLaunchResponse> LaunchAsync(RecoveryGuardLaunchRequest request, CancellationToken token);
    }

    internal sealed class RecoverySupervisorTransport : IRecoverySupervisorTransport
    {
        public Task<RecoveryGuardSafetyPrepareResponse> PrepareSafetyAsync(RecoveryGuardSafetyPrepareRequest request, CancellationToken token) =>
            RecoveryGuardSupervisorProtocol.PrepareSafetyAsync(request, token);
        public Task<RecoveryGuardSafetyExecuteResponse> ExecuteSafetyAsync(RecoveryGuardSafetyExecuteRequest request, CancellationToken token) =>
            RecoveryGuardSupervisorProtocol.ExecuteSafetyAsync(request, token);
        public Task<RecoveryGuardLaunchPreparationResponse> PrepareLaunchAsync(RecoveryGuardLaunchPreparationRequest request, CancellationToken token) =>
            RecoveryGuardSupervisorProtocol.PrepareLaunchAsync(request, token);
        public Task<RecoveryGuardLaunchResponse> LaunchAsync(RecoveryGuardLaunchRequest request, CancellationToken token) =>
            RecoveryGuardSupervisorProtocol.LaunchAsync(request, token);
    }

    internal sealed class SupervisorRecoveryActions : IRecoveryExecutionActions
    {
        private readonly RecoveryControlStore _store;
        private readonly IRecoverySupervisorTransport _transport;
        private readonly Func<DateTime> _now;
        private readonly Func<RecoveryProcessIdentity, ProcessObservation> _probe;
        public int MaximumCallSeconds => 25;

        internal SupervisorRecoveryActions(RecoveryControlStore store, IRecoverySupervisorTransport transport,
            Func<DateTime> now, Func<RecoveryProcessIdentity, ProcessObservation> probe)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public async Task<RecoveryActionResult> ExecuteAsync(RecoveryControlState context, CancellationToken cancellationToken)
        {
            try { return await ExecuteBoundAsync(context, cancellationToken).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (ex.Message == "RecoveryActionSessionSuperseded")
            {
                return Result(RecoveryActionOutcome.Pending, "SessionChanged;ReconcileCurrentSession");
            }
            catch (InvalidOperationException ex) when (ex.Message == "RecoveryOwnerFenced")
            {
                var current = _store.Read().Transaction;
                if (current?.TransactionId == context.Transaction.TransactionId &&
                    current.Epoch != context.Transaction.Epoch && current.Owner?.Matches(context.Transaction.Owner) == true)
                    return Result(RecoveryActionOutcome.Pending, "RecoveryAttemptChanged;ReconcileCurrentEpoch");
                throw;
            }
        }

        private async Task<RecoveryActionResult> ExecuteBoundAsync(RecoveryControlState context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tx = context.Transaction;
            var sessionId = context.Intent.WatchdogSessionId;
            var state = _store.ReadOwnedStage(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, _now(), sessionId);
            tx = state.Transaction;
            if (tx.ActionEpoch != 0 && tx.ActionEpoch != tx.Epoch)
            {
                if (tx.Stage == RecoveryStage.SafeStop && tx.LaunchOperationId == null && tx.SafetyAuthorityId != null)
                {
                    var reconciled = await _transport.PrepareSafetyAsync(new RecoveryGuardSafetyPrepareRequest
                    { RequestId = Guid.NewGuid().ToString("N"), TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner },
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var latest = _store.ReadOwnedStage(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, _now(), sessionId);
                    if (latest.Transaction.ActionEpoch == 0 && latest.Transaction.SafetyAuthorityId == null)
                        return Result(RecoveryActionOutcome.Pending, "AdoptedSafetyRetired;PrepareNewSafety");
                    return Result(RecoveryActionOutcome.Blocked, "AdoptedSafetyReconciliation:" + reconciled.Detail);
                }
                if (tx.LaunchOperationId == null || (tx.Stage != RecoveryStage.SafeStop && tx.Stage != RecoveryStage.Launch))
                    return Result(RecoveryActionOutcome.Blocked, "RecoveryActionRequiresReconciliation");
            }
            var requestId = Guid.NewGuid().ToString("N");
            if (tx.LaunchOperationId != null && (tx.Stage == RecoveryStage.SafeStop || tx.Stage == RecoveryStage.Launch))
            {
                var previous = state.Launches.SingleOrDefault(l => l.OperationId == tx.LaunchOperationId);
                if (previous?.State == "Reserved")
                    _store.ConfirmReservationExpiredBeforeConsumption(previous.OperationId, _now());
                if (previous?.State == "Started" && _probe(previous.Process) == ProcessObservation.Exited)
                    _store.ConfirmLaunchedProcessExited(previous.OperationId);
                previous = _store.Read().Launches.SingleOrDefault(l => l.OperationId == tx.LaunchOperationId);
                if (previous?.State == "Exited" || (previous?.State == "StartFailed" &&
                    previous.FailureEvidence == "ReservationExpiredBeforeConsumption"))
                {
                    if (!state.MatchesBoundTerminalLaunch(previous))
                        return Result(RecoveryActionOutcome.Blocked, "TerminalAttemptBindingUnproven");
                    var reconciled = await _transport.PrepareSafetyAsync(new RecoveryGuardSafetyPrepareRequest
                    { RequestId = requestId, TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner }, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = _store.Read().Transaction;
                    if (current?.TransactionId != tx.TransactionId || current.Epoch != tx.Epoch)
                        return Result(RecoveryActionOutcome.Pending, "TerminalAttemptArchived;ReadCurrentEpoch");
                    return Result(RecoveryActionOutcome.Blocked, "TerminalAttemptReconciliation:" + reconciled.Detail);
                }
            }
            if (tx.ActionEpoch != 0 && tx.ActionEpoch != tx.Epoch)
                return Result(RecoveryActionOutcome.Blocked, "RecoveryActionRequiresReconciliation");
            if (tx.Stage == RecoveryStage.SafeStop)
            {
                if (_probe(state.Intent.MainProcess) != ProcessObservation.Exited)
                    return Result(RecoveryActionOutcome.Blocked, "ActiveMainIndependentSafetyProofRequired");
                if (tx.SafetyAuthorityId == null)
                {
                    var prepared = await _transport.PrepareSafetyAsync(new RecoveryGuardSafetyPrepareRequest
                    { RequestId = requestId, TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner }, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!prepared.Accepted) return Result(RecoveryActionOutcome.Blocked, "SafetyPreparationDenied:" + prepared.Detail);
                    _store.BindRecoveryAction(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, prepared.SafetyAuthorityId, null, _now(), sessionId);
                    return Result(RecoveryActionOutcome.Pending, "SafetyAuthorityBound");
                }
                var executed = await _transport.ExecuteSafetyAsync(new RecoveryGuardSafetyExecuteRequest
                { RequestId = requestId, TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner,
                    SafetyAuthorityId = tx.SafetyAuthorityId }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _store.ReadOwnedStage(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, _now(), sessionId);
                if (!executed.Accepted || executed.State == RecoveryGuardSafetyExecutionState.Blocked)
                    return Result(RecoveryActionOutcome.Blocked, "SafetyExecutionBlocked:" + executed.Detail);
                if (executed.State != RecoveryGuardSafetyExecutionState.Completed)
                    return Result(RecoveryActionOutcome.Pending, "SafetyExecutionPending:" + executed.Detail);
                if (executed.Worker?.IsValid() != true || _probe(executed.Worker) != ProcessObservation.Exited)
                    return Result(RecoveryActionOutcome.Blocked, "SafetyWorkerExitUnproven");
                return Result(RecoveryActionOutcome.Completed, "SupervisorSafetyCompletedAndWorkerExited");
            }
            if (tx.Stage == RecoveryStage.Retire)
                return Result(tx.SafetyAuthorityId != null && _probe(state.Intent.MainProcess) == ProcessObservation.Exited
                    ? RecoveryActionOutcome.Completed : RecoveryActionOutcome.Blocked, "OldMainExitObservation");
            if (tx.Stage != RecoveryStage.Launch) throw new InvalidOperationException("RecoveryActionStageUnsupported");
            if (tx.SafetyAuthorityId == null) return Result(RecoveryActionOutcome.Blocked, "SafetyAuthorityMissing");
            if (tx.LaunchOperationId == null)
            {
                var prepared = await _transport.PrepareLaunchAsync(new RecoveryGuardLaunchPreparationRequest
                { RequestId = requestId, TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner,
                    SafetyAuthorityId = tx.SafetyAuthorityId }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!prepared.Accepted) return Result(RecoveryActionOutcome.Blocked, "LaunchPreparationDenied:" + prepared.Detail);
                _store.BindRecoveryAction(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, tx.SafetyAuthorityId, prepared.OperationId, _now(), sessionId);
                return Result(RecoveryActionOutcome.Pending, "LaunchOperationBound");
            }
            // A response can be lost after creation or after the main binds its
            // recovered RunId. Reconcile shared evidence before asking to launch.
            var recorded = state.Launches.SingleOrDefault(l => l.OperationId == tx.LaunchOperationId);
            if (recorded?.State == "Started")
            {
                if (!state.Matches(recorded.Authorization) || recorded.Process?.IsValid() != true ||
                    _probe(recorded.Process) != ProcessObservation.ExactAlive)
                    return Result(RecoveryActionOutcome.Blocked, "RecordedLaunchIdentityUnproven");
                return new RecoveryActionResult { Outcome = RecoveryActionOutcome.Completed, Evidence = "RecordedLaunchReconciled",
                    OperationId = tx.LaunchOperationId, Process = recorded.Process };
            }
            if (recorded != null && recorded.State != "Reserved" && recorded.State != "Consumed")
                return Result(RecoveryActionOutcome.Blocked, "PriorLaunchRequiresReconciliation");
            var launched = await _transport.LaunchAsync(new RecoveryGuardLaunchRequest
            { RequestId = requestId, TransactionId = tx.TransactionId, Epoch = tx.Epoch, Owner = tx.Owner,
                SafetyAuthorityId = tx.SafetyAuthorityId, OperationId = tx.LaunchOperationId }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _store.ReadOwnedStage(tx.TransactionId, tx.Epoch, tx.Owner, tx.Stage, _now(), sessionId);
            return new RecoveryActionResult { Outcome = launched.Accepted ? RecoveryActionOutcome.Completed : RecoveryActionOutcome.Blocked,
                Evidence = "SupervisorLaunch:" + launched.Detail, OperationId = tx.LaunchOperationId, Process = launched.Process };
        }

        private static RecoveryActionResult Result(RecoveryActionOutcome outcome, string evidence) =>
            new RecoveryActionResult { Outcome = outcome, Evidence = evidence };
    }
}
