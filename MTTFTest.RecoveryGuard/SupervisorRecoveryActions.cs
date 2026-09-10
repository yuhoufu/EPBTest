using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal static class StalledMainRetirementPolicy
    {
        internal static bool CanRetire(RecoveryControlState state, RecoveryGuardSettings settings, DateTime now)
        {
            if (settings == null || settings.Mode != RecoveryGuardMode.RecoverStalled || now.Kind != DateTimeKind.Utc)
                return false;
            var tx = state?.Transaction;
            var intent = state?.Intent;
            var observation = state?.Observation;
            var snapshot = observation?.LastSnapshot;
            if (intent?.DesiredState != RecoveryDesiredState.Run || intent.MainProcess?.IsValid() != true ||
                !Guid.TryParse(intent.WatchdogSessionId, out var watchdogSession) || watchdogSession == Guid.Empty ||
                tx?.Stage != RecoveryStage.SafeStop || tx.OwnershipReleased || tx.WorkerRetirementRequestedUtcTicks != 0 ||
                tx.AuthorizationId != intent.AuthorizationId || tx.IntentVersion != intent.IntentVersion ||
                tx.Owner?.IsValid() != true || tx.Owner.Matches(intent.MainProcess) ||
                !Guid.TryParseExact(tx.TransactionId, "N", out var transactionId) || transactionId == Guid.Empty ||
                !string.Equals(state.MainExecutablePath, intent.MainProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                tx.Epoch != state.LastTakeoverEpoch || tx.LeaseUntilUtcTicks <= now.Ticks ||
                tx.StageDeadlineUtcTicks <= now.Ticks || tx.StageStartedUtcTicks <= 0 ||
                observation?.Established != true || observation.Expired ||
                observation.SuspectCount < settings.ConfirmationCount ||
                snapshot?.SourceAvailable != true || snapshot.MainProcess?.Matches(intent.MainProcess) != true ||
                snapshot.RunId != intent.RunId || snapshot.ConfigurationIdentity != intent.ConfigurationIdentity ||
                snapshot.Authorization == null || snapshot.Authorization.InstallationId != state.InstallationId ||
                snapshot.Authorization.AuthorizationId != intent.AuthorizationId ||
                snapshot.Authorization.IntentVersion != intent.IntentVersion ||
                snapshot.Channels == null || snapshot.Channels.Any(channel => channel == null) || observation.ChannelClocks == null)
                return false;
            var grace = TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, settings.StageTimeoutSeconds / 2)));
            if (now.Ticks - tx.StageStartedUtcTicks < grace.Ticks) return false;
            var staleBefore = now.AddSeconds(-settings.BusinessStallSeconds).Ticks;
            var channels = snapshot.Channels.Where(channel => channel != null && channel.Eligible &&
                !channel.Completed && !channel.PermanentlyIsolated && !channel.ManuallyExcluded).ToArray();
            if (channels.Length == 0 || channels.Select(channel => channel.Channel).Distinct().Count() != channels.Length)
                return false;
            // A single stalled channel is not evidence that the entire Main
            // cannot cooperate. Require every participating business clock to
            // be stale and preserve legitimate bounded phase waits.
            foreach (var channel in channels)
            {
                if (channel.StageDeadlineUtcTicks > now.Ticks) return false;
                var clocks = observation.ChannelClocks.Where(clock => clock != null && clock.Channel == channel.Channel).ToArray();
                if (clocks.Length != 1 || clocks[0].SampleProgressUtcTicks <= 0 ||
                    clocks[0].ControlProgressUtcTicks <= 0 || clocks[0].PersistedProgressUtcTicks <= 0 ||
                    clocks[0].SampleProgressUtcTicks > staleBefore || clocks[0].ControlProgressUtcTicks > staleBefore ||
                    clocks[0].PersistedProgressUtcTicks > staleBefore) return false;
            }
            return true;
        }
    }

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
        private readonly RecoveryGuardSettings _settings;
        private readonly Action _demandRetirementAllowed;
        private readonly Func<RecoveryProcessIdentity, string, Action, bool> _retireMain;
        public int MaximumCallSeconds => 25;

        internal SupervisorRecoveryActions(RecoveryControlStore store, IRecoverySupervisorTransport transport,
            Func<DateTime> now, Func<RecoveryProcessIdentity, ProcessObservation> probe,
            RecoveryGuardSettings settings = null, Action demandRetirementAllowed = null,
            Func<RecoveryProcessIdentity, string, Action, bool> retireMain = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _now = now ?? throw new ArgumentNullException(nameof(now));
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _settings = settings;
            _demandRetirementAllowed = demandRetirementAllowed;
            _retireMain = retireMain ?? RecoveryWorkerRetirement.RetireExactMainProcess;
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
                var mainObservation = _probe(state.Intent.MainProcess);
                if (mainObservation == ProcessObservation.ExactAlive)
                {
                    if (_demandRetirementAllowed != null && StalledMainRetirementPolicy.CanRetire(state, _settings, _now()) &&
                        tx.SafetyAuthorityId == null && tx.LaunchOperationId == null)
                    {
                        _demandRetirementAllowed();
                        _store.RecordMainRetirementIntent(tx.TransactionId, tx.Epoch, tx.Owner,
                            sessionId, state.Intent.MainProcess, _now());
                        var exited = _retireMain(state.Intent.MainProcess, state.MainExecutablePath, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _demandRetirementAllowed();
                            var latest = _store.ReadOwnedStage(tx.TransactionId, tx.Epoch, tx.Owner,
                                RecoveryStage.SafeStop, _now(), sessionId);
                            if (!StalledMainRetirementPolicy.CanRetire(latest, _settings, _now()) ||
                                latest.Intent.MainProcess?.Matches(state.Intent.MainProcess) != true ||
                                latest.Transaction.SafetyAuthorityId != null || latest.Transaction.LaunchOperationId != null)
                                throw new InvalidOperationException("RecoveryMainRetirementAuthorityChanged");
                            // Store reads and commissioning checks can take time;
                            // cancellation during those checks still vetoes termination.
                            cancellationToken.ThrowIfCancellationRequested();
                        });
                        return Result(RecoveryActionOutcome.Pending, exited
                            ? "StalledMainRetired;IndependentSafetyRequired" : "StalledMainRetirementExitPending");
                    }
                    return Result(RecoveryActionOutcome.Pending, "ActiveMainSafeStopPending");
                }
                if (mainObservation != ProcessObservation.Exited)
                    return Result(RecoveryActionOutcome.Blocked, "ActiveMainIdentityUnproven");
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
