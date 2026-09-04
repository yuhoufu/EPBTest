using System;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    // UI discovery never creates a replacement run for an existing journal.
    // Project switching must commit its own explicit transaction instead.
    public sealed class EngineEnvironmentSession
    {
        public string SessionId { get; private set; }
        public string RunId { get; private set; }
        public long RunEpoch { get; private set; }
        public bool ShouldLaunchEngine { get; private set; }
        public string Reason { get; private set; }

        public static bool IsExpectedProjectHandoffGap(RecoveryKernelJournalDocument journal, long now)
        {
            var command = journal?.PendingCommand;
            if (command?.IsStructurallyValid() != true || command.Kind != RecoveryCommandKind.ActivateProjectSwitch ||
                command.DeadlineUtcTicks <= now || journal.ActiveIntents?.Length != 1) return false;
            var intent = journal.ActiveIntents[0]; var desired = journal.DesiredState;
            return intent.IsStructurallyValid() && intent.Stage == RecoveryStage.ProjectSwitchActivating &&
                intent.OwnerId == command.OwnerId && intent.Identity.ToCanonicalString() == command.Identity.ToCanonicalString() &&
                intent.ProjectSwitch?.ComputeSha256() == command.ProjectSwitch.ComputeSha256() &&
                desired?.IsStructurallyValid() == true && desired.SessionId == command.Identity.SessionId &&
                desired.RunId == command.Identity.RunId && desired.RunEpoch == command.Identity.RunEpoch &&
                desired.State == SystemTerminalState.SafeIdleAlarmed;
        }

        public static EngineEnvironmentSession Resolve(RecoveryKernelJournalDocument journal,
            EngineStateSnapshot engine, bool nativeTransferAvailable)
        {
            if (journal == null) throw new ArgumentNullException(nameof(journal));
            if (engine != null && !engine.IsStructurallyValid()) throw new ArgumentException("EngineSnapshotInvalid");
            var desired = journal.DesiredState;
            if (desired != null)
            {
                if (!desired.IsStructurallyValid()) throw new InvalidOperationException("DurableSessionInvalid");
                var same = engine != null && engine.SessionId == desired.SessionId && engine.RunId == desired.RunId && engine.RunEpoch == desired.RunEpoch;
                // A settled alarm has no recovery owner to launch the missing host.
                // Reopen only the same safe-idle environment; retain the durable alarm
                // and require the normal operator/safety gates before any formal run.
                var launch = nativeTransferAvailable && engine == null && journal.ActiveIntents.Length == 0 && journal.PendingCommand == null &&
                    (desired.State == SystemTerminalState.StoppedByOperator || desired.State == SystemTerminalState.SafeIdleAlarmed);
                return new EngineEnvironmentSession
                {
                    SessionId = desired.SessionId, RunId = desired.RunId, RunEpoch = desired.RunEpoch,
                    ShouldLaunchEngine = launch,
                    Reason = same ? "ExistingDurableEngine" : launch
                        ? desired.State == SystemTerminalState.StoppedByOperator ? "ReopenStoppedDurableSession" : "ReopenAlarmedDurableSession"
                        : "DisplayDurableSessionWhileKernelOwnsRecovery"
                };
            }
            if (journal.ActiveIntents.Length != 0 || journal.PendingCommand != null)
                throw new InvalidOperationException("RecoveryOwnerWithoutDurableSession");
            if (engine != null)
                return new EngineEnvironmentSession { SessionId = engine.SessionId, RunId = engine.RunId, RunEpoch = engine.RunEpoch,
                    Reason = "AttachInitialEngineAwaitingAdmission" };
            if (!nativeTransferAvailable) throw new InvalidOperationException("InitialEngineEnvironmentBusy");
            return new EngineEnvironmentSession { SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1,
                ShouldLaunchEngine = true, Reason = "FirstSafeIdleEnvironment" };
        }
    }

    // Mutual exclusion for external EngineHost/SafetyAgent handle ownership.
    // This is not a recovery Owner and does not choose a recovery strategy.
    public sealed class EngineNativeProcessFence
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        public IDisposable TryAcquire() => _gate.Wait(0) ? new Lease(_gate) : null;
        public IDisposable AcquireUntil(long deadlineUtcTicks)
        {
            var milliseconds = Math.Min(180000, TimeSpan.FromTicks(Math.Max(0, deadlineUtcTicks - DateTime.UtcNow.Ticks)).TotalMilliseconds);
            if (milliseconds < 1 || !_gate.Wait((int)milliseconds)) throw new TimeoutException("EngineNativeTransferDeadlineExceeded");
            return new Lease(_gate);
        }
        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim _gate;
            internal Lease(SemaphoreSlim gate) { _gate = gate; }
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
