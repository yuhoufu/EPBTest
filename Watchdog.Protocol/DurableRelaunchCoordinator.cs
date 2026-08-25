using System;
using System.Globalization;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Durable process-relaunch state machine.  Every externally useful
    /// transition is committed synchronously before the result advertises an
    /// action.  Any commit failure closes the circuit in memory and returns no
    /// launch/attach/commit action.
    /// </summary>
    public sealed class DurableRelaunchCoordinator
    {
        private readonly object _gate = new object();
        private readonly string _sessionId;
        private readonly IDurableRelaunchStore _store;
        private DurableRelaunchPermitRecord _record;
        private long _nextGeneration;

        public DurableRelaunchCoordinator(
            string sessionId,
            IDurableRelaunchStore store,
            int defaultMaximumProcessRelaunches)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("SessionId不能为空。", nameof(sessionId));
            _sessionId = sessionId;
            _store = store ?? throw new ArgumentNullException(nameof(store));
            DefaultMaximumProcessRelaunches = Math.Max(0, defaultMaximumProcessRelaunches);
            LoadAndValidate();
        }

        public int DefaultMaximumProcessRelaunches { get; }

        public DurableRelaunchPermitRecord Snapshot
        {
            get { lock (_gate) return _record?.Clone(); }
        }

        public DurableRelaunchResult ApproveOrGetExisting(DurableRelaunchRequest request)
        {
            if (request == null) return Reject("RequestMissing");
            lock (_gate)
            {
                if (_record.State == DurableRelaunchPermitState.Blocked)
                    return ResultLocked(false, false, false, true, "RecoveryBlocked");
                if (_record.State == DurableRelaunchPermitState.Revoked)
                    return ResultLocked(false, false, false, false, "Revoked");

                var normalized = NormalizeRequest(request);
                if (normalized.MaximumProcessRelaunches <= 0)
                    return ResultLocked(false, false, false, true, "RelaunchBudgetZero");

                if (_record.State == DurableRelaunchPermitState.Approved ||
                    _record.State == DurableRelaunchPermitState.LaunchIntent ||
                    _record.State == DurableRelaunchPermitState.Started ||
                    _record.State == DurableRelaunchPermitState.Attached)
                {
                    if (!RequestMatches(_record, normalized))
                        return BlockLocked("ConcurrentPermitScopeMismatch");
                    return ResultLocked(true, true, false, false, "ExistingPermit");
                }

                if (_record.State == DurableRelaunchPermitState.Failed &&
                    _record.ConsecutiveFailures >= normalized.MaximumProcessRelaunches)
                    return BlockLocked("RelaunchBudgetExhausted");

                var generation = Math.Max(_nextGeneration, _record.Generation) + 1;
                _nextGeneration = generation;
                var candidate = new DurableRelaunchPermitRecord
                {
                    SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion,
                    SessionId = _sessionId,
                    Generation = generation,
                    PermitId = Guid.NewGuid().ToString("N"),
                    State = DurableRelaunchPermitState.Approved,
                    Fingerprint = normalized.Fingerprint,
                    ProgressToken = normalized.ProgressToken,
                    ProcessSource = normalized.ProcessSource,
                    RunId = normalized.RunId,
                    RecoveryStage = normalized.RecoveryStage,
                    PermitNonce = Guid.NewGuid().ToString("N"),
                    ConsecutiveFailures = _record.State == DurableRelaunchPermitState.Committed
                        ? 0
                        : _record.ConsecutiveFailures,
                    MaximumProcessRelaunches = normalized.MaximumProcessRelaunches,
                    LastTransitionUtcTicks = DateTime.UtcNow.Ticks
                };
                return CommitTransitionLocked(
                    candidate,
                    "Approve",
                    actionAllowed: true,
                    existing: false);
            }
        }

        public DurableRelaunchResult BeginLaunch(DurableRelaunchPermitIdentity identity)
        {
            lock (_gate)
            {
                if (!MatchesIdentityLocked(identity))
                    return RejectLocked("PermitIdentityMismatch");
                if (_record.State == DurableRelaunchPermitState.LaunchIntent)
                    return ResultLocked(true, true, false, false, "LaunchIntentAlreadyCommitted");
                if (_record.State != DurableRelaunchPermitState.Approved)
                    return IllegalTransitionLocked("BeginLaunchRequiresApproved");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.LaunchIntent;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return CommitTransitionLocked(candidate, "BeginLaunch", true, false);
            }
        }

        public DurableRelaunchResult CommitStarted(
            DurableRelaunchPermitIdentity identity,
            int processId,
            long processStartUtcTicks)
        {
            lock (_gate)
            {
                if (!MatchesIdentityLocked(identity))
                    return RejectLocked("PermitIdentityMismatch");
                if (processId <= 0 || processStartUtcTicks <= 0)
                    return BlockLocked("StartedIdentityMissing");
                if (_record.State == DurableRelaunchPermitState.Started)
                {
                    if (_record.ProcessId == processId &&
                        _record.ProcessStartUtcTicks == processStartUtcTicks)
                        return ResultLocked(true, true, false, false, "StartedAlreadyCommitted");
                    return BlockLocked("StartedIdentityChanged");
                }
                if (_record.State != DurableRelaunchPermitState.LaunchIntent)
                    return IllegalTransitionLocked("CommitStartedRequiresLaunchIntent");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Started;
                candidate.ProcessId = processId;
                candidate.ProcessStartUtcTicks = processStartUtcTicks;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return CommitTransitionLocked(candidate, "CommitStarted", true, false);
            }
        }

        public DurableRelaunchResult CommitAttached(
            DurableRelaunchPermitIdentity identity,
            int processId,
            long processStartUtcTicks)
        {
            lock (_gate)
            {
                if (!MatchesIdentityLocked(identity))
                    return RejectLocked("PermitIdentityMismatch");
                if (_record.State == DurableRelaunchPermitState.Attached)
                    return ResultLocked(true, true, false, false, "AttachedAlreadyCommitted");
                if (_record.State != DurableRelaunchPermitState.Started)
                    return IllegalTransitionLocked("CommitAttachedRequiresStarted");
                if (processId <= 0 || processStartUtcTicks <= 0 ||
                    _record.ProcessId != processId ||
                    _record.ProcessStartUtcTicks != processStartUtcTicks)
                    return BlockLocked("AttachedIdentityMismatch");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Attached;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return CommitTransitionLocked(candidate, "CommitAttached", true, false);
            }
        }

        public DurableRelaunchResult CommitRecoveryBatch(
            DurableRelaunchPermitIdentity identity,
            string runId,
            string progressToken,
            long recoveryCommitGeneration)
        {
            lock (_gate)
            {
                if (!MatchesIdentityLocked(identity))
                    return RejectLocked("PermitIdentityMismatch");
                if (_record.State == DurableRelaunchPermitState.Committed)
                    return ResultLocked(true, true, false, false, "RecoveryBatchAlreadyCommitted");
                if (_record.State != DurableRelaunchPermitState.Attached)
                    return IllegalTransitionLocked("CommitRecoveryBatchRequiresAttached");
                if (string.IsNullOrWhiteSpace(runId) ||
                    string.IsNullOrWhiteSpace(progressToken) ||
                    recoveryCommitGeneration <= 0)
                    return BlockLocked("RecoveryCommitEvidenceMissing");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Committed;
                candidate.RunId = runId;
                candidate.ProgressToken = progressToken;
                candidate.RecoveryCommitGeneration = recoveryCommitGeneration;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return CommitTransitionLocked(candidate, "CommitRecoveryBatch", true, false);
            }
        }

        public DurableRelaunchResult ClosePermitAndRegisterFailure(
            DurableRelaunchPermitIdentity identity,
            string failureCode,
            string failureReason = null,
            bool processIdentityMismatch = false)
        {
            lock (_gate)
            {
                if (!MatchesIdentityLocked(identity))
                    return RejectLocked("PermitIdentityMismatch");
                if (_record.State == DurableRelaunchPermitState.Blocked)
                    return ResultLocked(false, true, false, true, "RecoveryBlocked");
                if (_record.State == DurableRelaunchPermitState.Failed)
                    return ResultLocked(true, true, false, false, "FailureAlreadyRegistered");
                if (_record.State == DurableRelaunchPermitState.Committed ||
                    _record.State == DurableRelaunchPermitState.Revoked)
                    return RejectLocked("PermitAlreadyClosed");

                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Failed;
                candidate.ConsecutiveFailures = Math.Max(0, candidate.ConsecutiveFailures) + 1;
                candidate.LastFailureCode = string.IsNullOrWhiteSpace(failureCode)
                    ? "RecoveryProcessFailed"
                    : failureCode;
                candidate.LastFailureReason = failureReason ?? string.Empty;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                if (processIdentityMismatch ||
                    candidate.ConsecutiveFailures >= Math.Max(1, candidate.MaximumProcessRelaunches))
                    candidate.State = DurableRelaunchPermitState.Blocked;
                return CommitTransitionLocked(candidate, "ClosePermitAndRegisterFailure", false,
                    existing: false);
            }
        }

        public DurableRelaunchResult Revoke(string reason = "Revoked")
        {
            lock (_gate)
            {
                if (_record.State == DurableRelaunchPermitState.Revoked)
                    return ResultLocked(true, true, false, false, "AlreadyRevoked");
                if (_record.State == DurableRelaunchPermitState.Blocked)
                    return ResultLocked(false, true, false, true, "RecoveryBlocked");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Revoked;
                candidate.LastFailureCode = reason;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                return CommitTransitionLocked(candidate, "Revoke", false, false);
            }
        }

        /// <summary>
        /// Permanently closes the circuit after an external safety invariant
        /// fails (for example the surrounding journal snapshot cannot be
        /// committed).  This is intentionally separate from Revoke: a
        /// revoked operator session is not the same as a durable relaunch
        /// failure and must not be reopened by a later failure report.
        /// </summary>
        public DurableRelaunchResult Block(string reason = "RecoveryBlocked")
        {
            lock (_gate)
            {
                if (_record.State == DurableRelaunchPermitState.Blocked)
                    return ResultLocked(false, true, false, true, "RecoveryBlocked");
                return BlockLocked(string.IsNullOrWhiteSpace(reason)
                    ? "RecoveryBlocked"
                    : reason);
            }
        }

        /// <summary>
        /// Reconciles a persisted Started/Attached permit after process
        /// restart.  The process probe is deliberately called outside the
        /// coordinator lock; the identity is checked again before mutation.
        /// </summary>
        public DurableRelaunchResult RecoverAfterRestart(
            Func<int, long, DurableRelaunchProcessObservation> processProbe)
        {
            DurableRelaunchPermitRecord observed;
            lock (_gate)
            {
                if (_record.State == DurableRelaunchPermitState.LaunchIntent)
                    return BlockLocked("LaunchIntentOutcomeUnknown");
                if (_record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return ResultLocked(true, true, false,
                        _record.State == DurableRelaunchPermitState.Blocked,
                        "NoProcessReconciliationNeeded");
                observed = _record.Clone();
            }

            DurableRelaunchProcessObservation observation;
            try
            {
                observation = processProbe == null
                    ? DurableRelaunchProcessObservation.Unknown
                    : processProbe(observed.ProcessId, observed.ProcessStartUtcTicks);
            }
            catch
            {
                observation = DurableRelaunchProcessObservation.Unknown;
            }

            lock (_gate)
            {
                if (!MatchesIdentityLocked(observed.Identity))
                    return RejectLocked("PermitChangedDuringProbe");
                if (_record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return ResultLocked(true, true, false, false, "ReconciledByOtherThread");
                if (observation == DurableRelaunchProcessObservation.Alive)
                    return ResultLocked(true, true, false, false, "ProcessAlive");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Failed;
                candidate.ConsecutiveFailures++;
                candidate.LastFailureCode = observation == DurableRelaunchProcessObservation.IdentityMismatch
                    ? "ProcessIdentityReuse"
                    : observation == DurableRelaunchProcessObservation.Unknown
                        ? "OutcomeUnknown"
                        : "ProcessDeadAfterRestart";
                candidate.LastFailureReason = candidate.LastFailureCode;
                candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
                if (observation != DurableRelaunchProcessObservation.Dead ||
                    candidate.ConsecutiveFailures >= Math.Max(1, candidate.MaximumProcessRelaunches))
                    candidate.State = DurableRelaunchPermitState.Blocked;
                return CommitTransitionLocked(candidate, "RecoverAfterRestart", false, false);
            }
        }

        private void LoadAndValidate()
        {
            DurableRelaunchPermitRecord loaded = null;
            try { loaded = _store.Load(_sessionId); }
            catch { }
            lock (_gate)
            {
                if (loaded == null)
                {
                    _record = WatchdogJournalV4Validator.CreateNone(_sessionId);
                    return;
                }
                var validation = WatchdogJournalV4Validator.ValidateRecord(loaded, _sessionId);
                _record = validation.IsValid && validation.Record != null
                    ? validation.Record.Clone()
                    : WatchdogJournalV4Validator.CreateBlocked(
                        _sessionId,
                        validation.Reason ?? "InvalidDurableRelaunchState",
                        loaded);
                _nextGeneration = Math.Max(0, _record.Generation);
            }
        }

        private DurableRelaunchResult CommitTransitionLocked(
            DurableRelaunchPermitRecord candidate,
            string operation,
            bool actionAllowed,
            bool existing)
        {
            candidate.SchemaVersion = WatchdogJournalPolicy.CurrentSchemaVersion;
            candidate.SessionId = _sessionId;
            try
            {
                if (_store.TryCommit(candidate.Clone()))
                {
                    _record = candidate.Clone();
                    return ResultLocked(
                        true,
                        existing,
                        candidate.State != DurableRelaunchPermitState.Blocked && actionAllowed,
                        candidate.State == DurableRelaunchPermitState.Blocked,
                        operation);
                }
            }
            catch { }

            var blocked = WatchdogJournalV4Validator.CreateBlocked(
                _sessionId,
                "DurableStoreCommitFailed",
                candidate);
            _record = blocked;
            try { _store.TryCommit(blocked.Clone()); } catch { }
            return ResultLocked(false, false, false, true, "DurableStoreCommitFailed");
        }

        private DurableRelaunchResult BlockLocked(string reason)
        {
            var candidate = WatchdogJournalV4Validator.CreateBlocked(_sessionId, reason, _record);
            return CommitTransitionLocked(candidate, "Block:" + reason, false, false);
        }

        private DurableRelaunchResult IllegalTransitionLocked(string reason) => BlockLocked(reason);

        private DurableRelaunchResult Reject(string reason) =>
            new DurableRelaunchResult { Succeeded = false, Reason = reason, Record = Snapshot };

        private DurableRelaunchResult RejectLocked(string reason) =>
            ResultLocked(false, false, false, false, reason);

        private DurableRelaunchResult ResultLocked(
            bool succeeded,
            bool existing,
            bool actionAllowed,
            bool blocked,
            string reason)
        {
            return new DurableRelaunchResult
            {
                Succeeded = succeeded,
                Existing = existing,
                ActionAllowed = actionAllowed,
                Blocked = blocked,
                Reason = reason,
                Record = _record?.Clone()
            };
        }

        private bool MatchesIdentityLocked(DurableRelaunchPermitIdentity identity)
        {
            return identity != null &&
                   string.Equals(identity.SessionId, _sessionId, StringComparison.Ordinal) &&
                   _record.Identity.Equals(identity);
        }

        private static DurableRelaunchRequest NormalizeRequest(DurableRelaunchRequest request)
        {
            return new DurableRelaunchRequest
            {
                Fingerprint = Normalize(request.Fingerprint, "UnknownFingerprint"),
                ProgressToken = Normalize(request.ProgressToken, "UnknownProgress"),
                ProcessSource = Normalize(request.ProcessSource, RecoveryFailurePolicy.InitialProcessSource),
                RunId = Normalize(request.RunId, "UnknownRun"),
                RecoveryStage = Normalize(request.RecoveryStage, "UnknownStage"),
                PermitNonce = Normalize(request.PermitNonce, string.Empty),
                MaximumProcessRelaunches = Math.Max(0, request.MaximumProcessRelaunches)
            };
        }

        private static bool RequestMatches(
            DurableRelaunchPermitRecord record,
            DurableRelaunchRequest request)
        {
            return string.Equals(record.Fingerprint, request.Fingerprint, StringComparison.Ordinal) &&
                   string.Equals(record.ProgressToken, request.ProgressToken, StringComparison.Ordinal) &&
                   string.Equals(record.ProcessSource, request.ProcessSource, StringComparison.Ordinal) &&
                   string.Equals(record.RunId, request.RunId, StringComparison.Ordinal) &&
                   string.Equals(record.RecoveryStage, request.RecoveryStage, StringComparison.Ordinal);
        }

        private static string Normalize(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
