using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal enum RuntimeCallbackPipelineState
    {
        Unbound = 0,
        Binding = 1,
        BoundInactive = 2,
        Activating = 3,
        Ready = 4,
        Closing = 5,
        Terminal = 6,
        FailClosed = 7
    }

    /// <summary>
    /// Frozen identity captured only after the Engine exact-Attached gate.
    /// AuthorityGeneration and AttachEpoch are the validated attached
    /// connection generation and cannot be changed by later reconnects.
    /// </summary>
    internal sealed class RuntimeValidatedAttachIdentity
    {
        internal string SessionId { get; }
        internal long SessionGeneration { get; }
        internal long SessionLease { get; }
        internal long AuthorityGeneration { get; }
        internal long AttachedConnectionGeneration { get; }
        internal long AttachEpoch { get; }
        internal int AuthorityProcessId { get; }
        internal long AuthorityProcessStartUtcTicks { get; }
        internal string AuthorityInstanceNonceHash { get; }
        internal string ExactKey => string.Join("|", new[]
        {
            SessionId,
            SessionGeneration.ToString(CultureInfo.InvariantCulture),
            SessionLease.ToString(CultureInfo.InvariantCulture),
            AuthorityGeneration.ToString(CultureInfo.InvariantCulture),
            AttachedConnectionGeneration.ToString(CultureInfo.InvariantCulture),
            AttachEpoch.ToString(CultureInfo.InvariantCulture),
            AuthorityProcessId.ToString(CultureInfo.InvariantCulture),
            AuthorityProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
            AuthorityInstanceNonceHash
        });

        internal RuntimeValidatedAttachIdentity(string sessionId, long sessionGeneration, long sessionLease,
            long attachedConnectionGeneration, int authorityProcessId, long authorityProcessStartUtcTicks,
            string authorityInstanceNonce = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("SessionId不能为空。", nameof(sessionId));
            if (sessionGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
            if (sessionLease <= 0) throw new ArgumentOutOfRangeException(nameof(sessionLease));
            if (attachedConnectionGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(attachedConnectionGeneration));
            if (authorityProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(authorityProcessId));
            if (authorityProcessStartUtcTicks <= 0) throw new ArgumentOutOfRangeException(nameof(authorityProcessStartUtcTicks));
            SessionId = sessionId;
            SessionGeneration = sessionGeneration;
            SessionLease = sessionLease;
            AuthorityGeneration = attachedConnectionGeneration;
            AttachedConnectionGeneration = attachedConnectionGeneration;
            AttachEpoch = attachedConnectionGeneration;
            AuthorityProcessId = authorityProcessId;
            AuthorityProcessStartUtcTicks = authorityProcessStartUtcTicks;
            AuthorityInstanceNonceHash = HashNonce(authorityInstanceNonce);
        }

        private static string HashNonce(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce)) return string.Empty;
            using (var sha = SHA256.Create())
                return BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(nonce.Trim())))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
        }
    }

    internal sealed class RuntimePendingStopRequest
    {
        internal string ReasonCode { get; }
        internal string CorrelationId { get; }
        internal string RequestId { get; }
        internal string Detail { get; }
        internal string PipeDetail { get; }
        internal string DurableDetail { get; }
        internal WatchdogStopAllSourceFlags SourceFlags { get; }
        internal WatchdogStopAllSourceFlags FirstSource { get; }
        internal WatchdogStopAllSourceFlags ObservedSources { get; }
        internal WatchdogStopAllSourceFlags AdmittedSources { get; }
        internal long PipeSequence { get; }
        internal long DurableVersion { get; }
        internal long PipeAdmissionReservationId { get; }
        internal long DurableAdmissionReservationId { get; }

        internal RuntimePendingStopRequest(string reasonCode, string correlationId, string requestId,
            string detail, WatchdogStopAllSourceFlags sourceFlags, long pipeSequence, long durableVersion,
            string pipeDetail = null, string durableDetail = null,
            WatchdogStopAllSourceFlags observedSources = WatchdogStopAllSourceFlags.None,
            WatchdogStopAllSourceFlags admittedSources = WatchdogStopAllSourceFlags.None,
            long pipeAdmissionReservationId = 0, long durableAdmissionReservationId = 0,
            WatchdogStopAllSourceFlags firstSource = WatchdogStopAllSourceFlags.None)
        {
            ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? "WatchdogStopAll" : reasonCode.Trim();
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId.Trim();
            RequestId = string.IsNullOrWhiteSpace(requestId) ? CorrelationId : requestId.Trim();
            Detail = detail ?? string.Empty;
            PipeDetail = pipeDetail ?? string.Empty;
            DurableDetail = durableDetail ?? string.Empty;
            SourceFlags = sourceFlags;
            FirstSource = firstSource == WatchdogStopAllSourceFlags.Pipe ||
                          firstSource == WatchdogStopAllSourceFlags.Durable
                ? firstSource
                : ((sourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0
                    ? WatchdogStopAllSourceFlags.Pipe
                    : WatchdogStopAllSourceFlags.Durable);
            ObservedSources = observedSources == WatchdogStopAllSourceFlags.None ? sourceFlags : observedSources;
            AdmittedSources = admittedSources;
            PipeSequence = pipeSequence;
            DurableVersion = durableVersion;
            PipeAdmissionReservationId = pipeAdmissionReservationId;
            DurableAdmissionReservationId = durableAdmissionReservationId;
        }
    }

    internal sealed class RuntimeIngressAdmissionReservation : IDisposable
    {
        private readonly RuntimeCallbackIngressGate _owner;
        private int _settled;
        internal WatchdogStopAllSourceFlags Source { get; }
        internal RuntimePendingStopRequest Request { get; }
        internal long ReservationId { get; }
        internal long DrainToken { get; }
        internal RuntimeCallbackIngressGate Owner => _owner;
        internal bool IsSettled => Volatile.Read(ref _settled) != 0;

        internal RuntimeIngressAdmissionReservation(RuntimeCallbackIngressGate owner,
            WatchdogStopAllSourceFlags source, RuntimePendingStopRequest request, long reservationId,
            long drainToken)
        {
            _owner = owner;
            Source = source;
            Request = request;
            ReservationId = reservationId;
            DrainToken = drainToken;
        }

        internal bool Commit()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            return _owner.CommitAdmission(this);
        }

        internal bool Release()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0) return false;
            return _owner.ReleaseAdmission(this);
        }

        public void Dispose() => Release();
    }

    /// <summary>
    /// The one ingress canonical request for a runtime context.  Pipe and
    /// Durable are evidence sources for this same request; they never create
    /// parallel buffered requests.
    /// </summary>
    internal sealed class RuntimeIngressCanonicalRequest
    {
        internal string ReasonCode { get; }
        internal string CorrelationId { get; }
        internal string RequestId { get; }
        internal string Detail { get; private set; }
        internal string PipeDetail { get; private set; }
        internal string DurableDetail { get; private set; }
        internal WatchdogStopAllSourceFlags SourceFlags { get; private set; }
        internal WatchdogStopAllSourceFlags FirstSource { get; }
        internal WatchdogStopAllSourceFlags ObservedSources => SourceFlags;
        internal WatchdogStopAllSourceFlags AdmittedSources { get; private set; }
        internal long PipeSequence { get; private set; }
        internal long DurableVersion { get; private set; }
        internal bool PipeAdmissionReserved { get; private set; }
        internal bool DurableAdmissionReserved { get; private set; }
        internal long PipeAdmissionReservationId { get; private set; }
        internal long DurableAdmissionReservationId { get; private set; }

        internal RuntimeIngressCanonicalRequest(string reasonCode, string correlationId,
            string requestId, string detail, WatchdogStopAllSourceFlags source,
            long pipeSequence, long durableVersion)
        {
            ReasonCode = reasonCode ?? string.Empty;
            CorrelationId = correlationId ?? string.Empty;
            RequestId = requestId ?? string.Empty;
            Detail = detail ?? string.Empty;
            PipeDetail = (source & WatchdogStopAllSourceFlags.Pipe) != 0 ? (detail ?? string.Empty) : string.Empty;
            DurableDetail = (source & WatchdogStopAllSourceFlags.Durable) != 0 ? (detail ?? string.Empty) : string.Empty;
            SourceFlags = source;
            FirstSource = source;
            AdmittedSources = WatchdogStopAllSourceFlags.None;
            PipeSequence = pipeSequence;
            DurableVersion = durableVersion;
        }

        internal bool HasSource(WatchdogStopAllSourceFlags source) =>
            (SourceFlags & source) != 0;

        internal bool IdentityMatches(string reason, string correlation, string request)
        {
            return string.Equals(ReasonCode, reason ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(CorrelationId, correlation ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(RequestId, request ?? string.Empty, StringComparison.Ordinal);
        }

        internal void Observe(WatchdogStopAllSourceFlags source, long pipeSequence,
            long durableVersion, string detail)
        {
            var hadPipe = (SourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0;
            var hadDurable = (SourceFlags & WatchdogStopAllSourceFlags.Durable) != 0;
            SourceFlags |= source;
            if ((source & WatchdogStopAllSourceFlags.Pipe) != 0 && !hadPipe)
                PipeDetail = detail ?? string.Empty;
            if ((source & WatchdogStopAllSourceFlags.Durable) != 0 && !hadDurable)
                DurableDetail = detail ?? string.Empty;
            if (string.IsNullOrEmpty(Detail)) Detail = detail ?? string.Empty;
            if ((source & WatchdogStopAllSourceFlags.Pipe) != 0 && pipeSequence > PipeSequence)
                PipeSequence = pipeSequence;
            if ((source & WatchdogStopAllSourceFlags.Durable) != 0 && durableVersion > DurableVersion)
                DurableVersion = durableVersion;
        }

        internal bool HasPipeDetail => (SourceFlags & WatchdogStopAllSourceFlags.Pipe) != 0;
        internal bool HasDurableDetail => (SourceFlags & WatchdogStopAllSourceFlags.Durable) != 0;

        internal bool TryReserve(WatchdogStopAllSourceFlags source, long reservationId)
        {
            if (reservationId <= 0) return false;
            if ((source & WatchdogStopAllSourceFlags.Pipe) != 0)
            {
                if ((AdmittedSources & WatchdogStopAllSourceFlags.Pipe) != 0 || PipeAdmissionReserved) return false;
                PipeAdmissionReserved = true;
                PipeAdmissionReservationId = reservationId;
                return true;
            }
            if ((source & WatchdogStopAllSourceFlags.Durable) != 0)
            {
                if ((AdmittedSources & WatchdogStopAllSourceFlags.Durable) != 0 || DurableAdmissionReserved) return false;
                DurableAdmissionReserved = true;
                DurableAdmissionReservationId = reservationId;
                return true;
            }
            return false;
        }

        internal bool Commit(WatchdogStopAllSourceFlags source, long reservationId)
        {
            if (reservationId <= 0) return false;
            if ((source & WatchdogStopAllSourceFlags.Pipe) != 0)
            {
                if (!PipeAdmissionReserved || PipeAdmissionReservationId != reservationId) return false;
                PipeAdmissionReserved = false;
                PipeAdmissionReservationId = 0;
                AdmittedSources |= WatchdogStopAllSourceFlags.Pipe;
                return true;
            }
            if ((source & WatchdogStopAllSourceFlags.Durable) != 0)
            {
                if (!DurableAdmissionReserved || DurableAdmissionReservationId != reservationId) return false;
                DurableAdmissionReserved = false;
                DurableAdmissionReservationId = 0;
                AdmittedSources |= WatchdogStopAllSourceFlags.Durable;
                return true;
            }
            return false;
        }

        internal bool Release(WatchdogStopAllSourceFlags source, long reservationId)
        {
            if (reservationId <= 0) return false;
            if ((source & WatchdogStopAllSourceFlags.Pipe) != 0)
            {
                if (!PipeAdmissionReserved || PipeAdmissionReservationId != reservationId) return false;
                PipeAdmissionReserved = false;
                PipeAdmissionReservationId = 0;
                return true;
            }
            if ((source & WatchdogStopAllSourceFlags.Durable) != 0)
            {
                if (!DurableAdmissionReserved || DurableAdmissionReservationId != reservationId) return false;
                DurableAdmissionReserved = false;
                DurableAdmissionReservationId = 0;
                return true;
            }
            return false;
        }

        internal RuntimePendingStopRequest Snapshot()
        {
            return new RuntimePendingStopRequest(ReasonCode, CorrelationId, RequestId,
                Detail, SourceFlags, PipeSequence, DurableVersion,
                PipeDetail, DurableDetail, ObservedSources, AdmittedSources,
                PipeAdmissionReservationId, DurableAdmissionReservationId, FirstSource);
        }
    }

    internal sealed class RuntimeIngressOffer
    {
        internal bool Accepted { get; }
        internal bool Live { get; }
        internal bool Conflict { get; }
        internal string Reason { get; }
        internal RuntimePendingStopRequest Request { get; }

        internal RuntimeIngressOffer(bool accepted, bool live, bool conflict, string reason,
            RuntimePendingStopRequest request)
        {
            Accepted = accepted;
            Live = live;
            Conflict = conflict;
            Reason = reason ?? string.Empty;
            Request = request;
        }
    }

    internal sealed class RuntimeCallbackIngressSnapshot
    {
        internal RuntimeCallbackPipelineState State { get; }
        internal bool Active { get; }
        internal bool Closing { get; }
        internal bool Disposed { get; }
        internal bool FailClosed { get; }
        internal string FailureReason { get; }
        internal string FrozenCorrelationId { get; }
        internal string FrozenReasonCode { get; }
        internal string FrozenRequestId { get; }
        internal string PipeDetail { get; }
        internal string DurableDetail { get; }
        internal WatchdogStopAllSourceFlags ObservedSources { get; }
        internal WatchdogStopAllSourceFlags AdmittedSources { get; }
        internal long PipeSequence { get; }
        internal long DurableVersion { get; }
        internal bool PipeAdmissionReserved { get; }
        internal bool DurableAdmissionReserved { get; }
        internal long PipeAdmissionReservationId { get; }
        internal long DurableAdmissionReservationId { get; }
        internal int PendingCount { get; }
        internal int ActiveProducers { get; }
        internal int ActiveAdmissions { get; }
        internal long ActiveDrainToken { get; }

        internal RuntimeCallbackIngressSnapshot(RuntimeCallbackPipelineState state, bool active, bool closing,
            bool disposed, bool failClosed, string failureReason, string frozenCorrelationId,
            string frozenReasonCode, string frozenRequestId, long pipeSequence, long durableVersion,
            int pendingCount, int activeProducers, string pipeDetail = null, string durableDetail = null,
            WatchdogStopAllSourceFlags observedSources = WatchdogStopAllSourceFlags.None,
            WatchdogStopAllSourceFlags admittedSources = WatchdogStopAllSourceFlags.None,
            bool pipeAdmissionReserved = false, bool durableAdmissionReserved = false,
            int activeAdmissions = 0, long pipeAdmissionReservationId = 0,
            long durableAdmissionReservationId = 0, long activeDrainToken = 0)
        {
            State = state;
            Active = active;
            Closing = closing;
            Disposed = disposed;
            FailClosed = failClosed;
            FailureReason = failureReason ?? string.Empty;
            FrozenCorrelationId = frozenCorrelationId ?? string.Empty;
            FrozenReasonCode = frozenReasonCode ?? string.Empty;
            FrozenRequestId = frozenRequestId ?? string.Empty;
            PipeDetail = pipeDetail ?? string.Empty;
            DurableDetail = durableDetail ?? string.Empty;
            ObservedSources = observedSources;
            AdmittedSources = admittedSources;
            PipeSequence = pipeSequence;
            DurableVersion = durableVersion;
            PipeAdmissionReserved = pipeAdmissionReserved;
            DurableAdmissionReserved = durableAdmissionReserved;
            PipeAdmissionReservationId = pipeAdmissionReservationId;
            DurableAdmissionReservationId = durableAdmissionReservationId;
            PendingCount = pendingCount;
            ActiveProducers = activeProducers;
            ActiveAdmissions = activeAdmissions;
            ActiveDrainToken = activeDrainToken;
        }
    }

    /// <summary>
    /// Inert canonical router.  Context construction allocates only this
    /// object; dispatcher/coordinator threads are created by explicit target
    /// binding after the reservation wins.
    /// </summary>
    internal sealed class RuntimeCallbackIngressGate : IDisposable
    {
        private readonly object _gate = new object();
        private TaskCompletionSource<bool> _drainCompletion;
        private TaskCompletionSource<bool> _admissionDrainCompletion;
        private RuntimeIngressCanonicalRequest _canonical;
        private long _pipeSequence;
        private long _durableVersion;
        private long _admissionReservationSequence;
        private long _drainTokenSequence;
        private long _activeDrainToken;
        private int _activeProducers;
        private int _activeAdmissions;
        private readonly Dictionary<long, RuntimeIngressAdmissionReservation> _activeAdmissionReservations =
            new Dictionary<long, RuntimeIngressAdmissionReservation>();
        // Durable evidence is reserved at canonical routing time and only
        // consumed after the corresponding coordinator admission succeeds.
        // This prevents a rejected/closed admission from losing the marker.
        private int _durableMarkerReserved;
        private int _durableMarkerClaimed;
        private bool _active;
        // BeginClosing is the pre-engine-receipt phase: callbacks are still
        // admitted while the engine is being quiesced.  Only CloseAndDrain
        // closes the admission boundary after the receipt is observed.
        private bool _closing;
        private bool _acceptingClosed;
        private bool _disposed;
        private bool _failClosed;
        private string _failureReason = string.Empty;
        private string _frozenCorrelationId = string.Empty;
        private string _frozenReasonCode = string.Empty;
        private string _frozenRequestId = string.Empty;

        internal RuntimeCallbackIngressLease TryAcquire(RuntimeTransportSessionContext context)
        {
            if (context == null) return null;
            lock (_gate)
            {
                if (_disposed || _acceptingClosed) return null;
                _activeProducers++;
                return new RuntimeCallbackIngressLease(this, context);
            }
        }

        internal RuntimeIngressOffer Route(RuntimeCallbackIngressLease lease,
            RuntimeTransportSessionContext context, string reason, string correlation, string requestId,
            string detail, WatchdogStopAllSourceFlags source)
        {
            if (lease == null || !lease.IsExact(context))
                return new RuntimeIngressOffer(false, false, false, "IngressLeaseMismatch", null);
            lock (_gate)
            {
                if (_disposed || _acceptingClosed) return new RuntimeIngressOffer(false, false, false, "IngressClosed", null);
                if (_failClosed) return new RuntimeIngressOffer(false, false, true, _failureReason, null);
                if (source != WatchdogStopAllSourceFlags.Pipe &&
                    source != WatchdogStopAllSourceFlags.Durable)
                    return new RuntimeIngressOffer(false, false, false, "InvalidSourceFlags", null);
                var canonical = _canonical;
                var inputReason = string.IsNullOrWhiteSpace(reason) ? "WatchdogStopAll" : reason.Trim();
                var inputCorrelation = string.IsNullOrWhiteSpace(correlation)
                    ? Guid.NewGuid().ToString("N") : correlation.Trim();
                var inputRequest = string.IsNullOrWhiteSpace(requestId) ? inputCorrelation : requestId.Trim();
                var observed = canonical != null && canonical.HasSource(source);
                if (source == WatchdogStopAllSourceFlags.Durable && _durableMarkerClaimed != 0)
                    return new RuntimeIngressOffer(false, false, false, "DurableAlreadyConsumed", null);
                if (source == WatchdogStopAllSourceFlags.Durable && _durableMarkerReserved != 0)
                    return new RuntimeIngressOffer(false, false, false, "DurableAdmissionReserved", null);
                if (canonical != null && observed &&
                    !canonical.IdentityMatches(inputReason, inputCorrelation, inputRequest))
                {
                    _failClosed = true;
                    _failureReason = "SameSourceIdentityConflict";
                    return new RuntimeIngressOffer(false, false, true, _failureReason, null);
                }

                // The first source freezes correlation/reason/request.  The
                // opposite source is evidence for that same request and
                // inherits the frozen identity without comparing its own
                // transport-specific envelope fields.
                var newSource = canonical == null || !observed;
                var normalizedReason = canonical == null || observed ? inputReason : canonical.ReasonCode;
                var normalizedCorrelation = canonical == null || observed ? inputCorrelation : canonical.CorrelationId;
                var normalizedRequest = canonical == null || observed ? inputRequest : canonical.RequestId;
                var pipeSequence = source == WatchdogStopAllSourceFlags.Pipe && newSource
                    ? _pipeSequence + 1 : (canonical?.PipeSequence ?? 0);
                var durableVersion = source == WatchdogStopAllSourceFlags.Durable && newSource
                    ? _durableVersion + 1 : (canonical?.DurableVersion ?? 0);
                if (canonical == null)
                {
                    canonical = new RuntimeIngressCanonicalRequest(normalizedReason,
                        normalizedCorrelation, normalizedRequest, detail, source,
                        pipeSequence, durableVersion);
                    _canonical = canonical;
                    _frozenCorrelationId = canonical.CorrelationId;
                    _frozenReasonCode = canonical.ReasonCode;
                    _frozenRequestId = canonical.RequestId;
                }
                else if (newSource)
                {
                    canonical.Observe(source, pipeSequence, durableVersion, detail);
                }
                if (source == WatchdogStopAllSourceFlags.Pipe && newSource) _pipeSequence = pipeSequence;
                if (source == WatchdogStopAllSourceFlags.Durable)
                {
                    if (newSource) _durableVersion = durableVersion;
                    // Keep a marker reservation while the source admission is
                    // attempted outside this lock.
                    _durableMarkerReserved = 1;
                }
                var request = canonical.Snapshot();
                if (_active) return new RuntimeIngressOffer(true, true, false, string.Empty, request);
                return new RuntimeIngressOffer(true, false, false, string.Empty, request);
            }
        }

        internal RuntimeIngressAdmissionReservation TryReserveAdmission(
            WatchdogStopAllSourceFlags source)
        {
            if (source != WatchdogStopAllSourceFlags.Pipe &&
                source != WatchdogStopAllSourceFlags.Durable) return null;
            lock (_gate)
            {
                if (_disposed || _failClosed || _canonical == null) return null;
                // Once closing has rejected new producers, only the explicit
                // drain token may finish already-observed source evidence.
                if (_acceptingClosed && _activeDrainToken == 0) return null;
                var reservationId = ++_admissionReservationSequence;
                var drainToken = _activeDrainToken;
                if (!_canonical.HasSource(source) || !_canonical.TryReserve(source, reservationId)) return null;
                _activeAdmissions++;
                var reservation = new RuntimeIngressAdmissionReservation(this, source,
                    _canonical.Snapshot(), reservationId, drainToken);
                _activeAdmissionReservations[reservationId] = reservation;
                return reservation;
            }
        }

        internal bool CommitAdmission(RuntimeIngressAdmissionReservation reservation)
        {
            if (reservation == null) return false;
            lock (_gate)
            {
                if (_canonical == null || !ReferenceEquals(reservation.Owner, this) ||
                    !_activeAdmissionReservations.TryGetValue(reservation.ReservationId, out var current) ||
                    !ReferenceEquals(current, reservation)) return false;
                // A reservation created before CloseAndDrain has token 0.  It
                // must become stale as soon as the close boundary publishes
                // any non-zero token; conversely a drain reservation must not
                // survive the boundary after that token is cleared.  Compare
                // the complete token pair rather than treating zero as a
                // wildcard, otherwise a pre-close reservation can commit
                // during a later drain generation.
                if (_activeDrainToken != reservation.DrainToken)
                {
                    _canonical.Release(reservation.Source, reservation.ReservationId);
                    RemoveAdmissionReservationLocked(reservation);
                    return false;
                }
                var committed = _canonical.Commit(reservation.Source, reservation.ReservationId);
                if (!committed)
                {
                    _canonical.Release(reservation.Source, reservation.ReservationId);
                    RemoveAdmissionReservationLocked(reservation);
                    return false;
                }
                if (reservation.Source == WatchdogStopAllSourceFlags.Durable)
                {
                    _durableMarkerReserved = 0;
                    _durableMarkerClaimed = 1;
                }
                RemoveAdmissionReservationLocked(reservation);
                return true;
            }
        }

        internal bool ReleaseAdmission(RuntimeIngressAdmissionReservation reservation)
        {
            if (reservation == null) return false;
            lock (_gate)
            {
                if (_canonical == null || !ReferenceEquals(reservation.Owner, this) ||
                    !_activeAdmissionReservations.TryGetValue(reservation.ReservationId, out var current) ||
                    !ReferenceEquals(current, reservation)) return false;
                // See CommitAdmission: zero is the pre-close generation, not
                // a wildcard.  Every settlement must match the exact drain
                // generation captured by the reservation.
                if (_activeDrainToken != reservation.DrainToken)
                {
                    _canonical.Release(reservation.Source, reservation.ReservationId);
                    RemoveAdmissionReservationLocked(reservation);
                    return false;
                }
                var released = _canonical.Release(reservation.Source, reservation.ReservationId);
                if (!released)
                {
                    RemoveAdmissionReservationLocked(reservation);
                    return false;
                }
                if (reservation.Source == WatchdogStopAllSourceFlags.Durable)
                    _durableMarkerReserved = 0;
                RemoveAdmissionReservationLocked(reservation);
                return true;
            }
        }

        private void RemoveAdmissionReservationLocked(RuntimeIngressAdmissionReservation reservation)
        {
            if (reservation == null || !_activeAdmissionReservations.Remove(reservation.ReservationId)) return;
            ReleaseAdmissionCountLocked();
        }

        private void ReleaseAdmissionCountLocked()
        {
            if (_activeAdmissions > 0) _activeAdmissions--;
            if (_activeAdmissions == 0) _admissionDrainCompletion?.TrySetResult(true);
        }

        internal RuntimePendingStopRequest[] ActivateAndTake(WatchdogStopAllScopeLease scopeLease)
        {
            if (scopeLease == null) return Array.Empty<RuntimePendingStopRequest>();
            lock (_gate)
            {
                if (_disposed || _failClosed || _acceptingClosed) return Array.Empty<RuntimePendingStopRequest>();
                // Activation is only the buffer->live transition.  The
                // canonical outbox remains intact until each source admission
                // commits, so a failed offer can be retried without losing
                // evidence.
                _active = true;
                return _canonical == null
                    ? Array.Empty<RuntimePendingStopRequest>()
                    : new[] { _canonical.Snapshot() };
            }
        }

        /// <summary>
        /// Returns the immutable canonical outbox without changing its
        /// observed/admitted state.  The advance executor uses this on every
        /// Ready pass so a failed source admission remains pumpable.
        /// </summary>
        internal RuntimePendingStopRequest[] CaptureLiveOutbox()
        {
            lock (_gate)
            {
                if (_disposed || _failClosed || !_active || _canonical == null)
                    return Array.Empty<RuntimePendingStopRequest>();
                return new[] { _canonical.Snapshot() };
            }
        }

        internal void BeginClosing()
        {
            lock (_gate) _closing = true;
        }

        internal RuntimeCallbackIngressDrainReceipt CloseAndDrain(TimeSpan timeout,
            Func<RuntimePendingStopRequest[], bool> pump = null,
            Func<bool> advanceTerminal = null, Func<bool> retryTerminal = null)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            var started = DateTime.UtcNow;
            lock (_gate)
            {
                _closing = true;
                _acceptingClosed = true;
                _activeDrainToken = ++_drainTokenSequence;
                if (_activeProducers != 0)
                    _drainCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_activeAdmissions != 0)
                    _admissionDrainCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var pumpFault = string.Empty;
            while (DateTime.UtcNow - started < timeout)
            {
                var active = Volatile.Read(ref _activeProducers);
                var admissions = Volatile.Read(ref _activeAdmissions);
                var pending = PendingCount;
                var advanceDone = advanceTerminal == null || SafeWorkersTerminal(advanceTerminal);
                var retryDone = retryTerminal == null || SafeWorkersTerminal(retryTerminal);
                var workersDone = advanceDone && retryDone;
                if (active == 0 && admissions == 0 && pending == 0 && workersDone) break;

                // Existing producers and in-flight admissions are joined
                // before asking the coordinator to pump the retained
                // outbox.  This gives the drain token a single owner and
                // prevents a late producer from racing a source reservation.
                if (active == 0 && admissions == 0 && pending != 0 && pump != null)
                {
                    try
                    {
                        var outbox = CaptureLiveOutbox();
                        if (outbox.Length != 0 && !pump(outbox))
                            Thread.Sleep(5);
                    }
                    catch (Exception ex)
                    {
                        pumpFault = ex.GetBaseException().Message;
                        lock (_gate)
                        {
                            _failClosed = true;
                            _failureReason = "IngressDrainPumpFault:" + ex.GetType().Name;
                        }
                        break;
                    }
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
            lock (_gate) _activeDrainToken = 0;
            var finalActive = Volatile.Read(ref _activeProducers);
            var finalAdmissions = Volatile.Read(ref _activeAdmissions);
            var finalPending = PendingCount;
            var finalAdvanceDone = advanceTerminal == null || SafeWorkersTerminal(advanceTerminal);
            var finalRetryDone = retryTerminal == null || SafeWorkersTerminal(retryTerminal);
            var timedOut = DateTime.UtcNow - started >= timeout &&
                (finalActive != 0 || finalAdmissions != 0 || finalPending != 0 ||
                    !finalAdvanceDone || !finalRetryDone);
            if (timedOut)
            {
                lock (_gate)
                {
                    if (!_failClosed)
                    {
                        _failClosed = true;
                        _failureReason = finalPending != 0
                            ? "IngressDrainDeadlineExceeded"
                            : "IngressDrainWorkersIncomplete";
                    }
                }
            }
            var finalFailClosed = false;
            lock (_gate) finalFailClosed = _failClosed;
            return new RuntimeCallbackIngressDrainReceipt(true,
                finalActive == 0 && finalAdmissions == 0,
                timedOut, finalActive, finalAdmissions, finalPending,
                DateTime.UtcNow - started, finalAdvanceDone, finalRetryDone,
                finalPending == 0 && string.IsNullOrEmpty(pumpFault), finalFailClosed);
        }

        private static bool SafeWorkersTerminal(Func<bool> workersTerminal)
        {
            try { return workersTerminal(); }
            catch { return false; }
        }

        internal RuntimeCallbackIngressSnapshot Capture()
        {
            lock (_gate)
            {
                return new RuntimeCallbackIngressSnapshot(
                    _failClosed ? RuntimeCallbackPipelineState.FailClosed :
                    _closing ? RuntimeCallbackPipelineState.Closing :
                    _active ? RuntimeCallbackPipelineState.Ready : RuntimeCallbackPipelineState.Unbound,
                    _active, _closing, _disposed, _failClosed, _failureReason,
                    _frozenCorrelationId, _frozenReasonCode, _frozenRequestId,
                    _pipeSequence, _durableVersion, PendingCount, _activeProducers,
                    _canonical?.PipeDetail, _canonical?.DurableDetail,
                    _canonical?.ObservedSources ?? WatchdogStopAllSourceFlags.None,
                    _canonical?.AdmittedSources ?? WatchdogStopAllSourceFlags.None,
                    _canonical?.PipeAdmissionReserved ?? false,
                    _canonical?.DurableAdmissionReserved ?? false,
                    _activeAdmissions,
                    _canonical?.PipeAdmissionReservationId ?? 0,
                    _canonical?.DurableAdmissionReservationId ?? 0,
                    _activeDrainToken);
            }
        }

        internal int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    if (_canonical == null) return 0;
                    var pending = _canonical.ObservedSources & ~_canonical.AdmittedSources;
                    var count = 0;
                    if ((pending & WatchdogStopAllSourceFlags.Pipe) != 0) count++;
                    if ((pending & WatchdogStopAllSourceFlags.Durable) != 0) count++;
                    return count;
                }
            }
        }

        internal void MarkFailClosed(string reason)
        {
            lock (_gate)
            {
                _failClosed = true;
                if (string.IsNullOrWhiteSpace(_failureReason))
                    _failureReason = reason ?? "PipelineFailClosed";
            }
        }

        private void Release(RuntimeCallbackIngressLease lease)
        {
            lock (_gate)
            {
                if (_activeProducers > 0) _activeProducers--;
                if (_activeProducers == 0) _drainCompletion?.TrySetResult(true);
            }
        }

        private static int ToMilliseconds(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) return 0;
            return (int)Math.Min(int.MaxValue, Math.Max(1, timeout.TotalMilliseconds));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _closing = true;
                _acceptingClosed = true;
                _activeDrainToken = 0;
            }
        }

        internal sealed class RuntimeCallbackIngressLease : IDisposable
        {
            private readonly RuntimeCallbackIngressGate _owner;
            private readonly RuntimeTransportSessionContext _context;
            private readonly string _sessionId;
            private readonly long _sessionGeneration;
            private int _disposed;

            internal RuntimeCallbackIngressLease(RuntimeCallbackIngressGate owner, RuntimeTransportSessionContext context)
            {
                _owner = owner;
                _context = context;
                _sessionId = context.SessionId;
                _sessionGeneration = context.SessionGeneration;
            }

            internal bool IsExact(RuntimeTransportSessionContext context) =>
                Volatile.Read(ref _disposed) == 0 && ReferenceEquals(_context, context) &&
                string.Equals(_sessionId, context?.SessionId, StringComparison.Ordinal) &&
                _sessionGeneration == context?.SessionGeneration;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _owner.Release(this);
            }
        }
    }

    internal sealed class RuntimeCallbackIngressDrainReceipt
    {
        internal bool AcceptingClosed { get; }
        internal bool AllProducersReleased { get; }
        internal bool TimedOut { get; }
        internal int ActiveProducers { get; }
        internal int ActiveAdmissions { get; }
        internal bool AllAdmissionsReleased => ActiveAdmissions == 0;
        internal int PendingRequests { get; }
        internal TimeSpan Elapsed { get; }
        internal bool AdvanceTerminal { get; }
        internal bool RetryTerminal { get; }
        internal bool IngressFailClosed { get; }
        internal bool OutboxDrained => PendingRequests == 0 && ActiveAdmissions == 0;
        internal bool DrainTerminal => AllProducersReleased && OutboxDrained &&
            AdvanceTerminal && RetryTerminal;

        internal RuntimeCallbackIngressDrainReceipt(bool acceptingClosed, bool allProducersReleased,
            bool timedOut, int activeProducers, int pendingRequests, TimeSpan elapsed)
            : this(acceptingClosed, allProducersReleased, timedOut, activeProducers, 0,
                pendingRequests, elapsed, true, true, pendingRequests == 0)
        {
        }

        internal RuntimeCallbackIngressDrainReceipt(bool acceptingClosed, bool allProducersReleased,
            bool timedOut, int activeProducers, int activeAdmissions, int pendingRequests, TimeSpan elapsed,
            bool advanceTerminal = true, bool retryTerminal = true, bool outboxDrained = false,
            bool ingressFailClosed = false)
        {
            AcceptingClosed = acceptingClosed;
            AllProducersReleased = allProducersReleased;
            TimedOut = timedOut;
            ActiveProducers = activeProducers;
            ActiveAdmissions = activeAdmissions;
            PendingRequests = pendingRequests;
            Elapsed = elapsed;
            AdvanceTerminal = advanceTerminal;
            RetryTerminal = retryTerminal;
            IngressFailClosed = ingressFailClosed;
        }
    }

    internal sealed class RuntimeSafetyTargetReservation
    {
        internal RuntimeTransportSessionContext Context { get; }
        internal string TargetId { get; }
        internal IWatchdogCallbackPostTarget Target { get; }
        internal bool TransferOwnership { get; }
        internal long ReservationId { get; }
        internal long PipelineGeneration { get; }

        internal RuntimeSafetyTargetReservation(RuntimeTransportSessionContext context,
            string targetId, IWatchdogCallbackPostTarget target, bool transferOwnership,
            long reservationId, long pipelineGeneration)
        {
            Context = context;
            TargetId = targetId ?? string.Empty;
            Target = target;
            TransferOwnership = transferOwnership;
            ReservationId = reservationId;
            PipelineGeneration = pipelineGeneration;
        }
    }

    internal sealed class RuntimeSafetyTargetLease : IDisposable
    {
        private readonly RuntimeTransportSessionContext _context;
        private readonly IWatchdogCallbackPostTarget _target;
        private int _disposed;
        private int _ownedTargetDisposed;
        internal string TargetId { get; }
        internal bool TransferOwnership { get; }
        internal long ReservationId { get; }
        internal long PipelineGeneration { get; }
        internal bool Accepted { get; }
        internal string RejectionReason { get; }
        internal RuntimeTransportSessionContext Context => _context;
        internal IWatchdogCallbackPostTarget Target => _target;
        internal bool IsActive => Accepted && Volatile.Read(ref _disposed) == 0 &&
            _context != null && _context.IsCurrentSafetyTargetLease(this);

        internal RuntimeSafetyTargetLease(RuntimeTransportSessionContext context, string targetId,
            IWatchdogCallbackPostTarget target, bool transferOwnership, long reservationId,
            long pipelineGeneration)
        {
            _context = context;
            TargetId = targetId ?? string.Empty;
            _target = target;
            TransferOwnership = transferOwnership;
            ReservationId = reservationId;
            PipelineGeneration = pipelineGeneration;
            Accepted = context != null && target != null && reservationId > 0;
            RejectionReason = string.Empty;
        }

        private RuntimeSafetyTargetLease(string rejectionReason)
        {
            RejectionReason = rejectionReason ?? "Rejected";
            TargetId = string.Empty;
            TransferOwnership = false;
            ReservationId = 0;
            PipelineGeneration = 0;
            Accepted = false;
        }

        internal static RuntimeSafetyTargetLease Rejected(string reason)
        {
            return new RuntimeSafetyTargetLease(reason);
        }

        internal bool Matches(string targetId, IWatchdogCallbackPostTarget target) =>
            IsActive && string.Equals(TargetId, targetId?.Trim(), StringComparison.Ordinal) && ReferenceEquals(_target, target);

        /// <summary>
        /// Releases a lease admitted by a binding transaction which never
        /// reached the owning Main adapter.  This is intentionally distinct
        /// from Dispose(): an application-owned active lease is still a
        /// safety fault when disposed, while an unpublished transaction is
        /// closed through the exact Runtime context shutdown path.
        /// </summary>
        internal bool ReleaseUnpublished()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return false;
            return _context != null && _context.TryReleaseUnpublishedSafetyTarget(this);
        }

        internal void DisposeOwnedTarget()
        {
            if (!TransferOwnership || Interlocked.Exchange(ref _ownedTargetDisposed, 1) != 0) return;
            try { (_target as IDisposable)?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_context != null)
                _context.TryFailPipeline(PipelineGeneration, "SafetyTargetLeaseDisposed");
        }
    }

    internal enum RuntimeHandlerBindingStatus
    {
        Reserved = 0,
        Binding = 1,
        Bound = 2,
        Rejected = 3,
        Disposed = 4
    }

    internal sealed class RuntimePendingHandlerRegistration
    {
        private readonly object _gate = new object();
        private WatchdogStopAllRegistrationLease _actual;
        private readonly TaskCompletionSource<WatchdogStopAllRegistrationLease> _bindingCompletion =
            new TaskCompletionSource<WatchdogStopAllRegistrationLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bindingStatus = (int)RuntimeHandlerBindingStatus.Reserved;
        internal string SubscriberId { get; }
        internal RuntimeSafetyTargetLease TargetLease { get; }
        internal RuntimeTransportSessionContext Context { get; }
        internal long ReservationId { get; private set; }
        internal long PipelineGeneration => TargetLease?.PipelineGeneration ?? 0;
        internal bool Accepted { get; private set; }
        internal string RejectionReason { get; private set; }
        internal Func<WatchdogStopAllOfferEnvelope, Task> Handler { get; }
        internal RuntimeHandlerBindingStatus Status =>
            (RuntimeHandlerBindingStatus)Volatile.Read(ref _bindingStatus);
        internal bool IsDisposed => Status == RuntimeHandlerBindingStatus.Disposed;
        internal bool IsBound => Status == RuntimeHandlerBindingStatus.Bound;
        internal bool ReservationAccepted => Accepted;
        internal string FailureKind => RejectionReason ?? string.Empty;
        internal Task<WatchdogStopAllRegistrationLease> BindingCompletion => _bindingCompletion.Task;

        internal RuntimePendingHandlerRegistration(string subscriberId, RuntimeSafetyTargetLease targetLease,
            Func<WatchdogStopAllOfferEnvelope, Task> handler, RuntimeTransportSessionContext context,
            long reservationId)
        {
            SubscriberId = subscriberId ?? string.Empty;
            TargetLease = targetLease;
            Handler = handler;
            Context = context;
            ReservationId = reservationId;
            Accepted = context != null && targetLease != null && reservationId > 0;
            RejectionReason = Accepted ? string.Empty : "HandlerReservationRejected";
        }

        internal bool TryBind(WatchdogStopAllRegistrationLease registration)
        {
            if (registration == null || !registration.Accepted)
            {
                try { registration?.Dispose(); } catch { }
                TryReject("CoordinatorRegistrationRejected");
                return false;
            }
            if (Interlocked.CompareExchange(ref _bindingStatus,
                    (int)RuntimeHandlerBindingStatus.Binding,
                    (int)RuntimeHandlerBindingStatus.Reserved) != (int)RuntimeHandlerBindingStatus.Reserved)
            {
                try { registration.Dispose(); } catch { }
                return false;
            }
            lock (_gate)
            {
                if (Status != RuntimeHandlerBindingStatus.Binding || _actual != null)
                {
                    try { registration.Dispose(); } catch { }
                    return false;
                }
                _actual = registration;
            }
            if (Interlocked.CompareExchange(ref _bindingStatus,
                    (int)RuntimeHandlerBindingStatus.Bound,
                    (int)RuntimeHandlerBindingStatus.Binding) != (int)RuntimeHandlerBindingStatus.Binding)
            {
                try { registration.Dispose(); } catch { }
                return false;
            }
            _bindingCompletion.TrySetResult(registration);
            return true;
        }

        internal void Dispose()
        {
            while (true)
            {
                var state = Volatile.Read(ref _bindingStatus);
                if (state == (int)RuntimeHandlerBindingStatus.Disposed) return;
                if (Interlocked.CompareExchange(ref _bindingStatus,
                        (int)RuntimeHandlerBindingStatus.Disposed, state) == state) break;
            }
            WatchdogStopAllRegistrationLease actual;
            lock (_gate) actual = _actual;
            try { actual?.Dispose(); } catch { }
            _bindingCompletion.TrySetResult(null);
        }

        private void TryReject(string reason)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(RejectionReason))
                    RejectionReason = reason ?? "HandlerRejected";
            }
            if (Interlocked.CompareExchange(ref _bindingStatus,
                    (int)RuntimeHandlerBindingStatus.Rejected,
                    (int)RuntimeHandlerBindingStatus.Binding) == (int)RuntimeHandlerBindingStatus.Binding ||
                Interlocked.CompareExchange(ref _bindingStatus,
                    (int)RuntimeHandlerBindingStatus.Rejected,
                    (int)RuntimeHandlerBindingStatus.Reserved) == (int)RuntimeHandlerBindingStatus.Reserved)
                _bindingCompletion.TrySetResult(null);
        }
    }

    internal sealed class RuntimeShutdownReceipt
    {
        internal long ClosingAttempt { get; }
        internal ShutdownReceipt EngineReceipt { get; }
        internal RuntimeCallbackIngressDrainReceipt IngressReceipt { get; }
        internal WatchdogStopAllDrainReceipt StopAllReceipt { get; }
        internal WatchdogRuntimeCallbackDrainReceipt CallbackReceipt { get; }
        internal RuntimeCallbackPipelineState PipelineState { get; }
        internal string TerminalReason { get; }
        internal long RetentionVersion { get; }
        internal string SessionId { get; }
        internal long SessionGeneration { get; }
        internal long SessionLease { get; }
        internal bool Retained { get; }
        internal bool JournalFlushCompleted { get; }
        internal bool JournalDisposed { get; }
        internal bool PreviousRuntimeShutdownIncomplete { get; }
        /// <summary>
        /// True only after the closing tombstone has been durably completed.
        /// A detached transport alone is insufficient to authorize process
        /// exit because a later startup could otherwise re-attach the old
        /// session identity.
        /// </summary>
        internal bool SessionClosingPersisted { get; }
        /// <summary>
        /// Fixture-only cleanup is deliberately distinguishable from a
        /// production terminal receipt.  It may release worker objects so a
        /// test process does not leak, but it never manufactures safety
        /// evidence or a terminal outcome.
        /// </summary>
        internal bool TestAbandoned { get; }
        internal bool SessionDetached => EngineReceipt?.SessionDetached == true;
        internal bool AllWorkersTerminal => EngineReceipt?.AllWorkersTerminal == true &&
            (CallbackReceipt == null || CallbackReceipt.AllWorkersTerminal);
        /// <summary>Worker/process/dispatcher resources are gone, independent
        /// of whether the observed safety request has been resolved.</summary>
        internal bool WorkerResourcesReleased =>
            (EngineReceipt == null || EngineReceipt.AllResourcesReleased) &&
            (IngressReceipt == null ||
                (IngressReceipt.AllProducersReleased &&
                 IngressReceipt.AllAdmissionsReleased &&
                 IngressReceipt.AdvanceTerminal && IngressReceipt.RetryTerminal)) &&
            (StopAllReceipt == null || StopAllReceipt.AllResourcesReleased) &&
            (CallbackReceipt == null || CallbackReceipt.AllResourcesReleased);
        /// <summary>
        /// Current production has no authoritative takeover/SafeIdle receipt
        /// channel.  Therefore an observed source is resolved only when its
        /// exact observed-minus-admitted ledger is empty.  This intentionally
        /// leaves FailClosed with retained Pending evidence non-terminal.
        /// </summary>
        internal bool SafetyEvidenceResolved =>
            !TestAbandoned && (IngressReceipt == null || IngressReceipt.PendingRequests == 0);
        /// <summary>
        /// Pipeline-owned resource evidence.  This intentionally excludes
        /// the Runtime journal boundary and the retained owner; callers that
        /// need the complete Runtime result must use <see cref="IsTerminal"/>.
        /// </summary>
        internal bool PipelineResourcesReleased =>
            WorkerResourcesReleased && SafetyEvidenceResolved;
        /// <summary>
        /// Terminal evidence owned by the Engine and callback pipeline only.
        /// Journal flushing/disposal and the process-retention owner are
        /// deliberately outside this predicate; they are the final Runtime
        /// shutdown stage and are represented by <see cref="IsTerminal"/>.
        /// </summary>
        internal bool PipelineTerminal =>
            WatchdogRuntime.IsExactEngineTerminal(SessionLease, EngineReceipt) &&
            PipelineState == RuntimeCallbackPipelineState.Terminal &&
            PipelineResourcesReleased;
        internal bool AllResourcesReleased => IsTerminal;
        internal bool PipelineNotCreatedNoWork => PipelineState == RuntimeCallbackPipelineState.Terminal &&
            !TestAbandoned && string.Equals(TerminalReason, "PipelineNotCreatedNoWork", StringComparison.Ordinal);
        internal bool RequiredSafetySubscriberMissing =>
            string.Equals(TerminalReason, "RequiredSafetySubscriberMissing", StringComparison.Ordinal);
        /// <summary>
        /// Complete process-level terminal evidence.  A pipeline can be
        /// terminal while its frozen journal is still pending; callers must
        /// not clear the retained owner until both journal boundaries have
        /// completed.
        /// </summary>
        internal bool IsTerminal => PipelineTerminal &&
            JournalFlushCompleted && JournalDisposed && !Retained && !TestAbandoned;

        /// <summary>
        /// Physical engine ownership, callback workers and the safety ledger
        /// have all reached terminal state.  A journal retry may still be
        /// retained, but it is no longer safe for it to hold the WinForms
        /// process hostage: the closing session cannot receive or issue a
        /// safety command again.
        /// </summary>
        internal bool SafeExitAllowed => !TestAbandoned &&
            !PreviousRuntimeShutdownIncomplete && SessionClosingPersisted && PipelineTerminal &&
            (SessionDetached || SessionLease == 0);

        internal RuntimeShutdownDisposition Disposition => IsTerminal
            ? RuntimeShutdownDisposition.Terminal
            : SafeExitAllowed
                ? RuntimeShutdownDisposition.DetachedRetained
                : RuntimeShutdownDisposition.BlockingFailure;

        internal bool IsCloseAuthorized => Disposition == RuntimeShutdownDisposition.Terminal ||
            Disposition == RuntimeShutdownDisposition.DetachedRetained;

        internal bool IsStickyBlockingFailure =>
            Disposition == RuntimeShutdownDisposition.BlockingFailure &&
            string.Equals(
                TerminalReason,
                "ShutdownIdentityMismatch",
                StringComparison.Ordinal);

        internal RuntimeShutdownReceipt(long closingAttempt, ShutdownReceipt engineReceipt,
            RuntimeCallbackIngressDrainReceipt ingressReceipt, WatchdogStopAllDrainReceipt stopAllReceipt,
            WatchdogRuntimeCallbackDrainReceipt callbackReceipt, RuntimeCallbackPipelineState pipelineState,
            string terminalReason, bool testAbandoned = false,
            long retentionVersion = 0, string sessionId = null,
            long sessionGeneration = 0, long sessionLease = 0,
            bool retained = false, bool journalFlushCompleted = false,
            bool journalDisposed = false,
            bool previousRuntimeShutdownIncomplete = false,
            bool sessionClosingPersisted = true)
        {
            ClosingAttempt = closingAttempt;
            EngineReceipt = engineReceipt;
            IngressReceipt = ingressReceipt;
            StopAllReceipt = stopAllReceipt;
            CallbackReceipt = callbackReceipt;
            PipelineState = pipelineState;
            TerminalReason = terminalReason ?? string.Empty;
            TestAbandoned = testAbandoned;
            RetentionVersion = retentionVersion;
            SessionId = sessionId ?? string.Empty;
            SessionGeneration = sessionGeneration;
            SessionLease = sessionLease;
            Retained = retained;
            JournalFlushCompleted = journalFlushCompleted;
            JournalDisposed = journalDisposed;
            PreviousRuntimeShutdownIncomplete = previousRuntimeShutdownIncomplete;
            SessionClosingPersisted = sessionClosingPersisted;
        }

        internal RuntimeShutdownReceipt AsTestAbandoned()
        {
            if (TestAbandoned) return this;
            return new RuntimeShutdownReceipt(ClosingAttempt, EngineReceipt, IngressReceipt,
                StopAllReceipt, CallbackReceipt, PipelineState,
                TerminalReason, true, RetentionVersion, SessionId,
                SessionGeneration, SessionLease, Retained,
                JournalFlushCompleted, JournalDisposed,
                PreviousRuntimeShutdownIncomplete, SessionClosingPersisted);
        }

        internal RuntimeShutdownReceipt WithRetention(long retentionVersion,
            bool retained, bool journalFlushCompleted, bool journalDisposed,
            bool previousRuntimeShutdownIncomplete = false,
            bool? sessionClosingPersisted = null)
        {
            return new RuntimeShutdownReceipt(ClosingAttempt, EngineReceipt,
                IngressReceipt, StopAllReceipt, CallbackReceipt, PipelineState,
                TerminalReason, TestAbandoned, retentionVersion, SessionId,
                SessionGeneration, SessionLease, retained,
                journalFlushCompleted, journalDisposed,
                previousRuntimeShutdownIncomplete,
                sessionClosingPersisted ?? SessionClosingPersisted);
        }
    }

    internal sealed class RuntimeCallbackDispatcherSnapshot
    {
        internal bool Created { get; }
        internal bool Closing { get; }
        internal bool WorkerAlive { get; }
        internal int SafetyQueueCount { get; }
        internal int SafetyPostingCount { get; }
        internal int SafetyActiveCount { get; }
        internal int DiagnosticQueueCount { get; }
        internal int DiagnosticPostingCount { get; }
        internal int DiagnosticActiveCount { get; }
        internal int InFlightCount { get; }
        internal int ActiveOfferProducerCount { get; }
        internal long DiagnosticRejectedCount { get; }
        internal long DiagnosticDroppedCount { get; }
        internal long DiagnosticCoalescedCount { get; }
        internal long SpaceAvailableFailureCount { get; }
        internal long AuditFailureCount { get; }

        internal RuntimeCallbackDispatcherSnapshot(WatchdogRuntimeCallbackDispatch dispatch)
        {
            Created = dispatch != null;
            if (dispatch == null) return;
            Closing = dispatch.IsClosing;
            WorkerAlive = dispatch.IsWorkerAlive;
            SafetyQueueCount = dispatch.SafetyQueueCount;
            SafetyPostingCount = dispatch.SafetyPostingCount;
            SafetyActiveCount = dispatch.SafetyActiveCount;
            DiagnosticQueueCount = dispatch.DiagnosticQueueCount;
            DiagnosticPostingCount = dispatch.DiagnosticPostingCount;
            DiagnosticActiveCount = dispatch.DiagnosticActiveCount;
            InFlightCount = dispatch.InFlightCount;
            ActiveOfferProducerCount = dispatch.ActiveOfferProducerCount;
            DiagnosticRejectedCount = dispatch.DiagnosticRejectedCount;
            DiagnosticDroppedCount = dispatch.DiagnosticDroppedCount;
            DiagnosticCoalescedCount = dispatch.DiagnosticCoalescedCount;
            SpaceAvailableFailureCount = dispatch.SpaceAvailableFailureCount;
            AuditFailureCount = dispatch.AuditFailureCount;
        }
    }

    internal sealed class RuntimeCallbackPipelineSnapshot
    {
        internal RuntimeCallbackPipelineState State { get; }
        internal RuntimeValidatedAttachIdentity AttachIdentity { get; }
        internal RuntimeSafetyTargetLease SafetyTarget { get; }
        internal WatchdogStopAllScopeLease ScopeLease { get; }
        internal bool DispatcherCreated { get; }
        internal bool CoordinatorCreated { get; }
        internal int HandlerCount { get; }
        internal int UnboundHandlerCount { get; }
        internal RuntimeCallbackIngressSnapshot Ingress { get; }
        internal long ClosingAttempt { get; }
        internal long PipelineGeneration { get; }
        internal long SafetyTargetReservationId { get; }
        internal RuntimeCallbackDispatcherSnapshot Dispatcher { get; }
        internal bool AdvanceTerminal { get; }
        internal bool RetryTerminal { get; }
        internal long RetryGeneration { get; }
        internal string FailureReason { get; }

        internal RuntimeCallbackPipelineSnapshot(RuntimeTransportSessionContext context)
        {
            State = context?.PipelineState ?? RuntimeCallbackPipelineState.Terminal;
            AttachIdentity = context?.ValidatedAttachIdentity;
            SafetyTarget = context?.SafetyTargetLease;
            ScopeLease = context?.ScopeLease;
            DispatcherCreated = context?.CallbackDispatch != null;
            CoordinatorCreated = context?.StopAllCoordinator != null;
            var handlers = context?.SnapshotPendingHandlers() ?? Array.Empty<RuntimePendingHandlerRegistration>();
            HandlerCount = handlers.Count(item => !item.IsDisposed &&
                item.Status != RuntimeHandlerBindingStatus.Rejected);
            UnboundHandlerCount = handlers.Count(item => !item.IsDisposed &&
                item.Status == RuntimeHandlerBindingStatus.Reserved);
            Ingress = context?.IngressGate?.Capture();
            ClosingAttempt = context?.ClosingAttempt ?? 0;
            PipelineGeneration = context?.PipelineGeneration ?? 0;
            SafetyTargetReservationId = context?.SafetyTargetReservation?.ReservationId ?? 0;
            Dispatcher = new RuntimeCallbackDispatcherSnapshot(context?.CallbackDispatch);
            AdvanceTerminal = context?.IsPipelineAdvanceTerminal ?? true;
            RetryTerminal = context?.IsPipelineRetryTerminal ?? true;
            RetryGeneration = context?.RetryGeneration ?? 0;
            FailureReason = context?.PipelineFailureReason ?? string.Empty;
        }
    }

    internal static partial class WatchdogRuntime
    {
        private static WatchdogClientTransportCallbacks BuildCallbackPipelineCallbacks(RuntimeTransportSessionContext context)
        {
            return new WatchdogClientTransportCallbacks
            {
                CreateRunSession = (recovery, attempt) => CreateRunSession(context, recovery, attempt),
                CaptureHeartbeat = () => CaptureHeartbeat(context),
                RecordEvent = (eventType, detail) => RecordClientEvent(context, eventType, detail),
                TransportError = (reason, detail) => RaiseTransportError(context, reason, detail),
                TransportLost = (reason, detail) => RaiseTransportLost(context, reason, detail),
                StopAllRequested = (reason, correlation) => PublishStopAllFromPipe(context, reason, correlation),
                RecoveryFailureReceiptReceived = message =>
                    ObserveRecoveryFailureReceipt(context, message),
                ObserveDurableStopMarker = () => PublishDurableStopMarker(context)
            };
        }

        private static void ActivateCallbackPipeline(RuntimeTransportSessionContext context,
            WatchdogClientTransportSnapshot engine)
        {
            if (context == null || engine == null ||
                !IsExactAttached(new RuntimeTransportSnapshot(context, engine, false))) return;
            var identity = new RuntimeValidatedAttachIdentity(
                context.SessionId,
                context.SessionGeneration,
                context.SessionLease,
                engine.AttachedConnectionGeneration,
                engine.AuthorityProcessId,
                engine.AuthorityProcessStartUtcTicks,
                engine.AuthorityInstanceNonce);
            ActivateCallbackPipelineExact(context, identity);
        }

        /// <summary>
        /// Production activation transaction after the transport exact
        /// Attached gate.  The runtime facade and deterministic test seam both
        /// call this method; no caller is allowed to duplicate the identity
        /// bind/advance sequence.
        /// </summary>
        internal static bool ActivateCallbackPipelineExact(
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity identity)
        {
            if (context == null || identity == null ||
                !string.Equals(context.SessionId, identity.SessionId, StringComparison.Ordinal) ||
                context.SessionGeneration != identity.SessionGeneration ||
                context.SessionLease != identity.SessionLease)
                return false;
            if (!context.TryBindValidatedAttachIdentity(identity))
            {
                context.TryFailPipeline(context.PipelineGeneration, "ValidatedAttachIdentityRejected");
                return false;
            }
            TryAdvancePipeline(context);
            return context.PipelineState == RuntimeCallbackPipelineState.Ready;
        }

        /// <summary>
        /// Reserves target identity under the context gate, creates the real
        /// dispatcher/coordinator outside that gate, and publishes the pair
        /// only if the reservation still belongs to this call.
        /// </summary>
        internal static RuntimeSafetyTargetLease BindStopAllSafetyTarget(string targetId,
            IWatchdogCallbackPostTarget target, bool transferOwnership)
        {
            return BindStopAllSafetyTarget(CaptureContext(), targetId, target, transferOwnership);
        }

        // Context overload used by the production callback-pipeline seam and
        // deterministic tests.  It is the same transaction as the
        // application entry point; it never consults the process-global
        // active-context slot.
        internal static RuntimeSafetyTargetLease BindStopAllSafetyTarget(
            RuntimeTransportSessionContext context, string targetId,
            IWatchdogCallbackPostTarget target, bool transferOwnership)
        {
            if (string.IsNullOrWhiteSpace(targetId) || target == null)
                return RuntimeSafetyTargetLease.Rejected("SafetyTargetMissing");
            if (context == null) return RuntimeSafetyTargetLease.Rejected("RuntimeContextMissing");
            var existing = context.SafetyTargetLease;
            if (existing != null)
                return existing.Matches(targetId, target)
                    ? existing
                    : RuntimeSafetyTargetLease.Rejected("SafetyTargetAlreadyBound");
            if (!context.TryReserveSafetyTarget(targetId, target, transferOwnership, out var reservation))
                return RuntimeSafetyTargetLease.Rejected("SafetyTargetReservationRejected");
            WatchdogRuntimeCallbackDispatch dispatch = null;
            WatchdogStopAllOfferCoordinator coordinator = null;
            try
            {
                dispatch = new WatchdogRuntimeCallbackDispatch(new WatchdogRuntimeCallbackDispatchOptions
                {
                    SafetyPostTarget = target,
                    // Diagnostics have their own owned lane target.  A
                    // safety target is never shared with the diagnostic lane.
                    DiagnosticPostTarget = null
                });
                coordinator = new WatchdogStopAllOfferCoordinator(dispatch);
                if (!context.TryPublishPipelineComponents(reservation, dispatch, coordinator,
                    out var targetLease))
                {
                    try { coordinator.Dispose(); } catch { }
                    try { dispatch.Dispose(); } catch { }
                    context.AbortSafetyTargetReservation(reservation, "SafetyTargetReservationLost");
                    if (reservation.TransferOwnership) try { (target as IDisposable)?.Dispose(); } catch { }
                    return RuntimeSafetyTargetLease.Rejected("SafetyTargetReservationLost");
                }
                TryAdvancePipeline(context);
                return targetLease;
            }
            catch (Exception ex)
            {
                try { coordinator?.Dispose(); } catch { }
                try { dispatch?.Dispose(); } catch { }
                context.AbortSafetyTargetReservation(reservation, "SafetyTargetBindFault:" + ex.GetType().Name);
                if (reservation.TransferOwnership) try { (target as IDisposable)?.Dispose(); } catch { }
                return RuntimeSafetyTargetLease.Rejected("SafetyTargetBindFault:" + ex.GetType().Name);
            }
        }

        internal static RuntimeStopAllHandlerLease RegisterStopAllHandler(RuntimeSafetyTargetLease targetLease,
            string subscriberId, Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            return RegisterStopAllHandler(CaptureContext(), targetLease, subscriberId, handler);
        }

        internal static RuntimeStopAllHandlerLease RegisterStopAllHandler(
            RuntimeTransportSessionContext context, RuntimeSafetyTargetLease targetLease,
            string subscriberId, Func<WatchdogStopAllOfferEnvelope, Task> handler)
        {
            if (context == null || targetLease == null || !targetLease.IsActive || handler == null ||
                !ReferenceEquals(targetLease, context.SafetyTargetLease))
                return RuntimeStopAllHandlerLease.Rejected("StopAllTargetLeaseMismatch");
            if (string.IsNullOrWhiteSpace(subscriberId))
                return RuntimeStopAllHandlerLease.Rejected("SubscriberIdMissing");
            if (!context.TryReservePendingHandler(targetLease, subscriberId.Trim(), handler, out var pending))
                return RuntimeStopAllHandlerLease.Rejected("HandlerReservationRejected");
            var lease = new RuntimeStopAllHandlerLease(context, pending);
            TryAdvancePipeline(context);
            return lease;
        }

        private static void TryAdvancePipeline(RuntimeTransportSessionContext context)
        {
            if (context == null || !context.TryEnterPipelineAdvance()) return;
            do
            {
                try { TryAdvancePipelineCore(context); }
                catch (Exception ex)
                {
                    context.TryFailPipeline(context.PipelineGeneration, ex.GetBaseException().Message);
                    try { RecordClientEvent(context, "StopAllPipelineAdvanceFault", ex.GetBaseException().Message); }
                    catch { }
                }
            }
            while (context.FinishPipelineAdvance());
        }

        internal static void TryAdvancePipelineForContext(RuntimeTransportSessionContext context)
        {
            TryAdvancePipeline(context);
        }

        private static void TryAdvancePipelineCore(RuntimeTransportSessionContext context)
        {
            if (context == null) return;
            WatchdogStopAllOfferCoordinator coordinator;
            RuntimeValidatedAttachIdentity identity;
            RuntimeSafetyTargetLease targetLease;
            long pipelineGeneration;
            bool activate = false;
            lock (context.CallbackGate)
            {
                var state = context.PipelineState;
                if (state == RuntimeCallbackPipelineState.Closing || state == RuntimeCallbackPipelineState.Terminal ||
                    state == RuntimeCallbackPipelineState.FailClosed) return;
                coordinator = context.StopAllCoordinator;
                identity = context.ValidatedAttachIdentity;
                targetLease = context.SafetyTargetLease;
                pipelineGeneration = context.PipelineGeneration;
                var hasHandler = context.SnapshotPendingHandlers().Any(item => !item.IsDisposed &&
                    item.Status != RuntimeHandlerBindingStatus.Rejected);
                if (state == RuntimeCallbackPipelineState.BoundInactive && targetLease != null && identity != null &&
                    coordinator != null && hasHandler)
                {
                    if (!context.TryMarkPipelineState(RuntimeCallbackPipelineState.BoundInactive,
                        RuntimeCallbackPipelineState.Activating, pipelineGeneration)) return;
                    activate = true;
                }
            }
            if (activate)
            {
                WatchdogStopAllScopeLease scopeLease = null;
                try
                {
                    scopeLease = coordinator.Activate(BuildScope(context, identity));
                    context.BindCallbackScope(scopeLease, pipelineGeneration);
                    RegisterPendingHandlers(context, coordinator, scopeLease);
                    if (!context.IsCurrentPipelineGeneration(pipelineGeneration)) return;
                    var buffered = context.IngressGate.ActivateAndTake(scopeLease);
                    // Test/diagnostic observation is deliberately between the
                    // real production state transitions; it cannot mutate
                    // the pipeline or create a second activation path.
                    context.AfterActivateAndTakeBeforeReadyProbe?.Invoke(context);
                    if (!context.TryMarkPipelineState(RuntimeCallbackPipelineState.Activating,
                        RuntimeCallbackPipelineState.Ready, pipelineGeneration)) return;
                    // Activation never consumes the canonical outbox.  Pump
                    // the snapshot produced by activation and then keep the
                    // same pump path for every subsequent Ready advance.
                    foreach (var request in buffered) OfferPendingStopAll(context, scopeLease, request);
                    PumpPendingIngress(context, scopeLease);
                    return;
                }
                catch (Exception ex)
                {
                    context.TryFailPipeline(pipelineGeneration, ex.GetBaseException().Message);
                    RecordClientEvent(context, "StopAllPipelineActivationFault", ex.GetBaseException().Message);
                    return;
                }
            }
            if (context.PipelineState == RuntimeCallbackPipelineState.Ready && coordinator != null && context.ScopeLease != null)
            {
                try
                {
                    RegisterPendingHandlers(context, coordinator, context.ScopeLease);
                    PumpPendingIngress(context, context.ScopeLease);
                }
                catch (Exception ex)
                {
                    context.TryFailPipeline(pipelineGeneration, ex.GetBaseException().Message);
                    RecordClientEvent(context, "StopAllHandlerRegistrationFault", ex.GetBaseException().Message);
                }
            }
        }

        private static void PumpPendingIngress(RuntimeTransportSessionContext context,
            WatchdogStopAllScopeLease scopeLease)
        {
            if (context == null || scopeLease == null || context.StopAllCoordinator == null) return;
            foreach (var request in context.IngressGate.CaptureLiveOutbox())
            {
                if (context.IngressGate.PendingCount == 0) break;
                OfferPendingStopAll(context, scopeLease, request);
            }
        }

        private static WatchdogStopAllExactScopeIdentity BuildScope(RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity identity)
        {
            var channels = context.SelectedChannels ?? Array.Empty<int>();
            var channel = channels.Length == 0 ? "ALL" : channels[0].ToString(CultureInfo.InvariantCulture);
            var group = channels.Length == 0 ? "ALL" : string.Join(",", channels.Select(item => item.ToString(CultureInfo.InvariantCulture)));
            var scopeId = Guid.NewGuid();
            return new WatchdogStopAllExactScopeIdentity(context.SessionId, context.SessionGeneration,
                identity.SessionId, identity.SessionGeneration, identity.SessionLease,
                identity.AuthorityGeneration, identity.AttachedConnectionGeneration,
                "WatchdogRuntime", group, channel, "StopAll", "Runtime", scopeId,
                identity.AuthorityProcessId, identity.AuthorityProcessStartUtcTicks,
                identity.AttachEpoch, scopeId);
        }

        private static void RegisterPendingHandlers(RuntimeTransportSessionContext context,
            WatchdogStopAllOfferCoordinator coordinator, WatchdogStopAllScopeLease scopeLease)
        {
            foreach (var pending in context.SnapshotPendingHandlers())
            {
                if (pending.IsDisposed || pending.IsBound ||
                    pending.Status == RuntimeHandlerBindingStatus.Rejected) continue;
                var registration = coordinator.RegisterSafety(scopeLease, pending.SubscriberId, pending.Handler);
                if (!pending.TryBind(registration) && !pending.IsDisposed)
                    throw new InvalidOperationException("StopAll handler lease binding failed.");
                if (!pending.IsDisposed && pending.IsBound)
                    context.AddCallbackHandlerLease(registration);
            }
        }

        private static void PublishStopAllFromPipe(RuntimeTransportSessionContext context,
            string reason, string correlation)
        {
            PublishStopAllIngress(context, reason, correlation, correlation,
                WatchdogStopAllSourceFlags.Pipe, "StopAllRequested");
        }

        private static bool PublishDurableStopMarker(RuntimeTransportSessionContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.SessionId) ||
                !WatchdogControlMarker.IsRevoked(context.JournalDirectory, context.SessionId)) return false;
            // A durable marker is an evidence source for the one canonical
            // request.  Its identity is deterministic; it is never a fresh
            // random correlation which could conflict with a pipe request.
            var correlation = "durable-stop:" + context.SessionId;
            return PublishStopAllIngress(context, "WatchdogDurableOperatorStopMarker", correlation, correlation,
                WatchdogStopAllSourceFlags.Durable, "DurableStopMarkerObserved");
        }

        private static bool PublishStopAllIngress(RuntimeTransportSessionContext context, string reason,
            string correlation, string requestId, WatchdogStopAllSourceFlags source, string eventType)
        {
            if (context == null || context.EngineShutdownReceiptObserved) return false;
            var ingress = context.IngressGate.TryAcquire(context);
            if (ingress == null) return false;
            try
            {
                RecordClientEvent(context, eventType, (reason ?? string.Empty) + ":" + (correlation ?? string.Empty));
                var routed = context.IngressGate.Route(ingress, context, reason, correlation, requestId,
                    reason ?? string.Empty, source);
                if (!routed.Accepted)
                {
                    if (routed.Conflict) context.TryFailPipeline(context.PipelineGeneration, routed.Reason);
                    RecordClientEvent(context, eventType + "Rejected", routed.Reason);
                    return false;
                }
                if (routed.Live)
                {
                    var scope = context.ScopeLease;
                    if (scope == null || context.StopAllCoordinator == null)
                    {
                        context.TryFailPipeline(context.PipelineGeneration, "StopAllPipelineUnavailable");
                        return false;
                    }
                    var admissionAccepted = OfferPendingStopAll(context, scope, routed.Request);
                    if (source == WatchdogStopAllSourceFlags.Durable && !admissionAccepted)
                        return false;
                }
                return true;
            }
            finally { ingress.Dispose(); }
        }

        internal static bool PublishStopAllIngressForContext(RuntimeTransportSessionContext context,
            string reason, string correlation, string requestId,
            WatchdogStopAllSourceFlags source, string eventType)
        {
            return PublishStopAllIngress(context, reason, correlation, requestId, source, eventType);
        }

        private static bool OfferPendingStopAll(RuntimeTransportSessionContext context,
            WatchdogStopAllScopeLease scopeLease, RuntimePendingStopRequest pending)
        {
            if (context == null || scopeLease == null || pending == null || context.StopAllCoordinator == null) return false;

            // Each source has its own outbox admission reservation.  The
            // reservation is held only while the coordinator is called outside
            // the ingress lock.  A coordinator result is authoritative: an
            // accepted/coalesced/pending request, including the explicit
            // RequiredSafetySubscriberMissing result, is committed because
            // the coordinator has retained the canonical request.  Only a
            // source-admission CAS race asks the current advance owner for an
            // immediate recheck; it never starts a timer/retry worker.
            var anyAccepted = false;
            var durableAccepted = false;
            var immediateRecheck = false;
            var sourceOrder = pending.FirstSource == WatchdogStopAllSourceFlags.Durable
                ? new[] { WatchdogStopAllSourceFlags.Durable, WatchdogStopAllSourceFlags.Pipe }
                : new[] { WatchdogStopAllSourceFlags.Pipe, WatchdogStopAllSourceFlags.Durable };
            foreach (var source in sourceOrder)
            {
                if ((pending.ObservedSources & source) == 0) continue;
                var admission = context.IngressGate.TryReserveAdmission(source);
                if (admission == null)
                {
                    // A reservation held by another producer is an explicit
                    // ownership boundary (the test seam also uses this to
                    // model a pre-attach admission).  Do not spin the single
                    // advance owner while that exact token is outstanding;
                    // its settlement/next source event will request the
                    // next production pass.
                    continue;
                }

                try
                {
                    var request = admission.Request;
                    var detail = source == WatchdogStopAllSourceFlags.Pipe
                        ? request.PipeDetail : request.DurableDetail;
                    var envelope = new WatchdogStopAllOfferEnvelope(scopeLease.Scope, scopeLease,
                        request.CorrelationId, request.ReasonCode, detail, source,
                        source == WatchdogStopAllSourceFlags.Pipe ? request.PipeSequence : 0,
                        source == WatchdogStopAllSourceFlags.Durable ? request.DurableVersion : 0,
                        request.RequestId);
                    var result = source == WatchdogStopAllSourceFlags.Durable
                        ? context.StopAllCoordinator.OfferDurable(scopeLease, envelope)
                        : context.StopAllCoordinator.OfferPipe(scopeLease, envelope);
                    if (result == null)
                        throw new InvalidOperationException("Coordinator returned a null StopAll offer result.");

                    switch (result.Status)
                    {
                        case WatchdogStopAllOfferStatus.Pending:
                        case WatchdogStopAllOfferStatus.Accepted:
                        case WatchdogStopAllOfferStatus.Coalesced:
                        case WatchdogStopAllOfferStatus.RequiredSafetySubscriberMissing:
                            // RequiredSafetySubscriberMissing is not a
                            // rejection.  The coordinator deliberately keeps
                            // that canonical request pending for a later
                            // subscriber, so the ingress evidence is consumed
                            // exactly once here.
                            if (admission.Commit())
                            {
                                anyAccepted = true;
                                if (source == WatchdogStopAllSourceFlags.Durable)
                                    durableAccepted = true;
                            }
                            else
                            {
                                immediateRecheck = true;
                            }
                            break;

                        case WatchdogStopAllOfferStatus.ConflictFailClosed:
                        case WatchdogStopAllOfferStatus.Closed:
                        case WatchdogStopAllOfferStatus.Invalid:
                        case WatchdogStopAllOfferStatus.Rejected:
                            // These outcomes are terminal for this pipeline
                            // generation.  Do not leave an ingress token in
                            // flight and do not schedule another attempt.
                            admission.Release();
                            FailPipelineForOffer(context, result.Status, result.Reason);
                            return false;

                        default:
                            admission.Release();
                            FailPipelineForOffer(context, result.Status, result.Reason);
                            return false;
                    }
                }
                catch (Exception ex)
                {
                    // The exact reservation is settled first; the pipeline is
                    // then sticky fail-closed and never retried.
                    try { admission.Release(); } catch { }
                    var detail = ex.GetBaseException().Message;
                    try { RecordClientEvent(context, "StopAllOfferFault", detail); }
                    catch { }
                    context.TryFailPipeline(context.PipelineGeneration,
                        "StopAllOffer:Exception:" + ex.GetBaseException().GetType().Name + ":" + detail);
                    return false;
                }
            }

            if (immediateRecheck)
                RequestImmediatePipelineAdvance(context);
            return (pending.ObservedSources & WatchdogStopAllSourceFlags.Durable) != 0
                ? durableAccepted
                : anyAccepted;
        }

        private static void FailPipelineForOffer(RuntimeTransportSessionContext context,
            WatchdogStopAllOfferStatus status, string reason)
        {
            var normalized = string.IsNullOrWhiteSpace(reason) ? "NoReason" : reason.Trim();
            try { RecordClientEvent(context, "StopAllOfferRejected", status + ":" + normalized); }
            catch { }
            context?.TryFailPipeline(context.PipelineGeneration,
                "StopAllOffer:" + status + ":" + normalized);
        }

        private static void RequestImmediatePipelineAdvance(RuntimeTransportSessionContext context)
        {
            if (context == null) return;
            var generation = context.PipelineGeneration;
            if (!context.IsCurrentPipelineGeneration(generation)) return;
            // When an advance owner is already active this only sets its
            // request bit.  Otherwise enter the one synchronous owner now;
            // there is intentionally no delayed retry path.
            if (!context.RequestPipelineAdvance())
                TryAdvancePipeline(context);
        }

        private static void PublishTransportDiagnostic(RuntimeTransportSessionContext context,
            string eventType, string reason, string detail, bool lost)
        {
            if (context == null || context.EngineShutdownReceiptObserved) return;
            RecordClientEvent(context, eventType, (reason ?? string.Empty) + ":" + (detail ?? string.Empty));
            var identity = context.ValidatedAttachIdentity;
            var dispatch = context.CallbackDispatch;
            if (identity == null || dispatch == null || context.PipelineState != RuntimeCallbackPipelineState.Ready) return;
            var callbackIdentity = new WatchdogRuntimeCallbackIdentity(
                identity.SessionId, identity.SessionGeneration, identity.SessionLease,
                identity.AuthorityGeneration, identity.AttachedConnectionGeneration,
                context.ScopeLease?.Scope?.ScopeId ?? Guid.NewGuid());
            var normalizedReason = reason ?? string.Empty;
            var normalizedDetail = detail ?? string.Empty;
            dispatch.OfferDiagnostic(new WatchdogRuntimeCallbackOffer(
                callbackIdentity,
                WatchdogRuntimeCallbackLane.Diagnostic,
                eventType,
                () =>
                {
                    InvokeDiagnosticHandlers(lost ? TransportLost : TransportError,
                        normalizedReason, normalizedDetail, context, eventType);
                    return Task.CompletedTask;
                },
                normalizedDetail,
                eventType + ":" + normalizedReason,
                normalizedReason));
        }

        private static void InvokeDiagnosticHandlers(Action<string, string> handlers, string reason,
            string detail, RuntimeTransportSessionContext context, string eventType)
        {
            if (handlers == null) return;
            foreach (var item in handlers.GetInvocationList())
            {
                try { ((Action<string, string>)item)(reason, detail); }
                catch (Exception ex) { RecordClientEvent(context, eventType + "HandlerFault", ex.GetBaseException().Message); }
            }
        }

        private static bool TryCaptureTakeoverContext(RuntimeTransportSessionContext context,
            out string correlation, out string reason)
        {
            correlation = string.Empty;
            reason = string.Empty;
            var scope = context?.ScopeLease;
            if (context == null || scope == null || context.StopAllCoordinator == null) return false;
            try
            {
                var snapshot = context.StopAllCoordinator.Capture(scope);
                var request = snapshot.CanonicalRequests
                    .OrderByDescending(item => item.CanonicalSequence)
                    .FirstOrDefault();
                if (request == null) return false;
                correlation = request.CorrelationId ?? string.Empty;
                reason = request.ReasonCode ?? string.Empty;
                return !string.IsNullOrWhiteSpace(correlation);
            }
            catch { return false; }
        }

        private static bool HasActiveTakeover(RuntimeTransportSessionContext context)
        {
            return TryCaptureTakeoverContext(context, out _, out _);
        }

        private static RuntimeShutdownReceipt CloseCallbackPipelineCore(
            RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt,
            TimeSpan budget, string abandonReason, bool testAbandoned = false)
        {
            if (context == null)
                return new RuntimeShutdownReceipt(0, engineReceipt, null, null, null,
                    RuntimeCallbackPipelineState.Terminal, "PipelineNotCreatedNoWork",
                    journalFlushCompleted: true, journalDisposed: true);
            var cached = context.CachedShutdownReceipt;
            // A fixture abandon may deliberately re-enter the shared close
            // boundary after a production FailClosed receipt in order to
            // release test-owned workers.  Normal production re-entry remains
            // idempotent and returns the cached receipt.
            if (cached != null && (cached.PipelineTerminal || cached.TestAbandoned)) return cached;
            if (budget < TimeSpan.Zero) budget = TimeSpan.Zero;
            if (budget == TimeSpan.Zero) budget = TimeSpan.FromSeconds(5);
            context.TrySetPipelineState(RuntimeCallbackPipelineState.Closing);
            context.IngressGate.BeginClosing();
            var dispatch = context.CallbackDispatch;
            var coordinator = context.StopAllCoordinator;
            var scope = context.ScopeLease;
            Func<RuntimePendingStopRequest[], bool> pump = null;
            if (coordinator != null && scope != null)
            {
                pump = requests =>
                {
                    foreach (var request in requests ?? Array.Empty<RuntimePendingStopRequest>())
                        OfferPendingStopAll(context, scope, request);
                    return context.IngressGate.PendingCount == 0;
                };
            }
            // Close first rejects new producers, then lets the explicit drain
            // token finish observed-but-unadmitted sources.  Only after this
            // receipt is stable may the coordinator seal its scope.
            var ingressReceipt = context.IngressGate.CloseAndDrain(budget,
                pump, () => context.IsPipelineAdvanceTerminal,
                () => context.IsPipelineRetryTerminal);
            WatchdogStopAllDrainReceipt stopReceipt = null;
            WatchdogRuntimeCallbackDrainReceipt callbackReceipt = null;
            RuntimeCallbackPipelineState finalState;
            string reason;
            var stickyFailureReason = context.PipelineFailureReason;
            if (dispatch == null || coordinator == null || scope == null)
            {
                if (ingressReceipt.DrainTerminal && ingressReceipt.PendingRequests == 0)
                {
                    finalState = RuntimeCallbackPipelineState.Terminal;
                    reason = string.IsNullOrWhiteSpace(stickyFailureReason)
                        ? (string.IsNullOrWhiteSpace(abandonReason) ?
                            "PipelineNotCreatedNoWork" : abandonReason) : stickyFailureReason;
                }
                else
                {
                    finalState = RuntimeCallbackPipelineState.FailClosed;
                    reason = string.IsNullOrWhiteSpace(stickyFailureReason)
                        ? (string.IsNullOrWhiteSpace(abandonReason) ?
                            "RequiredSafetySubscriberMissing" : abandonReason) : stickyFailureReason;
                }
            }
            else
            {
                coordinator.BeginClose(scope);
                stopReceipt = coordinator.Drain(scope, budget);
                callbackReceipt = stopReceipt?.DispatcherReceipt ?? dispatch.CompleteAndDrain(budget);
                var resourcesTerminal = ingressReceipt.DrainTerminal && ingressReceipt.PendingRequests == 0 &&
                    stopReceipt?.IsTerminal == true && callbackReceipt?.AllResourcesReleased == true;
                finalState = resourcesTerminal
                    ? (string.IsNullOrWhiteSpace(stickyFailureReason)
                        ? RuntimeCallbackPipelineState.Terminal
                        : RuntimeCallbackPipelineState.FailClosed)
                    : RuntimeCallbackPipelineState.FailClosed;
                    reason = !string.IsNullOrWhiteSpace(stickyFailureReason)
                    ? stickyFailureReason
                    : (finalState == RuntimeCallbackPipelineState.Terminal
                        ? (string.IsNullOrWhiteSpace(abandonReason) ?
                            "PipelineDrained" : abandonReason)
                        : (ingressReceipt.PendingRequests != 0
                            ? "PendingIngressNotDrained"
                            : (stopReceipt?.Reason ?? "PipelineDrainIncomplete")));
            }
            context.TrySetPipelineState(finalState);
            // Every non-terminal production retry receives a fresh monotonic
            // close attempt.  Terminal/test-fixture cached receipts are
            // short-circuited above, so this cannot create a second close
            // component or session.
            var closingAttempt = context.BeginCallbackClose();
            var receipt = new RuntimeShutdownReceipt(closingAttempt, engineReceipt, ingressReceipt,
                stopReceipt, callbackReceipt, finalState, reason, testAbandoned,
                retentionVersion: closingAttempt,
                sessionId: context.SessionId,
                sessionGeneration: context.SessionGeneration,
                sessionLease: context.SessionLease,
                journalFlushCompleted: context.Journal == null,
                journalDisposed: context.Journal == null);
            context.CacheShutdownReceipt(receipt);
            // Production FailClosed deliberately retains the context,
            // owner, canonical ledger and target until real delivery becomes
            // terminal or an authoritative takeover/SafeIdle receipt exists.
            // Fixture abandonment is the only path allowed to force shared
            // cleanup while retaining a non-terminal evidence receipt.
            if (finalState == RuntimeCallbackPipelineState.Terminal || testAbandoned)
            {
                foreach (var lease in context.TakeCallbackHandlerLeases())
                    try { lease?.Dispose(); } catch { }
                try { coordinator?.Dispose(); } catch { }
                try { dispatch?.Dispose(); } catch { }
                context.SafetyTargetLease?.DisposeOwnedTarget();
                context.TryClearCallbackScope();
                context.IngressGate.Dispose();
            }
            return receipt;
        }

        private static RuntimeShutdownReceipt CloseCallbackPipeline(
            RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt)
        {
            return CleanupCallbackPipelineResources(context, TimeSpan.FromSeconds(5),
                string.Empty, engineReceipt);
        }

        /// <summary>
        /// Shared production cleanup boundary. Normal Engine shutdown and the
        /// deterministic fixture abandonment path use this same transaction;
        /// no test-only drain or disposal algorithm is allowed.
        /// </summary>
        internal static RuntimeShutdownReceipt CleanupCallbackPipelineResources(
            RuntimeTransportSessionContext context, TimeSpan budget, string abandonReason,
            ShutdownReceipt engineReceipt = null, bool testAbandoned = false)
        {
            if (context == null)
                return new RuntimeShutdownReceipt(0, engineReceipt, null, null, null,
                    RuntimeCallbackPipelineState.Terminal, "PipelineNotCreatedNoWork", testAbandoned,
                    journalFlushCompleted: true, journalDisposed: true);
            var cached = context.CachedShutdownReceipt;
            if (cached != null && (cached.PipelineTerminal || cached.TestAbandoned))
                return testAbandoned ? cached.AsTestAbandoned() : cached;
            return CloseCallbackPipelineCore(context, engineReceipt, budget, abandonReason, testAbandoned);
        }

        internal static RuntimeShutdownReceipt CloseCallbackPipelineForContext(
            RuntimeTransportSessionContext context, ShutdownReceipt engineReceipt)
        {
            return CloseCallbackPipeline(context, engineReceipt);
        }

        internal static RuntimeCallbackPipelineSnapshot CaptureCallbackPipelineSnapshot()
        {
            return new RuntimeCallbackPipelineSnapshot(CaptureContext());
        }

        internal sealed class RuntimeStopAllHandlerLease : IDisposable
        {
            private readonly RuntimeTransportSessionContext _context;
            private readonly RuntimePendingHandlerRegistration _pending;
            private int _disposed;

            internal bool Accepted { get; }
            internal bool ReservationAccepted => Accepted;
            internal string RejectionReason { get; }
            internal string FailureKind => _pending?.FailureKind ?? RejectionReason ?? string.Empty;
            internal RuntimeHandlerBindingStatus Status => _pending?.Status ?? RuntimeHandlerBindingStatus.Rejected;
            internal RuntimeTransportSessionContext Context => _context;
            internal long PipelineGeneration => _pending?.PipelineGeneration ?? 0;
            internal string SubscriberId => _pending?.SubscriberId ?? string.Empty;
            internal long RegistrationId => _pending?.ReservationId ?? 0;
            internal bool IsBound => _pending?.IsBound == true;
            internal Task<WatchdogStopAllRegistrationLease> BindingCompletion =>
                _pending?.BindingCompletion ?? Task.FromResult<WatchdogStopAllRegistrationLease>(null);
            internal bool IsActive => Accepted && Volatile.Read(ref _disposed) == 0 &&
                _pending != null && !_pending.IsDisposed &&
                _context != null && _context.IsCurrentPendingHandler(_pending);

            internal RuntimeStopAllHandlerLease(RuntimeTransportSessionContext context,
                RuntimePendingHandlerRegistration pending)
            {
                _context = context;
                _pending = pending;
                Accepted = context != null && pending != null && pending.Accepted;
                RejectionReason = Accepted ? string.Empty : (pending?.RejectionReason ?? "HandlerRejected");
            }

            private RuntimeStopAllHandlerLease(string rejectionReason)
            {
                RejectionReason = rejectionReason ?? "Rejected";
                Accepted = false;
            }

            internal static RuntimeStopAllHandlerLease Rejected(string reason)
            {
                return new RuntimeStopAllHandlerLease(reason);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_pending == null || _context == null) return;
                // Disposal before the pipeline is bound is a benign cancel of
                // the reservation.  It must not transition the pipeline to
                // FailClosed or consume another subscriber's registration.
                _pending.Dispose();
                _context.RemovePendingHandler(_pending);
            }
        }
    }
}
