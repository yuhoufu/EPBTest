using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class SupervisorServiceRuntime
    {
        private readonly object _guardSafetyPreparationGate = new object();

        private async Task InactiveRecoveryCleanupLoopAsync(CancellationToken cancellationToken)
        {
            string lastFailure = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ReconcileInactiveRecovery(cancellationToken);
                    lastFailure = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    var reason = ex.GetBaseException().Message;
                    if (reason != lastFailure) WriteAudit("InactiveRecoveryCleanupDeferred", reason);
                    lastFailure = reason;
                }
                try { await Task.Delay(15000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        private void ReconcileInactiveRecovery(CancellationToken cancellationToken)
        {
            var control = new RecoveryControlStore();
            if (!control.IsRegisteredOrPending) return;
            var state = control.Read();
            var tx = state.Transaction;
            var now = DateTime.UtcNow;
            if (state.Intent == null || tx == null || tx.OwnershipReleased || tx.LeaseUntilUtcTicks > now.Ticks ||
                (state.Intent.DesiredState == RecoveryDesiredState.Run && state.Observation?.Expired != true)) return;
            var boot = RecoveryProcessProbe.ReadBootId();
            if (RecoveryProcessProbe.Observe(tx.Owner, boot) != ProcessObservation.Exited ||
                RecoveryProcessProbe.Observe(state.Intent.MainProcess, boot) != ProcessObservation.Exited) return;
            if (string.IsNullOrEmpty(tx.SafetyAuthorityId))
                throw new InvalidOperationException("RecoveryInactiveSafetyAuthorityMissing");
            // Same gate as main/SafetyAgent creation: no new hardware actor can
            // appear between inventory verification and releasing the epoch.
            using (SupervisorHardwareLaunchGate.Enter(StateDirectory))
            {
                RecoveryLaunchReconciler.ReconcileOutstanding(control);
                SupervisorOwnedSafetyAgent.AssertOtherSafetyRecordsRetired(StateDirectory, null);
                if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, tx.SafetyAuthorityId, out var safety, out var failure) ||
                    safety.SessionId != state.Intent.WatchdogSessionId || safety.Receipt.StopSafetyTransactionId != tx.TransactionId ||
                    safety.Receipt.OldProcessId != state.Intent.MainProcess.ProcessId ||
                    safety.Receipt.OldProcessStartUtcTicks != state.Intent.MainProcess.StartUtcTicks)
                    throw new InvalidDataException("RecoveryInactiveSafetyAuthorityUnproven:" + failure);
                var worker = AssertGuardSafetyWorkerRetired(Guid.NewGuid().ToString("N"), tx.TransactionId, tx.Epoch, tx.Owner, safety);
                cancellationToken.ThrowIfCancellationRequested();
                control.ReconcileInactiveTakeover(state.Token(), tx.TransactionId, tx.Epoch, tx.Owner, ProcessObservation.Exited,
                    state.Intent.MainProcess, ProcessObservation.Exited, tx.SafetyAuthorityId, worker, ProcessObservation.Exited, DateTime.UtcNow);
                WriteAudit("InactiveRecoveryOwnershipClosed", "Transaction=" + tx.TransactionId + ";Epoch=" + tx.Epoch);
            }
        }

        private void HandleGuardSafetyPrepareConnection(NamedPipeServerStream pipe, BinaryReader reader, BinaryWriter writer)
        {
            RecoveryGuardSafetyPrepareRequest request = null;
            RecoveryGuardSafetyPrepareResponse response;
            try
            {
                request = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardSafetyPrepareRequest>(reader);
                request?.Validate();
                if (request == null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var peer) ||
                    peer != request.Owner.ProcessId ||
                    RecoveryProcessProbe.Observe(request.Owner, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.ExactAlive)
                    throw new InvalidDataException("RecoveryGuardPipeOwnerMismatch");
                var authority = PrepareGuardCrashSafety(request);
                response = new RecoveryGuardSafetyPrepareResponse
                {
                    RequestId = request.RequestId, Accepted = true, SafetyAuthorityId = authority.AuthorityId,
                    Detail = "SafetyAuthorityPrepared;SafetyExecutionNotCompletedByPreparation"
                };
            }
            catch (Exception ex)
            {
                response = new RecoveryGuardSafetyPrepareResponse
                { RequestId = request?.RequestId, Accepted = false, Detail = ex.GetBaseException().Message };
            }
            RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.SafetyPrepareResponseMagic, response);
        }

        private SupervisorSafetyAuthorityRecord PrepareGuardCrashSafety(RecoveryGuardSafetyPrepareRequest request)
        {
            // Serializes only preparation in this service, never a hardware wait
            // or the global authorization mutex. No SafetyAgent is started here.
            if (!Monitor.TryEnter(_guardSafetyPreparationGate, 3000))
                throw new IOException("RecoveryGuardSafetyPreparationBusy");
            try
            {
                AssertGuardSafetyMaintenanceAllowed();
                var control = new RecoveryControlStore();
                var stage = control.Read().Transaction?.Stage;
                if (stage != RecoveryStage.SafeStop && stage != RecoveryStage.Launch)
                    throw new InvalidOperationException("RecoveryGuardSafetyPreparationStageInvalid");
                var state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    stage.Value, DateTime.UtcNow);
                RecoveryLaunchReconciler.ReconcileOutstanding(control);
                state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    stage.Value, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                if (string.IsNullOrWhiteSpace(state.Intent.WatchdogSessionId) ||
                    !_sessions.TryGetValue(state.Intent.WatchdogSessionId, out var session) ||
                    !session.MatchesMainProcessIdentity(state.Intent.MainProcess.ProcessId, state.Intent.MainProcess.StartUtcTicks))
                    throw new InvalidDataException("RecoveryGuardRegisteredSessionMismatch");
                var project = WatchdogJournalPaths.ValidateProjectDirectory(session.RegisteredProjectDirectory);
                if (RecoveryProcessProbe.Observe(state.Intent.MainProcess, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.Exited)
                    throw new InvalidOperationException("RecoveryGuardOldMainExitUnproven");
                if (!WatchdogCrashRecoverySeedStore.TryRead(project, state.Intent.WatchdogSessionId, out var seed))
                    throw new InvalidDataException("RecoveryGuardCrashSeedUnavailable");
                var loaded = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId)
                    .Load(state.Intent.WatchdogSessionId);
                if (loaded?.Exists != true || loaded.Unproven || loaded.Blocked)
                    throw new InvalidDataException("RecoveryGuardSafetyPermitUnavailable");
                if (state.Transaction.ActionEpoch > 0 && state.Transaction.ActionEpoch < state.Transaction.Epoch &&
                    state.Transaction.LaunchOperationId == null)
                {
                    var tx = state.Transaction;
                    SupervisorGuardSafetyPreparationPolicy.ValidateSource(state, seed, loaded.Record);
                    if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, tx.SafetyAuthorityId, out var oldSafety, out var oldFailure))
                        throw new InvalidDataException("RecoveryGuardAdoptedSafetyUnavailable:" + oldFailure);
                    SupervisorGuardSafetyPreparationPolicy.ValidateExisting(state, seed, loaded.Record, oldSafety, tx.ActionEpoch);
                    using (SupervisorHardwareLaunchGate.Enter(StateDirectory))
                    {
                        var worker = AssertGuardSafetyWorkerRetired(request.RequestId, tx.TransactionId, tx.Epoch, request.Owner, oldSafety);
                        SupervisorOwnedSafetyAgent.AssertOtherSafetyRecordsRetired(StateDirectory, null);
                        control.ReconcileAdoptedSafetyAction(tx.TransactionId, tx.Epoch, request.Owner,
                            state.Intent.WatchdogSessionId, tx.ActionEpoch, tx.SafetyAuthorityId, state.Intent.MainProcess,
                            ProcessObservation.Exited, worker, ProcessObservation.Exited, DateTime.UtcNow);
                    }
                    throw new InvalidOperationException("RecoveryAdoptedSafetyRetired;PrepareNewSafety");
                }
                if (state.Transaction.LaunchOperationId != null)
                {
                    var tx = state.Transaction;
                    using (SupervisorHardwareLaunchGate.Enter(StateDirectory))
                    {
                        if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, tx.SafetyAuthorityId, out var previousSafety, out var priorFailure) ||
                            previousSafety.SessionId != state.Intent.WatchdogSessionId || previousSafety.Receipt.StopSafetyTransactionId != tx.TransactionId)
                            throw new InvalidDataException("RecoveryGuardPreviousSafetyUnproven:" + priorFailure);
                        var worker = AssertGuardSafetyWorkerRetired(request.RequestId, request.TransactionId, request.Epoch, request.Owner, previousSafety);
                        SupervisorOwnedSafetyAgent.AssertOtherSafetyRecordsRetired(StateDirectory, null);
                        if (loaded.Record.State == DurableRelaunchPermitState.Approved)
                        {
                            SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, state, loaded);
                            SupervisorGuardSafetyPreparationPolicy.ValidateExisting(state, seed, loaded.Record, previousSafety, tx.ActionEpoch);
                        }
                        else
                        {
                            loaded = EnsureGuardExitedPermit(request, state, project, loaded, closeOnly: true);
                            if (loaded.Record.State != DurableRelaunchPermitState.Failed ||
                            (loaded.Record.DetailCode != "GuardConfirmedLaunchedProcessExited:" + tx.LaunchOperationId &&
                             loaded.Record.DetailCode != "GuardConfirmedLaunchNeverConsumed:" + tx.LaunchOperationId))
                                throw new InvalidOperationException("RecoveryGuardPreviousLaunchRequiresReconciliation");
                        }
                        control.RestartTerminalRecoveryAttempt(tx.TransactionId, tx.Epoch, request.Owner, state.Intent.WatchdogSessionId,
                            tx.SafetyAuthorityId, tx.LaunchOperationId, worker, ProcessObservation.Exited, DateTime.UtcNow);
                    }
                    throw new InvalidOperationException("RecoveryAttemptRestarted;ReadCurrentEpoch");
                }
                if (stage != RecoveryStage.SafeStop)
                    throw new InvalidOperationException("RecoveryGuardSafetyPreparationStageInvalid");
                loaded = EnsureGuardExitedPermit(request, state, project, loaded);
                var permit = loaded.Record;
                SupervisorGuardSafetyPreparationPolicy.ValidateSource(state, seed, permit);
                if (!session.MatchesProjectRoot(seed.ProjectDirectory) ||
                    !session.MatchesMainExecutable(seed.MainExecutablePath, seed.MainExecutableSha256))
                    throw new InvalidDataException("RecoveryGuardSafetySeedRegistrationMismatch");
                var handoffId = SupervisorGuardSafetyPreparationPolicy.HandoffId(state, permit);
                var authorityId = SupervisorSafetyAuthorityStore.ComputeAuthorityId(seed.SessionId, handoffId,
                    permit.Generation, permit.PermitId);
                var authorityPath = SupervisorSafetyAuthorityStore.GetPath(StateDirectory, authorityId);
                if (File.Exists(authorityPath))
                {
                    if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, authorityId, out var existing, out var failure))
                        throw new InvalidDataException("RecoveryGuardExistingSafetyAuthorityCorrupt:" + failure);
                    SupervisorGuardSafetyPreparationPolicy.ValidateExisting(state, seed, permit, existing);
                    return existing;
                }
                // A lost primary with a surviving backup is not a new action.
                if (File.Exists(authorityPath + ".bak"))
                    throw new InvalidDataException("RecoveryGuardExistingSafetyAuthorityPrimaryMissing");
                var source = WatchdogSafetyConfigSnapshotStore.Validate(project, seed.SeedId, seed.ConfigSnapshotPath,
                    seed.ConfigSnapshotManifestPath, seed.ConfigSnapshotManifestSha256);
                if (source?.Succeeded != true ||
                    !SupervisorProtocol.Sha256Equals(SupervisorProtocol.ComputeSha256(seed.MainExecutablePath), seed.MainExecutableSha256) ||
                    !SupervisorProtocol.Sha256Equals(SupervisorProtocol.ComputeSha256(seed.SafetyAgentExecutablePath), seed.SafetyAgentExecutableSha256))
                    throw new InvalidDataException("RecoveryGuardSafetySourceSnapshotOrPackageInvalid");
                var snapshot = WatchdogSafetyConfigSnapshotStore.Create(project, handoffId, source.ConfigDirectory,
                    string.Empty, seed.BuildIdentity, source.Runtime, seed.SessionId, seed.SessionGeneration,
                    seed.SessionLease, permit.Generation, permit.PermitId, seed.MainExecutableSha256, seed.SafetyAgentExecutableSha256);
                if (snapshot?.Succeeded != true) throw new InvalidDataException("RecoveryGuardSafetySnapshotCreateFailed");
                var receipt = SupervisorGuardSafetyPreparationPolicy.CreateReceipt(state, seed, permit, snapshot,
                    ProcessObservation.Exited, DateTime.UtcNow);
                AssertGuardSafetyMaintenanceAllowed();
                var latest = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                if (!latest.Matches(state.Token()) || latest.Intent.MainProcess?.Matches(state.Intent.MainProcess) != true)
                    throw new InvalidOperationException("RecoveryGuardSafetyPreparationSuperseded");
                var currentPermit = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId)
                    .Load(state.Intent.WatchdogSessionId);
                if (currentPermit?.Exists != true || currentPermit.Unproven || currentPermit.Blocked ||
                    currentPermit.Revision != loaded.Revision || currentPermit.Sha256 != loaded.Sha256)
                    throw new InvalidOperationException("RecoveryGuardSafetyPermitChangedDuringPreparation");
                // This commit grants no output permission. Actual safety work
                // and every subsequent launch must revalidate the Guard fence.
                return SupervisorSafetyAuthorityStore.CreateOrRead(StateDirectory, project, receipt);
            }
            finally { Monitor.Exit(_guardSafetyPreparationGate); }
        }

        private static void AssertGuardSafetyMaintenanceAllowed()
        {
            WatchdogMaintenancePolicy.AssertMainLaunchAllowed();
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTestRecoveryGuard", "maintenance-inhibit.json")))
                throw new InvalidOperationException("RecoveryGuardMaintenanceInhibited");
        }
    }

    internal static class SupervisorGuardSafetyPreparationPolicy
    {
        internal static void ValidateSource(RecoveryControlState state, WatchdogCrashRecoverySeed seed,
            DurableRelaunchAuthorityRecord permit)
        {
            if (state?.Intent?.MainProcess?.IsValid() != true || state.Transaction?.Stage != RecoveryStage.SafeStop ||
                seed?.IsValidFor(state.Intent.WatchdogSessionId) != true ||
                permit == null || permit.SessionId != state.Intent.WatchdogSessionId ||
                permit.State != DurableRelaunchPermitState.Approved || permit.Generation < 1 ||
                !RecoveryGuardSupervisorProtocol.IsId(permit.PermitId) ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(permit.PermitNonce) ||
                !Guid.TryParse(permit.RunId, out var permitRun) || !Guid.TryParse(state.Intent.RunId, out var run) ||
                run == Guid.Empty || permitRun != run ||
                !string.Equals(seed.MainExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RecoveryGuardSafetySourceIdentityMismatch");
        }

        internal static string HandoffId(RecoveryControlState state, DurableRelaunchAuthorityRecord permit, long? actionEpoch = null) =>
            SupervisorProtocol.ComputeTextSha256("GuardSafety|" + state.InstallationId + "|" +
                state.Transaction.TransactionId + "|" + (actionEpoch ?? state.Transaction.Epoch) + "|" + permit.Generation + "|" + permit.PermitId)
                .Substring(0, 32).ToLowerInvariant();

        internal static void ValidateExisting(RecoveryControlState state, WatchdogCrashRecoverySeed seed,
            DurableRelaunchAuthorityRecord permit, SupervisorSafetyAuthorityRecord authority, long? actionEpoch = null)
        {
            var receipt = authority?.Receipt;
            if (authority?.IsValid() != true || authority.HandoffId != HandoffId(state, permit, actionEpoch) ||
                receipt.SessionId != seed.SessionId || receipt.SessionGeneration != seed.SessionGeneration ||
                receipt.SessionLease != seed.SessionLease || receipt.RelaunchPermitGeneration != permit.Generation ||
                receipt.RelaunchPermitId != permit.PermitId ||
                receipt.RelaunchPermitNonceSha256 != WatchdogTakeoverPermitBindingPolicy.HashNonce(permit.PermitNonce) ||
                receipt.OldProcessId != state.Intent.MainProcess.ProcessId ||
                receipt.OldProcessStartUtcTicks != state.Intent.MainProcess.StartUtcTicks ||
                receipt.RunId != state.Intent.RunId || !receipt.CrashRecovery ||
                receipt.StopSafetyTransactionId != state.Transaction.TransactionId ||
                !string.Equals(receipt.MainExecutablePath, seed.MainExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !SupervisorProtocol.Sha256Equals(receipt.MainExecutableSha256, seed.MainExecutableSha256) ||
                !string.Equals(receipt.SafetyAgentExecutablePath, seed.SafetyAgentExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !SupervisorProtocol.Sha256Equals(receipt.SafetyAgentExecutableSha256, seed.SafetyAgentExecutableSha256))
                throw new InvalidDataException("RecoveryGuardExistingSafetyAuthorityMismatch");
        }

        internal static WatchdogSafetyHandoffReceipt CreateReceipt(RecoveryControlState state, WatchdogCrashRecoverySeed seed,
            DurableRelaunchAuthorityRecord permit, WatchdogSafetyConfigSnapshotResult snapshot,
            ProcessObservation oldMainObservation, DateTime nowUtc)
        {
            ValidateSource(state, seed, permit);
            if (oldMainObservation != ProcessObservation.Exited)
                throw new InvalidDataException("RecoveryGuardOldMainExitUnproven");
            if (snapshot?.Succeeded != true || nowUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("RecoveryGuardSafetySnapshotInvalid");
            var manifest = snapshot.Manifest;
            if (manifest?.SchemaVersion != 2 || manifest.HandoffId != HandoffId(state, permit) ||
                manifest.SessionId != seed.SessionId || manifest.SessionGeneration != seed.SessionGeneration ||
                manifest.SessionLease != seed.SessionLease || manifest.PermitGeneration != permit.Generation ||
                manifest.PermitId != permit.PermitId ||
                !SupervisorProtocol.Sha256Equals(manifest.MainExecutableSha256, seed.MainExecutableSha256) ||
                !SupervisorProtocol.Sha256Equals(manifest.SafetyAgentExecutableSha256, seed.SafetyAgentExecutableSha256))
                throw new InvalidDataException("RecoveryGuardSafetySnapshotBindingMismatch");
            return new WatchdogSafetyHandoffReceipt
            {
                SchemaVersion = SupervisorProtocol.SchemaVersion, SessionId = seed.SessionId,
                SessionGeneration = seed.SessionGeneration, SessionLease = seed.SessionLease,
                HandoffId = HandoffId(state, permit), Nonce = Guid.NewGuid().ToString("N"),
                StopSafetyTransactionId = state.Transaction.TransactionId, RunId = state.Intent.RunId,
                Revision = 1, State = WatchdogSafetyHandoffState.Accepted, Stage = WatchdogSafetyStage.None,
                PersistenceDrained = false, LogicalQuiescent = true, HardwareResourcesReleased = true,
                ExecutionAuthorizationRevoked = true, CallbacksIsolated = true, CrashRecovery = true,
                OldProcessExitProven = true, OldProcessId = state.Intent.MainProcess.ProcessId,
                OldProcessStartUtcTicks = state.Intent.MainProcess.StartUtcTicks,
                OldProcessObservation = DurableRelaunchProcessObservation.Dead,
                OldProcessExitObservedUtcTicks = nowUtc.Ticks,
                OldProcessExitEvidenceOwner = WatchdogSafetyEvidenceOwner.SupervisorService,
                OldProcessExitEvidenceSource = "RecoveryGuardOwnedCrashPreparation",
                DataAuditState = WatchdogDataAuditState.CrashRepairRequired,
                ProjectDirectory = seed.ProjectDirectory, MainExecutablePath = seed.MainExecutablePath,
                MainExecutableSha256 = seed.MainExecutableSha256,
                SafetyAgentExecutablePath = seed.SafetyAgentExecutablePath, SafetyAgentExecutableSha256 = seed.SafetyAgentExecutableSha256,
                ConfigSnapshotPath = snapshot.ConfigDirectory, ConfigSnapshotManifestPath = snapshot.ManifestPath,
                ConfigSnapshotManifestSha256 = snapshot.ManifestSha256, ConfigSnapshotSchemaVersion = 2,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                RelaunchPermitGeneration = permit.Generation, RelaunchPermitId = permit.PermitId,
                RelaunchPermitNonceSha256 = WatchdogTakeoverPermitBindingPolicy.HashNonce(permit.PermitNonce),
                Detail = "GuardPreparedCrashSafety;HardwareSafetyPending"
            };
        }
    }
}
