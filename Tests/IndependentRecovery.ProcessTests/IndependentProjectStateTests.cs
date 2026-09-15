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

        private static void VerifyControllerChannelPause(string root, long now)
        {
            var directory = Path.Combine(root, "durable-channel-pause");
            var store = Fixture(directory, now);
            var original = store.Read();
            var command = Guid.NewGuid().ToString("N");
            store.SetControllerChannelPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch,
                7, true, command, now + 1);
            var paused = new IndependentProjectStateStore(directory).Read();
            Assert(paused.Intent.SelectedChannels.SequenceEqual(original.Intent.SelectedChannels) &&
                paused.Intent.RecoveryChannels().SequenceEqual(new[] { 4, 5, 8, 9, 12 }) &&
                paused.Intent.PausedChannels.SequenceEqual(new[] { 7 }),
                "single pause was not durable or changed other channel selections");
            store.SetControllerChannelPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch,
                7, true, command, now + 2);
            Assert(store.Read().Revision == paused.Revision, "duplicate channel pause changed authority");
            store.SetControllerManualPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch,
                true, Guid.NewGuid().ToString("N"), now + 2);
            store.SetControllerManualPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch,
                false, Guid.NewGuid().ToString("N"), now + 3);
            Assert(!store.Read().Intent.RecoveryChannels().Contains(7),
                "whole-batch continue cleared an independent channel pause");
            var deadlineBeforeChannelContinue = store.Read().StartupDeadlineUtcTicks;
            Reject(() => store.SetControllerChannelPause(Identity(), original.Intent.RunId, original.Intent.RunEpoch,
                7, false, Guid.NewGuid().ToString("N"), now + 3), "foreign controller resumed paused channel");
            Reject(() => store.SetControllerChannelPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch + 1,
                7, false, Guid.NewGuid().ToString("N"), now + 3), "wrong epoch resumed paused channel");
            store.SetControllerChannelPause(original.Controller, original.Intent.RunId, original.Intent.RunEpoch,
                7, false, Guid.NewGuid().ToString("N"), now + 4);
            Assert(store.Read().Intent.RecoveryChannels().SequenceEqual(original.Intent.RecoveryChannels()) &&
                store.Read().StartupDeadlineUtcTicks == deadlineBeforeChannelContinue,
                "channel continue lost targets or postponed other stalled channels");
            foreach (var kind in new[] { "stopped", "batch-paused", "unselected", "permanent", "completed", "maintenance" })
            {
                var denied = Fixture(Path.Combine(root, "channel-continue-" + kind), now);
                denied.Update(denied.Read().Revision, state =>
                {
                    state.Intent.PausedChannels = new[] { 7 };
                    if (kind == "stopped") { state.Intent.ManualStopped = true; state.Intent.Armed = false; }
                    if (kind == "batch-paused") state.Intent.ManualPaused = true;
                    if (kind == "unselected") state.Intent.SelectedChannels = new[] { 8, 9 };
                    if (kind == "permanent") state.Intent.PermanentChannels = new[] { 7 };
                    if (kind == "completed") state.Intent.CompletedChannels = new[] { 7 };
                    if (kind == "maintenance") state.Maintenance = true;
                    return true;
                });
                var before = denied.Read();
                Reject(() => denied.SetControllerChannelPause(before.Controller, before.Intent.RunId, before.Intent.RunEpoch,
                    7, false, Guid.NewGuid().ToString("N"), now + 5), "channel continue bypassed " + kind);
                Assert(denied.Read().Revision == before.Revision, "rejected continue mutated " + kind);
            }
            var pending = Fixture(Path.Combine(root, "channel-pause-launch-race"), now);
            var ticket = Ready(pending, now);
            var parent = pending.Read();
            pending.SetControllerChannelPause(parent.Controller, parent.Intent.RunId, parent.Intent.RunEpoch,
                7, true, Guid.NewGuid().ToString("N"), now + 1);
            Assert(pending.Read().Ticket.Revoked && !pending.Read().Intent.RecoveryChannels().Contains(7),
                "operator pause left pending launch permission active");
            Reject(() => pending.ConsumeLaunchTicket(pending.Read().Revision, ticket.Nonce, Identity(), Hash, now + 2),
                "old ticket restored a manually paused channel");
            using (var child = Process.Start(new ProcessStartInfo(Exe,
                "--state-consume \"" + Path.Combine(root, "channel-pause-launch-race") + "\" " +
                ticket.Nonce + " " + pending.Read().Revision) { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    if (!child.WaitForExit(10000)) throw new Exception("paused ticket consumer deadline exceeded");
                    Assert(child.ExitCode == 23 && pending.Read().Ticket.Consumer == null,
                        "replacement process consumed a ticket revoked by single-channel pause");
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                }
            }
            Reject(() => pending.SetControllerChannelPause(parent.Controller, parent.Intent.RunId, parent.Intent.RunEpoch,
                7, false, Guid.NewGuid().ToString("N"), now + 2), "channel continue raced active takeover");
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

        private static void VerifyTakeoverAudit(string root, long now)
        {
            var directory = Path.Combine(root, "takeover-audit");
            var store = Fixture(directory, now);
            var original = store.Read();
            var tx = store.BeginRecovery(original.Revision, "executor", now, "DatabaseStalled:8,9");
            store = new IndependentProjectStateStore(directory);
            var row = store.Read().Audit.Last();
            Assert(row.Source == "IndependentTakeover" && row.RunId == original.Intent.RunId &&
                row.RunEpoch == original.Intent.RunEpoch && row.Reason.Contains("RequestId=" + tx.RequestId) &&
                row.Reason.Contains("Generation=1;DatabaseStalled:8,9"), "takeover cause was not durably bound to old run and transaction");
            Assert(row.Before.SequenceEqual(original.Intent.SelectedChannels) && row.After.SequenceEqual(row.Before) &&
                store.Read().Intent.Revision == original.Intent.Revision, "diagnostic admission changed channel authorization");
            var revision = store.Read().Revision;
            Reject(() => store.BeginRecovery(revision, "executor", now, "RejectedDuplicate"), "duplicate takeover admitted");
            Assert(store.Read().Revision == revision && store.Read().Audit.Last().Reason == row.Reason,
                "rejected takeover rewrote committed trigger");
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId, phase, true, now, 30000, "PhaseReplaced");
            var ticket = store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash,
                Process.GetCurrentProcess().SessionId, now);
            store.MarkLaunchDispatched(store.Read().Revision, ticket.Nonce, "executor", now);
            var consumer = Identity();
            store.ConsumeLaunchTicket(store.Read().Revision, ticket.Nonce, consumer, Hash, now);
            store.CommitReplacementRun(store.Read().Revision, consumer, Guid.NewGuid().ToString("N"), 2, now + 1);
            var replaced = new IndependentProjectStateStore(directory).Read();
            Assert(replaced.Audit.Single(a => a.Source == "IndependentTakeover").Reason == row.Reason &&
                replaced.Audit.Single(a => a.Source == "IndependentTakeover").RunId == original.Intent.RunId &&
                replaced.Intent.RunId != original.Intent.RunId, "replacement binding erased original trigger");
            var bounded = Fixture(Path.Combine(root, "takeover-audit-bounded"), now);
            var boundedTx = bounded.BeginRecovery(bounded.Read().Revision, "executor", now, new string('x', 1000));
            var boundedRow = bounded.Read().Audit.Last();
            Assert(boundedRow.Reason.Length <= 192 && boundedRow.Reason.Contains(boundedTx.RequestId),
                "oversize trigger lost transaction identity or exceeded diagnostic limit");
        }

        private static void VerifyOperatorSafetyStop(string root, long now)
        {
            var store = Fixture(Path.Combine(root, "operator-safety-stop"), now);
            var initial = store.Read(); var command = Guid.NewGuid().ToString("N");
            store.RequestOperatorSafetyStop(initial.Revision, initial.Intent.RunId, initial.Intent.RunEpoch, "executor", command, now);
            var stopped = store.Read(); var tx = stopped.Transaction;
            Assert(!stopped.Intent.Armed && stopped.Intent.ManualStopped && stopped.SafetyCleanupPending && tx.OperatorStopOnly &&
                tx.AttemptsUtcTicks.Length == 0 && stopped.Ticket == null, "operator stop did not atomically revoke and enqueue cleanup");
            store.RequestOperatorSafetyStop(stopped.Revision, initial.Intent.RunId, initial.Intent.RunEpoch, "executor", command, now + 1);
            Assert(store.Read().Transaction.RequestId == tx.RequestId && store.Read().Intent.Revision == stopped.Intent.Revision,
                "duplicate operator stop restarted cleanup");
            Reject(() => store.RequestOperatorSafetyStop(store.Read().Revision, Guid.NewGuid().ToString("N"), 1, "executor", command, now),
                "old run stop could affect current batch");
            Reject(() => store.IssueLaunchTicket(store.Read().Revision, "executor", tx.Generation, Hash, 1, now), "stop minted a launch ticket");
            foreach (var phase in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                store.CompleteSafetyStage(store.Read().Revision, "executor", tx.Generation, tx.RequestId, phase, true, now, 30000, "StopConfirmed");
            Assert(store.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled && !store.Read().SafetyCleanupPending &&
                store.Read().Ticket == null, "safe stop did not finish without launch");
            store.RequestOperatorSafetyStop(store.Read().Revision, initial.Intent.RunId, 1, "executor", Guid.NewGuid().ToString("N"), now + 2);
            Assert(store.Read().Transaction.RequestId == tx.RequestId, "completed stop repeated hardware cleanup");
            var next = Intent(root); next.RunId = Guid.NewGuid().ToString("N");
            next.ProjectDirectory = initial.Intent.ProjectDirectory; next.DatabasePath = initial.Intent.DatabasePath;
            store.ArmManualRun(store.Read().Revision, next, Identity(), now + 3);
            var recovery = store.BeginRecovery(store.Read().Revision, "executor", now + 4);
            Assert(recovery.AttemptsUtcTicks.Length == 1 && !recovery.OperatorStopOnly,
                "manual shutdown consumed restart budget or imposed a restart cooldown");

            var busy = Fixture(Path.Combine(root, "operator-stop-during-safety"), now);
            var active = busy.BeginRecovery(busy.Read().Revision, "executor", now);
            var before = busy.Read();
            busy.RequestOperatorSafetyStop(before.Revision, before.Intent.RunId, 1, "executor", Guid.NewGuid().ToString("N"), now + 1);
            Assert(busy.Read().Transaction.RequestId == active.RequestId && !busy.Read().Intent.Armed,
                "operator stop replaced an already-running safe-off transaction");

            var launched = Fixture(Path.Combine(root, "operator-stop-during-launch"), now);
            var ticket = Ready(launched, now); var consumer = Identity();
            launched.ConsumeLaunchTicket(launched.Read().Revision, ticket.Nonce, consumer, Hash, now);
            before = launched.Read();
            launched.RequestOperatorSafetyStop(before.Revision, before.Intent.RunId, 1, "executor", Guid.NewGuid().ToString("N"), now + 1);
            Assert(launched.Read().Controller.Matches(consumer) && launched.Read().Ticket == null && launched.Read().Transaction.OperatorStopOnly,
                "stop before bootstrap lost replacement identity");

            var failed = Fixture(Path.Combine(root, "operator-stop-exhausted"), now);
            failed.Update(failed.Read().Revision, state =>
            {
                var prior = IndependentRecoveryTransitions.Begin(null, state.Intent, "executor", now - TimeSpan.FromMinutes(3).Ticks);
                prior.Phase = IndependentRecoveryPhase.NeedsAttention;
                prior.AttemptsUtcTicks = new[] { now - TimeSpan.FromMinutes(3).Ticks, now - TimeSpan.FromMinutes(2).Ticks, now - TimeSpan.FromSeconds(5).Ticks };
                prior.LastAttemptUtcTicks = prior.AttemptsUtcTicks.Last(); state.Transaction = prior; return true;
            });
            before = failed.Read();
            failed.RequestOperatorSafetyStop(before.Revision, before.Intent.RunId, 1, "executor", Guid.NewGuid().ToString("N"), now);
            tx = failed.Read().Transaction;
            Assert(tx.OperatorStopOnly && tx.AttemptsUtcTicks.SequenceEqual(before.Transaction.AttemptsUtcTicks),
                "stop was rate-limited or erased exhausted restart history");
            failed.CompleteSafetyStage(failed.Read().Revision, "executor", tx.Generation, tx.RequestId,
                IndependentRecoveryPhase.CooperativeStop, false, now, 30000, "InjectedStopFailure");
            var operations = new PumpOperations();
            new IndependentProjectRecoveryPump(failed, operations, "executor", 30000, 30000).Tick(now + TimeSpan.FromMinutes(2).Ticks);
            Assert(failed.Read().Transaction.Phase == IndependentRecoveryPhase.NeedsAttention && failed.Read().SafetyCleanupPending &&
                operations.Launches == 0 && failed.Read().Transaction.RequestId == tx.RequestId,
                "failed operator stop silently retried or resumed");
        }

        private static void VerifySessionRegistry(string root, long now, IndependentExecutorRegistration registration)
        {
            var registryStore = new IndependentProjectStateStore(Path.Combine(root, "session-registry"));
            registryStore.Update(0, state => { state.Maintenance = false; return true; });
            var registryParent = Identity();
            registryStore.ArmManualRun(1, Intent(root), registryParent, now);
            var savedSafetyPath = registration.SafetyExecutablePath;
            var savedRegistryDirectory = registration.StateDirectory;
            registration.StateDirectory = Path.Combine(root, "session-registry");
            registration.SafetyExecutablePath = Path.Combine(root, "MTTFTest.SafetyAgent.exe");
            var registeredExe = Path.Combine(root, "MTTFTest.Watchdog.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), registeredExe);
            IndependentProcessIdentity registeredIdentity = null;
            using (var registeredWorker = IndependentBoundedWorker.StartSession(registeredExe, "/c exit 0", root,
                (pid, ticks) =>
                {
                    registeredIdentity = new IndependentProcessIdentity { Pid = pid, StartUtcTicks = ticks,
                        ExecutablePath = registeredExe, WindowsSessionId = Process.GetCurrentProcess().SessionId,
                        SessionToken = Guid.NewGuid().ToString("N") };
                    var child = new IndependentSessionProcess { Process = registeredIdentity, Role = "Watchdog",
                        ParentPid = registryParent.Pid, ParentStartUtcTicks = registryParent.StartUtcTicks };
                    registryStore.RegisterSessionProcess(registration, child);
                    var registeredRevision = registryStore.Read().Revision;
                    registryStore.RegisterSessionProcess(registration, child);
                    Assert(registryStore.Read().SessionProcesses.Length == 1 && registryStore.Read().Revision == registeredRevision,
                        "suspended registration was not durable/idempotent");
                    Reject(() => registryStore.AcknowledgeSessionProcessExit(registeredIdentity), "live helper removed from cleanup registry");
                }))
            {
                var deadline = Stopwatch.StartNew();
                while (registeredWorker.Poll() == IndependentWorkerState.Running && deadline.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                Assert(registeredWorker.Poll() == IndependentWorkerState.Completed, "registered session child failed to execute");
            }
            registryStore.AcknowledgeSessionProcessExit(registeredIdentity);
            Assert(registryStore.Read().SessionProcesses.Length == 0, "exited helper was not retired from registry");
            registryStore.BeginRecovery(registryStore.Read().Revision, "executor", now);
            var cleanupState = registryStore.Read();
            cleanupState.Transaction.Phase = IndependentRecoveryPhase.PowerOff;
            var olderChild = new IndependentSessionProcess
            {
                Process = registeredIdentity, Role = "Watchdog", ParentPid = registryParent.Pid,
                ParentStartUtcTicks = registryParent.StartUtcTicks - 1
            };
            cleanupState.SessionProcesses = new[] { olderChild };
            Assert(registration.SelectSessionCleanupTarget(cleanupState, "executor") == olderChild,
                "older parent helper disappeared from installation cleanup");
            Reject(() => registration.SelectSessionCleanupTarget(cleanupState, "other-executor"),
                "foreign executor selected cleanup target");
            cleanupState.SafetyCleanupPending = false;
            Reject(() => registration.SelectSessionCleanupTarget(cleanupState, "executor"),
                "helper selected without active cleanup");
            cleanupState.SafetyCleanupPending = true;
            cleanupState.Transaction.Phase = IndependentRecoveryPhase.CooperativeStop;
            Reject(() => registration.SelectSessionCleanupTarget(cleanupState, "executor"),
                "helper selected before independent takeover");
            cleanupState.Transaction.Phase = IndependentRecoveryPhase.PowerOff;
            var savedChildPath = registeredIdentity.ExecutablePath;
            registeredIdentity.ExecutablePath = Path.Combine(root, "foreign-install", "MTTFTest.Watchdog.exe");
            Reject(() => registration.SelectSessionCleanupTarget(cleanupState, "executor"),
                "foreign installation helper accepted from persisted state");
            registeredIdentity.ExecutablePath = savedChildPath;
            olderChild.Role = "SafetyAgent";
            Reject(() => registration.SelectSessionCleanupTarget(cleanupState, "executor"),
                "persisted role and executable mismatch accepted");
            olderChild.Role = "Watchdog";
            Reject(() =>
            {
                using (var rejectedWorker = IndependentBoundedWorker.StartSession(registeredExe, "/c exit 0", root,
                    (pid, ticks) => registryStore.RegisterSessionProcess(registration, new IndependentSessionProcess
                    {
                        ParentPid = registryParent.Pid, ParentStartUtcTicks = registryParent.StartUtcTicks, Role = "Watchdog",
                        Process = new IndependentProcessIdentity { Pid = pid, StartUtcTicks = ticks, ExecutablePath = registeredExe,
                            WindowsSessionId = Process.GetCurrentProcess().SessionId, SessionToken = Guid.NewGuid().ToString("N") }
                    }))) { }
            }, "new helper executed after cleanup claimed the project");
            registration.SafetyExecutablePath = savedSafetyPath;
            registration.StateDirectory = savedRegistryDirectory;
        }

        internal static int RunSessionRegistryOnly()
        {
            _count = 0;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "session-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var registration = CreateRegistrationFixture(root);
            VerifySessionRegistry(root, DateTime.UtcNow.Ticks, registration);
            return _count;
        }

        private static IndependentExecutorRegistration CreateRegistrationFixture(string root)
        {
            return new IndependentExecutorRegistration
            {
                InstallationId = Guid.NewGuid().ToString("N"), ProjectDirectory = root,
                // Fixture metadata only: this mode never creates an interactive launch task.
                InteractiveUserSid = "S-1-5-21-1-2-3-1001",
                DatabasePath = Path.Combine(root, "index.db"), DatabaseCreationUtcTicks = 1,
                StateDirectory = root, ExecutablePath = Exe, ExecutableSha256 = Hash,
                SafetyExecutablePath = Exe, SafetyExecutableSha256 = Hash, ConfigurationSha256 = Hash, ConfigDirectory = root,
                Files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }
                    .Select(n => new IndependentSafetyConfigFile { Name = n, Sha256 = Hash }).ToArray(),
                Runtime = new SafetyRuntimeSnapshot { SampleRateHz = 2000, SamplesPerChannel = 20,
                    PressureChannels = new[] { "Pressure_1" }, ReleaseSafePressureBar = new[] { 1d },
                    PressureSampleMaxAgeMs = 100, ReleaseStableMs = 300, ReleaseTimeoutMs = 5000 }
            };
        }

        internal static int RunRegistrationSealOnly()
        {
            _count = 0;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "registration-seal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var registration = CreateRegistrationFixture(root);
            registration.ExecutableSha256 = registration.SafetyExecutableSha256 = SupervisorProtocol.ComputeSha256(Exe);
            foreach (var file in registration.Files)
            {
                var path = Path.Combine(root, file.Name);
                File.WriteAllText(path, "<isolated-config />");
                file.Sha256 = SupervisorProtocol.ComputeSha256(path);
            }
            var draft = Path.Combine(root, "draft.json");
            var destination = Path.Combine(root, "registration.json");
            BoundedJson.Write(draft, registration);
            var original = SupervisorProtocol.ComputeSha256(draft);
            var sealedRegistration = IndependentExecutorRegistration.SealDraft(draft, destination);
            Assert(sealedRegistration.ConfigDirectory != root && sealedRegistration.Files.All(file =>
                SupervisorProtocol.ComputeSha256(Path.Combine(sealedRegistration.ConfigDirectory, file.Name)) == file.Sha256),
                "sealed files differ from draft manifest");
            Assert(SupervisorProtocol.ComputeSha256(draft) == original, "sealing mutated the draft");
            var finalHash = SupervisorProtocol.ComputeSha256(destination);
            var rejected = false;
            try { IndependentExecutorRegistration.SealDraft(draft, destination); }
            catch (IOException) { rejected = true; }
            Assert(rejected && SupervisorProtocol.ComputeSha256(destination) == finalHash, "existing registration overwritten");
            File.AppendAllText(Path.Combine(root, registration.Files[0].Name), "tampered");
            var failed = Path.Combine(root, "failed.json");
            rejected = false;
            try { IndependentExecutorRegistration.SealDraft(draft, failed); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected && !File.Exists(failed), "changed source configuration published");
            rejected = false;
            try { IndependentExecutorRegistration.SealDraft(draft, Path.Combine(root, "other-state", "registration.json")); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "registration published outside its state directory");
            return _count;
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
                VerifyTakeoverAudit(root, now);
                VerifyOperatorSafetyStop(root, now);
                Reject(() => IndependentInstallationBinding.RequireControllerAbsent(Exe),
                    "binding admitted live controller executable");
                IndependentInstallationBinding.RequireControllerAbsent(Path.Combine(root, Path.GetFileName(Exe)));
                Assert(true, "unrelated same-name executable blocked installation");
                var legacyExe = Path.Combine(root, "legacy-launch.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), legacyExe);
                var mutexName = "Global\\MTTF-IndependentBinding-" + SupervisorProtocol.ComputeTextSha256(legacyExe.ToUpperInvariant());
                using (var bindingLock = new Mutex(false, mutexName))
                using (var entered = new ManualResetEventSlim(false))
                {
                    bindingLock.WaitOne();
                    System.Threading.Tasks.Task<bool> launchAttempt = null;
                    try
                    {
                        launchAttempt = System.Threading.Tasks.Task.Run(() =>
                        {
                            entered.Set();
                            try
                            {
                                using (var child = IndependentInstallationBinding.StartLegacyProcess(new ProcessStartInfo
                                { FileName = legacyExe, Arguments = "/c exit 0", UseShellExecute = false, CreateNoWindow = true }))
                                {
                                    if (child != null && !child.WaitForExit(3000))
                                    { child.Kill(); child.WaitForExit(3000); }
                                }
                                return false;
                            }
                            catch (Exception error) when (error is InvalidOperationException || error is InvalidDataException ||
                                error is UnauthorizedAccessException) { return true; }
                        });
                        if (!entered.Wait(3000)) throw new Exception("legacy launch worker did not start");
                        Assert(!launchAttempt.Wait(100), "legacy launch ignored installation mutex");
                        // An invalid/new binding must also fail closed. This is
                        // deliberately written after the launch request began.
                        File.WriteAllText(IndependentInstallationBinding.PathFor(legacyExe), "{}");
                    }
                    finally { bindingLock.ReleaseMutex(); }
                    Assert(launchAttempt.Wait(6000) && launchAttempt.Result, "legacy launch did not recheck binding after installation lock");
                }
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
            var verificationDone = Fixture(Path.Combine(root, "verification-target-completion"), now);
            var sessionState = verificationDone.Read();
            var journalProject = IndependentInstallationBinding.ProjectDirectoryFromSessionJournal(
                Path.Combine(root, "WatchdogSessions"));
            IndependentInstallationBinding.RequireSessionHostState(sessionState, root, journalProject,
                sessionState.Controller.Pid, sessionState.Controller.StartUtcTicks);
            Assert(journalProject == Path.GetFullPath(root), "journal directory was treated as project identity");
            var journalRejected = false;
            try { IndependentInstallationBinding.ProjectDirectoryFromSessionJournal(root); }
            catch (InvalidDataException) { journalRejected = true; }
            Assert(journalRejected, "arbitrary directory accepted as session journal");
            Reject(() => IndependentInstallationBinding.RequireSessionHostState(sessionState, root,
                IndependentInstallationBinding.ProjectDirectoryFromSessionJournal(
                    Path.Combine(root, "other-project", "WatchdogSessions")),
                sessionState.Controller.Pid, sessionState.Controller.StartUtcTicks), "different project journal accepted");
            IndependentInstallationBinding.RequireSessionHostState(sessionState, root, root,
                sessionState.Controller.Pid, sessionState.Controller.StartUtcTicks);
            Assert(true, "current session host rejected");
            Reject(() => IndependentInstallationBinding.RequireSessionHostState(sessionState, root, root,
                sessionState.Controller.Pid + 1, sessionState.Controller.StartUtcTicks), "old session parent accepted");
            Reject(() => IndependentInstallationBinding.RequireSessionHostState(sessionState, root, root,
                sessionState.Controller.Pid, sessionState.Controller.StartUtcTicks + 1), "reused parent PID accepted");
            sessionState.SafetyCleanupPending = true;
            Reject(() => IndependentInstallationBinding.RequireSessionHostState(sessionState, root, root,
                sessionState.Controller.Pid, sessionState.Controller.StartUtcTicks), "session respawn admitted during cleanup");
            sessionState.SafetyCleanupPending = false;
            sessionState.Intent.Armed = false; sessionState.Intent.ManualStopped = true;
            IndependentInstallationBinding.RequireSessionHostState(sessionState, root, root,
                sessionState.Controller.Pid + 1, sessionState.Controller.StartUtcTicks + 1);
            Assert(true, "stopped old intent blocked fresh manual process supervision");
            var nearZeroStore = Fixture(Path.Combine(root, "near-zero-retry"), now);
            var nz = nearZeroStore.Read();
            Assert(!nearZeroStore.RecordNearZeroFault(nz.Controller, nz.Intent.RunId, nz.Intent.RunEpoch, 4, false,
                Guid.NewGuid().ToString("N"), now), "first uncertain near zero became permanent");
            Assert(!nearZeroStore.RecordNearZeroFault(nz.Controller, nz.Intent.RunId, nz.Intent.RunEpoch, 4, false,
                Guid.NewGuid().ToString("N"), now), "same run repeated fault consumed independent retry");
            Assert(new IndependentProjectStateStore(Path.Combine(root, "near-zero-retry")).Read().NearZeroRetries.Length == 1,
                "near zero retry was not durable");
            var nzTicket = Ready(nearZeroStore, now);
            Assert(!nearZeroStore.RecordNearZeroFault(nz.Controller, nz.Intent.RunId, nz.Intent.RunEpoch, 4, false,
                Guid.NewGuid().ToString("N"), now), "old process fault during takeover became retry failure");
            var nzConsumer = Identity();
            nearZeroStore.ConsumeLaunchTicket(nearZeroStore.Read().Revision, nzTicket.Nonce, nzConsumer, Hash, now);
            nearZeroStore.CommitReplacementRun(nearZeroStore.Read().Revision, nzConsumer, Guid.NewGuid().ToString("N"), 2, now);
            var nzNew = nearZeroStore.Read();
            Reject(() => nearZeroStore.RecordNearZeroFault(nz.Controller, nz.Intent.RunId, nz.Intent.RunEpoch, 5, false,
                Guid.NewGuid().ToString("N"), now), "old process changed replacement near zero policy");
            Assert(nearZeroStore.RecordNearZeroFault(nzConsumer, nzNew.Intent.RunId, nzNew.Intent.RunEpoch, 4, false,
                Guid.NewGuid().ToString("N"), now), "failure after independent retry did not isolate");
            Assert(nearZeroStore.Read().Intent.PermanentChannels.Contains(4) &&
                !nearZeroStore.Read().Intent.PermanentChannels.Contains(5) && nearZeroStore.Read().Ticket.Revoked,
                "retry failure affected healthy channel or retained launch authority");
            verificationDone.Update(verificationDone.Read().Revision, state =>
            {
                state.Intent.MechanicalTargets = state.Intent.SelectedChannels.Select(channel =>
                    new IndependentMechanicalTarget { Channel = channel, TotalCount = 5 }).ToArray();
                return true;
            });
            var doneTicket = Ready(verificationDone, now);
            var doneConsumer = Identity();
            verificationDone.ConsumeLaunchTicket(verificationDone.Read().Revision, doneTicket.Nonce, doneConsumer, Hash, now);
            verificationDone.CommitReplacementRun(verificationDone.Read().Revision, doneConsumer, Guid.NewGuid().ToString("N"), 2, now);
            verificationDone.Update(verificationDone.Read().Revision, state =>
            {
                IndependentRecoveryTransitions.Advance(state.Transaction, state.Intent, "executor", state.Transaction.Generation,
                    IndependentRecoveryPhase.Verifying, now, 60000, "VerificationFixture");
                return true;
            });
            var doneState = verificationDone.Read();
            var doneCounts = doneState.Transaction.Channels.ToDictionary(channel => channel, channel => channel == 4 ? 5L : 1L);
            verificationDone.RecordVerificationCompletions(doneState.Revision, doneState.Intent.DatabasePath,
                doneState.Intent.DatabaseCreationUtcTicks, doneCounts, now);
            var partialDone = verificationDone.Read();
            IndependentRecoveryTransitions.RequireCurrent(partialDone.Transaction, partialDone.Intent, "executor", partialDone.Transaction.Generation);
            Assert(partialDone.Transaction.Phase == IndependentRecoveryPhase.Verifying && !partialDone.Ticket.Revoked &&
                !partialDone.Intent.RecoveryChannels().Contains(4) && partialDone.Intent.RecoveryChannels().Contains(5),
                "partial target completion disrupted healthy verification or retained completed target");
            Reject(() => verificationDone.RecordVerificationCompletions(doneState.Revision, doneState.Intent.DatabasePath,
                doneState.Intent.DatabaseCreationUtcTicks, doneCounts, now), "late completion overwrote authority");
            var regressedCounts = doneCounts.Keys.ToDictionary(channel => channel, channel => channel == 4 ? 4L : 5L);
            Reject(() => verificationDone.RecordVerificationCompletions(partialDone.Revision, doneState.Intent.DatabasePath,
                doneState.Intent.DatabaseCreationUtcTicks, regressedCounts, now), "completed mechanical count regression accepted");
            var partialProof = verificationDone.Read();
            IndependentRecoveryTransitions.Advance(partialProof.Transaction, partialProof.Intent, "executor",
                partialProof.Transaction.Generation, IndependentRecoveryPhase.Verified, now, 10000, "RemainingChannelsProofFixture");
            Assert(partialProof.Transaction.Phase == IndependentRecoveryPhase.Verified,
                "completed target prevented remaining channels from reaching verified state");
            verificationDone.RecordVerificationCompletions(partialDone.Revision, doneState.Intent.DatabasePath,
                doneState.Intent.DatabaseCreationUtcTicks, doneCounts.Keys.ToDictionary(channel => channel, channel => 5L), now + 1);
            var allDone = verificationDone.Read();
            Assert(allDone.SafetyCleanupPending && allDone.Ticket.Revoked && allDone.Controller.Matches(doneConsumer) &&
                allDone.Transaction.Phase == IndependentRecoveryPhase.CooperativeStop && allDone.Transaction.AttemptsUtcTicks.Length == 1,
                "target completion skipped exact replacement cleanup or charged a new restart");
            foreach (var stage in new[] { IndependentRecoveryPhase.CooperativeStop, IndependentRecoveryPhase.PowerOff,
                IndependentRecoveryPhase.RetireControls, IndependentRecoveryPhase.OutputsSafe })
                verificationDone.CompleteSafetyStage(verificationDone.Read().Revision, "executor", allDone.Transaction.Generation,
                    allDone.Transaction.RequestId, stage, true, now + 2, 30000, "SimulatedCompletionCleanup");
            Assert(!verificationDone.Read().SafetyCleanupPending && !verificationDone.Read().Intent.Armed &&
                verificationDone.Read().Transaction.Phase == IndependentRecoveryPhase.Cancelled &&
                verificationDone.Read().Transaction.Detail == "TargetsCompleted;SafetyCleanupCompleted;NoRestart",
                "final completion became recovery success or permitted restart");
            var completedStore = Fixture(Path.Combine(root, "database-completion"), now);
            completedStore.Update(completedStore.Read().Revision, state =>
            {
                state.Intent.MechanicalTargets = state.Intent.SelectedChannels.Select(channel =>
                    new IndependentMechanicalTarget { Channel = channel, TotalCount = 5 }).ToArray();
                return true;
            });
            var completionState = completedStore.Read();
            var completionCounts = new System.Collections.Generic.Dictionary<int, long> { [4] = 5, [5] = 4 };
            Reject(() => completedStore.RecordDatabaseCompletions(completionState.Revision, completionState.Intent.DatabasePath,
                completionState.Intent.DatabaseCreationUtcTicks + 1, completionCounts, now), "foreign database completed channels");
            completedStore.RecordDatabaseCompletions(completionState.Revision, completionState.Intent.DatabasePath,
                completionState.Intent.DatabaseCreationUtcTicks, completionCounts, now);
            Assert(completedStore.Read().Intent.CompletedChannels.SequenceEqual(new[] { 4 }) &&
                completedStore.Read().Intent.RecoveryChannels().Contains(5), "completion stopped unfinished peer or retained completed lane");
            Reject(() => completedStore.RecordDatabaseCompletions(completionState.Revision, completionState.Intent.DatabasePath,
                completionState.Intent.DatabaseCreationUtcTicks, completionCounts, now), "stale completion overwrote run state");
            completedStore.RecordDatabaseCompletions(completedStore.Read().Revision, completionState.Intent.DatabasePath,
                completionState.Intent.DatabaseCreationUtcTicks, completionState.Intent.SelectedChannels.ToDictionary(channel => channel, channel => 5L), now);
            Assert(!completedStore.Read().Intent.Armed && completedStore.Read().Intent.RecoveryChannels().Length == 0 &&
                completedStore.Read().Transaction == null, "completed project restarted or falsely claimed recovery verification");
            var permanentRoot = Path.Combine(root, "controller-permanent");
            var permanentStore = Fixture(permanentRoot, now);
            var permanentState = permanentStore.Read();
            var exclusionCommand = Guid.NewGuid().ToString("N");
            permanentStore.RecordControllerPermanentExclusion(permanentState.Controller, permanentState.Intent.RunId,
                permanentState.Intent.RunEpoch, new[] { 4, 5 }, "ConfirmedPermanent", exclusionCommand, now);
            Assert(permanentStore.Read().Intent.RecoveryChannels().SequenceEqual(new[] { 7, 8, 9, 12 }),
                "permanent cohort was not atomically excluded from restart");
            var excludedRevision = permanentStore.Read().Revision;
            permanentStore.RecordControllerPermanentExclusion(permanentState.Controller, permanentState.Intent.RunId,
                permanentState.Intent.RunEpoch, new[] { 5, 4 }, "ConfirmedPermanent", exclusionCommand, now);
            Assert(permanentStore.Read().Revision == excludedRevision, "duplicate permanent cohort changed authority");
            Reject(() => permanentStore.RecordControllerPermanentExclusion(Identity(), permanentState.Intent.RunId,
                permanentState.Intent.RunEpoch, new[] { 7 }, "WrongOwner", exclusionCommand, now), "foreign permanent exclusion accepted");
            permanentStore.RecordControllerManualStop(permanentState.Controller, permanentState.Intent.RunId,
                permanentState.Intent.RunEpoch, Guid.NewGuid().ToString("N"), now);
            var nextPermanentIntent = Intent(permanentRoot); nextPermanentIntent.RunEpoch = permanentState.Intent.RunEpoch + 1;
            permanentStore.ArmManualRun(permanentStore.Read().Revision, nextPermanentIntent, permanentState.Controller, now);
            Assert(permanentStore.Read().Intent.PermanentChannels.SequenceEqual(new[] { 4, 5 }),
                "ordinary restart cleared permanent isolation");
            Reject(() => permanentStore.RecordControllerPermanentExclusion(permanentState.Controller, permanentState.Intent.RunId,
                permanentState.Intent.RunEpoch, new[] { 7 }, "OldRun", exclusionCommand, now), "old run excluded new batch");
            Reject(() => permanentStore.ResetPermanentExclusionForOperator(permanentStore.Read().Revision, Identity(),
                4, true, Guid.NewGuid().ToString("N"), now), "unbound process reset active run isolation");
            var resetRevision = permanentStore.Read().Revision;
            permanentStore.ResetPermanentExclusionForOperator(resetRevision, permanentState.Controller,
                4, true, Guid.NewGuid().ToString("N"), now);
            Assert(permanentStore.Read().Intent.PermanentChannels.SequenceEqual(new[] { 5 }) &&
                permanentStore.Read().Intent.PausedChannels.Contains(4) && !permanentStore.Read().Intent.RecoveryChannels().Contains(4),
                "manual isolation reset silently resumed lane");
            Reject(() => permanentStore.ResetPermanentExclusionForOperator(resetRevision, permanentState.Controller,
                5, true, Guid.NewGuid().ToString("N"), now), "stale reset overwrote newer authority");
            permanentStore.BeginRecovery(permanentStore.Read().Revision, "executor", now);
            Reject(() => permanentStore.ResetPermanentExclusionForOperator(permanentStore.Read().Revision, permanentState.Controller,
                5, true, Guid.NewGuid().ToString("N"), now), "operator reset crossed active takeover");
            var resetCold = Fixture(Path.Combine(root, "reset-stopped-installation"), now);
            var resetColdState = resetCold.Read();
            resetCold.RecordControllerPermanentExclusion(resetColdState.Controller, resetColdState.Intent.RunId,
                resetColdState.Intent.RunEpoch, new[] { 4 }, "Permanent", exclusionCommand, now);
            resetCold.RecordControllerManualStop(resetColdState.Controller, resetColdState.Intent.RunId,
                resetColdState.Intent.RunEpoch, Guid.NewGuid().ToString("N"), now);
            resetCold.ResetPermanentExclusionForOperator(resetCold.Read().Revision, Identity(),
                4, false, Guid.NewGuid().ToString("N"), now);
            Assert(!resetCold.Read().Intent.PermanentChannels.Contains(4) && resetCold.Read().Intent.ManualStopped &&
                !resetCold.Read().Intent.Armed && !resetCold.Read().Intent.SelectedChannels.Contains(4),
                "stopped installation reset lost manual stop or selected excluded lane");
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
            VerifyControllerChannelPause(root, now);
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
            VerifySessionRegistry(root, now, registration);
            var registrationPath = Path.Combine(root, "executor.json");
            var binding = new IndependentInstallationBinding
            { InstallationId = registration.InstallationId, RegistrationPath = registrationPath };
            binding.Validate(registration, registration.ExecutablePath);
            Assert(true, "matching installed executable must bind its registered authority");
            foreach (var mismatch in new Action[] {
                () => binding.Validate(registration, Path.Combine(root, "different.exe")),
                () => new IndependentInstallationBinding { InstallationId = Guid.NewGuid().ToString("N"), RegistrationPath = registrationPath }.Validate(registration, Exe),
                () => new IndependentInstallationBinding { InstallationId = registration.InstallationId, RegistrationPath = Path.Combine(root, "other", "executor.json") }.Validate(registration, Exe)
            })
            {
                var failedBinding = false;
                try { mismatch(); } catch (InvalidDataException) { failedBinding = true; }
                Assert(failedBinding, "installation binding crossed executable, installation or state directory");
            }
            Assert(IndependentInstallationBinding.Resolve(Path.Combine(root, "unmanaged.exe")) == null,
                "unmanaged installation was silently enrolled");
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
            var pendingNearZero = pumpStore.Read();
            pumpStore.RecordNearZeroFault(pendingNearZero.Controller, pendingNearZero.Intent.RunId,
                pendingNearZero.Intent.RunEpoch, 4, false, Guid.NewGuid().ToString("N"), now);
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
            Assert(pumpStore.Read().NearZeroRetries.Length == 1, "launch alone erased near zero retry history");
            pumpStore.CommitReplacementRun(pumpStore.Read().Revision, consumer, Guid.NewGuid().ToString("N"), 2, now + 3);
            pump.Tick(now + 4);
            Assert(pumpStore.Read().Transaction.Phase == IndependentRecoveryPhase.Verified && operations.Verifications == 1,
                "verified replacement could not complete transaction");
            Assert(pumpStore.Read().NearZeroRetries.Length == 0, "verified recovery did not close near zero incident");
            var verifiedRun = pumpStore.Read();
            pumpStore.SetControllerChannelPause(consumer, verifiedRun.Intent.RunId, verifiedRun.Intent.RunEpoch,
                7, true, Guid.NewGuid().ToString("N"), now + 5);
            Assert(pumpStore.Read().Intent.RecoveryChannels().Length == 5 && !pumpStore.Read().Ticket.Revoked &&
                pumpStore.Read().Transaction.IntentRevision == pumpStore.Read().Intent.Revision,
                "single pause invalidated verified healthy controller binding");
            pumpStore.SetControllerChannelPause(consumer, verifiedRun.Intent.RunId, verifiedRun.Intent.RunEpoch,
                7, false, Guid.NewGuid().ToString("N"), now + 6);
            Assert(pumpStore.Read().Intent.RecoveryChannels().Length == 6 &&
                pumpStore.Read().Transaction.IntentRevision == pumpStore.Read().Intent.Revision &&
                pumpStore.Read().Controller.Matches(consumer), "channel continue failed to preserve verified binding");
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
