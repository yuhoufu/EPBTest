using System;
using System.Diagnostics;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Production host seam for the durable relaunch transaction.  The host
    /// owns policy/UI and this object owns only the persisted permit and OS
    /// process port.  Tests inject both ports, so they observe the same
    /// transitions used by WatchdogHost rather than a copy of the algorithm.
    /// </summary>
    internal sealed class HostRelaunchOrchestrator
    {
        private readonly DurableRelaunchCoordinator _coordinator;
        private readonly ISystemRelaunchProcessLauncher _launcher;

        internal HostRelaunchOrchestrator(
            string sessionId,
            IDurableRelaunchStore store,
            int maximumProcessRelaunches,
            ISystemRelaunchProcessLauncher launcher)
        {
            _coordinator = new DurableRelaunchCoordinator(
                sessionId,
                store,
                maximumProcessRelaunches);
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        }

        internal DurableRelaunchPermitRecord Snapshot => _coordinator.Snapshot;

        internal DurableRelaunchResult ApproveOrGetExisting(DurableRelaunchRequest request) =>
            _coordinator.ApproveOrGetExisting(request);

        internal DurableRelaunchResult BeginLaunch(DurableRelaunchPermitIdentity identity) =>
            _coordinator.BeginLaunch(identity);

        internal DurableRelaunchResult CommitStarted(
            DurableRelaunchPermitIdentity identity,
            int processId,
            long processStartUtcTicks) =>
            _coordinator.CommitStarted(identity, processId, processStartUtcTicks);

        internal DurableRelaunchResult CommitAttached(
            DurableRelaunchPermitIdentity identity,
            int processId,
            long processStartUtcTicks) =>
            _coordinator.CommitAttached(identity, processId, processStartUtcTicks);

        internal DurableRelaunchResult CommitRecoveryBatch(
            DurableRelaunchPermitIdentity identity,
            string runId,
            string progressToken,
            long recoveryCommitGeneration) =>
            _coordinator.CommitRecoveryBatch(
                identity,
                runId,
                progressToken,
                recoveryCommitGeneration);

        internal DurableRelaunchResult ClosePermitAndRegisterFailure(
            DurableRelaunchPermitIdentity identity,
            string failureCode,
            string failureReason = null,
            bool processIdentityMismatch = false) =>
            _coordinator.ClosePermitAndRegisterFailure(
                identity,
                failureCode,
                failureReason,
                processIdentityMismatch);

        internal DurableRelaunchResult RecoverAfterRestart(
            Func<int, long, DurableRelaunchProcessObservation> processProbe) =>
            _coordinator.RecoverAfterRestart(processProbe);

        internal DurableRelaunchResult Block(string reason) => _coordinator.Block(reason);

        internal Process Start(ProcessStartInfo startInfo) => _launcher.Start(startInfo);

        internal void KillExact(Process process) => _launcher.KillExact(process);
    }
}
