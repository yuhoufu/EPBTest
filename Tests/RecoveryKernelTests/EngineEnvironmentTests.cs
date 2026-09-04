using System;
using System.Linq;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        private static void FirstEngineEnvironmentDoesNotReinitializeObservedHost()
        {
            var empty = new RecoveryKernelJournalDocument();
            var first = EngineEnvironmentSession.Resolve(empty, null, true);
            Assert(first.ShouldLaunchEngine && first.RunEpoch == 1 && RecoveryProtocolV7.IsGuid(first.SessionId), "initial environment invalid");
            var existing = EnvironmentEngine(Identity("System")); existing.HardwareInitialized = false;
            var attached = EngineEnvironmentSession.Resolve(empty, existing, true);
            Assert(!attached.ShouldLaunchEngine && attached.SessionId == existing.SessionId && attached.RunId == existing.RunId,
                "UI reset identity or reinitialized a starting engine");
        }

        private static void StoppedEnvironmentRetainsDurableRun()
        {
            var coordinator = ParkedEnvironment(out var identity);
            var document = coordinator.Snapshot();
            document.Budgets = new[] { new RecoveryBudgetState { ResourceScope = "Channel:4", LocalRebuildAttempts = 2 } };
            document.IsolatedResources = new[] { "Channel:4" };
            var plan = EngineEnvironmentSession.Resolve(document, null, true);
            Assert(plan.ShouldLaunchEngine && plan.SessionId == identity.SessionId && plan.RunId == identity.RunId && plan.RunEpoch == identity.RunEpoch,
                "stopped reopen created another session/run");
            Assert(document.IsolatedResources.Single() == "Channel:4" && document.Budgets.Single().LocalRebuildAttempts == 2,
                "UI discovery reset recovery budget or isolation");
            Assert(!EngineEnvironmentSession.Resolve(document, null, false).ShouldLaunchEngine, "UI crossed an external native transfer");
        }

        private static void AlarmedEnvironmentReopensWithoutClearingSafetyFailure()
        {
            var document = ParkedEnvironment(out var identity).Snapshot();
            document.DesiredState.State = SystemTerminalState.SafeIdleAlarmed;
            document.DesiredState.Reason = "SafetyProofIncomplete";
            document.Budgets = new[] { new RecoveryBudgetState { ResourceScope = "Channel:4", LocalRebuildAttempts = 2 } };
            document.IsolatedResources = new[] { "Channel:4" };
            var revision = document.Revision;
            var plan = EngineEnvironmentSession.Resolve(document, null, true);
            Assert(plan.ShouldLaunchEngine && plan.Reason == "ReopenAlarmedDurableSession" &&
                plan.SessionId == identity.SessionId && plan.RunId == identity.RunId && plan.RunEpoch == identity.RunEpoch,
                "ownerless alarmed session left UI waiting for a nonexistent recovery");
            Assert(document.Revision == revision && document.DesiredState.State == SystemTerminalState.SafeIdleAlarmed &&
                document.DesiredState.Reason == "SafetyProofIncomplete" && document.IsolatedResources.Single() == "Channel:4" &&
                document.Budgets.Single().LocalRebuildAttempts == 2 && document.PendingCommand == null,
                "reopening cleared safety failure, recovery history or issued a run command");
            Assert(!EngineEnvironmentSession.Resolve(document, null, false).ShouldLaunchEngine,
                "alarmed reopen crossed native ownership fence");
            Assert(!EngineEnvironmentSession.Resolve(document, EnvironmentEngine(identity), true).ShouldLaunchEngine,
                "alarmed reopen duplicated an existing engine");
            var recovering = StartRecoverableIncident(out _).Snapshot();
            recovering.DesiredState.State = SystemTerminalState.SafeIdleAlarmed;
            Assert(!EngineEnvironmentSession.Resolve(recovering, null, true).ShouldLaunchEngine,
                "alarmed reopen competed with active recovery");
            recovering.ActiveIntents = Array.Empty<RecoveryIntent>();
            Assert(recovering.PendingCommand != null && !EngineEnvironmentSession.Resolve(recovering, null, true).ShouldLaunchEngine,
                "alarmed reopen ignored pending command");
        }

        private static void RecoveringEnvironmentDisplaysWithoutLaunching()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var before = coordinator.Snapshot();
            foreach (var available in new[] { false, true })
            {
                var plan = EngineEnvironmentSession.Resolve(before, null, available);
                Assert(!plan.ShouldLaunchEngine && plan.RunId == before.DesiredState.RunId, "UI launched a competing recovery process");
            }
            Assert(coordinator.Snapshot().ActiveIntents.Single().Identity.IncidentId == incident &&
                coordinator.Snapshot().Revision == before.Revision, "UI modified durable owner");
        }

        private static void ForeignRunCannotOverwriteDurableSession()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var before = coordinator.Snapshot();
            var foreign = Identity("System");
            Assert(!coordinator.AdmitSafeIdleEngine(EnvironmentEngine(foreign)), "foreign ready process erased incident");
            RejectEnvironment(() => coordinator.Observe(Observation(foreign, ResourceKind.System, "System", false)));
            RejectEnvironment(() => coordinator.StartByOperator(foreign.SessionId, foreign.RunId, foreign.RunEpoch, "foreign"));
            RejectEnvironment(() => coordinator.StopByOperator(foreign.SessionId, foreign.RunId, foreign.RunEpoch, "foreign"));
            var after = coordinator.Snapshot();
            Assert(after.Revision == before.Revision && after.ActiveIntents.Single().OwnerId == before.ActiveIntents.Single().OwnerId &&
                after.PendingCommand.CommandId == before.PendingCommand.CommandId, "foreign ingress changed durable transaction");
            var ui = EngineEnvironmentSession.Resolve(after, EnvironmentEngine(foreign), true);
            Assert(!ui.ShouldLaunchEngine && ui.RunId == before.DesiredState.RunId, "foreign snapshot became UI run authority");
            var parked = ParkedEnvironment(out _);
            var parkedBefore = parked.Snapshot();
            Assert(!parked.AdmitSafeIdleEngine(EnvironmentEngine(foreign)) && parked.Snapshot().Revision == parkedBefore.Revision,
                "stopped discovery silently switched projects");
        }

        private static void OperatorStartRequiresFinishedInitialization()
        {
            foreach (var invalidate in new Action<EngineStateSnapshot>[]
            {
                e => e.HardwareInitialized = false, e => e.OutputsEnergized = true,
                e => e.RecoveryOwnerId = RecoveryProtocolV7.NewId(), e => e.RecoveryIncidentId = RecoveryProtocolV7.NewId(),
                e => e.State = SystemTerminalState.Running
            })
            {
                var coordinator = new RecoveryCoordinator(new MemoryJournal());
                var command = Operator(out var engine, OperatorCommandKind.Start); invalidate(engine);
                Assert(!coordinator.AdmitOperatorCommand(command, engine).Accepted && coordinator.Snapshot().PendingCommand == null,
                    "start admitted while initialization or another owner remained");
            }
            var ready = new RecoveryCoordinator(new MemoryJournal());
            var start = Operator(out var released, OperatorCommandKind.Start);
            released.HardwareInitialized = false; released.HardwareRecompositionReady = true;
            Assert(ready.AdmitOperatorCommand(start, released).Accepted, "released host cannot enter supervised preflight");
        }

        private static void ExternalNativeOwnershipIsExclusiveAndBounded()
        {
            var fence = new EngineNativeProcessFence();
            using (var old = fence.TryAcquire())
            {
                Assert(old != null && fence.TryAcquire() == null, "native transfer was not exclusive");
                var timedOut = false;
                try { using (fence.AcquireUntil(DateTime.UtcNow.AddMilliseconds(20).Ticks)) { } }
                catch (TimeoutException) { timedOut = true; }
                Assert(timedOut && fence.TryAcquire() == null, "timeout released another native owner");
                old.Dispose();
                using (var next = fence.TryAcquire())
                {
                    Assert(next != null, "actual release did not admit next owner");
                    old.Dispose();
                    Assert(fence.TryAcquire() == null, "duplicate dispose released replacement owner");
                }
            }
        }

        private static RecoveryCoordinator ParkedEnvironment(out RecoveryIdentity identity)
        {
            identity = Identity("System");
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var stop = coordinator.StopByOperator(identity.SessionId, identity.RunId, identity.RunEpoch, "test stop");
            coordinator.AcknowledgeCommand(stop.Intent.Identity.IncidentId, stop.Command.CommandId, true, "isolated fixture proof");
            return coordinator;
        }

        private static EngineStateSnapshot EnvironmentEngine(RecoveryIdentity identity) => new EngineStateSnapshot
        {
            EngineInstanceId = RecoveryProtocolV7.NewId(), SessionId = identity.SessionId, RunId = identity.RunId, RunEpoch = identity.RunEpoch,
            Revision = 1, PulseSequence = 1, CapturedUtcTicks = DateTime.UtcNow.Ticks, State = SystemTerminalState.SafeIdleAlarmed, HardwareInitialized = true
        };

        private static void RejectEnvironment(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("foreign environment accepted");
        }
    }
}
