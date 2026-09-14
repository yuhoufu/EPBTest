using System;

namespace MTTFTest.Watchdog.Protocol
{
    public enum IndependentOperationResult { Pending, Completed, Failed }

    // Implementations must start/query an isolated bounded worker. None of these
    // methods may synchronously execute controller or native hardware operations.
    public interface IIndependentRecoveryOperations
    {
        IndependentOperationResult CooperativeStop(IndependentRecoveryTransaction transaction);
        IndependentOperationResult ConfirmPowerOff(IndependentRecoveryTransaction transaction);
        IndependentOperationResult RetireExactControls(IndependentRecoveryTransaction transaction);
        IndependentOperationResult ConfirmOutputsAndPressure(IndependentRecoveryTransaction transaction);
        IndependentOperationResult LaunchOnce(IndependentRecoveryTransaction transaction);
        IndependentOperationResult VerifyActionsAndDatabase(IndependentRecoveryTransaction transaction);
        void CancelPendingLaunch(IndependentRecoveryTransaction transaction);
    }

    // One short tick, one durable phase. Restarting the service resumes the
    // persisted phase instead of issuing a new launch or discarding the fence.
    public sealed class IndependentRecoveryExecutor
    {
        private readonly IIndependentRecoveryOperations _operations;
        private readonly Action<IndependentRecoveryTransaction> _save;
        private readonly long _powerBudgetMs, _outputsBudgetMs;

        public IndependentRecoveryExecutor(IIndependentRecoveryOperations operations,
            Action<IndependentRecoveryTransaction> save, long powerBudgetMs, long outputsBudgetMs)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _save = save ?? throw new ArgumentNullException(nameof(save));
            if (powerBudgetMs <= 0 || outputsBudgetMs <= 0 || powerBudgetMs > 300000 || outputsBudgetMs > 300000)
                throw new ArgumentOutOfRangeException(nameof(powerBudgetMs));
            _powerBudgetMs = powerBudgetMs; _outputsBudgetMs = outputsBudgetMs;
        }

        public void Tick(IndependentRecoveryTransaction transaction, IndependentRunIntent intent,
            string executorIdentity, long generation, long now)
        {
            if (transaction == null || transaction.IsTerminal) return;
            // A stale caller must not cancel work owned by a replacement executor.
            if (transaction.ExecutorIdentity != executorIdentity || transaction.Generation != generation)
                throw new InvalidOperationException("IndependentExecutorIdentityChanged");
            try { IndependentRecoveryTransitions.RequireCurrent(transaction, intent, executorIdentity, generation); }
            catch (InvalidOperationException)
            {
                _operations.CancelPendingLaunch(transaction);
                Finish(transaction, IndependentRecoveryPhase.Cancelled, "OperatorOrRunAuthorityChanged");
                return;
            }
            var phase = transaction.Phase;
            var expired = now >= transaction.PhaseDeadlineUtcTicks;
            if (expired && phase != IndependentRecoveryPhase.CooperativeStop)
            {
                _operations.CancelPendingLaunch(transaction);
                Finish(transaction, IndependentRecoveryPhase.NeedsAttention, "DeadlineExceeded:" + phase);
                return;
            }
            IndependentOperationResult result;
            try
            {
                result = phase == IndependentRecoveryPhase.CooperativeStop ?
                    (expired ? IndependentOperationResult.Completed : _operations.CooperativeStop(transaction)) :
                    phase == IndependentRecoveryPhase.PowerOff ? _operations.ConfirmPowerOff(transaction) :
                    phase == IndependentRecoveryPhase.RetireControls ? _operations.RetireExactControls(transaction) :
                    phase == IndependentRecoveryPhase.OutputsSafe ? _operations.ConfirmOutputsAndPressure(transaction) :
                    phase == IndependentRecoveryPhase.LaunchPending ? _operations.LaunchOnce(transaction) :
                    phase == IndependentRecoveryPhase.Verifying ? _operations.VerifyActionsAndDatabase(transaction) :
                    IndependentOperationResult.Failed;
            }
            catch (Exception ex)
            {
                // Keep phase/owner durable even when a worker connection fails.
                // A failed observation is not permission to rerun a launch.
                transaction.Detail = "WorkerObservationFailed:" + phase + ":" + ex.GetType().Name;
                _save(transaction);
                return;
            }
            if (result == IndependentOperationResult.Pending) return;
            if (result == IndependentOperationResult.Failed && phase != IndependentRecoveryPhase.CooperativeStop)
            {
                _operations.CancelPendingLaunch(transaction);
                Finish(transaction, IndependentRecoveryPhase.NeedsAttention, "StageFailed:" + phase);
                return;
            }
            var next = phase == IndependentRecoveryPhase.CooperativeStop ? IndependentRecoveryPhase.PowerOff :
                phase == IndependentRecoveryPhase.PowerOff ? IndependentRecoveryPhase.RetireControls :
                phase == IndependentRecoveryPhase.RetireControls ? IndependentRecoveryPhase.OutputsSafe :
                phase == IndependentRecoveryPhase.OutputsSafe ? IndependentRecoveryPhase.LaunchPending :
                phase == IndependentRecoveryPhase.LaunchPending ? IndependentRecoveryPhase.Verifying :
                IndependentRecoveryPhase.Verified;
            var budget = next == IndependentRecoveryPhase.PowerOff ? _powerBudgetMs :
                next == IndependentRecoveryPhase.OutputsSafe ? _outputsBudgetMs :
                next == IndependentRecoveryPhase.Verifying ? checked(intent.StartupBudgetMs + intent.PeriodMs * 3 + 60000) :
                next == IndependentRecoveryPhase.LaunchPending ? 30000 : 10000;
            IndependentRecoveryTransitions.Advance(transaction, intent, executorIdentity, generation, next,
                now, budget, expired ? "CooperativeDeadlineEscalated" : "Completed:" + phase);
            _save(transaction);
        }

        private void Finish(IndependentRecoveryTransaction tx, IndependentRecoveryPhase phase, string detail)
        {
            tx.Phase = phase; tx.Revision = checked(tx.Revision + 1); tx.Detail = detail;
            _save(tx);
        }
    }
}
