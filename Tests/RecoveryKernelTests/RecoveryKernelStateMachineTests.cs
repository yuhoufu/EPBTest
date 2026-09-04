using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using MTTFTest.Recovery.Kernel;
using MTTFTest.Watchdog.Protocol;

namespace RecoveryKernelTests
{
    internal static partial class RecoveryKernelStateMachineTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("pressure maintenance holds one owner and stops without counting formal work", () => MaintenanceRoundTrip(false), ref passed);
            Run("pressure maintenance persists owner and final results through DPAPI journal reload", () => MaintenanceRoundTrip(true), ref passed);
            Run("pressure maintenance requires stopped state independent proof and typed receipts", MaintenanceRequiresStopAndProof, ref passed);
            Run("pressure page end joins revoked owner and finalizes every durable alias", () => MaintenancePageClosure(true), ref passed);
            Run("pressure page end fails every alias when independent safety is unproven", () => MaintenancePageClosure(false), ref passed);
            Run("pressure maintenance heartbeat cannot renew duplicates expired or absolute lifetime", MaintenanceHeartbeatLimits, ref passed);
            Run("pressure maintenance interruptions converge without reenergizing or changing owner", MaintenanceInterruptions, ref passed);
            Run("pressure maintenance wire and journal reject foreign and orphaned authority", MaintenanceProtocolAndJournalBinding, ref passed);
            Run("project switch commits destination and same owner only after preparation", ProjectSwitchRoundTrip, ref passed);
            Run("project switch requires stopped engine and typed immutable request", ProjectSwitchAdmissionFences, ref passed);
            Run("project switch rejects stale forged and incomplete preparation receipts", ProjectSwitchReceiptFences, ref passed);
            Run("operator stop joins in-flight project switch without a second owner", ProjectSwitchStopJoinsOwner, ref passed);
            Run("project switch deadlines converge without silent source fallback", ProjectSwitchFailureAndDeadlines, ref passed);
            Run("unknown project handoff faults cannot renew owner or deadline", ProjectSwitchFaultKeepsOwnerAndDeadline, ref passed);
            Run("project switch destination and result survive durable journal reload", ProjectSwitchDurableReload, ref passed);
            Run("new project retains one owner and rejects changed creation payload", () => ProjectSwitchRoundTrip(true), ref passed);
            Run("new project metadata survives DPAPI journal and stops after activation", () => ProjectSwitchDurableReload(true), ref passed);
            Run("project reset retains one owner and rejects changed reset payload", () => ProjectSwitchRoundTrip(false, true), ref passed);
            Run("project reset metadata survives DPAPI journal and final stop", () => ProjectSwitchDurableReload(false, true), ref passed);
            Run("project reset phase deadlines cannot silently return to the old run", () => ProjectSwitchFailureAndDeadlines(true), ref passed);
            Run("initial UI discovery attaches a starting host without reinitialization", FirstEngineEnvironmentDoesNotReinitializeObservedHost, ref passed);
            Run("stopped environment retains session run budgets and isolation", StoppedEnvironmentRetainsDurableRun, ref passed);
            Run("recovery UI discovery never launches a competing EngineHost", RecoveringEnvironmentDisplaysWithoutLaunching, ref passed);
            Run("foreign process and commands cannot replace durable run identity", ForeignRunCannotOverwriteDurableSession, ref passed);
            Run("start waits for initialization or valid released-host readiness", OperatorStartRequiresFinishedInitialization, ref passed);
            Run("external native process ownership is exclusive and bounded", ExternalNativeOwnershipIsExclusiveAndBounded, ref passed);
            Run("schema7 contracts bind complete transaction identity", ProtocolContracts, ref passed);
            Run("intent and owner commit before command", IntentBeforeCommand, ref passed);
            Run("unknown scope expands to system", UnknownScopeExpandsToSystem, ref passed);
            Run("duplicate observation retains one owner", DuplicateObservationIsIdempotent, ref passed);
            Run("recovery budget is 2 local 2 current 1 LKG", FixedRecoveryLadder, ref passed);
            Run("confirmed hardware gets one active qualification", ConfirmedHardwareSingleQualification, ref passed);
            Run("UI exit does not interrupt EngineHost", UiExitIsIndependent, ref passed);
            Run("I0026 startup permit failure is recoverable work", I0026StartupFailureRegression, ref passed);
            Run("stage deadline converges instead of staying recovering", StageDeadlineConverges, ref passed);
            Run("concurrent resource faults expand to system owner", ConcurrentScopesExpandToSystem, ref passed);
            Run("operator stop commits owner before stop command", OperatorStopIsTransactional, ref passed);
            Run("operator start requires proof preflight and qualification", OperatorStartIsTransactional, ref passed);
            Run("EngineHost exit bypasses local rebuild and replaces process", EngineHostExitSchedulesReplacement, ref passed);
            Run("recovery budget resets only after stable formal work", StableRunResetsBudget, ref passed);
            Run("10000 generated unknown faults converge", GeneratedUnknownFaultsConverge, ref passed);
            Run("operator receipts commit atomically and deduplicate concurrent submissions", OperatorAdmissionDeduplicates, ref passed);
            Run("rejected command query is durable and payload reuse is rejected", OperatorRejectionIsDurable, ref passed);
            Run("operator receipt survives journal process replacement", OperatorAdmissionSurvivesReload, ref passed);
            Run("operator receipt cache is bounded and retired requests cannot execute", OperatorAdmissionRetirement, ref passed);
            Run("configuration is stopped-only owned and never resumes formal work", ConfigurationTransaction, ref passed);
            Run("DAQ configuration uses the same stopped-only owner and independent safety proof", () => ConfigurationTransaction(true), ref passed);
            Run("DAQ configuration DPAPI journal persists final and interrupted execution receipts", ConfigurationDurableReceipt, ref passed);
            Run("AO calibration uses stopped-only owner and independent safety proof", () => ConfigurationTransaction(false, ao: true), ref passed);
            Run("AO calibration DPAPI journal persists final and interrupted receipts", () => ConfigurationDurableReceipt(true), ref passed);
            Run("alarm panel admission preserves recovery owner scope budgets and isolation", PanelDoesNotChangeRecovery, ref passed);
            Run("alarm panel receipt survives reload and duplicate completion", PanelDurableCompletion, ref passed);
            Run("alarm panel pending queue is bounded", PanelQueueBounded, ref passed);
            Run("alarm dispatcher survives lost receipt and bounds expired work", PanelDispatcherReplayAndDeadline, ref passed);
            Run("recovery dispatch permits stop beside blocked normal work without unbounded retries", RecoveryDispatchIsBounded, ref passed);
            Run("operator stop tolerates older display revision but not another run", StopUsesRunFence, ref passed);
            Run("expanded fault rejects stale scoped safety proof", ExpandedFaultRejectsOldProof, ref passed);
            Run("all recovery proofs reject expired and future evidence", RecoveryProofRequiresFreshness, ref passed);
            Run("timed-out normal executor requires a durable safety barrier before budgeted retry", ExpiredNormalRequiresSafetyBarrier, ref passed);
            Run("late qualification cannot bypass expanded-scope safety command", LateQualificationCannotResume, ref passed);
            Run("failed active qualification and formal admission cross a new OFF proof", FormalAdmissionFailureRequiresSafety, ref passed);
            Run("manual pause and continue retain one durable owner across Supervisor reload", ManualBatchDurableRoundTrip, ref passed);
            Run("manual continuation rejects replacement engine and wrong owner", ManualBatchIdentityFence, ref passed);
            Run("fault during manual pause requires safety and forbids continuation", ManualBatchFaultInvalidation, ref passed);
            Run("manual pause timeout and operator stop cannot leave reusable continuation", ManualBatchStopAndDeadline, ref passed);
            Run("channel controls share one durable owner and preserve healthy peers", ManualChannelsShareOwner, ref passed);
            Run("channel controls reject ineligible targets and retire on fault", ManualChannelsRejectUnsafeContinuation, ref passed);
            Run("isolated channel retry uses one owner proof qualification and reintegration", QualificationRetryRoundTrip, ref passed);
            Run("failed isolated channel retry consumes its only budget and reasserts isolation", QualificationRetryFailureIsPermanent, ref passed);
            Run("qualification retry rejects shared scope foreign host and exhausted budget", QualificationRetryAdmissionFences, ref passed);
            return passed;
        }

        private static void RecoveryDispatchIsBounded()
        {
            var coordinator = StartRecoverableIncident(out _);
            var normal = coordinator.Snapshot().PendingCommand;
            var safety = coordinator.StopByOperator(normal.Identity.SessionId, normal.Identity.RunId, normal.Identity.RunEpoch, "test stop").Command;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var stopped = new ManualResetEventSlim())
            {
                var failures = 0;
                var calls = 0;
                var pump = new RecoveryCommandDispatchPump(command =>
                {
                    Interlocked.Increment(ref calls);
                    if (command.CommandId == normal.CommandId) { entered.Set(); release.Wait(5000); throw new IOException("lost receipt"); }
                    stopped.Set();
                }, (_, __) => Interlocked.Increment(ref failures));
                try
                {
                    Assert(pump.TryDispatch(normal) && entered.Wait(3000), "normal dispatch did not start");
                    for (var index = 0; index < 1000; index++) Assert(!pump.TryDispatch(normal), "normal retry storm");
                    Assert(pump.TryDispatch(safety) && stopped.Wait(3000), "stop waited for normal command");
                    for (var index = 0; index < 1000; index++) Assert(!pump.TryDispatch(safety), "stop retry storm");
                    Assert(!pump.StopAsync(20).GetAwaiter().GetResult(), "timeout released actual worker ownership");
                    release.Set();
                    Assert(pump.StopAsync(3000).GetAwaiter().GetResult() && calls == 2 && failures == 1, "workers were not bounded");
                    Assert(!pump.TryDispatch(normal), "stopped pump accepted new work");
                }
                finally { release.Set(); pump.StopAsync(3000).GetAwaiter().GetResult(); }
            }
        }

        private static void StopUsesRunFence()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var stop = Operator(out var engine, OperatorCommandKind.Stop); engine.Revision = 20;
            Assert(coordinator.AdmitOperatorCommand(stop, engine).Accepted, "stop rejected because display lagged");
            var wrongRun = stop.Clone(); wrongRun.CommandId = RecoveryProtocolV7.NewId(); wrongRun.RunId = RecoveryProtocolV7.NewId();
            Assert(!coordinator.AdmitOperatorCommand(wrongRun, engine).Accepted, "stop crossed run fence");
            var future = stop.Clone(); future.CommandId = RecoveryProtocolV7.NewId(); future.BaseRevision = 21;
            Assert(!coordinator.AdmitOperatorCommand(future, engine).Accepted, "future revision accepted");
            var start = stop.Clone(); start.CommandId = RecoveryProtocolV7.NewId(); start.Kind = OperatorCommandKind.Start;
            Assert(!coordinator.AdmitOperatorCommand(start, engine).Accepted, "start lost exact revision gate");
        }

        private static void ExpandedFaultRejectsOldProof()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            var first = coordinator.Observe(Observation(identity, ResourceKind.Channel, "Channel:4", true));
            var oldProof = Proof(first.Command.Identity.Clone());
            var other = identity.Clone(); other.IncidentId = RecoveryProtocolV7.NewId(); other.ResourceScope = "Channel:9";
            coordinator.Observe(Observation(other, ResourceKind.Channel, "Channel:9", true));
            var pending = coordinator.Snapshot().PendingCommand;
            Assert(pending.TargetResource == "System", "fault did not expand");
            RejectProof(coordinator, oldProof);
            Assert(coordinator.Snapshot().PendingCommand.CommandId == pending.CommandId, "stale proof mutated expanded intent");
            coordinator.AcceptSafetyProof(Proof(pending.Identity.Clone()));
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
        }

        private static void RecoveryProofRequiresFreshness()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var first = coordinator.Observe(Observation(Identity("System"), ResourceKind.System, "System", false));
            var expired = Proof(first.Command.Identity.Clone());
            expired.CapturedUtcTicks = DateTime.UtcNow.AddMinutes(-2).Ticks; expired.ValidUntilUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks;
            RejectProof(coordinator, expired);
            var future = Proof(first.Command.Identity.Clone());
            future.CapturedUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks; future.ValidUntilUtcTicks = DateTime.UtcNow.AddMinutes(2).Ticks;
            RejectProof(coordinator, future);
            Assert(coordinator.Snapshot().PendingCommand.CommandId == first.Command.CommandId, "invalid proof advanced command");
        }

        private static void RejectProof(RecoveryCoordinator coordinator, SafetyProof proof)
        {
            try { coordinator.AcceptSafetyProof(proof); }
            catch (InvalidOperationException ex) { Assert(ex.Message == "SafetyProofBindingInvalid", "unexpected proof rejection: " + ex.Message); return; }
            throw new InvalidOperationException("stale safety proof accepted");
        }

        private static void ExpiredNormalRequiresSafetyBarrier()
        {
            var journal = new MemoryJournal(); var coordinator = new RecoveryCoordinator(journal);
            var first = coordinator.Observe(Observation(Identity("Channel:4"), ResourceKind.Channel, "Channel:4", true));
            coordinator.AcceptSafetyProof(Proof(first.Command.Identity));
            var normal = coordinator.Snapshot().PendingCommand;
            coordinator.ReconcileExpiredCommand(normal.DeadlineUtcTicks + 1);
            coordinator = new RecoveryCoordinator(journal);
            var barrier = coordinator.Snapshot();
            Assert(barrier.PendingCommand.Kind == RecoveryCommandKind.DisableOutputs && barrier.ActiveIntents.Single().OwnerId == normal.OwnerId &&
                barrier.PendingCommand.Identity.Generation > normal.Identity.Generation && barrier.PendingCommand.CommandSequence > normal.CommandSequence,
                "timeout released ownership or failed to retire old executor");
            Assert(barrier.Budgets.Single().LocalRebuildAttempts == 1 &&
                barrier.ActiveIntents.Single().CommandAfterSafetyProof == RecoveryCommandKind.RebuildResource,
                "timeout bypassed retry budget or lost durable continuation");
            coordinator.AcknowledgeCommand(normal.Identity.IncidentId, normal.CommandId, true, "late success");
            Assert(coordinator.Snapshot().PendingCommand.CommandId == barrier.PendingCommand.CommandId, "late success replaced safety barrier");
            coordinator.AcceptSafetyProof(Proof(barrier.PendingCommand.Identity));
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
            Assert(coordinator.Snapshot().ActiveIntents.Single().CommandAfterSafetyProof == RecoveryCommandKind.None, "continuation was not consumed");
            // The second timeout still spends the second local attempt; after proof
            // the ladder advances to replacement, never back to local attempt one.
            normal = coordinator.Snapshot().PendingCommand;
            coordinator.ReconcileExpiredCommand(normal.DeadlineUtcTicks + 1);
            coordinator.AcceptSafetyProof(Proof(coordinator.Snapshot().PendingCommand.Identity));
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            Assert(coordinator.Snapshot().Budgets.Single().LocalRebuildAttempts == 2, "safety barrier reset budget");
        }

        private static void LateQualificationCannotResume()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            var first = coordinator.Observe(Observation(identity, ResourceKind.Channel, "Channel:4", true));
            coordinator.AcceptSafetyProof(Proof(first.Command.Identity));
            for (var index = 0; index < 2; index++)
            {
                var command = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, true, "ready");
            }
            var qualification = coordinator.Snapshot().PendingCommand;
            Assert(qualification.Kind == RecoveryCommandKind.RunQualificationCycle, "fixture did not reach qualification");
            var other = identity.Clone(); other.IncidentId = RecoveryProtocolV7.NewId(); other.ResourceScope = "Channel:9";
            coordinator.Observe(Observation(other, ResourceKind.Channel, "Channel:9", true));
            var barrier = coordinator.Snapshot().PendingCommand;
            var rejected = false;
            try { coordinator.AcceptQualification(new QualificationReceipt { Identity = qualification.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(), QualificationCyclesCompleted = 2, CapturedUtcTicks = DateTime.UtcNow.Ticks }); }
            catch (InvalidOperationException ex) { rejected = ex.Message == "QualificationReceiptBindingInvalid"; }
            Assert(rejected && coordinator.Snapshot().PendingCommand.CommandId == barrier.CommandId, "late qualification bypassed safety proof");
        }

        private static OperatorCommand PanelCommand(OperatorCommand source)
        {
            var command = source.Clone(); command.CommandId = RecoveryProtocolV7.NewId();
            command.Kind = OperatorCommandKind.AcknowledgeAlarms;
            command.AlarmPanel = new AlarmPanelCommand { PanelInstanceId = RecoveryProtocolV7.NewId(), BaseRevision = 1 };
            command.PayloadSha256 = command.AlarmPanel.ComputeSha256();
            return command;
        }

        private static void FormalAdmissionFailureRequiresSafety()
        {
            foreach (var failureKind in new[] { RecoveryCommandKind.RunQualificationCycle, RecoveryCommandKind.ResumeFormalRun })
            {
                var coordinator = StartRecoverableIncident(out var incident);
                while (coordinator.Snapshot().PendingCommand.Kind != RecoveryCommandKind.RunQualificationCycle)
                {
                    var current = coordinator.Snapshot().PendingCommand;
                    coordinator.AcknowledgeCommand(incident, current.CommandId, true, "ready");
                }
                if (failureKind == RecoveryCommandKind.ResumeFormalRun)
                    coordinator.AcceptQualification(new QualificationReceipt
                    {
                        Identity = coordinator.Snapshot().PendingCommand.Identity, ReceiptId = RecoveryProtocolV7.NewId(),
                        QualificationCyclesCompleted = 2, CapturedUtcTicks = DateTime.UtcNow.Ticks
                    });
                var failed = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(incident, failed.CommandId, false, "unknown executor failure after potential energization");
                var fence = coordinator.Snapshot();
                Assert(fence.PendingCommand?.Kind == RecoveryCommandKind.DisableOutputs && fence.ActiveIntents.Length == 1 &&
                    fence.PendingCommand.OwnerId == failed.OwnerId && fence.PendingCommand.Identity.Generation > failed.Identity.Generation,
                    "failed executor discarded owner or skipped new safety proof");
                coordinator.AcceptSafetyProof(Proof(fence.PendingCommand.Identity));
                if (failureKind == RecoveryCommandKind.RunQualificationCycle)
                    AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
                else
                    Assert(coordinator.Snapshot().PendingCommand == null && coordinator.Snapshot().ActiveIntents.Length == 0 &&
                        coordinator.Snapshot().DesiredState.State == SystemTerminalState.SafeIdleAlarmed, "formal failure did not converge after OFF proof");
            }
        }

        private static void PanelDoesNotChangeRecovery()
        {
            var journal = new MemoryJournal(); var coordinator = new RecoveryCoordinator(journal);
            var start = Operator(out var engine, OperatorCommandKind.Start);
            coordinator.AdmitOperatorCommand(start, engine);
            var before = coordinator.Snapshot();
            before.IsolatedResources = new[] { "Channel:6" };
            before.Budgets = new[] { new RecoveryBudgetState { ResourceScope = "System", LocalRebuildAttempts = 2 } };
            journal.CompareExchange(before.Revision, before);
            var command = PanelCommand(start);
            var admission = coordinator.AdmitOperatorCommand(command, engine);
            var after = coordinator.Snapshot();
            Assert(admission.Accepted && admission.IsStructurallyValid() && admission.OwnerId == "" && admission.IncidentId == "", "panel created recovery owner");
            Assert(after.ActiveIntents.Single().OwnerId == before.ActiveIntents.Single().OwnerId &&
                after.PendingCommand.CommandId == before.PendingCommand.CommandId && after.DesiredState.State == before.DesiredState.State &&
                after.Budgets.Single().LocalRebuildAttempts == 2 && after.IsolatedResources.SequenceEqual(before.IsolatedResources), "annunciation changed recovery authority");
            command.AlarmPanel.BuzzerEnabled = true;
            Assert(!command.IsStructurallyValid(), "panel payload tampering accepted");
        }

        private static void PanelDurableCompletion()
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.PanelJournal." + RecoveryProtocolV7.NewId());
            try
            {
                var coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false));
                var stop = Operator(out var engine, OperatorCommandKind.Stop); coordinator.AdmitOperatorCommand(stop, engine);
                var command = PanelCommand(stop); coordinator.AdmitOperatorCommand(command, engine);
                coordinator = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false));
                Assert(coordinator.QueryOperatorCommand(command).PanelTransaction.IsStructurallyValid(), "panel payload not durable");
                var receipt = new OperatorExecutionReceipt { CommandId = command.CommandId, Fingerprint = OperatorCommandAdmission.GetFingerprint(command),
                    Succeeded = true, CompletedUtcTicks = DateTime.UtcNow.Ticks, Detail = "serial write only" };
                coordinator.CompletePanelCommand(command, receipt);
                var revision = coordinator.Snapshot().Revision;
                coordinator.CompletePanelCommand(command, receipt);
                var restored = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false)).QueryOperatorCommand(command);
                Assert(restored.ExecutionCompleted && restored.ExecutionSucceeded && coordinator.Snapshot().Revision == revision, "completion replay changed outcome");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void PanelQueueBounded()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var stop = Operator(out var engine, OperatorCommandKind.Stop); coordinator.AdmitOperatorCommand(stop, engine);
            for (var index = 0; index < 16; index++) Assert(coordinator.AdmitOperatorCommand(PanelCommand(stop), engine).Accepted, "queue capacity too small");
            Assert(!coordinator.AdmitOperatorCommand(PanelCommand(stop), engine).Accepted &&
                coordinator.Snapshot().OperatorAdmissions.Count(a => a.PanelTransaction != null && !a.ExecutionCompleted) == 16, "unbounded panel queue");
        }

        private static void PanelDispatcherReplayAndDeadline()
        {
            var journal = new MemoryJournal(); var coordinator = new RecoveryCoordinator(journal);
            var stop = Operator(out var engine, OperatorCommandKind.Stop); coordinator.AdmitOperatorCommand(stop, engine);
            var command = PanelCommand(stop); coordinator.AdmitOperatorCommand(command, engine);
            var dispatcher = new AlarmPanelCommandDispatcher(coordinator);
            var calls = 0;
            try { dispatcher.ExecuteNext(DateTime.UtcNow.Ticks, pending => { calls++; throw new IOException("lost response"); }); }
            catch (IOException) { }
            Assert(!coordinator.QueryOperatorCommand(command).ExecutionCompleted, "lost receipt dropped durable work");
            dispatcher = new AlarmPanelCommandDispatcher(new RecoveryCoordinator(journal));
            var result = dispatcher.ExecuteNext(DateTime.UtcNow.Ticks, pending =>
            {
                calls++; Assert(pending.CommandId == command.CommandId && pending.PayloadSha256 == command.PayloadSha256, "replay changed identity");
                return new OperatorExecutionReceipt { CommandId = pending.CommandId, Fingerprint = OperatorCommandAdmission.GetFingerprint(pending),
                    Succeeded = true, CompletedUtcTicks = DateTime.UtcNow.Ticks };
            });
            Assert(result.ExecutionSucceeded && calls == 2, "reloaded dispatcher failed");
            Assert(dispatcher.ExecuteNext(DateTime.UtcNow.Ticks, _ => throw new Exception("duplicate execution")) == null, "completed command replayed");
            var expired = PanelCommand(stop); coordinator.AdmitOperatorCommand(expired, engine);
            result = dispatcher.ExecuteNext(expired.IssuedUtcTicks + TimeSpan.FromSeconds(36).Ticks,
                _ => throw new Exception("expired command dispatched"));
            Assert(result.ExecutionCompleted && !result.ExecutionSucceeded && result.CommandId == expired.CommandId,
                "expired alarm command remained indefinitely pending");
        }

        private static void OperatorAdmissionDeduplicates()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var command = Operator(out var snapshot, OperatorCommandKind.Start);
            var results = Enumerable.Range(0, 16).Select(_ => Task.Run(() => coordinator.AdmitOperatorCommand(command, snapshot))).ToArray();
            Task.WaitAll(results);
            var accepted = results[0].Result;
            Assert(results.All(task => task.Result.Accepted && task.Result.OwnerId == accepted.OwnerId), "concurrent duplicate created a new owner");
            Assert(journal.Load().Revision == 1 && journal.Load().OperatorAdmissions.Length == 1, "receipt and intent were not one CAS");
            Assert(journal.Load().ActiveIntents.Single().OwnerId == accepted.OwnerId, "receipt owner not in journal");
            var replay = new RecoveryCoordinator(journal).AdmitOperatorCommand(command, null);
            Assert(replay.Accepted && replay.OwnerId == accepted.OwnerId && journal.Load().Revision == 1, "replay mutated recovered journal");
            var stop = Operator(out snapshot, OperatorCommandKind.Stop);
            stop.SessionId = command.SessionId; stop.RunId = command.RunId;
            snapshot.SessionId = command.SessionId; snapshot.RunId = command.RunId;
            var stopped = coordinator.AdmitOperatorCommand(stop, snapshot);
            var revision = journal.Load().Revision;
            Assert(stopped.Accepted && stopped.OwnerId != accepted.OwnerId, "explicit stop did not replace start owner");
            Assert(coordinator.AdmitOperatorCommand(command, snapshot).OwnerId == accepted.OwnerId && journal.Load().Revision == revision,
                "late repeated start revived superseded owner");
        }

        private static void OperatorRejectionIsDurable()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var command = Operator(out var snapshot, OperatorCommandKind.Start);
            snapshot.Revision++;
            var rejected = coordinator.AdmitOperatorCommand(command, snapshot);
            Assert(!rejected.Accepted && rejected.FailureCode == "OperatorCommandEngineRevisionConflict", "stale revision accepted");
            snapshot.Revision--;
            Assert(!coordinator.AdmitOperatorCommand(command, snapshot).Accepted, "rejected command acquired a new meaning");
            Assert(!coordinator.QueryOperatorCommand(command).Accepted, "query changed rejected receipt");
            command.Kind = OperatorCommandKind.Stop;
            var conflict = false;
            try { coordinator.QueryOperatorCommand(command); } catch (InvalidOperationException ex) { conflict = ex.Message == "OperatorCommandIdPayloadConflict"; }
            Assert(conflict, "same ID with different payload was accepted");
        }

        private static void OperatorAdmissionSurvivesReload()
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.OperatorJournalTests." + Guid.NewGuid().ToString("N"));
            try
            {
                var command = Operator(out var snapshot, OperatorCommandKind.Stop);
                var accepted = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false)).AdmitOperatorCommand(command, snapshot);
                var restored = new RecoveryCoordinator(new FileRecoveryKernelJournal(root, false));
                Assert(restored.QueryOperatorCommand(command).OwnerId == accepted.OwnerId, "durable receipt lost on reload");
                Assert(restored.Snapshot().ActiveIntents.Single().OwnerId == accepted.OwnerId, "durable intent diverged from receipt");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void OperatorAdmissionRetirement()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var retired = Operator(out var engine, OperatorCommandKind.Stop);
            coordinator.AdmitOperatorCommand(retired, engine);
            for (var index = 0; index < 1024; index++)
            {
                var next = Operator(out var nextEngine, OperatorCommandKind.Stop);
                coordinator.AdmitOperatorCommand(next, nextEngine);
            }
            Assert(journal.Load().OperatorAdmissions.Length == 1024, "operator ledger unbounded");
            var owner = journal.Load().ActiveIntents.Single().OwnerId;
            Assert(!coordinator.AdmitOperatorCommand(retired, engine).Accepted, "retired command executed again");
            Assert(journal.Load().ActiveIntents.Single().OwnerId == owner, "retired replay replaced active owner");
        }

        private static OperatorCommand Operator(out EngineStateSnapshot snapshot, OperatorCommandKind kind)
        {
            snapshot = new EngineStateSnapshot
            {
                EngineInstanceId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1, Revision = 1, PulseSequence = 1, State = SystemTerminalState.StoppedByOperator, HardwareInitialized = true,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            };
            return new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId, RunEpoch = 1,
                BaseRevision = 1, Kind = kind, PayloadSha256 = SupervisorProtocol.ComputeTextSha256(kind.ToString()), IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static void ConfigurationTransaction() => ConfigurationTransaction(false);

        private static void ConfigurationDurableReceipt() => ConfigurationDurableReceipt(false);

        private static void ConfigurationDurableReceipt(bool ao)
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.DaqJournal." + Guid.NewGuid().ToString("N"));
            try { ConfigurationTransaction(!ao, new FileRecoveryKernelJournal(root), ao); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static void ConfigurationTransaction(bool daq, IRecoveryKernelJournal journal = null, bool ao = false)
        {
            journal = journal ?? new MemoryJournal(); var coordinator = new RecoveryCoordinator(journal);
            var stop = Operator(out var engine, OperatorCommandKind.Stop); engine.HardwareInitialized = true;
            var stopped = coordinator.AdmitOperatorCommand(stop, engine);
            var pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(stopped.IncidentId, pending.CommandId, true, "independent stop proof complete");
            var settings = new EngineTestConfiguration
            {
                TestName = "synthetic", StoreDir = "D:\\Synthetic", TestPeriod = 15, TestTarget = 200000,
                Channels = Enumerable.Range(1, 12).Select(c => new EngineRunnerConfiguration { Channel = c }).ToArray(),
                Hydraulics = new[] { new EngineHydraulicSetting { Id = 1 }, new EngineHydraulicSetting { Id = 2 } }
            };
            var command = stop.Clone(); command.CommandId = RecoveryProtocolV7.NewId(); command.Kind = OperatorCommandKind.CommitConfiguration;
            command.TestConfiguration = new TestConfigurationCommit { Configuration = settings, BaseConfigurationSha256 = settings.ComputeSha256() };
            if (daq)
            {
                var ai = new EngineDaqConfiguration { Channels = new[] { new EngineDaqChannelConfiguration
                { Sequence = 1, PhysicalChannel = "Dev1/ai0", ParameterName = "EPB1_current", Unit = "A", Slope = 10, ParameterType = "电流", Enabled = true } } };
                command.TestConfiguration = new TestConfigurationCommit { DaqConfiguration = ai, BaseConfigurationRevision = 3, BaseConfigurationSha256 = ai.ComputeSha256() };
            }
            if (ao)
            {
                var calibration = new AoCalibrationCommit { DeviceName = "Cylinder1", Points = new[]
                { new EngineAoCalibrationPoint { Voltage = 1, Pressure = 25 }, new EngineAoCalibrationPoint { Voltage = 4, Pressure = 85 } } };
                command.TestConfiguration = new TestConfigurationCommit { AoCalibration = calibration, BaseConfigurationRevision = 5,
                    BaseConfigurationSha256 = SupervisorProtocol.ComputeTextSha256("independent AO baseline") };
            }
            command.PayloadSha256 = command.TestConfiguration.ComputeSha256();
            engine.HardwareInitialized = false;
            Assert(!coordinator.AdmitOperatorCommand(command, engine).Accepted, "unavailable hardware/configuration accepted");
            engine.HardwareRecompositionReady = true;
            Assert(!coordinator.AdmitOperatorCommand(command, engine).Accepted, "durable rejection changed on replay");
            // This is a new operator attempt after a definitive rejection, not
            // a timeout retry (which must keep the original command ID).
            command = command.Clone(); command.CommandId = RecoveryProtocolV7.NewId();
            var accepted = coordinator.AdmitOperatorCommand(command, engine);
            Assert(accepted.Accepted, "released stopped configuration rejected");
            pending = coordinator.Snapshot().PendingCommand;
            Assert(pending.Kind == RecoveryCommandKind.DisableOutputs && pending.OperatorTransaction.CommandId == command.CommandId,
                "configuration not owned before safety request");
            var wrongProof = Proof(pending.Identity.Clone()); wrongProof.Identity.Generation++;
            var rejected = false;
            try { coordinator.AcceptSafetyProof(wrongProof); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "foreign safety proof allowed configuration");
            coordinator.AcceptSafetyProof(Proof(pending.Identity));
            pending = new RecoveryCoordinator(journal).Snapshot().PendingCommand;
            Assert(pending.Kind == RecoveryCommandKind.CommitTestConfiguration && pending.IsStructurallyValid(), "configuration payload not durable");
            if (ao) pending.OperatorTransaction.TestConfiguration.AoCalibration.Points[0].Pressure++;
            else if (daq) pending.OperatorTransaction.TestConfiguration.DaqConfiguration.Channels[0].Intercept++;
            else pending.OperatorTransaction.TestConfiguration.Configuration.TestTarget++;
            Assert(!pending.IsStructurallyValid(), "payload not bound to command key");
            pending = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(accepted.IncidentId, pending.CommandId, true, "committed");
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.StoppedByOperator &&
                coordinator.Snapshot().PendingCommand == null && coordinator.Snapshot().ActiveIntents.Length == 0, "configuration auto-started work");
            var duplicate = coordinator.AdmitOperatorCommand(command, null);
            Assert(duplicate.OwnerId == accepted.OwnerId, "config replay generated new owner");
            Assert(duplicate.ExecutionCompleted && duplicate.ExecutionSucceeded && duplicate.IsStructurallyValid(), "configuration final receipt missing");
            command = command.Clone(); command.CommandId = RecoveryProtocolV7.NewId();
            engine.State = SystemTerminalState.Running;
            Assert(!coordinator.AdmitOperatorCommand(command, engine).Accepted, "running configuration accepted");
            engine.State = SystemTerminalState.StoppedByOperator;
            command.CommandId = RecoveryProtocolV7.NewId();
            var active = coordinator.AdmitOperatorCommand(command, engine);
            Assert(active.Accepted, "second stopped configuration rejected");
            var observation = Observation(coordinator.Snapshot().PendingCommand.Identity.Clone(), ResourceKind.System, "System", false);
            observation.Identity.IncidentId = RecoveryProtocolV7.NewId();
            var cancelled = coordinator.Observe(observation);
            Assert(cancelled.Reason == "ConfigurationInterruptedByFault" && cancelled.Command.OperatorTransaction == null,
                "fault retained configuration mutation");
            var failed = new RecoveryCoordinator(journal).QueryOperatorCommand(command);
            Assert(failed.ExecutionCompleted && !failed.ExecutionSucceeded && failed.IsStructurallyValid(), "configuration interruption receipt missing");
            coordinator.AcceptSafetyProof(Proof(cancelled.Command.Identity));
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.SafeIdleAlarmed && coordinator.Snapshot().PendingCommand == null,
                "interrupted configuration retried or resumed");
            foreach (var interruption in new[] { "stop", "safety-timeout", "commit-timeout" })
            {
                var resetStop = stop.Clone(); resetStop.CommandId = RecoveryProtocolV7.NewId();
                var stopAdmission = coordinator.AdmitOperatorCommand(resetStop, engine);
                pending = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(stopAdmission.IncidentId, pending.CommandId, true, "isolated stop proof");
                command = command.Clone(); command.CommandId = RecoveryProtocolV7.NewId();
                Assert(coordinator.AdmitOperatorCommand(command, engine).Accepted, "stopped config retry rejected");
                pending = coordinator.Snapshot().PendingCommand;
                if (interruption == "stop")
                {
                    resetStop.CommandId = RecoveryProtocolV7.NewId(); coordinator.AdmitOperatorCommand(resetStop, engine);
                }
                else
                {
                    if (interruption == "commit-timeout")
                    { coordinator.AcceptSafetyProof(Proof(pending.Identity)); pending = coordinator.Snapshot().PendingCommand; }
                    coordinator.ReconcileExpiredCommand(pending.DeadlineUtcTicks + 1);
                }
                failed = new RecoveryCoordinator(journal).QueryOperatorCommand(command);
                Assert(failed.ExecutionCompleted && !failed.ExecutionSucceeded && failed.IsStructurallyValid(), "missing configuration failure receipt:" + interruption);
                pending = coordinator.Snapshot().PendingCommand;
                if (pending != null)
                {
                    if (pending.Kind == RecoveryCommandKind.StopByOperator)
                        coordinator.AcknowledgeCommand(pending.Identity.IncidentId, pending.CommandId, true, "stop complete");
                    else coordinator.AcceptSafetyProof(Proof(pending.Identity));
                }
            }
        }

        private static void ProtocolContracts()
        {
            var identity = Identity("Channel:4");
            Assert(identity.IsStructurallyValid(), "identity invalid");
            var observation = Observation(identity, ResourceKind.Channel, "Channel:4", true);
            Assert(observation.IsStructurallyValid(), "observation invalid");
            var intent = new RecoveryIntent
            {
                Identity = identity,
                OwnerId = RecoveryProtocolV7.NewId(),
                Stage = RecoveryStage.IntentCommitted,
                DesiredTerminalState = SystemTerminalState.RunningDegraded,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                UpdatedUtcTicks = DateTime.UtcNow.Ticks
            };
            Assert(intent.IsStructurallyValid(), "intent invalid");
            var command = new RecoveryCommand
            {
                Identity = identity,
                OwnerId = intent.OwnerId,
                CommandId = RecoveryProtocolV7.NewId(),
                CommandSequence = 1,
                Kind = RecoveryCommandKind.DisableOutputs,
                TargetResource = "Channel:4",
                DeadlineUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks
            };
            command.IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(
                identity, command.Kind, command.CommandSequence);
            Assert(command.IsStructurallyValid(), "command invalid");
        }

        private static void IntentBeforeCommand()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var decision = coordinator.Observe(Observation(
                Identity("Channel:4"), ResourceKind.Channel, "Channel:4", true));
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.Revision == 1, "first durable revision missing");
            Assert(snapshot.ActiveIntents.Length == 1, "intent not durable");
            Assert(snapshot.PendingCommand != null, "command not created");
            Assert(snapshot.ActiveIntents[0].OwnerId == snapshot.PendingCommand.OwnerId,
                "command not bound to owner");
            Assert(decision.Intent.OwnerId == snapshot.ActiveIntents[0].OwnerId,
                "returned owner differs from durable owner");
        }

        private static void UnknownScopeExpandsToSystem()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var observation = Observation(
                Identity("Channel:4"), ResourceKind.Channel, "NeverSeenResource", false);
            var decision = coordinator.Observe(observation);
            Assert(decision.Intent.Identity.ResourceScope == "System", "scope did not expand");
            Assert(decision.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "unknown safety scope did not stay de-energized");
        }

        private static void DuplicateObservationIsIdempotent()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var observation = Observation(
                Identity("Power:4"), ResourceKind.ElectricalGroup, "Power:4", true);
            var first = coordinator.Observe(observation);
            var second = coordinator.Observe(observation);
            Assert(second.Duplicate, "duplicate was not detected");
            Assert(first.Intent.OwnerId == second.Intent.OwnerId, "owner changed");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1, "duplicate owner created");
        }

        private static void FixedRecoveryLadder()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "local-1");
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "local-2");
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "current-1");
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "current-2");
            AssertKind(coordinator, RecoveryCommandKind.ActivateLastKnownGood);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false, "lkg-1");
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.PendingCommand == null, "recovery storm continued");
            Assert(snapshot.ActiveIntents.Length == 0, "terminal owner leaked");
            Assert(snapshot.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "exhausted recovery did not converge safe");
        }

        private static void ConfirmedHardwareSingleQualification()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var identity = Identity("Channel:4");
            var observation = Observation(identity, ResourceKind.Channel, "Channel:4", true);
            observation.HardwareConfirmed = true;
            coordinator.Observe(observation);
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, false,
                "passive preflight failed");
            AssertKind(coordinator, RecoveryCommandKind.IsolateResource);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, true,
                "Channel:4 permanently isolated");
            var snapshot = coordinator.Snapshot();
            Assert(snapshot.IsolatedResources.Contains("Channel:4"), "hardware scope not isolated");
            Assert(snapshot.PendingCommand?.Kind == RecoveryCommandKind.RunPassivePreflight &&
                snapshot.DesiredState.State == SystemTerminalState.SafeIdleAlarmed && snapshot.ActiveIntents.Length == 1,
                "isolation pretended healthy domains were already running");
            var owner = snapshot.ActiveIntents[0].OwnerId;
            coordinator = new RecoveryCoordinator(journal); // Resume the actual durable state, not a new Owner.
            command = coordinator.Snapshot().PendingCommand;
            Assert(command.OwnerId == owner, "isolation continuation lost owner after reload");
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, true, "healthy passive preflight");
            AssertKind(coordinator, RecoveryCommandKind.RunQualificationCycle);
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = coordinator.Snapshot().PendingCommand.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(), QualificationCyclesCompleted = 2,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            });
            AssertKind(coordinator, RecoveryCommandKind.ResumeFormalRun);
            command = coordinator.Snapshot().PendingCommand;
            Assert(command.OwnerId == owner && coordinator.Snapshot().ActiveIntents.Length == 1,
                "healthy formal start has no original Owner");
            coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, true, "healthy domains now running");
            snapshot = coordinator.Snapshot();
            Assert(snapshot.DesiredState.State == SystemTerminalState.RunningDegraded && snapshot.ActiveIntents.Length == 0 &&
                snapshot.IsolatedResources.Contains("Channel:4"), "healthy restart cleared isolation or leaked owner");
        }

        private static void UiExitIsIndependent()
        {
            var journal = new MemoryJournal();
            var coordinator = new RecoveryCoordinator(journal);
            var identity = Identity("UserInterface");
            var observation = Observation(
                identity, ResourceKind.UserInterface, "UserInterface", true);
            observation.FaultCode = "ProcessExited";
            observation.ObservableProperty = "UiPulseMissing";
            var decision = coordinator.Observe(observation);
            Assert(decision.Command.Kind == RecoveryCommandKind.EnsureUserInterface,
                "UI exit requested hardware recovery");
            Assert(decision.DesiredState.State == SystemTerminalState.Running,
                "UI exit changed engine desired state");
            coordinator.AcknowledgeCommand(
                identity.IncidentId, decision.Command.CommandId, true, "UI restarted");
            Assert(coordinator.Snapshot().DesiredState.State == SystemTerminalState.Running,
                "engine was interrupted by UI replacement");
        }

        private static void I0026StartupFailureRegression()
        {
            var sessionId = RecoveryProtocolV7.NewId();
            var identity = RecoveryCoordinator.CreateBootstrapIdentity(sessionId, 1, "Power:4");
            Assert(RecoveryProtocolV7.IsGuid(identity.RunId),
                "pre-formal start has no RunId");
            var observation = Observation(
                identity, ResourceKind.ElectricalGroup, "Power:4", true);
            observation.FaultCode = "StartupPositioningException";
            observation.ObservableProperty =
                "ChannelExecutionPermitMissing+SOUR:CURR?Timeout";
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var decision = coordinator.Observe(observation);
            Assert(decision.Reason != "RejectedNoWork", "startup was discarded as no work");
            Assert(decision.Intent.OwnerId.Length == 32, "startup incident has no owner");
            coordinator.AcceptSafetyProof(Proof(identity));
            for (var index = 0; index < 2; index++)
            {
                var failed = coordinator.Snapshot().PendingCommand;
                coordinator.AcknowledgeCommand(
                    identity.IncidentId, failed.CommandId, false, "lease/permit state corrupt");
            }
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            var replacement = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, replacement.CommandId, true, "fresh EngineHost");
            var preflight = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, preflight.CommandId, true, "passive safe");
            var qualification = coordinator.Snapshot().PendingCommand;
            Assert(qualification.Kind == RecoveryCommandKind.RunQualificationCycle,
                "qualification was skipped");
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = qualification.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                InterruptedCycleCounted = false
            });
            var resume = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId, resume.CommandId, true, "startup recovered");
            var terminal = coordinator.Snapshot();
            Assert(terminal.ActiveIntents.Length == 0, "startup owner leaked");
            Assert(terminal.DesiredState.State == SystemTerminalState.RunningDegraded,
                "I0026 sequence did not recover");
        }

        private static void StageDeadlineConverges()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            var decision = coordinator.Observe(Observation(
                identity, ResourceKind.Channel, "Channel:4", true));
            var expired = coordinator.ReconcileExpiredCommand(
                decision.Command.DeadlineUtcTicks + 1);
            Assert(expired != null, "expired safety stage was ignored");
            var terminal = coordinator.Snapshot();
            Assert(terminal.PendingCommand == null,
                "expired safety stage retained a pending command");
            Assert(terminal.ActiveIntents.Length == 0,
                "expired safety stage retained an owner");
            Assert(terminal.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "expired safety stage did not converge to SafeIdleAlarmed");
        }

        private static void ConcurrentScopesExpandToSystem()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var firstIdentity = Identity("Channel:4");
            var first = coordinator.Observe(Observation(
                firstIdentity, ResourceKind.Channel, "Channel:4", true));
            var firstCommandId = first.Command.CommandId;
            var secondIdentity = firstIdentity.Clone();
            secondIdentity.IncidentId = RecoveryProtocolV7.NewId();
            secondIdentity.ResourceScope = "DAQ:Dev2";
            var second = coordinator.Observe(Observation(
                secondIdentity, ResourceKind.DaqDevice, "DAQ:Dev2", true));
            Assert(second.Duplicate, "concurrent fault created a second owner");
            Assert(second.Intent.Identity.ResourceScope == "System",
                "concurrent independent scopes were not expanded to System");
            Assert(second.Command.Kind == RecoveryCommandKind.DisableOutputs,
                "scope expansion did not restart the all-domain safety gate");
            Assert(second.Command.TargetResource == "System",
                "expanded safety command retained a partial target");
            Assert(second.Command.CommandId != firstCommandId,
                "scope expansion reused an obsolete partial-domain command");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1,
                "concurrent faults produced multiple recovery owners");
        }

        private static void GeneratedUnknownFaultsConverge()
        {
            var random = new Random(0x300000);
            for (var sample = 0; sample < 10000; sample++)
            {
                var coordinator = new RecoveryCoordinator(new MemoryJournal());
                var proven = random.Next(0, 3) != 0;
                var identity = Identity(proven ? "Channel:4" : "System");
                var observation = Observation(
                    identity,
                    proven ? ResourceKind.Channel : ResourceKind.System,
                    proven ? "Channel:4" : "Unknown:" + sample,
                    proven);
                observation.FaultCode = "Unknown";
                observation.ObservableProperty = "GeneratedProperty:" + random.Next();
                coordinator.Observe(observation);
                var proof = Proof(identity);
                if (random.Next(0, 4) == 0) proof.PressureSafe = false;
                coordinator.AcceptSafetyProof(proof);
                var steps = 0;
                while (coordinator.Snapshot().ActiveIntents.Length > 0 && steps++ < 16)
                {
                    var snapshot = coordinator.Snapshot();
                    Assert(snapshot.PendingCommand != null, "generated sequence retained an owner without work");
                    var command = snapshot.PendingCommand;
                    var succeed = command.Kind == RecoveryCommandKind.RunPassivePreflight ||
                                  command.Kind == RecoveryCommandKind.RunQualificationCycle ||
                                  command.Kind == RecoveryCommandKind.ResumeFormalRun
                        ? random.Next(0, 2) == 0
                        : false;
                    if (command.Kind == RecoveryCommandKind.RunQualificationCycle && succeed)
                        coordinator.AcceptQualification(new QualificationReceipt { Identity = command.Identity,
                            ReceiptId = RecoveryProtocolV7.NewId(), QualificationCyclesCompleted = 2, CapturedUtcTicks = DateTime.UtcNow.Ticks });
                    else coordinator.AcknowledgeCommand(identity.IncidentId, command.CommandId, succeed, "generated");
                }
                var terminal = coordinator.Snapshot();
                Assert(terminal.ActiveIntents.Length == 0,
                    "generated sequence retained owner at sample " + sample);
                Assert(IsLegalTerminal(terminal.DesiredState?.State ?? SystemTerminalState.Unknown),
                    "illegal terminal at sample " + sample);
            }
        }

        private static void OperatorStopIsTransactional()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var sessionId = RecoveryProtocolV7.NewId();
            var runId = RecoveryProtocolV7.NewId();
            var decision = coordinator.StopByOperator(
                sessionId, runId, 1, "OperatorStopTest");
            var pending = coordinator.Snapshot();
            Assert(decision.Intent != null && decision.Command != null,
                "operator stop owner or command missing");
            Assert(pending.ActiveIntents.Length == 1,
                "operator stop intent was not durable");
            Assert(pending.PendingCommand.Kind == RecoveryCommandKind.StopByOperator,
                "operator stop command missing");
            Assert(pending.DesiredState.State == SystemTerminalState.SafeIdleAlarmed,
                "stop declared terminal before hardware receipt");
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                decision.Command.CommandId,
                true,
                "outputs off");
            var terminal = coordinator.Snapshot();
            Assert(terminal.DesiredState.State == SystemTerminalState.StoppedByOperator,
                "operator stop did not reach terminal after receipt");
            Assert(terminal.ActiveIntents.Length == 0,
                "operator stop owner leaked");
        }

        private static void OperatorStartIsTransactional()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var decision = coordinator.StartByOperator(
                RecoveryProtocolV7.NewId(),
                RecoveryProtocolV7.NewId(),
                1,
                "OperatorStartTest");
            Assert(decision.Command.Kind == RecoveryCommandKind.DisableOutputs,
                "operator start did not begin at safety gate");
            Assert(coordinator.Snapshot().ActiveIntents.Length == 1,
                "operator start owner was not durable");
            coordinator.AcceptSafetyProof(Proof(decision.Intent.Identity));
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                command.CommandId,
                true,
                "passive safe");
            AssertKind(coordinator, RecoveryCommandKind.RunQualificationCycle);
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = coordinator.Snapshot().PendingCommand.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            });
            AssertKind(coordinator, RecoveryCommandKind.ResumeFormalRun);
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                decision.Intent.Identity.IncidentId,
                command.CommandId,
                true,
                "formal run active");
            var terminal = coordinator.Snapshot();
            Assert(terminal.ActiveIntents.Length == 0,
                "operator start owner leaked");
            Assert(terminal.DesiredState.State == SystemTerminalState.Running,
                "operator start did not converge Running");
        }

        private static void EngineHostExitSchedulesReplacement()
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("EngineHost");
            var decision = coordinator.Observe(Observation(
                identity, ResourceKind.EngineHost, "EngineHost", true));
            Assert(decision.Intent.RequiresEngineReplacement,
                "EngineHost process fault was treated as resource rebuild");
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.ReplaceEngineHost);
            var replacement = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(
                identity.IncidentId,
                replacement.CommandId,
                true,
                "replacement admitted safe idle");
            AssertKind(coordinator, RecoveryCommandKind.RunPassivePreflight);
        }

        private static void StableRunResetsBudget()
        {
            var coordinator = StartRecoverableIncident(out var incident);
            var command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, false,
                "first local rebuild failed");
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "second local rebuild succeeded");
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "passive safe");
            var identity = coordinator.Snapshot().ActiveIntents[0].Identity;
            coordinator.AcceptQualification(new QualificationReceipt
            {
                Identity = coordinator.Snapshot().PendingCommand.Identity,
                ReceiptId = RecoveryProtocolV7.NewId(),
                QualificationCyclesCompleted = 2,
                FormalCyclesCompleted = 0,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            });
            command = coordinator.Snapshot().PendingCommand;
            coordinator.AcknowledgeCommand(incident, command.CommandId, true,
                "formal running");
            var before = coordinator.Snapshot();
            Assert(before.Budgets.Any(value => value.LocalRebuildAttempts == 1),
                "test did not consume recovery budget");
            var stableSince = DateTime.UtcNow.AddMinutes(-11).Ticks;
            Assert(coordinator.ObserveStableProgress(
                    identity.SessionId,
                    identity.RunId,
                    identity.RunEpoch,
                    2,
                    5,
                    stableSince,
                    DateTime.UtcNow.Ticks),
                "qualified stable run did not reset budget");
            Assert(coordinator.Snapshot().Budgets.All(value =>
                    value.LocalRebuildAttempts == 0 &&
                    value.CurrentEngineReplacementAttempts == 0 &&
                    value.LastKnownGoodAttempts == 0 &&
                    value.ActiveQualificationAttempts == 0),
                "recovery budget did not reset atomically");
        }

        private static RecoveryCoordinator StartRecoverableIncident(out string incident)
        {
            var coordinator = new RecoveryCoordinator(new MemoryJournal());
            var identity = Identity("Channel:4");
            incident = identity.IncidentId;
            coordinator.Observe(Observation(
                identity, ResourceKind.Channel, "Channel:4", true));
            coordinator.AcceptSafetyProof(Proof(identity));
            AssertKind(coordinator, RecoveryCommandKind.RebuildResource);
            return coordinator;
        }

        private static RecoveryIdentity Identity(string scope)
        {
            return new RecoveryIdentity
            {
                SessionId = RecoveryProtocolV7.NewId(),
                RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1,
                IncidentId = RecoveryProtocolV7.NewId(),
                ResourceScope = scope,
                Generation = 1,
                Revision = 1
            };
        }

        private static FaultObservation Observation(
            RecoveryIdentity identity,
            ResourceKind kind,
            string resource,
            bool scopeProven)
        {
            return new FaultObservation
            {
                ObservationId = RecoveryProtocolV7.NewId(),
                Identity = identity,
                ResourceKind = kind,
                ResourceId = resource,
                FaultCode = "Unknown",
                ObservableProperty = "InvariantChanged",
                Severity = FaultSeverity.RecoveryRequired,
                ScopeProven = scopeProven,
                OutputOffConfirmed = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                SafetyChainHealthy = true,
                ObservedUtcTicks = DateTime.UtcNow.Ticks
            };
        }

        private static SafetyProof Proof(RecoveryIdentity identity)
        {
            return new SafetyProof
            {
                Identity = identity,
                ProofId = RecoveryProtocolV7.NewId(),
                SafetyAgentInstanceId = RecoveryProtocolV7.NewId(),
                OutputsOff = true,
                PressureSafe = true,
                DataBoundaryClosed = true,
                OldProcessIsolated = true,
                SafetyChainHealthy = true,
                CapturedUtcTicks = DateTime.UtcNow.Ticks,
                ValidUntilUtcTicks = DateTime.UtcNow.AddSeconds(30).Ticks
            };
        }

        private static bool IsLegalTerminal(SystemTerminalState state)
        {
            return state == SystemTerminalState.Running ||
                   state == SystemTerminalState.RunningDegraded ||
                   state == SystemTerminalState.SafeIdleAlarmed ||
                   state == SystemTerminalState.StoppedByOperator;
        }

        private static void AssertKind(
            RecoveryCoordinator coordinator,
            RecoveryCommandKind expected)
        {
            Assert(coordinator.Snapshot().PendingCommand?.Kind == expected,
                "expected command " + expected + " but got " +
                coordinator.Snapshot().PendingCommand?.Kind);
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class MemoryJournal : IRecoveryKernelJournal
        {
            private readonly object _gate = new object();
            private RecoveryKernelJournalDocument _document =
                new RecoveryKernelJournalDocument
                {
                    Revision = 0,
                    UpdatedUtcTicks = DateTime.UtcNow.Ticks
                };

            public RecoveryKernelJournalDocument Load()
            {
                lock (_gate) return _document.Clone();
            }

            public RecoveryJournalCommitResult CompareExchange(
                long expectedRevision,
                RecoveryKernelJournalDocument candidate)
            {
                lock (_gate)
                {
                    if (_document.Revision != expectedRevision)
                        return new RecoveryJournalCommitResult
                        {
                            Conflict = true,
                            Reason = "conflict",
                            Document = _document.Clone()
                        };
                    _document = candidate.Clone();
                    _document.Revision = expectedRevision + 1;
                    _document.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    return new RecoveryJournalCommitResult
                    {
                        Committed = true,
                        Reason = "committed",
                        Document = _document.Clone()
                    };
                }
            }
        }
    }
}
