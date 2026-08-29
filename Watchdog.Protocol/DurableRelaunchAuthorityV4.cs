using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// The schema4 failure authority is intentionally separate from the
    /// pre-existing relaunch coordinator.  It does not accept an
    /// IDurableRelaunchStore, a legacy token, or a launch callback.
    /// </summary>
    public enum DurableAuthorityCommitStatus
    {
        Committed = 0,
        Conflict = 1,
        Busy = 2,
        WriteFailed = 3,
        DurableBlocked = 4,
        NotAttempted = 5,
        Invalid = 6,
        ReadFailed = 7,
        // A candidate was durably applied after the final read-back.  This
        // is deliberately distinct from the legacy Committed name so a
        // caller cannot infer success from a free-form Reason string.
        CandidateApplied = 8,
        // The requested candidate was not applied because a validated,
        // already-blocked record won the admission race.  The existing
        // record/sha pair must be present for this status.
        ExistingBlocked = 9
    }

    public enum DurableAuthorityRestartSafety
    {
        Proven = 0,
        Unproven = 1
    }

    public enum DurableAuthorityFailureKind
    {
        None = 0,
        Missing = 1,
        Corrupt = 2,
        Unreadable = 3,
        SessionMismatch = 4,
        Invalid = 5,
        BootstrapUnproven = 6,
        IdentityConflict = 7,
        WriteFailed = 8,
        // Bounded mutex admission failed.  Busy is retryable and must not
        // poison the process-local circuit like a read/proof failure does.
        Busy = 9
    }

    /// <summary>Independent schema4 persistence port.</summary>
    public interface IDurableRelaunchAuthorityStoreV4
    {
        string CanonicalPath { get; }
        DurableAuthorityStoreReadResult Load(string expectedSessionId);
        DurableAuthorityStoreCommitResult TryCommit(
            DurableRelaunchAuthorityRecord candidate,
            long expectedRevision,
            string expectedSha256,
            bool blockedMarker);
    }

    public sealed class DurableRelaunchAuthorityRecord
    {
        public int SchemaVersion { get; set; } = 4;
        public string RecordKind { get; set; } = "DurableRelaunchAuthority";
        // Format 2 adds the durable launch-intent identity.  Format 1 is
        // accepted only by the migration path; all newly written records use
        // this revision.
        public int RecordFormatRevision { get; set; } = 2;
        public long AuthorityRevision { get; set; }
        public string SessionId { get; set; }
        public DurableRelaunchPermitState State { get; set; }
        public long Generation { get; set; }
        public string PermitId { get; set; }
        public string PermitNonce { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int MaximumProcessRelaunches { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long ConnectionGeneration { get; set; }
        public long RecoveryAttemptGeneration { get; set; }
        public long RecoveryCommitGeneration { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public string LastFailureOperationId { get; set; }
        public string LastFailureCorrelationId { get; set; }
        public string LastFailurePayloadSha256 { get; set; }
        public string LastFailureCanonicalSha256 { get; set; }
        public string LastFailureFingerprint { get; set; }
        public string LastFailureCode { get; set; }
        public string LastFailureDetailCode { get; set; }
        // Frozen failure-run context.  RunId/RunEpoch/Recovery* above become
        // the successfully recovered run after CommitCommitted and therefore
        // cannot also remain the canonical failure evidence.
        public string LastFailureRunId { get; set; }
        public long LastFailureRunEpoch { get; set; }
        public string LastFailureRecoveryStage { get; set; }
        public string LastFailureRecoveryProgressToken { get; set; }
        public string LastFailureRecoveryProcessSource { get; set; }
        public string LastFailureDeviceOrChannelGroup { get; set; }
        public string LastFailureSessionNonce { get; set; }
        public int LastFailureProcessId { get; set; }
        public long LastFailureProcessStartUtcTicks { get; set; }
        public long LastFailureConnectionGeneration { get; set; }
        public long LastFailureAttemptGeneration { get; set; }
        public long LastFailurePermitGeneration { get; set; }
        public string LastFailurePermitId { get; set; }
        public string LastFailurePermitNonce { get; set; }
        public string LastFailureDisposition { get; set; }
        public long LastFailureDecisionSequence { get; set; }
        public long LastFailureDecisionUtcTicks { get; set; }
        public bool LastFailurePermanent { get; set; }
        public bool CircuitOpen { get; set; }
        // Monotonic wall-clock evidence for the latest durable authority
        // mutation.  The sidecar uses this to detect a permit which was
        // approved but never progressed to launch/attach.
        public long LastTransitionUtcTicks { get; set; }
        public string DetailCode { get; set; }
        public string BootstrapMarker { get; set; }
        public string BootstrapPrimaryPath { get; set; }
        public string BootstrapPrimarySha256 { get; set; }
        public string BootstrapProofPath { get; set; }
        public string BootstrapProofSha256 { get; set; }
        public string BootstrapProofMarker { get; set; }
        public int BootstrapMaximumProcessRelaunches { get; set; }

        // Launch authorization is a separate durable projection from the
        // failure reporter identity above.  These fields are populated only
        // after the Approved -> LaunchIntent CAS and are never inferred from
        // a Process object.
        public string LaunchIntentId { get; set; }
        public string LaunchExecutablePath { get; set; }
        public string LaunchExecutableSha256 { get; set; }
        public string LaunchArguments { get; set; }
        public string LaunchWorkingDirectory { get; set; }
        public string LaunchOptionsCanonical { get; set; }
        public string LaunchSpecSha256 { get; set; }
        public long LaunchAuthorityRevision { get; set; }
        public string LaunchAuthoritySha256 { get; set; }
        public bool LaunchConsumed { get; set; }

        public DurableRelaunchAuthorityRecord Clone()
        {
            return (DurableRelaunchAuthorityRecord)MemberwiseClone();
        }

        public bool IsPristineNone(string expectedSessionId)
        {
            return State == DurableRelaunchPermitState.None &&
                   string.Equals(SessionId, expectedSessionId, StringComparison.Ordinal) &&
                   Generation == 0 && string.IsNullOrEmpty(PermitId) &&
                   string.IsNullOrEmpty(PermitNonce) && ConsecutiveFailures == 0 &&
                   MaximumProcessRelaunches > 0 && ProcessId == 0 &&
                   ProcessStartUtcTicks == 0 && ConnectionGeneration == 0 &&
                   RecoveryAttemptGeneration == 0 && RecoveryCommitGeneration == 0 &&
                   RunEpoch == 0 && string.IsNullOrEmpty(RunId) &&
                   string.IsNullOrEmpty(RecoveryStage) && string.IsNullOrEmpty(RecoveryProgressToken) &&
                   string.IsNullOrEmpty(RecoveryProcessSource) && string.IsNullOrEmpty(DeviceOrChannelGroup) &&
                   string.IsNullOrEmpty(DetailCode) && !CircuitOpen &&
                   string.IsNullOrEmpty(LastFailureCanonicalSha256) &&
                   string.IsNullOrEmpty(LastFailureOperationId) && string.IsNullOrEmpty(LastFailureCorrelationId) &&
                   string.IsNullOrEmpty(LastFailurePayloadSha256) && string.IsNullOrEmpty(LastFailureCode) &&
                   string.IsNullOrEmpty(LastFailureFingerprint) && string.IsNullOrEmpty(LastFailureDetailCode) &&
                   string.IsNullOrEmpty(LastFailureSessionNonce) && LastFailureProcessId == 0 &&
                   LastFailureProcessStartUtcTicks == 0 && LastFailureConnectionGeneration == 0 &&
                   LastFailureAttemptGeneration == 0 && LastFailurePermitGeneration == 0 &&
                   string.IsNullOrEmpty(LastFailurePermitId) && string.IsNullOrEmpty(LastFailurePermitNonce) &&
                   string.IsNullOrEmpty(LastFailureDisposition) &&
                   LastFailureDecisionSequence == 0 && LastFailureDecisionUtcTicks == 0 && !LastFailurePermanent &&
                   string.IsNullOrEmpty(LaunchIntentId) && string.IsNullOrEmpty(LaunchExecutablePath) &&
                   string.IsNullOrEmpty(LaunchExecutableSha256) && string.IsNullOrEmpty(LaunchArguments) &&
                   string.IsNullOrEmpty(LaunchWorkingDirectory) && string.IsNullOrEmpty(LaunchOptionsCanonical) &&
                   string.IsNullOrEmpty(LaunchSpecSha256) && LaunchAuthorityRevision == 0 &&
                   string.IsNullOrEmpty(LaunchAuthoritySha256) && !LaunchConsumed &&
                   ((string.IsNullOrEmpty(BootstrapProofPath) && string.IsNullOrEmpty(BootstrapProofSha256) && string.IsNullOrEmpty(BootstrapProofMarker) && BootstrapMaximumProcessRelaunches == 0) ||
                    (!string.IsNullOrEmpty(BootstrapProofPath) && RecoveryFailureReceipt.IsSha256(BootstrapProofSha256) &&
                     string.Equals(BootstrapProofMarker, "schema4-authority-bootstrap-proof-v1", StringComparison.Ordinal) &&
                     BootstrapMaximumProcessRelaunches == MaximumProcessRelaunches));
        }
    }

    /// <summary>
    /// Internal proof object.  A public DTO/result must never be able to
    /// launder a caller-supplied store or path into an authority instance.
    /// </summary>
    internal sealed class DurableRelaunchAuthorityBootstrapCapability
    {
        internal DurableRelaunchAuthorityBootstrapCapability(
            WatchdogJournalBootstrapReceipt primary,
            string authorityPath)
        {
            if (primary == null) throw new ArgumentNullException(nameof(primary));
            SessionId = primary.SessionId;
            PrimarySnapshotPath = primary.Path;
            PrimarySnapshotSha256 = primary.SHA256;
            PrimaryRevision = primary.AuthorityRevision;
            Marker = primary.Marker;
            ProofPath = primary.ProofPath;
            ProofSha256 = primary.ProofSHA256;
            ProofMarker = primary.ProofMarker;
            MaximumProcessRelaunches = primary.MaximumProcessRelaunches;
            AuthorityPath = authorityPath;
        }

        public string SessionId { get; }
        public string PrimarySnapshotPath { get; }
        public string PrimarySnapshotSha256 { get; }
        public long PrimaryRevision { get; }
        public string Marker { get; }
        public string ProofPath { get; }
        public string ProofSha256 { get; }
        public string ProofMarker { get; }
        public int MaximumProcessRelaunches { get; }
        public string AuthorityPath { get; }
        public bool IsVerified => !string.IsNullOrEmpty(SessionId) &&
                                  !string.IsNullOrEmpty(PrimarySnapshotPath) &&
                                  !string.IsNullOrEmpty(ProofPath) &&
                                  !string.IsNullOrEmpty(AuthorityPath) &&
                                  RecoveryFailureReceipt.IsSha256(PrimarySnapshotSha256) &&
                                  RecoveryFailureReceipt.IsSha256(ProofSha256) &&
                                  string.Equals(ProofMarker, "schema4-authority-bootstrap-proof-v1", StringComparison.Ordinal) &&
                                  PrimaryRevision >= 0 && MaximumProcessRelaunches > 0 &&
                                  !string.IsNullOrEmpty(Marker);
    }

    public sealed class DurableAuthorityStoreReadResult
    {
        public DurableRelaunchAuthorityRecord Record { get; internal set; }
        public string Sha256 { get; internal set; }
        public long Revision { get; internal set; }
        public bool Exists { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Unproven { get; internal set; }
        public DurableAuthorityFailureKind FailureKind { get; internal set; }
        public string Reason { get; internal set; }
        public DurableAuthorityStoreReadResult Clone() => new DurableAuthorityStoreReadResult
        {
            Record = Record?.Clone(), Sha256 = Sha256, Revision = Revision,
            Exists = Exists, Blocked = Blocked, Unproven = Unproven,
            FailureKind = FailureKind, Reason = Reason
        };
    }

    public sealed class DurableAuthorityStoreCommitResult
    {
        public DurableAuthorityCommitStatus Status { get; internal set; }
        public DurableAuthorityFailureKind FailureKind { get; internal set; }
        public DurableRelaunchAuthorityRecord Record { get; internal set; }
        public string Sha256 { get; internal set; }
        public long Revision { get; internal set; }
        public string Reason { get; internal set; }
        public bool Durable => (Status == DurableAuthorityCommitStatus.Committed ||
                                Status == DurableAuthorityCommitStatus.DurableBlocked ||
                                Status == DurableAuthorityCommitStatus.CandidateApplied ||
                                Status == DurableAuthorityCommitStatus.ExistingBlocked) &&
                               Record != null && RecoveryFailureReceipt.IsSha256(Sha256);
    }

    public sealed class DurableAuthorityDecisionResult
    {
        public DurableAuthorityCommitStatus CommitStatus { get; internal set; }
        public RecoveryFailureReceipt Receipt { get; internal set; }
        public DurableRelaunchAuthorityRecord Record { get; internal set; }
        public bool Durable { get; internal set; }
        public bool ActionAllowed { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool BlockedInCurrentProcess { get; internal set; }
        public bool Unproven { get; internal set; }
        public bool FailureRegistered { get; internal set; }
        public DurableAuthorityRestartSafety RestartSafety { get; internal set; }
        public string Reason { get; internal set; }
        public long AuthorityRevision { get; internal set; }

        public DurableAuthorityDecisionResult Clone() => new DurableAuthorityDecisionResult
        {
            CommitStatus = CommitStatus, Receipt = Receipt?.Clone(), Record = Record?.Clone(),
            Durable = Durable, ActionAllowed = ActionAllowed, Blocked = Blocked,
            BlockedInCurrentProcess = BlockedInCurrentProcess,
            Unproven = Unproven,
            FailureRegistered = FailureRegistered, RestartSafety = RestartSafety,
            Reason = Reason, AuthorityRevision = AuthorityRevision
        };
    }

    public sealed class DurableAuthorityReconcileResult
    {
        public DurableAuthorityDecisionResult Decision { get; internal set; }
        public DurableRelaunchProcessObservation Observation { get; internal set; }
        public bool ActionAllowed => Decision != null && Decision.ActionAllowed;
        public bool OutcomeUnknown { get; internal set; }
        public string Reason { get; internal set; }
    }

    public sealed class DurableAuthorityOpenResult
    {
        public DurableRelaunchAuthorityV4 Authority { get; internal set; }
        public WatchdogJournalBootstrapReceipt BootstrapReceipt { get; internal set; }
        public DurableAuthorityStoreReadResult Store { get; internal set; }
        public bool Succeeded { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Unproven { get; internal set; }
        public string Reason { get; internal set; }
    }

    /// <summary>
    /// Strict schema4 authority.  All operations clone/freeze their input
    /// before entering the authority gate and never retain caller references.
    /// </summary>
    public sealed class DurableRelaunchAuthorityV4
    {
        private readonly object _gate = new object();
        private readonly string _sessionId;
        private readonly IDurableRelaunchAuthorityStoreV4 _store;
        private readonly DurableRelaunchAuthorityBootstrapCapability _capability;
        private DurableRelaunchAuthorityRecord _record;
        private string _sha256;
        private bool _blockedInCurrentProcess;

        internal DurableRelaunchAuthorityV4(
            string sessionId,
            IDurableRelaunchAuthorityStoreV4 store,
            DurableRelaunchAuthorityBootstrapCapability capability)
        {
            if (!IsCanonicalSession(sessionId)) throw new ArgumentException("SessionId must be GUID N.", nameof(sessionId));
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (capability == null || !capability.IsVerified ||
                !string.Equals(capability.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(capability.AuthorityPath, store.CanonicalPath, StringComparison.Ordinal))
                throw new ArgumentException("Verified bootstrap capability required.", nameof(capability));
            _sessionId = sessionId;
            _store = store;
            _capability = capability;
            var loaded = store.Load(sessionId);
            if (loaded == null || loaded.Record == null || loaded.Blocked || loaded.Unproven)
            {
                _blockedInCurrentProcess = true;
                _record = DurableRelaunchAuthorityV4Validator.CreateBlocked(sessionId, "AuthorityLoadBlocked");
            }
            else
            {
                _record = loaded.Record.Clone();
                _sha256 = loaded.Sha256;
                if (!IsDurableRecordPair(_record, _sha256) ||
                    _record.MaximumProcessRelaunches != capability.MaximumProcessRelaunches ||
                    !string.Equals(_record.BootstrapPrimaryPath, capability.PrimarySnapshotPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_record.BootstrapPrimarySha256, capability.PrimarySnapshotSha256, StringComparison.Ordinal) ||
                    !string.Equals(_record.BootstrapProofPath, capability.ProofPath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_record.BootstrapProofSha256, capability.ProofSha256, StringComparison.Ordinal) ||
                    !string.Equals(_record.BootstrapProofMarker, capability.ProofMarker, StringComparison.Ordinal) ||
                    !string.Equals(_record.BootstrapMarker, capability.Marker, StringComparison.Ordinal) ||
                    _record.BootstrapMaximumProcessRelaunches != capability.MaximumProcessRelaunches)
                {
                    _blockedInCurrentProcess = true;
                }
            }
        }

        public string SessionId => _sessionId;
        internal DurableRelaunchAuthorityBootstrapCapability Capability => _capability;
        public DurableRelaunchAuthorityRecord Snapshot
        {
            get { lock (_gate) return _record?.Clone(); }
        }
        public bool IsBlockedInCurrentProcess
        {
            get { lock (_gate) return _blockedInCurrentProcess; }
        }

        public DurableAuthorityDecisionResult RegisterFailureAndDecide(RecoveryFailureOperation operation)
        {
            // This is intentionally the first operation on the caller input.
            RecoveryFailureFrozenOperation frozen;
            string freezeReason;
            if (!RecoveryFailureFrozenOperation.TryFreeze(operation, out frozen, out freezeReason))
                return InvalidDecision(freezeReason ?? "OperationInvalid");

            if (!string.Equals(frozen.SessionId, _sessionId, StringComparison.Ordinal))
                return InvalidDecision("SessionMismatch", RecoveryFailureDispositions.IdentityConflict);

            lock (_gate)
            {
                // Every CAS conflict starts a fresh transaction.  The
                // candidate, count, pending decision, sequence and receipt
                // are rebuilt from the newly loaded record; no stale patch is
                // ever applied to a later revision.
                for (var attempt = 0; attempt < 64; attempt++)
                {
                    if (_blockedInCurrentProcess)
                        return BlockedDecisionLocked("CircuitOpen", frozen);
                    var fresh = _store.Load(_sessionId);
                    if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    {
                        // Mutex admission is a transient coordination
                        // result, not evidence that the authority is
                        // corrupt.  Do not set the process circuit here;
                        // the next call must be able to retry normally.
                        return BusyDecisionLocked(fresh.Reason ?? "AuthorityMutexBusy");
                    }
                    if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    {
                        _blockedInCurrentProcess = true;
                        return BlockedDecisionLocked("AuthorityReloadBlocked", frozen);
                    }
                    _record = fresh.Record.Clone();
                    _sha256 = fresh.Sha256;

                    // Persisted circuits are sticky read-only boundaries.
                    // No new correlation, budget, token, or prose may reopen
                    // or advance a blocked authority.
                    if (_record.State == DurableRelaunchPermitState.Blocked ||
                        _record.State == DurableRelaunchPermitState.Revoked ||
                        _record.CircuitOpen)
                        return StickyBlockedDecisionLocked(frozen, _record.DetailCode ?? "CircuitOpen");

                    // The bootstrap proof freezes the only trusted budget;
                    // operation input can never update or raise it.
                    if (frozen.MaximumProcessRelaunches != _record.MaximumProcessRelaunches)
                        return BudgetMismatchDecisionLocked();

                    if (HasCorrelation(_record, frozen.RequestCorrelationId))
                    {
                        if (string.Equals(_record.LastFailureCanonicalSha256, frozen.CanonicalSha256, StringComparison.Ordinal) &&
                            StableTupleMatches(_record, frozen))
                            return ReplayLocked(frozen);

                        var conflictCandidate = _record.Clone();
                        ApplyFailure(conflictCandidate, frozen, RecoveryFailureDispositions.IdentityConflict, "IdentityConflict", conflictCandidate.ConsecutiveFailures);
                        conflictCandidate.State = DurableRelaunchPermitState.Blocked;
                        conflictCandidate.CircuitOpen = true;
                        var conflictReceipt = BuildReceipt(frozen, conflictCandidate, RecoveryFailureDispositions.IdentityConflict, false, true, false, true, "IdentityConflict");
                        var conflictCommit = TryCommitCandidateLocked(conflictCandidate, conflictReceipt, true);
                        if (conflictCommit != null && conflictCommit.Status == DurableAuthorityCommitStatus.Busy)
                            return BusyDecisionLocked(conflictCommit.Reason ?? "AuthorityMutexBusy");
                        if (conflictCommit != null && conflictCommit.Status == DurableAuthorityCommitStatus.ExistingBlocked)
                        {
                            // Another authority won the circuit while this
                            // candidate was being admitted.  Do not return
                            // the losing caller's receipt as if it had been
                            // registered.  Rebuild the sticky receipt from
                            // the durable winner and leave bytes/revision
                            // untouched.
                            if (conflictCommit.Record != null)
                            {
                                _record = conflictCommit.Record.Clone();
                                _sha256 = conflictCommit.Sha256;
                            }
                            return StickyBlockedDecisionLocked(frozen, _record?.DetailCode ?? "CircuitOpen");
                        }
                        if (conflictCommit != null && IsDurableApplied(conflictCommit))
                        {
                            _record = conflictCommit.Record.Clone(); _sha256 = conflictCommit.Sha256;
                            conflictReceipt.Durable = true; conflictReceipt.FailureRegistered = true;
                            return new DurableAuthorityDecisionResult
                            {
                                CommitStatus = conflictCommit.Status, Receipt = conflictReceipt.Clone(), Record = _record.Clone(),
                                Durable = true, Blocked = true, FailureRegistered = true,
                                RestartSafety = DurableAuthorityRestartSafety.Proven, Reason = "IdentityConflict",
                                AuthorityRevision = _record.AuthorityRevision
                            };
                        }
                        if (conflictCommit != null && conflictCommit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                        return FailClosedAfterCommitLocked(frozen, conflictCandidate, conflictReceipt,
                            conflictCommit?.Reason ?? "IdentityConflictCommitFailed");
                    }

                    var candidate = _record.Clone();
                    var nextCount = checked(Math.Max(0, candidate.ConsecutiveFailures) + 1);
                    var budget = frozen.MaximumProcessRelaunches;
                    // Approved/LaunchIntent/Started describe an action which has
                    // not yet reached an authenticated running client.  Attached
                    // is different: an exact operation replay was handled above,
                    // while a new failure from/for the attached process must close
                    // that generation and mint the next permit atomically.  Treating
                    // Attached as merely "pending" stranded the authority at the old
                    // generation after a later watchdog takeover.
                    var pending = candidate.State == DurableRelaunchPermitState.Approved ||
                                  candidate.State == DurableRelaunchPermitState.LaunchIntent ||
                                  candidate.State == DurableRelaunchPermitState.Started;
                    var disposition = pending ? RecoveryFailureDispositions.RelaunchAlreadyPending : RecoveryFailureDispositions.RelaunchApproved;
                    var detail = pending ? "RelaunchAlreadyPending" : "RelaunchApproved";
                    var blocked = frozen.Permanent || nextCount >= budget;
                    if (blocked)
                    {
                        disposition = RecoveryFailureDispositions.CircuitOpen;
                        detail = frozen.Permanent ? "PermanentFailure" : "RelaunchBudgetExhausted";
                        candidate.State = DurableRelaunchPermitState.Blocked;
                        candidate.CircuitOpen = true;
                    }
                    else if (!pending)
                    {
                        candidate.State = DurableRelaunchPermitState.Approved;
                        candidate.Generation = Math.Max(candidate.Generation, 0) + 1;
                        candidate.PermitId = Guid.NewGuid().ToString("N");
                        candidate.PermitNonce = Guid.NewGuid().ToString("N");
                        ClearLaunchIdentity(candidate);
                    }
                    ApplyFailure(candidate, frozen, disposition, detail, nextCount);
                    var receipt = BuildReceipt(frozen, candidate, disposition, false, true, false, blocked, detail);
                    var commit = TryCommitCandidateLocked(candidate, receipt, false);
                    if (commit != null && commit.Status == DurableAuthorityCommitStatus.Busy)
                        return BusyDecisionLocked(commit.Reason ?? "AuthorityMutexBusy");
                    if (commit != null && IsCandidateApplied(commit))
                    {
                        receipt.Durable = true; receipt.FailureRegistered = true;
                        _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                        return new DurableAuthorityDecisionResult
                        {
                            CommitStatus = commit.Status, Receipt = receipt.Clone(), Record = _record.Clone(),
                            Durable = true, ActionAllowed = !blocked && !pending, Blocked = blocked,
                            FailureRegistered = true, RestartSafety = DurableAuthorityRestartSafety.Proven,
                            Reason = detail, AuthorityRevision = _record.AuthorityRevision
                        };
                    }
                    if (commit != null && commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                    return FailClosedAfterCommitLocked(frozen, candidate, receipt,
                        commit == null ? "AuthorityCommitMissing" : commit.Reason);
                }
                _blockedInCurrentProcess = true;
                return InvalidDecision("AuthorityCasRetryExhausted");
            }
        }

        /// <summary>
        /// Durable Approved -> LaunchIntent transition.  This is the only
        /// API which can mint a launch capability.  No process API is called
        /// here; callers must explicitly pass the returned capability to the
        /// guarded launcher after this method has read the intent back.
        /// </summary>
        public DurableAuthorityTransitionResult PrepareLaunchIntent(DurableLaunchIntent intent)
        {
            if (intent == null) return TransitionInvalid("IntentMissing");
            intent = intent.Clone();
            string validation;
            if (!DurableLaunchCanonical.TryValidate(intent, out validation) ||
                !string.Equals(intent.SessionId, _sessionId, StringComparison.Ordinal))
                return TransitionInvalid(validation ?? "SessionMismatch");
            lock (_gate)
            {
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    if (_blockedInCurrentProcess) return TransitionBlocked("AuthorityBlocked");
                    var fresh = _store.Load(_sessionId);
                    if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                    if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                        return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                    _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                    if (_record.State != DurableRelaunchPermitState.Approved)
                        return TransitionInvalid("ExpectedApproved");
                    if (!MatchesIntentIdentity(_record, intent))
                        return TransitionInvalid("PermitIdentityMismatch");
                    if (HasLaunchIdentity(_record))
                        return TransitionInvalid("LaunchIntentAlreadyPresent");
                    var authorityRevision = _record.AuthorityRevision;
                    var authoritySha = _sha256;
                    var candidate = _record.Clone();
                    candidate.State = DurableRelaunchPermitState.LaunchIntent;
                    candidate.LaunchIntentId = intent.IntentId;
                    candidate.LaunchExecutablePath = intent.ExecutablePath;
                    candidate.LaunchExecutableSha256 = intent.ExecutableSha256;
                    candidate.LaunchArguments = intent.Arguments ?? string.Empty;
                    candidate.LaunchWorkingDirectory = intent.WorkingDirectory;
                    candidate.LaunchOptionsCanonical = intent.LaunchOptionsCanonical;
                    candidate.LaunchSpecSha256 = intent.LaunchSpecSha256;
                    candidate.LaunchAuthorityRevision = authorityRevision;
                    candidate.LaunchAuthoritySha256 = authoritySha;
                    candidate.LaunchConsumed = false;
                    var commit = TryCommitCandidateLocked(candidate, null, false);
                    if (commit == null) return TransitionWriteFailed("NullCommit");
                    if (commit.Status == DurableAuthorityCommitStatus.Busy || commit.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                    if (commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                    if (!IsCandidateApplied(commit)) return TransitionWriteFailed(commit.Reason ?? "LaunchIntentCommitFailed");
                    _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                    return new DurableAuthorityTransitionResult
                    {
                        Status = DurableAuthorityTransitionStatus.Committed,
                        Reason = "LaunchIntentCommitted", Record = _record.Clone(), Sha256 = _sha256,
                        Capability = new DurableLaunchIntentCapability(intent, authorityRevision, authoritySha)
                    };
                }
                return TransitionConflict("LaunchIntentCasRetryExhausted");
            }
        }

        public DurableAuthorityTransitionResult CommitStarted(
            DurableLaunchIntentCapability capability, int processId, long processStartUtcTicks)
        {
            if (capability == null || processId <= 0 || processStartUtcTicks <= 0)
                return TransitionInvalid("StartedIdentityMissing");
            return TransitionLifecycle(
                capability,
                DurableRelaunchPermitState.LaunchIntent,
                current => current.LaunchConsumed ? null : "LaunchIntentNotConsumed",
                candidate =>
            {
                candidate.State = DurableRelaunchPermitState.Started;
                candidate.ProcessId = processId;
                candidate.ProcessStartUtcTicks = processStartUtcTicks;
            });
        }

        /// <summary>
        /// Atomically consumes a persisted LaunchIntent immediately before
        /// the guarded OS boundary.  This closes the crash window between
        /// intent read-back and Process.Start: after this CAS a restart may
        /// reconcile the outcome, but it can never start the same intent a
        /// second time.
        /// </summary>
        public DurableAuthorityTransitionResult ConsumeLaunchIntent(
            DurableLaunchIntentCapability capability)
        {
            if (capability == null) return TransitionInvalid("LaunchCapabilityMissing");
            var intent = capability.CloneIntent();
            string validation;
            if (!DurableLaunchCanonical.TryValidate(intent, out validation))
                return TransitionInvalid(validation);
            lock (_gate)
            {
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    if (_blockedInCurrentProcess) return TransitionBlocked("AuthorityBlocked");
                    var fresh = _store.Load(_sessionId);
                    if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                    if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                        return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                    _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                    if (_record.State != DurableRelaunchPermitState.LaunchIntent)
                        return TransitionInvalid("ExpectedLaunchIntent");
                    if (!MatchesCapability(_record, capability))
                        return TransitionInvalid("LaunchCapabilityMismatch");
                    if (_record.LaunchConsumed)
                        return TransitionInvalid("LaunchIntentAlreadyConsumed");
                    var candidate = _record.Clone();
                    candidate.LaunchConsumed = true;
                    var commit = TryCommitCandidateLocked(candidate, null, false);
                    if (commit == null) return TransitionWriteFailed("NullCommit");
                    if (commit.Status == DurableAuthorityCommitStatus.Busy ||
                        commit.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                    if (commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                    if (!IsCandidateApplied(commit))
                        return TransitionWriteFailed(commit.Reason ?? "LaunchConsumeFailed");
                    _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                    return new DurableAuthorityTransitionResult
                    {
                        Status = DurableAuthorityTransitionStatus.Committed,
                        Reason = "LaunchIntentConsumed", Record = _record.Clone(),
                        Sha256 = _sha256, Capability = capability
                    };
                }
                return TransitionConflict("LaunchConsumeCasRetryExhausted");
            }
        }

        public DurableAuthorityTransitionResult CommitAttached(DurableLaunchIntentCapability capability)
        {
            if (capability == null) return TransitionInvalid("AttachedCapabilityMissing");
            return TransitionLifecycle(capability, DurableRelaunchPermitState.Started, candidate =>
            {
                candidate.State = DurableRelaunchPermitState.Attached;
            });
        }

        public DurableAuthorityTransitionResult CommitCommitted(
            DurableLaunchIntentCapability capability, string runId, long runEpoch,
            string recoveryStage, string progressToken, long recoveryCommitGeneration)
        {
            if (capability == null || string.IsNullOrWhiteSpace(runId) || runEpoch <= 0 ||
                string.IsNullOrWhiteSpace(recoveryStage) || string.IsNullOrWhiteSpace(progressToken) ||
                recoveryCommitGeneration <= 0)
                return TransitionInvalid("CommittedEvidenceMissing");
            // The Attached record intentionally contains the failed run's
            // recovery context.  A successful recovery batch is the boundary
            // which replaces it with the newly-created run identity.  Capability
            // and Attached lifecycle validation authenticate the writer; requiring
            // the new RunId/epoch/stage/token to equal the old failure context made
            // every real recovery commit impossible.
            return TransitionLifecycle(capability, DurableRelaunchPermitState.Attached, candidate =>
            {
                // Format-2 records created before this fix used the mutable
                // current context as their canonical failure context.  Freeze
                // it before replacing the current fields so old Attached
                // records can complete recovery without migration ambiguity.
                if (string.IsNullOrEmpty(candidate.LastFailureRunId))
                {
                    candidate.LastFailureRunId = candidate.RunId;
                    candidate.LastFailureRunEpoch = candidate.RunEpoch;
                    candidate.LastFailureRecoveryStage = candidate.RecoveryStage;
                    candidate.LastFailureRecoveryProgressToken = candidate.RecoveryProgressToken;
                    candidate.LastFailureRecoveryProcessSource = candidate.RecoveryProcessSource;
                    candidate.LastFailureDeviceOrChannelGroup = candidate.DeviceOrChannelGroup;
                }
                candidate.State = DurableRelaunchPermitState.Committed;
                candidate.RunId = runId;
                candidate.RunEpoch = runEpoch;
                candidate.RecoveryStage = recoveryStage;
                candidate.RecoveryProgressToken = progressToken;
                candidate.RecoveryCommitGeneration = recoveryCommitGeneration;
            });
        }

        /// <summary>Rehydrates a persisted intent without changing bytes.</summary>
        public DurableAuthorityTransitionResult ResumeLaunchIntent()
        {
            lock (_gate)
            {
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                if (_record.State != DurableRelaunchPermitState.LaunchIntent &&
                    _record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return TransitionInvalid("NoLaunchIntentToResume");
                if (!HasLaunchIdentity(_record)) return TransitionInvalid("LaunchIntentInvalid");
                if (_record.State == DurableRelaunchPermitState.LaunchIntent && _record.LaunchConsumed)
                    return TransitionInvalid("LaunchIntentAlreadyConsumed");
                var intent = new DurableLaunchIntent
                {
                    SessionId = _record.SessionId,
                    SessionNonce = _record.LastFailureSessionNonce,
                    Generation = _record.Generation,
                    PermitId = _record.PermitId,
                    PermitNonce = _record.PermitNonce,
                    IntentId = _record.LaunchIntentId,
                    ExecutablePath = _record.LaunchExecutablePath,
                    ExecutableSha256 = _record.LaunchExecutableSha256,
                    Arguments = _record.LaunchArguments,
                    WorkingDirectory = _record.LaunchWorkingDirectory,
                    LaunchOptionsCanonical = _record.LaunchOptionsCanonical,
                    LaunchSpecSha256 = _record.LaunchSpecSha256
                };
                string reason;
                if (!DurableLaunchCanonical.TryValidate(intent, out reason)) return TransitionInvalid(reason);
                return new DurableAuthorityTransitionResult
                {
                    Status = DurableAuthorityTransitionStatus.Committed,
                    Reason = "LaunchIntentResumed", Record = _record.Clone(), Sha256 = _sha256,
                    Capability = new DurableLaunchIntentCapability(intent, _record.LaunchAuthorityRevision, _record.LaunchAuthoritySha256)
                };
            }
        }

        /// <summary>
        /// Explicit fail-closed terminal transition for a current authority
        /// transaction.  It is still a full durable CAS and cannot be used to
        /// clear a different session/permit.
        /// </summary>
        public DurableAuthorityTransitionResult BlockCurrent(string reason)
        {
            lock (_gate)
            {
                if (_blockedInCurrentProcess) return TransitionBlocked("AuthorityBlocked");
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                if (_record.State == DurableRelaunchPermitState.Blocked || _record.State == DurableRelaunchPermitState.Revoked)
                    return new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Committed, Reason = "AlreadyBlocked", Record = _record.Clone(), Sha256 = _sha256 };
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Blocked;
                candidate.CircuitOpen = true;
                candidate.DetailCode = string.IsNullOrWhiteSpace(reason) ? "AuthorityBlocked" : reason;
                if (!string.IsNullOrEmpty(candidate.LastFailureCanonicalSha256))
                    candidate.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
                var commit = TryCommitCandidateLocked(candidate, null, true);
                if (commit == null) return TransitionWriteFailed("NullCommit");
                if (commit.Status == DurableAuthorityCommitStatus.Busy || commit.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                if (!IsBlockedApplied(commit)) return TransitionWriteFailed(commit.Reason ?? "BlockCommitFailed");
                _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                return new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Committed, Reason = candidate.DetailCode, Record = _record.Clone(), Sha256 = _sha256 };
            }
        }

        /// <summary>
        /// Durably supersedes an approved-but-not-consumed automatic takeover
        /// when SafeIdle, an operator stop, or a newer authority wins before
        /// process termination.  This is a terminal circuit state: a stale
        /// in-memory permit cannot become consumable again after host restart.
        /// </summary>
        public DurableAuthorityTransitionResult RevokeCurrent(string reason)
        {
            lock (_gate)
            {
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                _record = fresh.Record.Clone();
                _sha256 = fresh.Sha256;
                if (_record.State == DurableRelaunchPermitState.Revoked)
                    return new DurableAuthorityTransitionResult
                    {
                        Status = DurableAuthorityTransitionStatus.Committed,
                        Reason = "AlreadyRevoked",
                        Record = _record.Clone(),
                        Sha256 = _sha256
                    };
                if (_record.State != DurableRelaunchPermitState.Approved &&
                    _record.State != DurableRelaunchPermitState.LaunchIntent &&
                    _record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return TransitionInvalid("NoActivePermit");

                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Revoked;
                candidate.CircuitOpen = true;
                candidate.DetailCode = string.IsNullOrWhiteSpace(reason)
                    ? "PermitSuperseded"
                    : reason;
                if (!string.IsNullOrEmpty(candidate.LastFailureCanonicalSha256))
                    candidate.LastFailureDisposition = RecoveryFailureDispositions.Superseded;
                var commit = TryCommitCandidateLocked(candidate, null, true);
                if (commit == null) return TransitionWriteFailed("NullCommit");
                if (commit.Status == DurableAuthorityCommitStatus.Busy ||
                    commit.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                if (!IsBlockedApplied(commit))
                    return TransitionWriteFailed(commit.Reason ?? "PermitRevokeFailed");
                _record = commit.Record.Clone();
                _sha256 = commit.Sha256;
                return new DurableAuthorityTransitionResult
                {
                    Status = DurableAuthorityTransitionStatus.Committed,
                    Reason = candidate.DetailCode,
                    Record = _record.Clone(),
                    Sha256 = _sha256
                };
            }
        }

        public DurableAuthorityTransitionResult CloseCurrentAsFailed(string reason)
        {
            lock (_gate)
            {
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                if (_record.State != DurableRelaunchPermitState.Approved &&
                    _record.State != DurableRelaunchPermitState.LaunchIntent &&
                    _record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return TransitionInvalid("NoActivePermit");
                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.Failed;
                candidate.DetailCode = string.IsNullOrWhiteSpace(reason) ? "PermitClosed" : reason;
                candidate.ProcessId = 0;
                candidate.ProcessStartUtcTicks = 0;
                candidate.ConnectionGeneration = Math.Max(1, candidate.ConnectionGeneration);
                candidate.RecoveryAttemptGeneration = Math.Max(1, candidate.RecoveryAttemptGeneration);
                // A failed permit is terminal evidence, not a new launch
                // action.  Keep reporter evidence but clear process-owned
                // launch identity so a subsequent failure operation can mint
                // a fresh generation through the normal CAS.
                candidate.LaunchIntentId = null;
                candidate.LaunchExecutablePath = null;
                candidate.LaunchExecutableSha256 = null;
                candidate.LaunchArguments = null;
                candidate.LaunchWorkingDirectory = null;
                candidate.LaunchOptionsCanonical = null;
                candidate.LaunchSpecSha256 = null;
                candidate.LaunchAuthorityRevision = 0;
                candidate.LaunchAuthoritySha256 = null;
                candidate.LaunchConsumed = false;
                var commit = TryCommitCandidateLocked(candidate, null, false);
                if (commit == null) return TransitionWriteFailed("NullCommit");
                if (commit.Status == DurableAuthorityCommitStatus.Busy || commit.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                if (commit.Status == DurableAuthorityCommitStatus.Conflict) return TransitionConflict("PermitCloseConflict");
                if (!IsCandidateApplied(commit)) return TransitionWriteFailed(commit.Reason ?? "PermitCloseFailed");
                _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                return new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Committed, Reason = "PermitClosed", Record = _record.Clone(), Sha256 = _sha256 };
            }
        }

        public DurableAuthorityTransitionResult RejectCurrentNoWork(
            DurableRelaunchPermitIdentity identity,
            string reason)
        {
            lock (_gate)
            {
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                _record = fresh.Record.Clone();
                _sha256 = fresh.Sha256;
                if (_record.State == DurableRelaunchPermitState.RejectedNoWork &&
                    MatchesPermitIdentity(_record, identity))
                    return new DurableAuthorityTransitionResult
                    {
                        Status = DurableAuthorityTransitionStatus.Committed,
                        Reason = "AlreadyRejectedNoWork",
                        Record = _record.Clone(),
                        Sha256 = _sha256
                    };
                if (!MatchesPermitIdentity(_record, identity))
                    return TransitionInvalid("PermitIdentityMismatch");
                if (_record.State != DurableRelaunchPermitState.Approved &&
                    _record.State != DurableRelaunchPermitState.LaunchIntent &&
                    _record.State != DurableRelaunchPermitState.Started &&
                    _record.State != DurableRelaunchPermitState.Attached)
                    return TransitionInvalid("NoActivePermit");

                var candidate = _record.Clone();
                candidate.State = DurableRelaunchPermitState.RejectedNoWork;
                candidate.CircuitOpen = false;
                candidate.DetailCode = string.IsNullOrWhiteSpace(reason)
                    ? "RejectedNoWork"
                    : reason;
                candidate.ProcessId = 0;
                candidate.ProcessStartUtcTicks = 0;
                candidate.RecoveryCommitGeneration = 0;
                ClearLaunchIdentity(candidate);
                var commit = TryCommitCandidateLocked(candidate, null, false);
                if (commit == null) return TransitionWriteFailed("NullCommit");
                if (commit.Status == DurableAuthorityCommitStatus.Busy ||
                    commit.FailureKind == DurableAuthorityFailureKind.Busy)
                    return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                if (commit.Status == DurableAuthorityCommitStatus.Conflict)
                    return TransitionConflict("RejectedNoWorkConflict");
                if (!IsCandidateApplied(commit))
                    return TransitionWriteFailed(commit.Reason ?? "RejectedNoWorkCommitFailed");
                _record = commit.Record.Clone();
                _sha256 = commit.Sha256;
                return new DurableAuthorityTransitionResult
                {
                    Status = DurableAuthorityTransitionStatus.Committed,
                    Reason = candidate.DetailCode,
                    Record = _record.Clone(),
                    Sha256 = _sha256
                };
            }
        }

        private DurableAuthorityTransitionResult TransitionLifecycle(
            DurableLaunchIntentCapability capability,
            DurableRelaunchPermitState expectedState,
            Action<DurableRelaunchAuthorityRecord> mutate)
        {
            return TransitionLifecycle(capability, expectedState, null, mutate);
        }

        private DurableAuthorityTransitionResult TransitionLifecycle(
            DurableLaunchIntentCapability capability,
            DurableRelaunchPermitState expectedState,
            Func<DurableRelaunchAuthorityRecord, string> validateCurrent,
            Action<DurableRelaunchAuthorityRecord> mutate)
        {
            if (capability == null) return TransitionInvalid("LaunchCapabilityMissing");
            var intent = capability.CloneIntent();
            string validation;
            if (!DurableLaunchCanonical.TryValidate(intent, out validation)) return TransitionInvalid(validation);
            lock (_gate)
            {
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    if (_blockedInCurrentProcess) return TransitionBlocked("AuthorityBlocked");
                    var fresh = _store.Load(_sessionId);
                    if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(fresh.Reason ?? "AuthorityMutexBusy");
                    if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                        return TransitionUnproven(fresh?.Reason ?? "AuthorityReloadBlocked");
                    _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                    if (_record.State != expectedState) return TransitionInvalid("ExpectedStateMismatch");
                    if (!MatchesCapability(_record, capability)) return TransitionInvalid("LaunchCapabilityMismatch");
                    var currentValidation = validateCurrent?.Invoke(_record);
                    if (!string.IsNullOrEmpty(currentValidation))
                        return TransitionInvalid(currentValidation);
                    var candidate = _record.Clone();
                    mutate(candidate);
                    var commit = TryCommitCandidateLocked(candidate, null, false);
                    if (commit == null) return TransitionWriteFailed("NullCommit");
                    if (commit.Status == DurableAuthorityCommitStatus.Busy || commit.FailureKind == DurableAuthorityFailureKind.Busy)
                        return TransitionBusy(commit.Reason ?? "AuthorityMutexBusy");
                    if (commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                    if (!IsCandidateApplied(commit)) return TransitionWriteFailed(commit.Reason ?? "LifecycleCommitFailed");
                    _record = commit.Record.Clone(); _sha256 = commit.Sha256;
                    return new DurableAuthorityTransitionResult
                    {
                        Status = DurableAuthorityTransitionStatus.Committed,
                        Reason = "LifecycleCommitted", Record = _record.Clone(), Sha256 = _sha256,
                        Capability = capability
                    };
                }
                return TransitionConflict("LifecycleCasRetryExhausted");
            }
        }

        private bool MatchesIntentIdentity(DurableRelaunchAuthorityRecord record, DurableLaunchIntent intent)
        {
            return record != null && intent != null && string.Equals(record.SessionId, intent.SessionId, StringComparison.Ordinal) &&
                   record.Generation == intent.Generation && string.Equals(record.PermitId, intent.PermitId, StringComparison.Ordinal) &&
                   string.Equals(record.PermitNonce, intent.PermitNonce, StringComparison.Ordinal);
        }

        private bool MatchesCapability(DurableRelaunchAuthorityRecord record, DurableLaunchIntentCapability capability)
        {
            var intent = capability?.CloneIntent();
            return MatchesIntentIdentity(record, intent) && HasLaunchIdentity(record) &&
                   string.Equals(record.LaunchIntentId, intent.IntentId, StringComparison.Ordinal) &&
                   string.Equals(record.LaunchSpecSha256, intent.LaunchSpecSha256, StringComparison.Ordinal) &&
                   record.LaunchAuthorityRevision == capability.AuthorityRevision &&
                   string.Equals(record.LaunchAuthoritySha256, capability.AuthoritySha256, StringComparison.Ordinal);
        }

        private static bool MatchesPermitIdentity(
            DurableRelaunchAuthorityRecord record,
            DurableRelaunchPermitIdentity identity)
        {
            return record != null && identity != null &&
                   record.Generation == identity.Generation &&
                   string.Equals(record.SessionId, identity.SessionId, StringComparison.Ordinal) &&
                   string.Equals(record.PermitId, identity.PermitId, StringComparison.Ordinal);
        }

        private static bool HasLaunchIdentity(DurableRelaunchAuthorityRecord record)
        {
            return record != null && !string.IsNullOrWhiteSpace(record.LaunchIntentId) &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchExecutableSha256) &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchSpecSha256) &&
                   RecoveryFailureReceipt.IsSha256(record.LaunchAuthoritySha256);
        }

        private static DurableAuthorityTransitionResult TransitionInvalid(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Invalid, Reason = reason };
        private static DurableAuthorityTransitionResult TransitionBusy(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Busy, Reason = reason };
        private static DurableAuthorityTransitionResult TransitionBlocked(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Blocked, Reason = reason };
        private static DurableAuthorityTransitionResult TransitionUnproven(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Unproven, Reason = reason };
        private static DurableAuthorityTransitionResult TransitionWriteFailed(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.WriteFailed, Reason = reason };
        private static DurableAuthorityTransitionResult TransitionConflict(string reason) =>
            new DurableAuthorityTransitionResult { Status = DurableAuthorityTransitionStatus.Conflict, Reason = reason };

        public DurableAuthorityReconcileResult ReconcileAfterRestart(
            Func<int, long, DurableRelaunchProcessObservation> processProbe)
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                DurableRelaunchAuthorityRecord observed;
                string observedSha;
                lock (_gate)
                {
                    observed = _record?.Clone();
                    observedSha = _sha256;
                    if (observed == null || observed.State == DurableRelaunchPermitState.None ||
                        observed.State == DurableRelaunchPermitState.Committed || observed.State == DurableRelaunchPermitState.Failed ||
                        observed.State == DurableRelaunchPermitState.Revoked || observed.State == DurableRelaunchPermitState.Blocked ||
                        observed.State == DurableRelaunchPermitState.RejectedNoWork)
                        return new DurableAuthorityReconcileResult
                        {
                            Observation = DurableRelaunchProcessObservation.Unknown,
                            Decision = BlockedDecisionLocked("NoLaunchAfterRestart"), Reason = "NoLaunchAfterRestart"
                        };
                }

                // Approved/Intent have no provable outcome and are blocked
                // without probing or exposing an action.
                if (observed.State == DurableRelaunchPermitState.Approved || observed.State == DurableRelaunchPermitState.LaunchIntent)
                {
                    lock (_gate)
                    {
                        var fresh = _store.Load(_sessionId);
                        // A bounded mutex admission failure is a transient
                        // coordination result, not an outcome-unknown
                        // safety event.  Preserve the observed in-memory
                        // record and durable bytes; callers may retry the
                        // exact reconciliation without creating a marker or
                        // poisoning this process's circuit.
                        if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                            return BusyReconcileLocked(DurableRelaunchProcessObservation.Unknown,
                                fresh.Reason ?? "AuthorityMutexBusy");
                        if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                        { _blockedInCurrentProcess = true; return new DurableAuthorityReconcileResult { OutcomeUnknown = true, Observation = DurableRelaunchProcessObservation.Unknown, Decision = BlockedDecisionLocked("OutcomeUnknownReloadBlocked"), Reason = "OutcomeUnknown" }; }
                        _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                        if (!IdentityMatches(_record, observed, _sha256, observedSha)) continue;
                        var blocked = _record.Clone(); blocked.State = DurableRelaunchPermitState.Blocked; blocked.CircuitOpen = true; blocked.DetailCode = "OutcomeUnknown";
                        if (!string.IsNullOrEmpty(blocked.LastFailureCanonicalSha256))
                            blocked.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
                        var commit = TryCommitCandidateLocked(blocked, null, true);
                        if (commit != null && (commit.Status == DurableAuthorityCommitStatus.Busy ||
                                               commit.FailureKind == DurableAuthorityFailureKind.Busy))
                            return BusyReconcileLocked(DurableRelaunchProcessObservation.Unknown,
                                commit.Reason ?? "AuthorityMutexBusy");
                        if (commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                        if (commit.Durable && commit.Record != null) { _record = commit.Record.Clone(); _sha256 = commit.Sha256; }
                        return new DurableAuthorityReconcileResult
                        {
                            OutcomeUnknown = true, Observation = DurableRelaunchProcessObservation.Unknown,
                            Decision = new DurableAuthorityDecisionResult
                            {
                                CommitStatus = commit.Status, Record = _record.Clone(), Durable = commit.Durable,
                                Blocked = true, RestartSafety = commit.Durable ? DurableAuthorityRestartSafety.Proven : DurableAuthorityRestartSafety.Unproven,
                                Reason = "OutcomeUnknown", AuthorityRevision = _record.AuthorityRevision
                            }, Reason = "OutcomeUnknown"
                        };
                    }
                }

                DurableRelaunchProcessObservation observation;
                try { observation = processProbe == null ? DurableRelaunchProcessObservation.Unknown : processProbe(observed.ProcessId, observed.ProcessStartUtcTicks); }
                catch { observation = DurableRelaunchProcessObservation.Unknown; }
                lock (_gate)
                {
                    var fresh = _store.Load(_sessionId);
                    // Do not turn a post-probe mutex miss into a local
                    // blocked state.  The probe is only evidence; the
                    // durable reload is still the authority boundary.
                    if (fresh != null && fresh.FailureKind == DurableAuthorityFailureKind.Busy)
                        return BusyReconcileLocked(observation, fresh.Reason ?? "AuthorityMutexBusy");
                    if (fresh == null || fresh.Record == null || fresh.Blocked || fresh.Unproven)
                    { _blockedInCurrentProcess = true; return new DurableAuthorityReconcileResult { Observation = observation, Decision = BlockedDecisionLocked("ReconcileReloadBlocked"), Reason = "ReconcileReloadBlocked" }; }
                    _record = fresh.Record.Clone(); _sha256 = fresh.Sha256;
                    if (!IdentityMatches(_record, observed, _sha256, observedSha)) continue;
                    if (observation == DurableRelaunchProcessObservation.Alive)
                        return new DurableAuthorityReconcileResult
                        {
                            Observation = observation,
                            Decision = new DurableAuthorityDecisionResult
                            {
                                CommitStatus = DurableAuthorityCommitStatus.Committed, Record = _record.Clone(), Durable = true,
                                ActionAllowed = false, RestartSafety = DurableAuthorityRestartSafety.Proven,
                                Reason = "ProcessAlive", AuthorityRevision = _record.AuthorityRevision
                            }, Reason = "ProcessAlive"
                        };

                    var failed = _record.Clone();
                    failed.ConsecutiveFailures = Math.Max(0, failed.ConsecutiveFailures) + 1;
                    failed.State = observation == DurableRelaunchProcessObservation.Dead && failed.ConsecutiveFailures < Math.Max(1, failed.MaximumProcessRelaunches)
                        ? DurableRelaunchPermitState.Failed : DurableRelaunchPermitState.Blocked;
                    failed.CircuitOpen = failed.State == DurableRelaunchPermitState.Blocked;
                    failed.DetailCode = observation == DurableRelaunchProcessObservation.Dead ? "ProcessDeadAfterRestart" : "ProcessIdentityUnknown";
                    if (failed.CircuitOpen && !string.IsNullOrEmpty(failed.LastFailureCanonicalSha256))
                        failed.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
                    var commit = TryCommitCandidateLocked(failed, null, failed.CircuitOpen);
                    if (commit != null && (commit.Status == DurableAuthorityCommitStatus.Busy ||
                                           commit.FailureKind == DurableAuthorityFailureKind.Busy))
                        return BusyReconcileLocked(observation, commit.Reason ?? "AuthorityMutexBusy");
                    if (commit.Status == DurableAuthorityCommitStatus.Conflict) continue;
                    if (commit.Durable && commit.Record != null) { _record = commit.Record.Clone(); _sha256 = commit.Sha256; }
                    return new DurableAuthorityReconcileResult
                    {
                        Observation = observation,
                        Decision = new DurableAuthorityDecisionResult
                        {
                            CommitStatus = commit.Status, Record = _record.Clone(), Durable = commit.Durable,
                            Blocked = failed.State == DurableRelaunchPermitState.Blocked,
                            RestartSafety = commit.Durable ? DurableAuthorityRestartSafety.Proven : DurableAuthorityRestartSafety.Unproven,
                            Reason = failed.DetailCode, AuthorityRevision = _record.AuthorityRevision
                        }, Reason = failed.DetailCode
                    };
                }
            }
            lock (_gate) { _blockedInCurrentProcess = true; return new DurableAuthorityReconcileResult { Observation = DurableRelaunchProcessObservation.Unknown, Decision = InvalidDecision("ReconcileCasRetryExhausted"), Reason = "ReconcileCasRetryExhausted" }; }
        }

        private DurableAuthorityReconcileResult BusyReconcileLocked(
            DurableRelaunchProcessObservation observation,
            string reason)
        {
            return new DurableAuthorityReconcileResult
            {
                Observation = observation,
                OutcomeUnknown = false,
                Decision = BusyDecisionLocked(reason ?? "AuthorityMutexBusy"),
                Reason = reason ?? "AuthorityMutexBusy"
            };
        }

        private DurableAuthorityDecisionResult ReplayLocked(RecoveryFailureFrozenOperation frozen)
        {
            if (_record.State == DurableRelaunchPermitState.Blocked || _record.State == DurableRelaunchPermitState.Revoked || _record.CircuitOpen)
                return StickyBlockedDecisionLocked(frozen, _record.DetailCode ?? "CircuitOpen");
            var receipt = BuildReceipt(frozen, _record, _record.LastFailureDisposition,
                true, true, true, _record.CircuitOpen, _record.DetailCode ?? "Replay");
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.Committed, Receipt = receipt,
                Record = _record.Clone(), Durable = true, ActionAllowed =
                    string.Equals(_record.LastFailureDisposition, RecoveryFailureDispositions.RelaunchApproved, StringComparison.Ordinal) && !_record.CircuitOpen,
                Blocked = _record.CircuitOpen, FailureRegistered = true,
                RestartSafety = DurableAuthorityRestartSafety.Proven, Reason = "IdempotentReplay",
                AuthorityRevision = _record.AuthorityRevision
            };
        }

        private DurableAuthorityDecisionResult StickyBlockedDecisionLocked(RecoveryFailureFrozenOperation frozen, string reason)
        {
            // ExistingBlocked is a durable status, never an in-memory
            // interpretation of a null/stale record.  If the proof or
            // read-back pair is no longer valid, expose ReadFailed instead
            // and do not clone the stale record into a decision.
            if (!IsDurableRecordPair(_record, _sha256))
            {
                return new DurableAuthorityDecisionResult
                {
                    CommitStatus = DurableAuthorityCommitStatus.ReadFailed,
                    Receipt = null, Record = null, Durable = false,
                    ActionAllowed = false, Blocked = true,
                    BlockedInCurrentProcess = true,
                    Unproven = true,
                    FailureRegistered = false,
                    RestartSafety = DurableAuthorityRestartSafety.Unproven,
                    Reason = reason ?? "AuthorityReadFailed",
                    AuthorityRevision = 0
                };
            }
            // A sticky replay must describe the durable blocked operation
            // which actually won the authority race.  Never echo the new
            // caller's correlation/detail as if it had been registered.
            RecoveryFailureReceipt receipt = null;
            if (!string.IsNullOrEmpty(_record.LastFailureCorrelationId) &&
                RecoveryFailureReceipt.IsValidCorrelation(_record.LastFailureCorrelationId) &&
                RecoveryFailureReceipt.IsSha256(_record.LastFailurePayloadSha256) &&
                !string.IsNullOrEmpty(_record.LastFailureCode) &&
                !string.IsNullOrEmpty(_record.LastFailureFingerprint) &&
                _record.LastFailureDecisionSequence > 0)
            {
                receipt = new RecoveryFailureReceipt
                {
                    RequestCorrelationId = _record.LastFailureCorrelationId,
                    RequestPayloadSha256 = _record.LastFailurePayloadSha256,
                    FailureCode = _record.LastFailureCode,
                    FailureFingerprint = _record.LastFailureFingerprint,
                    Disposition = RecoveryFailureDispositions.CircuitOpen,
                    Durable = true, PermitClosed = true, FailureRegistered = false,
                    CircuitOpen = true, ConsecutiveCount = _record.ConsecutiveFailures,
                    RelaunchPermitGeneration = _record.Generation,
                    DecisionSequence = _record.LastFailureDecisionSequence,
                    DecisionUtcTicks = _record.LastFailureDecisionUtcTicks,
                    // DetailCode is the effective durable disposition used
                    // by the winning receipt (LastFailureDetailCode is the
                    // caller's failure code and may intentionally differ,
                    // e.g. PermanentFailure/BudgetExhausted).
                    DetailCode = _record.DetailCode ?? _record.LastFailureDetailCode ?? "CircuitOpen"
                };
            }
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.ExistingBlocked,
                Receipt = receipt,
                Record = _record?.Clone(),
                Durable = IsDurableRecordPair(_record, _sha256),
                ActionAllowed = false,
                Blocked = true,
                BlockedInCurrentProcess = _blockedInCurrentProcess,
                FailureRegistered = false,
                RestartSafety = IsDurableRecordPair(_record, _sha256) ? DurableAuthorityRestartSafety.Proven : DurableAuthorityRestartSafety.Unproven,
                Reason = reason ?? "CircuitOpen",
                AuthorityRevision = _record?.AuthorityRevision ?? 0
            };
        }

        private DurableAuthorityDecisionResult BusyDecisionLocked(string reason)
        {
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.Busy,
                Receipt = null, Record = null, Durable = false,
                ActionAllowed = false, Blocked = false,
                BlockedInCurrentProcess = false, Unproven = true,
                FailureRegistered = false,
                RestartSafety = DurableAuthorityRestartSafety.Unproven,
                Reason = reason ?? "AuthorityMutexBusy", AuthorityRevision = 0
            };
        }

        private DurableAuthorityDecisionResult BudgetMismatchDecisionLocked()
        {
            // A caller-supplied budget is not authority evidence.  Reject it
            // without registering a failure or writing a blocked candidate;
            // a later request carrying the proof-frozen budget remains
            // eligible to proceed.
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.Invalid,
                Receipt = null, Record = _record?.Clone(), Durable = false,
                ActionAllowed = false, Blocked = true,
                BlockedInCurrentProcess = false, Unproven = false,
                FailureRegistered = false,
                RestartSafety = DurableAuthorityRestartSafety.Proven,
                Reason = "BudgetMismatch", AuthorityRevision = _record?.AuthorityRevision ?? 0
            };
        }

        private DurableAuthorityDecisionResult PersistIdentityConflictLocked(RecoveryFailureFrozenOperation frozen, string reason)
        {
            var candidate = _record.Clone();
            ApplyFailure(candidate, frozen, RecoveryFailureDispositions.IdentityConflict, reason, candidate.ConsecutiveFailures);
            candidate.State = DurableRelaunchPermitState.Blocked;
            candidate.CircuitOpen = true;
            var receipt = BuildReceipt(frozen, candidate, RecoveryFailureDispositions.IdentityConflict,
                false, true, true, true, reason);
            var commit = TryCommitCandidateLocked(candidate, receipt, true);
            if (commit != null && commit.Status == DurableAuthorityCommitStatus.Busy)
                return BusyDecisionLocked(commit.Reason ?? "AuthorityMutexBusy");
            if (commit != null && commit.Status == DurableAuthorityCommitStatus.ExistingBlocked)
            {
                if (commit.Record != null)
                {
                    _record = commit.Record.Clone();
                    _sha256 = commit.Sha256;
                }
                return StickyBlockedDecisionLocked(frozen, _record?.DetailCode ?? "CircuitOpen");
            }
            if (commit != null && IsDurableApplied(commit))
            {
                _record = commit.Record.Clone();
                _sha256 = commit.Sha256;
                receipt.Durable = true;
                return new DurableAuthorityDecisionResult
                {
                    CommitStatus = commit.Status, Receipt = receipt.Clone(), Record = _record.Clone(),
                    Durable = true, ActionAllowed = false, Blocked = true,
                    FailureRegistered = true, RestartSafety = DurableAuthorityRestartSafety.Proven,
                    Reason = reason, AuthorityRevision = _record.AuthorityRevision
                };
            }
            if (commit != null && commit.Status == DurableAuthorityCommitStatus.Conflict)
                return new DurableAuthorityDecisionResult { CommitStatus = DurableAuthorityCommitStatus.Conflict, Reason = reason };
            return FailClosedAfterCommitLocked(frozen, candidate, receipt, commit?.Reason ?? reason);
        }

        private DurableAuthorityDecisionResult BlockedDecisionLocked(string reason, RecoveryFailureFrozenOperation frozen = null)
        {
            if (_record != null && !string.IsNullOrEmpty(_sha256) &&
                (_record.State == DurableRelaunchPermitState.Blocked || _record.State == DurableRelaunchPermitState.Revoked || _record.CircuitOpen))
                return StickyBlockedDecisionLocked(frozen, reason);
            return new DurableAuthorityDecisionResult
            {
                // A failed reload/proof/read is not a durable blocked record.
                // Never expose the constructor's synthetic blocked sentinel
                // or a stale pre-failure record as if it were authoritative.
                CommitStatus = DurableAuthorityCommitStatus.ReadFailed,
                Record = null,
                Receipt = null,
                Durable = false, Blocked = true, BlockedInCurrentProcess = true,
                Unproven = true,
                RestartSafety = DurableAuthorityRestartSafety.Unproven, Reason = reason,
                AuthorityRevision = 0
            };
        }

        private DurableAuthorityDecisionResult InvalidDecision(string reason, string disposition = null)
        {
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.Invalid, Durable = false, Blocked = true,
                BlockedInCurrentProcess = true, RestartSafety = DurableAuthorityRestartSafety.Unproven,
                Reason = reason, Receipt = new RecoveryFailureReceipt
                {
                    RequestCorrelationId = string.Empty, RequestPayloadSha256 = string.Empty,
                    FailureCode = "InvalidOperation", FailureFingerprint = "InvalidOperation",
                    Disposition = disposition ?? RecoveryFailureDispositions.CircuitOpen,
                    DetailCode = reason, DecisionUtcTicks = DateTime.UtcNow.Ticks
                }
            };
        }

        private DurableAuthorityDecisionResult FailClosedAfterCommitLocked(
            RecoveryFailureFrozenOperation frozen,
            DurableRelaunchAuthorityRecord candidate,
            RecoveryFailureReceipt receipt,
            string reason)
        {
            var marker = candidate.Clone();
            marker.State = DurableRelaunchPermitState.Blocked;
            marker.CircuitOpen = true;
            marker.DetailCode = reason ?? "DurableFailureCommitFailed";
            if (!string.IsNullOrEmpty(marker.LastFailureCanonicalSha256))
                marker.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
            var markerCommit = TryCommitCandidateLocked(marker, receipt, true);
            if (markerCommit != null && markerCommit.Status == DurableAuthorityCommitStatus.Conflict)
            {
                // The candidate may have reached File.Replace before its
                // read-back failed. Reload the complete new record and
                // rebuild the marker against that fresh revision; never
                // patch a stale expected revision.
                var fresh = _store.Load(_sessionId);
                if (fresh != null && fresh.Record != null && !fresh.Blocked && !fresh.Unproven)
                {
                    _record = fresh.Record.Clone();
                    _sha256 = fresh.Sha256;
                    marker = candidate.Clone();
                    marker.State = DurableRelaunchPermitState.Blocked;
                    marker.CircuitOpen = true;
                    marker.DetailCode = reason ?? "DurableFailureCommitFailed";
                    if (!string.IsNullOrEmpty(marker.LastFailureCanonicalSha256))
                        marker.LastFailureDisposition = RecoveryFailureDispositions.CircuitOpen;
                    markerCommit = TryCommitCandidateLocked(marker, receipt, true);
                }
            }
            if (markerCommit != null && IsBlockedApplied(markerCommit))
            {
                _record = markerCommit.Record.Clone();
                _sha256 = markerCommit.Sha256;
                if (markerCommit.Status == DurableAuthorityCommitStatus.ExistingBlocked)
                    return StickyBlockedDecisionLocked(frozen, _record.DetailCode ?? "CircuitOpen");
                receipt.Disposition = RecoveryFailureDispositions.CircuitOpen;
                receipt.Durable = true;
                receipt.FailureRegistered = false;
                receipt.CircuitOpen = true;
                receipt.DetailCode = marker.DetailCode;
                return new DurableAuthorityDecisionResult
                {
                    // The marker is a normal candidate transaction which
                    // was read back successfully.  DurableBlocked is kept
                    // only for legacy stores; production marker success is
                    // CandidateApplied + Record.CircuitOpen.
                    CommitStatus = markerCommit.Status == DurableAuthorityCommitStatus.ExistingBlocked
                        ? DurableAuthorityCommitStatus.ExistingBlocked
                        : DurableAuthorityCommitStatus.CandidateApplied,
                    Receipt = receipt.Clone(), Record = _record.Clone(), Durable = true,
                    Blocked = true, BlockedInCurrentProcess = false,
                    RestartSafety = DurableAuthorityRestartSafety.Proven,
                    Reason = marker.DetailCode, AuthorityRevision = _record.AuthorityRevision
                };
            }
            _blockedInCurrentProcess = true;
            receipt.Disposition = RecoveryFailureDispositions.CircuitOpen;
            receipt.Durable = false; receipt.FailureRegistered = false; receipt.CircuitOpen = true;
            receipt.DetailCode = "DurableFailureCommitFailed";
            return new DurableAuthorityDecisionResult
            {
                CommitStatus = DurableAuthorityCommitStatus.WriteFailed, Receipt = receipt.Clone(), Record = _record?.Clone(),
                Durable = false, Blocked = true, BlockedInCurrentProcess = true, Unproven = true,
                RestartSafety = DurableAuthorityRestartSafety.Unproven, Reason = reason ?? "DurableFailureCommitFailed",
                AuthorityRevision = _record?.AuthorityRevision ?? 0
            };
        }

        private DurableAuthorityStoreCommitResult TryCommitCandidateLocked(
            DurableRelaunchAuthorityRecord candidate, RecoveryFailureReceipt receipt, bool blockedMarker)
        {
            var expected = _record?.AuthorityRevision ?? 0;
            var expectedSha = _sha256 ?? string.Empty;
            candidate = candidate.Clone();
            candidate.AuthorityRevision = expected + 1;
            candidate.SchemaVersion = 4;
            candidate.RecordKind = "DurableRelaunchAuthority";
            candidate.RecordFormatRevision = DurableRelaunchAuthorityV4Validator.RequiredFormatRevision;
            candidate.LastTransitionUtcTicks = DateTime.UtcNow.Ticks;
            var result = _store.TryCommit(candidate, expected, expectedSha, blockedMarker);
            if (result == null) return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.ReadFailed, Reason = "NullCommit" };
            if (IsDurableApplied(result) && !IsDurableRecordPair(result.Record, result.Sha256))
                return new DurableAuthorityStoreCommitResult { Status = DurableAuthorityCommitStatus.ReadFailed, Reason = "CommitReadBackUnproven" };
            return result;
        }

        private bool IsDurableRecordPair(DurableRelaunchAuthorityRecord record, string sha256)
        {
            if (record == null || !RecoveryFailureReceipt.IsSha256(sha256) ||
                !string.Equals(record.SessionId, _sessionId, StringComparison.Ordinal)) return false;
            string reason;
            return DurableRelaunchAuthorityV4Validator.TryValidateRecord(record, _sessionId, out reason);
        }

        private bool IsCandidateApplied(DurableAuthorityStoreCommitResult result) =>
            result != null && (result.Status == DurableAuthorityCommitStatus.Committed ||
                               result.Status == DurableAuthorityCommitStatus.CandidateApplied) &&
            IsDurableRecordPair(result.Record, result.Sha256);

        private bool IsBlockedApplied(DurableAuthorityStoreCommitResult result) =>
            result != null && (result.Status == DurableAuthorityCommitStatus.CandidateApplied ||
                               result.Status == DurableAuthorityCommitStatus.DurableBlocked ||
                               result.Status == DurableAuthorityCommitStatus.ExistingBlocked) &&
            IsDurableRecordPair(result.Record, result.Sha256) && result.Record.CircuitOpen;

        private bool IsDurableApplied(DurableAuthorityStoreCommitResult result) =>
            IsCandidateApplied(result) || IsBlockedApplied(result);

        private static void ApplyFailure(DurableRelaunchAuthorityRecord candidate, RecoveryFailureFrozenOperation frozen, string disposition, string detail, int count)
        {
            candidate.SessionId = frozen.SessionId;
            candidate.ConsecutiveFailures = count;
            // MaximumProcessRelaunches is a bootstrap-frozen authority field;
            // never copy an operation's requested budget into the record.
            candidate.MaximumProcessRelaunches = Math.Max(1, candidate.MaximumProcessRelaunches);
            candidate.ConnectionGeneration = frozen.ConnectionGeneration;
            candidate.RecoveryAttemptGeneration = frozen.RecoveryAttemptGeneration;
            candidate.RunId = frozen.RunId; candidate.RunEpoch = frozen.RunEpoch;
            candidate.RecoveryStage = frozen.RecoveryStage; candidate.RecoveryProgressToken = frozen.RecoveryProgressToken;
            candidate.RecoveryProcessSource = frozen.RecoveryProcessSource; candidate.DeviceOrChannelGroup = frozen.DeviceOrChannelGroup;
            candidate.LastFailureOperationId = frozen.OperationId;
            candidate.LastFailureCorrelationId = frozen.RequestCorrelationId;
            candidate.LastFailurePayloadSha256 = frozen.RequestPayloadSha256;
            // The authority budget is immutable.  For a budget-mismatch
            // identity conflict the durable blocked record must still carry
            // a self-consistent evidence digest; recompute the canonical
            // projection with the frozen authority budget rather than
            // persisting a digest for a value that is deliberately rejected.
            candidate.LastFailureCanonicalSha256 = CanonicalForAuthorityBudget(frozen, candidate.MaximumProcessRelaunches);
            candidate.LastFailureFingerprint = frozen.FailureFingerprint;
            candidate.LastFailureCode = frozen.FailureCode;
            candidate.LastFailureDetailCode = frozen.DetailCode;
            candidate.LastFailureRunId = frozen.RunId;
            candidate.LastFailureRunEpoch = frozen.RunEpoch;
            candidate.LastFailureRecoveryStage = frozen.RecoveryStage;
            candidate.LastFailureRecoveryProgressToken = frozen.RecoveryProgressToken;
            candidate.LastFailureRecoveryProcessSource = frozen.RecoveryProcessSource;
            candidate.LastFailureDeviceOrChannelGroup = frozen.DeviceOrChannelGroup;
            candidate.LastFailureSessionNonce = frozen.SessionNonce;
            candidate.LastFailureProcessId = frozen.SidecarProcessId;
            candidate.LastFailureProcessStartUtcTicks = frozen.SidecarProcessStartUtcTicks;
            candidate.LastFailureConnectionGeneration = frozen.ConnectionGeneration;
            candidate.LastFailureAttemptGeneration = frozen.RecoveryAttemptGeneration;
            candidate.LastFailurePermitGeneration = candidate.Generation;
            candidate.LastFailurePermitId = candidate.PermitId;
            candidate.LastFailurePermitNonce = candidate.PermitNonce;
            candidate.LastFailureDisposition = disposition;
            candidate.LastFailureDecisionSequence = Math.Max(0, candidate.LastFailureDecisionSequence) + 1;
            candidate.LastFailureDecisionUtcTicks = DateTime.UtcNow.Ticks;
            candidate.LastFailurePermanent = frozen.Permanent;
            candidate.DetailCode = detail;
        }

        private static void ClearLaunchIdentity(DurableRelaunchAuthorityRecord candidate)
        {
            if (candidate == null) return;
            candidate.ProcessId = 0;
            candidate.ProcessStartUtcTicks = 0;
            candidate.LaunchIntentId = null;
            candidate.LaunchExecutablePath = null;
            candidate.LaunchExecutableSha256 = null;
            candidate.LaunchArguments = null;
            candidate.LaunchWorkingDirectory = null;
            candidate.LaunchOptionsCanonical = null;
            candidate.LaunchSpecSha256 = null;
            candidate.LaunchAuthorityRevision = 0;
            candidate.LaunchAuthoritySha256 = null;
            candidate.LaunchConsumed = false;
            candidate.RecoveryCommitGeneration = 0;
        }

        private static string CanonicalForAuthorityBudget(RecoveryFailureFrozenOperation frozen, int budget)
        {
            if (frozen == null) return null;
            var operation = frozen.ToOperation();
            operation.MaximumProcessRelaunches = Math.Max(1, budget);
            return operation.CanonicalSha256();
        }

        private static RecoveryFailureReceipt BuildReceipt(RecoveryFailureFrozenOperation frozen, DurableRelaunchAuthorityRecord record, string disposition, bool durable, bool permitClosed, bool registered, bool circuitOpen, string detail)
        {
            return new RecoveryFailureReceipt
            {
                RequestCorrelationId = frozen.RequestCorrelationId,
                RequestPayloadSha256 = frozen.RequestPayloadSha256,
                FailureCode = frozen.FailureCode,
                FailureFingerprint = frozen.FailureFingerprint,
                Disposition = RecoveryFailureDispositions.IsDefined(disposition) ? disposition : RecoveryFailureDispositions.CircuitOpen,
                Durable = durable, PermitClosed = permitClosed, FailureRegistered = registered,
                CircuitOpen = circuitOpen, ConsecutiveCount = Math.Max(0, record.ConsecutiveFailures),
                RelaunchPermitGeneration = Math.Max(0, record.Generation), DecisionSequence = Math.Max(1, record.LastFailureDecisionSequence),
                DecisionUtcTicks = Math.Max(1, record.LastFailureDecisionUtcTicks), DetailCode = detail ?? "RecoveryFailure"
            };
        }

        private static bool HasCorrelation(DurableRelaunchAuthorityRecord record, string correlation) =>
            record != null && string.Equals(record.LastFailureCorrelationId, correlation, StringComparison.Ordinal);

        private static bool StableTupleMatches(DurableRelaunchAuthorityRecord record, RecoveryFailureFrozenOperation operation)
        {
            return record != null && string.Equals(record.LastFailureSessionNonce, operation.SessionNonce, StringComparison.Ordinal) &&
                   record.LastFailureProcessId == operation.SidecarProcessId && record.LastFailureProcessStartUtcTicks == operation.SidecarProcessStartUtcTicks &&
                   record.LastFailureConnectionGeneration == operation.ConnectionGeneration && record.LastFailureAttemptGeneration == operation.RecoveryAttemptGeneration &&
                   string.Equals(record.LastFailureOperationId, operation.OperationId, StringComparison.Ordinal);
        }

        private static bool IdentityMatches(
            DurableRelaunchAuthorityRecord current,
            DurableRelaunchAuthorityRecord observed,
            string currentSha,
            string observedSha)
        {
            return current != null && observed != null &&
                   string.Equals(current.SessionId, observed.SessionId, StringComparison.Ordinal) &&
                   current.AuthorityRevision == observed.AuthorityRevision &&
                   string.Equals(currentSha ?? string.Empty, observedSha ?? string.Empty, StringComparison.Ordinal) &&
                   current.Generation == observed.Generation &&
                   string.Equals(current.PermitId, observed.PermitId, StringComparison.Ordinal) &&
                   string.Equals(current.PermitNonce, observed.PermitNonce, StringComparison.Ordinal) &&
                   current.ProcessId == observed.ProcessId &&
                   current.ProcessStartUtcTicks == observed.ProcessStartUtcTicks &&
                   current.ConnectionGeneration == observed.ConnectionGeneration &&
                   current.RecoveryAttemptGeneration == observed.RecoveryAttemptGeneration;
        }

        internal static bool IsCanonicalSession(string value)
        {
            Guid guid;
            return !string.IsNullOrEmpty(value) && Guid.TryParseExact(value, "N", out guid) &&
                   string.Equals(guid.ToString("N"), value, StringComparison.Ordinal);
        }
    }

    public static class DurableRelaunchAuthorityFactory
    {
        public static DurableAuthorityOpenResult TryCreatePristine(
            string directory, string sessionId, int processId, long processStartUtcTicks)
        {
            return TryCreatePristine(directory, sessionId, processId, processStartUtcTicks, 8);
        }

        public static DurableAuthorityOpenResult TryCreatePristine(
            string directory, string sessionId, int processId, long processStartUtcTicks,
            int maximumProcessRelaunches)
        {
            WatchdogJournalBootstrapReceipt receipt;
            string error;
            if (!WatchdogJournalBootstrap.TryCreateAuthorityNew(directory, sessionId, processId, processStartUtcTicks,
                maximumProcessRelaunches, out receipt, out error))
                return new DurableAuthorityOpenResult { Succeeded = false, Blocked = true, Unproven = true, Reason = error };
            var store = new DurableRelaunchAuthorityFileStore(directory, sessionId);
            var pristine = DurableRelaunchAuthorityV4Validator.CreatePristine(sessionId, maximumProcessRelaunches);
            pristine.BootstrapPrimaryPath = receipt.Path;
            pristine.BootstrapPrimarySha256 = receipt.SHA256;
            pristine.BootstrapProofPath = receipt.ProofPath;
            pristine.BootstrapProofSha256 = receipt.ProofSHA256;
            pristine.BootstrapProofMarker = receipt.ProofMarker;
            pristine.BootstrapMaximumProcessRelaunches = receipt.MaximumProcessRelaunches;
            var created = store.CreatePristine(pristine);
            if (created == null || created.Status != DurableAuthorityCommitStatus.Committed)
                return new DurableAuthorityOpenResult { Succeeded = false, Blocked = true, Unproven = true, BootstrapReceipt = receipt, Reason = created?.Reason ?? "AuthorityCreateFailed" };
            WatchdogJournalBootstrapReceipt proofAfter;
            string proofAfterError;
            if (!WatchdogJournalBootstrap.TryReadBootstrapProof(directory, sessionId, out proofAfter, out proofAfterError) ||
                !string.Equals(proofAfter.ProofPath, receipt.ProofPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(proofAfter.ProofSHA256, receipt.ProofSHA256, StringComparison.Ordinal) ||
                proofAfter.MaximumProcessRelaunches != maximumProcessRelaunches)
                return new DurableAuthorityOpenResult { Succeeded = false, Blocked = true, Unproven = true, BootstrapReceipt = receipt, Reason = proofAfterError ?? "BootstrapProofChanged" };
            var read = store.Load(sessionId);
            if (read == null || read.Record == null || read.Blocked || read.Unproven ||
                !string.Equals(receipt.Path, read.Record.BootstrapPrimaryPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.SHA256, read.Record.BootstrapPrimarySha256, StringComparison.Ordinal) ||
                !string.Equals(receipt.ProofPath, read.Record.BootstrapProofPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(receipt.ProofSHA256, read.Record.BootstrapProofSha256, StringComparison.Ordinal) ||
                !string.Equals(receipt.ProofMarker, read.Record.BootstrapProofMarker, StringComparison.Ordinal) ||
                read.Record.MaximumProcessRelaunches != maximumProcessRelaunches ||
                read.Record.BootstrapMaximumProcessRelaunches != maximumProcessRelaunches ||
                !string.Equals(receipt.Marker, read.Record.BootstrapMarker, StringComparison.Ordinal) ||
                !string.Equals(receipt.SessionId, read.Record.SessionId, StringComparison.Ordinal))
                return new DurableAuthorityOpenResult { Succeeded = false, Blocked = true, Unproven = true, Store = read, BootstrapReceipt = receipt, Reason = read?.Reason ?? "BootstrapReadFailed" };
            var primary = WatchdogJournalBootstrapReceiptCopy(receipt);
            var capability = new DurableRelaunchAuthorityBootstrapCapability(primary, store.CanonicalPath);
            var authority = new DurableRelaunchAuthorityV4(sessionId, store, capability);
            return new DurableAuthorityOpenResult { Succeeded = true, Authority = authority, BootstrapReceipt = receipt, Store = read, Reason = "Created" };
        }

        public static DurableAuthorityOpenResult TryOpenExisting(string directory, string sessionId)
        {
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(sessionId))
                return new DurableAuthorityOpenResult { Blocked = true, Unproven = true, Reason = "SessionIdNotN" };
            WatchdogJournalBootstrapReceipt primary;
            string primaryError;
            if (!WatchdogJournalBootstrap.TryReadBootstrapProof(directory, sessionId, out primary, out primaryError))
                return new DurableAuthorityOpenResult { Blocked = true, Unproven = true, Reason = primaryError ?? "PrimaryBootstrapBlocked" };
            var store = new DurableRelaunchAuthorityFileStore(directory, sessionId);
            var read = store.Load(sessionId);
            if (read == null || read.Record == null || read.Blocked || read.Unproven || string.IsNullOrEmpty(read.Record.BootstrapMarker) ||
                !string.Equals(primary.Path, read.Record.BootstrapPrimaryPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(primary.SHA256, read.Record.BootstrapPrimarySha256, StringComparison.Ordinal) ||
                !string.Equals(primary.ProofPath, read.Record.BootstrapProofPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(primary.ProofSHA256, read.Record.BootstrapProofSha256, StringComparison.Ordinal) ||
                !string.Equals(primary.ProofMarker, read.Record.BootstrapProofMarker, StringComparison.Ordinal) ||
                read.Record.MaximumProcessRelaunches != primary.MaximumProcessRelaunches ||
                read.Record.BootstrapMaximumProcessRelaunches != primary.MaximumProcessRelaunches)
                return new DurableAuthorityOpenResult { Blocked = true, Unproven = true, Store = read, Reason = read?.Reason ?? "AuthorityOpenBlocked" };
            var cap = new DurableRelaunchAuthorityBootstrapCapability(primary, store.CanonicalPath);
            var authority = new DurableRelaunchAuthorityV4(sessionId, store, cap);
            return new DurableAuthorityOpenResult { Succeeded = true, Authority = authority, BootstrapReceipt = primary, Store = read, Reason = "Opened" };
        }

        private static WatchdogJournalBootstrapReceipt WatchdogJournalBootstrapReceiptCopy(WatchdogJournalBootstrapReceipt source)
        {
            // Receipt is immutable; keeping this helper local makes it
            // explicit that the capability is bound to the read-back primary
            // snapshot, never to caller-owned mutable data.
            return source;
        }
    }

    /// <summary>Explicit V4-named facade; it delegates to the same factory.</summary>
    public static class DurableRelaunchAuthorityV4Factory
    {
        public static DurableAuthorityOpenResult TryCreatePristine(string directory, string sessionId, int processId, long processStartUtcTicks) =>
            DurableRelaunchAuthorityFactory.TryCreatePristine(directory, sessionId, processId, processStartUtcTicks);
        public static DurableAuthorityOpenResult TryCreatePristine(string directory, string sessionId, int processId, long processStartUtcTicks, int maximumProcessRelaunches) =>
            DurableRelaunchAuthorityFactory.TryCreatePristine(directory, sessionId, processId, processStartUtcTicks, maximumProcessRelaunches);
        public static DurableAuthorityOpenResult TryOpenExisting(string directory, string sessionId) =>
            DurableRelaunchAuthorityFactory.TryOpenExisting(directory, sessionId);
    }

    /// <summary>Immutable operation captured at the authority boundary.</summary>
    public sealed class RecoveryFailureFrozenOperation
    {
        private RecoveryFailureFrozenOperation(RecoveryFailureOperation source, string canonical)
        {
            OperationId = source.OperationId; SessionId = source.SessionId; SessionNonce = source.SessionNonce;
            SidecarProcessId = source.SidecarProcessId; SidecarProcessStartUtcTicks = source.SidecarProcessStartUtcTicks;
            ConnectionGeneration = source.ConnectionGeneration; RecoveryAttemptGeneration = source.RecoveryAttemptGeneration;
            RequestCorrelationId = source.RequestCorrelationId; RequestPayloadSha256 = source.RequestPayloadSha256;
            FailureCode = source.FailureCode; FailureFingerprint = source.FailureFingerprint; Permanent = source.Permanent;
            DetailCode = source.DetailCode; RunId = source.RunId; RunEpoch = source.RunEpoch; RecoveryStage = source.RecoveryStage;
            RecoveryProgressToken = source.RecoveryProgressToken; RecoveryProcessSource = source.RecoveryProcessSource;
            DeviceOrChannelGroup = source.DeviceOrChannelGroup; MaximumProcessRelaunches = source.MaximumProcessRelaunches;
            CanonicalSha256 = canonical;
        }

        public string OperationId { get; }
        public string SessionId { get; }
        public string SessionNonce { get; }
        public int SidecarProcessId { get; }
        public long SidecarProcessStartUtcTicks { get; }
        public long ConnectionGeneration { get; }
        public long RecoveryAttemptGeneration { get; }
        public string RequestCorrelationId { get; }
        public string RequestPayloadSha256 { get; }
        public string FailureCode { get; }
        public string FailureFingerprint { get; }
        public bool Permanent { get; }
        public string DetailCode { get; }
        public string RunId { get; }
        public long RunEpoch { get; }
        public string RecoveryStage { get; }
        public string RecoveryProgressToken { get; }
        public string RecoveryProcessSource { get; }
        public string DeviceOrChannelGroup { get; }
        public int MaximumProcessRelaunches { get; }
        public string CanonicalSha256 { get; }

        public RecoveryFailureOperation ToOperation() => new RecoveryFailureOperation
        {
            OperationId = OperationId, SessionId = SessionId, SessionNonce = SessionNonce,
            SidecarProcessId = SidecarProcessId, SidecarProcessStartUtcTicks = SidecarProcessStartUtcTicks,
            ConnectionGeneration = ConnectionGeneration, RecoveryAttemptGeneration = RecoveryAttemptGeneration,
            RequestCorrelationId = RequestCorrelationId, RequestPayloadSha256 = RequestPayloadSha256,
            FailureCode = FailureCode, FailureFingerprint = FailureFingerprint, Permanent = Permanent,
            DetailCode = DetailCode, RunId = RunId, RunEpoch = RunEpoch, RecoveryStage = RecoveryStage,
            RecoveryProgressToken = RecoveryProgressToken, RecoveryProcessSource = RecoveryProcessSource,
            DeviceOrChannelGroup = DeviceOrChannelGroup, MaximumProcessRelaunches = MaximumProcessRelaunches
        };

        public static bool TryFreeze(RecoveryFailureOperation input, out RecoveryFailureFrozenOperation frozen, out string reason)
        {
            frozen = null; reason = null;
            if (input == null) { reason = "OperationMissing"; return false; }
            var clone = input.Clone();
            Normalize(clone);
            if (!DurableRelaunchAuthorityV4.IsCanonicalSession(clone.SessionId)) { reason = "SessionIdNotN"; return false; }
            if (!GuidN(clone.OperationId)) { reason = "OperationIdNotN"; return false; }
            if (!GuidN(clone.RequestCorrelationId)) { reason = "CorrelationNotN"; return false; }
            if (!RecoveryFailureReceipt.IsSha256(clone.RequestPayloadSha256)) { reason = "PayloadHashInvalid"; return false; }
            if (!Token(clone.SessionNonce, 128) || !Token(clone.FailureCode, 64) || !Token(clone.FailureFingerprint, 256) ||
                !Token(clone.DetailCode, 64) || !Token(clone.RunId, 128) || !Token(clone.RecoveryStage, 64) ||
                !Token(clone.RecoveryProgressToken, 128) || !Token(clone.RecoveryProcessSource, 64) || !Token(clone.DeviceOrChannelGroup, 128))
            { reason = "TokenInvalid"; return false; }
            if (clone.SidecarProcessId <= 0 || clone.SidecarProcessStartUtcTicks <= 0 || clone.ConnectionGeneration <= 0 ||
                clone.RecoveryAttemptGeneration <= 0 || clone.RunEpoch <= 0 || clone.MaximumProcessRelaunches <= 0 ||
                clone.MaximumProcessRelaunches > 1000000)
            { reason = "IdentityNotPositive"; return false; }
            var canonical = RecoveryFailureCanonical.Sha256(clone);
            frozen = new RecoveryFailureFrozenOperation(clone, canonical);
            return true;
        }

        private static void Normalize(RecoveryFailureOperation value)
        {
            value.OperationId = (value.OperationId ?? string.Empty).Trim().ToLowerInvariant();
            value.SessionId = (value.SessionId ?? string.Empty).Trim().ToLowerInvariant();
            value.SessionNonce = (value.SessionNonce ?? string.Empty).Trim();
            value.RequestCorrelationId = (value.RequestCorrelationId ?? string.Empty).Trim().ToLowerInvariant();
            value.RequestPayloadSha256 = (value.RequestPayloadSha256 ?? string.Empty).Trim().ToUpperInvariant();
            value.FailureCode = (value.FailureCode ?? string.Empty).Trim();
            value.FailureFingerprint = (value.FailureFingerprint ?? string.Empty).Trim();
            value.DetailCode = (value.DetailCode ?? string.Empty).Trim();
            value.RunId = (value.RunId ?? string.Empty).Trim(); value.RecoveryStage = (value.RecoveryStage ?? string.Empty).Trim();
            value.RecoveryProgressToken = (value.RecoveryProgressToken ?? string.Empty).Trim();
            value.RecoveryProcessSource = (value.RecoveryProcessSource ?? string.Empty).Trim();
            value.DeviceOrChannelGroup = (value.DeviceOrChannelGroup ?? string.Empty).Trim();
        }

        private static bool GuidN(string value) { Guid g; return !string.IsNullOrEmpty(value) && Guid.TryParseExact(value, "N", out g) && g.ToString("N") == value; }
        private static bool Token(string value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max && value.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c) && c != '\r' && c != '\n');
    }
}
