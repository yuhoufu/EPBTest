using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class SupervisorServiceRuntime
    {
        private DurableAuthorityStoreReadResult EnsureGuardExitedPermit(RecoveryGuardSafetyPrepareRequest request,
            RecoveryControlState state, string project, DurableAuthorityStoreReadResult loaded, bool closeOnly = false)
        {
            if (loaded.Record.State == DurableRelaunchPermitState.Approved) return loaded;
            if (loaded.Record.State == DurableRelaunchPermitState.Started || loaded.Record.State == DurableRelaunchPermitState.LaunchIntent)
            {
                var control = new RecoveryControlStore();
                state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    state.Transaction.Stage, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                var neverCreated = loaded.Record.State == DurableRelaunchPermitState.LaunchIntent;
                if (neverCreated) SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, state, loaded);
                else
                {
                    var process = SupervisorGuardPermitPolicy.FindExitedLaunch(request, state, loaded);
                    if (RecoveryProcessProbe.Observe(process, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.Exited)
                        throw new InvalidOperationException("RecoveryGuardFailedLaunchExitUnproven");
                }
                var authority = DurableRelaunchAuthorityFactory.TryOpenExisting(project, state.Intent.WatchdogSessionId);
                if (authority?.Succeeded != true)
                    throw new InvalidDataException("RecoveryGuardFailedLaunchAuthorityUnavailable");
                var closed = authority.Authority.CloseCurrentAsFailed((neverCreated ? "GuardConfirmedLaunchNeverConsumed:" :
                    "GuardConfirmedLaunchedProcessExited:") + loaded.Record.LaunchIntentId,
                    loaded.Revision, loaded.Sha256);
                if (closed?.Succeeded != true)
                    throw new InvalidOperationException("RecoveryGuardFailedLaunchCloseDeferred:" + closed?.Reason);
                state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    state.Transaction.Stage, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                loaded = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId).Load(state.Intent.WatchdogSessionId);
                if (loaded?.Exists != true || loaded.Blocked || loaded.Unproven ||
                    loaded.Record.State != DurableRelaunchPermitState.Failed || loaded.Sha256 != closed.Sha256)
                    throw new InvalidDataException("RecoveryGuardFailedLaunchReadbackChanged");
            }
            if (closeOnly) return loaded;
            var observation = RecoveryProcessProbe.Observe(state.Intent.MainProcess, RecoveryProcessProbe.ReadBootId());
            if (loaded.Record.CircuitOpen)
            {
                var control = new RecoveryControlStore();
                state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                SupervisorGuardPermitPolicy.ValidateHalfOpen(request, state, loaded, observation, DateTime.UtcNow);
                var store = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId);
                var reopened = store.TryOpenAutomaticHalfOpen(loaded.Record.LastFailureFingerprint,
                    loaded.Record.ConsecutiveFailures, loaded.Revision, loaded.Sha256);
                if (reopened?.Status != DurableAuthorityCommitStatus.CandidateApplied)
                    throw new InvalidOperationException("RecoveryGuardHalfOpenDeferred:" + reopened?.Reason);
                control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                var after = store.Load(state.Intent.WatchdogSessionId);
                if (after?.Exists != true || after.Blocked || after.Unproven ||
                    after.Revision != reopened.Revision || after.Sha256 != reopened.Sha256 ||
                    after.Record.State != DurableRelaunchPermitState.Approved)
                    throw new InvalidDataException("RecoveryGuardHalfOpenReadbackChanged");
                return after;
            }
            var operation = SupervisorGuardPermitPolicy.BuildFailure(request, state, loaded, observation);
            var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(project, state.Intent.WatchdogSessionId);
            if (opened?.Succeeded != true) throw new InvalidDataException("RecoveryGuardPermitAuthorityUnavailable:" + opened?.Reason);
            new RecoveryControlStore().ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner, RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
            var decision = opened.Authority.RegisterFailureAndDecide(operation, loaded.Revision, loaded.Sha256);
            if (decision?.ActionAllowed != true || !decision.Durable)
                throw new InvalidOperationException("RecoveryGuardPermitNotApproved:" + decision?.Reason);
            // Approval alone never grants device output. Recheck the shared
            // intent after the strict CAS; a concurrent stop still wins.
            new RecoveryControlStore().ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner, RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
            var result = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId).Load(state.Intent.WatchdogSessionId);
            if (result?.Exists != true || result.Blocked || result.Unproven ||
                result.Record.State != DurableRelaunchPermitState.Approved || result.Record.LastFailureOperationId != operation.OperationId)
                throw new InvalidDataException("RecoveryGuardPermitReadbackChanged");
            return result;
        }
    }

    internal static class SupervisorGuardPermitPolicy
    {
        internal static void ValidateUnpreparedLaunch(RecoveryGuardSafetyPrepareRequest request,
            RecoveryControlState state, DurableAuthorityStoreReadResult loaded)
        {
            request.Validate();
            var tx = state?.Transaction;
            var source = loaded?.Record;
            var launch = state?.Launches?.SingleOrDefault(l => l.OperationId == tx?.LaunchOperationId);
            if (tx == null || (tx.Stage != RecoveryStage.SafeStop && tx.Stage != RecoveryStage.Launch) ||
                tx.TransactionId != request.TransactionId || tx.Epoch != request.Epoch || tx.Owner?.Matches(request.Owner) != true ||
                loaded?.Exists != true || loaded.Blocked || loaded.Unproven || source == null ||
                source.State != DurableRelaunchPermitState.Approved || source.CircuitOpen || source.LaunchConsumed ||
                !string.IsNullOrEmpty(source.LaunchIntentId) || source.ProcessId != 0 || source.ProcessStartUtcTicks != 0 ||
                source.SessionId != state.Intent?.WatchdogSessionId || launch?.State != "StartFailed" ||
                !state.MatchesBoundTerminalLaunch(launch) ||
                state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                throw new InvalidDataException("RecoveryGuardUnpreparedLaunchRequiresReconciliation");
        }

        internal static void ValidateUnconsumedLaunch(RecoveryGuardSafetyPrepareRequest request,
            RecoveryControlState state, DurableAuthorityStoreReadResult loaded)
        {
            request.Validate();
            var source = loaded?.Record;
            var tx = state?.Transaction;
            if (state?.Intent?.DesiredState != RecoveryDesiredState.Run ||
                (tx?.Stage != RecoveryStage.SafeStop && tx?.Stage != RecoveryStage.Launch) ||
                tx.OwnershipReleased || tx.TransactionId != request.TransactionId || tx.Epoch != request.Epoch ||
                tx.Owner?.Matches(request.Owner) != true || loaded?.Exists != true || loaded.Blocked || loaded.Unproven ||
                source?.State != DurableRelaunchPermitState.LaunchIntent || source.CircuitOpen ||
                source.SessionId != state.Intent.WatchdogSessionId || string.IsNullOrEmpty(source.LaunchIntentId) ||
                source.ProcessId != 0 || source.ProcessStartUtcTicks != 0 ||
                !string.Equals(source.LaunchExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                state.Launches == null || state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                throw new InvalidDataException("RecoveryGuardUnconsumedLaunchRequiresReconciliation");
            var launch = state.Launches.SingleOrDefault(l => l.OperationId == source.LaunchIntentId);
            if (launch?.State != "StartFailed" || launch.FailureEvidence != "ReservationExpiredBeforeConsumption" ||
                launch.Process != null || !state.MatchesBoundTerminalLaunch(launch))
                throw new InvalidDataException("RecoveryGuardUnconsumedLaunchProofMissing");
        }

        internal static RecoveryProcessIdentity FindExitedLaunch(RecoveryGuardSafetyPrepareRequest request,
            RecoveryControlState state, DurableAuthorityStoreReadResult loaded)
        {
            request.Validate();
            var source = loaded?.Record;
            var tx = state?.Transaction;
            if (state?.Intent?.DesiredState != RecoveryDesiredState.Run ||
                (tx?.Stage != RecoveryStage.SafeStop && tx?.Stage != RecoveryStage.Launch) ||
                tx.OwnershipReleased || tx.TransactionId != request.TransactionId || tx.Epoch != request.Epoch ||
                tx.Owner?.Matches(request.Owner) != true || loaded?.Exists != true || loaded.Blocked || loaded.Unproven ||
                source?.State != DurableRelaunchPermitState.Started || source.CircuitOpen ||
                source.SessionId != state.Intent.WatchdogSessionId || string.IsNullOrEmpty(source.LaunchIntentId) ||
                state.Launches == null || state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                throw new InvalidDataException("RecoveryGuardFailedLaunchRequiresReconciliation");
            var launch = state.Launches.SingleOrDefault(l => l.OperationId == source.LaunchIntentId);
            if (launch?.State != "Exited" || !state.MatchesBoundTerminalLaunch(launch) || launch.Process?.IsValid() != true ||
                launch.Process.ProcessId != source.ProcessId || launch.Process.StartUtcTicks != source.ProcessStartUtcTicks ||
                !string.Equals(launch.Process.ExecutablePath, source.LaunchExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(launch.Process.ExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RecoveryGuardFailedLaunchIdentityMismatch");
            return launch.Process;
        }

        internal static void ValidateHalfOpen(RecoveryGuardSafetyPrepareRequest request, RecoveryControlState state,
            DurableAuthorityStoreReadResult loaded, ProcessObservation oldMain, DateTime nowUtc)
        {
            request.Validate();
            var source = loaded?.Record;
            var tx = state?.Transaction;
            var intent = state?.Intent;
            if (nowUtc.Kind != DateTimeKind.Utc || oldMain != ProcessObservation.Exited ||
                intent?.MainProcess?.IsValid() != true || intent.DesiredState != RecoveryDesiredState.Run ||
                tx?.Stage != RecoveryStage.SafeStop || tx.OwnershipReleased || tx.TransactionId != request.TransactionId ||
                tx.Epoch != request.Epoch || tx.Owner?.Matches(request.Owner) != true ||
                tx.CircuitCooldownSeconds < 1 || tx.CircuitCooldownSeconds > 86400 ||
                state.Launches == null || state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started") ||
                loaded?.Exists != true || loaded.Blocked || loaded.Unproven || source == null ||
                source.SessionId != intent.WatchdogSessionId || source.State != DurableRelaunchPermitState.Blocked ||
                !source.CircuitOpen || source.DetailCode != "RelaunchBudgetExhausted" ||
                source.LastFailureDisposition != RecoveryFailureDispositions.CircuitOpen ||
                source.ConsecutiveFailures < source.MaximumProcessRelaunches ||
                string.IsNullOrWhiteSpace(source.LastFailureCode) ||
                !ContinuousRecoveryPolicy.CanRetrySoftwareFailure(source.LastFailureCode, source.LastFailurePermanent) ||
                !Guid.TryParse(source.RunId, out var sourceRun) || !Guid.TryParse(intent.RunId, out var currentRun) || sourceRun != currentRun)
                throw new InvalidDataException("RecoveryGuardHalfOpenSourceRequiresReconciliation");
            if ((source.ProcessId == 0 && source.LaunchConsumed) ||
                (source.ProcessId != 0 && (source.ProcessId != intent.MainProcess.ProcessId ||
                    source.ProcessStartUtcTicks != intent.MainProcess.StartUtcTicks)))
                throw new InvalidDataException("RecoveryGuardHalfOpenMainIdentityUnproven");
            // Keep the existing host's longest normal cooldown floor, including
            // its LastKnownGood delay. The Guard value is frozen at Claim.
            var lastKnownGood = string.Equals(Path.GetFileName(Path.GetDirectoryName(intent.MainProcess.ExecutablePath)),
                "LastKnownGood", StringComparison.OrdinalIgnoreCase);
            var seconds = Math.Max(tx.CircuitCooldownSeconds, WatchdogHost.CalculateCircuitHalfOpenDelaySeconds(2, lastKnownGood));
            if (source.LastFailureDecisionUtcTicks <= 0 || source.LastFailureDecisionUtcTicks > nowUtc.Ticks ||
                nowUtc.Ticks - source.LastFailureDecisionUtcTicks < TimeSpan.FromSeconds(seconds).Ticks)
                throw new InvalidOperationException("RecoveryGuardHalfOpenCoolingDown");
        }

        internal static RecoveryFailureOperation BuildFailure(RecoveryGuardSafetyPrepareRequest request, RecoveryControlState state,
            DurableAuthorityStoreReadResult loaded, ProcessObservation oldMain)
        {
            request.Validate();
            var source = loaded?.Record;
            if (oldMain != ProcessObservation.Exited || state?.Intent?.MainProcess?.IsValid() != true ||
                state.Transaction?.Stage != RecoveryStage.SafeStop || state.Transaction.TransactionId != request.TransactionId ||
                state.Transaction.Epoch != request.Epoch || state.Transaction.Owner?.Matches(request.Owner) != true ||
                state.Launches == null || state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started") ||
                loaded?.Exists != true || loaded.Blocked || loaded.Unproven || source == null || source.CircuitOpen ||
                source.SessionId != state.Intent.WatchdogSessionId ||
                (source.State != DurableRelaunchPermitState.None && source.State != DurableRelaunchPermitState.Failed &&
                    source.State != DurableRelaunchPermitState.Committed && source.State != DurableRelaunchPermitState.Attached))
                throw new InvalidDataException("RecoveryGuardFailureSourceRequiresReconciliation");
            if ((source.State == DurableRelaunchPermitState.Committed || source.State == DurableRelaunchPermitState.Attached) &&
                (source.ProcessId != state.Intent.MainProcess.ProcessId || source.ProcessStartUtcTicks != state.Intent.MainProcess.StartUtcTicks))
                throw new InvalidDataException("RecoveryGuardFailureMainIdentityMismatch");
            var payload = SupervisorProtocol.ComputeTextSha256("GuardMainExited|" + state.InstallationId + "|" +
                request.TransactionId + "|" + request.Epoch.ToString(CultureInfo.InvariantCulture) + "|" + loaded.Sha256);
            return new RecoveryFailureOperation
            {
                OperationId = payload.Substring(0, 32).ToLowerInvariant(), RequestCorrelationId = payload.Substring(0, 32).ToLowerInvariant(),
                RequestPayloadSha256 = payload, SessionId = source.SessionId,
                SessionNonce = request.TransactionId,
                // Legacy protocol names: these fields carry the actual Guard
                // reporter, explicitly identified by RecoveryProcessSource.
                // Never substitute the registered sidecar's PID or nonce.
                SidecarProcessId = request.Owner.ProcessId, SidecarProcessStartUtcTicks = request.Owner.StartUtcTicks,
                ConnectionGeneration = request.Epoch, RecoveryAttemptGeneration = checked(source.Generation + 1),
                FailureCode = "GuardMainExited", FailureFingerprint = "GuardMainExited", Permanent = false,
                DetailCode = "GuardObservedExactExit", RunId = state.Intent.RunId, RunEpoch = request.Epoch,
                RecoveryStage = "SafeStop", RecoveryProgressToken = request.TransactionId,
                RecoveryProcessSource = "RecoveryGuard", DeviceOrChannelGroup = "CurrentAuthorizedRun",
                MaximumProcessRelaunches = source.MaximumProcessRelaunches
            };
        }
    }
}
