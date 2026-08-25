using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog.Client
{
    [Flags]
    public enum WatchdogStopAllSourceFlags
    {
        None = 0,
        Pipe = 1,
        Durable = 2,
        Both = Pipe | Durable
    }

    /// <summary>
    /// Delivery state only. Offer/scope outcomes are represented by
    /// WatchdogStopAllOfferStatus and never share this enum.
    /// </summary>
    public enum WatchdogStopAllRegistrationState
    {
        Pending = 0,
        Admitting = 1,
        Posted = 2,
        Completed = 3,
        HandlerFaulted = 4,
        Canceled = 5,
        PostRejected = 6,
        SkippedDisposed = 7
    }

    public enum WatchdogStopAllOfferStatus
    {
        Pending = 0,
        Accepted = 1,
        Coalesced = 2,
        RequiredSafetySubscriberMissing = 3,
        Rejected = 4,
        ConflictFailClosed = 5,
        Closed = 6,
        Invalid = 7
    }

    /// <summary>
    /// Complete immutable identity for a StopAll scope. SessionLease is an
    /// identity field; ActivationId belongs to WatchdogStopAllScopeLease and
    /// is deliberately checked independently.
    /// </summary>
    public sealed class WatchdogStopAllExactScopeIdentity : IEquatable<WatchdogStopAllExactScopeIdentity>
    {
        public string RunId { get; }
        public long RunEpoch { get; }
        public string SessionId { get; }
        public long SessionGeneration { get; }
        public long SessionLease { get; }
        public long AuthorityGeneration { get; }
        public long ScopeGeneration { get; }
        public string OwnerKind { get; }
        public string ChannelGroup { get; }
        public string Channel { get; }
        public string TargetPhase { get; }
        public string ResourceScope { get; }
        public Guid ScopeId { get; }
        public long ProcessId { get; }
        public long ProcessStartTicks { get; }
        public long AttachEpoch { get; }
        public Guid IncidentId { get; }
        public long Pid => ProcessId;
        public long StartTicks => ProcessStartTicks;

        public WatchdogStopAllExactScopeIdentity(
            string runId,
            long runEpoch,
            string sessionId,
            long sessionGeneration,
            long sessionLease,
            long authorityGeneration,
            long scopeGeneration,
            string ownerKind,
            string channelGroup,
            string channel,
            string targetPhase,
            string resourceScope,
            Guid scopeId,
            long processId,
            long processStartTicks,
            long attachEpoch,
            Guid incidentId)
        {
            RunId = Require(runId, nameof(runId));
            SessionId = Require(sessionId, nameof(sessionId));
            OwnerKind = Require(ownerKind, nameof(ownerKind));
            ChannelGroup = Require(channelGroup, nameof(channelGroup));
            Channel = Require(channel, nameof(channel));
            TargetPhase = Require(targetPhase, nameof(targetPhase));
            ResourceScope = Require(resourceScope, nameof(resourceScope));
            RequirePositive(runEpoch, nameof(runEpoch));
            RequirePositive(sessionGeneration, nameof(sessionGeneration));
            RequirePositive(sessionLease, nameof(sessionLease));
            RequirePositive(authorityGeneration, nameof(authorityGeneration));
            RequirePositive(scopeGeneration, nameof(scopeGeneration));
            RequirePositive(processId, nameof(processId));
            RequirePositive(processStartTicks, nameof(processStartTicks));
            RequirePositive(attachEpoch, nameof(attachEpoch));
            if (scopeId == Guid.Empty) throw new ArgumentException("ScopeId不能为空。", nameof(scopeId));
            if (incidentId == Guid.Empty) throw new ArgumentException("IncidentId不能为空。", nameof(incidentId));
            RunEpoch = runEpoch;
            SessionGeneration = sessionGeneration;
            SessionLease = sessionLease;
            AuthorityGeneration = authorityGeneration;
            ScopeGeneration = scopeGeneration;
            ScopeId = scopeId;
            ProcessId = processId;
            ProcessStartTicks = processStartTicks;
            AttachEpoch = attachEpoch;
            IncidentId = incidentId;
        }

        public string ExactKey => string.Join("|", new[]
        {
            RunId, Number(RunEpoch), SessionId, Number(SessionGeneration), Number(SessionLease),
            Number(AuthorityGeneration), Number(ScopeGeneration), OwnerKind, ChannelGroup,
            Channel, TargetPhase, ResourceScope, ScopeId.ToString("N"), Number(ProcessId),
            Number(ProcessStartTicks), Number(AttachEpoch), IncidentId.ToString("N")
        });

        public bool Equals(WatchdogStopAllExactScopeIdentity other) =>
            other != null && string.Equals(ExactKey, other.ExactKey, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as WatchdogStopAllExactScopeIdentity);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ExactKey);
        public override string ToString() => ExactKey;

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("不能为空。", name);
            return value.Trim();
        }

        private static void RequirePositive(long value, string name)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(name);
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class WatchdogStopAllScopeLease
    {
        public WatchdogStopAllExactScopeIdentity Scope { get; }
        public long ActivationId { get; }

        internal WatchdogStopAllScopeLease(WatchdogStopAllExactScopeIdentity scope, long activationId)
        {
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            if (activationId <= 0) throw new ArgumentOutOfRangeException(nameof(activationId));
            ActivationId = activationId;
        }
    }

    /// <summary>
    /// Immutable source evidence. It has no callback or target. A canonical
    /// envelope can carry both sources after Pipe/Durable merge.
    /// </summary>
    public sealed class WatchdogStopAllOfferEnvelope
    {
        public WatchdogStopAllExactScopeIdentity Scope { get; }
        public WatchdogStopAllScopeLease Lease { get; }
        public string CorrelationId { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public WatchdogStopAllSourceFlags SourceFlags { get; }
        public long PipeSequence { get; }
        public long DurableVersion { get; }
        public string RequestId { get; }

        public WatchdogStopAllOfferEnvelope(
            WatchdogStopAllExactScopeIdentity scope,
            WatchdogStopAllScopeLease lease,
            string correlationId,
            string reasonCode,
            string detail,
            WatchdogStopAllSourceFlags sourceFlags,
            long pipeSequence,
            long durableVersion,
            string requestId = null)
        {
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            Lease = lease ?? throw new ArgumentNullException(nameof(lease));
            CorrelationId = Require(correlationId, nameof(correlationId));
            ReasonCode = Require(reasonCode, nameof(reasonCode));
            Detail = detail ?? string.Empty;
            if (sourceFlags == WatchdogStopAllSourceFlags.None ||
                (sourceFlags & ~WatchdogStopAllSourceFlags.Both) != 0)
                throw new ArgumentOutOfRangeException(nameof(sourceFlags));
            if (pipeSequence < 0 || durableVersion < 0)
                throw new ArgumentOutOfRangeException(nameof(pipeSequence));
            if ((sourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0 && pipeSequence <= 0)
                throw new ArgumentOutOfRangeException(nameof(pipeSequence));
            if ((sourceFlags & WatchdogStopAllSourceFlags.Durable) != 0 && durableVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(durableVersion));
            if ((sourceFlags & WatchdogStopAllSourceFlags.Pipe) == 0 && pipeSequence != 0)
                throw new ArgumentException("无Pipe source时PipeSequence必须为0。", nameof(pipeSequence));
            if ((sourceFlags & WatchdogStopAllSourceFlags.Durable) == 0 && durableVersion != 0)
                throw new ArgumentException("无Durable source时DurableVersion必须为0。", nameof(durableVersion));
            SourceFlags = sourceFlags;
            PipeSequence = pipeSequence;
            DurableVersion = durableVersion;
            RequestId = requestId ?? string.Empty;
        }

        public bool HasPipe => (SourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0;
        public bool HasDurable => (SourceFlags & WatchdogStopAllSourceFlags.Durable) != 0;

        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("不能为空。", name);
            return value.Trim();
        }
    }

    public sealed class WatchdogStopAllCanonicalRequestState
    {
        public string CorrelationId { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string RequestId { get; }
        public WatchdogStopAllExactScopeIdentity Scope { get; }
        public WatchdogStopAllScopeLease Lease { get; }
        public WatchdogStopAllSourceFlags SourceFlags { get; }
        public long PipeSequence { get; }
        public long DurableVersion { get; }
        public long CanonicalSequence { get; }
        public long RequestRevision { get; }
        public WatchdogStopAllOfferEnvelope Envelope { get; }

        internal WatchdogStopAllCanonicalRequestState(
            string correlationId,
            string reasonCode,
            string detail,
            string requestId,
            WatchdogStopAllExactScopeIdentity scope,
            WatchdogStopAllScopeLease lease,
            WatchdogStopAllSourceFlags sourceFlags,
            long pipeSequence,
            long durableVersion,
            long canonicalSequence,
            long requestRevision)
        {
            CorrelationId = correlationId ?? string.Empty;
            ReasonCode = reasonCode ?? string.Empty;
            Detail = detail ?? string.Empty;
            RequestId = requestId ?? string.Empty;
            Scope = scope;
            Lease = lease;
            SourceFlags = sourceFlags;
            PipeSequence = pipeSequence;
            DurableVersion = durableVersion;
            CanonicalSequence = canonicalSequence;
            RequestRevision = requestRevision;
            Envelope = new WatchdogStopAllOfferEnvelope(
                scope, lease, CorrelationId, ReasonCode, Detail, sourceFlags,
                pipeSequence, durableVersion, RequestId);
        }
    }

    public sealed class WatchdogStopAllRegistrationLease : IDisposable
    {
        private Action _dispose;
        private int _disposed;

        internal WatchdogStopAllRegistrationLease(
            WatchdogStopAllScopeLease scopeLease,
            string subscriberId,
            long registrationId,
            bool accepted,
            string rejectionReason,
            Action dispose)
        {
            ScopeLease = scopeLease;
            SubscriberId = subscriberId ?? string.Empty;
            RegistrationId = registrationId;
            ActivationId = scopeLease == null ? 0 : scopeLease.ActivationId;
            Accepted = accepted;
            RejectionReason = rejectionReason ?? string.Empty;
            _dispose = dispose;
        }

        public WatchdogStopAllScopeLease ScopeLease { get; }
        public string SubscriberId { get; }
        public long RegistrationId { get; }
        public long ActivationId { get; }
        public bool Accepted { get; }
        public string RejectionReason { get; }
        public bool IsActive => Accepted && Volatile.Read(ref _disposed) == 0;

        /// <summary>
        /// Deactivates this lease from the owning coordinator without calling
        /// back into the coordinator.  Dispose() cannot be called while the
        /// coordinator gate is held because its callback re-enters that gate.
        /// </summary>
        internal void DeactivateFromCoordinator()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Interlocked.Exchange(ref _dispose, null);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var dispose = Interlocked.Exchange(ref _dispose, null);
            if (dispose != null) dispose();
            GC.SuppressFinalize(this);
        }
    }

    public sealed class WatchdogStopAllDeliveryResult
    {
        public long RegistrationId { get; }
        public string SubscriberId { get; }
        public long ActivationId { get; }
        public long DeliveryGeneration { get; }
        public long AdmissionSequence { get; }
        public long RequestCanonicalSequence { get; }
        public long RequestRevision { get; }
        public long DispatcherCanonicalSequence { get; }
        public WatchdogRuntimeCallbackIdentity DispatcherIdentity { get; }
        public WatchdogRuntimeCallbackIdentity DispatcherOfferIdentity => DispatcherIdentity;
        public string DispatcherIdentityExactKey => DispatcherIdentity == null ? string.Empty : DispatcherIdentity.ExactKey;
        public string DispatcherOfferIdentityExactKey => DispatcherIdentityExactKey;
        // Compatibility alias: callers that only know the old field still
        // observe the request-side canonical sequence.
        public long CanonicalSequence { get; }
        public WatchdogStopAllRegistrationState DeliveryState { get; }
        public string FailureReason { get; }
        public bool CompletionTerminal { get; }
        public Task<WatchdogCallbackCompletionReceipt> Completion { get; }

        internal WatchdogStopAllDeliveryResult(
            long registrationId,
            string subscriberId,
            long activationId,
            long deliveryGeneration,
            long admissionSequence,
            long requestCanonicalSequence,
            long requestRevision,
            long dispatcherCanonicalSequence,
            WatchdogRuntimeCallbackIdentity dispatcherIdentity,
            WatchdogStopAllRegistrationState deliveryState,
            string failureReason,
            bool completionTerminal,
            Task<WatchdogCallbackCompletionReceipt> completion)
        {
            RegistrationId = registrationId;
            SubscriberId = subscriberId ?? string.Empty;
            ActivationId = activationId;
            DeliveryGeneration = deliveryGeneration;
            AdmissionSequence = admissionSequence;
            RequestCanonicalSequence = requestCanonicalSequence;
            RequestRevision = requestRevision;
            DispatcherCanonicalSequence = dispatcherCanonicalSequence;
            DispatcherIdentity = dispatcherIdentity;
            CanonicalSequence = requestCanonicalSequence;
            DeliveryState = deliveryState;
            FailureReason = failureReason ?? string.Empty;
            CompletionTerminal = completionTerminal;
            Completion = completion ?? Task.FromResult<WatchdogCallbackCompletionReceipt>(null);
        }
    }

    public sealed class WatchdogStopAllOfferResult
    {
        public WatchdogStopAllOfferStatus Status { get; }
        public string CorrelationId { get; }
        public string Reason { get; }
        public WatchdogStopAllCanonicalRequestState Canonical { get; }
        public IReadOnlyList<WatchdogStopAllDeliveryResult> Deliveries { get; }
        public long CanonicalSequence => Canonical == null ? 0 : Canonical.CanonicalSequence;
        public long RegistrationId => Deliveries.Count == 0 ? 0 : Deliveries[0].RegistrationId;
        public Task<WatchdogCallbackCompletionReceipt> Completion =>
            Deliveries.Count == 0 ? Task.FromResult<WatchdogCallbackCompletionReceipt>(null) : Deliveries[0].Completion;
        public bool Accepted => Status == WatchdogStopAllOfferStatus.Pending ||
                                 Status == WatchdogStopAllOfferStatus.Accepted ||
                                 Status == WatchdogStopAllOfferStatus.Coalesced ||
                                 Status == WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing;

        internal WatchdogStopAllOfferResult(
            WatchdogStopAllOfferStatus status,
            string correlationId,
            string reason,
            WatchdogStopAllCanonicalRequestState canonical,
            IReadOnlyList<WatchdogStopAllDeliveryResult> deliveries)
        {
            Status = status;
            CorrelationId = correlationId ?? string.Empty;
            Reason = reason ?? string.Empty;
            Canonical = canonical;
            Deliveries = deliveries ?? new WatchdogStopAllDeliveryResult[0];
        }
    }

    public sealed class WatchdogStopAllRegistrationSnapshot
    {
        public long RegistrationId { get; }
        public string SubscriberId { get; }
        public string CorrelationId { get; }
        public long ActivationId { get; }
        public long DeliveryGeneration { get; }
        public long AdmissionSequence { get; }
        public long RequestCanonicalSequence { get; }
        public long RequestRevision { get; }
        public long DispatcherCanonicalSequence { get; }
        public WatchdogRuntimeCallbackIdentity DispatcherIdentity { get; }
        public WatchdogRuntimeCallbackIdentity DispatcherOfferIdentity => DispatcherIdentity;
        public string DispatcherIdentityExactKey => DispatcherIdentity == null ? string.Empty : DispatcherIdentity.ExactKey;
        public string DispatcherOfferIdentityExactKey => DispatcherIdentityExactKey;
        // Compatibility alias for the pre-split delivery snapshot.
        public long CanonicalSequence { get; }
        public WatchdogStopAllRegistrationState DeliveryState { get; }
        public string FailureReason { get; }
        public bool CompletionTerminal { get; }

        internal WatchdogStopAllRegistrationSnapshot(
            long registrationId,
            string subscriberId,
            string correlationId,
            long activationId,
            long deliveryGeneration,
            long admissionSequence,
            long requestCanonicalSequence,
            long requestRevision,
            long dispatcherCanonicalSequence,
            WatchdogRuntimeCallbackIdentity dispatcherIdentity,
            WatchdogStopAllRegistrationState deliveryState,
            string failureReason,
            bool completionTerminal)
        {
            RegistrationId = registrationId;
            SubscriberId = subscriberId ?? string.Empty;
            CorrelationId = correlationId ?? string.Empty;
            ActivationId = activationId;
            DeliveryGeneration = deliveryGeneration;
            AdmissionSequence = admissionSequence;
            RequestCanonicalSequence = requestCanonicalSequence;
            RequestRevision = requestRevision;
            DispatcherCanonicalSequence = dispatcherCanonicalSequence;
            DispatcherIdentity = dispatcherIdentity;
            CanonicalSequence = requestCanonicalSequence;
            DeliveryState = deliveryState;
            FailureReason = failureReason ?? string.Empty;
            CompletionTerminal = completionTerminal;
        }
    }

    public sealed class WatchdogStopAllSubscriberSnapshot
    {
        public long RegistrationId { get; }
        public string SubscriberId { get; }
        public long ActivationId { get; }
        public bool Active { get; }

        internal WatchdogStopAllSubscriberSnapshot(long registrationId, string subscriberId, long activationId, bool active)
        {
            RegistrationId = registrationId;
            SubscriberId = subscriberId ?? string.Empty;
            ActivationId = activationId;
            Active = active;
        }
    }

    public sealed class WatchdogStopAllCoordinatorSnapshot
    {
        public WatchdogStopAllExactScopeIdentity Scope { get; }
        public WatchdogStopAllScopeLease Lease { get; }
        public long Revision { get; }
        public bool Active { get; }
        public bool Closing { get; }
        public bool Sealed { get; }
        public bool DrainStarted { get; }
        public bool Disposed { get; }
        public bool FailClosed { get; }
        public string FailureReason { get; }
        public int ActiveSubscriberCount { get; }
        public bool PumpActive { get; }
        public long PumpGeneration { get; }
        public IReadOnlyList<WatchdogStopAllSubscriberSnapshot> Subscribers { get; }
        public IReadOnlyList<WatchdogStopAllCanonicalRequestState> CanonicalRequests { get; }
        public IReadOnlyList<WatchdogStopAllRegistrationSnapshot> Registrations { get; }

        internal WatchdogStopAllCoordinatorSnapshot(
            WatchdogStopAllExactScopeIdentity scope,
            WatchdogStopAllScopeLease lease,
            long revision,
            bool active,
            bool closing,
            bool sealedState,
            bool drainStarted,
            bool disposed,
            bool failClosed,
            string failureReason,
            int activeSubscriberCount,
            bool pumpActive,
            long pumpGeneration,
            IReadOnlyList<WatchdogStopAllSubscriberSnapshot> subscribers,
            IReadOnlyList<WatchdogStopAllCanonicalRequestState> canonicalRequests,
            IReadOnlyList<WatchdogStopAllRegistrationSnapshot> registrations)
        {
            Scope = scope;
            Lease = lease;
            Revision = revision;
            Active = active;
            Closing = closing;
            Sealed = sealedState;
            DrainStarted = drainStarted;
            Disposed = disposed;
            FailClosed = failClosed;
            FailureReason = failureReason ?? string.Empty;
            ActiveSubscriberCount = activeSubscriberCount;
            PumpActive = pumpActive;
            PumpGeneration = pumpGeneration;
            Subscribers = subscribers ?? new WatchdogStopAllSubscriberSnapshot[0];
            CanonicalRequests = canonicalRequests ?? new WatchdogStopAllCanonicalRequestState[0];
            Registrations = registrations ?? new WatchdogStopAllRegistrationSnapshot[0];
        }
    }

    public sealed class WatchdogStopAllCloseReceipt
    {
        public bool Accepted { get; }
        public bool Closing { get; }
        public long Revision { get; }
        public string Reason { get; }

        internal WatchdogStopAllCloseReceipt(bool accepted, bool closing, long revision, string reason)
        {
            Accepted = accepted;
            Closing = closing;
            Revision = revision;
            Reason = reason ?? string.Empty;
        }
    }

    public sealed class WatchdogStopAllDrainReceipt
    {
        public bool IsTerminal { get; }
        public bool TimedOut { get; }
        public bool AllRegistrationsTerminal { get; }
        public bool AllResourcesReleased { get; }
        public bool Sealed { get; }
        public bool FailClosed { get; }
        public string Reason { get; }
        public WatchdogRuntimeCallbackDrainReceipt DispatcherReceipt { get; }
        public WatchdogStopAllCoordinatorSnapshot Snapshot { get; }

        internal WatchdogStopAllDrainReceipt(
            bool terminal,
            bool timedOut,
            bool registrationsTerminal,
            bool resourcesReleased,
            bool sealedState,
            bool failClosed,
            string reason,
            WatchdogRuntimeCallbackDrainReceipt dispatcherReceipt,
            WatchdogStopAllCoordinatorSnapshot snapshot)
        {
            IsTerminal = terminal;
            TimedOut = timedOut;
            AllRegistrationsTerminal = registrationsTerminal;
            AllResourcesReleased = resourcesReleased;
            Sealed = sealedState;
            FailClosed = failClosed;
            Reason = reason ?? string.Empty;
            DispatcherReceipt = dispatcherReceipt;
            Snapshot = snapshot;
        }
    }

    /// <summary>
    /// Production StopAll coordinator. CanonicalRequestState is kept
    /// separately from the registration-id delivery dictionary. External
    /// dispatcher calls and subscriber handlers are always made outside _gate.
    /// </summary>
    public sealed class WatchdogStopAllOfferCoordinator : IDisposable
    {
        private sealed class SubscriberRecord
        {
            internal readonly long RegistrationId;
            internal readonly string SubscriberId;
            internal readonly Func<WatchdogStopAllOfferEnvelope, Task> Handler;
            internal WatchdogStopAllRegistrationLease Lease;
            internal bool Active;

            internal SubscriberRecord(long registrationId, string subscriberId, Func<WatchdogStopAllOfferEnvelope, Task> handler)
            {
                RegistrationId = registrationId;
                SubscriberId = subscriberId;
                Handler = handler;
                Active = true;
            }
        }

        private sealed class CanonicalRequestState
        {
            internal readonly string CorrelationId;
            internal readonly string ReasonCode;
            internal string RequestId;
            internal readonly WatchdogStopAllExactScopeIdentity Scope;
            internal readonly WatchdogStopAllScopeLease Lease;
            internal string Detail;
            internal WatchdogStopAllSourceFlags SourceFlags;
            internal long PipeSequence;
            internal long DurableVersion;
            internal long CanonicalSequence;
            // Stable identity of this request revision.  CanonicalSequence
            // may advance when the second source merges; RequestRevision
            // does not and is checked independently by delivery completion.
            internal readonly long RequestRevision;
            internal WatchdogStopAllOfferEnvelope Envelope;
            internal readonly HashSet<long> DeliveryIds = new HashSet<long>();

            internal CanonicalRequestState(WatchdogStopAllOfferEnvelope envelope, long canonicalSequence)
            {
                CorrelationId = envelope.CorrelationId;
                ReasonCode = envelope.ReasonCode;
                RequestId = envelope.RequestId;
                Scope = envelope.Scope;
                Lease = envelope.Lease;
                Detail = envelope.Detail;
                SourceFlags = envelope.SourceFlags;
                PipeSequence = envelope.PipeSequence;
                DurableVersion = envelope.DurableVersion;
                CanonicalSequence = canonicalSequence;
                RequestRevision = canonicalSequence;
                Envelope = envelope;
            }

            internal bool Merge(WatchdogStopAllOfferEnvelope envelope, long canonicalSequence)
            {
                var changed = false;
                if ((envelope.SourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0 && envelope.PipeSequence > PipeSequence)
                {
                    PipeSequence = envelope.PipeSequence;
                    changed = true;
                }
                if ((envelope.SourceFlags & WatchdogStopAllSourceFlags.Durable) != 0 && envelope.DurableVersion > DurableVersion)
                {
                    DurableVersion = envelope.DurableVersion;
                    changed = true;
                }
                var mergedFlags = SourceFlags | envelope.SourceFlags;
                if (mergedFlags != SourceFlags)
                {
                    SourceFlags = mergedFlags;
                    changed = true;
                }
                if (string.IsNullOrEmpty(RequestId) && !string.IsNullOrEmpty(envelope.RequestId))
                {
                    RequestId = envelope.RequestId;
                    changed = true;
                }
                if (changed)
                {
                    Detail = envelope.Detail;
                    CanonicalSequence = canonicalSequence;
                    Envelope = new WatchdogStopAllOfferEnvelope(
                        Scope, Lease, CorrelationId, ReasonCode, Detail, SourceFlags,
                        PipeSequence, DurableVersion, RequestId);
                }
                return changed;
            }

            internal WatchdogStopAllCanonicalRequestState Snapshot()
            {
                return new WatchdogStopAllCanonicalRequestState(
                    CorrelationId, ReasonCode, Detail, RequestId, Scope, Lease,
                    SourceFlags, PipeSequence, DurableVersion, CanonicalSequence, RequestRevision);
            }
        }

        private sealed class RegistrationRecord
        {
            internal readonly long RegistrationId;
            internal readonly SubscriberRecord Subscriber;
            internal readonly CanonicalRequestState Canonical;
            internal readonly long ActivationId;
            internal readonly TaskCompletionSource<WatchdogCallbackCompletionReceipt> Completion =
                new TaskCompletionSource<WatchdogCallbackCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal long DeliveryGeneration;
            internal long AdmissionSequence;
            internal long RequestCanonicalSequence;
            internal long RequestRevision;
            internal long DispatcherCanonicalSequence;
            internal WatchdogRuntimeCallbackIdentity DispatcherIdentity;
            internal WatchdogStopAllRegistrationState DeliveryState = WatchdogStopAllRegistrationState.Pending;
            internal string FailureReason = string.Empty;
            internal bool PumpClaimed;
            internal bool InFlight;
            internal bool Terminal;

            internal RegistrationRecord(long registrationId, SubscriberRecord subscriber, CanonicalRequestState canonical)
            {
                RegistrationId = registrationId;
                Subscriber = subscriber;
                Canonical = canonical;
                ActivationId = subscriber.Lease.ActivationId;
                RequestCanonicalSequence = canonical.CanonicalSequence;
                RequestRevision = canonical.RequestRevision;
            }
        }

        private sealed class DispatchPlan
        {
            internal readonly RegistrationRecord Record;
            internal readonly SubscriberRecord Subscriber;
            internal readonly CanonicalRequestState Canonical;
            internal readonly long ActivationId;
            internal readonly long DeliveryGeneration;
            internal readonly long RequestCanonicalSequence;
            internal readonly long RequestRevision;
            internal readonly WatchdogRuntimeCallbackIdentity DispatcherIdentity;

            internal DispatchPlan(RegistrationRecord record)
            {
                Record = record;
                Subscriber = record.Subscriber;
                Canonical = record.Canonical;
                ActivationId = record.ActivationId;
                DeliveryGeneration = record.DeliveryGeneration;
                RequestCanonicalSequence = record.RequestCanonicalSequence;
                RequestRevision = record.RequestRevision;
                DispatcherIdentity = new WatchdogRuntimeCallbackIdentity(
                    record.Canonical.Scope.SessionId,
                    record.Canonical.Scope.SessionGeneration,
                    record.Canonical.Scope.SessionLease,
                    record.Canonical.Scope.AuthorityGeneration,
                    record.Canonical.Scope.ScopeGeneration,
                    record.Canonical.Scope.ScopeId);
            }
        }

        private readonly object _gate = new object();
        private readonly WatchdogRuntimeCallbackDispatch _dispatcher;
        private readonly Dictionary<long, RegistrationRecord> _registrations = new Dictionary<long, RegistrationRecord>();
        private readonly Dictionary<string, CanonicalRequestState> _canonicalRequests = new Dictionary<string, CanonicalRequestState>(StringComparer.Ordinal);
        private readonly Dictionary<string, SubscriberRecord> _subscribers = new Dictionary<string, SubscriberRecord>(StringComparer.Ordinal);
        private WatchdogStopAllScopeLease _lease;
        private long _activationSequence;
        private long _subscriberSequence;
        private long _registrationSequence;
        private long _canonicalSequence;
        private long _revision;
        private string _pipeCorrelation;
        private string _durableCorrelation;
        private bool _active;
        private bool _closing;
        private bool _sealed;
        private bool _drainStarted;
        private bool _disposed;
        private bool _terminal;
        private bool _failClosed;
        private string _failureReason = string.Empty;
        private Task _pumpTask;
        private long _pumpGeneration;

        public WatchdogStopAllOfferCoordinator(WatchdogRuntimeCallbackDispatch dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _dispatcher.SpaceAvailable += OnSpaceAvailable;
        }

        public WatchdogStopAllScopeLease Activate(WatchdogStopAllExactScopeIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            lock (_gate)
            {
                if (_active) throw new InvalidOperationException("StopAll scope已经激活。");
                if (_activationSequence == long.MaxValue) throw new InvalidOperationException("ActivationId耗尽。");
                _activationSequence++;
                _lease = new WatchdogStopAllScopeLease(identity, _activationSequence);
                _active = true;
                _revision++;
                return _lease;
            }
        }

        public WatchdogStopAllRegistrationLease RegisterSafety(
            WatchdogStopAllScopeLease scopeLease,
            string subscriberId,
            Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            if (scopeLease == null) throw new ArgumentNullException(nameof(scopeLease));
            if (string.IsNullOrWhiteSpace(subscriberId)) throw new ArgumentException("SubscriberId不能为空。", nameof(subscriberId));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            subscriberId = subscriberId.Trim();
            SubscriberRecord subscriber;
            WatchdogStopAllRegistrationLease registrationLease;
            lock (_gate)
            {
                var validation = ValidateLeaseLocked(scopeLease);
                if (validation != null) return RejectedSubscriberLease(scopeLease, subscriberId, validation);
                if (_disposed) return RejectedSubscriberLease(scopeLease, subscriberId, "CoordinatorDisposed");
                if (_sealed) return RejectedSubscriberLease(scopeLease, subscriberId, "RegistrationsSealed");
                if (_failClosed) return RejectedSubscriberLease(scopeLease, subscriberId, _failureReason);
                if (_subscribers.ContainsKey(subscriberId)) return RejectedSubscriberLease(scopeLease, subscriberId, "SubscriberIdAlreadyRegistered");
                if (_subscriberSequence == long.MaxValue) throw new InvalidOperationException("Subscriber registration exhausted.");
                _subscriberSequence++;
                subscriber = new SubscriberRecord(_subscriberSequence, subscriberId, handler);
                registrationLease = new WatchdogStopAllRegistrationLease(
                    scopeLease, subscriberId, subscriber.RegistrationId, true, string.Empty,
                    () => DisposeSubscriber(scopeLease, subscriberId, subscriber.RegistrationId));
                subscriber.Lease = registrationLease;
                _subscribers.Add(subscriberId, subscriber);
                foreach (var canonical in _canonicalRequests.Values.ToArray()) EnsureDeliveryLocked(subscriber, canonical);
                _revision++;
            }
            RequestPump();
            return registrationLease;
        }

        public WatchdogStopAllOfferResult OfferPipe(WatchdogStopAllScopeLease scopeLease, WatchdogStopAllOfferEnvelope envelope) =>
            Offer(scopeLease, envelope, WatchdogStopAllSourceFlags.Pipe);

        public WatchdogStopAllOfferResult OfferDurable(WatchdogStopAllScopeLease scopeLease, WatchdogStopAllOfferEnvelope envelope) =>
            Offer(scopeLease, envelope, WatchdogStopAllSourceFlags.Durable);

        public WatchdogStopAllCoordinatorSnapshot Capture(WatchdogStopAllScopeLease scopeLease)
        {
            lock (_gate)
            {
                var validation = ValidateLeaseLocked(scopeLease);
                if (validation != null) throw new InvalidOperationException(validation);
                return CaptureLocked();
            }
        }

        /// <summary>Close rejects new offers but does not close Pending deliveries.</summary>
        public WatchdogStopAllCloseReceipt BeginClose(WatchdogStopAllScopeLease scopeLease)
        {
            lock (_gate)
            {
                var validation = ValidateLeaseLocked(scopeLease);
                if (validation != null) return new WatchdogStopAllCloseReceipt(false, _closing, _revision, validation);
                if (_disposed) return new WatchdogStopAllCloseReceipt(false, _closing, _revision, "CoordinatorDisposed");
                if (_closing) return new WatchdogStopAllCloseReceipt(true, true, _revision, "AlreadyClosing");
                _closing = true;
                _revision++;
                return new WatchdogStopAllCloseReceipt(true, true, _revision, string.Empty);
            }
        }

        public WatchdogStopAllDrainReceipt Drain(WatchdogStopAllScopeLease scopeLease, TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            lock (_gate)
            {
                var validation = ValidateLeaseLocked(scopeLease);
                if (validation != null)
                    return new WatchdogStopAllDrainReceipt(false, false, false, false, _sealed, _failClosed, validation, null, null);
                if (!_closing)
                    return new WatchdogStopAllDrainReceipt(false, false, false, false, false, _failClosed, "BeginCloseRequired", null, CaptureLocked());
                if (!_sealed)
                {
                    _sealed = true;
                    _drainStarted = true;
                    _revision++;
                    EnsureAllDeliveriesLocked();
                    if (ActiveSubscriberCountLocked() == 0 && _canonicalRequests.Count != 0)
                        EnterFailClosedLocked("RequiredSafetySubscriberMissing", false);
                }
                else _drainStarted = true;
            }

            lock (_gate)
            {
                if (_failClosed && _canonicalRequests.Count != 0 && ActiveSubscriberCountLocked() == 0)
                    return BuildDrainReceipt(false, true, _registrations.Values.All(item => item.Terminal),
                        "RequiredSafetySubscriberMissing", null);
            }

            RequestPump();
            var deadline = DateTime.UtcNow + timeout;
            WatchdogRuntimeCallbackDrainReceipt dispatcherReceipt = null;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                bool coordinatorPending;
                bool allRegistrationsTerminal;
                Task pump;
                lock (_gate)
                {
                    coordinatorPending = _registrations.Values.Any(item => !item.Terminal &&
                        (item.DeliveryState == WatchdogStopAllRegistrationState.Pending || item.DeliveryState == WatchdogStopAllRegistrationState.Admitting));
                    allRegistrationsTerminal = _registrations.Values.All(item => item.Terminal);
                    pump = _pumpTask;
                }
                if (pump != null && !pump.IsCompleted && remaining > TimeSpan.Zero) WaitTask(pump, remaining);
                lock (_gate)
                {
                    coordinatorPending = _registrations.Values.Any(item => !item.Terminal &&
                        (item.DeliveryState == WatchdogStopAllRegistrationState.Pending || item.DeliveryState == WatchdogStopAllRegistrationState.Admitting));
                    allRegistrationsTerminal = _registrations.Values.All(item => item.Terminal);
                    pump = _pumpTask;
                }

                // A timeout must not close the shared dispatcher while there
                // are coordinator Pending deliveries that still need the
                // SpaceAvailable pump on a later Drain call.
                if (coordinatorPending || (pump != null && !pump.IsCompleted))
                {
                    if (remaining <= TimeSpan.Zero)
                        return BuildDrainReceipt(false, true, allRegistrationsTerminal, "CoordinatorPending", dispatcherReceipt);
                    Thread.Sleep(Math.Min(10, Math.Max(1, (int)remaining.TotalMilliseconds)));
                    RequestPump();
                    continue;
                }
                if (allRegistrationsTerminal)
                {
                    if (remaining <= TimeSpan.Zero)
                        return BuildDrainReceipt(false, true, true, "DispatcherDrainPending", dispatcherReceipt);
                    dispatcherReceipt = _dispatcher.CompleteAndDrain(remaining);
                    if (dispatcherReceipt != null && dispatcherReceipt.IsTerminal)
                    {
                        var detachSpaceAvailable = false;
                        var terminalReady = false;
                        lock (_gate)
                        {
                            if (_terminal)
                            {
                                terminalReady = true;
                            }
                            else
                            {
                                // The dispatcher may report terminal while a
                                // coordinator pump is still unwinding.  Do
                                // not publish terminal until the second
                                // coordinator-side barrier observes both the
                                // pump and every registration terminal.
                                var pumpCompleted = _pumpTask == null || _pumpTask.IsCompleted;
                                var registrationsCompleted = _registrations.Values.All(item => item.Terminal);
                                if (pumpCompleted && registrationsCompleted)
                                {
                                    _terminal = true;
                                    _revision++;
                                    detachSpaceAvailable = true;
                                    terminalReady = true;
                                }
                            }
                        }
                        if (detachSpaceAvailable) _dispatcher.SpaceAvailable -= OnSpaceAvailable;
                        if (terminalReady)
                            return BuildDrainReceipt(true, false, true, string.Empty, dispatcherReceipt);
                    }
                    if (DateTime.UtcNow >= deadline)
                        return BuildDrainReceipt(false, true, true, "DispatcherDrainTimedOut", dispatcherReceipt);
                    continue;
                }
                if (remaining <= TimeSpan.Zero)
                    return BuildDrainReceipt(false, true, false, "RegistrationPending", dispatcherReceipt);
                RequestPump();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _closing = true;
                _sealed = true;
                foreach (var subscriber in _subscribers.Values.ToArray())
                {
                    subscriber.Active = false;
                    if (subscriber.Lease != null) subscriber.Lease.DeactivateFromCoordinator();
                }
                foreach (var record in _registrations.Values.ToArray())
                {
                    if (record.Terminal) continue;
                    record.PumpClaimed = false;
                    record.Terminal = true;
                    record.DeliveryState = WatchdogStopAllRegistrationState.Canceled;
                    record.FailureReason = "CoordinatorDisposed";
                    record.Completion.TrySetResult(null);
                }
                _revision++;
            }
            _dispatcher.SpaceAvailable -= OnSpaceAvailable;
            GC.SuppressFinalize(this);
        }

        private WatchdogStopAllOfferResult Offer(
            WatchdogStopAllScopeLease scopeLease,
            WatchdogStopAllOfferEnvelope envelope,
            WatchdogStopAllSourceFlags expectedSource)
        {
            if (envelope == null) return RejectedOffer(WatchdogStopAllOfferStatus.Invalid, string.Empty, "EnvelopeMissing");
            WatchdogStopAllOfferResult result;
            bool shouldPump;
            lock (_gate)
            {
                var validation = ValidateLeaseLocked(scopeLease);
                if (validation != null) return RejectedOffer(WatchdogStopAllOfferStatus.Invalid, envelope.CorrelationId, validation);
                if (_disposed) return RejectedOffer(WatchdogStopAllOfferStatus.Closed, envelope.CorrelationId, "CoordinatorDisposed");
                if (!ReferenceEquals(envelope.Lease, _lease) || !envelope.Scope.Equals(_lease.Scope))
                    return RejectedOffer(WatchdogStopAllOfferStatus.Invalid, envelope.CorrelationId, "EnvelopeScopeMismatch");
                if (envelope.SourceFlags != expectedSource)
                    return RejectedOffer(WatchdogStopAllOfferStatus.Invalid, envelope.CorrelationId, "SourceFlagsMustBeSingleSource");
                if (_closing) return RejectedOffer(WatchdogStopAllOfferStatus.Closed, envelope.CorrelationId, "CoordinatorClosing");
                if (_failClosed) return RejectedOffer(WatchdogStopAllOfferStatus.ConflictFailClosed, envelope.CorrelationId, _failureReason);
                if (expectedSource == WatchdogStopAllSourceFlags.Pipe && _durableCorrelation != null &&
                    !string.Equals(_durableCorrelation, envelope.CorrelationId, StringComparison.Ordinal))
                {
                    EnterFailClosedLocked("CorrelationConflict", true);
                    return RejectedOffer(WatchdogStopAllOfferStatus.ConflictFailClosed, envelope.CorrelationId, _failureReason);
                }
                if (expectedSource == WatchdogStopAllSourceFlags.Durable && _pipeCorrelation != null &&
                    !string.Equals(_pipeCorrelation, envelope.CorrelationId, StringComparison.Ordinal))
                {
                    EnterFailClosedLocked("CorrelationConflict", true);
                    return RejectedOffer(WatchdogStopAllOfferStatus.ConflictFailClosed, envelope.CorrelationId, _failureReason);
                }

                CanonicalRequestState canonical;
                var created = !_canonicalRequests.TryGetValue(envelope.CorrelationId, out canonical);
                if (created)
                {
                    _canonicalSequence++;
                    canonical = new CanonicalRequestState(envelope, _canonicalSequence);
                    _canonicalRequests.Add(canonical.CorrelationId, canonical);
                    _revision++;
                }
                else
                {
                    if (!string.Equals(canonical.ReasonCode, envelope.ReasonCode, StringComparison.Ordinal) ||
                        !canonical.Scope.Equals(envelope.Scope) ||
                        (!string.IsNullOrEmpty(canonical.RequestId) && !string.IsNullOrEmpty(envelope.RequestId) &&
                         !string.Equals(canonical.RequestId, envelope.RequestId, StringComparison.Ordinal)))
                    {
                        EnterFailClosedLocked("CorrelationOrReasonConflict", true);
                        return RejectedOffer(WatchdogStopAllOfferStatus.ConflictFailClosed, envelope.CorrelationId,
                            _failureReason, canonical.Snapshot());
                    }
                    if (canonical.Merge(envelope, _canonicalSequence + 1))
                    {
                        _canonicalSequence++;
                        canonical.CanonicalSequence = _canonicalSequence;
                        _revision++;
                    }
                }
                if ((envelope.SourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0) _pipeCorrelation = envelope.CorrelationId;
                if ((envelope.SourceFlags & WatchdogStopAllSourceFlags.Durable) != 0) _durableCorrelation = envelope.CorrelationId;
                EnsureDeliveriesForActiveSubscribersLocked(canonical);
                var activeSubscribers = ActiveSubscriberCountLocked();
                var status = activeSubscribers == 0 ? WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing :
                    created ? WatchdogStopAllOfferStatus.Pending : WatchdogStopAllOfferStatus.Coalesced;
                result = BuildOfferResultLocked(canonical, status,
                    status == WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing ? "RequiredSafetySubscriberMissing" :
                    created ? "Pending" : "Coalesced");
                shouldPump = activeSubscribers != 0;
            }
            if (shouldPump) RequestPump();
            return result;
        }

        private void RequestPump()
        {
            TaskCompletionSource<bool> completion;
            long generation;
            lock (_gate)
            {
                if (_disposed || _terminal || _failClosed || !_active || ActiveSubscriberCountLocked() == 0) return;
                if (!_registrations.Values.Any(item =>
                        item.DeliveryState == WatchdogStopAllRegistrationState.Pending &&
                        !item.Terminal && item.Subscriber.Active)) return;
                if (_pumpTask != null && !_pumpTask.IsCompleted) return;
                _pumpGeneration++;
                generation = _pumpGeneration;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pumpTask = completion.Task;
                Task.Run(() => RunPump(completion, generation));
            }
        }

        private void RunPump(TaskCompletionSource<bool> completion, long generation)
        {
            try
            {
                while (true)
                {
                    List<DispatchPlan> plans;
                    lock (_gate)
                    {
                        if (_disposed || _terminal || _failClosed || ActiveSubscriberCountLocked() == 0) return;
                        plans = _registrations.Values
                            .Where(item => item.DeliveryState == WatchdogStopAllRegistrationState.Pending && !item.PumpClaimed && item.Subscriber.Active)
                            .OrderBy(item => item.RegistrationId)
                            .Select(item =>
                            {
                                item.PumpClaimed = true;
                                item.DeliveryState = WatchdogStopAllRegistrationState.Admitting;
                                item.DeliveryGeneration++;
                                return new DispatchPlan(item);
                            }).ToList();
                    }
                    if (plans.Count == 0) return;
                    for (var planIndex = 0; planIndex < plans.Count; planIndex++)
                    {
                        var plan = plans[planIndex];
                        WatchdogRuntimeCallbackOfferResult admission = null;
                        Exception failure = null;
                        for (var attempt = 0; attempt < 128; attempt++)
                        {
                            try { admission = _dispatcher.OfferSafety(CreateDispatcherOffer(plan)); }
                            catch (Exception ex) { failure = ex; }
                            if (failure != null || admission == null || admission.Accepted ||
                                admission.Status != WatchdogRuntimeCallbackOfferStatus.RejectedSaturated ||
                                !string.Equals(admission.RejectionReason, "SafetyGateBusy", StringComparison.Ordinal)) break;
                            Thread.Sleep(1);
                        }
                        if (failure != null)
                        {
                            CompletePostRejected(plan, "DispatcherException:" + failure.GetType().Name);
                            continue;
                        }
                        if (admission == null || !admission.Accepted)
                        {
                            if (admission != null && admission.Status == WatchdogRuntimeCallbackOfferStatus.RejectedSaturated)
                            {
                                RevertToPending(plan);
                                for (var remainingIndex = planIndex + 1; remainingIndex < plans.Count; remainingIndex++)
                                    RevertToPending(plans[remainingIndex]);
                                return;
                            }
                            CompletePostRejected(plan, admission == null ? "DispatcherRejected" : admission.RejectionReason);
                            continue;
                        }
                        AttachAdmission(plan, admission);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    foreach (var record in _registrations.Values.Where(item =>
                                 item.DeliveryState == WatchdogStopAllRegistrationState.Admitting && !item.Terminal).ToArray())
                    {
                        record.PumpClaimed = false;
                        record.Terminal = true;
                        record.DeliveryState = WatchdogStopAllRegistrationState.PostRejected;
                        record.FailureReason = "PumpException:" + ex.GetType().Name;
                        record.Completion.TrySetResult(null);
                    }
                    _revision++;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pumpTask, completion.Task)) _pumpTask = null;
                }
                completion.TrySetResult(true);
            }
        }

        private WatchdogRuntimeCallbackOffer CreateDispatcherOffer(DispatchPlan plan)
        {
            return new WatchdogRuntimeCallbackOffer(
                plan.DispatcherIdentity, WatchdogRuntimeCallbackLane.Safety, "StopAll:" + plan.Canonical.ReasonCode,
                () => InvokeDelivery(plan), plan.Canonical.Detail,
                plan.Canonical.CorrelationId, plan.Canonical.ReasonCode);
        }

        private async Task InvokeDelivery(DispatchPlan plan)
        {
            WatchdogStopAllOfferEnvelope envelope;
            Func<WatchdogStopAllOfferEnvelope, Task> handler;
            lock (_gate)
            {
                if (!IsCurrentRegistrationLocked(plan.Record) || plan.Record.Terminal ||
                    (plan.Record.DeliveryState != WatchdogStopAllRegistrationState.Admitting &&
                     plan.Record.DeliveryState != WatchdogStopAllRegistrationState.Posted) ||
                    plan.Record.ActivationId != plan.ActivationId ||
                    plan.Record.DeliveryGeneration != plan.DeliveryGeneration || !plan.Subscriber.Active)
                {
                    SkipDisposedLocked(plan.Record);
                    return;
                }
                plan.Record.InFlight = true;
                envelope = plan.Canonical.Envelope;
                handler = plan.Subscriber.Handler;
            }
            try
            {
                var task = handler(envelope);
                if (task != null) await task.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (IsCurrentRegistrationLocked(plan.Record)) plan.Record.InFlight = false;
                }
            }
        }

        private void AttachAdmission(DispatchPlan plan, WatchdogRuntimeCallbackOfferResult admission)
        {
            var completion = admission.Completion ?? Task.FromResult<WatchdogCallbackCompletionReceipt>(null);
            var admissionSequence = admission.AdmissionSequence;
            var requestCanonicalSequence = plan.RequestCanonicalSequence;
            var requestRevision = plan.RequestRevision;
            var dispatcherCanonicalSequence = admission.CanonicalSequence;
            bool attached;
            lock (_gate)
            {
                attached = IsCurrentRegistrationLocked(plan.Record) && !plan.Record.Terminal &&
                    plan.Record.DeliveryState == WatchdogStopAllRegistrationState.Admitting &&
                    plan.Record.ActivationId == plan.ActivationId && plan.Record.DeliveryGeneration == plan.DeliveryGeneration &&
                    plan.Record.RequestCanonicalSequence == requestCanonicalSequence &&
                    plan.Record.RequestRevision == requestRevision &&
                    plan.Record.Canonical.RequestRevision == requestRevision &&
                    plan.Subscriber.Active;
                if (attached)
                {
                    plan.Record.PumpClaimed = false;
                    plan.Record.AdmissionSequence = admissionSequence;
                    plan.Record.DispatcherCanonicalSequence = dispatcherCanonicalSequence;
                    plan.Record.DispatcherIdentity = plan.DispatcherIdentity;
                    plan.Record.DeliveryState = WatchdogStopAllRegistrationState.Posted;
                    _revision++;
                }
                else plan.Record.PumpClaimed = false;
            }
            if (!attached) return;
            var activation = plan.ActivationId;
            var registration = plan.Record.RegistrationId;
            var deliveryGeneration = plan.DeliveryGeneration;
            completion.ContinueWith(task => CompleteDelivery(plan.Record, activation, registration,
                deliveryGeneration, admissionSequence, requestCanonicalSequence, requestRevision,
                dispatcherCanonicalSequence, task), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void CompleteDelivery(
            RegistrationRecord record,
            long activationId,
            long registrationId,
            long deliveryGeneration,
            long admissionSequence,
            long requestCanonicalSequence,
            long requestRevision,
            long dispatcherCanonicalSequence,
            Task<WatchdogCallbackCompletionReceipt> completion)
        {
            WatchdogCallbackCompletionReceipt receipt = null;
            string failure = string.Empty;
            try
            {
                if (completion.Status == TaskStatus.RanToCompletion) receipt = completion.Result;
                else failure = completion.IsCanceled ? "Canceled" : "HandlerFaulted";
            }
            catch (Exception ex) { failure = "HandlerFaulted:" + ex.GetType().Name; }
            lock (_gate)
            {
                if (!IsCurrentRegistrationLocked(record) || record.Terminal || record.ActivationId != activationId ||
                    record.RegistrationId != registrationId || record.DeliveryGeneration != deliveryGeneration ||
                    record.AdmissionSequence != admissionSequence ||
                    record.RequestCanonicalSequence != requestCanonicalSequence ||
                    record.RequestRevision != requestRevision ||
                    record.Canonical.RequestRevision != requestRevision ||
                    record.DispatcherCanonicalSequence != dispatcherCanonicalSequence) return;
                record.Terminal = true;
                record.PumpClaimed = false;
                if (!string.IsNullOrEmpty(failure))
                    record.DeliveryState = failure == "Canceled" ? WatchdogStopAllRegistrationState.Canceled : WatchdogStopAllRegistrationState.HandlerFaulted;
                else if (receipt == null || receipt.Status == WatchdogCallbackCompletionStatus.PostRejected)
                    record.DeliveryState = WatchdogStopAllRegistrationState.PostRejected;
                else if (receipt.Status == WatchdogCallbackCompletionStatus.Canceled)
                    record.DeliveryState = WatchdogStopAllRegistrationState.Canceled;
                else if (receipt.Status == WatchdogCallbackCompletionStatus.HandlerFaulted)
                    record.DeliveryState = WatchdogStopAllRegistrationState.HandlerFaulted;
                else record.DeliveryState = WatchdogStopAllRegistrationState.Completed;
                record.FailureReason = !string.IsNullOrEmpty(failure) ? failure : receipt?.Reason ?? string.Empty;
                _revision++;
            }
            record.Completion.TrySetResult(receipt);
        }

        private void RevertToPending(DispatchPlan plan)
        {
            lock (_gate)
            {
                if (!IsCurrentRegistrationLocked(plan.Record) || plan.Record.Terminal) return;
                if (plan.Record.DeliveryState == WatchdogStopAllRegistrationState.Admitting)
                {
                    plan.Record.PumpClaimed = false;
                    plan.Record.DeliveryState = WatchdogStopAllRegistrationState.Pending;
                    _revision++;
                }
            }
        }

        private void CompletePostRejected(DispatchPlan plan, string reason)
        {
            lock (_gate)
            {
                if (!IsCurrentRegistrationLocked(plan.Record) || plan.Record.Terminal) return;
                plan.Record.PumpClaimed = false;
                plan.Record.Terminal = true;
                plan.Record.DeliveryState = WatchdogStopAllRegistrationState.PostRejected;
                plan.Record.FailureReason = reason ?? "PostRejected";
                _revision++;
            }
            plan.Record.Completion.TrySetResult(null);
        }

        private void DisposeSubscriber(WatchdogStopAllScopeLease scopeLease, string subscriberId, long registrationId)
        {
            lock (_gate)
            {
                if (!_active || !ReferenceEquals(scopeLease, _lease) || _lease.ActivationId != scopeLease.ActivationId) return;
                if (!_subscribers.TryGetValue(subscriberId, out var subscriber) || subscriber.RegistrationId != registrationId || !subscriber.Active) return;
                subscriber.Active = false;
                foreach (var record in _registrations.Values.Where(item => ReferenceEquals(item.Subscriber, subscriber)).ToArray())
                {
                    if (record.Terminal) continue;
                    record.PumpClaimed = false;
                    record.Terminal = true;
                    record.DeliveryState = WatchdogStopAllRegistrationState.SkippedDisposed;
                    record.FailureReason = "SubscriberDisposed";
                    record.Completion.TrySetResult(null);
                }
                _revision++;
            }
        }

        private void OnSpaceAvailable() => RequestPump();

        private void EnsureAllDeliveriesLocked()
        {
            foreach (var canonical in _canonicalRequests.Values.ToArray()) EnsureDeliveriesForActiveSubscribersLocked(canonical);
        }

        private void EnsureDeliveriesForActiveSubscribersLocked(CanonicalRequestState canonical)
        {
            foreach (var subscriber in _subscribers.Values.Where(item => item.Active).ToArray()) EnsureDeliveryLocked(subscriber, canonical);
        }

        private void EnsureDeliveryLocked(SubscriberRecord subscriber, CanonicalRequestState canonical)
        {
            if (canonical.DeliveryIds.Any(id => _registrations.TryGetValue(id, out var existing) && ReferenceEquals(existing.Subscriber, subscriber))) return;
            if (_registrationSequence == long.MaxValue) throw new InvalidOperationException("Delivery registration exhausted.");
            _registrationSequence++;
            var record = new RegistrationRecord(_registrationSequence, subscriber, canonical);
            canonical.DeliveryIds.Add(record.RegistrationId);
            _registrations.Add(record.RegistrationId, record);
            _revision++;
        }

        private int ActiveSubscriberCountLocked() => _subscribers.Values.Count(item => item.Active);

        private void EnterFailClosedLocked(string reason, bool rejectPending)
        {
            _failClosed = true;
            _failureReason = reason ?? "ConflictFailClosed";
            _revision++;
            if (!rejectPending) return;
            foreach (var record in _registrations.Values.Where(item => !item.Terminal &&
                         (item.DeliveryState == WatchdogStopAllRegistrationState.Pending || item.DeliveryState == WatchdogStopAllRegistrationState.Admitting)).ToArray())
            {
                record.PumpClaimed = false;
                record.Terminal = true;
                record.DeliveryState = WatchdogStopAllRegistrationState.PostRejected;
                record.FailureReason = _failureReason;
                record.Completion.TrySetResult(null);
            }
        }

        private string ValidateLeaseLocked(WatchdogStopAllScopeLease scopeLease)
        {
            if (!_active || _lease == null) return "CoordinatorNotActive";
            if (scopeLease == null) return "LeaseMissing";
            if (!ReferenceEquals(scopeLease, _lease) || scopeLease.ActivationId != _lease.ActivationId || !scopeLease.Scope.Equals(_lease.Scope)) return "LeaseMismatch";
            return null;
        }

        private WatchdogStopAllOfferResult BuildOfferResultLocked(CanonicalRequestState canonical, WatchdogStopAllOfferStatus status, string reason)
        {
            var deliveries = canonical.DeliveryIds.Where(id => _registrations.ContainsKey(id)).OrderBy(id => id)
                .Select(id => DeliveryResultLocked(_registrations[id])).ToArray();
            return new WatchdogStopAllOfferResult(status, canonical.CorrelationId, reason, canonical.Snapshot(), deliveries);
        }

        private WatchdogStopAllDeliveryResult DeliveryResultLocked(RegistrationRecord record)
        {
            return new WatchdogStopAllDeliveryResult(record.RegistrationId, record.Subscriber.SubscriberId, record.ActivationId,
                record.DeliveryGeneration, record.AdmissionSequence, record.RequestCanonicalSequence,
                record.RequestRevision, record.DispatcherCanonicalSequence, record.DispatcherIdentity,
                record.DeliveryState, record.FailureReason, record.Terminal, record.Completion.Task);
        }

        private WatchdogStopAllOfferResult RejectedOffer(WatchdogStopAllOfferStatus status, string correlationId, string reason, WatchdogStopAllCanonicalRequestState canonical = null)
        {
            return new WatchdogStopAllOfferResult(status, correlationId, reason, canonical, new WatchdogStopAllDeliveryResult[0]);
        }

        private WatchdogStopAllRegistrationLease RejectedSubscriberLease(WatchdogStopAllScopeLease scopeLease, string subscriberId, string reason)
        {
            return new WatchdogStopAllRegistrationLease(scopeLease, subscriberId, 0, false, reason, null);
        }

        private WatchdogStopAllDrainReceipt BuildDrainReceipt(bool terminal, bool timedOut, bool registrationsTerminal, string reason, WatchdogRuntimeCallbackDrainReceipt dispatcherReceipt)
        {
            WatchdogStopAllCoordinatorSnapshot snapshot;
            bool failClosed;
            lock (_gate) { snapshot = CaptureLocked(); failClosed = _failClosed; }
            return new WatchdogStopAllDrainReceipt(terminal, timedOut, registrationsTerminal,
                terminal && dispatcherReceipt != null && dispatcherReceipt.AllResourcesReleased,
                snapshot.Sealed, failClosed, reason, dispatcherReceipt, snapshot);
        }

        private WatchdogStopAllCoordinatorSnapshot CaptureLocked()
        {
            var subscribers = _subscribers.Values.OrderBy(item => item.RegistrationId)
                .Select(item => new WatchdogStopAllSubscriberSnapshot(item.RegistrationId, item.SubscriberId, item.Lease.ActivationId, item.Active)).ToArray();
            var canonical = _canonicalRequests.Values.OrderBy(item => item.CanonicalSequence).Select(item => item.Snapshot()).ToArray();
            var registrations = _registrations.Values.OrderBy(item => item.RegistrationId).Select(RegistrationSnapshotLocked).ToArray();
            return new WatchdogStopAllCoordinatorSnapshot(_lease?.Scope, _lease, _revision, _active, _closing, _sealed,
                _drainStarted, _disposed, _failClosed, _failureReason, ActiveSubscriberCountLocked(),
                _pumpTask != null && !_pumpTask.IsCompleted, _pumpGeneration, subscribers, canonical, registrations);
        }

        private WatchdogStopAllRegistrationSnapshot RegistrationSnapshotLocked(RegistrationRecord record)
        {
            return new WatchdogStopAllRegistrationSnapshot(record.RegistrationId, record.Subscriber.SubscriberId,
                record.Canonical.CorrelationId, record.ActivationId, record.DeliveryGeneration,
                record.AdmissionSequence, record.RequestCanonicalSequence, record.RequestRevision,
                record.DispatcherCanonicalSequence, record.DispatcherIdentity, record.DeliveryState,
                record.FailureReason, record.Terminal);
        }

        private bool IsCurrentRegistrationLocked(RegistrationRecord record)
        {
            return record != null && _registrations.TryGetValue(record.RegistrationId, out var current) && ReferenceEquals(current, record);
        }

        private void SkipDisposedLocked(RegistrationRecord record)
        {
            if (!IsCurrentRegistrationLocked(record) || record.Terminal) return;
            record.PumpClaimed = false;
            record.Terminal = true;
            record.DeliveryState = WatchdogStopAllRegistrationState.SkippedDisposed;
            record.FailureReason = "SubscriberDisposed";
            _revision++;
            record.Completion.TrySetResult(null);
        }

        private static void WaitTask(Task task, TimeSpan timeout)
        {
            if (task == null || task.IsCompleted || timeout <= TimeSpan.Zero) return;
            var milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, timeout.TotalMilliseconds));
            try { task.Wait(milliseconds); } catch (AggregateException) { }
        }
    }
}
