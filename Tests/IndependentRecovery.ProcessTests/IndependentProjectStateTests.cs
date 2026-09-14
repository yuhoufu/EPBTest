using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentProjectStateTests
    {
        private static string Exe => Process.GetCurrentProcess().MainModule.FileName;
        private static string Hash => new string('a', 64);
        private static int _count;
        private static void Assert(bool value, string reason)
        { if (!value) throw new Exception(reason); _count++; }
        private static void Reject(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException) { _count++; return; }
            throw new Exception(reason);
        }
        private static IndependentProcessIdentity Identity()
        {
            using (var p = Process.GetCurrentProcess()) return new IndependentProcessIdentity
            { Pid = p.Id, StartUtcTicks = p.StartTime.ToUniversalTime().Ticks,
                WindowsSessionId = p.SessionId, ExecutablePath = Exe, SessionToken = Guid.NewGuid().ToString("N") };
        }
        private static IndependentRunIntent Intent(string root) => new IndependentRunIntent
        {
            Revision = 1, ProjectDirectory = root, DatabasePath = Path.Combine(root, "index.db"),
            DatabaseCreationUtcTicks = 1, ExecutablePath = Exe, ConfigurationSha256 = Hash,
            RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, Armed = true,
            SelectedChannels = new[] { 4, 5, 7, 8, 9, 12 }, PeriodMs = 15000, StartupBudgetMs = 180000
        };
        private static IndependentProjectStateStore Fixture(string root, long now)
        {
            var store = new IndependentProjectStateStore(root);
            store.Update(0, state => { state.Maintenance = false; return true; });
            var old = Identity(); old.Pid += 100000; // an identity fixture, never sent to OS termination
            store.ArmManualRun(1, Intent(root), old, now);
            return store;
        }
        private static IndependentLaunchTicket Ready(IndependentProjectStateStore store, long now, bool dispatch = true)
        {
            var tx = store.BeginRecovery(store.Read().Revision, "executor", now);
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
            {
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId,
                    phase, true, now, 30000, "SimulatedStageConfirmed");
            }
            var ticket = store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash,
                Process.GetCurrentProcess().SessionId, now);
            if (dispatch) store.MarkLaunchDispatched(store.Read().Revision, ticket.Nonce, "executor", now);
            return store.Read().Ticket;
        }

        internal static int ConsumeChild(string root, string nonce, long revision)
        {
            try
            {
                new IndependentProjectStateStore(root).ConsumeLaunchTicket(revision, nonce, Identity(), Hash, DateTime.UtcNow.Ticks);
                return 0;
            }
            catch (InvalidOperationException) { return 23; }
        }

        internal static int RunAll(bool cooperationOnly = false)
        {
            _count = 0;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                Path.GetTempPath(), "independent-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            if (!cooperationOnly)
            using (var currentIdentity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                if (!currentIdentity.IsSystem)
                {
                    var accessDenied = false;
                    try { using (var operation = new IndependentSafetyWorkerOperation(Exe, Hash, Path.Combine(root, "missing.json"))) { } }
                    catch (UnauthorizedAccessException) { accessDenied = true; }
                    Assert(accessDenied, "non-SYSTEM operation invoked a hardware worker");
                    accessDenied = false;
                    try { using (var runner = new IndependentProjectSafetyRunner(Path.Combine(root, "missing.json"), "executor")) { } }
                    catch (UnauthorizedAccessException) { accessDenied = true; }
                    Assert(accessDenied, "non-SYSTEM runner accessed installed hardware configuration");
                    accessDenied = false;
                    try { IndependentInteractiveLauncher.Dispatch(Path.Combine(root, "missing.json"), Guid.NewGuid().ToString("N")); }
                    catch (UnauthorizedAccessException) { accessDenied = true; }
                    Assert(accessDenied, "non-SYSTEM launcher reached task scheduler");
                    accessDenied = false;
                    try { using (var operation = new IndependentLaunchWorkerOperation(Path.Combine(root, "missing.json"), Guid.NewGuid().ToString("N"))) { } }
                    catch (UnauthorizedAccessException) { accessDenied = true; }
                    Assert(accessDenied, "non-SYSTEM launch operation created a worker");
                }
            }
            var now = DateTime.UtcNow.Ticks;
            if (!cooperationOnly)
            {
                var installStore = Fixture(Path.Combine(root, "installation-maintenance"), now);
                var installState = installStore.Read();
                Reject(() => installStore.SetInstallationMaintenance(installState.Revision, true), "installation admitted armed run");
                installStore.SetControllerManualPause(installState.Controller, installState.Intent.RunId,
                    installState.Intent.RunEpoch, true, Guid.NewGuid().ToString("N"), now);
                Reject(() => installStore.SetInstallationMaintenance(installStore.Read().Revision, true), "installation treated pause as stop");
                installStore.RecordControllerManualStop(installState.Controller, installState.Intent.RunId,
                    installState.Intent.RunEpoch, Guid.NewGuid().ToString("N"), now);
                installStore.SetInstallationMaintenance(installStore.Read().Revision, true);
                Assert(installStore.Read().Maintenance && installStore.Read().Intent.ManualStopped,
                    "maintenance lost manual stop");
                installStore.SetInstallationMaintenance(installStore.Read().Revision, false);
                Assert(!installStore.Read().Maintenance && !installStore.Read().Intent.Armed,
                    "leaving maintenance armed the experiment");
                var cleanupStore = Fixture(Path.Combine(root, "installation-cleanup"), now);
                cleanupStore.BeginRecovery(cleanupStore.Read().Revision, "executor", now);
                Reject(() => cleanupStore.SetInstallationMaintenance(cleanupStore.Read().Revision, true),
                    "installation erased active cleanup");
            }
            var cooperationStore = Fixture(Path.Combine(root, "cooperation"), now);
            var cooperationState = cooperationStore.Read();
            var cooperation = cooperationStore.BeginRecovery(cooperationState.Revision, "executor", now);
            Reject(() => cooperationStore.AcknowledgeCooperativeStop(Identity(), cooperation.RunId,
                cooperation.RunEpoch, cooperation.RequestId, cooperation.Generation, now), "foreign cooperation accepted");
            Reject(() => cooperationStore.AcknowledgeCooperativeStop(cooperationState.Controller, cooperation.RunId,
                cooperation.RunEpoch + 1, cooperation.RequestId, cooperation.Generation, now), "old run cooperation accepted");
            Reject(() => cooperationStore.AcknowledgeCooperativeStop(cooperationState.Controller, cooperation.RunId,
                cooperation.RunEpoch, cooperation.RequestId, cooperation.Generation, cooperation.PhaseDeadlineUtcTicks), "late cooperation accepted");
            cooperationStore.AcknowledgeCooperativeStop(cooperationState.Controller, cooperation.RunId,
                cooperation.RunEpoch, cooperation.RequestId, cooperation.Generation, now);
            var acknowledged = cooperationStore.Read();
            Assert(acknowledged.CooperativeStopReceipt.RequestId == cooperation.RequestId &&
                acknowledged.SafetyCleanupPending && acknowledged.Transaction.Phase == IndependentRecoveryPhase.CooperativeStop &&
                acknowledged.Ticket == null, "cooperation skipped independent safety");
            cooperationStore.AcknowledgeCooperativeStop(cooperationState.Controller, cooperation.RunId,
                cooperation.RunEpoch, cooperation.RequestId, cooperation.Generation, now);
            Assert(cooperationStore.Read().Revision == acknowledged.Revision, "duplicate cooperation changed revision");
            cooperationStore.CompleteSafetyStage(acknowledged.Revision, "executor", cooperation.Generation,
                cooperation.RequestId, IndependentRecoveryPhase.CooperativeStop, true, now, 30000, "CooperationOnly");
            Reject(() => cooperationStore.AcknowledgeCooperativeStop(cooperationState.Controller, cooperation.RunId,
                cooperation.RunEpoch, cooperation.RequestId, cooperation.Generation, now), "receipt accepted after stage advanced");
            if (cooperationOnly)
            {
                Console.WriteLine($"PASS independent cooperation {_count}/{_count}");
                return _count;
            }
            var selectionStore = Fixture(Path.Combine(root, "controller-selection"), now);
            var selectionState = selectionStore.Read();
            var selectionCommand = Guid.NewGuid().ToString("N");
            selectionStore.SetControllerSelection(selectionState.Controller, selectionState.Intent.RunId,
                selectionState.Intent.RunEpoch, 4, false, selectionCommand, now);
            Assert(!selectionStore.Read().Intent.SelectedChannels.Contains(4) &&
                !selectionStore.Read().Intent.RecoveryChannels().Contains(4), "unchecked lane retained restart permission");
            var selectionRevision = selectionStore.Read().Revision;
            selectionStore.SetControllerSelection(selectionState.Controller, selectionState.Intent.RunId,
                selectionState.Intent.RunEpoch, 4, false, selectionCommand, now);
            Assert(selectionStore.Read().Revision == selectionRevision, "duplicate selection changed authority");
            selectionStore.SetControllerSelection(selectionState.Controller, selectionState.Intent.RunId,
                selectionState.Intent.RunEpoch, 4, true, Guid.NewGuid().ToString("N"), now);
            Assert(selectionStore.Read().Intent.SelectedChannels.Contains(4) &&
                !selectionStore.Read().Intent.RecoveryChannels().Contains(4) &&
                selectionStore.Read().Intent.RecoveryChannels().Length == 5, "checking box silently restarted a stopped lane");
            Reject(() => selectionStore.SetControllerSelection(Identity(), selectionState.Intent.RunId,
                selectionState.Intent.RunEpoch, 5, false, Guid.NewGuid().ToString("N"), now), "foreign process changed selection");
            var pauseStore = Fixture(Path.Combine(root, "durable-controller-pause"), now);
            var pauseState = pauseStore.Read();
            var pauseCommand = Guid.NewGuid().ToString("N");
            pauseStore.SetControllerManualPause(pauseState.Controller, pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                true, pauseCommand, now + 1);
            var pausedState = pauseStore.Read();
            Assert(pausedState.Intent.RecoveryChannels().Length == 0 && pausedState.Intent.Armed,
                "pause retained recovery targets or lost explicit resume intent");
            pauseStore.SetControllerManualPause(pauseState.Controller, pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                true, pauseCommand, now + 2);
            Assert(pauseStore.Read().Revision == pausedState.Revision, "duplicate pause was not idempotent");
            Reject(() => pauseStore.SetControllerManualPause(Identity(), pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                false, pauseCommand, now + 3), "foreign process resumed the paused run");
            pauseStore.SetControllerManualPause(pauseState.Controller, pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                false, Guid.NewGuid().ToString("N"), now + 4);
            Assert(pauseStore.Read().Intent.RecoveryChannels().Length == 6 && pauseStore.Read().RunStartedUtcTicks == now + 4,
                "explicit continue failed to restore authorized targets and startup window");
            pauseStore.RecordControllerManualStop(pauseState.Controller, pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                Guid.NewGuid().ToString("N"), now + 5);
            Reject(() => pauseStore.SetControllerManualPause(pauseState.Controller, pauseState.Intent.RunId, pauseState.Intent.RunEpoch,
                false, Guid.NewGuid().ToString("N"), now + 6), "continue undid a whole-run manual stop");
            var startupStore = Fixture(Path.Combine(root, "durable-startup-deadline"), now);
            var startup = startupStore.Read();
            Assert(startup.SchemaVersion == 2 && startup.RunStartedUtcTicks == now &&
                startup.StartupDeadlineUtcTicks == now + TimeSpan.FromMilliseconds(startup.Intent.StartupBudgetMs).Ticks &&
                startup.RootRunId == startup.Intent.RunId, "initial run deadline/root not durable");
            Reject(() => startupStore.ArmManualRun(startup.Revision, Intent(Path.Combine(root, "durable-startup-deadline")),
                Identity(), now + 1), "repeat start replaced an armed batch");
            Assert(startupStore.Read().Revision == startup.Revision && startupStore.Read().RunStartedUtcTicks == now,
                "rejected start changed startup deadline");
            var startupTicket = Ready(startupStore, now);
            var startupConsumer = Identity();
            startupStore.ConsumeLaunchTicket(startupStore.Read().Revision, startupTicket.Nonce, startupConsumer, Hash, now);
            startupStore.CommitReplacementRun(startupStore.Read().Revision, startupConsumer, Guid.NewGuid().ToString("N"), 2, now + 5);
            Assert(startupStore.Read().RunStartedUtcTicks == now + 5 && startupStore.Read().RootRunId == startup.RootRunId,
                "replacement reset root or inherited old startup deadline");
            var dispatchStore = Fixture(Path.Combine(root, "dispatch-once"), now);
            var unlaunched = Ready(dispatchStore, now, false);
            Reject(() => dispatchStore.ConsumeLaunchTicket(dispatchStore.Read().Revision, unlaunched.Nonce, Identity(), Hash, now),
                "undispatched ticket was consumed");
            dispatchStore.MarkLaunchDispatched(dispatchStore.Read().Revision, unlaunched.Nonce, "executor", now);
            Reject(() => dispatchStore.MarkLaunchDispatched(dispatchStore.Read().Revision, unlaunched.Nonce, "executor", now),
                "crashed launcher dispatched the same nonce twice");
            Assert(dispatchStore.Read().Ticket.DispatchStartedUtcTicks == now && dispatchStore.Read().Ticket.Consumer == null,
                "dispatch reported main process consumption");
            var dispatched = dispatchStore.Read();
            Assert(IndependentLaunchObservation.Evaluate(dispatched, dispatched.Transaction.RequestId,
                dispatched.Transaction.Generation, unlaunched.Nonce, now) == IndependentOperationResult.Pending,
                "dispatch without consumption completed launch phase");
            Assert(IndependentLaunchObservation.Evaluate(dispatched, dispatched.Transaction.RequestId,
                dispatched.Transaction.Generation, Guid.NewGuid().ToString("N"), now) == IndependentOperationResult.Failed,
                "different ticket was observed as current launch");
            Assert(IndependentLaunchObservation.Evaluate(dispatched, dispatched.Transaction.RequestId,
                dispatched.Transaction.Generation, unlaunched.Nonce, dispatched.Transaction.PhaseDeadlineUtcTicks) == IndependentOperationResult.Failed,
                "launch observation ignored deadline");
            dispatchStore.ConsumeLaunchTicket(dispatched.Revision, unlaunched.Nonce, Identity(), Hash, now);
            Assert(IndependentLaunchObservation.Evaluate(dispatchStore.Read(), dispatched.Transaction.RequestId,
                dispatched.Transaction.Generation, unlaunched.Nonce, now) == IndependentOperationResult.Completed,
                "durable consumption not accepted as launch completion");
            dispatchStore.UpdateOperatorIntent(dispatchStore.Read().Revision, "OperatorStop", "stop", now,
                intent => intent.ManualStopped = true);
            Assert(IndependentLaunchObservation.Evaluate(dispatchStore.Read(), dispatched.Transaction.RequestId,
                dispatched.Transaction.Generation, unlaunched.Nonce, now) == IndependentOperationResult.Failed,
                "consumption concealed later operator stop");
            var manualStore = Fixture(Path.Combine(root, "controller-manual-stop"), now);
            var manualState = manualStore.Read();
            var manualCommand = Guid.NewGuid().ToString("N");
            Reject(() => manualStore.RecordControllerManualStop(Identity(), manualState.Intent.RunId,
                manualState.Intent.RunEpoch, manualCommand, now), "unbound process revoked current run");
            var manualTicket = Ready(manualStore, now);
            manualStore.RecordControllerManualStop(manualState.Controller, manualState.Intent.RunId,
                manualState.Intent.RunEpoch, manualCommand, now);
            var manualStopped = manualStore.Read();
            Assert(manualStopped.Intent.ManualStopped && !manualStopped.Intent.Armed && manualStopped.Ticket.Revoked,
                "controller stop failed to revoke pending ticket");
            manualStore.RecordControllerManualStop(manualState.Controller, manualState.Intent.RunId,
                manualState.Intent.RunEpoch, manualCommand, now);
            Assert(manualStore.Read().Revision == manualStopped.Revision, "duplicate controller stop was not idempotent");
            Reject(() => manualStore.ConsumeLaunchTicket(manualStopped.Revision, manualTicket.Nonce, Identity(), Hash, now),
                "controller stop left ticket consumable");
            var retryStore = Fixture(Path.Combine(root, "cleanup-retry"), now);
            var retryTx = retryStore.BeginRecovery(retryStore.Read().Revision, "executor", now);
            void FailPower(IndependentProjectStateStore target, IndependentRecoveryTransaction attempt, long time)
            {
                target.CompleteSafetyStage(target.Read().Revision, "executor", attempt.Generation, attempt.RequestId,
                    IndependentRecoveryPhase.CooperativeStop, true, time, 30000, "fixture");
                target.CompleteSafetyStage(target.Read().Revision, "executor", attempt.Generation, attempt.RequestId,
                    IndependentRecoveryPhase.PowerOff, false, time, 30000, "InjectedPowerFailure");
            }
            FailPower(retryStore, retryTx, now);
            Reject(() => retryStore.RetrySafetyCleanup(retryStore.Read().Revision, "executor", now + TimeSpan.FromSeconds(59).Ticks),
                "cleanup retry bypassed minimum interval");
            Reject(() => retryStore.RetrySafetyCleanup(retryStore.Read().Revision, "other", now + TimeSpan.FromSeconds(60).Ticks),
                "foreign executor stole failed cleanup");
            var firstRequest = retryTx.RequestId;
            var firstGeneration = retryTx.Generation;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var time = now + TimeSpan.FromSeconds(60 * attempt).Ticks;
                retryTx = retryStore.RetrySafetyCleanup(retryStore.Read().Revision, "executor", time);
                Assert(retryStore.Read().SafetyCleanupPending && retryTx.Generation == firstGeneration + attempt &&
                    retryTx.AttemptsUtcTicks.Length == attempt + 1, "cleanup retry lost obligation or retry history");
                FailPower(retryStore, retryTx, time);
            }
            Reject(() => retryStore.RetrySafetyCleanup(retryStore.Read().Revision, "executor", now + TimeSpan.FromMinutes(3).Ticks),
                "fourth project cleanup attempt admitted");
            new IndependentProjectRecoveryPump(retryStore, new PumpOperations(), "executor", 30000, 30000)
                .Tick(now + TimeSpan.FromMinutes(40).Ticks);
            Assert(retryStore.Read().Transaction.RequestId == retryTx.RequestId &&
                retryStore.Read().Transaction.Phase == IndependentRecoveryPhase.NeedsAttention,
                "pump automatically cleared exhausted cleanup after cooling window");
            Reject(() => retryStore.CompleteSafetyStage(retryStore.Read().Revision, "executor", firstGeneration, firstRequest,
                IndependentRecoveryPhase.PowerOff, true, now + TimeSpan.FromMinutes(3).Ticks, 30000, "LateReceipt"),
                "old cleanup receipt advanced replacement attempt");
            retryStore = Fixture(Path.Combine(root, "cleanup-retry-stopped"), now);
            retryTx = retryStore.BeginRecovery(retryStore.Read().Revision, "executor", now);
            FailPower(retryStore, retryTx, now);
            retryStore.UpdateOperatorIntent(retryStore.Read().Revision, "OperatorStop", "manual stop", now,
                intent => { intent.ManualStopped = true; intent.Armed = false; });
            var retryTime = now + TimeSpan.FromMinutes(1).Ticks;
            retryTx = retryStore.RetrySafetyCleanup(retryStore.Read().Revision, "executor", retryTime);
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                retryStore.CompleteSafetyStage(retryStore.Read().Revision, "executor", retryTx.Generation, retryTx.RequestId,
                    phase, true, retryTime, 30000, "CleanupConfirmed");
            Assert(!retryStore.Read().SafetyCleanupPending && retryStore.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled &&
                retryStore.Read().Intent.ManualStopped && !retryStore.Read().Intent.Armed && retryStore.Read().Ticket == null,
                "manual stop cleanup retry authorized restart");
            var autoStore = Fixture(Path.Combine(root, "cleanup-retry-pump"), now);
            var autoTx = autoStore.BeginRecovery(autoStore.Read().Revision, "executor", now);
            FailPower(autoStore, autoTx, now);
            var retryPump = new IndependentProjectRecoveryPump(autoStore, new PumpOperations(), "executor", 30000, 30000);
            retryPump.Tick(now + TimeSpan.FromSeconds(59).Ticks);
            Assert(autoStore.Read().Transaction.RequestId == autoTx.RequestId, "pump retried before minimum interval");
            retryPump.Tick(now + TimeSpan.FromSeconds(60).Ticks);
            Assert(autoStore.Read().Transaction.Generation == autoTx.Generation + 1 &&
                autoStore.Read().Transaction.Phase == IndependentRecoveryPhase.CooperativeStop,
                "pump left failed cleanup permanently stranded");
            var store = Fixture(Path.Combine(root, "revocation"), now);
            var tx = store.BeginRecovery(store.Read().Revision, "executor", now);
            Assert(store.Read().SafetyCleanupPending, "begin lost safety cleanup obligation");
            Reject(() => store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash, 1, now),
                "issued ticket before safety stages");
            store.UpdateOperatorIntent(store.Read().Revision, "OperatorStop", "manual", now,
                intent => { intent.ManualStopped = true; intent.Armed = false; });
            var stopped = store.Read();
            Assert(stopped.SafetyCleanupPending && stopped.Intent.RecoveryChannels().Length == 0,
                "operator stop discarded safety cleanup or retained restart authority");
            Reject(() => store.ArmManualRun(stopped.Revision, Intent(root), Identity(), now), "started while cleanup pending");
            Reject(() => store.CompleteSafetyStage(stopped.Revision, "other", tx.Generation, tx.RequestId,
                tx.Phase, true, now, 30000, "wrong executor"), "foreign cleanup receipt admitted");
            foreach (var stage in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId,
                    stage, true, now, 30000, "SimulatedCleanupAfterManualStop");
            Assert(!store.Read().SafetyCleanupPending && store.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled,
                "revocation prevented safety cleanup or authorized restart");

            var lateStore = Fixture(Path.Combine(root, "late-safety"), now);
            var lateTx = lateStore.BeginRecovery(lateStore.Read().Revision, "executor", now);
            lateStore.CompleteSafetyStage(lateStore.Read().Revision, "executor", lateTx.Generation, lateTx.RequestId,
                IndependentRecoveryPhase.CooperativeStop, false, now + TimeSpan.FromSeconds(25).Ticks, 1000, "CooperativeTimeout");
            Assert(lateStore.Read().Transaction.Phase == IndependentRecoveryPhase.PowerOff,
                "cooperative timeout did not escalate independently");
            lateStore.CompleteSafetyStage(lateStore.Read().Revision, "executor", lateTx.Generation, lateTx.RequestId,
                IndependentRecoveryPhase.PowerOff, true, now + TimeSpan.FromSeconds(27).Ticks, 30000, "LateReceipt");
            Assert(lateStore.Read().SafetyCleanupPending &&
                lateStore.Read().Transaction.Phase == IndependentRecoveryPhase.NeedsAttention,
                "late safety success cleared cleanup obligation");

            store = Fixture(Path.Combine(root, "ticket"), now);
            var ticket = Ready(store, now);
            var revision = store.Read().Revision;
            var sessionZero = Identity(); sessionZero.WindowsSessionId = 0;
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, sessionZero, Hash, now),
                "service session consumed interactive startup ticket");
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), new string('b', 64), now),
                "wrong executable consumed ticket");
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), Hash,
                now + TimeSpan.FromMinutes(1).Ticks), "expired ticket consumed");
            var consumer = Identity();
            store.ConsumeLaunchTicket(revision, ticket.Nonce, consumer, Hash, now);
            var recovered = store.Read();
            Assert(recovered.Ticket.Consumer.Matches(consumer) && recovered.Transaction.ReplacementPid == consumer.Pid,
                "ticket consumption and PID binding not atomic");
            Reject(() => store.ConsumeLaunchTicket(recovered.Revision, ticket.Nonce, Identity(), Hash, now), "ticket replay admitted");
            var run = Guid.NewGuid().ToString("N");
            store.CommitReplacementRun(recovered.Revision, consumer, run, 2, now);
            recovered = store.Read();
            Assert(recovered.Intent.RunId == run && recovered.Transaction.RunId == run &&
                recovered.Intent.Revision == recovered.Transaction.IntentRevision && recovered.Controller.Matches(consumer),
                "replacement left mixed run identities");
            var staleController = Identity(); staleController.Pid += 100000;
            Reject(() => store.RecordControllerManualStop(staleController, run, 2, Guid.NewGuid().ToString("N"), now),
                "old controller revoked replacement run");
            Reject(() => store.RecordControllerManualStop(consumer, Guid.NewGuid().ToString("N"), 2, Guid.NewGuid().ToString("N"), now),
                "old run stop revoked current batch");
            Reject(() => store.CommitReplacementRun(recovered.Revision, consumer, Guid.NewGuid().ToString("N"), 3, now),
                "bootstrap committed a second run using old ticket");

            store = Fixture(Path.Combine(root, "stop-race"), now);
            ticket = Ready(store, now); revision = store.Read().Revision;
            store.UpdateOperatorIntent(revision, "OperatorSelection", "exclude", now,
                intent => intent.SelectedChannels = new[] { 7, 8, 9 });
            Reject(() => store.ConsumeLaunchTicket(revision, ticket.Nonce, Identity(), Hash, now), "stale writer restored selection");
            Reject(() => store.ConsumeLaunchTicket(store.Read().Revision, ticket.Nonce, Identity(), Hash, now), "revoked ticket accepted");
            Assert(store.Read().Intent.RecoveryChannels().SequenceEqual(new[] { 7, 8, 9 }), "excluded channels returned");
            var selectionAudit = store.Read().Audit.Last();
            Assert(selectionAudit.Before.SequenceEqual(new[] { 4, 5, 7, 8, 9, 12 }) &&
                selectionAudit.After.SequenceEqual(new[] { 7, 8, 9 }), "selection audit lost before/after values");

            store = Fixture(Path.Combine(root, "after-consume-stop"), now);
            ticket = Ready(store, now); consumer = Identity();
            store.ConsumeLaunchTicket(store.Read().Revision, ticket.Nonce, consumer, Hash, now);
            store.UpdateOperatorIntent(store.Read().Revision, "OperatorStop", "stop after consume", now,
                intent => intent.ManualStopped = true);
            Reject(() => store.CommitReplacementRun(store.Read().Revision, consumer, Guid.NewGuid().ToString("N"), 2, now),
                "stop between ticket consumption and startup was ignored");

            store = Fixture(Path.Combine(root, "isolation"), now);
            store.UpdateOperatorIntent(store.Read().Revision, "PermanentFault", "isolated", now,
                intent => intent.PermanentChannels = new[] { 4 });
            store.UpdateOperatorIntent(store.Read().Revision, "OperatorStop", "stop before explicit new run", now,
                intent => { intent.ManualStopped = true; intent.Armed = false; });
            store.ArmManualRun(store.Read().Revision, Intent(Path.Combine(root, "isolation")), Identity(), now);
            Assert(!store.Read().Intent.RecoveryChannels().Contains(4), "ordinary restart cleared permanent isolation");
            for (var i = 0; i < 80; i++)
                store.UpdateOperatorIntent(store.Read().Revision, "OperatorPause", new string('中', 512), now,
                    intent => intent.ManualPaused = !intent.ManualPaused);
            Assert(store.Read().Audit.Length == 32 && new FileInfo(Path.Combine(root, "isolation", "independent-project-state.json")).Length < 65536,
                "audit overflow blocked future stop transactions");

            var raceRoot = Path.Combine(root, "process-race");
            store = Fixture(raceRoot, DateTime.UtcNow.Ticks); ticket = Ready(store, DateTime.UtcNow.Ticks);
            revision = store.Read().Revision;
            var args = "--state-consume \"" + raceRoot + "\" " + ticket.Nonce + " " + revision;
            using (var first = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            using (var second = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    if (!first.WaitForExit(10000) || !second.WaitForExit(10000)) throw new Exception("consumer deadline exceeded");
                    Assert(new[] { first.ExitCode, second.ExitCode }.OrderBy(c => c).SequenceEqual(new[] { 0, 23 }),
                        "two processes consumed one permission");
                    var onDisk = new IndependentProjectStateStore(raceRoot).Read();
                    Assert(onDisk.Ticket.Consumer.Pid == (first.ExitCode == 0 ? first.Id : second.Id), "winner binding not durable");
                    Reject(() => store.ConsumeLaunchTicket(onDisk.Revision, ticket.Nonce, Identity(), Hash, DateTime.UtcNow.Ticks),
                        "dead consumer automatically resurrected its spent permission");
                }
                finally
                {
                    if (!first.HasExited) { first.Kill(); first.WaitForExit(3000); }
                    if (!second.HasExited) { second.Kill(); second.WaitForExit(3000); }
                }
            }
            var corrupt = Path.Combine(raceRoot, "independent-project-state.json");
            var text = File.ReadAllText(corrupt);
            File.WriteAllText(corrupt, text.Replace("\"SafetyCleanupPending\":false,", ""));
            var rejected = false;
            try { store.Read(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "missing safety flag silently defaulted to false");
            var safetyIntent = Intent(root);
            var safetyTx = IndependentRecoveryTransitions.Begin(null, safetyIntent, "executor", now);
            IndependentRecoveryTransitions.Advance(safetyTx, safetyIntent, "executor", safetyTx.Generation,
                IndependentRecoveryPhase.PowerOff, now, 30000, "fixture");
            var command = new IndependentSafetyWorkerCommand
            {
                StageNonce = Guid.NewGuid().ToString("N"), Transaction = safetyTx,
                IssuedUtcTicks = now, DeadlineUtcTicks = now + TimeSpan.FromSeconds(30).Ticks,
                ConfigDirectory = root,
                Files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }
                    .Select(n => new IndependentSafetyConfigFile { Name = n, Sha256 = Hash }).ToArray(),
                Runtime = new SafetyRuntimeSnapshot { SampleRateHz = 2000, SamplesPerChannel = 20,
                    PressureChannels = new[] { "Pressure_1" }, ReleaseSafePressureBar = new[] { 1d },
                    PressureSampleMaxAgeMs = 100, ReleaseStableMs = 300, ReleaseTimeoutMs = 5000 }
            };
            command.Validate(now);
            var registration = new IndependentExecutorRegistration
            {
                InstallationId = Guid.NewGuid().ToString("N"), ProjectDirectory = root,
                InteractiveUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value,
                DatabasePath = safetyIntent.DatabasePath, DatabaseCreationUtcTicks = 1,
                StateDirectory = root, ExecutablePath = Exe, ExecutableSha256 = Hash,
                SafetyExecutablePath = Exe, SafetyExecutableSha256 = Hash,
                ConfigurationSha256 = Hash, ConfigDirectory = root,
                Files = command.Files, Runtime = command.Runtime
            };
            registration.RequireBoundIntent(safetyIntent);
            var registrationPath = Path.Combine(root, "executor.json");
            var launchTask = new IndependentLaunchTaskDefinition
            {
                Path = registration.LaunchTaskName, Executable = registration.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(registration.ExecutablePath),
                Arguments = IndependentLaunchTaskDefinition.ExpectedArguments(registrationPath),
                UserSid = registration.InteractiveUserSid, Enabled = true, AllowDemandStart = true,
                ActionCount = 1, RunLevel = 1, LogonType = 3, MultipleInstances = 2, ExecutionTimeLimit = "PT0S",
                SecurityDescriptor = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)"
            };
            launchTask.Validate(registration, registrationPath);
            IndependentLaunchTaskDefinition.ValidateSecurityDescriptor("O:SYG:SYD:(A;;FA;;;SY)(A;OICIIO;GA;;;CO)");
            var taskRootDescriptor = "O:SYG:SYD:PAI(A;CI;FA;;;BA)(A;OI;0x1f019f;;;BA)(A;CI;FA;;;SY)(A;OI;0x1f019f;;;SY)(A;CI;FW;;;AU)(A;CI;FW;;;NS)(A;CI;FW;;;LS)(A;OICIIO;FA;;;CO)";
            IndependentLaunchTaskDefinition.ValidateAncestorSecurityDescriptor(taskRootDescriptor);
            Assert(true, "standard task root should permit isolated protected descendants");
            foreach (var descriptor in new[] { taskRootDescriptor,
                "O:SYG:SYD:(A;;FA;;;SY)(A;;0x40;;;AU)",
                "O:SYG:SYD:(A;;FA;;;SY)(A;;WD;;;AU)" })
            {
                var rejectedAcl = false;
                try
                {
                    if (descriptor == taskRootDescriptor) IndependentLaunchTaskDefinition.ValidateSecurityDescriptor(descriptor);
                    else IndependentLaunchTaskDefinition.ValidateAncestorSecurityDescriptor(descriptor);
                }
                catch (UnauthorizedAccessException) { rejectedAcl = true; }
                Assert(rejectedAcl, "task ACL allowed modification/deletion of protected descendants");
            }
            Assert(true, "inherit-only parent ACE should not grant parent mutation");
            launchTask.Enabled = false;
            var disabledRejected = false;
            try { launchTask.Validate(registration, registrationPath); } catch (InvalidDataException) { disabledRejected = true; }
            Assert(disabledRejected, "disabled task accepted despite possible successful RunEx return");
            launchTask.Enabled = true;
            launchTask.UserSid = "S-1-5-18";
            var systemRejected = false;
            try { launchTask.Validate(registration, registrationPath); } catch (InvalidDataException) { systemRejected = true; }
            Assert(systemRejected, "SYSTEM main program principal accepted");
            launchTask.UserSid = registration.InteractiveUserSid;
            launchTask.SecurityDescriptor = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BU)";
            var taskAclRejected = false;
            try { launchTask.Validate(registration, registrationPath); } catch (UnauthorizedAccessException) { taskAclRejected = true; }
            Assert(taskAclRejected, "user-writable highest task accepted");
            launchTask.SecurityDescriptor = "O:SYG:SYD:(A;;FA;;;SY)(A;;FA;;;BA)";
            launchTask.Arguments = "--independent-ticket $(Arg0)";
            var redirectedTask = false;
            try { launchTask.Validate(registration, registrationPath); } catch (InvalidDataException) { redirectedTask = true; }
            Assert(redirectedTask, "task without registered project binding accepted");
            Assert(registration.LaunchTaskName.EndsWith(registration.InstallationId), "task identity not installation-bound");
            safetyIntent.DatabaseCreationUtcTicks++;
            var wrongDatabase = false;
            try { registration.RequireBoundIntent(safetyIntent); } catch (InvalidDataException) { wrongDatabase = true; }
            Assert(wrongDatabase, "replacement database accepted by registration");
            safetyIntent.DatabaseCreationUtcTicks--;
            safetyIntent.ExecutablePath = Path.Combine(root, "other.exe");
            var wrongExecutable = false;
            try { registration.RequireBoundIntent(safetyIntent); } catch (InvalidDataException) { wrongExecutable = true; }
            Assert(wrongExecutable, "mutable intent redirected elevated executable");
            safetyIntent.ExecutablePath = Exe;
            registration.DatabasePath = Path.Combine(root + "-other", "index.db");
            var siblingProject = false;
            try { registration.Validate(); } catch (InvalidDataException) { siblingProject = true; }
            Assert(siblingProject, "project prefix collision accepted");
            registration.DatabasePath = safetyIntent.DatabasePath;
            registration.ConfigDirectory = Path.Combine(root, "..", "redirected");
            var traversal = false;
            try { registration.Validate(); } catch (InvalidDataException) { traversal = true; }
            Assert(traversal, "noncanonical config path accepted");
            registration.ConfigDirectory = root;
            registration.ConfigurationSha256 = new string('b', 64);
            var changedConfig = false;
            try { registration.RequireBoundIntent(safetyIntent); } catch (InvalidDataException) { changedConfig = true; }
            Assert(changedConfig, "changed project configuration accepted");
            registration.ConfigurationSha256 = Hash;
            var cleanupState = new IndependentProjectState
            { Revision = 1, Intent = safetyIntent, Transaction = safetyTx, SafetyCleanupPending = true,
                RootRunId = safetyIntent.RunId, RunStartedUtcTicks = now,
                StartupDeadlineUtcTicks = now + TimeSpan.FromMilliseconds(safetyIntent.StartupBudgetMs).Ticks };
            safetyIntent.ManualStopped = true;
            var cleanupCommand = registration.CreateSafetyCommand(cleanupState, "executor", now);
            Assert(cleanupCommand.ConfigDirectory == registration.ConfigDirectory && cleanupCommand.StageNonce != command.StageNonce,
                "manual revocation blocked cleanup or reused stage nonce");
            Reject(() => registration.CreateSafetyCommand(cleanupState, "other", now), "foreign executor created hardware command");
            cleanupState.SafetyCleanupPending = false;
            Reject(() => registration.CreateSafetyCommand(cleanupState, "executor", now), "completed cleanup created hardware command");
            safetyIntent.ManualStopped = false;
            var receipt = new IndependentSafetyWorkerReceipt
            {
                StageNonce = command.StageNonce, CommandSha256 = Hash, RequestId = safetyTx.RequestId,
                Generation = safetyTx.Generation, Phase = safetyTx.Phase, Confirmed = true,
                WorkerPid = 123, WorkerStartUtcTicks = now, CompletedUtcTicks = now + 1
            };
            Assert(receipt.MatchesCurrent(command, Hash, 123, now, now + 2), "matching safety worker receipt rejected");
            Assert(!receipt.MatchesCurrent(command, Hash, 124, now, now + 2), "wrong worker accepted");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now - 1, now + 2), "reused PID accepted");
            Assert(!receipt.MatchesCurrent(command, new string('b', 64), 123, now, now + 2), "wrong sealed command accepted");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now, command.DeadlineUtcTicks), "late receipt accepted");
            receipt.StageNonce = Guid.NewGuid().ToString("N");
            Assert(!receipt.MatchesCurrent(command, Hash, 123, now, now + 2), "previous stage nonce accepted");
            command.Files[0].Name = "..\\AIConfig.xml";
            var invalidManifest = false;
            try { command.Validate(now); } catch (InvalidDataException) { invalidManifest = true; }
            Assert(invalidManifest, "config traversal accepted");
            var pumpStore = Fixture(Path.Combine(root, "pump-stop"), now);
            pumpStore.BeginRecovery(pumpStore.Read().Revision, "executor", now);
            var operations = new PumpOperations();
            var pump = new IndependentProjectRecoveryPump(pumpStore, operations, "executor", 30000, 30000);
            var pumpNow = now + TimeSpan.FromSeconds(25).Ticks;
            pump.Tick(pumpNow);
            Assert(pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.PowerOff && operations.CancelWorkers > 0,
                "pump did not independently expire collaborative stop");
            pump.Tick(pumpNow + 1);
            pumpStore.UpdateOperatorIntent(pumpStore.Read().Revision, "OperatorStop", "stop during cleanup", pumpNow + 2,
                intent => intent.ManualStopped = true);
            pump.Tick(pumpNow + 3); pump.Tick(pumpNow + 4);
            Assert(pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled &&
                !pumpStore.Read().SafetyCleanupPending && operations.Launches == 0,
                "pump stopped cleanup or launched after operator revocation");
            pumpStore = Fixture(Path.Combine(root, "pump-launch"), now);
            ticket = Ready(pumpStore, now);
            operations = new PumpOperations();
            pump = new IndependentProjectRecoveryPump(pumpStore, operations, "executor", 30000, 30000);
            Reject(() => pump.Tick(now), "pump trusted nondurable launcher PID");
            consumer = Identity();
            pumpStore.ConsumeLaunchTicket(pumpStore.Read().Revision, ticket.Nonce, consumer, Hash, now);
            pump.Tick(now + 1);
            pump.Tick(now + 2);
            Assert(pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.Verifying && operations.Verifications == 0,
                "process alive verified before new run binding");
            pumpStore.CommitReplacementRun(pumpStore.Read().Revision, consumer, Guid.NewGuid().ToString("N"), 2, now + 3);
            pump.Tick(now + 4);
            Assert(pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.Verified && operations.Verifications == 1,
                "verified replacement could not complete transaction");
            var verifiedRun = pumpStore.Read();
            pumpStore.SetControllerManualPause(consumer, verifiedRun.Intent.RunId, verifiedRun.Intent.RunEpoch,
                true, Guid.NewGuid().ToString("N"), now + 5);
            Assert(pumpStore.Read().Intent.RecoveryChannels().Length == 0 && !pumpStore.Read().Ticket.Revoked &&
                pumpStore.Read().Transaction.IntentRevision == pumpStore.Read().Intent.Revision,
                "pause broke verified binding or retained restart targets");
            pumpStore.SetControllerManualPause(consumer, verifiedRun.Intent.RunId, verifiedRun.Intent.RunEpoch,
                false, Guid.NewGuid().ToString("N"), now + 6);
            Assert(pumpStore.Read().Intent.RecoveryChannels().Length == 6 &&
                pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.Verified &&
                pumpStore.Read().Controller.Matches(consumer), "continue replaced verified process or failed to restore targets");
            var abandonedStore = Fixture(Path.Combine(root, "consumed-before-bootstrap-stop"), now);
            var abandonedTicket = Ready(abandonedStore, now);
            var abandonedConsumer = Identity();
            abandonedStore.ConsumeLaunchTicket(abandonedStore.Read().Revision, abandonedTicket.Nonce,
                abandonedConsumer, Hash, now);
            abandonedStore.UpdateOperatorIntent(abandonedStore.Read().Revision, "OperatorStop", "stop before bootstrap", now,
                intent => { intent.ManualStopped = true; intent.Armed = false; });
            var cancellation = new PumpOperations { FailCancellation = true };
            var abandonedPump = new IndependentProjectRecoveryPump(abandonedStore, cancellation, "executor", 30000, 30000);
            Reject(() => abandonedPump.Tick(now + 1), "fixture cancellation did not fail");
            var abandoned = abandonedStore.Read();
            Assert(abandoned.SafetyCleanupPending && abandoned.Ticket.Revoked &&
                abandoned.Controller.Matches(abandonedConsumer) &&
                abandoned.Transaction.Phase == IndependentRecoveryPhase.NeedsAttention,
                "cancel failure lost consumed process safety cleanup");
            Assert(cancellation.CancelWorkers > 0, "launch cancellation failure skipped bounded worker cleanup");
            Reject(() => abandonedStore.CommitReplacementRun(abandoned.Revision, abandonedConsumer,
                Guid.NewGuid().ToString("N"), 2, now + 2), "cancelled consumer bootstrapped");
            cancellation.FailCancellation = false;
            abandonedPump.Tick(now + TimeSpan.FromSeconds(60).Ticks);
            var cleanup = abandonedStore.Read();
            Assert(cleanup.SafetyCleanupPending && cleanup.Transaction.Phase == IndependentRecoveryPhase.CooperativeStop &&
                cleanup.Controller.Matches(abandonedConsumer) && cleanup.Intent.ManualStopped,
                "retry lost replacement identity or manual stop");
            var cleanupNow = now + TimeSpan.FromSeconds(85).Ticks;
            abandonedPump.Tick(cleanupNow);
            abandonedPump.Tick(cleanupNow + 1);
            abandonedPump.Tick(cleanupNow + 2);
            abandonedPump.Tick(cleanupNow + 3);
            Assert(!abandonedStore.Read().SafetyCleanupPending &&
                abandonedStore.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled && cancellation.Launches == 0,
                "replacement cleanup after manual stop relaunched the trial");
            var failedStore = Fixture(Path.Combine(root, "committed-verification-failure"), now);
            var failedTicket = Ready(failedStore, now);
            var failedConsumer = Identity();
            failedStore.ConsumeLaunchTicket(failedStore.Read().Revision, failedTicket.Nonce, failedConsumer, Hash, now);
            failedStore.CommitReplacementRun(failedStore.Read().Revision, failedConsumer, Guid.NewGuid().ToString("N"), 2, now);
            var failingOperations = new PumpOperations { FailVerification = true };
            var failedPump = new IndependentProjectRecoveryPump(failedStore, failingOperations, "executor", 30000, 30000);
            failedPump.Tick(now + 1);
            failedPump.Tick(now + 2);
            Assert(failedStore.Read().SafetyCleanupPending && failedStore.Read().Controller.Matches(failedConsumer) &&
                failedStore.Read().Transaction.Phase == IndependentRecoveryPhase.NeedsAttention && failedStore.Read().Ticket.Revoked,
                "verification failure forgot the live replacement");
            failedPump.Tick(now + TimeSpan.FromSeconds(60).Ticks);
            Assert(failedStore.Read().Transaction.AttemptsUtcTicks.Length == 2 && failedStore.Read().Intent.Armed,
                "verification failure bypassed retry budget or erased run intent");
            Console.WriteLine("PASS independent project state " + _count + "/" + _count);
            return _count;
        }

        private sealed class PumpOperations : IIndependentProjectRuntimeOperations
        {
            internal int CancelWorkers, Launches, Verifications;
            internal bool FailCancellation, FailVerification;
            public void CancelStageWorkers() { CancelWorkers++; }
            public void CancelPendingLaunch(IndependentRecoveryTransaction tx)
            { if (FailCancellation) throw new InvalidOperationException("InjectedCancelFailure"); }
            public IndependentOperationResult CooperativeStop(IndependentRecoveryTransaction tx) => IndependentOperationResult.Pending;
            public IndependentOperationResult ConfirmPowerOff(IndependentRecoveryTransaction tx) => IndependentOperationResult.Completed;
            public IndependentOperationResult RetireExactControls(IndependentRecoveryTransaction tx) => IndependentOperationResult.Completed;
            public IndependentOperationResult ConfirmOutputsAndPressure(IndependentRecoveryTransaction tx) => IndependentOperationResult.Completed;
            public IndependentOperationResult LaunchOnce(IndependentRecoveryTransaction tx) { Launches++; return IndependentOperationResult.Completed; }
            public IndependentOperationResult VerifyActionsAndDatabase(IndependentRecoveryTransaction tx)
            { Verifications++; return FailVerification ? IndependentOperationResult.Failed : IndependentOperationResult.Completed; }
        }
    }
}
