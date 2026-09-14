using System;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    public interface IIndependentProjectRuntimeOperations : IIndependentRecoveryOperations
    {
        // Must close the native Job owner, not merely cancel a managed task.
        void CancelStageWorkers();
    }

    // Shared by the production service and its fault-injection host. Each tick
    // starts or polls bounded operations; it never calls the controller in-process.
    public sealed class IndependentProjectRecoveryPump
    {
        private readonly IndependentProjectStateStore _store;
        private readonly IIndependentProjectRuntimeOperations _operations;
        private readonly string _executor;
        private readonly long _powerBudgetMs, _outputsBudgetMs;

        public IndependentProjectRecoveryPump(IndependentProjectStateStore store,
            IIndependentProjectRuntimeOperations operations, string executor, long powerBudgetMs, long outputsBudgetMs)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            if (string.IsNullOrWhiteSpace(executor) || powerBudgetMs <= 0 || outputsBudgetMs <= 0 ||
                powerBudgetMs > 300000 || outputsBudgetMs > 300000) throw new ArgumentException("IndependentPumpConfigurationInvalid");
            _executor = executor; _powerBudgetMs = powerBudgetMs; _outputsBudgetMs = outputsBudgetMs;
        }

        public void Tick(long now)
        {
            var state = _store.Read();
            var tx = state?.Transaction;
            if (tx == null) return;
            if (tx.ExecutorIdentity != _executor) throw new InvalidOperationException("IndependentPumpOwnerMismatch");
            if (now < tx.LastAttemptUtcTicks) throw new InvalidOperationException("IndependentPumpClockRegressed");
            if (tx.IsTerminal)
            {
                if (tx.Phase == IndependentRecoveryPhase.NeedsAttention && state.SafetyCleanupPending &&
                    tx.AttemptsUtcTicks.Length < 3 && now - tx.LastAttemptUtcTicks >= TimeSpan.FromSeconds(60).Ticks)
                {
                    _operations.CancelStageWorkers();
                    _store.RetrySafetyCleanup(state.Revision, _executor, now);
                }
                // Exhausted cleanup remains visible for operator action. Merely
                // waiting thirty minutes must not clear this failed chain.
                return;
            }
            if (state.SafetyCleanupPending)
            {
                var expired = now >= tx.PhaseDeadlineUtcTicks;
                IndependentOperationResult result;
                if (expired)
                {
                    _operations.CancelStageWorkers();
                    result = IndependentOperationResult.Failed;
                }
                else
                {
                    result = tx.Phase == IndependentRecoveryPhase.CooperativeStop ? _operations.CooperativeStop(tx) :
                        tx.Phase == IndependentRecoveryPhase.PowerOff ? _operations.ConfirmPowerOff(tx) :
                        tx.Phase == IndependentRecoveryPhase.RetireControls ? _operations.RetireExactControls(tx) :
                        tx.Phase == IndependentRecoveryPhase.OutputsSafe ? _operations.ConfirmOutputsAndPressure(tx) :
                        throw new InvalidOperationException("IndependentPumpCleanupPhaseInvalid");
                }
                if (result == IndependentOperationResult.Pending) return;
                _operations.CancelStageWorkers();
                var nextBudget = tx.Phase == IndependentRecoveryPhase.CooperativeStop ? _powerBudgetMs :
                    tx.Phase == IndependentRecoveryPhase.PowerOff ? 10000 :
                    tx.Phase == IndependentRecoveryPhase.RetireControls ? _outputsBudgetMs : 30000;
                _store.CompleteSafetyStage(state.Revision, _executor, tx.Generation, tx.RequestId, tx.Phase,
                    result == IndependentOperationResult.Completed, now, nextBudget,
                    expired ? "ExternalWorkerDeadlineExceeded" : "WorkerCompleted:" + tx.Phase);
                return;
            }
            var authorized = !state.Maintenance && state.Intent != null && !state.Intent.ManualStopped &&
                !state.Intent.ManualPaused && state.Intent.Armed && tx.IntentRevision == state.Intent.Revision &&
                tx.RunId == state.Intent.RunId && tx.RunEpoch == state.Intent.RunEpoch;
            if (!authorized || now >= tx.PhaseDeadlineUtcTicks)
            {
                Finish(state, authorized ? IndependentRecoveryPhase.NeedsAttention : IndependentRecoveryPhase.Cancelled,
                    authorized ? "ReplacementVerificationOrLaunchDeadlineExceeded" : "RestartAuthorityRevoked");
                CancelReplacementWorkers(tx);
                return;
            }
            if (tx.Phase == IndependentRecoveryPhase.Verifying &&
                (state.Ticket?.Consumer == null || !state.Ticket.Consumer.Matches(state.Controller))) return;
            var outcome = tx.Phase == IndependentRecoveryPhase.LaunchPending ? _operations.LaunchOnce(tx) :
                tx.Phase == IndependentRecoveryPhase.Verifying ? _operations.VerifyActionsAndDatabase(tx) :
                throw new InvalidOperationException("IndependentPumpPostSafetyPhaseInvalid");
            if (outcome == IndependentOperationResult.Pending) return;
            if (outcome == IndependentOperationResult.Failed)
            {
                Finish(state, IndependentRecoveryPhase.NeedsAttention, "ReplacementFailed:" + tx.Phase);
                CancelReplacementWorkers(tx);
                return;
            }
            // A launcher observes a consumed durable ticket; it must not invent
            // an in-memory PID and use process existence as recovery evidence.
            _store.Update(state.Revision, latest =>
            {
                if (tx.Phase == IndependentRecoveryPhase.LaunchPending &&
                    (latest.Ticket?.Consumer == null || latest.Ticket.Revoked ||
                     latest.Ticket.Consumer.Pid != latest.Transaction.ReplacementPid ||
                     latest.Ticket.Consumer.StartUtcTicks != latest.Transaction.ReplacementStartUtcTicks))
                    throw new InvalidOperationException("IndependentLaunchNotDurablyConsumed");
                IndependentRecoveryTransitions.Advance(latest.Transaction, latest.Intent, _executor, tx.Generation,
                    tx.Phase == IndependentRecoveryPhase.LaunchPending ? IndependentRecoveryPhase.Verifying : IndependentRecoveryPhase.Verified,
                    now, tx.Phase == IndependentRecoveryPhase.LaunchPending ?
                        checked(latest.Intent.StartupBudgetMs + latest.Intent.PeriodMs * 3 + 60000) : 10000,
                    tx.Phase == IndependentRecoveryPhase.LaunchPending ? "ReplacementTicketConsumed" :
                        "ActionsCountersAndThreeDatabaseCommitsVerified:Channels=" + string.Join(",", tx.Channels.Except(latest.Intent.CompletedChannels)) +
                        ";TargetCompleted=" + string.Join(",", tx.Channels.Intersect(latest.Intent.CompletedChannels)));
                if (latest.Transaction.Phase == IndependentRecoveryPhase.Verified)
                {
                    // Close only incidents that this replacement actually
                    // verified. A later fault is a new incident; process launch
                    // alone must never erase a pending retry or an isolation.
                    latest.NearZeroRetries = latest.NearZeroRetries.Where(r =>
                        !tx.Channels.Contains(r.Channel) || r.FirstGeneration >= tx.Generation ||
                        r.FirstRunId == latest.Intent.RunId || latest.Intent.PermanentChannels.Contains(r.Channel)).ToArray();
                }
                return true;
            });
        }

        private void CancelReplacementWorkers(IndependentRecoveryTransaction transaction)
        {
            try { _operations.CancelPendingLaunch(transaction); }
            finally { _operations.CancelStageWorkers(); }
        }

        private void Finish(IndependentProjectState state, IndependentRecoveryPhase phase, string detail)
        {
            _store.Update(state.Revision, latest =>
            {
                latest.Transaction.Phase = phase;
                latest.Transaction.Revision = checked(latest.Transaction.Revision + 1);
                latest.Transaction.Detail = detail;
                if (latest.Ticket != null) latest.Ticket.Revoked = true;
                if (latest.Ticket?.Consumer != null)
                {
                    // Task Scheduler starts the main process outside the launch
                    // worker Job. Disposing that Job does not retire the main.
                    // Preserve its exact identity and the cleanup obligation in
                    // the same durable write that revokes restart authority.
                    // This also covers consumption before bootstrap commits a run.
                    latest.Controller = latest.Ticket.Consumer;
                    latest.SafetyCleanupPending = true;
                    latest.Transaction.Phase = IndependentRecoveryPhase.NeedsAttention;
                    latest.Transaction.Detail = "ReplacementSafetyCleanupRequired:" + detail;
                }
                return true;
            });
        }
    }
}
