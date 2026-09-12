using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class RecoveryGuardSupervisorTests
    {
        internal static int RunAll()
        {
            WireBoundaries();
            PipeOwnerCannotBeClaimedInPayload();
            EvidenceBinding();
            MissingOrRevokedPreparedAuthority();
            CancelPartialResponseAsync().GetAwaiter().GetResult();
            FragmentedResponseAsync().GetAwaiter().GetResult();
            PipeOwnerCannotBeClaimedInPayload(true);
            GuardSafetyPreparationEvidence();
            PipeOwnerCannotBeClaimedInPayload(safetyExecution: true);
            GuardSafetyExecutionEvidence();
            SupervisorPeerMustMatchService();
            LaunchPreparationChannelScope();
            GuardExitFailureCreatesBoundPermit();
            LegacyHalfOpenAndLaunchRespectTakeover();
            GuardHalfOpenEvidenceAndVersion();
            StrictGuardLaunchLifecycle();
            Console.WriteLine("PASS RecoveryGuard Supervisor 16/16 有界协议、双向进程身份、安全准备/执行、严格启动生命周期、故障登记及启动范围冻结、半开互斥与证据");
            return 16 + SafetyAgentReconciliationTests.RunAll();
        }

        private static void StrictGuardLaunchLifecycle()
        {
            var directory = Path.Combine(Path.GetTempPath(), "EPB-Guard-StrictLaunch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var f = new Fixture();
                var request = new RecoveryGuardSafetyPrepareRequest
                {
                    RequestId = f.Request.RequestId, TransactionId = f.Request.TransactionId,
                    Epoch = f.Request.Epoch, Owner = f.Request.Owner
                };
                f.State.Transaction = new RecoveryTakeoverTransaction
                {
                    TransactionId = request.TransactionId, Epoch = request.Epoch,
                    Owner = request.Owner, Stage = RecoveryStage.SafeStop
                };
                var opened = DurableRelaunchAuthorityFactory.TryCreatePristine(
                    directory, f.State.Intent.WatchdogSessionId,
                    request.Owner.ProcessId, request.Owner.StartUtcTicks, 8);
                Check(opened.Succeeded, "strict Guard launch bootstrap");
                var operation = SupervisorGuardPermitPolicy.BuildFailure(
                    request, f.State, opened.Store, ProcessObservation.Exited);
                var approved = opened.Authority.RegisterFailureAndDecide(
                    operation, opened.Store.Revision, opened.Store.Sha256);
                Check(approved.ActionAllowed, "strict Guard launch approval");
                var executable = request.Owner.ExecutablePath;
                var executableSha = SupervisorProtocol.ComputeSha256(executable);
                var intent = new DurableLaunchIntent
                {
                    SessionId = operation.SessionId, SessionNonce = operation.SessionNonce,
                    Generation = approved.Record.Generation, PermitId = approved.Record.PermitId,
                    PermitNonce = approved.Record.PermitNonce, IntentId = Guid.NewGuid().ToString("N"),
                    ExecutablePath = executable, ExecutableSha256 = executableSha,
                    Arguments = "--strict-guard-fixture", WorkingDirectory = Path.GetDirectoryName(executable),
                    LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
                };
                intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
                var prepared = opened.Authority.PrepareLaunchIntent(intent);
                Check(prepared.Succeeded, "strict Guard launch intent");
                var context = SupervisorGuardStrictLaunchLifecycle.Consume(
                    directory, operation.SessionId, intent.IntentId, intent.Generation,
                    intent.PermitId, prepared.Capability.AuthorityRevision,
                    prepared.Capability.AuthoritySha256, executable, executableSha);
                var consumed = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId)
                    .Load(operation.SessionId).Record;
                Check(consumed.State == DurableRelaunchPermitState.LaunchIntent && consumed.LaunchConsumed,
                    "strict launch consumed before OS boundary");
                SupervisorGuardStrictLaunchLifecycle.CommitStarted(
                    context, request.Owner.ProcessId, request.Owner.StartUtcTicks);
                var started = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId)
                    .Load(operation.SessionId).Record;
                Check(started.State == DurableRelaunchPermitState.Started &&
                    started.ProcessId == request.Owner.ProcessId &&
                    started.ProcessStartUtcTicks == request.Owner.StartUtcTicks,
                    "strict launch records exact created process");
                try
                {
                    SupervisorGuardStrictLaunchLifecycle.Consume(
                        directory, operation.SessionId, intent.IntentId, intent.Generation,
                        intent.PermitId, prepared.Capability.AuthorityRevision,
                        prepared.Capability.AuthoritySha256, executable, executableSha);
                    throw new Exception("consumed strict launch replayed");
                }
                catch (IOException ex) when (ex.Message.Contains("ConsumeFailed")) { }
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }

        private static void GuardHalfOpenEvidenceAndVersion()
        {
            var directory = Path.Combine(Path.GetTempPath(), "EPB-Guard-HalfOpen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var f = new Fixture();
                var request = new RecoveryGuardSafetyPrepareRequest { RequestId = f.Request.RequestId,
                    TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch, Owner = f.Request.Owner };
                f.State.Transaction = new RecoveryTakeoverTransaction { TransactionId = request.TransactionId,
                    Epoch = request.Epoch, Owner = request.Owner, Stage = RecoveryStage.SafeStop, CircuitCooldownSeconds = 600 };
                var opened = DurableRelaunchAuthorityFactory.TryCreatePristine(directory, f.State.Intent.WatchdogSessionId,
                    request.Owner.ProcessId, request.Owner.StartUtcTicks, 1);
                Check(opened.Succeeded, "half-open bootstrap");
                var failure = SupervisorGuardPermitPolicy.BuildFailure(request, f.State, opened.Store, ProcessObservation.Exited);
                var blocked = opened.Authority.RegisterFailureAndDecide(failure);
                Check(blocked.Durable && blocked.Record.CircuitOpen, "fixture reaches actual budget circuit");
                var store = new DurableRelaunchAuthorityFileStore(directory, failure.SessionId);
                var source = store.Load(failure.SessionId);
                var due = new DateTime(source.Record.LastFailureDecisionUtcTicks, DateTimeKind.Utc).AddSeconds(600);
                Action<DateTime, ProcessObservation> validate = (now, observation) =>
                    SupervisorGuardPermitPolicy.ValidateHalfOpen(request, f.State, source, observation, now);
                try { validate(due.AddTicks(-1), ProcessObservation.Exited); throw new Exception("early half-open accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("CoolingDown")) { }
                validate(due, ProcessObservation.Exited);
                Reject(() => validate(due, ProcessObservation.ExactAlive), "RequiresReconciliation");
                source.Record.LastFailurePermanent = true;
                Reject(() => validate(due, ProcessObservation.Exited), "RequiresReconciliation");
                source.Record.LastFailurePermanent = false;
                f.State.Intent.DesiredState = RecoveryDesiredState.Stopped;
                Reject(() => validate(due, ProcessObservation.Exited), "RequiresReconciliation");
                f.State.Intent.DesiredState = RecoveryDesiredState.Run;
                f.State.Launches.Add(new RecoveryLaunchReservation { State = "Consumed" });
                Reject(() => validate(due, ProcessObservation.Exited), "RequiresReconciliation");
                f.State.Launches.Clear();
                source.Record.LaunchConsumed = true;
                Reject(() => validate(due, ProcessObservation.Exited), "MainIdentityUnproven");
                source.Record.LaunchConsumed = false;
                var stale = store.TryOpenAutomaticHalfOpen(source.Record.LastFailureFingerprint,
                    source.Record.ConsecutiveFailures, source.Revision - 1, source.Sha256);
                Check(stale.Status == DurableAuthorityCommitStatus.Conflict && store.Load(failure.SessionId).Revision == source.Revision,
                    "stale revision cannot reopen or change history");
                var changedHash = store.TryOpenAutomaticHalfOpen(source.Record.LastFailureFingerprint,
                    source.Record.ConsecutiveFailures, source.Revision, new string('0', 64));
                Check(changedHash.Status == DurableAuthorityCommitStatus.Conflict, "mismatched hash cannot reopen");
                var result = store.TryOpenAutomaticHalfOpen(source.Record.LastFailureFingerprint,
                    source.Record.ConsecutiveFailures, source.Revision, source.Sha256);
                Check(result.Status == DurableAuthorityCommitStatus.CandidateApplied &&
                    result.Record.Generation > source.Record.Generation && result.Record.PermitId != source.Record.PermitId &&
                    result.Record.ConsecutiveFailures == source.Record.ConsecutiveFailures,
                    "half-open creates fresh permit without resetting cumulative failures");
                var replay = store.TryOpenAutomaticHalfOpen(source.Record.LastFailureFingerprint,
                    source.Record.ConsecutiveFailures, source.Revision, source.Sha256);
                Check(replay.Status == DurableAuthorityCommitStatus.Conflict && store.Load(failure.SessionId).Revision == result.Revision,
                    "lost response retry cannot mint another permit from stale source");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static void LegacyHalfOpenAndLaunchRespectTakeover()
        {
            foreach (var takeover in new[] { false, true })
            foreach (var approved in new[] { false, true })
            {
                var root = Path.Combine(Path.GetTempPath(), "EPB-Legacy-Permit-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var main = RecoveryProcessProbe.Current();
                    var session = Guid.NewGuid().ToString("N");
                    var run = Guid.NewGuid().ToString("N");
                    var now = DateTime.UtcNow.AddMinutes(-3);
                    var control = new RecoveryControlStore(Path.Combine(root, "control"));
                    control.Register("test", main.ExecutablePath);
                    var token = control.BeginManualRun(run, run, "config", main, now);
                    control.BindWatchdogSession(token, run, main, session, now);
                    var settings = new RecoveryGuardSettings { Mode = RecoveryGuardMode.RecoverStalled };
                    var snapshot = new RecoveryObservationSnapshot
                    {
                        Authorization = token, RunId = run, ConfigurationIdentity = "config", MainProcess = main,
                        Sequence = 1, SourceVersion = 1, SourceAvailable = true,
                        PublishedUtcTicks = now.Ticks, SourceUtcTicks = now.Ticks,
                        Channels = new[] { new RecoveryChannelProgress { Channel = 1, Eligible = true,
                            SampleSequence = 10, ControlSequence = 10, PersistedSequence = 10, Stage = "Formal" } }
                    };
                    control.Observe(snapshot, ProcessObservation.ExactAlive, main.BootId, now, settings, false);
                    now = now.AddMinutes(1);
                    snapshot.Sequence++; snapshot.SourceVersion++;
                    snapshot.PublishedUtcTicks = snapshot.SourceUtcTicks = now.Ticks;
                    snapshot.Channels[0].SampleSequence++;
                    snapshot.Channels[0].ControlSequence++;
                    snapshot.Channels[0].PersistedSequence++;
                    control.Observe(snapshot, ProcessObservation.ExactAlive, main.BootId, now, settings, false);
                    var project = Path.Combine(root, "project");
                    var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(project, session,
                        main.ProcessId, main.StartUtcTicks, 1);
                    Check(bootstrap.Succeeded, "strict fixture bootstrap");
                    var operation = new RecoveryFailureOperation
                    {
                        OperationId = Guid.NewGuid().ToString("N"), RequestCorrelationId = Guid.NewGuid().ToString("N"),
                        RequestPayloadSha256 = new string('A', 64), SessionId = session, SessionNonce = new string('b', 32),
                        SidecarProcessId = main.ProcessId, SidecarProcessStartUtcTicks = main.StartUtcTicks,
                        ConnectionGeneration = 1, RecoveryAttemptGeneration = 1, RunId = run, RunEpoch = 1,
                        FailureCode = "MainLaunchFailed", FailureFingerprint = "fixture", DetailCode = "MainLaunchFailed",
                        RecoveryStage = "Recovery", RecoveryProgressToken = "p1", RecoveryProcessSource = "StrictHost",
                        DeviceOrChannelGroup = "Host", MaximumProcessRelaunches = 1
                    };
                    var failure = bootstrap.Authority.RegisterFailureAndDecide(operation);
                    Check(failure.Durable && failure.Record.CircuitOpen, "one failure exhausts fixture budget");
                    var adapter = new StrictHostV4AuthorityAdapter(new WatchdogArguments
                    {
                        SessionId = session, PipeName = "fixture", ExecutablePath = main.ExecutablePath,
                        JournalDirectory = project, SidecarInstanceNonce = new string('b', 32), JournalPolicy = new WatchdogJournalPolicy()
                    }, main.ProcessId, main.StartUtcTicks, control);
                    if (approved)
                        Check(adapter.TryAutomaticHalfOpen("fixture", 1).Succeeded, "normal half-open preserves retry ability");
                    if (takeover)
                    {
                        for (var i = 0; i < 2; i++)
                        {
                            now = now.AddMinutes(1);
                            snapshot.Sequence++; snapshot.SourceVersion++;
                            snapshot.PublishedUtcTicks = snapshot.SourceUtcTicks = now.Ticks;
                            control.Observe(snapshot, ProcessObservation.Exited, main.BootId, now, settings, false);
                        }
                        control.Claim(snapshot, ProcessObservation.Exited, main, now, settings, false);
                    }
                    var before = new DurableRelaunchAuthorityFileStore(project, session).Load(session).Record;
                    var result = approved ? adapter.BeginLaunch(adapter.Snapshot.Identity, "--fixture") : adapter.TryAutomaticHalfOpen("fixture", 1);
                    if (takeover)
                    {
                        Check(!result.ActionAllowed && result.TransitionStatus == DurableAuthorityTransitionStatus.Busy,
                            "takeover defers legacy launch/half-open");
                        var persisted = new DurableRelaunchAuthorityFileStore(project, session).Load(session).Record;
                        Check(persisted.AuthorityRevision == before.AuthorityRevision && persisted.State == before.State &&
                            persisted.ConsecutiveFailures == before.ConsecutiveFailures, "denial leaves strict authority unchanged");
                        if (approved)
                        {
                            var tx = control.Read().Transaction;
                            var safetyId = Guid.NewGuid().ToString("N");
                            Action<RecoveryStage, RecoveryStage> advance = (from, to) => control.Advance(tx.TransactionId,
                                tx.Epoch, main, from, to, "fixture", DateTime.UtcNow, settings);
                            advance(RecoveryStage.Claim, RecoveryStage.SafeStop);
                            control.BindRecoveryAction(tx.TransactionId, tx.Epoch, main, RecoveryStage.SafeStop, safetyId, null, DateTime.UtcNow);
                            advance(RecoveryStage.SafeStop, RecoveryStage.Retire);
                            advance(RecoveryStage.Retire, RecoveryStage.Launch);
                            var external = DurableRelaunchAuthorityV4Factory.TryOpenExisting(project, session).Authority;
                            var intent = new DurableLaunchIntent
                            {
                                SessionId = session, SessionNonce = operation.SessionNonce, Generation = persisted.Generation,
                                PermitId = persisted.PermitId, PermitNonce = persisted.PermitNonce,
                                IntentId = Guid.NewGuid().ToString("N"), ExecutablePath = main.ExecutablePath,
                                ExecutableSha256 = SupervisorProtocol.ComputeSha256(main.ExecutablePath), Arguments = "--fixture",
                                WorkingDirectory = root, LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
                            };
                            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
                            var prepared = external.PrepareLaunchIntent(intent);
                            Check(prepared.Succeeded && external.ConsumeLaunchIntent(prepared.Capability).Succeeded &&
                                external.CommitStarted(prepared.Capability, main.ProcessId, main.StartUtcTicks).Succeeded,
                                "external authority records a created instance independently of adapter cache");
                            string denied;
                            Check(!adapter.TryRefreshGuardCreatedProcess(persisted.Generation, persisted.PermitId, persisted.PermitNonce,
                                main.ProcessId, main.StartUtcTicks, out denied), "strict Started alone cannot authorize attachment");
                            var currentToken = control.Read().Token();
                            var issued = DateTime.UtcNow;
                            control.ReserveLaunch(currentToken, intent.IntentId, issued, issued.AddMinutes(1));
                            control.BindRecoveryAction(tx.TransactionId, tx.Epoch, main, RecoveryStage.Launch, safetyId, intent.IntentId, issued);
                            control.ConsumeLaunch(currentToken, intent.IntentId, issued);
                            control.RecordLaunchResult(intent.IntentId, main, false);
                            var revisionBeforeRefresh = new DurableRelaunchAuthorityFileStore(project, session).Load(session).Revision;
                            Check(!adapter.TryRefreshGuardCreatedProcess(persisted.Generation, persisted.PermitId, persisted.PermitNonce,
                                main.ProcessId, main.StartUtcTicks, out denied), "missing prior safety history cannot be fabricated by attachment");
                            foreach (var stage in new[] { RecoveryReplacementState.Approved, RecoveryReplacementState.OldProcessExitProven,
                                RecoveryReplacementState.SafetyAgentRunning, RecoveryReplacementState.SafetyCompleted, RecoveryReplacementState.MainLaunchIntent })
                                Check(RecoveryReplacementTransactionStore.Advance(project, session, persisted.Generation,
                                    persisted.PermitId, stage, "fixture verified event").Succeeded, "fixture supplies prerequisite history");
                            Check(!adapter.TryRefreshGuardCreatedProcess(persisted.Generation, persisted.PermitId, persisted.PermitNonce,
                                main.ProcessId + 1, main.StartUtcTicks, out denied), "wrong attached PID rejected");
                            Check(adapter.TryRefreshGuardCreatedProcess(persisted.Generation, persisted.PermitId, persisted.PermitNonce,
                                main.ProcessId, main.StartUtcTicks, out denied), "shared exact creation refreshes the old adapter");
                            Check(new DurableRelaunchAuthorityFileStore(project, session).Load(session).Revision == revisionBeforeRefresh,
                                "refresh reads authority without mutating its revision");
                            Check(adapter.CommitAttached(adapter.Snapshot.Identity, main.ProcessId, main.StartUtcTicks).Succeeded,
                                "refreshed capability commits the exact attached instance");
                            control.SetOperatorIntent(currentToken.AuthorizationId, currentToken.IntentVersion, RecoveryDesiredState.Stopped, "fixture");
                            Check(!adapter.TryRefreshGuardCreatedProcess(persisted.Generation, persisted.PermitId, persisted.PermitNonce,
                                main.ProcessId, main.StartUtcTicks, out denied), "stop blocks even a previously refreshed attachment");
                        }
                    }
                    else Check(result.Succeeded, "ordinary legacy transition remains supported");
                }
                finally
                {
                    // root is a newly allocated, fixed child of the temporary directory.
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
        }

        private static void GuardExitFailureCreatesBoundPermit()
        {
            var directory = Path.Combine(Path.GetTempPath(), "EPB-GuardPermit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var f = new Fixture();
                var request = new RecoveryGuardSafetyPrepareRequest { RequestId = f.Request.RequestId,
                    TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch, Owner = f.Request.Owner };
                f.State.InstallationId = Guid.NewGuid().ToString("N");
                f.State.Transaction = new RecoveryTakeoverTransaction { TransactionId = request.TransactionId,
                    Epoch = request.Epoch, Owner = request.Owner, Stage = RecoveryStage.SafeStop };
                var opened = DurableRelaunchAuthorityFactory.TryCreatePristine(directory, f.State.Intent.WatchdogSessionId,
                    request.Owner.ProcessId, request.Owner.StartUtcTicks, 8);
                Check(opened.Succeeded, "isolated strict bootstrap must succeed");
                var before = opened.Store;
                var operation = SupervisorGuardPermitPolicy.BuildFailure(request, f.State, before, ProcessObservation.Exited);
                Check(operation.SidecarProcessId == request.Owner.ProcessId && operation.RecoveryProcessSource == "RecoveryGuard" &&
                    operation.MaximumProcessRelaunches == 8, "report actual Guard identity and preserve frozen budget");
                Reject(() => SupervisorGuardPermitPolicy.BuildFailure(request, f.State, before, ProcessObservation.ExactAlive), "RequiresReconciliation");
                f.State.Launches.Add(new RecoveryLaunchReservation { State = "Consumed" });
                Reject(() => SupervisorGuardPermitPolicy.BuildFailure(request, f.State, before, ProcessObservation.Exited), "RequiresReconciliation");
                f.State.Launches.Clear();
                var result = opened.Authority.RegisterFailureAndDecide(operation, before.Revision, before.Sha256);
                Check(result.Durable && result.ActionAllowed && result.Record.State == DurableRelaunchPermitState.Approved,
                    "exact-exit registration can mint the first recovery permit");
                var revision = result.Record.AuthorityRevision;
                var stale = opened.Authority.RegisterFailureAndDecide(operation, before.Revision, before.Sha256);
                Check(!stale.ActionAllowed && stale.Reason.Contains("AuthorityChangedBeforeFailureRegistration"),
                    "stale caller must not consume failure budget or replace a concurrent permit");
                var current = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId).Load(operation.SessionId);
                Check(current.Revision == revision && current.Record.ConsecutiveFailures == 1,
                    "stale approval is read-only and consumes no extra budget");
                var intent = new DurableLaunchIntent
                {
                    SessionId = operation.SessionId, SessionNonce = operation.SessionNonce,
                    Generation = current.Record.Generation, PermitId = current.Record.PermitId, PermitNonce = current.Record.PermitNonce,
                    IntentId = Guid.NewGuid().ToString("N"), ExecutablePath = request.Owner.ExecutablePath,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(request.Owner.ExecutablePath), Arguments = "--fixture",
                    WorkingDirectory = Path.GetDirectoryName(request.Owner.ExecutablePath),
                    LaunchOptionsCanonical = DurableLaunchCanonical.RequiredOptionsCanonical
                };
                intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
                var prepared = opened.Authority.PrepareLaunchIntent(intent);
                Check(prepared.Succeeded, "fixture prepares strict intent");
                f.State.Intent.AuthorizationId = Guid.NewGuid().ToString("N");
                f.State.Intent.IntentVersion = 1;
                f.State.LastTakeoverEpoch = request.Epoch;
                f.State.Transaction.AuthorizationId = f.State.Intent.AuthorizationId;
                f.State.Transaction.IntentVersion = f.State.Intent.IntentVersion;
                f.State.Transaction.ActionEpoch = request.Epoch;
                f.State.Transaction.LaunchOperationId = intent.IntentId;
                var neverCreated = new RecoveryLaunchReservation { OperationId = intent.IntentId,
                    Authorization = f.State.Token(), State = "StartFailed" };
                f.State.Launches.Add(neverCreated);
                var intentSource = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId).Load(operation.SessionId);
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource), "ProofMissing");
                neverCreated.FailureEvidence = "ReservationExpiredBeforeConsumption";
                SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, f.State, current);
                neverCreated.State = "Reserved";
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, f.State, current), "RequiresReconciliation");
                neverCreated.State = "StartFailed";
                var approvedSource = current.Record.Clone();
                current.Record.LaunchConsumed = true;
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, f.State, current), "RequiresReconciliation");
                current.Record.LaunchConsumed = approvedSource.LaunchConsumed;
                SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource);
                request.Epoch++;
                f.State.Transaction.Epoch = f.State.LastTakeoverEpoch = request.Epoch;
                Check(!f.State.Matches(neverCreated.Authorization), "adoption fences an unconsumed historical token");
                SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, f.State, current);
                SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource);
                f.State.Transaction.LaunchOperationId = Guid.NewGuid().ToString("N");
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource), "ProofMissing");
                f.State.Transaction.LaunchOperationId = intent.IntentId;
                request.Epoch--;
                f.State.Transaction.Epoch = f.State.LastTakeoverEpoch = request.Epoch;
                neverCreated.State = "Consumed";
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource), "RequiresReconciliation");
                neverCreated.State = "StartFailed";
                Check(opened.Authority.ConsumeLaunchIntent(prepared.Capability).Succeeded, "fixture consumes strict intent without creating a process");
                intentSource = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId).Load(operation.SessionId);
                SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource);
                f.State.Intent.DesiredState = RecoveryDesiredState.Stopped;
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnpreparedLaunch(request, f.State, current), "RequiresReconciliation");
                Reject(() => SupervisorGuardPermitPolicy.ValidateUnconsumedLaunch(request, f.State, intentSource), "RequiresReconciliation");
                f.State.Intent.DesiredState = RecoveryDesiredState.Run;
                f.State.Launches.Clear();
                Check(opened.Authority.CommitStarted(prepared.Capability, request.Owner.ProcessId, request.Owner.StartUtcTicks).Succeeded,
                    "fixture records exact strict instance");
                current = new DurableRelaunchAuthorityFileStore(directory, operation.SessionId).Load(operation.SessionId);
                f.State.Intent.AuthorizationId = Guid.NewGuid().ToString("N");
                f.State.Intent.IntentVersion = 1;
                f.State.LastTakeoverEpoch = request.Epoch;
                f.State.Transaction.AuthorizationId = f.State.Intent.AuthorizationId;
                var launch = new RecoveryLaunchReservation { OperationId = intent.IntentId, Authorization = f.State.Token(),
                    State = "Exited", Process = request.Owner };
                f.State.Launches.Add(launch);
                Check(SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current).Matches(request.Owner),
                    "policy locates only the exact stored exited receipt; service must still probe OS exit");
                request.Epoch++;
                f.State.Transaction.Epoch = f.State.LastTakeoverEpoch = request.Epoch;
                Check(!f.State.Matches(launch.Authorization) &&
                    SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current).Matches(request.Owner),
                    "adopted owner may close bound historical terminal proof without reviving its token");
                f.State.Transaction.LaunchOperationId = Guid.NewGuid().ToString("N");
                Reject(() => SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current), "IdentityMismatch");
                f.State.Transaction.LaunchOperationId = intent.IntentId;
                launch.State = "Started";
                Reject(() => SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current), "RequiresReconciliation");
                launch.State = "Exited";
                launch.OperationId = Guid.NewGuid().ToString("N");
                Reject(() => SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current), "IdentityMismatch");
                launch.OperationId = intent.IntentId;
                f.State.Intent.DesiredState = RecoveryDesiredState.Stopped;
                Reject(() => SupervisorGuardPermitPolicy.FindExitedLaunch(request, f.State, current), "RequiresReconciliation");
                f.State.Intent.DesiredState = RecoveryDesiredState.Run;
                var deniedClose = opened.Authority.CloseCurrentAsFailed("fixture", current.Revision - 1, current.Sha256);
                Check(deniedClose.Status == DurableAuthorityTransitionStatus.Conflict &&
                    new DurableRelaunchAuthorityFileStore(directory, operation.SessionId).Load(operation.SessionId).Revision == current.Revision,
                    "old exit evidence cannot close a newer strict revision");
                var closed = opened.Authority.CloseCurrentAsFailed("fixture", current.Revision, current.Sha256);
                Check(closed.Succeeded && closed.Record.State == DurableRelaunchPermitState.Failed &&
                    closed.Record.ConsecutiveFailures == current.Record.ConsecutiveFailures,
                    "closing a recorded failure does not itself count another failure");
                Reject(() => SupervisorGuardPermitPolicy.BuildFailure(request, f.State, current, ProcessObservation.Exited), "RequiresReconciliation");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void LaunchPreparationChannelScope()
        {
            var f = new Fixture();
            f.State.InstallationId = Guid.NewGuid().ToString("N");
            f.State.Intent.AuthorizationId = Guid.NewGuid().ToString("N");
            f.State.Intent.IntentVersion = 2;
            f.State.Intent.ConfigurationIdentity = "configuration";
            f.State.Transaction = new RecoveryTakeoverTransaction { TransactionId = f.Request.TransactionId, Epoch = 3 };
            var snapshot = new RecoveryObservationSnapshot { SourceAvailable = true,
                Authorization = new RecoveryAuthorizationToken { InstallationId = f.State.InstallationId,
                    AuthorizationId = f.State.Intent.AuthorizationId, IntentVersion = 2, TakeoverEpoch = 2 },
                RunId = f.State.Intent.RunId, ConfigurationIdentity = "configuration", MainProcess = f.State.Intent.MainProcess,
                Channels = new[] { new RecoveryChannelProgress { Channel = 1, Eligible = true },
                    new RecoveryChannelProgress { Channel = 2, Eligible = true, PermanentlyIsolated = true } } };
            f.State.Observation = new RecoveryGuardObservation { LastSnapshot = snapshot };
            var host = new GuardRecoveryHostBinding { ProcessId = 123, StartUtcTicks = DateTime.UtcNow.Ticks,
                InstanceNonce = Guid.NewGuid().ToString("N"), PipeName = "verified-host" };
            f.Prepared.Record.LastFailureSessionNonce = Guid.NewGuid().ToString("N");
            Func<DurableLaunchIntent> build = () => SupervisorGuardLaunchPreparationPolicy.Build(f.State, f.Prepared.Record,
                host, Path.GetDirectoryName(f.State.MainExecutablePath));
            var first = build();
            Check(first.Arguments.Contains("--exclude-channels \"2,3,4,5,6,7,8,9,10,11,12\""), "isolated and absent channels remain excluded");
            Check(first.LaunchSpecSha256 == build().LaunchSpecSha256, "repeat preparation is deterministic");
            snapshot.RunId = Guid.NewGuid().ToString("N");
            Reject(() => build(), "ChannelSnapshotMismatch");
            snapshot.RunId = f.State.Intent.RunId;
            snapshot.Channels[0].Completed = true;
            try { build(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("NoEligibleChannels")) { return; }
            throw new Exception("No eligible channels must prevent preparation");
        }

        private static void SupervisorPeerMustMatchService()
        {
            var peerType = typeof(RecoveryControlStore).Assembly.GetType("MTTFTest.RecoveryControl.RecoverySupervisorPeer", true);
            var verify = peerType.GetMethod("VerifyPeer", BindingFlags.Static | BindingFlags.NonPublic);
            var serviceType = typeof(SupervisorServiceRuntime).Assembly.GetType("MTTFTest.Watchdog.SupervisorWindowsService", true);
            using (var service = (IDisposable)Activator.CreateInstance(serviceType, true))
                Check((string)peerType.GetField("ServiceName", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue() ==
                    (string)serviceType.GetProperty("ServiceName").GetValue(service), "independent service-name contract drifted");
            var name = "EPB-Guard-Server-Peer-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var deadline = new CancellationTokenSource(10000))
            {
                var connected = server.WaitForConnectionAsync(deadline.Token);
                client.Connect(5000); connected.GetAwaiter().GetResult();
                var pid = checked((uint)RecoveryProcessProbe.Current().ProcessId);
                Action<Func<uint>> check = resolver =>
                {
                    try { verify.Invoke(null, new object[] { client, resolver }); }
                    catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
                };
                var calls = 0;
                check(() => { calls++; return pid; });
                Check(calls == 2, "service identity is rechecked around the kernel pipe peer query");
                Action<Func<uint>, string> deny = (resolver, expected) =>
                {
                    try { check(resolver); }
                    catch (IOException ex) when (ex.Message.Contains(expected)) { return; }
                    throw new Exception("Expected service peer rejection: " + expected);
                };
                deny(() => 0, "IdentityMismatch");
                deny(() => pid + 1, "IdentityMismatch");
                calls = 0;
                deny(() => ++calls == 1 ? pid : pid + 1, "ChangedDuringPeerCheck");
            }
        }

        private static Task<RecoveryGuardLaunchResponse> LaunchOnTestPipe(RecoveryGuardLaunchRequest request,
            string name, CancellationToken cancellationToken) =>
            (Task<RecoveryGuardLaunchResponse>)typeof(RecoveryGuardSupervisorProtocol)
                .GetMethod("LaunchOnPipeAsync", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { request, name, cancellationToken });

        private static async Task CancelPartialResponseAsync()
        {
            var f = new Fixture();
            var name = "EPB-Guard-Cancel-Test-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var cancellation = new CancellationTokenSource())
            using (var limit = new CancellationTokenSource(10000))
            {
                var connected = server.WaitForConnectionAsync(limit.Token);
                var launch = LaunchOnTestPipe(f.Request, name, cancellation.Token);
                await connected.ConfigureAwait(false);
                using (var reader = new BinaryReader(server, Encoding.UTF8, true))
                {
                    RecoveryGuardSupervisorProtocol.ReadMagic(reader, RecoveryGuardSupervisorProtocol.RequestMagic);
                    var received = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchRequest>(reader);
                    Check(received.OperationId == f.Request.OperationId, "request was delivered before cancellation");
                }
                // Leave the peer alive with a partial response; EOF cannot be
                // what makes this client's pending read finish.
                await server.WriteAsync(new[] { (byte)RecoveryGuardSupervisorProtocol.ResponseMagic.Length }, 0, 1,
                    limit.Token).ConfigureAwait(false);
                cancellation.Cancel();
                var completed = await Task.WhenAny(launch, Task.Delay(3000, limit.Token)).ConfigureAwait(false);
                Check(completed == launch, "cancellation must end pending pipe I/O while the peer stays connected");
                try { await launch.ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                throw new Exception("Expected caller cancellation, never a successful or rejected launch receipt");
            }
        }

        private static async Task FragmentedResponseAsync()
        {
            var f = new Fixture();
            var name = "EPB-Guard-Fragments-Test-" + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var limit = new CancellationTokenSource(10000))
            {
                var connected = server.WaitForConnectionAsync(limit.Token);
                var launch = LaunchOnTestPipe(f.Request, name, limit.Token);
                await connected.ConfigureAwait(false);
                using (var reader = new BinaryReader(server, Encoding.UTF8, true))
                {
                    RecoveryGuardSupervisorProtocol.ReadMagic(reader, RecoveryGuardSupervisorProtocol.RequestMagic);
                    RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchRequest>(reader).Validate();
                }
                byte[] bytes;
                using (var buffer = new MemoryStream())
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
                {
                    RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.ResponseMagic,
                        new RecoveryGuardLaunchResponse { RequestId = f.Request.RequestId,
                            OperationId = f.Request.OperationId, Accepted = true, Process = f.Request.Owner, Detail = "Started" });
                    bytes = buffer.ToArray();
                }
                for (var offset = 0; offset < bytes.Length; offset += 3)
                    await server.WriteAsync(bytes, offset, Math.Min(3, bytes.Length - offset), limit.Token).ConfigureAwait(false);
                var response = await launch.ConfigureAwait(false);
                Check(response.Accepted && response.Process.Matches(f.Request.Owner), "fragmented response must retain receipt identity");
            }
        }

        private static void Check(bool value, string detail)
        {
            if (!value) throw new Exception("RecoveryGuard Supervisor: " + detail);
        }

        private static void Reject(Action action, string code)
        {
            try { action(); }
            catch (InvalidDataException ex) when (ex.Message.Contains(code)) { return; }
            throw new Exception("Expected rejection: " + code);
        }

        private static void WireBoundaries()
        {
            Check(RecoveryGuardSupervisorProtocol.PipeName == SupervisorProtocol.PipeName, "stable pipe contract drifted");
            var f = new Fixture();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                RecoveryGuardSupervisorProtocol.Write(writer, RecoveryGuardSupervisorProtocol.RequestMagic, f.Request);
                stream.Position = 0;
                RecoveryGuardSupervisorProtocol.ReadMagic(reader, RecoveryGuardSupervisorProtocol.RequestMagic);
                var read = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchRequest>(reader);
                read.Validate();
                Check(read.Owner.Matches(f.Request.Owner) && read.TransactionId == f.Request.TransactionId,
                    "wire must retain exact owner and transaction");
                stream.SetLength(0); stream.Position = 0; writer.Write(65537); stream.Position = 0;
                Reject(() => RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchRequest>(reader), "SizeInvalid");
                stream.SetLength(0); stream.Position = 0; writer.Write((byte)255); stream.Position = 0;
                Reject(() => RecoveryGuardSupervisorProtocol.ReadMagic(reader, RecoveryGuardSupervisorProtocol.ResponseMagic), "MagicMismatch");
            }
        }

        private static void PipeOwnerCannotBeClaimedInPayload(bool safetyPreparation = false, bool safetyExecution = false)
        {
            var f = new Fixture();
            f.Request.Owner.ProcessId++; // The actual pipe client is this test process.
            var requestMagic = safetyExecution ? RecoveryGuardSupervisorProtocol.SafetyExecuteMagic :
                safetyPreparation ? RecoveryGuardSupervisorProtocol.SafetyPrepareMagic : RecoveryGuardSupervisorProtocol.RequestMagic;
            var responseMagic = safetyExecution ? RecoveryGuardSupervisorProtocol.SafetyExecuteResponseMagic :
                safetyPreparation ? RecoveryGuardSupervisorProtocol.SafetyPrepareResponseMagic : RecoveryGuardSupervisorProtocol.ResponseMagic;
            var pipeName = "EPB-Guard-Peer-Test-" + Guid.NewGuid().ToString("N");
            using (var runtime = new SupervisorServiceRuntime()) // Never Start: no service, hardware, registration or task.
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1))
            using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
            using (var deadline = new Timer(_ => { try { server.Dispose(); client.Dispose(); } catch { } }, null, 10000, Timeout.Infinite))
            {
                var serve = Task.Run(() =>
                {
                    server.WaitForConnection();
                    using (var reader = new BinaryReader(server, Encoding.UTF8, true))
                    using (var writer = new BinaryWriter(server, Encoding.UTF8, true))
                    {
                        RecoveryGuardSupervisorProtocol.ReadMagic(reader, requestMagic);
                        typeof(SupervisorServiceRuntime).GetMethod(safetyExecution ? "HandleGuardSafetyExecuteConnection" :
                            safetyPreparation ? "HandleGuardSafetyPrepareConnection" : "HandleGuardLaunchConnection", BindingFlags.NonPublic | BindingFlags.Instance)
                            .Invoke(runtime, new object[] { server, reader, writer });
                    }
                });
                client.Connect(5000);
                using (var reader = new BinaryReader(client, Encoding.UTF8, true))
                using (var writer = new BinaryWriter(client, Encoding.UTF8, true))
                {
                    if (safetyExecution)
                        RecoveryGuardSupervisorProtocol.Write(writer, requestMagic, new RecoveryGuardSafetyExecuteRequest
                        { RequestId = f.Request.RequestId, TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch,
                            Owner = f.Request.Owner, SafetyAuthorityId = f.Request.SafetyAuthorityId });
                    else if (safetyPreparation)
                        RecoveryGuardSupervisorProtocol.Write(writer, requestMagic, new RecoveryGuardSafetyPrepareRequest
                        { RequestId = f.Request.RequestId, TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch, Owner = f.Request.Owner });
                    else RecoveryGuardSupervisorProtocol.Write(writer, requestMagic, f.Request);
                    RecoveryGuardSupervisorProtocol.ReadMagic(reader, responseMagic);
                    var response = RecoveryGuardSupervisorProtocol.ReadBody<RecoveryGuardLaunchResponse>(reader);
                    Check(!response.Accepted && response.RequestId == f.Request.RequestId &&
                        response.Detail == "RecoveryGuardPipeOwnerMismatch", "payload PID must not override kernel peer identity");
                }
                serve.GetAwaiter().GetResult();
            }
        }

        private static void GuardSafetyExecutionEvidence()
        {
            var f = new Fixture();
            Check(SupervisorGuardSafetyExecutionPolicy.Evaluate(f.Safety.Receipt, ProcessObservation.ExactAlive) ==
                RecoveryGuardSafetyExecutionState.Pending, "complete receipt cannot retire a still-live safety process");
            Check(SupervisorGuardSafetyExecutionPolicy.Evaluate(f.Safety.Receipt, ProcessObservation.Unknown) ==
                RecoveryGuardSafetyExecutionState.Blocked, "unknown worker cannot prove hardware resources released");
            Check(SupervisorGuardSafetyExecutionPolicy.Evaluate(f.Safety.Receipt, ProcessObservation.Exited) ==
                RecoveryGuardSafetyExecutionState.Completed, "complete safety proof plus exact exit may finish SafeStop");
            f.Safety.Receipt.PowerOff = false;
            Check(SupervisorGuardSafetyExecutionPolicy.Evaluate(f.Safety.Receipt, ProcessObservation.Exited) ==
                RecoveryGuardSafetyExecutionState.Blocked, "exit alone cannot substitute for missing power-off evidence");
            f.Safety.Receipt.PowerOff = true;
            var directory = Path.GetDirectoryName(f.Request.Owner.ExecutablePath);
            f.Safety.Receipt.SafetyAgentExecutablePath = Path.Combine(directory, "MTTFTest.SafetyAgent.exe");
            f.Rehash();
            var request = new RecoveryGuardSafetyExecuteRequest
            {
                RequestId = f.Request.RequestId, TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch,
                Owner = f.Request.Owner, SafetyAuthorityId = f.Safety.AuthorityId
            };
            var launch = SupervisorGuardSafetyExecutionPolicy.BuildLaunchRequest(request, f.Safety, directory);
            Check(launch.IsStructurallyValid() && launch.RequesterProcessId == request.Owner.ProcessId &&
                launch.AuthorityId == request.SafetyAuthorityId && launch.AuthorityReceiptRevision == f.Safety.InitialReceiptRevision &&
                launch.Arguments.Contains(f.Safety.Receipt.Nonce) && launch.Arguments.Contains(f.Safety.ProjectDirectory),
                "service derives safety arguments and authority binding without impersonating a sidecar");
            f.Safety.Receipt.SafetyAgentExecutablePath = Path.Combine(directory, "cmd.exe"); f.Rehash();
            Reject(() => SupervisorGuardSafetyExecutionPolicy.BuildLaunchRequest(request, f.Safety, directory), "AgentPathMismatch");
            request.SafetyAuthorityId = Guid.NewGuid().ToString("N");
            Reject(() => SupervisorGuardSafetyExecutionPolicy.BuildLaunchRequest(request, f.Safety, directory), "AuthorityBindingMismatch");
        }

        private static void GuardSafetyPreparationEvidence()
        {
            var f = new Fixture();
            f.State.InstallationId = Guid.NewGuid().ToString("N");
            f.State.Transaction = new RecoveryTakeoverTransaction
            { TransactionId = f.Request.TransactionId, Epoch = f.Request.Epoch, Stage = RecoveryStage.SafeStop };
            f.Prepared.Record.State = DurableRelaunchPermitState.Approved;
            var seed = new WatchdogCrashRecoverySeed
            {
                Revision = 1, SessionId = f.Safety.SessionId, SessionGeneration = 1, SessionLease = 1,
                SeedId = Guid.NewGuid().ToString("N"), ProjectDirectory = f.Safety.ProjectDirectory,
                MainExecutablePath = f.State.MainExecutablePath, MainExecutableSha256 = new string('a', 64),
                SafetyAgentExecutablePath = "safety.exe", SafetyAgentExecutableSha256 = new string('b', 64),
                ConfigSnapshotSchemaVersion = 2, ConfigSnapshotPath = "source-config", ConfigSnapshotManifestPath = "source-manifest",
                ConfigSnapshotManifestSha256 = new string('c', 64)
            };
            var snapshot = new WatchdogSafetyConfigSnapshotResult
            {
                Succeeded = true, ConfigDirectory = "frozen-config", ManifestPath = "frozen-manifest", ManifestSha256 = new string('d', 64),
                Manifest = new WatchdogSafetyConfigSnapshotManifest
                {
                    HandoffId = SupervisorGuardSafetyPreparationPolicy.HandoffId(f.State, f.Prepared.Record),
                    SessionId = seed.SessionId, SessionGeneration = seed.SessionGeneration, SessionLease = seed.SessionLease,
                    PermitGeneration = f.Prepared.Record.Generation, PermitId = f.Prepared.Record.PermitId,
                    MainExecutableSha256 = seed.MainExecutableSha256, SafetyAgentExecutableSha256 = seed.SafetyAgentExecutableSha256
                }
            };
            Func<WatchdogSafetyHandoffReceipt> create = () => SupervisorGuardSafetyPreparationPolicy.CreateReceipt(
                f.State, seed, f.Prepared.Record, snapshot, ProcessObservation.Exited, DateTime.UtcNow);
            var receipt = create();
            Check(receipt.IsValidFor(seed.SessionId) && receipt.State == WatchdogSafetyHandoffState.Accepted &&
                !receipt.MotorsOff && !receipt.PowerOff && !receipt.PressureSafe && !receipt.PersistenceDrained &&
                !WatchdogRecoveryReadinessPolicy.IsCompleteSafetyHandoffProof(receipt),
                "preparation must never invent hydraulic, power, motor or persistence completion");
            Check(receipt.HandoffId == create().HandoffId && receipt.SidecarProcessId == 0 &&
                receipt.StopSafetyTransactionId == f.Request.TransactionId,
                "same transaction and permit identifies one handoff, without impersonating the sidecar");
            var authority = new SupervisorSafetyAuthorityRecord
            {
                AuthorityId = SupervisorSafetyAuthorityStore.ComputeAuthorityId(seed.SessionId, receipt.HandoffId, 1, receipt.RelaunchPermitId),
                SessionId = seed.SessionId, HandoffId = receipt.HandoffId, ProjectDirectory = seed.ProjectDirectory,
                InitialReceiptRevision = 1, ReceiptRevision = 1, Receipt = receipt,
                InitialReceiptCanonicalSha256 = SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt),
                ReceiptCanonicalSha256 = SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt)
            };
            SupervisorGuardSafetyPreparationPolicy.ValidateExisting(f.State, seed, f.Prepared.Record, authority);
            f.State.Transaction.Epoch++;
            Reject(() => SupervisorGuardSafetyPreparationPolicy.ValidateExisting(f.State, seed, f.Prepared.Record, authority), "ExistingSafetyAuthorityMismatch");
            SupervisorGuardSafetyPreparationPolicy.ValidateExisting(f.State, seed, f.Prepared.Record, authority, f.State.Transaction.Epoch - 1);
            Reject(() => SupervisorGuardSafetyPreparationPolicy.ValidateExisting(f.State, seed, f.Prepared.Record, authority,
                f.State.Transaction.Epoch + 1), "ExistingSafetyAuthorityMismatch");
            f.State.Transaction.Epoch--;
            snapshot.Manifest.PermitGeneration++;
            Reject(() => create(), "SnapshotBindingMismatch");
            snapshot.Manifest.PermitGeneration--;
            Reject(() => SupervisorGuardSafetyPreparationPolicy.CreateReceipt(f.State, seed, f.Prepared.Record, snapshot,
                ProcessObservation.ExactAlive, DateTime.UtcNow), "OldMainExitUnproven");
            f.Prepared.Record.RunId = Guid.NewGuid().ToString("N");
            Reject(() => create(), "SourceIdentityMismatch");
            f.Prepared.Record.RunId = f.State.Intent.RunId;
            f.Prepared.Record.State = DurableRelaunchPermitState.Revoked;
            Reject(() => create(), "SourceIdentityMismatch");
        }

        private static void EvidenceBinding()
        {
            var f = new Fixture(); f.Validate();
            f.Safety.Receipt.PowerOff = false; f.Rehash();
            Reject(f.Validate, "SafetyEvidenceMismatch");
            f = new Fixture(); f.Safety.Receipt.OldProcessStartUtcTicks++; f.Rehash();
            Reject(f.Validate, "SafetyEvidenceMismatch");
            f = new Fixture(); f.Safety.Receipt.RunId = Guid.NewGuid().ToString("N"); f.Rehash();
            Reject(f.Validate, "SafetyEvidenceMismatch");
            f = new Fixture(); f.Prepared.Record.PermitId = Guid.NewGuid().ToString("N");
            Reject(f.Validate, "PreparedAuthorityMismatch");
            f = new Fixture(); f.Prepared.Record.LaunchIntentId = Guid.NewGuid().ToString("N");
            Reject(f.Validate, "PreparedAuthorityMismatch");
        }

        private static void MissingOrRevokedPreparedAuthority()
        {
            var f = new Fixture(); f.Prepared = null;
            Reject(f.Validate, "PreparedAuthorityMismatch");
            f = new Fixture(); f.Prepared.Unproven = true;
            Reject(f.Validate, "PreparedAuthorityMismatch");
            f = new Fixture(); f.Prepared.Record.State = DurableRelaunchPermitState.Revoked;
            Reject(f.Validate, "PreparedAuthorityMismatch");
            f = new Fixture(); f.Prepared.Record.RunId = Guid.NewGuid().ToString("N");
            Reject(f.Validate, "PreparedAuthorityMismatch");
        }

        private sealed class Fixture
        {
            internal readonly RecoveryGuardLaunchRequest Request;
            internal readonly RecoveryControlState State;
            internal readonly SupervisorSafetyAuthorityRecord Safety;
            internal DurableAuthorityStoreReadResult Prepared;
            internal Fixture()
            {
                var process = RecoveryProcessProbe.Current();
                var session = Guid.NewGuid().ToString("N");
                var run = Guid.NewGuid().ToString("N");
                var permit = Guid.NewGuid().ToString("N");
                var nonce = Guid.NewGuid().ToString("N");
                var hash = new string('a', 64);
                Request = new RecoveryGuardLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"), TransactionId = Guid.NewGuid().ToString("N"),
                    Epoch = 1, Owner = process, OperationId = Guid.NewGuid().ToString("N"),
                    SafetyAuthorityId = Guid.NewGuid().ToString("N")
                };
                State = new RecoveryControlState
                {
                    MainExecutablePath = process.ExecutablePath,
                    Intent = new RecoveryRunIntent { MainProcess = process, RunId = run, WatchdogSessionId = session }
                };
                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SessionId = session, SessionGeneration = 1, SessionLease = 1, HandoffId = Guid.NewGuid().ToString("N"),
                    Nonce = nonce, RunId = run, Revision = 1, State = WatchdogSafetyHandoffState.Completed,
                    Stage = WatchdogSafetyStage.Completed, MotorsOff = true, PowerOff = true, PressureSafe = true,
                    PersistenceDrained = true, LogicalQuiescent = true, HardwareResourcesReleased = true,
                    ExecutionAuthorizationRevoked = true, CallbacksIsolated = true,
                    OldProcessId = process.ProcessId, OldProcessStartUtcTicks = process.StartUtcTicks,
                    ProjectDirectory = Path.GetDirectoryName(process.ExecutablePath),
                    MainExecutablePath = process.ExecutablePath, MainExecutableSha256 = hash,
                    SafetyAgentExecutablePath = "safety.exe", SafetyAgentExecutableSha256 = hash,
                    ConfigSnapshotSchemaVersion = 2, ConfigSnapshotPath = "snapshot", ConfigSnapshotManifestPath = "manifest",
                    ConfigSnapshotManifestSha256 = hash, RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit,
                    RelaunchPermitGeneration = 1, RelaunchPermitId = permit,
                    RelaunchPermitNonceSha256 = WatchdogTakeoverPermitBindingPolicy.HashNonce(nonce)
                };
                Safety = new SupervisorSafetyAuthorityRecord
                {
                    AuthorityId = Request.SafetyAuthorityId, SessionId = session, HandoffId = receipt.HandoffId,
                    ProjectDirectory = receipt.ProjectDirectory, InitialReceiptRevision = 1, ReceiptRevision = 1,
                    InitialReceiptCanonicalSha256 = hash, Receipt = receipt
                };
                Rehash();
                Prepared = new DurableAuthorityStoreReadResult
                {
                    Exists = true,
                    Record = new DurableRelaunchAuthorityRecord
                    {
                        State = DurableRelaunchPermitState.LaunchIntent, SessionId = session, RunId = run,
                        LaunchIntentId = Request.OperationId, Generation = 1, PermitId = permit, PermitNonce = nonce,
                        LaunchExecutablePath = process.ExecutablePath, LaunchExecutableSha256 = hash
                    }
                };
            }
            internal void Rehash() => Safety.ReceiptCanonicalSha256 = SupervisorSafetyAuthorityStore.ComputeReceiptSha256(Safety.Receipt);
            internal void Validate() => SupervisorGuardLaunchPolicy.ValidateEvidence(Request, State, Safety, Prepared);
        }
    }
}
