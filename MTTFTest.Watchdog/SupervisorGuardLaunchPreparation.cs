using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class GuardRecoveryHostBinding
    {
        internal int ProcessId;
        internal long StartUtcTicks;
        internal string InstanceNonce;
        internal string PipeName;
    }

    internal sealed partial class SupervisorServiceRuntime
    {
        private void HandleGuardLaunchPrepareConnection(NamedPipeServerStream pipe, BinaryReader reader, BinaryWriter writer)
        {
            RecoveryGuardLaunchPreparationRequest request = null;
            RecoveryGuardLaunchPreparationResponse response;
            try
            {
                request = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchPreparationRequest>(reader);
                request?.Validate();
                if (request == null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var peer) ||
                    peer != request.Owner.ProcessId || RecoveryProcessProbe.Observe(request.Owner,
                        RecoveryProcessProbe.ReadBootId()) != ProcessObservation.ExactAlive)
                    throw new InvalidDataException("RecoveryGuardPipeOwnerMismatch");
                response = PrepareGuardLaunch(request);
            }
            catch (Exception ex)
            {
                response = new RecoveryGuardLaunchPreparationResponse { RequestId = request?.RequestId,
                    SafetyAuthorityId = request?.SafetyAuthorityId, Detail = ex.GetBaseException().Message };
            }
            RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.LaunchPrepareResponseMagic, response);
        }

        private RecoveryGuardLaunchPreparationResponse PrepareGuardLaunch(RecoveryGuardLaunchPreparationRequest request)
        {
            if (!Monitor.TryEnter(_guardSafetyPreparationGate, 3000))
                throw new IOException("RecoveryGuardSafetyPreparationBusy");
            try { return PrepareGuardLaunchCore(request); }
            finally { Monitor.Exit(_guardSafetyPreparationGate); }
        }

        private RecoveryGuardLaunchPreparationResponse PrepareGuardLaunchCore(RecoveryGuardLaunchPreparationRequest request)
        {
            AssertGuardSafetyMaintenanceAllowed();
            var control = new RecoveryControlStore();
            var state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner, RecoveryStage.Launch, DateTime.UtcNow);
            if (string.IsNullOrEmpty(state.Intent.WatchdogSessionId) ||
                !_sessions.TryGetValue(state.Intent.WatchdogSessionId, out var session) ||
                !session.MatchesMainProcessIdentity(state.Intent.MainProcess.ProcessId, state.Intent.MainProcess.StartUtcTicks))
                throw new InvalidDataException("RecoveryGuardRegisteredSessionMismatch");
            if (RecoveryProcessProbe.Observe(state.Intent.MainProcess, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryGuardOldMainExitUnproven");
            if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, request.SafetyAuthorityId, out var safety, out var failure))
                throw new InvalidDataException("RecoveryGuardSafetyAuthorityUnavailable:" + failure);
            if (!session.MatchesProjectRoot(safety.ProjectDirectory) ||
                !session.MatchesMainExecutable(safety.Receipt.MainExecutablePath, safety.Receipt.MainExecutableSha256))
                throw new InvalidDataException("RecoveryGuardSafetyRegistrationMismatch");
            AssertGuardSafetyWorkerRetired(request.RequestId, request.TransactionId, request.Epoch, request.Owner, safety);
            var opened = DurableRelaunchAuthorityFactory.TryOpenExisting(safety.ProjectDirectory, state.Intent.WatchdogSessionId);
            if (opened?.Succeeded != true) throw new InvalidDataException("RecoveryGuardLaunchAuthorityUnavailable:" + opened?.Reason);
            var source = opened.Store.Record;
            var host = session.ReadRecoveryHostBinding();
            var intent = SupervisorGuardLaunchPreparationPolicy.Build(state, source, host, safety.ProjectDirectory);
            var launchRequest = new RecoveryGuardLaunchRequest { RequestId = request.RequestId, TransactionId = request.TransactionId,
                Epoch = request.Epoch, Owner = request.Owner, SafetyAuthorityId = request.SafetyAuthorityId, OperationId = intent.IntentId };
            // Validate the safety/permit binding before mutating the strict authority.
            var candidate = source.Clone();
            candidate.State = DurableRelaunchPermitState.LaunchIntent;
            candidate.LaunchIntentId = intent.IntentId;
            candidate.LaunchExecutablePath = intent.ExecutablePath;
            candidate.LaunchExecutableSha256 = intent.ExecutableSha256;
            SupervisorGuardLaunchPolicy.ValidateRecord(launchRequest, state, safety, candidate);
            var reservation = control.ReserveAndBindRecoveryLaunch(state.Token(), request.TransactionId, request.Epoch,
                request.Owner, request.SafetyAuthorityId, intent.IntentId, DateTime.UtcNow, DateTime.UtcNow.AddSeconds(60),
                state.Intent.WatchdogSessionId);
            if (reservation.State != "Reserved")
                throw new InvalidOperationException("RecoveryGuardPreparedLaunchRequiresReconciliation");
            if (source.State == DurableRelaunchPermitState.Approved)
            {
                control.CaptureGuardLaunchFence(request.TransactionId, request.Epoch, request.Owner, DateTime.UtcNow);
                var committed = opened.Authority.PrepareLaunchIntent(intent);
                if (committed?.Status != DurableAuthorityTransitionStatus.Committed)
                    throw new IOException("RecoveryGuardLaunchPreparationFailed:" + committed?.Reason);
            }
            else if (source.State != DurableRelaunchPermitState.LaunchIntent || source.LaunchConsumed ||
                source.LaunchIntentId != intent.IntentId || source.LaunchSpecSha256 != intent.LaunchSpecSha256)
                throw new InvalidDataException("RecoveryGuardExistingLaunchRequiresReconciliation");
            control.CaptureGuardLaunchFence(request.TransactionId, request.Epoch, request.Owner, DateTime.UtcNow);
            // Reconcile verified events, not invented progress: the old main
            // exited, the recorded safety worker completed and exited, and the
            // strict launch intent above is durable. No main has been created.
            foreach (var completedStage in new[] { RecoveryReplacementState.Approved,
                RecoveryReplacementState.OldProcessExitProven, RecoveryReplacementState.SafetyAgentRunning,
                RecoveryReplacementState.SafetyCompleted, RecoveryReplacementState.MainLaunchIntent })
            {
                var projected = RecoveryReplacementTransactionStore.Advance(safety.ProjectDirectory,
                    intent.SessionId, intent.Generation, intent.PermitId, completedStage,
                    "Guard reconciled verified history;Safety=" + safety.AuthorityId + ";Operation=" + intent.IntentId);
                if (projected?.Succeeded != true)
                    throw new InvalidOperationException("RecoveryGuardReplacementHistoryDeferred:" + projected?.Reason);
            }
            return new RecoveryGuardLaunchPreparationResponse { RequestId = request.RequestId,
                SafetyAuthorityId = request.SafetyAuthorityId, Accepted = true, OperationId = intent.IntentId,
                Detail = "LaunchIntentPrepared;NoProcessCreated" };
        }

        private RecoveryProcessIdentity AssertGuardSafetyWorkerRetired(string requestId, string transactionId, long epoch,
            RecoveryProcessIdentity owner, SupervisorSafetyAuthorityRecord authority)
        {
            string serviceDirectory;
            using (var process = Process.GetCurrentProcess()) serviceDirectory = Path.GetDirectoryName(process.MainModule.FileName);
            var request = new RecoveryGuardSafetyExecuteRequest { RequestId = requestId, TransactionId = transactionId,
                Epoch = epoch, Owner = owner, SafetyAuthorityId = authority.AuthorityId };
            var launch = SupervisorGuardSafetyExecutionPolicy.BuildLaunchRequest(request, authority, serviceDirectory);
            var key = launch.SessionId + "-" + launch.PermitGeneration + "-" + launch.PermitId + "-" + launch.HandoffId;
            var owned = _safetyAgents.GetOrAdd(key, _ => new SupervisorOwnedSafetyAgent(key));
            var observed = owned.ObserveRecorded(launch, StateDirectory, out var worker, out var hasRecord);
            if (!hasRecord || SupervisorGuardSafetyExecutionPolicy.Evaluate(authority.Receipt, observed) != RecoveryGuardSafetyExecutionState.Completed)
                throw new InvalidOperationException("RecoveryGuardSafetyWorkerRetirementUnproven");
            return worker;
        }
    }

    internal static class SupervisorGuardLaunchPreparationPolicy
    {
        internal static DurableLaunchIntent Build(RecoveryControlState state, DurableRelaunchAuthorityRecord permit,
            GuardRecoveryHostBinding host, string project)
        {
            var snapshot = state?.Observation?.LastSnapshot;
            if (snapshot?.SourceAvailable != true || snapshot.Authorization == null || state.Intent == null ||
                snapshot.Authorization.InstallationId != state.InstallationId ||
                snapshot.Authorization.AuthorizationId != state.Intent.AuthorizationId ||
                snapshot.Authorization.IntentVersion != state.Intent.IntentVersion || snapshot.RunId != state.Intent.RunId ||
                snapshot.ConfigurationIdentity != state.Intent.ConfigurationIdentity ||
                snapshot.MainProcess?.Matches(state.Intent.MainProcess) != true || snapshot.Channels == null ||
                snapshot.Channels.Length == 0 || snapshot.Channels.Any(c => c == null || c.Channel < 1 || c.Channel > 12) ||
                snapshot.Channels.Select(c => c.Channel).Distinct().Count() != snapshot.Channels.Length)
                throw new InvalidDataException("RecoveryGuardLaunchChannelSnapshotMismatch");
            var eligible = snapshot.Channels.Where(c => c.Eligible && !c.Completed && !c.PermanentlyIsolated && !c.ManuallyExcluded)
                .Select(c => c.Channel).ToArray();
            if (eligible.Length == 0) throw new InvalidOperationException("RecoveryGuardNoEligibleChannels");
            if (host == null || host.ProcessId <= 0 || host.StartUtcTicks <= 0 ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(host.InstanceNonce) || string.IsNullOrWhiteSpace(host.PipeName))
                throw new InvalidDataException("RecoveryGuardSessionHostUnavailable");
            var operation = SupervisorProtocol.ComputeTextSha256("GuardLaunch|" + state.InstallationId + "|" +
                state.Transaction.TransactionId + "|" + state.Transaction.Epoch + "|" + permit.Generation + "|" + permit.PermitId)
                .Substring(0, 32).ToLowerInvariant();
            var arguments = "--watchdog-recover " + Quote(permit.SessionId) + " --watchdog-pipe " + Quote(host.PipeName) +
                " --previous-pid " + state.Intent.MainProcess.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " --recovery-attempt " + checked(permit.ConsecutiveFailures + 1).ToString(CultureInfo.InvariantCulture) +
                " --exclude-channels " + Quote(string.Join(",", Enumerable.Range(1, 12).Except(eligible))) +
                " --sidecar-pid " + host.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " --sidecar-start-ticks " + host.StartUtcTicks.ToString(CultureInfo.InvariantCulture) +
                " --sidecar-instance-nonce " + Quote(host.InstanceNonce) +
                " --relaunch-generation " + permit.Generation.ToString(CultureInfo.InvariantCulture) +
                " --relaunch-permit-id " + Quote(permit.PermitId) + " --relaunch-permit-nonce " + Quote(permit.PermitNonce) +
                " --journal-directory " + Quote(project);
            var intent = new DurableLaunchIntent { SessionId = permit.SessionId, SessionNonce = permit.LastFailureSessionNonce,
                Generation = permit.Generation, PermitId = permit.PermitId, PermitNonce = permit.PermitNonce, IntentId = operation,
                ExecutablePath = Path.GetFullPath(state.MainExecutablePath),
                ExecutableSha256 = SupervisorProtocol.ComputeSha256(state.MainExecutablePath), Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(state.MainExecutablePath)),
                LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical };
            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
            return intent;
        }

        private static string Quote(string value)
        {
            if (value == null || value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0 || value.EndsWith("\\", StringComparison.Ordinal))
                throw new InvalidDataException("RecoveryGuardLaunchArgumentInvalid");
            return "\"" + value + "\"";
        }
    }
}
