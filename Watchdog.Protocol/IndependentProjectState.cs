using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class IndependentProcessIdentity
    {
        public int Pid { get; set; }
        public long StartUtcTicks { get; set; }
        public int WindowsSessionId { get; set; }
        public string ExecutablePath { get; set; }
        public string SessionToken { get; set; }

        public void Validate()
        {
            if (Pid <= 0 || StartUtcTicks <= 0 || WindowsSessionId < 0 ||
                !Path.IsPathRooted(ExecutablePath ?? string.Empty) ||
                !Guid.TryParseExact(SessionToken, "N", out _))
                throw new InvalidDataException("IndependentProcessIdentityInvalid");
        }

        public bool Matches(IndependentProcessIdentity other) => other != null && Pid == other.Pid &&
            StartUtcTicks == other.StartUtcTicks && WindowsSessionId == other.WindowsSessionId &&
            string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            SessionToken == other.SessionToken;
    }

    public sealed class IndependentLaunchTicket
    {
        public string Nonce { get; set; }
        public string RequestId { get; set; }
        public long Generation { get; set; }
        public long IntentRevision { get; set; }
        public string ExecutableSha256 { get; set; }
        public int WindowsSessionId { get; set; }
        public long ExpiresUtcTicks { get; set; }
        public long DispatchStartedUtcTicks { get; set; }
        public bool Revoked { get; set; }
        public IndependentProcessIdentity Consumer { get; set; }
    }

    public sealed class IndependentIntentAudit
    {
        public long UtcTicks { get; set; }
        public long Revision { get; set; }
        public string Source { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int[] Before { get; set; }
        public int[] After { get; set; }
        public string Reason { get; set; }
    }

    // This aggregate is the production authority boundary. Intent revocation,
    // ticket consumption and replacement binding must never be separate writes.
    // The host must protect its directory and registered configuration with ACLs
    // before opening this store; a JSON file is not a security boundary by itself.
    public sealed class IndependentProjectState
    {
        public int SchemaVersion { get; set; } = 2;
        public long Revision { get; set; }
        public long RunStartedUtcTicks { get; set; }
        public long StartupDeadlineUtcTicks { get; set; }
        public string RootRunId { get; set; }
        public bool Maintenance { get; set; } = true;
        public bool SafetyCleanupPending { get; set; }
        public IndependentRunIntent Intent { get; set; }
        public IndependentRecoveryTransaction Transaction { get; set; }
        public IndependentProcessIdentity Controller { get; set; }
        public IndependentLaunchTicket Ticket { get; set; }
        public IndependentIntentAudit[] Audit { get; set; } = Array.Empty<IndependentIntentAudit>();

        public void Validate()
        {
            if (SchemaVersion != 2 || Revision <= 0 || Audit == null || Audit.Length > 32)
                throw new InvalidDataException("IndependentProjectStateInvalid");
            Intent?.Validate(); Transaction?.Validate(); Controller?.Validate();
            if (Intent != null && (!Guid.TryParseExact(RootRunId, "N", out _) || RunStartedUtcTicks <= 0 || StartupDeadlineUtcTicks <= RunStartedUtcTicks ||
                StartupDeadlineUtcTicks - RunStartedUtcTicks != TimeSpan.FromMilliseconds(Intent.StartupBudgetMs).Ticks))
                throw new InvalidDataException("IndependentRunStartupDeadlineInvalid");
            if (Transaction != null && Intent == null || SafetyCleanupPending && Transaction == null)
                throw new InvalidDataException("IndependentProjectAuthorityMissing");
            if (Ticket != null)
            {
                Ticket.Consumer?.Validate();
                if (!Guid.TryParseExact(Ticket.Nonce, "N", out _) || Transaction == null ||
                    Ticket.RequestId != Transaction.RequestId || Ticket.Generation != Transaction.Generation ||
                    Ticket.IntentRevision <= 0 || Ticket.ExpiresUtcTicks <= 0 || Ticket.WindowsSessionId <= 0 ||
                    Ticket.DispatchStartedUtcTicks < 0 || Ticket.DispatchStartedUtcTicks >= Ticket.ExpiresUtcTicks ||
                    (Ticket.Consumer != null && Ticket.DispatchStartedUtcTicks == 0) ||
                    Ticket.ExecutableSha256?.Length != 64 || !Ticket.ExecutableSha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("IndependentLaunchTicketInvalid");
            }
            foreach (var row in Audit)
                if (row == null || row.Revision <= 0 || row.UtcTicks <= 0 ||
                    string.IsNullOrWhiteSpace(row.Source) || row.Source.Length > 64 ||
                    row.Reason == null || row.Reason.Length > 192 || row.Before == null || row.After == null ||
                    row.Before.Length > 12 || row.After.Length > 12 ||
                    row.Before.Concat(row.After).Any(c => c < 1 || c > 12))
                    throw new InvalidDataException("IndependentIntentAuditInvalid");
        }
    }

    public sealed class IndependentProjectStateStore
    {
        private readonly string _path;
        private readonly string _mutexName;
        public IndependentProjectStateStore(string directory)
        {
            directory = Path.GetFullPath(directory);
            _path = Path.Combine(directory, "independent-project-state.json");
            _mutexName = "Global\\MTTF-IndependentProject-" +
                SupervisorProtocol.ComputeTextSha256(directory.ToUpperInvariant());
        }

        private T Locked<T>(Func<T> operation)
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                MutexRights.FullControl, AccessControlType.Allow));
            using (var identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new MutexAccessRule(identity.User,
                    MutexRights.FullControl, AccessControlType.Allow));
            using (var mutex = new Mutex(false, _mutexName, out _, security))
            {
                var owned = false;
                try
                {
                    try { owned = mutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("IndependentProjectStateBusy");
                    return operation();
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }

        public IndependentProjectState Read() => Locked(ReadUnsafe);
        private IndependentProjectState ReadUnsafe()
        {
            if (!File.Exists(_path)) return null;
            var state = BoundedJson.Read<IndependentProjectState>(_path);
            state.Validate();
            return state;
        }

        // All mutations receive freshly loaded state. No externally cached state
        // object can be saved over a newer operator action.
        public T Update<T>(long expectedRevision, Func<IndependentProjectState, T> update)
        {
            return Locked(() =>
            {
                var state = ReadUnsafe() ?? new IndependentProjectState();
                if (state.Revision != expectedRevision)
                    throw new InvalidOperationException("IndependentProjectRevisionConflict");
                var result = update(state);
                state.Revision = checked(expectedRevision + 1);
                state.Validate();
                BoundedJson.Write(_path, state);
                return result;
            });
        }

        public IndependentRunIntent UpdateOperatorIntent(long expectedRevision, string source, string reason,
            long now, Action<IndependentRunIntent> change)
        {
            return Update(expectedRevision, state =>
            {
                if (state.Intent == null) throw new InvalidOperationException("IndependentRunNotArmed");
                var before = state.Intent.SelectedChannels.ToArray();
                var run = state.Intent.RunId; var epoch = state.Intent.RunEpoch;
                change(state.Intent);
                if (state.Intent.RunId != run || state.Intent.RunEpoch != epoch)
                    throw new InvalidOperationException("OperatorUpdateCannotReplaceRun");
                state.Intent.Revision = checked(state.Intent.Revision + 1);
                state.Intent.Validate();
                if (state.Ticket != null) state.Ticket.Revoked = true;
                // Keep an active safety transaction and its frozen old controller
                // identity. Revoking restart does not mean safety cleanup finished.
                AppendAudit(state, source, reason, now, before);
                return state.Intent;
            });
        }

        public void RecordControllerManualStop(IndependentProcessIdentity controller, string runId, long runEpoch,
            string commandId, long now)
        {
            controller.Validate();
            if (!Guid.TryParseExact(commandId, "N", out _) || now <= 0)
                throw new ArgumentException("IndependentManualStopCommandInvalid");
            Locked(() =>
            {
                var state = ReadUnsafe();
                if (state?.Intent == null || !controller.Matches(state.Controller) ||
                    state.Intent.RunId != runId || state.Intent.RunEpoch != runEpoch)
                    throw new InvalidOperationException("IndependentManualStopStaleController");
                if (state.Intent.ManualStopped && !state.Intent.Armed) return true;
                var before = state.Intent.SelectedChannels.ToArray();
                state.Intent.ManualStopped = true;
                state.Intent.Armed = false;
                state.Intent.Revision = checked(state.Intent.Revision + 1);
                if (state.Ticket != null) state.Ticket.Revoked = true;
                AppendAudit(state, "OperatorStop", "CommandId=" + commandId, now, before);
                state.Revision = checked(state.Revision + 1);
                state.Validate();
                BoundedJson.Write(_path, state);
                return true;
            });
        }

        public void SetControllerManualPause(IndependentProcessIdentity controller, string runId, long runEpoch,
            bool paused, string commandId, long now)
        {
            controller.Validate();
            if (!Guid.TryParseExact(commandId, "N", out _) || now <= 0)
                throw new ArgumentException("IndependentPauseCommandInvalid");
            Locked(() =>
            {
                var state = ReadUnsafe();
                if (state?.Intent == null || !controller.Matches(state.Controller) ||
                    state.Intent.RunId != runId || state.Intent.RunEpoch != runEpoch)
                    throw new InvalidOperationException("IndependentPauseStaleController");
                if (!paused && (state.Maintenance || state.SafetyCleanupPending ||
                    state.Transaction?.IsTerminal == false || state.Intent.ManualStopped || !state.Intent.Armed))
                    throw new InvalidOperationException("IndependentPauseResumeNotAuthorized");
                if (state.Intent.ManualPaused == paused) return true;
                var before = state.Intent.SelectedChannels.ToArray();
                state.Intent.ManualPaused = paused;
                if (!paused)
                {
                    // Only an explicit continue opens a new startup observation
                    // window; service restarts and duplicate commands cannot.
                    state.RunStartedUtcTicks = now;
                    state.StartupDeadlineUtcTicks = checked(now + TimeSpan.FromMilliseconds(state.Intent.StartupBudgetMs).Ticks);
                }
                state.Intent.Revision = checked(state.Intent.Revision + 1);
                if (state.Transaction?.Phase == IndependentRecoveryPhase.Verified)
                {
                    // This consumed historical ticket cannot launch again. Keep
                    // the verified controller binding while explicit pause/resume
                    // updates its authority; a pending launch is revoked below.
                    state.Transaction.IntentRevision = state.Intent.Revision;
                    state.Transaction.Revision = checked(state.Transaction.Revision + 1);
                }
                else if (state.Ticket != null) state.Ticket.Revoked = true;
                AppendAudit(state, paused ? "OperatorPause" : "OperatorContinue", "CommandId=" + commandId, now, before);
                state.Revision = checked(state.Revision + 1);
                state.Validate();
                BoundedJson.Write(_path, state);
                return true;
            });
        }

        public IndependentRecoveryTransaction BeginRecovery(long expectedRevision, string executor, long now)
        {
            return Update(expectedRevision, state =>
            {
                if (state.Maintenance || state.SafetyCleanupPending || state.Controller == null || state.Intent == null)
                    throw new InvalidOperationException("IndependentProjectNotReadyForTakeover");
                state.Controller.Validate();
                state.Transaction = IndependentRecoveryTransitions.Begin(state.Transaction, state.Intent, executor, now);
                state.Ticket = null;
                state.SafetyCleanupPending = true;
                return state.Transaction;
            });
        }

        public void ArmManualRun(long expectedRevision, IndependentRunIntent intent,
            IndependentProcessIdentity controller, long now)
        {
            controller.Validate();
            Update(expectedRevision, state =>
            {
                if (state.Maintenance || state.SafetyCleanupPending || state.Transaction?.IsTerminal == false)
                    throw new InvalidOperationException("IndependentRecoveryOrMaintenanceStillActive");
                if (state.Intent?.RecoveryChannels().Length > 0)
                    throw new InvalidOperationException("IndependentManualRunAlreadyArmed");
                if (state.Intent != null && (state.Intent.RunId == intent.RunId ||
                    !string.Equals(state.Intent.ProjectDirectory, intent.ProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(state.Intent.DatabasePath, intent.DatabasePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("IndependentManualRunIdentityInvalid");
                var before = state.Intent?.SelectedChannels.ToArray() ?? Array.Empty<int>();
                intent.PermanentChannels = (state.Intent?.PermanentChannels ?? Array.Empty<int>())
                    .Union(intent.PermanentChannels ?? Array.Empty<int>()).OrderBy(c => c).ToArray();
                intent.Revision = checked((state.Intent?.Revision ?? 0) + 1);
                intent.Validate();
                if (!intent.Armed || intent.ManualStopped || intent.ManualPaused ||
                    !string.Equals(intent.ExecutablePath, controller.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentManualRunNotAuthorized");
                state.Intent = intent; state.Controller = controller; state.Ticket = null;
                state.RunStartedUtcTicks = now;
                state.RootRunId = intent.RunId;
                state.StartupDeadlineUtcTicks = checked(now + TimeSpan.FromMilliseconds(intent.StartupBudgetMs).Ticks);
                AppendAudit(state, "OperatorStart", "ManualRunArmed", now, before);
                return true;
            });
        }

        public IndependentRecoveryTransaction RetrySafetyCleanup(long expectedRevision, string executor, long now)
        {
            return Update(expectedRevision, state =>
            {
                if (!state.SafetyCleanupPending || state.Controller == null || state.Intent == null)
                    throw new InvalidOperationException("IndependentCleanupRetryNotRequired");
                state.Transaction = IndependentRecoveryTransitions.RetrySafetyCleanup(state.Transaction, state.Intent, executor, now);
                // No ticket can survive a retry, including maintenance/manual stop.
                state.Ticket = null;
                return state.Transaction;
            });
        }

        // Safety cleanup follows the already-authorized, frozen old session.
        // Operator revocation prevents restart, but must not strand the session
        // half-way through power-off / handle release / pressure confirmation.
        public IndependentRecoveryPhase CompleteSafetyStage(long expectedRevision, string executor,
            long generation, string requestId, IndependentRecoveryPhase completedPhase,
            bool confirmed, long now, long nextBudgetMs, string detail)
        {
            return Update(expectedRevision, state =>
            {
                var tx = state.Transaction;
                if (tx == null || tx.IsTerminal || !state.SafetyCleanupPending ||
                    tx.ExecutorIdentity != executor || tx.Generation != generation || tx.RequestId != requestId ||
                    tx.Phase != completedPhase || now < tx.LastAttemptUtcTicks ||
                    nextBudgetMs <= 0 || nextBudgetMs > 300000)
                    throw new InvalidOperationException("IndependentSafetyStageReceiptMismatch");
                if (completedPhase != IndependentRecoveryPhase.CooperativeStop &&
                    completedPhase != IndependentRecoveryPhase.PowerOff &&
                    completedPhase != IndependentRecoveryPhase.RetireControls &&
                    completedPhase != IndependentRecoveryPhase.OutputsSafe)
                    throw new InvalidOperationException("IndependentSafetyStageInvalid");
                // A late affirmative receipt is not current safety evidence.
                if (completedPhase != IndependentRecoveryPhase.CooperativeStop &&
                    (!confirmed || now >= tx.PhaseDeadlineUtcTicks))
                {
                    tx.Phase = IndependentRecoveryPhase.NeedsAttention;
                    tx.Detail = "SafetyCleanupUnconfirmed:" + completedPhase + ":" + Clip(detail, 192);
                    if (state.Ticket != null) state.Ticket.Revoked = true;
                    tx.Revision = checked(tx.Revision + 1);
                    return tx.Phase;
                }
                tx.Phase = completedPhase == IndependentRecoveryPhase.CooperativeStop ? IndependentRecoveryPhase.PowerOff :
                    completedPhase == IndependentRecoveryPhase.PowerOff ? IndependentRecoveryPhase.RetireControls :
                    completedPhase == IndependentRecoveryPhase.RetireControls ? IndependentRecoveryPhase.OutputsSafe :
                    IndependentRecoveryPhase.LaunchPending;
                tx.Revision = checked(tx.Revision + 1);
                tx.PhaseDeadlineUtcTicks = checked(now + TimeSpan.FromMilliseconds(nextBudgetMs).Ticks);
                tx.Detail = Clip(detail ?? string.Empty, 192);
                if (completedPhase == IndependentRecoveryPhase.OutputsSafe)
                {
                    state.SafetyCleanupPending = false;
                    var stillAuthorized = !state.Maintenance && state.Intent != null &&
                        state.Intent.Revision == tx.IntentRevision && state.Intent.RunId == tx.RunId &&
                        state.Intent.RunEpoch == tx.RunEpoch && tx.Channels.SequenceEqual(state.Intent.RecoveryChannels());
                    if (!stillAuthorized)
                    {
                        tx.Phase = IndependentRecoveryPhase.Cancelled;
                        tx.Detail = "SafetyCleanupCompleted;RestartAuthorityRevoked";
                        if (state.Ticket != null) state.Ticket.Revoked = true;
                    }
                }
                return tx.Phase;
            });
        }

        public IndependentLaunchTicket IssueLaunchTicket(long expectedRevision, string executor, long generation,
            string executableSha256, int windowsSessionId, long now)
        {
            return Update(expectedRevision, state =>
            {
                RequireLaunchAuthority(state, executor, generation);
                if (state.Ticket != null) throw new InvalidOperationException("IndependentTicketAlreadyIssued");
                state.Ticket = new IndependentLaunchTicket
                {
                    Nonce = Guid.NewGuid().ToString("N"), RequestId = state.Transaction.RequestId,
                    Generation = generation, IntentRevision = state.Intent.Revision,
                    ExecutableSha256 = executableSha256,
                    WindowsSessionId = windowsSessionId,
                    ExpiresUtcTicks = Math.Min(state.Transaction.PhaseDeadlineUtcTicks,
                        checked(now + TimeSpan.FromSeconds(30).Ticks))
                };
                if (state.Ticket.ExpiresUtcTicks <= now)
                    throw new InvalidOperationException("IndependentLaunchDeadlineExpired");
                return state.Ticket;
            });
        }

        public void MarkLaunchDispatched(long expectedRevision, string nonce, string executor, long now)
        {
            Update(expectedRevision, state =>
            {
                var ticket = state.Ticket;
                if (ticket == null || ticket.Nonce != nonce || ticket.Revoked || ticket.Consumer != null ||
                    ticket.DispatchStartedUtcTicks != 0 || now <= 0 || now >= ticket.ExpiresUtcTicks ||
                    now < state.Transaction.LastAttemptUtcTicks)
                    throw new InvalidOperationException("IndependentLaunchDispatchNotAuthorized");
                RequireLaunchAuthority(state, executor, ticket.Generation);
                ticket.DispatchStartedUtcTicks = now;
                return true;
            });
        }

        public IndependentRunIntent ConsumeLaunchTicket(long expectedRevision, string nonce,
            IndependentProcessIdentity consumer, string executableSha256, long now)
        {
            consumer.Validate();
            return Update(expectedRevision, state =>
            {
                var ticket = state.Ticket;
                if (ticket == null || ticket.Revoked || ticket.Consumer != null || ticket.Nonce != nonce ||
                    ticket.DispatchStartedUtcTicks <= 0 || now < ticket.DispatchStartedUtcTicks ||
                    ticket.ExpiresUtcTicks <= now || now < state.Transaction.LastAttemptUtcTicks ||
                    ticket.IntentRevision != state.Intent.Revision ||
                    consumer.WindowsSessionId != ticket.WindowsSessionId ||
                    !string.Equals(ticket.ExecutableSha256, executableSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(state.Intent.ExecutablePath, consumer.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentTicketNotConsumable");
                RequireLaunchAuthority(state, state.Transaction.ExecutorIdentity, ticket.Generation);
                if (state.Controller != null && state.Controller.Pid == consumer.Pid &&
                    state.Controller.StartUtcTicks == consumer.StartUtcTicks)
                    throw new InvalidOperationException("IndependentTicketRequiresNewProcess");
                ticket.Consumer = consumer;
                state.Transaction.ReplacementPid = consumer.Pid;
                state.Transaction.ReplacementStartUtcTicks = consumer.StartUtcTicks;
                state.Transaction.Revision = checked(state.Transaction.Revision + 1);
                return state.Intent;
            });
        }

        public void CommitReplacementRun(long expectedRevision, IndependentProcessIdentity consumer,
            string runId, long runEpoch, long now)
        {
            Update(expectedRevision, state =>
            {
                var ticket = state.Ticket;
                if (state.Maintenance || state.SafetyCleanupPending || ticket == null || ticket.Revoked ||
                    !consumer.Matches(ticket.Consumer) || ticket.IntentRevision != state.Intent.Revision ||
                    state.Intent.RecoveryChannels().Length == 0 || state.Transaction == null ||
                    (state.Transaction.Phase != IndependentRecoveryPhase.LaunchPending &&
                     state.Transaction.Phase != IndependentRecoveryPhase.Verifying) ||
                    now >= state.Transaction.PhaseDeadlineUtcTicks || now < state.Transaction.LastAttemptUtcTicks ||
                    !Guid.TryParseExact(runId, "N", out _) || runId == state.Intent.RunId ||
                    runEpoch <= state.Intent.RunEpoch)
                    throw new InvalidOperationException("IndependentReplacementRunNotAuthorized");
                var before = state.Intent.SelectedChannels.ToArray();
                state.Intent.RunId = runId; state.Intent.RunEpoch = runEpoch;
                state.Intent.Revision = checked(state.Intent.Revision + 1);
                state.Transaction.RunId = runId; state.Transaction.RunEpoch = runEpoch;
                state.Transaction.IntentRevision = state.Intent.Revision;
                state.Transaction.Revision = checked(state.Transaction.Revision + 1);
                // The consumed ticket can never launch again. Retain its original
                // revision as evidence instead of minting a new permission.
                state.Controller = consumer;
                state.RunStartedUtcTicks = now;
                state.StartupDeadlineUtcTicks = checked(now + TimeSpan.FromMilliseconds(state.Intent.StartupBudgetMs).Ticks);
                AppendAudit(state, "ReplacementBootstrap", "ReplacementRunCommitted", now, before);
                return true;
            });
        }

        private static void RequireLaunchAuthority(IndependentProjectState state, string executor, long generation)
        {
            if (state.Maintenance || state.SafetyCleanupPending || state.Transaction == null ||
                state.Transaction.Phase != IndependentRecoveryPhase.LaunchPending)
                throw new InvalidOperationException("IndependentSafetyCleanupNotComplete");
            IndependentRecoveryTransitions.RequireCurrent(state.Transaction, state.Intent, executor, generation);
        }

        private static void AppendAudit(IndependentProjectState state, string source, string reason, long now, int[] before)
        {
            state.Audit = state.Audit.Concat(new[] { new IndependentIntentAudit
            {
                UtcTicks = now, Revision = state.Intent.Revision, Source = Clip(source, 64),
                Reason = Clip(reason ?? string.Empty, 192), RunId = state.Intent.RunId, RunEpoch = state.Intent.RunEpoch,
                Before = before, After = state.Intent.SelectedChannels.ToArray()
            } }).Skip(Math.Max(0, state.Audit.Length + 1 - 32)).ToArray();
        }

        private static string Clip(string value, int length) => value == null || value.Length <= length ? value : value.Substring(0, length);
    }
}
