using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class SupervisorServiceRuntime
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

        private void HandleGuardLaunchConnection(NamedPipeServerStream pipe, BinaryReader reader, BinaryWriter writer)
        {
            RecoveryGuardLaunchRequest request = null;
            RecoveryGuardLaunchResponse response;
            try
            {
                request = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchRequest>(reader);
                request?.Validate();
                if (request == null || !GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var peer) ||
                    peer != request.Owner.ProcessId ||
                    RecoveryProcessProbe.Observe(request.Owner, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.ExactAlive)
                    throw new InvalidDataException("RecoveryGuardPipeOwnerMismatch");
                response = LaunchGuardPreparedMain(request);
            }
            catch (Exception ex)
            {
                response = new RecoveryGuardLaunchResponse
                {
                    RequestId = request?.RequestId, OperationId = request?.OperationId,
                    Accepted = false, Detail = ex.GetBaseException().Message
                };
            }
            RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.ResponseMagic, response);
        }

        private RecoveryGuardLaunchResponse LaunchGuardPreparedMain(RecoveryGuardLaunchRequest request)
        {
            WatchdogMaintenancePolicy.AssertMainLaunchAllowed();
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTestRecoveryGuard", "maintenance-inhibit.json")))
                throw new InvalidOperationException("RecoveryGuardMaintenanceInhibited");
            var control = new RecoveryControlStore();
            control.CaptureGuardLaunchFence(request.TransactionId, request.Epoch, request.Owner, DateTime.UtcNow);
            var state = control.Read();
            if (string.IsNullOrEmpty(state.Intent?.WatchdogSessionId) ||
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

            // Reuse the prepared launch specification; the independent client
            // never supplies a command line, executable, project path or permit.
            var prepared = new DurableRelaunchAuthorityFileStore(safety.ProjectDirectory, state.Intent.WatchdogSessionId)
                .Load(state.Intent.WatchdogSessionId);
            SupervisorGuardLaunchPolicy.ValidateEvidence(request, state, safety, prepared);
            AssertGuardSafetyWorkerRetired(request.RequestId, request.TransactionId, request.Epoch, request.Owner, safety);
            var source = prepared.Record;
            var expected = SupervisorGuardLaunchPreparationPolicy.Build(state, source,
                session.ReadRecoveryHostBinding(), safety.ProjectDirectory);
            if (source.LaunchArguments != expected.Arguments)
                throw new InvalidDataException("RecoveryGuardPreparedHostOrChannelScopeChanged");
            var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(safety.ProjectDirectory, safety.HandoffId,
                safety.Receipt.ConfigSnapshotPath, safety.Receipt.ConfigSnapshotManifestPath,
                safety.Receipt.ConfigSnapshotManifestSha256);
            if (snapshot?.Succeeded != true) throw new InvalidDataException("RecoveryGuardSafetySnapshotInvalid");
            var executable = Path.GetFullPath(state.MainExecutablePath);
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(executable), "MTTFTest.UnattendedMode.required")) ||
                !SupervisorProtocol.Sha256Equals(SupervisorProtocol.ComputeSha256(executable), source.LaunchExecutableSha256))
                throw new InvalidDataException("RecoveryGuardMainPackageMismatch");
            var desktop = session.RegisteredDesktopSessionId;
            if (desktop < 0) throw new InvalidOperationException("RecoveryGuardWaitingForSession");
            var launchRequest = new SupervisorMainLaunchRequest
            {
                RequestId = request.RequestId, ChallengeNonce = Guid.NewGuid().ToString("N"),
                RequesterProcessId = request.Owner.ProcessId, RequesterProcessStartUtcTicks = request.Owner.StartUtcTicks,
                DesktopSessionId = SessionAgentProtocol.RegisteredDesktopSessionId,
                ExecutablePath = executable, ExecutableSha256 = source.LaunchExecutableSha256,
                Arguments = source.LaunchArguments,
                ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(source.LaunchArguments),
                WorkingDirectory = source.LaunchWorkingDirectory, IsRecoveryLaunch = true,
                RecoveryCapabilityId = request.OperationId, RecoverySessionId = source.SessionId,
                RecoveryPermitGeneration = source.Generation, RecoveryPermitId = source.PermitId,
                RecoveryAuthorityRevision = source.LaunchAuthorityRevision,
                RecoveryAuthoritySha256 = source.LaunchAuthoritySha256
            };
            if (!launchRequest.IsStructurallyValid()) throw new InvalidDataException("RecoveryGuardPreparedLaunchInvalid");
            // The shared implementation rechecks current Guard ownership while
            // holding the same local launch gate, consumes the existing global
            // restart budget, reconciles receipts and uses SessionAgent.
            var result = LaunchValidatedMainThroughSessionAgent(
                launchRequest, executable, desktop, request, safety.ProjectDirectory);
            return new RecoveryGuardLaunchResponse
            {
                RequestId = request.RequestId, OperationId = request.OperationId, Accepted = result.Accepted,
                Process = new RecoveryProcessIdentity
                {
                    ProcessId = result.ProcessId, StartUtcTicks = result.ProcessStartUtcTicks,
                    ExecutablePath = executable, BootId = RecoveryProcessProbe.ReadBootId()
                },
                Detail = result.Detail
            };
        }
    }

    internal sealed class SupervisorGuardStrictLaunchContext
    {
        internal DurableRelaunchAuthorityV4 Authority { get; set; }
        internal DurableLaunchIntentCapability Capability { get; set; }
    }

    internal static class SupervisorGuardStrictLaunchLifecycle
    {
        internal static SupervisorGuardStrictLaunchContext Consume(
            string projectDirectory, string sessionId, string operationId,
            long generation, string permitId, long authorityRevision,
            string authoritySha256, string executablePath, string executableSha256)
        {
            var opened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(
                WatchdogJournalPaths.ValidateProjectDirectory(projectDirectory), sessionId);
            if (opened?.Succeeded != true)
                throw new InvalidDataException("RecoveryGuardStrictLaunchAuthorityUnavailable:" + opened?.Reason);
            var resumed = opened.Authority.ResumeLaunchIntent();
            var capability = resumed?.Capability;
            if (resumed?.Succeeded != true || capability == null ||
                capability.SessionId != sessionId || capability.IntentId != operationId ||
                capability.Generation != generation || capability.PermitId != permitId ||
                capability.AuthorityRevision != authorityRevision ||
                !SupervisorProtocol.Sha256Equals(capability.AuthoritySha256, authoritySha256) ||
                !string.Equals(Path.GetFullPath(capability.ExecutablePath),
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) ||
                !SupervisorProtocol.Sha256Equals(capability.ExecutableSha256, executableSha256))
                throw new InvalidDataException("RecoveryGuardStrictLaunchCapabilityMismatch");
            var consumed = opened.Authority.ConsumeLaunchIntent(capability);
            if (consumed?.Status != DurableAuthorityTransitionStatus.Committed)
                throw new IOException("RecoveryGuardStrictLaunchConsumeFailed:" + consumed?.Reason);
            return new SupervisorGuardStrictLaunchContext
            {
                Authority = opened.Authority,
                Capability = capability
            };
        }

        internal static void CommitStarted(
            SupervisorGuardStrictLaunchContext context, int processId, long processStartUtcTicks)
        {
            if (context?.Authority == null || context.Capability == null)
                throw new ArgumentNullException(nameof(context));
            var committed = context.Authority.CommitStarted(
                context.Capability, processId, processStartUtcTicks);
            if (committed?.Status != DurableAuthorityTransitionStatus.Committed)
                throw new IOException("RecoveryGuardStrictLaunchStartedCommitFailed:" + committed?.Reason);
        }
    }

    internal static class SupervisorGuardLaunchPolicy
    {
        internal static void ValidateEvidence(RecoveryGuardLaunchRequest request, RecoveryControlState state,
            SupervisorSafetyAuthorityRecord safety, DurableAuthorityStoreReadResult prepared)
        {
            if (prepared == null || prepared.Unproven || prepared.Blocked || !prepared.Exists)
                throw new InvalidDataException("RecoveryGuardPreparedAuthorityMismatch");
            ValidateRecord(request, state, safety, prepared.Record);
        }

        internal static void ValidateRecord(RecoveryGuardLaunchRequest request, RecoveryControlState state,
            SupervisorSafetyAuthorityRecord safety, DurableRelaunchAuthorityRecord source)
        {
            request.Validate();
            var receipt = safety?.Receipt;
            if (safety?.IsValid() != true || safety.AuthorityId != request.SafetyAuthorityId ||
                safety.SessionId != state.Intent?.WatchdogSessionId ||
                !WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(receipt) ||
                receipt.RelaunchDisposition != WatchdogRelaunchDisposition.PreserveApprovedPermit ||
                receipt.OldProcessId != state.Intent.MainProcess?.ProcessId ||
                receipt.OldProcessStartUtcTicks != state.Intent.MainProcess.StartUtcTicks ||
                !SameRun(receipt.RunId, state.Intent.RunId) ||
                !string.Equals(receipt.MainExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RecoveryGuardSafetyEvidenceMismatch");
            if (source == null ||
                source.State != DurableRelaunchPermitState.LaunchIntent ||
                !SameRun(source.RunId, state.Intent.RunId) ||
                source.SessionId != safety.SessionId || source.LaunchIntentId != request.OperationId ||
                source.Generation != receipt.RelaunchPermitGeneration || source.PermitId != receipt.RelaunchPermitId ||
                WatchdogTakeoverPermitBindingPolicy.HashNonce(source.PermitNonce) != receipt.RelaunchPermitNonceSha256 ||
                !string.Equals(source.LaunchExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !SupervisorProtocol.Sha256Equals(source.LaunchExecutableSha256, receipt.MainExecutableSha256))
                throw new InvalidDataException("RecoveryGuardPreparedAuthorityMismatch");
        }

        private static bool SameRun(string left, string right) => Guid.TryParse(left, out var a) &&
            Guid.TryParse(right, out var b) && a != Guid.Empty && a == b;
    }
}
