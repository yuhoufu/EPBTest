using System;
using System.Threading.Tasks;
using Controller;
using MTTFTest.RecoveryControl;

namespace MTEmbTest
{
    /// <summary>Cooperative, old-Main-only retirement. This never authorizes a
    /// launch: the Supervisor must still perform its independent safety handoff.</summary>
    internal sealed class RecoveryGuardLiveStop
    {
        private readonly RecoveryAuthorizationToken _oldLease;
        private readonly RecoveryProcessIdentity _main;
        internal readonly RecoveryProcessIdentity Owner;
        internal readonly string TransactionId;
        internal readonly long Epoch;
        internal readonly string SessionId;
        private readonly string _runId;
        private readonly string _rootRunId;
        private readonly string _configurationIdentity;
        private readonly long _stageStartedUtcTicks;
        internal string Key => TransactionId + ":" + Epoch + ":" + _stageStartedUtcTicks;
        internal string RejectionReason { get; private set; }

        private RecoveryGuardLiveStop(RecoveryControlState state, RecoveryAuthorizationToken oldLease,
            RecoveryProcessIdentity main, string runId)
        {
            _oldLease = new RecoveryAuthorizationToken
            {
                InstallationId = oldLease.InstallationId, AuthorizationId = oldLease.AuthorizationId,
                IntentVersion = oldLease.IntentVersion, TakeoverEpoch = oldLease.TakeoverEpoch
            };
            _main = Copy(main);
            Owner = Copy(state.Transaction.Owner);
            TransactionId = state.Transaction.TransactionId;
            Epoch = state.Transaction.Epoch;
            SessionId = state.Intent.WatchdogSessionId;
            _runId = runId;
            _rootRunId = state.Intent.RootRunId;
            _configurationIdentity = state.Intent.ConfigurationIdentity;
            _stageStartedUtcTicks = state.Transaction.StageStartedUtcTicks;
        }

        internal static RecoveryGuardLiveStop TryCreate(RecoveryControlState state,
            RecoveryAuthorizationToken oldLease, RecoveryProcessIdentity main, string runId, DateTime now)
        {
            if (oldLease == null || main?.IsValid() != true || state?.Intent == null ||
                state.Transaction?.Owner?.IsValid() != true) return null;
            var request = new RecoveryGuardLiveStop(state, oldLease, main, runId);
            return request.Matches(state, now) ? request : null;
        }

        internal bool Matches(RecoveryControlState state, DateTime now)
        {
            var tx = state?.Transaction;
            var intent = state?.Intent;
            return state?.InstallationId == _oldLease.InstallationId &&
                IsId(_oldLease.InstallationId) && IsId(_oldLease.AuthorizationId) &&
                IsId(_runId) && IsId(_rootRunId) && IsId(SessionId) &&
                !string.IsNullOrWhiteSpace(_configurationIdentity) && !Owner.Matches(_main) &&
                intent?.DesiredState == RecoveryDesiredState.Run && intent.RunId == _runId &&
                intent.RootRunId == _rootRunId && intent.ConfigurationIdentity == _configurationIdentity &&
                string.Equals(state.MainExecutablePath, _main.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                intent.AuthorizationId == _oldLease.AuthorizationId && intent.IntentVersion == _oldLease.IntentVersion &&
                intent.MainProcess?.Matches(_main) == true && !string.IsNullOrWhiteSpace(SessionId) &&
                intent.WatchdogSessionId == SessionId && tx != null && !tx.OwnershipReleased &&
                tx.TransactionId == TransactionId && Guid.TryParseExact(TransactionId, "N", out _) &&
                tx.AuthorizationId == intent.AuthorizationId && tx.IntentVersion == intent.IntentVersion &&
                tx.Epoch == Epoch && Epoch == state.LastTakeoverEpoch && Epoch > _oldLease.TakeoverEpoch &&
                tx.Owner?.Matches(Owner) == true && tx.LeaseUntilUtcTicks > now.Ticks &&
                tx.Stage == RecoveryStage.SafeStop && tx.StageStartedUtcTicks == _stageStartedUtcTicks &&
                _stageStartedUtcTicks > 0 && _stageStartedUtcTicks <= now.Ticks &&
                tx.StageDeadlineUtcTicks > now.Ticks && tx.WorkerRetirementRequestedUtcTicks == 0;
        }

        internal async Task<bool> RunAsync(Func<RecoveryControlState> readOwnedState,
            Func<StopContext, Task<StopSafetyResult>> stop, Action retire,
            Func<RecoveryProcessIdentity, bool> isAlive, Func<DateTime> now)
        {
            var requestedUtc = now();
            RejectionReason = null;
            if (!Matches(readOwnedState(), requestedUtc)) return Reject("AuthorityInvalidBeforeStop");
            if (!isAlive(_main) || !isAlive(Owner)) return Reject("ProcessIdentityUnprovenBeforeStop");
            var result = await stop(new StopContext
            {
                Source = StopSource.SystemFault, RunId = _runId,
                CorrelationId = TransactionId, RequestedUtc = requestedUtc,
                Initiator = "RecoveryGuardAutomaticSafeStop", Reason = "RecoveryGuardTakeover:" + Key
            }).ConfigureAwait(false);
            var completedUtc = now();
            // Do not wait for obsolete logical owners once physical safety and
            // accepted data are secured. Conversely, a generic timeout or an old
            // cached Stop result is never evidence permitting deliberate exit.
            if (result == null) return Reject("StopResultMissing");
            if (result.ReusedPreviousResult) return Reject("PreviousStopResultRequiresFreshEvidence");
            if (result.Source != StopSource.SystemFault ||
                result.RunId.ToString("N") != _runId || result.CorrelationId != TransactionId ||
                result.SafetyTransactionId == Guid.Empty || result.SafetyBoundaryGeneration <= 0)
                return Reject("StopTransactionMismatch");
            if (result.StartedUtc < requestedUtc || result.CompletedUtc < result.StartedUtc ||
                result.CompletedUtc > completedUtc) return Reject("StopEvidenceTimeInvalid");
            if (!result.PhysicalSafetyConfirmed) return Reject("CurrentPressureOrOffCommandUnconfirmed");
            if (!result.PersistenceBoundaryConfirmed || !result.RawStorageFlushed || result.DataContinuityCompromised)
                return Reject("AcceptedDataBoundaryUnconfirmed");
            if (!Matches(readOwnedState(), now())) return Reject("AuthorityChangedAfterStop");
            if (!isAlive(_main) || !isAlive(Owner)) return Reject("ProcessIdentityUnprovenAfterStop");
            retire();
            return true;
        }

        private bool Reject(string reason)
        {
            RejectionReason = reason;
            return false;
        }

        private static RecoveryProcessIdentity Copy(RecoveryProcessIdentity value) => new RecoveryProcessIdentity
        {
            ProcessId = value.ProcessId, StartUtcTicks = value.StartUtcTicks,
            ExecutablePath = value.ExecutablePath, BootId = value.BootId
        };

        private static bool IsId(string value) => Guid.TryParseExact(value, "N", out var parsed) && parsed != Guid.Empty;
    }
}
