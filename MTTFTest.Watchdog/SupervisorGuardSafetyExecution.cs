using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class SupervisorServiceRuntime
    {
        private void HandleGuardSafetyExecuteConnection(NamedPipeServerStream pipe, BinaryReader reader, BinaryWriter writer)
        {
            RecoveryGuardSafetyExecuteRequest request = null;
            RecoveryGuardSafetyExecuteResponse response;
            try
            {
                request = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardSafetyExecuteRequest>(reader);
                request?.Validate();
                if (request == null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var peer) ||
                    peer != request.Owner.ProcessId ||
                    RecoveryProcessProbe.Observe(request.Owner, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.ExactAlive)
                    throw new InvalidDataException("RecoveryGuardPipeOwnerMismatch");
                response = ExecuteGuardSafety(request);
            }
            catch (Exception ex)
            {
                response = new RecoveryGuardSafetyExecuteResponse
                {
                    RequestId = request?.RequestId, SafetyAuthorityId = request?.SafetyAuthorityId,
                    State = RecoveryGuardSafetyExecutionState.Blocked, Accepted = false, Detail = ex.GetBaseException().Message
                };
            }
            RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.SafetyExecuteResponseMagic, response);
        }

        private RecoveryGuardSafetyExecuteResponse ExecuteGuardSafety(RecoveryGuardSafetyExecuteRequest request)
        {
            AssertGuardSafetyMaintenanceAllowed();
            var control = new RecoveryControlStore();
            var state = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                RecoveryStage.SafeStop, DateTime.UtcNow);
            if (string.IsNullOrWhiteSpace(state.Intent.WatchdogSessionId) ||
                !_sessions.TryGetValue(state.Intent.WatchdogSessionId, out var session) ||
                !session.MatchesMainProcessIdentity(state.Intent.MainProcess.ProcessId, state.Intent.MainProcess.StartUtcTicks))
                throw new InvalidDataException("RecoveryGuardRegisteredSessionMismatch");
            if (RecoveryProcessProbe.Observe(state.Intent.MainProcess, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryGuardOldMainExitUnproven");
            var project = WatchdogJournalPaths.ValidateProjectDirectory(session.RegisteredProjectDirectory);
            if (!WatchdogCrashRecoverySeedStore.TryRead(project, state.Intent.WatchdogSessionId, out var seed))
                throw new InvalidDataException("RecoveryGuardCrashSeedUnavailable");
            var loaded = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId).Load(state.Intent.WatchdogSessionId);
            if (loaded?.Exists != true || loaded.Unproven || loaded.Blocked)
                throw new InvalidDataException("RecoveryGuardSafetyPermitUnavailable");
            SupervisorGuardSafetyPreparationPolicy.ValidateSource(state, seed, loaded.Record);
            if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, request.SafetyAuthorityId, out var authority, out var failure))
                throw new InvalidDataException("RecoveryGuardSafetyAuthorityUnavailable:" + failure);
            SupervisorGuardSafetyPreparationPolicy.ValidateExisting(state, seed, loaded.Record, authority);
            if (!session.MatchesProjectRoot(authority.ProjectDirectory) ||
                !session.MatchesMainExecutable(authority.Receipt.MainExecutablePath, authority.Receipt.MainExecutableSha256))
                throw new InvalidDataException("RecoveryGuardSafetyRegistrationMismatch");
            var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(project, authority.HandoffId,
                authority.Receipt.ConfigSnapshotPath, authority.Receipt.ConfigSnapshotManifestPath,
                authority.Receipt.ConfigSnapshotManifestSha256);
            if (snapshot?.Succeeded != true) throw new InvalidDataException("RecoveryGuardSafetySnapshotInvalid");
            string serviceDirectory;
            using (var process = Process.GetCurrentProcess()) serviceDirectory = Path.GetDirectoryName(process.MainModule.FileName);
            var launch = SupervisorGuardSafetyExecutionPolicy.BuildLaunchRequest(request, authority, serviceDirectory);
            if (!SupervisorProtocol.Sha256Equals(SupervisorProtocol.ComputeSha256(launch.ExecutablePath), launch.ExecutableSha256))
                throw new InvalidDataException("RecoveryGuardSafetyAgentPackageChanged");
            var key = launch.SessionId + "-" + launch.PermitGeneration + "-" + launch.PermitId + "-" + launch.HandoffId;
            var owned = _safetyAgents.GetOrAdd(key, _ => new SupervisorOwnedSafetyAgent(key));
            var observed = owned.ObserveRecorded(launch, StateDirectory, out var worker, out var hasRecord);
            var result = SupervisorGuardSafetyExecutionPolicy.Evaluate(authority.Receipt, observed);
            if (authority.Receipt.IsTerminal || observed == ProcessObservation.ExactAlive)
                return SafetyExecutionResponse(request, result, worker, "SafetyReceiptAndWorker:" + observed);
            if (hasRecord && observed != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryGuardSafetyWorkerOutcomeUnproven");

            var identity = owned.RegisterOrGet(launch, authority.Receipt, project, StateDirectory, () =>
            {
                AssertGuardSafetyMaintenanceAllowed();
                var latest = control.ReadOwnedStage(request.TransactionId, request.Epoch, request.Owner,
                    RecoveryStage.SafeStop, DateTime.UtcNow, state.Intent.WatchdogSessionId);
                if (!latest.Matches(state.Token()) || latest.Intent.MainProcess?.Matches(state.Intent.MainProcess) != true)
                    throw new InvalidOperationException("RecoveryGuardSafetyExecutionSuperseded");
                var permit = new DurableRelaunchAuthorityFileStore(project, state.Intent.WatchdogSessionId).Load(state.Intent.WatchdogSessionId);
                if (permit?.Exists != true || permit.Unproven || permit.Blocked ||
                    permit.Revision != loaded.Revision || permit.Sha256 != loaded.Sha256)
                    throw new InvalidOperationException("RecoveryGuardSafetyPermitChangedBeforeExecution");
                if (!SupervisorSafetyAuthorityStore.TryRead(StateDirectory, request.SafetyAuthorityId, out var current, out var reason) ||
                    current.Receipt.IsTerminal || current.ReceiptRevision != authority.ReceiptRevision)
                    throw new InvalidOperationException("RecoveryGuardSafetyAuthorityChangedBeforeExecution:" + reason);
            });
            worker = new RecoveryProcessIdentity { ProcessId = identity.ProcessId, StartUtcTicks = identity.ProcessStartUtcTicks,
                ExecutablePath = launch.ExecutablePath, BootId = RecoveryProcessProbe.ReadBootId() };
            WriteAudit("RecoveryGuardSafetyAgentJoined", "Authority=" + authority.AuthorityId + ";PID=" + worker.ProcessId);
            return SafetyExecutionResponse(request, RecoveryGuardSafetyExecutionState.Pending, worker, "SafetyAgentStartedOrReattached");
        }

        private static RecoveryGuardSafetyExecuteResponse SafetyExecutionResponse(RecoveryGuardSafetyExecuteRequest request,
            RecoveryGuardSafetyExecutionState state, RecoveryProcessIdentity worker, string detail) => new RecoveryGuardSafetyExecuteResponse
        {
            RequestId = request.RequestId, SafetyAuthorityId = request.SafetyAuthorityId,
            Accepted = state != RecoveryGuardSafetyExecutionState.Blocked, State = state, Worker = worker, Detail = detail
        };
    }

    internal static class SupervisorGuardSafetyExecutionPolicy
    {
        internal static RecoveryGuardSafetyExecutionState Evaluate(WatchdogSafetyHandoffReceipt receipt, ProcessObservation worker)
        {
            if (receipt == null) return RecoveryGuardSafetyExecutionState.Blocked;
            if (WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(receipt))
                return worker == ProcessObservation.Exited ? RecoveryGuardSafetyExecutionState.Completed :
                    worker == ProcessObservation.ExactAlive ? RecoveryGuardSafetyExecutionState.Pending : RecoveryGuardSafetyExecutionState.Blocked;
            return receipt.IsTerminal ? RecoveryGuardSafetyExecutionState.Blocked : RecoveryGuardSafetyExecutionState.Pending;
        }

        internal static SupervisorSafetyAgentLaunchRequest BuildLaunchRequest(RecoveryGuardSafetyExecuteRequest request,
            SupervisorSafetyAuthorityRecord authority, string serviceDirectory)
        {
            request.Validate();
            if (authority?.IsValid() != true || authority.AuthorityId != request.SafetyAuthorityId)
                throw new InvalidDataException("RecoveryGuardSafetyAuthorityBindingMismatch");
            var receipt = authority.Receipt;
            var executable = Path.GetFullPath(receipt.SafetyAgentExecutablePath);
            if (!string.Equals(Path.GetDirectoryName(executable), Path.GetFullPath(serviceDirectory), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(executable), "MTTFTest.SafetyAgent.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RecoveryGuardSafetyAgentPathMismatch");
            var arguments = "--session-id " + Quote(receipt.SessionId) + " --handoff-id " + Quote(receipt.HandoffId) +
                " --handoff-nonce " + Quote(receipt.Nonce) + " --journal-directory " + Quote(authority.ProjectDirectory);
            var result = new SupervisorSafetyAgentLaunchRequest
            {
                RequestId = request.RequestId, ChallengeNonce = Guid.NewGuid().ToString("N"),
                RequesterProcessId = request.Owner.ProcessId, RequesterProcessStartUtcTicks = request.Owner.StartUtcTicks,
                SessionId = receipt.SessionId, HandoffId = receipt.HandoffId,
                HandoffNonceSha256 = SupervisorProtocol.ComputeTextSha256(receipt.Nonce),
                PermitGeneration = receipt.RelaunchPermitGeneration, PermitId = receipt.RelaunchPermitId,
                AuthorityId = authority.AuthorityId, AuthorityReceiptRevision = authority.InitialReceiptRevision,
                AuthorityReceiptCanonicalSha256 = authority.InitialReceiptCanonicalSha256,
                ExecutablePath = executable, ExecutableSha256 = receipt.SafetyAgentExecutableSha256,
                Arguments = arguments, ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(arguments),
                WorkingDirectory = Path.GetFullPath(serviceDirectory)
            };
            if (!result.IsStructurallyValid()) throw new InvalidDataException("RecoveryGuardSafetyLaunchSpecificationInvalid");
            return result;
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
