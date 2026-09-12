using System;
using System.IO;
using System.Threading;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class SupervisorHardwareLaunchGate : IDisposable
    {
        private readonly Mutex _mutex;
        private SupervisorHardwareLaunchGate(Mutex mutex) { _mutex = mutex; }

        internal static SupervisorHardwareLaunchGate Enter(string stateDirectory)
        {
            var mutex = new Mutex(false, "Global\\MTTFTest.SafetyLaunch." +
                SupervisorProtocol.ComputeTextSha256(Path.GetFullPath(stateDirectory).ToUpperInvariant()).Substring(0, 32));
            try
            {
                bool held;
                try { held = mutex.WaitOne(3000); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("SupervisorSafetyLaunchBusy");
                return new SupervisorHardwareLaunchGate(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
    }

    internal static class SupervisorSafetyTakeoverPolicy
    {
        internal static void AssertLegacyCreationAllowed(RecoveryControlState state, WatchdogSafetyHandoffReceipt receipt,
            bool independentStopOrPauseConfirmed = false)
        {
            if (state.Intent?.DesiredState == RecoveryDesiredState.Run && !independentStopOrPauseConfirmed &&
                (state.Intent.MainProcess == null || receipt?.OldProcessId != state.Intent.MainProcess.ProcessId ||
                    receipt.OldProcessStartUtcTicks != state.Intent.MainProcess.StartUtcTicks))
                throw new InvalidOperationException("SupervisorSafetyLegacyRunSuperseded");
            if (state.Transaction == null || state.Transaction.OwnershipReleased) return;
            // A terminal operator intent must still be able to request safe-off.
            // A sidecar's Forbidden receipt alone cannot override a live Run
            // authority and inject another hardware owner during takeover.
            if (receipt?.RelaunchDisposition == WatchdogRelaunchDisposition.Forbidden &&
                (independentStopOrPauseConfirmed || state.Intent != null && (state.Intent.DesiredState == RecoveryDesiredState.Stopped ||
                    state.Intent.DesiredState == RecoveryDesiredState.Paused || state.Intent.DesiredState == RecoveryDesiredState.Completed)))
                return;
            throw new InvalidOperationException("SupervisorSafetyLegacyCreationTakeoverFenced");
        }
    }
}
