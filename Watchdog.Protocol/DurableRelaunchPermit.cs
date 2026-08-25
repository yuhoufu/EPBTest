using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Durable state of one process-relaunch permit.  A permit is never
    /// inferred from a PID or an attempt counter; its identity is the tuple
    /// SessionId + Generation + PermitId.
    /// </summary>
    public enum DurableRelaunchPermitState
    {
        None = 0,
        Approved = 1,
        LaunchIntent = 2,
        Started = 3,
        Attached = 4,
        Committed = 5,
        Failed = 6,
        Revoked = 7,
        Blocked = 8
    }

    public enum DurableRelaunchProcessObservation
    {
        Unknown = 0,
        Alive = 1,
        Dead = 2,
        IdentityMismatch = 3
    }

    public sealed class DurableRelaunchRequest
    {
        public string Fingerprint { get; set; }
        public string ProgressToken { get; set; }
        public string ProcessSource { get; set; }
        public string RunId { get; set; }
        public string RecoveryStage { get; set; }
        /// <summary>
        /// Launch nonce is transport evidence only; permit identity remains
        /// SessionId + Generation + PermitId.
        /// </summary>
        public string PermitNonce { get; set; }
        public int MaximumProcessRelaunches { get; set; }

        public DurableRelaunchRequest Clone() => new DurableRelaunchRequest
        {
            Fingerprint = Fingerprint,
            ProgressToken = ProgressToken,
            ProcessSource = ProcessSource,
            RunId = RunId,
            RecoveryStage = RecoveryStage,
            PermitNonce = PermitNonce,
            MaximumProcessRelaunches = MaximumProcessRelaunches
        };
    }

    public sealed class DurableRelaunchPermitIdentity : IEquatable<DurableRelaunchPermitIdentity>
    {
        public string SessionId { get; set; }
        public long Generation { get; set; }
        public string PermitId { get; set; }

        public DurableRelaunchPermitIdentity Clone() => new DurableRelaunchPermitIdentity
        {
            SessionId = SessionId,
            Generation = Generation,
            PermitId = PermitId
        };

        public bool Equals(DurableRelaunchPermitIdentity other)
        {
            return other != null &&
                   Generation == other.Generation &&
                   string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
                   string.Equals(PermitId, other.PermitId, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as DurableRelaunchPermitIdentity);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(SessionId ?? string.Empty);
                hash = (hash * 397) ^ Generation.GetHashCode();
                return (hash * 397) ^ StringComparer.Ordinal.GetHashCode(PermitId ?? string.Empty);
            }
        }

        public override string ToString() =>
            (SessionId ?? string.Empty) + ":" +
            Generation.ToString(CultureInfo.InvariantCulture) + ":" +
            (PermitId ?? string.Empty);
    }

    /// <summary>
    /// JSON-compatible schema4 record.  The coordinator only replaces this
    /// record after the synchronous durable store accepts it.
    /// </summary>
    public sealed class DurableRelaunchPermitRecord
    {
        public int SchemaVersion { get; set; } = WatchdogJournalPolicy.CurrentSchemaVersion;
        public string SessionId { get; set; }
        public long Generation { get; set; }
        public string PermitId { get; set; }
        public DurableRelaunchPermitState State { get; set; }
        public string Fingerprint { get; set; }
        public string ProgressToken { get; set; }
        public string ProcessSource { get; set; }
        public string RunId { get; set; }
        public string RecoveryStage { get; set; }
        public string PermitNonce { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int MaximumProcessRelaunches { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long RecoveryCommitGeneration { get; set; }
        public string LastFailureCode { get; set; }
        public string LastFailureReason { get; set; }
        // Schema4 failure-registration evidence.  These fields are additive;
        // old schema2/3 and early schema4 JSON deserialize with safe defaults.
        public string LastFailureOperationId { get; set; }
        public string LastFailureCorrelationId { get; set; }
        public string LastFailurePayloadSha256 { get; set; }
        public string LastFailureFingerprint { get; set; }
        public string LastFailureSessionNonce { get; set; }
        public int LastFailureProcessId { get; set; }
        public long LastFailureProcessStartUtcTicks { get; set; }
        public long LastFailureConnectionGeneration { get; set; }
        public long LastFailureAttemptGeneration { get; set; }
        public string LastFailureDisposition { get; set; }
        public long LastFailureDecisionSequence { get; set; }
        public long LastFailureDecisionUtcTicks { get; set; }
        public bool LastFailurePermanent { get; set; }
        public bool CircuitOpen { get; set; }
        public long LastTransitionUtcTicks { get; set; }

        public DurableRelaunchPermitIdentity Identity => new DurableRelaunchPermitIdentity
        {
            SessionId = SessionId,
            Generation = Generation,
            PermitId = PermitId
        };

        public DurableRelaunchPermitRecord Clone() => new DurableRelaunchPermitRecord
        {
            SchemaVersion = SchemaVersion,
            SessionId = SessionId,
            Generation = Generation,
            PermitId = PermitId,
            State = State,
            Fingerprint = Fingerprint,
            ProgressToken = ProgressToken,
            ProcessSource = ProcessSource,
            RunId = RunId,
            RecoveryStage = RecoveryStage,
            PermitNonce = PermitNonce,
            ConsecutiveFailures = ConsecutiveFailures,
            MaximumProcessRelaunches = MaximumProcessRelaunches,
            ProcessId = ProcessId,
            ProcessStartUtcTicks = ProcessStartUtcTicks,
            RecoveryCommitGeneration = RecoveryCommitGeneration,
            LastFailureCode = LastFailureCode,
            LastFailureReason = LastFailureReason,
            LastFailureOperationId = LastFailureOperationId,
            LastFailureCorrelationId = LastFailureCorrelationId,
            LastFailurePayloadSha256 = LastFailurePayloadSha256,
            LastFailureFingerprint = LastFailureFingerprint,
            LastFailureSessionNonce = LastFailureSessionNonce,
            LastFailureProcessId = LastFailureProcessId,
            LastFailureProcessStartUtcTicks = LastFailureProcessStartUtcTicks,
            LastFailureConnectionGeneration = LastFailureConnectionGeneration,
            LastFailureAttemptGeneration = LastFailureAttemptGeneration,
            LastFailureDisposition = LastFailureDisposition,
            LastFailureDecisionSequence = LastFailureDecisionSequence,
            LastFailureDecisionUtcTicks = LastFailureDecisionUtcTicks,
            LastFailurePermanent = LastFailurePermanent,
            CircuitOpen = CircuitOpen,
            LastTransitionUtcTicks = LastTransitionUtcTicks
        };
    }

    public enum DurableRelaunchBootstrapEvidenceKind
    {
        None = 0,
        VerifiedPristine = 1
    }

    /// <summary>
    /// Missing durable state is not implicitly pristine.  Callers must carry
    /// this typed evidence when a brand-new, verified installation is allowed
    /// to initialize its first None record.
    /// </summary>
    public sealed class DurableRelaunchBootstrapEvidence
    {
        public DurableRelaunchBootstrapEvidenceKind Kind { get; private set; }
        public string SessionId { get; private set; }
        public string EvidenceToken { get; private set; }

        public bool IsVerifiedPristine =>
            Kind == DurableRelaunchBootstrapEvidenceKind.VerifiedPristine &&
            !string.IsNullOrWhiteSpace(SessionId) &&
            !string.IsNullOrWhiteSpace(EvidenceToken);

        public static DurableRelaunchBootstrapEvidence None(string sessionId) =>
            new DurableRelaunchBootstrapEvidence
            {
                Kind = DurableRelaunchBootstrapEvidenceKind.None,
                SessionId = sessionId ?? string.Empty,
                EvidenceToken = string.Empty
            };

        public static DurableRelaunchBootstrapEvidence VerifiedPristine(
            string sessionId,
            string evidenceToken)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(evidenceToken))
                throw new ArgumentException("Verified pristine bootstrap requires a typed evidence token.");
            return new DurableRelaunchBootstrapEvidence
            {
                Kind = DurableRelaunchBootstrapEvidenceKind.VerifiedPristine,
                SessionId = sessionId,
                EvidenceToken = evidenceToken
            };
        }
    }

    public sealed class DurableRelaunchStoreLoadResult
    {
        public DurableRelaunchPermitRecord Record { get; internal set; }
        public bool Missing { get; internal set; }
        public bool Corrupt { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Initialized { get; internal set; }
        public string Reason { get; internal set; }
        public DurableRelaunchStoreLoadResult Clone() => new DurableRelaunchStoreLoadResult
        {
            Record = Record?.Clone(), Missing = Missing, Corrupt = Corrupt,
            Blocked = Blocked, Initialized = Initialized, Reason = Reason
        };
    }

    public sealed class DurableRelaunchFailureResult
    {
        public RecoveryFailureReceipt Receipt { get; internal set; }
        public DurableRelaunchPermitRecord Record { get; internal set; }
        public bool ActionAllowed { get; internal set; }
        public bool Blocked { get; internal set; }
        public bool Durable { get; internal set; }
        public string Reason { get; internal set; }
        public DurableRelaunchFailureResult Clone() => new DurableRelaunchFailureResult
        {
            Receipt = Receipt?.Clone(), Record = Record?.Clone(), ActionAllowed = ActionAllowed,
            Blocked = Blocked, Durable = Durable, Reason = Reason
        };
    }

    public sealed class DurableRelaunchResult
    {
        public bool Succeeded { get; set; }
        public bool Existing { get; set; }
        public bool ActionAllowed { get; set; }
        public bool Blocked { get; set; }
        public string Reason { get; set; }
        public DurableRelaunchPermitRecord Record { get; set; }
        public DurableAuthorityTransitionStatus TransitionStatus { get; set; }

        internal DurableRelaunchResult Clone()
        {
            return new DurableRelaunchResult
            {
                Succeeded = Succeeded,
                Existing = Existing,
                ActionAllowed = ActionAllowed,
                Blocked = Blocked,
                Reason = Reason,
                TransitionStatus = TransitionStatus,
                Record = Record?.Clone()
            };
        }
    }

    /// <summary>
    /// Critical persistence port.  TryCommit is synchronous by design: a
    /// caller may schedule/execute a process action only after it returns
    /// true.  Implementations must atomically replace their last-known JSON.
    /// </summary>
    public interface IDurableRelaunchStore
    {
        DurableRelaunchPermitRecord Load(string sessionId);
        bool TryCommit(DurableRelaunchPermitRecord record);
    }

    /// <summary>
    /// Optional schema4 failure-registration port.  The legacy two-method
    /// interface remains source-compatible with the existing Host adapter;
    /// the coordinator uses this port whenever durable failure authority is
    /// requested.
    /// </summary>
    public interface IDurableRelaunchStoreV4 : IDurableRelaunchStore
    {
        DurableRelaunchStoreLoadResult LoadV4(
            string sessionId,
            DurableRelaunchBootstrapEvidence bootstrapEvidence);

        bool TryCommitFailure(
            DurableRelaunchPermitRecord record,
            RecoveryFailureReceipt receipt,
            string operationId);

        bool TryCommitBlockedMarker(string sessionId, string reason);
    }

    /// <summary>
    /// Small deterministic store used by recovery tests and by embedders that
    /// already own a journal writer.  It is also a reference implementation of
    /// the synchronous store contract.
    /// </summary>
    public sealed class InMemoryDurableRelaunchStore : IDurableRelaunchStoreV4
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private readonly object _gate = new object();
        private string _lastSuccessfulJson;
        private RecoveryFailureReceipt _lastFailureReceipt;
        private string _lastFailureOperationId;
        private bool _blockedMarker;
        private string _blockedMarkerReason;
        private bool _failNextCommit;
        private bool _failCommitAndRollback;
        private bool _failMarkerCommit;

        public string LastSuccessfulJson
        {
            get { lock (_gate) return _lastSuccessfulJson; }
        }

        public void FailNextCommit()
        {
            lock (_gate) _failNextCommit = true;
        }

        public void FailCommitAndRollback()
        {
            lock (_gate) { _failCommitAndRollback = true; _failNextCommit = true; }
        }

        public void FailNextMarkerCommit()
        {
            lock (_gate) _failMarkerCommit = true;
        }

        public bool IsBlockedMarkerPresent
        {
            get { lock (_gate) return _blockedMarker; }
        }

        public RecoveryFailureReceipt LastFailureReceipt
        {
            get { lock (_gate) return _lastFailureReceipt?.Clone(); }
        }

        /// <summary>Test/embedding hook for restoring the last durable JSON.</summary>
        public void SeedJson(string json)
        {
            lock (_gate)
            {
                _lastSuccessfulJson = json;
                _blockedMarker = false;
                _blockedMarkerReason = null;
            }
        }

        public void SeedBlockedMarker(string reason = "DurableBlockedMarker")
        {
            lock (_gate)
            {
                _blockedMarker = true;
                _blockedMarkerReason = reason;
            }
        }

        public DurableRelaunchPermitRecord Load(string sessionId)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_lastSuccessfulJson)) return null;
                try
                {
                    return Json.Deserialize<DurableRelaunchPermitRecord>(_lastSuccessfulJson)?.Clone();
                }
                catch { return null; }
            }
        }

        public bool TryCommit(DurableRelaunchPermitRecord record)
        {
            if (record == null) return false;
            lock (_gate)
            {
                if (_failNextCommit)
                {
                    _failNextCommit = false;
                    return false;
                }
                _lastSuccessfulJson = Json.Serialize(record.Clone());
                return true;
            }
        }

        public DurableRelaunchStoreLoadResult LoadV4(
            string sessionId,
            DurableRelaunchBootstrapEvidence bootstrapEvidence)
        {
            lock (_gate)
            {
                if (_blockedMarker)
                    return new DurableRelaunchStoreLoadResult
                    {
                        Blocked = true, Reason = _blockedMarkerReason ?? "DurableBlockedMarker"
                    };
                if (!string.IsNullOrWhiteSpace(_lastSuccessfulJson))
                {
                    try
                    {
                        var validation = WatchdogJournalV4Validator.ValidateAndMigrate(
                            _lastSuccessfulJson, sessionId, bootstrapEvidence);
                        if (!validation.IsValid || validation.Record == null)
                        {
                            if (validation.Blocked && validation.Record != null)
                                _lastSuccessfulJson = Json.Serialize(validation.Record.Clone());
                            return new DurableRelaunchStoreLoadResult
                            {
                                Corrupt = true, Blocked = true,
                                Reason = validation.Reason ?? "InvalidSchema4Record"
                            };
                        }
                        if (validation.Migrated)
                            _lastSuccessfulJson = Json.Serialize(validation.Record.Clone());
                        return new DurableRelaunchStoreLoadResult
                        {
                            Record = validation.Record.Clone(),
                            Blocked = validation.Blocked ||
                                      validation.Record.State == DurableRelaunchPermitState.Blocked,
                            Reason = validation.Reason ?? "Loaded"
                        };
                    }
                    catch
                    {
                        return new DurableRelaunchStoreLoadResult
                        { Corrupt = true, Blocked = true, Reason = "UnreadableDurableState" };
                    }
                }
                if (bootstrapEvidence == null || !bootstrapEvidence.IsVerifiedPristine ||
                    !string.Equals(bootstrapEvidence.SessionId, sessionId, StringComparison.Ordinal))
                    return new DurableRelaunchStoreLoadResult
                    { Missing = true, Blocked = true, Reason = "MissingVerifiedBootstrap" };

                var pristine = WatchdogJournalV4Validator.CreateNone(sessionId);
                _lastSuccessfulJson = Json.Serialize(pristine.Clone());
                return new DurableRelaunchStoreLoadResult
                {
                    Record = pristine, Initialized = true, Reason = "VerifiedPristineBootstrap"
                };
            }
        }

        public bool TryCommitFailure(
            DurableRelaunchPermitRecord record,
            RecoveryFailureReceipt receipt,
            string operationId)
        {
            if (record == null || receipt == null || string.IsNullOrWhiteSpace(operationId)) return false;
            lock (_gate)
            {
                if (_failNextCommit)
                {
                    _failNextCommit = false;
                    return false;
                }
                _lastSuccessfulJson = Json.Serialize(record.Clone());
                _lastFailureReceipt = receipt.Clone();
                _lastFailureOperationId = operationId;
                if (_failCommitAndRollback)
                {
                    _failCommitAndRollback = false;
                    // Simulate a post-write rollback failure: the durable
                    // marker is the only authority that survives a restart.
                    _blockedMarker = true;
                    _blockedMarkerReason = "DurableCommitRollbackFailed";
                    return false;
                }
                return true;
            }
        }

        public bool TryCommitBlockedMarker(string sessionId, string reason)
        {
            lock (_gate)
            {
                if (_failMarkerCommit)
                {
                    _failMarkerCommit = false;
                    return false;
                }
                _blockedMarker = true;
                _blockedMarkerReason = string.IsNullOrWhiteSpace(reason)
                    ? "DurableBlockedMarker" : reason;
                return true;
            }
        }
    }
}
