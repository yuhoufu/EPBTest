using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.RecoveryControl
{
    /// <summary>
    /// Installation-scoped authority. Every writer uses the same short mutex.
    /// The commit head is flushed BEFORE the state replacement: interruption
    /// leaves an explicit uncertain commit, never an older runnable fallback.
    /// No hardware/pipe/process operation may be supplied inside a mutation.
    /// </summary>
    public sealed class RecoveryControlStore
    {
        private const int MaximumRecordBytes = 512 * 1024;
        private readonly string _root;
        private readonly string _mutexName;
        public static string DefaultRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MTTFTest", "RecoveryControl");
        public string Root => _root;

        private sealed class Envelope
        {
            public int SchemaVersion { get; set; }
            public long Revision { get; set; }
            public string Sha256 { get; set; }
            public string Payload { get; set; }
        }

        private sealed class CommitHead
        {
            public int SchemaVersion { get; set; }
            public long Revision { get; set; }
            public string Sha256 { get; set; }
        }

        public RecoveryControlStore(string root = null)
        {
            _root = Path.GetFullPath(root ?? DefaultRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(_root)) throw new ArgumentException("RecoveryControlRootInvalid");
            _mutexName = "Global\\MTTFTest.RecoveryControl.V1." + Hash(_root.ToUpperInvariant());
        }

        // Installation presence is deliberately separate from authority validity.
        // An existing directory with a missing/broken authority is NOT legacy mode.
        public bool IsRegisteredOrPending => Directory.Exists(_root);

        public RecoveryControlState Register(string benchId, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(benchId) || benchId.Length > 100 ||
                string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
                throw new ArgumentException("RecoveryRegistrationInvalid");
            return Locked(() =>
            {
                if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
                {
                    var current = ReadUnsafe();
                    if (current.BenchId != benchId || !string.Equals(current.MainExecutablePath,
                        Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("RecoveryRegistrationMismatch");
                    return current; // installation retry never resets authorization or budgets
                }
                Directory.CreateDirectory(_root);
                var state = new RecoveryControlState
                {
                    InstallationId = Guid.NewGuid().ToString("N"),
                    BenchId = benchId,
                    MainExecutablePath = Path.GetFullPath(executablePath)
                };
                AtomicWrite(PathOf("registration-id"), state.InstallationId);
                WriteStateUnsafe(state);
                return state;
            });
        }

        public RecoveryControlState Read() => Locked(ReadUnsafe);

        public RecoveryAuthorizationToken BeginManualRun(string rootRunId, string runId,
            string configurationIdentity, RecoveryProcessIdentity process, DateTime nowUtc)
        {
            RequireRunIdentity(rootRunId, runId, configurationIdentity, process, nowUtc);
            return Mutate(state =>
            {
                if (!string.Equals(state.MainExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RecoveryMainPathMismatch");
                if (state.Transaction != null && !state.Transaction.OwnershipReleased)
                    throw new InvalidOperationException("RecoveryTakeoverMustBeClosedBeforeManualStart");
                if (state.Intent?.DesiredState == RecoveryDesiredState.Run)
                    throw new InvalidOperationException("RecoveryPreviousRunMustBeStoppedBeforeManualStart");
                state.Intent = new RecoveryRunIntent
                {
                    AuthorizationId = Guid.NewGuid().ToString("N"),
                    RootRunId = rootRunId,
                    RunId = runId,
                    ConfigurationIdentity = configurationIdentity,
                    IntentVersion = checked((state.Intent?.IntentVersion ?? 0) + 1),
                    DesiredState = RecoveryDesiredState.Run,
                    MainProcess = process,
                    IssuedUtcTicks = nowUtc.Ticks,
                    Reason = "ManualStart"
                };
                state.Observation = null;
                return state.Token();
            });
        }

        public RecoveryAuthorizationToken SetOperatorIntent(string authorizationId, long expectedIntentVersion,
            RecoveryDesiredState desiredState, string reason)
        {
            if (desiredState == RecoveryDesiredState.Run || !Enum.IsDefined(typeof(RecoveryDesiredState), desiredState))
                throw new ArgumentException("UseExplicitManualStartOrContinue");
            return Mutate(state =>
            {
                if (state.Intent == null || state.Intent.AuthorizationId != authorizationId ||
                    state.Intent.IntentVersion != expectedIntentVersion)
                    throw new InvalidOperationException("RecoveryIntentConflict");
                if (desiredState == RecoveryDesiredState.Paused && state.Intent.DesiredState != RecoveryDesiredState.Run &&
                    state.Intent.DesiredState != RecoveryDesiredState.Paused)
                    throw new InvalidOperationException("RecoveryTerminalIntentCannotBecomePaused");
                if (desiredState == RecoveryDesiredState.Paused && state.Intent.DesiredState == RecoveryDesiredState.Run)
                    state.Intent.PausedSourceIntentVersion = state.Intent.IntentVersion;
                state.Intent.IntentVersion = checked(state.Intent.IntentVersion + 1);
                state.Intent.DesiredState = desiredState;
                state.Intent.Reason = Limit(reason);
                if (state.Transaction?.Active == true)
                {
                    state.Transaction.Stage = RecoveryStage.Cancelled;
                    state.Transaction.Reason = "OperatorIntent:" + desiredState;
                }
                return state.Token();
            });
        }

        public RecoveryAuthorizationToken ContinueManually(string authorizationId, long expectedVersion,
            string runId, RecoveryProcessIdentity process, DateTime nowUtc)
        {
            return Mutate(state =>
            {
                if (state.Intent?.AuthorizationId != authorizationId || state.Intent.IntentVersion != expectedVersion ||
                    state.Intent.DesiredState != RecoveryDesiredState.Paused ||
                    (state.Transaction != null && !state.Transaction.OwnershipReleased))
                    throw new InvalidOperationException("RecoveryManualContinueConflict");
                if (RecoveryRevocationSignal.HasStopRequest(state))
                    throw new InvalidOperationException("RecoveryManualContinueConflict:IndependentStop");
                RequireRunIdentity(state.Intent.RootRunId, runId, state.Intent.ConfigurationIdentity, process, nowUtc);
                if (!string.Equals(state.MainExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RecoveryMainPathMismatch");
                state.Intent.IntentVersion = checked(state.Intent.IntentVersion + 1);
                state.Intent.DesiredState = RecoveryDesiredState.Run;
                state.Intent.RunId = runId;
                state.Intent.MainProcess = process;
                state.Intent.IssuedUtcTicks = nowUtc.Ticks;
                state.Intent.Reason = "ManualContinue";
                state.Intent.PausedSourceIntentVersion = 0;
                state.Observation = null;
                return state.Token();
            });
        }

        public void BindWatchdogSession(RecoveryAuthorizationToken token, string runId,
            RecoveryProcessIdentity process, string sessionId, DateTime nowUtc)
        {
            if (!Guid.TryParseExact(sessionId, "N", out var session) || session == Guid.Empty)
                throw new ArgumentException("RecoveryWatchdogSessionInvalid");
            Mutate(state =>
            {
                AssertIntent(state, token, nowUtc);
                if (state.Intent.RunId != runId || state.Intent.MainProcess?.Matches(process) != true)
                    throw new InvalidOperationException("RecoveryWatchdogSessionWriterMismatch");
                state.Intent.WatchdogSessionId = session.ToString("N");
                return true;
            });
        }

        public RecoveryAuthorizationToken AuthorizeWatchdogSession(string sessionId, DateTime nowUtc)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe();
                if (!Guid.TryParseExact(sessionId, "N", out var session) || session == Guid.Empty ||
                    state.Intent?.WatchdogSessionId != session.ToString("N"))
                    throw new InvalidOperationException("RecoveryWatchdogSessionSuperseded");
                AssertExistingWatchdogLaunchAllowed(state, state.Token(), nowUtc);
                return state.Token();
            });
        }

        // Serializes legacy permit writes with Claim, without holding the state
        // mutex during strict-authority I/O. Operator stop remains independent.
        // The callback may only mutate the permit store, never start a process
        // or operate hardware; those actions require their own latest fences.
        public T RunLegacyRecoveryAuthorityMutation<T>(string sessionId, string runId,
            DateTime nowUtc, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return Locked(() =>
            {
                Locked(() =>
                {
                    var state = ReadUnsafe();
                    AssertIntent(state, state.Token(), nowUtc);
                    if (string.IsNullOrEmpty(sessionId) || state.Intent.WatchdogSessionId != sessionId ||
                        !SameRolloverRun(state.Intent.RunId, runId))
                        throw new InvalidOperationException("RecoveryLegacyAuthoritySessionOrRunSuperseded");
                    if (state.Transaction != null && !state.Transaction.OwnershipReleased)
                        throw new InvalidOperationException("RecoveryLegacyAuthoritySupersededByGuard");
                    if (state.Observation?.Established != true &&
                        string.IsNullOrEmpty(state.Intent.LegacyStartupAdmissionSessionId))
                    {
                        // The existing Watchdog must be able to recover a first learning
                        // failure before Guard has observed two progress snapshots. This
                        // exception is limited to the exact live process from this boot;
                        // it cannot authorize Guard takeover or recovery after power loss.
                        var age = nowUtc.Ticks - state.Intent.IssuedUtcTicks;
                        if (state.Observation?.Expired == true || age < 0 ||
                            age >= TimeSpan.FromMinutes(60).Ticks ||
                            RecoveryProcessProbe.Observe(state.Intent.MainProcess,
                                RecoveryProcessProbe.ReadBootId()) != ProcessObservation.ExactAlive)
                            throw new InvalidOperationException("RecoveryLegacyStartupProcessUnprovenOrExpired");
                        state.Intent.LegacyStartupAdmissionSessionId = sessionId;
                        state.Intent.LegacyStartupAdmissionBootId = state.Intent.MainProcess.BootId;
                        WriteStateUnsafe(state);
                    }
                    AssertExistingWatchdogLaunchAllowed(state, state.Token(), nowUtc);
                    return true;
                });
                return action();
            }, _mutexName + ".PermitMutation");
        }

        public void AssertGuardCreatedAttachment(string sessionId, string operationId,
            RecoveryProcessIdentity process, DateTime nowUtc)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                AssertAllowed(state, state.Token(), nowUtc);
                AssertActionSession(state, sessionId);
                var tx = state.Transaction;
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (tx?.Active != true || tx.OwnershipReleased || tx.LeaseUntilUtcTicks <= nowUtc.Ticks ||
                    tx.StageDeadlineUtcTicks <= nowUtc.Ticks || (tx.Stage != RecoveryStage.Launch && tx.Stage != RecoveryStage.Verify) ||
                    tx.ActionEpoch != tx.Epoch || tx.LaunchOperationId != operationId ||
                    launch?.State != "Started" || !state.Matches(launch.Authorization) ||
                    process?.Matches(launch.Process) != true)
                    throw new InvalidOperationException("RecoveryGuardAttachmentIdentityOrStageMismatch");
                return true;
            });
        }

        public void PublishSnapshot(RecoveryObservationSnapshot snapshot)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                if (!state.Matches(snapshot?.Authorization) || snapshot.MainProcess?.Matches(state.Intent.MainProcess) != true ||
                    snapshot.RunId != state.Intent.RunId || snapshot.ConfigurationIdentity != state.Intent.ConfigurationIdentity)
                    throw new InvalidOperationException("RecoverySnapshotWriterRevoked");
                WriteEnvelopeUnsafe("snapshot.json", snapshot, snapshot.Sequence);
                return true;
            });
        }

        public RecoveryObservationSnapshot ReadSnapshot()
        {
            return Locked(() => File.Exists(PathOf("snapshot.json"))
                ? Decode<RecoveryObservationSnapshot>(ReadEnvelopeUnsafe("snapshot.json")) : null);
        }

        public RecoveryDecision Observe(RecoveryObservationSnapshot snapshot, ProcessObservation process,
            string bootId, DateTime nowUtc, RecoveryGuardSettings settings, bool maintenance)
        {
            return Mutate(state =>
            {
                var decision = RecoveryDecisionPolicy.Observe(state, snapshot, process, bootId, nowUtc, settings, maintenance);
                if ((decision.Code == "IndependentStopObserved" || decision.Code == "IndependentPauseObserved") &&
                    (state.Intent?.DesiredState == RecoveryDesiredState.Run || state.Intent?.DesiredState == RecoveryDesiredState.Paused))
                {
                    if (decision.Code == "IndependentPauseObserved")
                        state.Intent.PausedSourceIntentVersion = state.Intent.IntentVersion;
                    state.Intent.IntentVersion = checked(state.Intent.IntentVersion + 1);
                    state.Intent.DesiredState = decision.Code == "IndependentPauseObserved"
                        ? RecoveryDesiredState.Paused : RecoveryDesiredState.Stopped;
                    state.Intent.Reason = decision.Code + "Confirmed";
                    if (state.Transaction?.Active == true)
                    {
                        state.Transaction.Stage = RecoveryStage.Cancelled;
                        state.Transaction.Reason = state.Intent.Reason;
                    }
                }
                return decision;
            });
        }

        public void PulseExecutionWorker(RecoveryProcessIdentity owner, RecoveryProcessIdentity previousIdentity, ProcessObservation previousOwner,
            DateTime nowUtc, int leaseSeconds)
        {
            if (owner?.IsValid() != true || nowUtc.Kind != DateTimeKind.Utc || leaseSeconds < 1 || leaseSeconds > 1800)
                throw new ArgumentException("RecoveryExecutionPulseInvalid");
            Mutate(state =>
            {
                var worker = state.ExecutionWorker;
                if (worker != null && worker.Owner?.Matches(owner) != true &&
                    (previousOwner != ProcessObservation.Exited || worker.Owner?.Matches(previousIdentity) != true))
                    throw new InvalidOperationException("RecoveryExecutionPreviousOwnerUnproven");
                if (worker?.Owner?.Matches(owner) == true &&
                    (worker.RetirementRequestedUtcTicks > 0 || nowUtc.Ticks < worker.LastPulseUtcTicks))
                    throw new InvalidOperationException("RecoveryExecutionWorkerFenced");
                state.ExecutionWorker = new RecoveryExecutionWorker { Owner = owner,
                    LastPulseUtcTicks = nowUtc.Ticks, LeaseUntilUtcTicks = nowUtc.AddSeconds(leaseSeconds).Ticks };
                return true;
            });
        }

        public void RequestIdleWorkerRetirement(RecoveryProcessIdentity owner, DateTime nowUtc)
        {
            Mutate(state =>
            {
                AssertIdleWorkerRetirement(state, owner, nowUtc, false);
                if (state.ExecutionWorker.RetirementRequestedUtcTicks == 0)
                    state.ExecutionWorker.RetirementRequestedUtcTicks = nowUtc.Ticks;
                return true;
            });
        }

        public void AssertIdleWorkerRetirement(RecoveryProcessIdentity owner, DateTime nowUtc) =>
            Locked(() => { AssertIdleWorkerRetirement(ReadUnsafe(), owner, nowUtc, true); return true; });

        private static void AssertIdleWorkerRetirement(RecoveryControlState state, RecoveryProcessIdentity owner,
            DateTime nowUtc, bool requireMarker)
        {
            AssertAllowed(state, state.Token(), nowUtc);
            var worker = state.ExecutionWorker;
            if (worker?.Owner?.Matches(owner) != true || worker.LeaseUntilUtcTicks <= 0 ||
                worker.LeaseUntilUtcTicks > nowUtc.Ticks ||
                (state.Transaction != null && !state.Transaction.OwnershipReleased) ||
                (requireMarker && worker.RetirementRequestedUtcTicks <= 0))
                throw new InvalidOperationException("RecoveryIdleWorkerRetirementDenied");
        }

        private static void AssertExecutionWorkerMayAct(RecoveryControlState state, RecoveryProcessIdentity owner)
        {
            if (state.ExecutionWorker != null && (state.ExecutionWorker.Owner?.Matches(owner) != true ||
                state.ExecutionWorker.RetirementRequestedUtcTicks > 0))
                throw new InvalidOperationException("RecoveryExecutionWorkerFenced");
        }

        public RecoveryTakeoverTransaction Claim(RecoveryObservationSnapshot snapshot, ProcessObservation process,
            RecoveryProcessIdentity owner, DateTime nowUtc, RecoveryGuardSettings settings, bool maintenance)
        {
            if (owner?.IsValid() != true) throw new ArgumentException("RecoveryOwnerInvalid");
            return Locked(() => Mutate(state =>
            {
                AssertExecutionWorkerMayAct(state, owner);
                // Decision is recomputed under the same short transaction as the
                // epoch change; an operator stop between scan and claim wins.
                var decision = RecoveryDecisionPolicy.Observe(state, snapshot, process,
                    owner.BootId, nowUtc, settings, maintenance);
                if (!decision.CanClaim) throw new InvalidOperationException("RecoveryClaimDenied:" + decision.Code);
                state.LastTakeoverEpoch = checked(state.LastTakeoverEpoch + 1);
                state.Transaction = new RecoveryTakeoverTransaction
                {
                    TransactionId = Guid.NewGuid().ToString("N"),
                    AuthorizationId = state.Intent.AuthorizationId,
                    IntentVersion = state.Intent.IntentVersion,
                    Epoch = state.LastTakeoverEpoch,
                    Owner = owner,
                    LeaseUntilUtcTicks = nowUtc.AddSeconds(settings.OwnerLeaseSeconds).Ticks,
                    Stage = RecoveryStage.Claim,
                    StageStartedUtcTicks = nowUtc.Ticks,
                    StageDeadlineUtcTicks = nowUtc.AddSeconds(settings.StageTimeoutSeconds).Ticks,
                    LastProgressUtcTicks = nowUtc.Ticks,
                    CircuitCooldownSeconds = settings.CooldownSeconds,
                    VerificationEvidenceMaxAgeSeconds = settings.SnapshotMaxAgeSeconds,
                    Reason = process == ProcessObservation.Exited ? "MainExited" : "BusinessStalled"
                };
                return state.Transaction;
            }), _mutexName + ".PermitMutation");
        }

        public RecoveryTakeoverTransaction Adopt(string transactionId, long expectedEpoch,
            RecoveryProcessIdentity replacement, ProcessObservation previousOwner, DateTime nowUtc,
            RecoveryGuardSettings settings)
        {
            settings.Validate();
            if (replacement?.IsValid() != true || previousOwner != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryOldOwnerExitUnproven");
            return Mutate(state =>
            {
                AssertAllowed(state, state.Token(), nowUtc);
                var transaction = state.Transaction;
                if (transaction?.Active != true || transaction.TransactionId != transactionId ||
                    transaction.Epoch != expectedEpoch || transaction.LeaseUntilUtcTicks > nowUtc.Ticks)
                    throw new InvalidOperationException("RecoveryOwnerNotAvailable");
                AssertExecutionWorkerMayAct(state, replacement);
                transaction.Epoch = state.LastTakeoverEpoch = checked(state.LastTakeoverEpoch + 1);
                transaction.Owner = replacement;
                transaction.WorkerRetirementRequestedUtcTicks = 0;
                transaction.LeaseUntilUtcTicks = nowUtc.AddSeconds(settings.OwnerLeaseSeconds).Ticks;
                // Preserve stage, launch consumption, cooldown and business
                // baseline. Adoption is not evidence an action succeeded.
                return transaction;
            });
        }

        public void RenewOwner(string transactionId, long epoch, RecoveryProcessIdentity owner,
            DateTime nowUtc, RecoveryGuardSettings settings)
        {
            settings.Validate();
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                state.Transaction.LeaseUntilUtcTicks = nowUtc.AddSeconds(settings.OwnerLeaseSeconds).Ticks;
                return true;
            });
        }

        public void RequestExpiredWorkerRetirement(string transactionId, long epoch,
            RecoveryProcessIdentity owner, DateTime nowUtc)
        {
            Mutate(state =>
            {
                AssertAllowed(state, state.Token(), nowUtc);
                var tx = state.Transaction;
                if (tx?.Active != true || tx.OwnershipReleased || tx.TransactionId != transactionId || tx.Epoch != epoch ||
                    tx.Owner?.Matches(owner) != true || tx.LeaseUntilUtcTicks > nowUtc.Ticks)
                    throw new InvalidOperationException("RecoveryWorkerRetirementUnavailable");
                if (tx.WorkerRetirementRequestedUtcTicks == 0)
                    tx.WorkerRetirementRequestedUtcTicks = nowUtc.Ticks;
                return true;
            });
        }

        public void AssertWorkerRetirement(string transactionId, long epoch, RecoveryProcessIdentity owner, DateTime nowUtc)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                AssertAllowed(state, state.Token(), nowUtc);
                var tx = state.Transaction;
                if (tx?.Active != true || tx.OwnershipReleased || tx.TransactionId != transactionId || tx.Epoch != epoch ||
                    tx.Owner?.Matches(owner) != true || tx.WorkerRetirementRequestedUtcTicks <= 0 || tx.LeaseUntilUtcTicks > nowUtc.Ticks)
                    throw new InvalidOperationException("RecoveryWorkerRetirementUnavailable");
                return true;
            });
        }

        public void Advance(string transactionId, long epoch, RecoveryProcessIdentity owner,
            RecoveryStage expectedStage, RecoveryStage nextStage, string evidence,
            DateTime nowUtc, RecoveryGuardSettings settings, bool releaseCompletedOwnership = false, string expectedSessionId = null)
        {
            settings.Validate();
            if (string.IsNullOrWhiteSpace(evidence) || !TransitionAllowed(expectedStage, nextStage) ||
                (releaseCompletedOwnership && nextStage != RecoveryStage.Complete))
                throw new ArgumentException("RecoveryStageTransitionInvalid");
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertActionSession(state, expectedSessionId);
                var transaction = state.Transaction;
                if (transaction.Stage != expectedStage)
                    throw new InvalidOperationException("RecoveryStageConflict");
                if (expectedStage == RecoveryStage.Cooldown && transaction.NextAttemptUtcTicks > nowUtc.Ticks &&
                    nextStage == RecoveryStage.Claim)
                    throw new InvalidOperationException("RecoveryCooldownNotElapsed");
                if (expectedStage != RecoveryStage.Cooldown && expectedStage != RecoveryStage.Blocked &&
                    nowUtc.Ticks >= transaction.StageDeadlineUtcTicks &&
                    nextStage != RecoveryStage.Cooldown && nextStage != RecoveryStage.Blocked && nextStage != RecoveryStage.Cancelled)
                    throw new InvalidOperationException("RecoveryStageDeadlineExceeded");
                if (nextStage == RecoveryStage.Complete && !RecoveryDecisionPolicy.HasFreshVerificationEvidence(state, nowUtc))
                    throw new InvalidOperationException("RecoveryBusinessVerificationIncomplete");
                if ((nextStage == RecoveryStage.Cooldown || nextStage == RecoveryStage.Blocked) &&
                    expectedStage != RecoveryStage.Cooldown && expectedStage != RecoveryStage.Blocked)
                {
                    var resumable = transaction.ActionEpoch == epoch &&
                        (expectedStage == RecoveryStage.SafeStop || expectedStage == RecoveryStage.Retire || expectedStage == RecoveryStage.Launch);
                    transaction.RetryActionStage = resumable ? (RecoveryStage?)expectedStage : null;
                    transaction.RetryActionEpoch = resumable ? epoch : 0;
                }
                transaction.Stage = nextStage;
                transaction.StageStartedUtcTicks = nowUtc.Ticks;
                transaction.StageDeadlineUtcTicks = nowUtc.AddSeconds(nextStage == RecoveryStage.Verify
                    ? settings.VerificationTimeoutSeconds : settings.StageTimeoutSeconds).Ticks;
                transaction.Reason = Limit(evidence);
                transaction.NextAttemptUtcTicks = nextStage == RecoveryStage.Cooldown
                    ? nowUtc.AddSeconds(settings.CooldownSeconds).Ticks : 0;
                if (nextStage == RecoveryStage.Verify)
                {
                    transaction.VerificationStartedUtcTicks = nowUtc.Ticks;
                    transaction.VerifiedBusinessCommitCount = 0;
                    transaction.OrphanVerificationUntilUtcTicks = 0;
                }
                if (nextStage != RecoveryStage.Cooldown && nextStage != RecoveryStage.Blocked && nextStage != RecoveryStage.Claim)
                    transaction.LastProgressUtcTicks = nowUtc.Ticks;
                if (releaseCompletedOwnership)
                    ReleaseCompletedTakeoverUnsafe(state, transactionId, epoch, owner);
                return true;
            });
        }

        public void ResumeRecoveryAction(string transactionId, long epoch, RecoveryProcessIdentity owner,
            DateTime nowUtc, RecoveryGuardSettings settings)
        {
            settings.Validate();
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                var transaction = state.Transaction;
                if (transaction.Stage != RecoveryStage.Cooldown || transaction.NextAttemptUtcTicks > nowUtc.Ticks ||
                    transaction.RetryActionEpoch != epoch || transaction.ActionEpoch != epoch ||
                    (transaction.RetryActionStage != RecoveryStage.SafeStop && transaction.RetryActionStage != RecoveryStage.Retire &&
                        transaction.RetryActionStage != RecoveryStage.Launch) || transaction.SafetyAuthorityId == null)
                    throw new InvalidOperationException("RecoveryActionResumeDenied");
                transaction.Stage = transaction.RetryActionStage.Value;
                transaction.StageStartedUtcTicks = nowUtc.Ticks;
                transaction.StageDeadlineUtcTicks = nowUtc.AddSeconds(settings.StageTimeoutSeconds).Ticks;
                transaction.NextAttemptUtcTicks = 0;
                transaction.Reason = "CooldownElapsed;ReconcilePersistedAction";
                // Restarting an observation budget is not action completion
                // and must not move business or substantial progress clocks.
                return true;
            });
        }

        public RecoveryLaunchFence CaptureLaunchFence(bool recovery, string watchdogSessionId, DateTime nowUtc)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe();
                var fence = new RecoveryLaunchFence
                {
                    IsRecovery = recovery,
                    Authorization = state.Token() ?? new RecoveryAuthorizationToken
                    {
                        InstallationId = state.InstallationId, TakeoverEpoch = state.LastTakeoverEpoch
                    }
                };
                AssertFence(state, fence, nowUtc);
                if (recovery && state.Transaction != null && !state.Transaction.OwnershipReleased)
                    throw new InvalidOperationException("RecoveryWatchdogSupersededByGuard");
                if (recovery && (string.IsNullOrWhiteSpace(watchdogSessionId) || state.Intent.WatchdogSessionId != watchdogSessionId))
                    throw new InvalidOperationException("RecoveryWatchdogSessionSuperseded");
                return fence;
            });
        }

        public RecoveryLaunchFence CaptureGuardLaunchFence(string transactionId, long epoch,
            RecoveryProcessIdentity owner, DateTime nowUtc)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe();
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertLaunchStage(state, nowUtc);
                return new RecoveryLaunchFence { IsRecovery = true, Authorization = state.Token() };
            });
        }

        public RecoveryControlState ReadOwnedStage(string transactionId, long epoch,
            RecoveryProcessIdentity owner, RecoveryStage expectedStage, DateTime nowUtc, string expectedSessionId = null)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe();
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertActionSession(state, expectedSessionId);
                if (state.Transaction.Stage != expectedStage || nowUtc.Ticks >= state.Transaction.StageDeadlineUtcTicks)
                    throw new InvalidOperationException("RecoveryOwnedStageMismatchOrExpired");
                return state;
            });
        }

        // Caller verifies the old safety worker against its durable Supervisor
        // record. No process is stopped here and historical launches stay intact.
        public void ReconcileAdoptedSafetyAction(string transactionId, long epoch, RecoveryProcessIdentity owner,
            string sessionId, long actionEpoch, string safetyAuthorityId, RecoveryProcessIdentity main,
            ProcessObservation mainObservation, RecoveryProcessIdentity safetyWorker,
            ProcessObservation safetyObservation, DateTime nowUtc)
        {
            if (main?.IsValid() != true || mainObservation != ProcessObservation.Exited ||
                safetyWorker?.IsValid() != true || safetyObservation != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryAdoptedSafetyExitUnproven");
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertActionSession(state, sessionId);
                var tx = state.Transaction;
                if (tx.Stage != RecoveryStage.SafeStop || nowUtc.Ticks >= tx.StageDeadlineUtcTicks ||
                    actionEpoch <= 0 || actionEpoch >= epoch || tx.ActionEpoch != actionEpoch ||
                    string.IsNullOrEmpty(safetyAuthorityId) || tx.SafetyAuthorityId != safetyAuthorityId ||
                    tx.LaunchOperationId != null || state.Intent.MainProcess?.Matches(main) != true ||
                    state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                    throw new InvalidOperationException("RecoveryAdoptedSafetyReconciliationUnavailable");
                var directory = PathOf("action-history");
                Directory.CreateDirectory(directory);
                AtomicWrite(Path.Combine(directory, Guid.Parse(transactionId).ToString("N") + "." + epoch + ".adopted-safety.json"),
                    Json().Serialize(new { Transaction = tx, SafetyWorker = safetyWorker, ArchivedUtcTicks = nowUtc.Ticks }));
                tx.ActionEpoch = 0;
                tx.SafetyAuthorityId = null;
                tx.RetryActionEpoch = 0;
                tx.RetryActionStage = null;
                tx.Reason = "AdoptedSafetyRetired;NewSafetyRequired";
                return true;
            });
        }

        public RecoveryTakeoverTransaction RestartTerminalRecoveryAttempt(string transactionId, long epoch,
            RecoveryProcessIdentity owner, string sessionId, string safetyAuthorityId, string launchOperationId,
            RecoveryProcessIdentity safetyWorker, ProcessObservation safetyObservation, DateTime nowUtc)
        {
            if (safetyWorker?.IsValid() != true || safetyObservation != ProcessObservation.Exited)
                throw new InvalidOperationException("RecoveryRetrySafetyExitUnproven");
            return Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertActionSession(state, sessionId);
                var tx = state.Transaction;
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == launchOperationId);
                if ((tx.Stage != RecoveryStage.SafeStop && tx.Stage != RecoveryStage.Launch) ||
                    nowUtc.Ticks >= tx.StageDeadlineUtcTicks ||
                    tx.SafetyAuthorityId != safetyAuthorityId || tx.LaunchOperationId != launchOperationId ||
                    string.IsNullOrEmpty(safetyAuthorityId) ||
                    !((launch?.State == "Exited" && launch.Process?.IsValid() == true) ||
                      (launch?.State == "StartFailed" && launch.FailureEvidence == "ReservationExpiredBeforeConsumption" && launch.Process == null)) ||
                    !state.MatchesBoundTerminalLaunch(launch) ||
                    state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                    throw new InvalidOperationException("RecoveryRetryAttemptRequiresReconciliation");
                var directory = PathOf("action-history");
                Directory.CreateDirectory(directory);
                AtomicWrite(Path.Combine(directory, Guid.Parse(transactionId).ToString("N") + "." + epoch + ".json"),
                    Json().Serialize(new { Transaction = tx, SafetyWorker = safetyWorker, Launch = launch, ArchivedUtcTicks = nowUtc.Ticks }));
                var duration = tx.StageDeadlineUtcTicks - tx.StageStartedUtcTicks;
                tx.Epoch = state.LastTakeoverEpoch = checked(state.LastTakeoverEpoch + 1);
                tx.Stage = RecoveryStage.SafeStop;
                tx.StageStartedUtcTicks = nowUtc.Ticks;
                tx.StageDeadlineUtcTicks = checked(nowUtc.Ticks + duration);
                tx.ActionEpoch = 0;
                tx.SafetyAuthorityId = null;
                tx.LaunchOperationId = null;
                tx.RetryActionStage = null;
                tx.RetryActionEpoch = 0;
                tx.VerifiedBusinessCommitCount = 0;
                tx.VerificationStartedUtcTicks = 0;
                tx.Reason = "TerminalAttemptArchived;NewSafetyAttempt";
                return tx;
            });
        }

        public RecoverySessionRollover ReserveSessionRollover(string transactionId, long epoch, RecoveryProcessIdentity owner,
            string sourceAuthoritySha256, ProcessObservation oldMain, DateTime nowUtc)
        {
            if (oldMain != ProcessObservation.Exited || sourceAuthoritySha256 == null || sourceAuthoritySha256.Length != 64 ||
                !sourceAuthoritySha256.All(c => Uri.IsHexDigit(c)))
                throw new ArgumentException("RecoverySessionRolloverEvidenceInvalid");
            return Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertRolloverStage(state, nowUtc);
                var previous = state.SessionRollover;
                if (previous?.State == "Activated" && previous.TransactionId == transactionId && previous.Epoch == epoch &&
                    previous.AuthorizationId == state.Intent.AuthorizationId && previous.IntentVersion == state.Intent.IntentVersion &&
                    previous.SourceAuthoritySha256 == sourceAuthoritySha256 && previous.NextSessionId == state.Intent.WatchdogSessionId &&
                    previous.PreviousRunId == state.Intent.RunId && previous.PreviousMain?.Matches(state.Intent.MainProcess) == true)
                    return previous;
                if (previous != null && previous.State == "Reserved")
                {
                    if (previous.TransactionId != transactionId || previous.Epoch != epoch ||
                        previous.AuthorizationId != state.Intent.AuthorizationId || previous.IntentVersion != state.Intent.IntentVersion ||
                        previous.SourceAuthoritySha256 != sourceAuthoritySha256 || previous.PreviousSessionId != state.Intent.WatchdogSessionId)
                        throw new InvalidOperationException("RecoverySessionRolloverRequiresReconciliation");
                    return previous;
                }
                if (!RecoveryGuardSupervisorProtocol.IsId(state.Intent.WatchdogSessionId))
                    throw new InvalidOperationException("RecoverySessionRolloverSourceMissing");
                if (previous != null)
                {
                    var directory = PathOf("session-history");
                    Directory.CreateDirectory(directory);
                    AtomicWrite(Path.Combine(directory, Guid.Parse(previous.OperationId).ToString("N") + ".json"), Json().Serialize(previous));
                }
                state.SessionRollover = new RecoverySessionRollover
                {
                    OperationId = Guid.NewGuid().ToString("N"), NextSessionId = Guid.NewGuid().ToString("N"),
                    PreviousSessionId = state.Intent.WatchdogSessionId, SourceAuthoritySha256 = sourceAuthoritySha256,
                    PreviousSafetyAuthorityId = state.Transaction.SafetyAuthorityId, PreviousLaunchOperationId = state.Transaction.LaunchOperationId,
                    AuthorizationId = state.Intent.AuthorizationId, IntentVersion = state.Intent.IntentVersion,
                    TransactionId = transactionId, Epoch = epoch, PreviousRunId = state.Intent.RunId,
                    RootRunId = state.Intent.RootRunId,
                    ConfigurationIdentity = state.Intent.ConfigurationIdentity, PreviousMain = state.Intent.MainProcess, State = "Reserved"
                };
                return state.SessionRollover;
            });
        }

        public void ActivateSessionRollover(string transactionId, long epoch, RecoveryProcessIdentity owner,
            string operationId, RecoveryProcessIdentity sessionHost, ProcessObservation hostObservation, DateTime nowUtc)
        {
            if (sessionHost?.IsValid() != true || hostObservation != ProcessObservation.ExactAlive)
                throw new ArgumentException("RecoverySessionHostUnproven");
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertRolloverStage(state, nowUtc);
                var rollover = state.SessionRollover;
                if (rollover == null || rollover.OperationId != operationId || rollover.TransactionId != transactionId || rollover.Epoch != epoch ||
                    rollover.AuthorizationId != state.Intent.AuthorizationId || rollover.IntentVersion != state.Intent.IntentVersion ||
                    rollover.PreviousRunId != state.Intent.RunId || rollover.PreviousMain?.Matches(state.Intent.MainProcess) != true)
                    throw new InvalidOperationException("RecoverySessionRolloverBindingMismatch");
                if (rollover.State == "Activated" && rollover.NextSessionId == state.Intent.WatchdogSessionId &&
                    rollover.SessionHost?.Matches(sessionHost) == true) return true;
                if (rollover.State != "Reserved" || rollover.PreviousSessionId != state.Intent.WatchdogSessionId)
                    throw new InvalidOperationException("RecoverySessionRolloverRequiresReconciliation");
                rollover.State = "Activated"; rollover.SessionHost = sessionHost;
                state.Intent.WatchdogSessionId = rollover.NextSessionId;
                // The retired session's action evidence remains in its stores
                // and the archived handoff. New actions must bind new identities.
                state.Transaction.ActionEpoch = 0;
                state.Transaction.SafetyAuthorityId = null;
                state.Transaction.LaunchOperationId = null;
                state.Transaction.RetryActionStage = null;
                state.Transaction.RetryActionEpoch = 0;
                return true;
            });
        }

        public void AssertCheckpointSessionRollover(string previousSessionId, string nextSessionId, string checkpointRunId,
            string checkpointRootRunId, RecoveryProcessIdentity main, DateTime nowUtc)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                AssertAllowed(state, state.Token(), nowUtc);
                var rollover = state.SessionRollover;
                var transaction = state.Transaction;
                if (rollover?.State != "Activated" || rollover.PreviousSessionId != previousSessionId ||
                    rollover.NextSessionId != nextSessionId || state.Intent.WatchdogSessionId != nextSessionId ||
                    rollover.AuthorizationId != state.Intent.AuthorizationId || rollover.IntentVersion != state.Intent.IntentVersion ||
                    rollover.ConfigurationIdentity != state.Intent.ConfigurationIdentity ||
                    !SameRolloverRun(rollover.RootRunId, state.Intent.RootRunId) ||
                    !SameRolloverRun(rollover.PreviousRunId, checkpointRunId) || !SameRolloverRun(rollover.RootRunId, checkpointRootRunId) ||
                    transaction == null || transaction.OwnershipReleased || transaction.TransactionId != rollover.TransactionId ||
                    transaction.Epoch != rollover.Epoch || transaction.LeaseUntilUtcTicks <= nowUtc.Ticks ||
                    (transaction.Stage != RecoveryStage.Launch && transaction.Stage != RecoveryStage.Verify) ||
                    nowUtc.Ticks >= transaction.StageDeadlineUtcTicks || main?.IsValid() != true ||
                    !state.Launches.Any(l => l.State == "Started" && l.OperationId == transaction.LaunchOperationId &&
                        state.Matches(l.Authorization) && l.Process?.Matches(main) == true))
                    throw new InvalidOperationException("RecoveryCheckpointSessionRolloverUnproven");
                return true;
            });
        }

        private static bool SameRolloverRun(string left, string right) => Guid.TryParse(left, out var a) &&
            Guid.TryParse(right, out var b) && a != Guid.Empty && a == b;

        private static void AssertRolloverStage(RecoveryControlState state, DateTime nowUtc)
        {
            if (state.Transaction.Stage != RecoveryStage.SafeStop || nowUtc.Ticks >= state.Transaction.StageDeadlineUtcTicks ||
                state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                throw new InvalidOperationException("RecoverySessionRolloverStageOrLaunchUnproven");
        }

        public void BindRecoveryAction(string transactionId, long epoch, RecoveryProcessIdentity owner,
            RecoveryStage expectedStage, string safetyAuthorityId, string launchOperationId, DateTime nowUtc, string expectedSessionId = null)
        {
            if (!RecoveryGuardSupervisorProtocol.IsId(safetyAuthorityId) ||
                (expectedStage != RecoveryStage.SafeStop && expectedStage != RecoveryStage.Launch) ||
                (expectedStage == RecoveryStage.SafeStop && launchOperationId != null) ||
                (expectedStage == RecoveryStage.Launch && !RecoveryGuardSupervisorProtocol.IsId(launchOperationId)))
                throw new ArgumentException("RecoveryActionBindingInvalid");
            Mutate(state =>
            {
                AssertOwner(state, transactionId, epoch, owner, nowUtc);
                AssertActionSession(state, expectedSessionId);
                var transaction = state.Transaction;
                if (transaction.Stage != expectedStage || nowUtc.Ticks >= transaction.StageDeadlineUtcTicks)
                    throw new InvalidOperationException("RecoveryOwnedStageMismatchOrExpired");
                if ((transaction.ActionEpoch != 0 && transaction.ActionEpoch != epoch) ||
                    (transaction.SafetyAuthorityId != null && transaction.SafetyAuthorityId != safetyAuthorityId) ||
                    (transaction.LaunchOperationId != null && transaction.LaunchOperationId != launchOperationId) ||
                    (expectedStage == RecoveryStage.Launch && transaction.SafetyAuthorityId == null))
                    throw new InvalidOperationException("RecoveryActionRequiresReconciliation");
                transaction.ActionEpoch = epoch;
                transaction.SafetyAuthorityId = safetyAuthorityId;
                transaction.LaunchOperationId = launchOperationId;
                return true;
            });
        }

        private static void AssertActionSession(RecoveryControlState state, string expectedSessionId)
        {
            if (expectedSessionId != null && (state.Intent?.WatchdogSessionId != expectedSessionId ||
                state.SessionRollover?.State == "Reserved" && state.SessionRollover.AuthorizationId == state.Intent.AuthorizationId &&
                    state.SessionRollover.IntentVersion == state.Intent.IntentVersion))
                throw new InvalidOperationException("RecoveryActionSessionSuperseded");
        }

        public void ReleaseCompletedTakeover(string transactionId, long epoch, RecoveryProcessIdentity owner)
        {
            Mutate(state =>
            {
                ReleaseCompletedTakeoverUnsafe(state, transactionId, epoch, owner);
                return true;
            });
        }

        public void ReconcileInactiveTakeover(RecoveryAuthorizationToken expectedIntent, string transactionId, long epoch,
            RecoveryProcessIdentity previousOwner, ProcessObservation ownerObservation,
            RecoveryProcessIdentity previousMain, ProcessObservation mainObservation, string safetyAuthorityId,
            RecoveryProcessIdentity safetyWorker, ProcessObservation safetyObservation, DateTime nowUtc)
        {
            if (nowUtc.Kind != DateTimeKind.Utc || ownerObservation != ProcessObservation.Exited ||
                mainObservation != ProcessObservation.Exited || safetyObservation != ProcessObservation.Exited ||
                safetyWorker?.IsValid() != true || string.IsNullOrEmpty(safetyAuthorityId))
                throw new InvalidOperationException("RecoveryInactiveProcessEvidenceUnproven");
            Mutate(state =>
            {
                var tx = state.Transaction;
                if (!state.Matches(expectedIntent) || tx == null || tx.TransactionId != transactionId || tx.Epoch != epoch ||
                    tx.Owner?.Matches(previousOwner) != true || state.Intent.MainProcess?.Matches(previousMain) != true ||
                    tx.SafetyAuthorityId != safetyAuthorityId || tx.AuthorizationId != state.Intent.AuthorizationId ||
                    tx.LeaseUntilUtcTicks > nowUtc.Ticks ||
                    (state.Intent.DesiredState == RecoveryDesiredState.Run && state.Observation?.Expired != true) ||
                    state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                    throw new InvalidOperationException("RecoveryInactiveReconciliationUnavailable");
                if (tx.OwnershipReleased) return true;
                if (state.Intent.DesiredState == RecoveryDesiredState.Run)
                {
                    state.Intent.IntentVersion = checked(state.Intent.IntentVersion + 1);
                    state.Intent.DesiredState = RecoveryDesiredState.Stopped;
                    state.Intent.Reason = "SupervisionExpired;OwnershipClosed";
                }
                tx.Stage = RecoveryStage.Cancelled;
                tx.OwnershipReleased = true;
                tx.Reason = "InactiveOwnershipClosedAfterProcessAndSafetyReconciliation";
                return true;
            });
        }

        public void ReconcileCompletedTakeover(string transactionId, long epoch,
            RecoveryProcessIdentity previousOwner, ProcessObservation previousOwnerObservation,
            RecoveryProcessIdentity currentMain, ProcessObservation currentMainObservation, DateTime nowUtc)
        {
            ReconcileHandback(transactionId, epoch, previousOwner, previousOwnerObservation,
                currentMain, currentMainObservation, nowUtc, false);
        }

        public void ReconcileVerifiedTakeover(string transactionId, long epoch,
            RecoveryProcessIdentity previousOwner, ProcessObservation previousOwnerObservation,
            RecoveryProcessIdentity currentMain, ProcessObservation currentMainObservation, DateTime nowUtc)
        {
            ReconcileHandback(transactionId, epoch, previousOwner, previousOwnerObservation,
                currentMain, currentMainObservation, nowUtc, true);
        }

        public bool TryObserveOrphanVerification(string transactionId, long epoch, RecoveryProcessIdentity previousOwner,
            ProcessObservation ownerObservation, RecoveryProcessIdentity main, ProcessObservation mainObservation, DateTime nowUtc)
        {
            if (ownerObservation != ProcessObservation.Exited || mainObservation != ProcessObservation.ExactAlive)
                throw new InvalidOperationException("RecoveryOrphanVerificationProcessUnproven");
            return Mutate(state =>
            {
                AssertAllowed(state, state.Token(), nowUtc);
                var tx = state.Transaction;
                if (tx?.Stage != RecoveryStage.Verify || tx.OwnershipReleased || tx.TransactionId != transactionId || tx.Epoch != epoch ||
                    state.LastTakeoverEpoch != epoch || tx.AuthorizationId != state.Intent.AuthorizationId || tx.IntentVersion != state.Intent.IntentVersion ||
                    tx.Owner?.Matches(previousOwner) != true || tx.LeaseUntilUtcTicks > nowUtc.Ticks ||
                    state.Intent.MainProcess?.Matches(main) != true || tx.VerificationEvidenceMaxAgeSeconds <= 0 ||
                    tx.VerificationEvidenceMaxAgeSeconds > 3600 ||
                    !state.Launches.Any(l => l.State == "Started" && state.Matches(l.Authorization) && l.Process?.Matches(main) == true) ||
                    state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" ||
                        (l.State == "Started" && l.Process?.Matches(main) != true)))
                    throw new InvalidOperationException("RecoveryOrphanVerificationUnavailable");
                // Once established, neither another observer nor new snapshots
                // move this deadline. It grants no new actor or output lease.
                if (tx.OrphanVerificationUntilUtcTicks > 0)
                    return nowUtc.Ticks < tx.OrphanVerificationUntilUtcTicks;
                if (!RecoveryDecisionPolicy.IsFreshSnapshot(state, state.Observation.LastSnapshot, nowUtc.Ticks,
                        new RecoveryGuardSettings { SnapshotMaxAgeSeconds = tx.VerificationEvidenceMaxAgeSeconds }) ||
                    !state.Observation.LastSnapshot.Channels.Any(c => c.Eligible))
                    throw new InvalidOperationException("RecoveryOrphanVerificationSourceUnavailable");
                var duration = tx.StageDeadlineUtcTicks - tx.StageStartedUtcTicks;
                if (duration <= 0 || duration > TimeSpan.FromMinutes(30).Ticks)
                    throw new InvalidOperationException("RecoveryOrphanVerificationBudgetUnproven");
                tx.OrphanVerificationUntilUtcTicks = checked(nowUtc.Ticks + duration);
                return true;
            });
        }

        private void ReconcileHandback(string transactionId, long epoch,
            RecoveryProcessIdentity previousOwner, ProcessObservation previousOwnerObservation,
            RecoveryProcessIdentity currentMain, ProcessObservation currentMainObservation, DateTime nowUtc,
            bool completeVerification)
        {
            // Probe outside the short authority transaction. Bind both probes
            // to the identities checked again inside it; never replace ownership
            // merely because a lease expired or a PID could not be queried.
            if (previousOwnerObservation != ProcessObservation.Exited ||
                currentMainObservation != ProcessObservation.ExactAlive)
                throw new InvalidOperationException("RecoveryCompletionProcessEvidenceUnproven");
            Mutate(state =>
            {
                AssertAllowed(state, state.Token(), nowUtc);
                if (state.Intent.MainProcess?.Matches(currentMain) != true ||
                    state.Transaction?.LeaseUntilUtcTicks > nowUtc.Ticks)
                    throw new InvalidOperationException("RecoveryCompletionReconciliationNotAvailable");
                if (completeVerification)
                {
                    if (!RecoveryDecisionPolicy.HasFreshVerificationEvidence(state, nowUtc) ||
                        (state.Transaction.OrphanVerificationUntilUtcTicks > 0 && nowUtc.Ticks >= state.Transaction.OrphanVerificationUntilUtcTicks) ||
                        !state.Launches.Any(l => l.State == "Started" && state.Matches(l.Authorization) &&
                            l.Process?.Matches(currentMain) == true))
                        throw new InvalidOperationException("RecoveryCompletionEvidenceIncomplete");
                    // Mutate persists only after the owner, intent and business
                    // evidence checks below succeed. No new output epoch is issued.
                    state.Transaction.Stage = RecoveryStage.Complete;
                }
                // No new actuator owner or epoch: the verified main must keep
                // its existing output token. This only closes a terminal record.
                ReleaseCompletedTakeoverUnsafe(state, transactionId, epoch, previousOwner);
                if (completeVerification)
                    state.Transaction.Reason = "VerifiedOwnershipReconciledAfterOwnerExit";
                return true;
            });
        }

        private static void ReleaseCompletedTakeoverUnsafe(RecoveryControlState state,
            string transactionId, long epoch, RecoveryProcessIdentity owner)
        {
            var transaction = state.Transaction;
            if (transaction == null || transaction.TransactionId != transactionId || transaction.Epoch != epoch ||
                state.LastTakeoverEpoch != epoch || transaction.Owner?.Matches(owner) != true ||
                transaction.AuthorizationId != state.Intent?.AuthorizationId || transaction.IntentVersion != state.Intent.IntentVersion)
                throw new InvalidOperationException("RecoveryCompletionOwnerFenced");
            if (transaction.Stage != RecoveryStage.Complete || transaction.VerifiedBusinessCommitCount < 2 ||
                state.Intent.DesiredState != RecoveryDesiredState.Run ||
                state.Observation?.LastVerifiedBusinessCommitUtcTicks <= transaction.VerificationStartedUtcTicks ||
                state.Observation?.LastSnapshot?.MainProcess?.Matches(state.Intent.MainProcess) != true ||
                state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed"))
                throw new InvalidOperationException("RecoveryCompletionEvidenceIncomplete");
            if (!transaction.OwnershipReleased)
            {
                state.Observation.SuspectCount = 0;
                state.Observation.SuspectSinceUtcTicks = 0;
            }
            transaction.OwnershipReleased = true;
        }

        public void AssertLaunchFence(RecoveryLaunchFence fence, DateTime nowUtc)
        {
            Locked(() => { AssertFence(ReadUnsafe(), fence, nowUtc); return true; });
        }

        private static void AssertFence(RecoveryControlState state, RecoveryLaunchFence fence, DateTime nowUtc)
        {
            if (fence?.SchemaVersion != 1 || fence.Authorization == null ||
                state.InstallationId != fence.Authorization.InstallationId ||
                state.LastTakeoverEpoch != fence.Authorization.TakeoverEpoch)
                throw new InvalidOperationException("RecoveryLaunchFenceStale");
            if (fence.IsRecovery) AssertExistingWatchdogLaunchAllowed(state, fence.Authorization, nowUtc);
            else if (state.Transaction != null && !state.Transaction.OwnershipReleased)
                throw new InvalidOperationException("RecoveryManualLaunchTakeoverInProgress");
        }

        public void AssertStartedLaunch(RecoveryLaunchFence fence, string operationId,
            RecoveryProcessIdentity process, DateTime nowUtc)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                AssertFence(state, fence, nowUtc);
                if (fence.IsRecovery)
                {
                    var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                    if (launch?.State != "Started" || !TokensEqual(launch.Authorization, fence.Authorization) ||
                        process?.Matches(launch.Process) != true)
                        throw new InvalidOperationException("RecoveryStartedLaunchIdentityMismatch");
                }
                return true;
            });
        }

        public RecoveryLaunchReservation ReserveLaunch(RecoveryAuthorizationToken token, string operationId,
            DateTime nowUtc, DateTime expiresUtc)
        {
            return ReserveLaunch(token, operationId, nowUtc, expiresUtc, null, 0, null, null, null);
        }

        public RecoveryLaunchReservation ReserveAndBindRecoveryLaunch(RecoveryAuthorizationToken token,
            string transactionId, long epoch, RecoveryProcessIdentity owner, string safetyAuthorityId,
            string operationId, DateTime nowUtc, DateTime expiresUtc, string sessionId)
        {
            if (!RecoveryGuardSupervisorProtocol.IsId(transactionId) ||
                !RecoveryGuardSupervisorProtocol.IsId(safetyAuthorityId) ||
                !RecoveryGuardSupervisorProtocol.IsId(sessionId) || owner?.IsValid() != true || epoch <= 0)
                throw new ArgumentException("RecoveryLaunchBindingInvalid");
            return ReserveLaunch(token, operationId, nowUtc, expiresUtc, transactionId, epoch, owner, safetyAuthorityId, sessionId);
        }

        private RecoveryLaunchReservation ReserveLaunch(RecoveryAuthorizationToken token, string operationId,
            DateTime nowUtc, DateTime expiresUtc, string transactionId, long epoch,
            RecoveryProcessIdentity owner, string safetyAuthorityId, string sessionId)
        {
            if (!Guid.TryParse(operationId, out _) || nowUtc.Kind != DateTimeKind.Utc || expiresUtc.Kind != DateTimeKind.Utc ||
                expiresUtc <= nowUtc || expiresUtc > nowUtc.AddMinutes(2))
                throw new ArgumentException("RecoveryLaunchReservationInvalid");
            return Mutate(state =>
            {
                AssertExistingWatchdogLaunchAllowed(state, token, nowUtc);
                if (transactionId != null)
                {
                    AssertOwner(state, transactionId, epoch, owner, nowUtc);
                    AssertActionSession(state, sessionId);
                    AssertLaunchStage(state, nowUtc);
                    var tx = state.Transaction;
                    if (tx.ActionEpoch != epoch || tx.SafetyAuthorityId != safetyAuthorityId ||
                        (tx.LaunchOperationId != null && tx.LaunchOperationId != operationId))
                        throw new InvalidOperationException("RecoveryActionRequiresReconciliation");
                    // Reservation and action binding are one durable commit,
                    // before preparing the external strict launch intent.
                    tx.LaunchOperationId = operationId;
                }
                var existing = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (existing != null)
                {
                    if (!TokensEqual(existing.Authorization, token))
                        throw new InvalidOperationException("RecoveryLaunchOperationIdentityConflict");
                    if (existing.State == "Reserved") AssertLaunchStage(state, nowUtc);
                    return existing; // caller reconciles; never issues a second Process.Start
                }
                AssertLaunchStage(state, nowUtc);
                // A reservation which was never consumed cannot have created a
                // process. Consumption checks the same deadline under this lock.
                foreach (var expired in state.Launches.Where(l => l.State == "Reserved" && l.ExpiresUtcTicks <= nowUtc.Ticks))
                {
                    expired.State = "StartFailed";
                    expired.FailureEvidence = "ReservationExpiredBeforeConsumption";
                }
                if (state.Launches.Any(l => l.State == "Reserved" || l.State == "Consumed" || l.State == "Started"))
                    throw new InvalidOperationException("RecoveryLaunchInFlight");
                var archivePath = PathOf("launch-history");
                var archivedName = Path.Combine(archivePath, Guid.Parse(operationId).ToString("N") + ".json");
                if (File.Exists(archivedName))
                    throw new InvalidOperationException("RecoveryLaunchAlreadyArchived");
                if (state.Launches.Count >= 32)
                {
                    Directory.CreateDirectory(archivePath);
                    foreach (var terminal in state.Launches.Where(l => l.State == "Exited" || l.State == "StartFailed").ToArray())
                    {
                        AtomicWrite(Path.Combine(archivePath, Guid.Parse(terminal.OperationId).ToString("N") + ".json"),
                            Json().Serialize(terminal));
                        state.Launches.Remove(terminal);
                    }
                }
                var reservation = new RecoveryLaunchReservation
                {
                    OperationId = operationId,
                    Authorization = token,
                    ExpiresUtcTicks = expiresUtc.Ticks,
                    State = "Reserved"
                };
                state.Launches.Add(reservation);
                return reservation;
            });
        }

        public bool ConfirmReservationExpiredBeforeConsumption(string operationId, DateTime nowUtc)
        {
            if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("RecoveryExpiryClockInvalid");
            return Mutate(state =>
            {
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (launch?.State == "StartFailed" && launch.FailureEvidence == "ReservationExpiredBeforeConsumption") return true;
                if (launch?.State != "Reserved" || launch.Process != null || launch.ExpiresUtcTicks <= 0 || launch.ExpiresUtcTicks > nowUtc.Ticks)
                    return false;
                // ConsumeLaunch uses this same state mutex before Process.Start.
                // Whichever transition wins prevents the other from succeeding.
                launch.State = "StartFailed";
                launch.FailureEvidence = "ReservationExpiredBeforeConsumption";
                return true;
            });
        }

        public void ConsumeLaunch(RecoveryAuthorizationToken token, string operationId, DateTime nowUtc)
        {
            Mutate(state =>
            {
                AssertExistingWatchdogLaunchAllowed(state, token, nowUtc);
                AssertLaunchStage(state, nowUtc);
                var reservation = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (reservation == null || !TokensEqual(reservation.Authorization, token) || reservation.State != "Reserved" ||
                    reservation.ExpiresUtcTicks <= nowUtc.Ticks)
                    throw new InvalidOperationException("RecoveryLaunchConsumedExpiredOrMissing");
                reservation.State = "Consumed";
                return true;
            });
        }

        // Reconciliation must be able to record a created process even if a stop
        // was committed during Process.Start. Recording never grants output.
        public void RecordLaunchResult(string operationId, RecoveryProcessIdentity process, bool startFailed)
        {
            if (!startFailed && process?.IsValid() != true)
                throw new ArgumentException("RecoveryLaunchProcessInvalid");
            Mutate(state =>
            {
                var reservation = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if ((reservation?.State == "Started" || reservation?.State == "Exited") && !startFailed && process?.Matches(reservation.Process) == true)
                    return true;
                if (reservation == null || reservation.State != "Consumed")
                    throw new InvalidOperationException("RecoveryLaunchNotConsumed");
                if (!startFailed && !string.Equals(state.MainExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RecoveryLaunchedPathMismatch");
                reservation.State = startFailed ? "StartFailed" : "Started";
                reservation.Process = process;
                return true;
            });
        }

        public bool ConfirmLaunchedProcessExited(string operationId)
        {
            var original = Read().Launches.SingleOrDefault(l => l.OperationId == operationId);
            if (original?.State == "Exited") return true;
            if (original?.State != "Started" ||
                RecoveryProcessProbe.Observe(original.Process, RecoveryProcessProbe.ReadBootId()) != ProcessObservation.Exited)
                return false;
            // Probe outside the authority lock; commit only against that exact
            // recorded instance. Lease expiry and missing response are not exit.
            return Mutate(state =>
            {
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (launch?.State == "Exited") return true;
                if (launch?.State != "Started" || launch.Process?.Matches(original.Process) != true) return false;
                launch.State = "Exited";
                return true;
            });
        }

        public void CompleteRecoveredRunBeforeAdmission(RecoveryAuthorizationToken token, string operationId,
            RecoveryProcessIdentity process, string rootRunId, string previousRunId,
            string configurationIdentity, DateTime nowUtc)
        {
            Mutate(state =>
            {
                // The main has validated durable remaining-cycle evidence, but
                // has not admitted an output-producing run. Bind its terminal
                // report to the exact creation receipt, never the latest token.
                AssertIntent(state, token, nowUtc);
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (launch?.State != "Started" || !TokensEqual(launch.Authorization, token) ||
                    process?.Matches(launch.Process) != true ||
                    state.Intent.RootRunId != rootRunId || state.Intent.RunId != previousRunId ||
                    state.Intent.ConfigurationIdentity != configurationIdentity)
                    throw new InvalidOperationException("RecoveryTerminalLaunchIdentityMismatch");
                state.Intent.IntentVersion = checked(state.Intent.IntentVersion + 1);
                state.Intent.DesiredState = RecoveryDesiredState.Completed;
                state.Intent.Reason = "FormalRunAlreadyCompletedAtRecoveryStartup";
                if (state.Transaction?.Active == true)
                {
                    state.Transaction.Stage = RecoveryStage.Cancelled;
                    state.Transaction.Reason = "BusinessAlreadyCompleted";
                }
                // Completion cancels further launches. It does not claim two
                // new commits or release hardware/transaction ownership.
                return true;
            });
        }

        public void BindRecoveredRun(RecoveryAuthorizationToken token, string operationId, string runId,
            RecoveryProcessIdentity process, DateTime nowUtc)
        {
            if (!Guid.TryParse(runId, out _)) throw new ArgumentException("RecoveryRunIdInvalid");
            Mutate(state =>
            {
                AssertExistingWatchdogLaunchAllowed(state, token, nowUtc);
                var reservation = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (reservation?.State != "Started" || !TokensEqual(reservation.Authorization, token) ||
                    process?.Matches(reservation.Process) != true)
                    throw new InvalidOperationException("RecoveryLaunchBindingDenied");
                state.Intent.MainProcess = process;
                state.Intent.RunId = runId;
                if (state.Observation != null)
                {
                    state.Observation.LastSnapshot = null;
                    state.Observation.ChannelClocks.Clear();
                }
                return true;
            });
        }

        public void ContinueInProcess(RecoveryAuthorizationToken token, string rootRunId, string runId,
            string configurationIdentity, RecoveryProcessIdentity process, DateTime nowUtc)
        {
            RequireRunIdentity(rootRunId, runId, configurationIdentity, process, nowUtc);
            Mutate(state =>
            {
                AssertIntent(state, token, nowUtc);
                if (state.Intent.RootRunId != rootRunId || state.Intent.ConfigurationIdentity != configurationIdentity ||
                    state.Intent.MainProcess?.Matches(process) != true || state.Transaction?.Active == true)
                    throw new InvalidOperationException("RecoveryInProcessContinuationDenied");
                state.Intent.RunId = runId;
                if (state.Observation != null)
                {
                    state.Observation.LastSnapshot = null;
                    state.Observation.ChannelClocks.Clear();
                }
                return true;
            });
        }

        public bool IsRecoveredRunReadyForAdmission(RecoveryAuthorizationToken token, string operationId,
            RecoveryProcessIdentity main, DateTime nowUtc)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe();
                AssertExistingWatchdogLaunchAllowed(state, token, nowUtc);
                var launch = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
                if (main?.Matches(state.Intent.MainProcess) != true || launch?.State != "Started" ||
                    !TokensEqual(launch.Authorization, token) || launch.Process?.Matches(main) != true)
                    throw new InvalidOperationException("RecoveryAdmissionProcessMismatch");
                var transaction = state.Transaction;
                if (transaction == null || transaction.OwnershipReleased) return true;
                if (transaction.Stage != RecoveryStage.Launch && transaction.Stage != RecoveryStage.Verify)
                    throw new InvalidOperationException("RecoveryAdmissionStageNotReady");
                if (transaction.WorkerRetirementRequestedUtcTicks > 0 ||
                    nowUtc.Ticks >= transaction.StageDeadlineUtcTicks || nowUtc.Ticks >= transaction.LeaseUntilUtcTicks)
                    throw new InvalidOperationException("RecoveryAdmissionStageOrLeaseExpired");
                // Process.Start may reach the main's cold admission before the
                // Guard receives its launch response. Wait with outputs disabled
                // until the persisted Verify transition, without relaxing it.
                return transaction.Stage == RecoveryStage.Verify;
            });
        }

        public void AssertRunAllowed(RecoveryAuthorizationToken token, RecoveryProcessIdentity main, DateTime nowUtc)
        {
            Locked(() =>
            {
                var state = ReadUnsafe();
                // Disabling Guard must not stop a currently authorized trial.
                // Admission expiry applies to a new automatic action, not the
                // existing process's normal control loop.
                AssertIntent(state, token, nowUtc);
                if (main?.Matches(state.Intent.MainProcess) != true)
                    throw new InvalidOperationException("RecoveryOutputProcessMismatch");
                if (state.Transaction?.Active == true &&
                    (state.Transaction.Stage != RecoveryStage.Verify || !state.Launches.Any(l =>
                        l.State == "Started" && TokensEqual(l.Authorization, token) && l.Process?.Matches(main) == true)))
                    throw new InvalidOperationException("RecoveryOutputTakeoverFenced");
                return true;
            });
        }

        public void AssertLaunchAllowed(RecoveryAuthorizationToken token, DateTime nowUtc)
        {
            Locked(() => { AssertExistingWatchdogLaunchAllowed(ReadUnsafe(), token, nowUtc); return true; });
        }

        private static void AssertLaunchStage(RecoveryControlState state, DateTime nowUtc)
        {
            var transaction = state.Transaction;
            if (transaction == null || transaction.OwnershipReleased) return;
            if (transaction.Stage != RecoveryStage.Launch)
                throw new InvalidOperationException("RecoveryLaunchStageNotReady");
            if (transaction.WorkerRetirementRequestedUtcTicks > 0 ||
                nowUtc.Ticks >= transaction.StageDeadlineUtcTicks || nowUtc.Ticks >= transaction.LeaseUntilUtcTicks)
                throw new InvalidOperationException("RecoveryLaunchStageOrLeaseExpired");
        }

        private static void AssertExistingWatchdogLaunchAllowed(RecoveryControlState state,
            RecoveryAuthorizationToken token, DateTime nowUtc)
        {
            AssertIntent(state, token, nowUtc);
            if (state.Observation?.Established == true ||
                (state.Transaction != null && !state.Transaction.OwnershipReleased))
            {
                AssertAllowed(state, token, nowUtc);
                return;
            }
            var age = nowUtc.Ticks - state.Intent.IssuedUtcTicks;
            if (state.Observation?.Expired == true || age < 0 || age >= TimeSpan.FromMinutes(60).Ticks ||
                string.IsNullOrEmpty(state.Intent.LegacyStartupAdmissionSessionId) ||
                state.Intent.LegacyStartupAdmissionSessionId != state.Intent.WatchdogSessionId ||
                state.Intent.LegacyStartupAdmissionBootId != state.Intent.MainProcess?.BootId ||
                state.Intent.LegacyStartupAdmissionBootId != RecoveryProcessProbe.ReadBootId())
                throw new InvalidOperationException("RecoveryLegacyStartupAdmissionMissingOrExpired");
        }

        private static void AssertAllowed(RecoveryControlState state, RecoveryAuthorizationToken token, DateTime nowUtc)
        {
            AssertIntent(state, token, nowUtc);
            var observation = state.Observation;
            if (observation?.Established != true)
                throw new InvalidOperationException("RecoverySupervisionNotEstablished");
            if (observation?.Expired == true || (observation != null &&
                (nowUtc.Ticks < observation.LastGuardUtcTicks - TimeSpan.FromSeconds(5).Ticks ||
                 nowUtc.Ticks - observation.LastGuardUtcTicks >= TimeSpan.FromMinutes(60).Ticks)))
                throw new InvalidOperationException("RecoverySupervisionExpiredOrClockInvalid");
        }

        private static void AssertIntent(RecoveryControlState state, RecoveryAuthorizationToken token, DateTime nowUtc)
        {
            if (nowUtc.Kind != DateTimeKind.Utc || !state.Matches(token) || state.Intent.DesiredState != RecoveryDesiredState.Run)
                throw new InvalidOperationException("RecoveryAuthorizationRevoked");
            if (RecoveryRevocationSignal.IsStopped(token) || RecoveryRevocationSignal.IsPaused(token))
                throw new InvalidOperationException("RecoveryAuthorizationRevokedByIndependentSignal");
        }

        private static void AssertOwner(RecoveryControlState state, string transactionId, long epoch,
            RecoveryProcessIdentity owner, DateTime nowUtc)
        {
            AssertExecutionWorkerMayAct(state, owner);
            AssertAllowed(state, state.Token(), nowUtc);
            var transaction = state.Transaction;
            if (transaction?.Active != true || transaction.TransactionId != transactionId || transaction.Epoch != epoch ||
                transaction.Owner?.Matches(owner) != true || transaction.WorkerRetirementRequestedUtcTicks > 0 ||
                transaction.LeaseUntilUtcTicks <= nowUtc.Ticks)
                throw new InvalidOperationException("RecoveryOwnerFenced");
        }

        private static bool TransitionAllowed(RecoveryStage from, RecoveryStage to)
        {
            if (from == RecoveryStage.Complete || from == RecoveryStage.Cancelled) return false;
            if (to == RecoveryStage.Blocked || to == RecoveryStage.Cooldown || to == RecoveryStage.Cancelled) return true;
            return (from == RecoveryStage.Claim && to == RecoveryStage.SafeStop) ||
                (from == RecoveryStage.SafeStop && to == RecoveryStage.Retire) ||
                (from == RecoveryStage.Retire && to == RecoveryStage.Launch) ||
                (from == RecoveryStage.Launch && to == RecoveryStage.Verify) ||
                (from == RecoveryStage.Verify && to == RecoveryStage.Complete) ||
                ((from == RecoveryStage.Cooldown || from == RecoveryStage.Blocked) && to == RecoveryStage.Claim);
        }

        private static void RequireRunIdentity(string rootRunId, string runId, string configuration,
            RecoveryProcessIdentity process, DateTime nowUtc)
        {
            if (!Guid.TryParse(rootRunId, out var root) || root == Guid.Empty || !Guid.TryParse(runId, out var run) ||
                run == Guid.Empty || string.IsNullOrWhiteSpace(configuration) || configuration.Length > 200 ||
                process?.IsValid() != true || nowUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("RecoveryRunIdentityInvalid");
        }

        private static bool TokensEqual(RecoveryAuthorizationToken a, RecoveryAuthorizationToken b) =>
            a != null && b != null && a.InstallationId == b.InstallationId && a.AuthorizationId == b.AuthorizationId &&
            a.IntentVersion == b.IntentVersion && a.TakeoverEpoch == b.TakeoverEpoch;

        private T Mutate<T>(Func<RecoveryControlState, T> mutation) => Locked(() =>
        {
            var state = ReadUnsafe();
            var before = Json().Serialize(state);
            var result = mutation(state);
            if (Json().Serialize(state) != before) WriteStateUnsafe(state);
            return result;
        });

        private RecoveryControlState ReadUnsafe()
        {
            var envelope = ReadEnvelopeUnsafe("control-state.json");
            var head = Json().Deserialize<CommitHead>(ReadBounded(PathOf("commit-head.json")));
            if (head?.SchemaVersion != 1 || head.Revision != envelope.Revision || head.Sha256 != envelope.Sha256)
                throw new InvalidDataException("RecoveryAuthorityCommitUncertain");
            var state = Decode<RecoveryControlState>(envelope);
            if (state?.SchemaVersion != 1 || state.Revision != envelope.Revision || state.Revision < 1 ||
                !Guid.TryParse(state.InstallationId, out var installation) || installation == Guid.Empty ||
                string.IsNullOrWhiteSpace(state.BenchId) || string.IsNullOrWhiteSpace(state.MainExecutablePath) ||
                state.Launches == null || (state.Intent != null &&
                (!Guid.TryParse(state.Intent.AuthorizationId, out _) || state.Intent.IntentVersion < 1 ||
                 !Enum.IsDefined(typeof(RecoveryDesiredState), state.Intent.DesiredState))))
                throw new InvalidDataException("RecoveryAuthorityInvalid");
            if (ReadBounded(PathOf("registration-id")) != state.InstallationId)
                throw new InvalidDataException("RecoveryRegistrationIdentityMismatch");
            return state;
        }

        private void WriteStateUnsafe(RecoveryControlState state)
        {
            state.Revision = checked(state.Revision + 1);
            var payload = Json().Serialize(state);
            var envelope = new Envelope { SchemaVersion = 1, Revision = state.Revision, Payload = payload, Sha256 = Hash(payload) };
            var encoded = Json().Serialize(envelope);
            if (Encoding.UTF8.GetByteCount(encoded) > MaximumRecordBytes)
                throw new InvalidDataException("RecoveryAuthorityTooLarge");
            AtomicWrite(PathOf("commit-head.json"), Json().Serialize(new CommitHead
            {
                SchemaVersion = 1, Revision = envelope.Revision, Sha256 = envelope.Sha256
            }));
            AtomicWrite(PathOf("control-state.json"), encoded);
        }

        private void WriteEnvelopeUnsafe<T>(string name, T value, long revision)
        {
            var payload = Json().Serialize(value);
            AtomicWrite(PathOf(name), Json().Serialize(new Envelope
            {
                SchemaVersion = 1, Revision = revision, Payload = payload, Sha256 = Hash(payload)
            }));
        }

        private Envelope ReadEnvelopeUnsafe(string name)
        {
            Envelope value;
            try { value = Json().Deserialize<Envelope>(ReadBounded(PathOf(name))); }
            catch (ArgumentException ex) { throw new InvalidDataException("RecoveryRecordJsonInvalid:" + name, ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("RecoveryRecordJsonInvalid:" + name, ex); }
            if (value?.SchemaVersion != 1 || value.Payload == null || value.Sha256 != Hash(value.Payload))
                throw new InvalidDataException("RecoveryRecordIntegrityInvalid:" + name);
            return value;
        }

        private static T Decode<T>(Envelope envelope) => Json().Deserialize<T>(envelope.Payload);
        private string PathOf(string name) => Path.Combine(_root, name);
        private static string Limit(string value) => (value ?? string.Empty).Length <= 1000
            ? value ?? string.Empty : value.Substring(0, 1000);
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = MaximumRecordBytes, RecursionLimit = 40 };

        private T Locked<T>(Func<T> action, string mutexName = null)
        {
            mutexName = mutexName ?? _mutexName;
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            Mutex opened;
            try { opened = Mutex.OpenExisting(mutexName, MutexRights.Synchronize | MutexRights.Modify); }
            catch (WaitHandleCannotBeOpenedException)
            {
                try { opened = new Mutex(false, mutexName, out _, security); }
                catch (UnauthorizedAccessException)
                {
                    opened = Mutex.OpenExisting(mutexName, MutexRights.Synchronize | MutexRights.Modify);
                }
            }
            using (var mutex = opened)
            {
                var held = false;
                try
                {
                    try { held = mutex.WaitOne(3000); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("RecoveryAuthorityBusy");
                    return action();
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length <= 0 || stream.Length > MaximumRecordBytes)
                    throw new InvalidDataException("RecoveryRecordSizeInvalid");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true))) return reader.ReadToEnd();
            }
        }

        private static void AtomicWrite(string path, string payload)
        {
            var bytes = new UTF8Encoding(false).GetBytes(payload);
            if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("RecoveryRecordTooLarge");
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
    }
}
