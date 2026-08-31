using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal enum RecoveryFailureHandoffOutcome
    {
        NoActiveSession = 0,
        ReceiptUnavailable = 1,
        RelaunchApproved = 2,
        RelaunchAlreadyPending = 3,
        CircuitOpen = 4,
        IdentityConflict = 5,
        SupersededByTakeover = 6
    }

    internal sealed class RecoveryFailureHandoffResult
    {
        internal RecoveryFailureHandoffOutcome Outcome { get; set; }
        internal RecoveryFailureReceipt Receipt { get; set; }
        internal string Detail { get; set; }
        internal bool WatchdogOwnsExit =>
            Outcome == RecoveryFailureHandoffOutcome.RelaunchApproved ||
            Outcome == RecoveryFailureHandoffOutcome.RelaunchAlreadyPending ||
            Outcome == RecoveryFailureHandoffOutcome.CircuitOpen ||
            Outcome == RecoveryFailureHandoffOutcome.IdentityConflict ||
            Outcome == RecoveryFailureHandoffOutcome.SupersededByTakeover;
    }

    internal sealed class WatchdogRecoveryIntent
    {
        public string SessionId { get; set; }
        public string PipeName { get; set; }
        public int PreviousPid { get; set; }
        public int RecoveryAttempt { get; set; }
        public int SidecarProcessId { get; set; }
        public long SidecarProcessStartUtcTicks { get; set; }
        public string SidecarInstanceNonce { get; set; }
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonce { get; set; }
        public string JournalDirectory { get; set; }
        public int[] ExcludedChannels { get; set; } = Array.Empty<int>();
        public bool StartIdle { get; set; }

        public static WatchdogRecoveryIntent Parse(string[] args)
        {
            string Read(string name)
            {
                for (var i = 0; i + 1 < (args?.Length ?? 0); i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return string.Empty;
            }

            var idleSession = Read("--watchdog-idle-restart");
            var session = string.IsNullOrWhiteSpace(idleSession)
                ? Read("--watchdog-recover")
                : idleSession;
            if (string.IsNullOrWhiteSpace(session)) return null;
            int.TryParse(Read("--previous-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var previousPid);
            int.TryParse(Read("--recovery-attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt);
            int.TryParse(Read("--sidecar-pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidecarPid);
            long.TryParse(Read("--sidecar-start-ticks"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sidecarStartTicks);
            long.TryParse(Read("--relaunch-generation"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var permitGeneration);
            var pipe = Read("--watchdog-pipe");
            var startIdle = !string.IsNullOrWhiteSpace(idleSession);
            if (!startIdle && string.IsNullOrWhiteSpace(pipe)) return null;
            return new WatchdogRecoveryIntent
            {
                SessionId = session,
                PipeName = pipe,
                PreviousPid = previousPid,
                RecoveryAttempt = Math.Max(1, attempt),
                SidecarProcessId = sidecarPid,
                SidecarProcessStartUtcTicks = sidecarStartTicks,
                SidecarInstanceNonce = Read("--sidecar-instance-nonce"),
                RelaunchPermitGeneration = Math.Max(0, permitGeneration),
                RelaunchPermitId = Read("--relaunch-permit-id"),
                RelaunchPermitNonce = Read("--relaunch-permit-nonce"),
                JournalDirectory = Read("--journal-directory"),
                StartIdle = startIdle,
                ExcludedChannels = (Read("--exclude-channels") ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel)
                        ? channel
                        : 0)
                    .Where(channel => channel >= 1 && channel <= 12)
                    .Distinct()
                    .OrderBy(channel => channel)
                    .ToArray()
            };
        }
    }

    internal sealed class WatchdogAttachResult
    {
        public bool Attached { get; set; }
        public string Warning { get; set; }
        public string SessionId { get; set; }
        public string JournalPolicyLog { get; set; }
    }

    internal enum RuntimeJournalMode
    {
        CreateNewInitial = 0,
        OpenExistingRecovery = 1,
        EmergencyRecoveryReject = 2
    }

    internal sealed class RuntimeTransportSessionContext
    {
        internal string SessionId { get; }
        internal string PipeName { get; }
        internal string MainExecutablePath { get; }
        internal string SidecarExecutablePath { get; }
        internal string JournalDirectory { get; }
        internal WatchdogJournalPolicy JournalPolicy { get; }
        internal int[] SelectedChannels { get; }
        internal bool RecoveryProcess { get; }
        internal int RecoveryAttempt { get; }
        internal long SessionGeneration { get; }
        // The Engine allocates the lease when BeginSession commits.  The
        // context is created before that call, so bind the lease exactly once
        // immediately after BeginSession and use the frozen value for every
        // subsequent callback/send/close operation.
        private long _sessionLease;
        internal long SessionLease => Volatile.Read(ref _sessionLease);
        internal long RelaunchPermitGeneration { get; }
        internal string RelaunchPermitId { get; }
        internal string RelaunchPermitNonce { get; }
        internal RuntimeJournalMode JournalMode { get; }
        internal int AuthorityProcessId { get; }
        internal long AuthorityProcessStartUtcTicks { get; }
        internal string AuthoritySessionId { get; }
        internal string AuthorityInstanceNonce { get; }
        internal RuntimeTransportSessionState State { get; }
        internal WatchdogJournalStore Journal { get; }
        internal WatchdogRuntimeCallbackDispatch CallbackDispatch { get; private set; }
        internal WatchdogStopAllOfferCoordinator StopAllCoordinator { get; private set; }
        internal RuntimeCallbackIngressGate IngressGate { get; }
        internal object CallbackGate { get; } = new object();
        private WatchdogStopAllScopeLease _scopeLease;
        private int _callbackPipelineActive;
        private int _engineShutdownReceiptObserved;
        private long _closingAttempt;
        private RuntimeShutdownReceipt _cachedShutdownReceipt;
        private readonly List<IDisposable> _callbackHandlerLeases = new List<IDisposable>();
        private readonly List<RuntimePendingHandlerRegistration> _pendingHandlerRegistrations =
            new List<RuntimePendingHandlerRegistration>();
        private RuntimeSafetyTargetReservation _safetyTargetReservation;
        private RuntimeSafetyTargetLease _safetyTargetLease;
        private long _targetReservationSequence;
        private long _pipelineGeneration;
        private long _handlerReservationSequence;
        private bool _advanceRunning;
        private bool _advanceRequested;
        // Pipeline advancement is now owned by the synchronous advance
        // executor.  There is deliberately no timer/retry worker: a retry
        // generation must not grow while an offer is merely waiting for a
        // source admission or a coordinator subscriber.
        private long _retryGeneration;
        private string _pipelineFailureReason = string.Empty;
        private RuntimeValidatedAttachIdentity _validatedAttachIdentity;
        private RuntimeCallbackPipelineState _pipelineState = RuntimeCallbackPipelineState.Unbound;
        // Deterministic production seam used only to observe the narrow
        // activation boundary.  The callback is invoked outside CallbackGate
        // after ActivateAndTake has retained the canonical outbox and before
        // the Activating -> Ready CAS.  The application leaves it null.
        internal Action<RuntimeTransportSessionContext> AfterActivateAndTakeBeforeReadyProbe { get; set; }
        internal WatchdogStopAllScopeLease ScopeLease
        {
            get { lock (CallbackGate) return _scopeLease; }
        }
        internal RuntimeSafetyTargetReservation SafetyTargetReservation
        {
            get { lock (CallbackGate) return _safetyTargetReservation; }
        }
        internal RuntimeSafetyTargetLease SafetyTargetLease
        {
            get { lock (CallbackGate) return _safetyTargetLease; }
        }
        internal long PipelineGeneration
        {
            get { lock (CallbackGate) return _pipelineGeneration; }
        }
        internal bool IsCurrentPipelineGeneration(long pipelineGeneration)
        {
            lock (CallbackGate)
                return pipelineGeneration > 0 && _pipelineGeneration == pipelineGeneration &&
                    _pipelineState != RuntimeCallbackPipelineState.Closing &&
                    _pipelineState != RuntimeCallbackPipelineState.Terminal &&
                    _pipelineState != RuntimeCallbackPipelineState.FailClosed;
        }
        internal RuntimeValidatedAttachIdentity ValidatedAttachIdentity
        {
            get { lock (CallbackGate) return _validatedAttachIdentity; }
        }
        internal RuntimeCallbackPipelineState PipelineState
        {
            get { lock (CallbackGate) return _pipelineState; }
        }
        internal string PipelineFailureReason
        {
            get { lock (CallbackGate) return _pipelineFailureReason; }
        }
        internal long RetryGeneration => Interlocked.Read(ref _retryGeneration);
        internal bool CallbackPipelineActive => Volatile.Read(ref _callbackPipelineActive) != 0;
        internal bool EngineShutdownReceiptObserved => Volatile.Read(ref _engineShutdownReceiptObserved) != 0;
        internal long ClosingAttempt => Interlocked.Read(ref _closingAttempt);
        internal RuntimeShutdownReceipt CachedShutdownReceipt
        {
            get { lock (CallbackGate) return _cachedShutdownReceipt; }
        }

        internal RuntimeTransportSessionContext(
            string sessionId,
            string pipeName,
            string mainExecutablePath,
            string sidecarExecutablePath,
            string journalDirectory,
            WatchdogJournalPolicy journalPolicy,
            int[] selectedChannels,
            bool recoveryProcess,
            int recoveryAttempt,
            long sessionGeneration,
            long relaunchPermitGeneration,
            string relaunchPermitId,
            string relaunchPermitNonce,
            RuntimeJournalMode journalMode,
            SidecarIdentityStateMachine.AuthoritySnapshot authoritySeed,
            RuntimeTransportSessionState state,
            WatchdogJournalStore journal,
            RuntimeCallbackIngressGate ingressGate)
        {
            SessionId = sessionId ?? string.Empty;
            PipeName = pipeName ?? string.Empty;
            MainExecutablePath = mainExecutablePath ?? string.Empty;
            SidecarExecutablePath = sidecarExecutablePath ?? string.Empty;
            JournalDirectory = journalDirectory ?? string.Empty;
            var sourcePolicy = journalPolicy ?? new WatchdogJournalPolicy();
            JournalPolicy = new WatchdogJournalPolicy
            {
                RetentionDays = sourcePolicy.RetentionDays,
                RetainSessionCount = sourcePolicy.RetainSessionCount,
                MaxTotalBytes = sourcePolicy.MaxTotalBytes,
                MaxSessionBytes = sourcePolicy.MaxSessionBytes,
                HeartbeatCheckpointSeconds = sourcePolicy.HeartbeatCheckpointSeconds,
                EmergencySpoolMaxBytes = sourcePolicy.EmergencySpoolMaxBytes
            }.Normalize();
            SelectedChannels = (selectedChannels ?? Array.Empty<int>()).ToArray();
            RecoveryProcess = recoveryProcess;
            RecoveryAttempt = recoveryAttempt;
            SessionGeneration = sessionGeneration;
            RelaunchPermitGeneration = relaunchPermitGeneration;
            RelaunchPermitId = relaunchPermitId ?? string.Empty;
            RelaunchPermitNonce = relaunchPermitNonce ?? string.Empty;
            JournalMode = journalMode;
            AuthorityProcessId = authoritySeed?.ProcessId ?? 0;
            AuthorityProcessStartUtcTicks = authoritySeed?.ProcessStartUtcTicks ?? 0;
            AuthoritySessionId = authoritySeed?.SessionId ?? string.Empty;
            AuthorityInstanceNonce = authoritySeed?.InstanceNonce ?? string.Empty;
            State = state ?? throw new ArgumentNullException(nameof(state));
            Journal = journal;
            IngressGate = ingressGate ?? throw new ArgumentNullException(nameof(ingressGate));
        }

        internal void BindSessionLease(long sessionLease)
        {
            if (sessionLease <= 0) throw new ArgumentOutOfRangeException(nameof(sessionLease));
            var previous = Interlocked.CompareExchange(ref _sessionLease, sessionLease, 0);
            if (previous != 0 && previous != sessionLease)
                throw new InvalidOperationException("Runtime context session lease cannot be rebound.");
        }

        internal WatchdogStopAllScopeLease BindCallbackScope(WatchdogStopAllScopeLease scopeLease,
            long pipelineGeneration)
        {
            if (scopeLease == null) throw new ArgumentNullException(nameof(scopeLease));
            lock (CallbackGate)
            {
                if (_pipelineGeneration != pipelineGeneration ||
                    _pipelineState != RuntimeCallbackPipelineState.Activating ||
                    _closingAttempt != 0)
                    throw new InvalidOperationException("StopAll pipeline activation lease is stale.");
                if (_scopeLease != null && !ReferenceEquals(_scopeLease, scopeLease))
                    throw new InvalidOperationException("Runtime callback scope cannot be rebound.");
                _scopeLease = scopeLease;
                Volatile.Write(ref _callbackPipelineActive, 1);
                return _scopeLease;
            }
        }

        internal bool TryReserveSafetyTarget(
            string targetId,
            IWatchdogCallbackPostTarget target,
            bool transferOwnership,
            out RuntimeSafetyTargetReservation reservation)
        {
            reservation = null;
            if (string.IsNullOrWhiteSpace(targetId) || target == null) return false;
            lock (CallbackGate)
            {
                if (_pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed)
                    return false;
                if (_safetyTargetLease != null || _safetyTargetReservation != null)
                    return false;
                if (Interlocked.Read(ref _closingAttempt) != 0) return false;
                _pipelineState = RuntimeCallbackPipelineState.Binding;
                var reservationId = Interlocked.Increment(ref _targetReservationSequence);
                var pipelineGeneration = Interlocked.Increment(ref _pipelineGeneration);
                reservation = new RuntimeSafetyTargetReservation(
                    this, targetId.Trim(), target, transferOwnership, reservationId,
                    pipelineGeneration);
                _safetyTargetReservation = reservation;
                return true;
            }
        }

        internal bool TryPublishPipelineComponents(
            RuntimeSafetyTargetReservation reservation,
            WatchdogRuntimeCallbackDispatch dispatch,
            WatchdogStopAllOfferCoordinator coordinator,
            out RuntimeSafetyTargetLease targetLease)
        {
            targetLease = null;
            if (reservation == null || dispatch == null || coordinator == null) return false;
            lock (CallbackGate)
            {
                if (!ReferenceEquals(_safetyTargetReservation, reservation) ||
                    reservation.ReservationId <= 0 ||
                    reservation.PipelineGeneration != _pipelineGeneration ||
                    _pipelineState != RuntimeCallbackPipelineState.Binding ||
                    Interlocked.Read(ref _closingAttempt) != 0)
                    return false;
                CallbackDispatch = dispatch;
                StopAllCoordinator = coordinator;
                targetLease = new RuntimeSafetyTargetLease(this, reservation.TargetId,
                    reservation.Target, reservation.TransferOwnership,
                    reservation.ReservationId, reservation.PipelineGeneration);
                _safetyTargetLease = targetLease;
                _safetyTargetReservation = null;
                _pipelineState = RuntimeCallbackPipelineState.BoundInactive;
                return true;
            }
        }

        internal void AbortSafetyTargetReservation(RuntimeSafetyTargetReservation reservation,
            string reason)
        {
            lock (CallbackGate)
            {
                if (!ReferenceEquals(_safetyTargetReservation, reservation)) return;
                _safetyTargetReservation = null;
                // A failed bind can fail-closed only while this exact
                // reservation still owns the Binding state.  Close/Terminal
                // wins the race and must remain the terminal truth.
                if (_pipelineState == RuntimeCallbackPipelineState.Binding &&
                    Interlocked.Read(ref _closingAttempt) == 0)
                    _pipelineState = RuntimeCallbackPipelineState.FailClosed;
            }
        }

        internal bool IsCurrentSafetyTargetLease(RuntimeSafetyTargetLease lease)
        {
            if (lease == null) return false;
            lock (CallbackGate)
            {
                return ReferenceEquals(_safetyTargetLease, lease) &&
                    lease.PipelineGeneration == _pipelineGeneration &&
                    _pipelineState != RuntimeCallbackPipelineState.Closing &&
                    _pipelineState != RuntimeCallbackPipelineState.Terminal &&
                    _pipelineState != RuntimeCallbackPipelineState.FailClosed;
            }
        }

        /// <summary>
        /// Withdraws a target lease that was admitted by the shared binding
        /// transaction but never published to its UI owner.  Disposing an
        /// active lease normally fail-closes the pipeline; an unpublished
        /// transaction must instead move the exact context to Closing so the
        /// normal shutdown coordinator can drain the already-created worker
        /// components without manufacturing a safety fault.
        /// </summary>
        internal bool TryReleaseUnpublishedSafetyTarget(RuntimeSafetyTargetLease lease)
        {
            if (lease == null) return false;
            lock (CallbackGate)
            {
                if (!ReferenceEquals(_safetyTargetLease, lease)) return false;
                if (_pipelineState == RuntimeCallbackPipelineState.Terminal)
                    return false;
                _safetyTargetLease = null;
                _safetyTargetReservation = null;
                if (_pipelineState != RuntimeCallbackPipelineState.Closing)
                    _pipelineState = RuntimeCallbackPipelineState.Closing;
                return true;
            }
        }

        internal bool TryMarkPipelineState(RuntimeCallbackPipelineState expected, RuntimeCallbackPipelineState next)
        {
            lock (CallbackGate)
            {
                if (_pipelineState != expected) return false;
                _pipelineState = next;
                return true;
            }
        }

        internal bool TryMarkPipelineState(RuntimeCallbackPipelineState expected,
            RuntimeCallbackPipelineState next, long pipelineGeneration)
        {
            lock (CallbackGate)
            {
                if (_pipelineGeneration != pipelineGeneration || _pipelineState != expected ||
                    _closingAttempt != 0) return false;
                _pipelineState = next;
                return true;
            }
        }

        internal bool TryEnterPipelineAdvance()
        {
            lock (CallbackGate)
            {
                if (_pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed)
                    return false;
                if (_advanceRunning)
                {
                    _advanceRequested = true;
                    return false;
                }
                _advanceRunning = true;
                return true;
            }
        }

        internal bool FinishPipelineAdvance()
        {
            lock (CallbackGate)
            {
                if (_advanceRequested &&
                    _pipelineState != RuntimeCallbackPipelineState.Closing &&
                    _pipelineState != RuntimeCallbackPipelineState.Terminal &&
                    _pipelineState != RuntimeCallbackPipelineState.FailClosed)
                {
                    _advanceRequested = false;
                    return true;
                }
                _advanceRunning = false;
                _advanceRequested = false;
                return false;
            }
        }

        internal bool IsPipelineAdvanceTerminal
        {
            get
            {
                lock (CallbackGate) return !_advanceRunning && !_advanceRequested;
            }
        }

        internal bool IsPipelineRetryTerminal
        {
            // There is no detached retry task anymore.  The current advance
            // owner rechecks its request flag before returning.
            get { return true; }
        }

        internal bool IsPipelineWorkersTerminal =>
            IsPipelineAdvanceTerminal && IsPipelineRetryTerminal;

        /// <summary>
        /// Requests another pass from the current advance owner.  It is
        /// intentionally not a scheduler: no timer, task, or retry
        /// generation is created.  A caller that gets false may synchronously
        /// enter the single advance executor.
        /// </summary>
        internal bool RequestPipelineAdvance()
        {
            lock (CallbackGate)
            {
                if (_pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed)
                    return false;
                if (!_advanceRunning)
                {
                    return false;
                }
                _advanceRequested = true;
                return true;
            }
        }

        internal bool TryFailPipeline(long pipelineGeneration, string reason)
        {
            var transitioned = false;
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "PipelineFailClosed" : reason;
            lock (CallbackGate)
            {
                if (_pipelineGeneration != pipelineGeneration ||
                    _pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed)
                    return false;
                if (string.IsNullOrWhiteSpace(_pipelineFailureReason))
                    _pipelineFailureReason = normalizedReason;
                _pipelineState = RuntimeCallbackPipelineState.FailClosed;
                transitioned = true;
            }
            // The ingress evidence remains observable, but a sticky
            // FailClosed generation must not strand an actionable outbox or
            // prevent the real dispatcher/coordinator resource receipt from
            // completing.  Keep this call outside CallbackGate.
            if (transitioned) IngressGate?.MarkFailClosed(normalizedReason);
            return transitioned;
        }

        internal bool TryBindValidatedAttachIdentity(RuntimeValidatedAttachIdentity identity)
        {
            if (identity == null) return false;
            lock (CallbackGate)
            {
                if (_pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed ||
                    Interlocked.Read(ref _closingAttempt) != 0)
                    return false;
                if (_validatedAttachIdentity != null)
                    return _validatedAttachIdentity.ExactKey == identity.ExactKey;
                _validatedAttachIdentity = identity;
                return true;
            }
        }

        internal RuntimePendingHandlerRegistration[] SnapshotPendingHandlers()
        {
            lock (CallbackGate) return _pendingHandlerRegistrations.ToArray();
        }

        internal bool TryReservePendingHandler(RuntimeSafetyTargetLease targetLease,
            string subscriberId, Func<WatchdogStopAllOfferEnvelope, Task> handler,
            out RuntimePendingHandlerRegistration registration)
        {
            registration = null;
            if (targetLease == null || string.IsNullOrWhiteSpace(subscriberId) || handler == null) return false;
            lock (CallbackGate)
            {
                if (_pipelineState == RuntimeCallbackPipelineState.Closing ||
                    _pipelineState == RuntimeCallbackPipelineState.Terminal ||
                    _pipelineState == RuntimeCallbackPipelineState.FailClosed ||
                    !ReferenceEquals(_safetyTargetLease, targetLease) ||
                    targetLease.PipelineGeneration != _pipelineGeneration ||
                    Interlocked.Read(ref _closingAttempt) != 0)
                    return false;
                if (_pendingHandlerRegistrations.Any(item => !item.IsDisposed &&
                    ReferenceEquals(item.TargetLease, targetLease) &&
                    string.Equals(item.SubscriberId, subscriberId, StringComparison.Ordinal)))
                    return false;
                var reservationId = Interlocked.Increment(ref _handlerReservationSequence);
                registration = new RuntimePendingHandlerRegistration(subscriberId, targetLease,
                    handler, this, reservationId);
                _pendingHandlerRegistrations.Add(registration);
                return true;
            }
        }

        internal void RemovePendingHandler(RuntimePendingHandlerRegistration registration)
        {
            if (registration == null) return;
            lock (CallbackGate) _pendingHandlerRegistrations.Remove(registration);
        }

        internal bool IsCurrentPendingHandler(RuntimePendingHandlerRegistration registration)
        {
            if (registration == null || registration.IsDisposed ||
                registration.Status == RuntimeHandlerBindingStatus.Rejected) return false;
            lock (CallbackGate)
            {
                return _pendingHandlerRegistrations.Contains(registration) &&
                    _pipelineState != RuntimeCallbackPipelineState.Closing &&
                    _pipelineState != RuntimeCallbackPipelineState.Terminal &&
                    _pipelineState != RuntimeCallbackPipelineState.FailClosed &&
                    _safetyTargetLease != null &&
                    ReferenceEquals(_safetyTargetLease, registration.TargetLease) &&
                    registration.PipelineGeneration == _pipelineGeneration;
            }
        }

        internal bool TrySetPipelineState(RuntimeCallbackPipelineState state)
        {
            lock (CallbackGate)
            {
                _pipelineState = state;
                return true;
            }
        }

        internal bool TryClearCallbackScope()
        {
            lock (CallbackGate)
            {
                if (_scopeLease == null) return true;
                _scopeLease = null;
                Volatile.Write(ref _callbackPipelineActive, 0);
                return true;
            }
        }

        internal long BeginCallbackClose()
        {
            return Interlocked.Increment(ref _closingAttempt);
        }

        internal void MarkEngineShutdownReceiptObserved()
        {
            Volatile.Write(ref _engineShutdownReceiptObserved, 1);
        }

        internal void CacheShutdownReceipt(RuntimeShutdownReceipt receipt)
        {
            if (receipt == null) return;
            lock (CallbackGate)
            {
                if (_cachedShutdownReceipt == null ||
                    receipt.ClosingAttempt >= _cachedShutdownReceipt.ClosingAttempt)
                    _cachedShutdownReceipt = receipt;
            }
        }

        internal void AddCallbackHandlerLease(IDisposable lease)
        {
            if (lease == null) return;
            lock (CallbackGate) _callbackHandlerLeases.Add(lease);
        }

        internal IDisposable[] TakeCallbackHandlerLeases()
        {
            lock (CallbackGate)
            {
                var leases = _callbackHandlerLeases.ToArray();
                _callbackHandlerLeases.Clear();
                return leases;
            }
        }
    }

    internal sealed class RuntimeTransportSessionState
    {
        internal readonly object Gate = new object();
        internal readonly RuntimeHeartbeatSource HeartbeatSource;
        internal long HeartbeatSequence;
        internal long ClientEventSequence;
        internal RecoveryFailureReport LastFailureReport;
        internal int SessionClosing;
        private readonly Dictionary<string, RuntimeRecoveryFailureReceiptWaiter>
            _recoveryFailureReceipts =
                new Dictionary<string, RuntimeRecoveryFailureReceiptWaiter>(
                    StringComparer.Ordinal);

        internal RuntimeTransportSessionState(Func<WatchdogHeartbeat> initialProvider)
        {
            HeartbeatSource = new RuntimeHeartbeatSource(initialProvider);
        }

        internal RuntimeRecoveryFailureReceiptWaiter RegisterRecoveryFailureReceipt(
            string correlationId,
            string requestPayloadSha256)
        {
            lock (Gate)
            {
                if (_recoveryFailureReceipts.TryGetValue(
                        correlationId,
                        out var existing))
                {
                    if (!string.Equals(existing.RequestPayloadSha256,
                            requestPayloadSha256,
                            StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "Recovery failure correlation payload conflict.");
                    return existing;
                }
                var created = new RuntimeRecoveryFailureReceiptWaiter(
                    correlationId,
                    requestPayloadSha256);
                _recoveryFailureReceipts.Add(correlationId, created);
                return created;
            }
        }

        internal RuntimeRecoveryFailureReceiptWaiter FindRecoveryFailureReceipt(
            string correlationId)
        {
            lock (Gate)
                return correlationId != null &&
                       _recoveryFailureReceipts.TryGetValue(
                           correlationId,
                           out var value)
                    ? value
                    : null;
        }

        internal void RemoveRecoveryFailureReceipt(
            RuntimeRecoveryFailureReceiptWaiter waiter)
        {
            if (waiter == null) return;
            lock (Gate)
            {
                if (_recoveryFailureReceipts.TryGetValue(
                        waiter.CorrelationId,
                        out var current) &&
                    ReferenceEquals(current, waiter))
                    _recoveryFailureReceipts.Remove(waiter.CorrelationId);
            }
        }
    }

    internal sealed class RuntimeRecoveryFailureReceiptWaiter
    {
        internal RuntimeRecoveryFailureReceiptWaiter(
            string correlationId,
            string requestPayloadSha256)
        {
            CorrelationId = correlationId;
            RequestPayloadSha256 = requestPayloadSha256;
            Completion = new TaskCompletionSource<RecoveryFailureReceipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal string CorrelationId { get; }
        internal string RequestPayloadSha256 { get; }
        internal TaskCompletionSource<RecoveryFailureReceipt> Completion { get; }
    }

    internal sealed class RuntimeHeartbeatSource
    {
        private Func<WatchdogHeartbeat> _provider;

        internal RuntimeHeartbeatSource(Func<WatchdogHeartbeat> provider)
        {
            _provider = provider;
        }

        internal void Set(Func<WatchdogHeartbeat> provider)
        {
            Interlocked.Exchange(ref _provider, provider);
        }

        internal WatchdogHeartbeat Capture()
        {
            var provider = Volatile.Read(ref _provider);
            return provider == null ? null : provider();
        }
    }

    internal sealed class RuntimeTransportSnapshot
    {
        internal RuntimeTransportSessionContext Context { get; }
        internal RuntimeTransportSessionContext ContextBefore { get; }
        internal RuntimeTransportSessionContext ContextAfter { get; }
        internal bool IsStable { get; }
        internal WatchdogClientTransportSnapshot Engine { get; }
        internal bool SessionClosing { get; }

        internal RuntimeTransportSnapshot(
            RuntimeTransportSessionContext context,
            WatchdogClientTransportSnapshot engine,
            bool sessionClosing,
            RuntimeTransportSessionContext contextBefore = null,
            RuntimeTransportSessionContext contextAfter = null,
            bool isStable = true)
        {
            ContextBefore = contextBefore ?? context;
            ContextAfter = contextAfter ?? context;
            IsStable = isStable && ReferenceEquals(ContextBefore, ContextAfter);
            Context = IsStable ? ContextBefore : null;
            Engine = engine;
            SessionClosing = sessionClosing;
        }
    }

    /// <summary>
    /// Business-facing compatibility facade.  All pipe, process, handshake,
    /// ACK, monitor and reconnect ownership belongs to the one process-static
    /// WatchdogClientTransportEngine.  This class retains only application
    /// session/journal/marker state and delegates transport operations.
    /// </summary>
    internal static partial class WatchdogRuntime
    {
        private static readonly object Gate = new object();
        private static readonly object HeartbeatCaptureGate = new object();
        // All session creation, recovery checkpoint and shutdown transitions
        // share one process-wide lifecycle gate.  Engine callbacks never take
        // this semaphore; they only observe the frozen context, so shutdown
        // can always perform the transport receipt outside Gate.
        private static readonly SemaphoreSlim SessionLifecycleGate = new SemaphoreSlim(1, 1);
        private static readonly WatchdogClientTransportEngine TransportEngine =
            new WatchdogClientTransportEngine();
        private static RuntimeTransportSessionContext _activeContext;
        private static RuntimeTransportSessionContext _lastDetachedContext;
        private static Func<WatchdogHeartbeat> _pendingHeartbeatProvider;
        private static string _journalExportDirectory;
        private static long _sessionGeneration;
        private static DaqRuntimeSettings _daqRuntimeSettings;

        internal static event Action<string, string> TransportLost;
        internal static event Action<string, string> TransportError;

        internal static bool IsAttached => IsExactAttached(CaptureTransportSnapshot());

        internal static void ConfigureDaqRuntimeSettings(DaqRuntimeSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (Gate)
            {
                if (_daqRuntimeSettings != null &&
                    (Math.Abs(_daqRuntimeSettings.SampleRateHz - settings.SampleRateHz) > 0.000001 ||
                     _daqRuntimeSettings.SamplesPerChannel != settings.SamplesPerChannel))
                    throw new InvalidOperationException(
                        "DaqRuntimeConfigInvalid: session DAQ settings cannot change after validation.");
                _daqRuntimeSettings = settings;
            }
        }

        private static DaqRuntimeSettings CaptureDaqRuntimeSettings()
        {
            lock (Gate)
                return _daqRuntimeSettings ?? DaqRuntimeSettings.Load(
                    ConfigurationManager.AppSettings);
        }

        /// <summary>
        /// The one production exact-transport predicate used by both the
        /// runtime lifecycle and WinForms binding.  UI code must not rebuild
        /// a weaker subset of the Attached identity gate.
        /// </summary>
        internal static bool IsExactAttachedSnapshot(RuntimeTransportSnapshot snapshot)
        {
            return IsExactAttached(snapshot);
        }

        /// <summary>
        /// Shared UI transaction identity gate.  The live application uses
        /// the stable process-wide composite; the hardware-independent UI
        /// acceptance seam uses the same frozen context identity gate without
        /// pretending that an isolated context is an active Engine session.
        /// </summary>
        internal static bool IsUiBindingIdentityCurrent(
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity expectedIdentity,
            bool requireGlobalAttached)
        {
            if (context == null || expectedIdentity == null ||
                Volatile.Read(ref context.State.SessionClosing) != 0 ||
                context.SessionLease <= 0 ||
                !string.Equals(context.SessionId, expectedIdentity.SessionId, StringComparison.Ordinal) ||
                context.SessionGeneration != expectedIdentity.SessionGeneration ||
                context.SessionLease != expectedIdentity.SessionLease)
                return false;
            var current = context.ValidatedAttachIdentity;
            if (current == null || current.ExactKey != expectedIdentity.ExactKey)
                return false;
            if (!requireGlobalAttached) return true;
            var composite = CaptureTransportSnapshot();
            return IsExactAttachedSnapshot(composite) &&
                ReferenceEquals(composite.Context, context) &&
                composite.Context.ValidatedAttachIdentity != null &&
                composite.Context.ValidatedAttachIdentity.ExactKey == expectedIdentity.ExactKey;
        }

        internal static string SessionId
        {
            get
            {
                lock (Gate) return _activeContext?.SessionId;
            }
        }

        internal static bool IsRecoveryInfrastructureHealthy()
        {
            var context = CaptureContext();
            return context != null &&
                   context.PipelineState == RuntimeCallbackPipelineState.Ready &&
                   context.CallbackPipelineActive &&
                   Volatile.Read(ref context.State.SessionClosing) == 0 &&
                   !HasActiveTakeover(context);
        }

        internal static bool IsActiveTakeoverCancellation(Exception exception)
        {
            if (!(exception?.GetBaseException() is OperationCanceledException)) return false;
            RuntimeTransportSessionContext context;
            lock (Gate) context = _activeContext;
            if (context == null) return false;
            return HasActiveTakeover(context);
        }

        internal static void ConfigureJournalExportPath(string directory)
        {
            string resolved = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(directory))
                    resolved = WatchdogJournalPaths.ValidateProjectDirectory(directory);
            }
            catch
            {
                lock (Gate) _journalExportDirectory = null;
                throw;
            }
            lock (Gate) _journalExportDirectory = resolved;
        }

        internal static RuntimeTransportSnapshot CaptureTransportSnapshot()
        {
            RuntimeTransportSessionContext before = null;
            RuntimeTransportSessionContext after = null;
            WatchdogClientTransportSnapshot engine = null;
            // Context replacement and the Engine snapshot are independent
            // locks.  Retry a bounded number of times and expose a stable
            // composite only when both context references are identical; a
            // caller must fail closed rather than combine two sessions.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                lock (Gate) before = _activeContext;
                engine = TransportEngine.CaptureSnapshot();
                lock (Gate) after = _activeContext;
                if (ReferenceEquals(before, after))
                {
                    var closing = before != null &&
                                  Volatile.Read(ref before.State.SessionClosing) != 0;
                    return new RuntimeTransportSnapshot(
                        before, engine, closing, before, after, true);
                }
            }

            // No stable pair was observed.  Keep the engine point-in-time
            // value for diagnostics, but intentionally hide the context.
            return new RuntimeTransportSnapshot(
                null,
                engine ?? TransportEngine.CaptureSnapshot(),
                true,
                before,
                after,
                false);
        }

        /// <summary>
        /// Creates an isolated context for the WinForms production binding
        /// seam.  It is the same production context type used by the normal
        /// lifecycle, but it deliberately does not install the process-wide
        /// active context or start a transport/sidecar.  The UI transaction
        /// can therefore prove BindTarget -&gt; RegisterHandler -&gt; Ready without
        /// constructing hardware-dependent Main_Frm state.
        /// </summary>
        internal static RuntimeTransportSessionContext
            CreateUiBindingProductionContext(int[] selectedChannels = null)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            var state = new RuntimeTransportSessionState(
                () => new WatchdogHeartbeat());
            var context = new RuntimeTransportSessionContext(
                sessionId,
                "ui-production-" + sessionId,
                string.Empty,
                string.Empty,
                string.Empty,
                new WatchdogJournalPolicy(),
                (selectedChannels ?? new[] { 4, 10 }).ToArray(),
                false,
                0,
                Interlocked.Increment(ref _sessionGeneration),
                0,
                string.Empty,
                string.Empty,
                RuntimeJournalMode.CreateNewInitial,
                null,
                state,
                null,
                new RuntimeCallbackIngressGate());
            context.BindSessionLease(1);
            return context;
        }

        /// <summary>
        /// Creates the same isolated UI context shape, but freezes the
        /// already-attached transport session identity supplied by the real
        /// client Engine.  This is used by the production UI acceptance seam
        /// to prove that the global exact-attached gate is not replaced by a
        /// synthetic context/session identity.
        /// </summary>
        internal static RuntimeTransportSessionContext
            CreateUiBindingProductionContext(
                WatchdogClientTransportSnapshot engineSnapshot,
                int[] selectedChannels = null)
        {
            if (engineSnapshot == null ||
                !engineSnapshot.IsAttached ||
                engineSnapshot.ActiveSessionLease <= 0 ||
                engineSnapshot.SessionGeneration <= 0 ||
                string.IsNullOrWhiteSpace(engineSnapshot.SessionId) ||
                engineSnapshot.AuthorityProcessId <= 0 ||
                engineSnapshot.AuthorityProcessStartUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(engineSnapshot.AuthorityInstanceNonce))
                throw new InvalidOperationException(
                    "UI production context requires a stable exact Attached Engine snapshot.");

            var authority = new SidecarIdentityStateMachine.AuthoritySnapshot(
                engineSnapshot.AuthorityProcessId,
                engineSnapshot.AuthorityProcessStartUtcTicks,
                engineSnapshot.AuthoritySessionId,
                engineSnapshot.AuthorityInstanceNonce);
            var state = new RuntimeTransportSessionState(
                () => new WatchdogHeartbeat());
            var context = new RuntimeTransportSessionContext(
                engineSnapshot.SessionId,
                "ui-engine-" + engineSnapshot.SessionId,
                string.Empty,
                string.Empty,
                string.Empty,
                new WatchdogJournalPolicy(),
                (selectedChannels ?? new[] { 4, 10 }).ToArray(),
                false,
                0,
                engineSnapshot.SessionGeneration,
                0,
                string.Empty,
                string.Empty,
                RuntimeJournalMode.CreateNewInitial,
                authority,
                state,
                null,
                new RuntimeCallbackIngressGate());
            context.BindSessionLease(engineSnapshot.ActiveSessionLease);
            return context;
        }

        /// <summary>
        /// Builds the frozen authority identity used by the isolated UI
        /// production seam.  The actual application obtains these exact
        /// values from the transport Attached message; this helper only
        /// supplies a live process identity to the same production identity
        /// gate when no hardware/sidecar is being started.
        /// </summary>
        internal static RuntimeValidatedAttachIdentity
            CreateUiBindingProductionIdentity(
                RuntimeTransportSessionContext context,
                string authorityInstanceNonce,
                long attachedConnectionGeneration = 1)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (attachedConnectionGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(attachedConnectionGeneration));
            using (var process = Process.GetCurrentProcess())
            {
                return new RuntimeValidatedAttachIdentity(
                    context.SessionId,
                    context.SessionGeneration,
                    context.SessionLease,
                    attachedConnectionGeneration,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    authorityInstanceNonce);
            }
        }

        internal static RuntimeValidatedAttachIdentity
            CreateUiBindingProductionIdentity(
                RuntimeTransportSessionContext context,
                WatchdogClientTransportSnapshot engineSnapshot)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (engineSnapshot == null ||
                !engineSnapshot.IsAttached ||
                engineSnapshot.ActiveSessionLease != context.SessionLease ||
                !string.Equals(engineSnapshot.SessionId, context.SessionId, StringComparison.Ordinal) ||
                engineSnapshot.SessionGeneration != context.SessionGeneration)
                throw new InvalidOperationException(
                    "Engine snapshot与UI production context的Session身份不一致。");
            return new RuntimeValidatedAttachIdentity(
                context.SessionId,
                context.SessionGeneration,
                context.SessionLease,
                engineSnapshot.AttachedConnectionGeneration,
                engineSnapshot.AuthorityProcessId,
                engineSnapshot.AuthorityProcessStartUtcTicks,
                engineSnapshot.AuthorityInstanceNonce);
        }

        /// <summary>
        /// Supplies the transport-boundary receipt for the isolated WinForms
        /// production seam.  The seam has a real Runtime context/pipeline and
        /// real WinForms dispatcher; only the transport shutdown receipt is
        /// injected because no sidecar is started by this hardware-independent
        /// binding transaction.  Its lease is deliberately the frozen
        /// context lease so the production exact-engine terminal gate is
        /// exercised rather than bypassed.
        /// </summary>
        internal static ShutdownReceipt CreateUiBindingProductionTransportReceipt(
            long sessionLease)
        {
            if (sessionLease <= 0)
                throw new ArgumentOutOfRangeException(nameof(sessionLease));
            var capturedUtcTicks = DateTime.UtcNow.Ticks;
            var termination = new WorkerTerminationState(
                sessionLease,
                capturedUtcTicks,
                readerTerminal: true,
                heartbeatTerminal: true,
                monitorTerminal: true,
                reconnectTerminal: true,
                connectTerminal: true,
                launchTerminal: true,
                launchReservationTerminal: true,
                retainedLaunchTerminal: true,
                pendingOwnerReleased: true,
                transportDetached: true,
                liveOwnedHandleCount: 0,
                ownedHandleCountBefore: 0,
                capturedOwnerReleased: true);
            return new ShutdownReceipt(
                sessionLease,
                capturedUtcTicks,
                termination,
                shutdownEventPublished: true,
                sessionDetached: true);
        }

        internal static async Task<WatchdogAttachResult> StartSessionAsync(int[] selectedChannels)
        {
            await SessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await StartSessionCoreAsync(selectedChannels).ConfigureAwait(false);
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        private static async Task<WatchdogAttachResult> StartSessionCoreAsync(int[] selectedChannels)
        {
            var previous = ShutdownRuntimeWithReceiptNoGate();
            if (!IsShutdownReady(previous))
            {
                return new WatchdogAttachResult
                {
                    Warning = "上一个看门狗会话尚未完成资源收口，已拒绝创建新会话。"
                };
            }
            var warnings = new List<string>();
            var policy = WatchdogJournalPolicy.Load(ConfigurationManager.AppSettings, warnings.Add);
            string journalDirectory;
            Func<WatchdogHeartbeat> provider;
            lock (Gate)
            {
                journalDirectory = _journalExportDirectory;
                provider = _pendingHeartbeatProvider;
            }
            if (string.IsNullOrWhiteSpace(journalDirectory))
                return new WatchdogAttachResult
                {
                    Warning = "独立看门狗项目Journal路径缺失或非法；已拒绝启动不可审计的Sidecar。"
                };

            var sessionId = Guid.NewGuid().ToString("N");
            var executable = Process.GetCurrentProcess().MainModule?.FileName ??
                             Assembly.GetEntryAssembly()?.Location;
            var sidecar = Path.Combine(
                Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                "MTTFTest.Watchdog.exe");
            var context = CreateContext(
                sessionId,
                "MTTFTest.Watchdog." + sessionId,
                executable,
                sidecar,
                journalDirectory,
                policy,
                selectedChannels,
                false,
                0,
                0,
                string.Empty,
                 string.Empty,
                 provider,
                 warnings,
                 RuntimeJournalMode.CreateNewInitial,
                 null);
            if (context == null)
                return new WatchdogAttachResult
                {
                    Warning = "独立看门狗Journal初始化失败；已拒绝启动不可审计的Sidecar。"
                };

            InstallContext(context);
            try
            {
                await StartAndAwaitExactAttachedAsync(context, null, null).ConfigureAwait(false);
                return new WatchdogAttachResult
                {
                    Attached = IsExactAttached(CaptureTransportSnapshot()),
                    SessionId = context.SessionId,
                    Warning = warnings.Count == 0 ? null : string.Join(" | ", warnings),
                    JournalPolicyLog = policy.ToStartupLogLine()
                };
            }
            catch (Exception ex)
            {
                try { context.Journal?.RecordError("Sidecar startup failed: " + ex); } catch { }
                try { context.Journal?.Flush(TimeSpan.FromSeconds(1)); } catch { }
                var receipt = ShutdownRuntimeWithReceiptNoGate();
                return new WatchdogAttachResult
                {
                    Warning = "独立看门狗启动或握手失败，已由唯一Engine收口：" +
                              ex.GetBaseException().Message +
                              (IsShutdownReady(receipt) ? string.Empty : ";资源收口未完成"),
                    JournalPolicyLog = policy.ToStartupLogLine()
                };
            }
        }

        internal static async Task AttachRecoverySessionAsync(
            WatchdogRecoveryIntent intent,
            int[] selectedChannels)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            await SessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AttachRecoverySessionCoreAsync(intent, selectedChannels).ConfigureAwait(false);
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        private static async Task AttachRecoverySessionCoreAsync(
            WatchdogRecoveryIntent intent,
            int[] selectedChannels)
        {
            var previous = ShutdownRuntimeWithReceiptNoGate();
            if (!IsShutdownReady(previous))
                throw new InvalidOperationException("上一个看门狗会话尚未完成资源收口，已拒绝恢复接管。");
            var warnings = new List<string>();
            var policy = WatchdogJournalPolicy.Load(ConfigurationManager.AppSettings, warnings.Add);
            string journalDirectory;
            Func<WatchdogHeartbeat> provider;
            lock (Gate)
            {
                journalDirectory = _journalExportDirectory;
                provider = _pendingHeartbeatProvider;
            }
            if (string.IsNullOrWhiteSpace(journalDirectory))
                throw new InvalidOperationException("Watchdog 恢复进程缺少项目Journal路径。");
            ValidateRecoveryIntent(intent);
            var executable = Process.GetCurrentProcess().MainModule?.FileName ??
                             Assembly.GetEntryAssembly()?.Location;
            var sidecar = Path.Combine(
                Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                "MTTFTest.Watchdog.exe");
            var seed = BuildAuthoritySeed(intent, out var rejection);
            if (seed == null)
                throw new InvalidOperationException(rejection);
            var context = CreateContext(
                intent.SessionId,
                intent.PipeName,
                executable,
                sidecar,
                journalDirectory,
                policy,
                selectedChannels,
                true,
                intent.RecoveryAttempt,
                intent.RelaunchPermitGeneration,
                intent.RelaunchPermitId,
                intent.RelaunchPermitNonce,
                provider,
                warnings,
                RuntimeJournalMode.OpenExistingRecovery,
                seed);
            if (context == null) throw new InvalidOperationException("Watchdog 恢复Journal初始化失败。");
            InstallContext(context);
            try
            {
                await StartAndAwaitExactAttachedAsync(context, seed, rejection).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var receipt = ShutdownRuntimeWithReceiptNoGate();
                if (!IsShutdownReady(receipt))
                    throw new InvalidOperationException(
                        "恢复接管失败且Transport资源未完成收口。", ex);
                throw;
            }
        }

        internal static async Task NotifyRecoveryCheckpointRejectedAsync(
            WatchdogRecoveryIntent intent,
            string reason)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            await SessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await NotifyRecoveryCheckpointRejectedCoreAsync(intent, reason).ConfigureAwait(false);
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        private static async Task NotifyRecoveryCheckpointRejectedCoreAsync(
            WatchdogRecoveryIntent intent,
            string reason)
        {
            var previous = ShutdownRuntimeWithReceiptNoGate();
            if (!IsShutdownReady(previous))
                throw new InvalidOperationException("上一个看门狗会话尚未完成资源收口，已拒绝检查点处理。");
            var warnings = new List<string>();
            var policy = WatchdogJournalPolicy.Load(ConfigurationManager.AppSettings, warnings.Add);
            string journalDirectory;
            Func<WatchdogHeartbeat> provider;
            lock (Gate)
            {
                journalDirectory = _journalExportDirectory;
                provider = _pendingHeartbeatProvider;
            }
            if (string.IsNullOrWhiteSpace(journalDirectory))
            {
                RecordInvalidRecoveryIntentLocalAudit(
                    intent, reason, "ProjectJournalDirectoryMissing", policy);
                return;
            }
            var executable = Process.GetCurrentProcess().MainModule?.FileName ??
                             Assembly.GetEntryAssembly()?.Location;
            var sidecar = Path.Combine(
                Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                "MTTFTest.Watchdog.exe");
            var seed = BuildAuthoritySeed(intent, out var rejection);
            try { ValidateRecoveryIntent(intent); }
            catch (Exception ex)
            {
                RecordInvalidRecoveryIntentLocalAudit(
                    intent,
                    reason,
                    "InvalidRecoveryIntent:" + ex.GetBaseException().Message,
                    policy);
                return;
            }
            if (seed == null)
            {
                RecordInvalidRecoveryIntentLocalAudit(
                    intent, reason, rejection ?? "AuthoritySeedInvalid", policy);
                return;
            }
            var context = CreateContext(
                intent.SessionId,
                intent.PipeName,
                executable,
                sidecar,
                journalDirectory,
                policy,
                Array.Empty<int>(),
                true,
                intent.RecoveryAttempt,
                intent.RelaunchPermitGeneration,
                intent.RelaunchPermitId,
                intent.RelaunchPermitNonce,
                provider,
                warnings,
                RuntimeJournalMode.EmergencyRecoveryReject,
                seed);
            if (context == null) throw new InvalidOperationException("Watchdog 恢复Journal初始化失败。");
            InstallContext(context);
            try
            {
                // A checkpoint rejection may report only through the exact
                // still-authoritative helper.  AttachExistingAuthorityOnly
                // prevents this error path from launching a replacement
                // sidecar or creating a new authority.
                await StartAndAwaitExactAttachedAsync(context, seed, rejection)
                    .ConfigureAwait(false);
                RecordClientEvent(context, "RecoveryCheckpointRejected", reason ?? "Unknown");
                await NotifyRecoveryAttemptFailedCoreAsync(
                        RecoveryFailurePolicy.NormalizeReport(
                            new RecoveryFailureReport
                            {
                                RootCode = "CheckpointInvariant",
                                DeviceOrChannelGroup = "RecoveryCheckpoint",
                                RunId = "CheckpointRejected",
                                RecoveryStage = "RecoveryAdmission",
                                // Exact v3 carries the P0-4 monotonic stage
                                // version, not prose.  Zero explicitly means
                                // that admission failed before material stage
                                // progress was observed.
                                RecoveryProgressToken = "0",
                                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource
                            }),
                        true,
                        "RecoveryCheckpointRejected:" + (reason ?? "Unknown"),
                        reason ?? string.Empty,
                        string.Empty)
                    .ConfigureAwait(false);
            }
            catch
            {
                ShutdownRuntimeWithReceiptNoGate();
                throw;
            }
            var receipt = ShutdownRuntimeWithReceiptNoGate();
            if (!IsShutdownReady(receipt))
                throw new InvalidOperationException("检查点拒绝审计收口未完成。");
        }

        private static void RecordInvalidRecoveryIntentLocalAudit(
            WatchdogRecoveryIntent intent,
            string reason,
            string rejection,
            WatchdogJournalPolicy policy)
        {
            var auditSession = Guid.NewGuid().ToString("N");
            try
            {
                using (var process = Process.GetCurrentProcess())
                using (var audit = new WatchdogJournalStore(
                    WatchdogJournalPaths.LocalControlDirectory,
                    auditSession,
                    "client",
                    policy,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    WatchdogJournalStoreMode.ClientAuditOnly))
                {
                    audit.Record(new WatchdogJournalEvent
                    {
                        EventType = "RecoveryIntentRejectedLocalAudit",
                        State = "FailClosed",
                        Reason = rejection ?? "InvalidRecoveryIntent",
                        Detail = "RequestedSession=" + (intent?.SessionId ?? string.Empty) +
                                 ";Reason=" + (reason ?? string.Empty)
                    });
                    audit.RecordError(
                        "InvalidRecoveryIntentLocalAudit:" +
                        (rejection ?? "InvalidRecoveryIntent"));
                    audit.Flush(TimeSpan.FromSeconds(2));
                }
            }
            catch
            {
                // This path is deliberately audit-only.  Failure to write a
                // local diagnostic must not bootstrap, attach or launch.
            }
        }

        private static RuntimeTransportSessionContext CreateContext(
            string sessionId,
            string pipeName,
            string executable,
            string sidecar,
            string journalDirectory,
            WatchdogJournalPolicy policy,
            int[] selectedChannels,
            bool recoveryProcess,
            int recoveryAttempt,
            long permitGeneration,
            string permitId,
            string permitNonce,
            Func<WatchdogHeartbeat> provider,
            List<string> warnings,
            RuntimeJournalMode journalMode,
            SidecarIdentityStateMachine.AuthoritySnapshot authoritySeed)
        {
            var generation = Interlocked.Increment(ref _sessionGeneration);
            WatchdogJournalStore journal = null;
            RuntimeCallbackIngressGate ingressGate = null;
            try
            {
                var effectiveDirectory = journalDirectory;
                if (string.IsNullOrWhiteSpace(effectiveDirectory))
                    throw new InvalidOperationException("Watchdog Journal目录缺失。");
                var canonicalSessionId = NormalizeSessionId(sessionId);
                if (journalMode == RuntimeJournalMode.OpenExistingRecovery ||
                    journalMode == RuntimeJournalMode.EmergencyRecoveryReject)
                {
                    if (!WatchdogJournalBootstrap.TryOpenExistingSession(
                            effectiveDirectory,
                            canonicalSessionId,
                            (policy ?? new WatchdogJournalPolicy()).MaxSessionBytes,
                            out _,
                            out var openError))
                        throw new InvalidOperationException(
                            "恢复Journal只读打开失败：" + openError);
                    var existingAuthority =
                        DurableRelaunchAuthorityV4Factory.TryOpenExisting(
                            effectiveDirectory,
                            canonicalSessionId);
                    if (existingAuthority == null ||
                        !existingAuthority.Succeeded ||
                        existingAuthority.Authority == null ||
                        existingAuthority.Store == null ||
                        existingAuthority.Store.Record == null ||
                        existingAuthority.Store.Blocked ||
                        existingAuthority.Store.Unproven)
                        throw new InvalidOperationException(
                            "恢复严格V4权威打开失败：" +
                            (existingAuthority?.Reason ?? "StrictAuthorityUnavailable"));
                }
                using (var process = Process.GetCurrentProcess())
                {
                    var processStartUtcTicks =
                        process.StartTime.ToUniversalTime().Ticks;
                    if (journalMode == RuntimeJournalMode.CreateNewInitial)
                    {
                        // The main process creates the immutable bootstrap
                        // proof and the separate strict relaunch record before
                        // the sidecar can be launched.  This is the only
                        // production create path; the host is open-existing
                        // only and therefore cannot manufacture authority for
                        // an unproven session.
                        var strictAuthority =
                            DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                                effectiveDirectory,
                                canonicalSessionId,
                                process.Id,
                                processStartUtcTicks);
                        if (strictAuthority == null ||
                            !strictAuthority.Succeeded ||
                            strictAuthority.Authority == null ||
                            strictAuthority.Store == null ||
                            strictAuthority.Store.Record == null ||
                            strictAuthority.Store.Blocked ||
                            strictAuthority.Store.Unproven)
                            throw new InvalidOperationException(
                                "schema4 strict authority bootstrap失败：" +
                                (strictAuthority?.Reason ?? "StrictAuthorityCreateFailed"));
                    }
                    journal = new WatchdogJournalStore(
                        effectiveDirectory,
                        canonicalSessionId,
                        "client",
                        policy,
                        process.Id,
                        processStartUtcTicks,
                        journalMode == RuntimeJournalMode.CreateNewInitial
                            ? WatchdogJournalStoreMode.FullAuthority
                            : WatchdogJournalStoreMode.ClientAuditOnly);
                }
                // The initial Context owns only an inert ingress gate.  The
                // dispatcher/coordinator threads are created later by the
                // explicit target binding transaction.
                ingressGate = new RuntimeCallbackIngressGate();
                var state = new RuntimeTransportSessionState(provider);
                var context = new RuntimeTransportSessionContext(
                    canonicalSessionId,
                    pipeName,
                    executable,
                    sidecar,
                    effectiveDirectory,
                    policy,
                    selectedChannels,
                    recoveryProcess,
                    recoveryAttempt,
                    generation,
                    permitGeneration,
                    permitId,
                    permitNonce,
                    journalMode,
                    authoritySeed,
                    state,
                    journal,
                    ingressGate);
                RecordClientEvent(context, "JournalConfigured", policy.ToStartupLogLine());
                foreach (var warning in warnings ?? new List<string>())
                    RecordClientEvent(context, "PolicyWarning", warning);
                if (journalMode == RuntimeJournalMode.CreateNewInitial && !File.Exists(sidecar))
                    throw new FileNotFoundException("未找到独立看门狗程序。", sidecar);
                return context;
            }
            catch
            {
                try { ingressGate?.Dispose(); } catch { }
                try { journal?.Flush(TimeSpan.FromSeconds(1)); } catch { }
                try { journal?.Dispose(); } catch { }
                return null;
            }
        }

        private static void InstallContext(RuntimeTransportSessionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!InstallContextWithRetentionGate(
                    context, ShutdownRetentionCoordinator, out var rejection))
                throw new InvalidOperationException(rejection);
        }

        private static string NormalizeSessionId(string sessionId)
        {
            if (!Guid.TryParseExact(sessionId, "N", out var value) || value == Guid.Empty)
                throw new InvalidOperationException("Watchdog SessionId 必须是非空N格式GUID。");
            return value.ToString("N").ToLowerInvariant();
        }

        private static SidecarIdentityStateMachine.AuthoritySnapshot BuildAuthoritySeed(
            WatchdogRecoveryIntent intent,
            out string rejection)
        {
            rejection = string.Empty;
            if (intent.SidecarProcessId <= 0 ||
                intent.SidecarProcessStartUtcTicks <= 0 ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(intent.SidecarInstanceNonce))
            {
                rejection = "恢复参数中的Sidecar身份不完整或challenge nonce非法";
                return null;
            }
            return new SidecarIdentityStateMachine.AuthoritySnapshot(
                intent.SidecarProcessId,
                intent.SidecarProcessStartUtcTicks,
                NormalizeSessionId(intent.SessionId),
                intent.SidecarInstanceNonce);
        }

        private static void ValidateRecoveryIntent(WatchdogRecoveryIntent intent)
        {
            if (intent == null) throw new ArgumentNullException(nameof(intent));
            NormalizeSessionId(intent.SessionId);
            if (string.IsNullOrWhiteSpace(intent.PipeName))
                throw new InvalidOperationException("恢复参数缺少Watchdog管道。");
            if (intent.SidecarProcessId <= 0 || intent.SidecarProcessStartUtcTicks <= 0 ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(intent.SidecarInstanceNonce))
                throw new InvalidOperationException("恢复参数缺少有效Sidecar PID/start/nonce身份。");
            // Normal relaunch recovery is authorized by the durable permit.
            // The operator-safe-idle handoff is different: it attaches the
            // still-authoritative helper by its frozen session/PID/start/nonce
            // identity and is explicitly AttachOnly, so it has no new launch
            // permit to consume.
            if (!intent.StartIdle &&
                (intent.RelaunchPermitGeneration <= 0 ||
                 string.IsNullOrWhiteSpace(intent.RelaunchPermitId) ||
                 !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(intent.RelaunchPermitNonce)))
                throw new InvalidOperationException("恢复参数缺少完整的Relaunch permit三元组。");
            if (!intent.StartIdle)
                WatchdogJournalPaths.ValidateProjectDirectory(intent.JournalDirectory);
        }

        private static WatchdogClientTransportOptions BuildOptions(
            RuntimeTransportSessionContext context,
            SidecarIdentityStateMachine.AuthoritySnapshot seed,
            string seedRejection)
        {
            return new WatchdogClientTransportOptions
            {
                SessionId = context.SessionId,
                PipeName = context.PipeName,
                MainExecutablePath = context.MainExecutablePath,
                SidecarExecutablePath = context.SidecarExecutablePath,
                JournalDirectory = context.JournalDirectory,
                JournalPolicy = context.JournalPolicy,
                SelectedChannels = context.SelectedChannels.ToArray(),
                RecoveryProcess = context.RecoveryProcess,
                RecoveryAttempt = context.RecoveryAttempt,
                SessionGeneration = context.SessionGeneration,
                LaunchPolicy = context.RecoveryProcess
                    ? WatchdogLaunchPolicy.AttachExistingAuthorityOnly
                    : WatchdogLaunchPolicy.LaunchIfPipeUnavailable,
                AuthoritySeed = seed,
                AuthoritySeedRejection = seedRejection
            };
        }

        private static async Task<WatchdogClientTransportSnapshot> StartAndAwaitExactAttachedAsync(
            RuntimeTransportSessionContext context,
            SidecarIdentityStateMachine.AuthoritySnapshot seed,
            string seedRejection)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var attached = new TaskCompletionSource<RuntimeTransportSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var failed = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Action<WatchdogClientTransportEvent> observer = null;
            observer = notification =>
            {
                try
                {
                    var composite = CaptureTransportSnapshot();
                    if (IsExactAttached(composite))
                    {
                        attached.TrySetResult(composite);
                        return;
                    }

                    var snapshot = composite?.Engine;
                    if (snapshot == null) return;
                    if (snapshot.TransportFailClosed || snapshot.IsSafeDegraded ||
                        snapshot.IsClosing ||
                        (!string.IsNullOrWhiteSpace(snapshot.SessionId) &&
                         !string.Equals(snapshot.SessionId, context.SessionId, StringComparison.Ordinal)))
                    {
                        var reason = snapshot.TransportFailureKind == WatchdogConnectFailureKind.Unknown
                            ? notification?.EventType ?? "TransportStateRejected"
                            : snapshot.TransportFailureKind.ToString();
                        failed.TrySetResult(new InvalidOperationException(
                            "Watchdog精确Attached门禁失败：" + reason + ";" +
                            (snapshot.TransportFailureDetail ?? notification?.Detail ?? string.Empty)));
                    }
                }
                catch (Exception ex)
                {
                    failed.TrySetResult(ex);
                }
            };

            // Subscribe before BeginSession so SessionStarted and every
            // subsequent transition is observed by the same session gate.
            TransportEngine.StateChanged += observer;
            try
            {
                TransportEngine.BeginSession(
                    BuildOptions(context, seed, seedRejection),
                    BuildCallbacks(context));
                var begunSnapshot = TransportEngine.CaptureSnapshot();
                if (begunSnapshot == null || begunSnapshot.SessionLease <= 0 ||
                    !begunSnapshot.SessionActive ||
                    !string.Equals(begunSnapshot.SessionId, context.SessionId, StringComparison.Ordinal) ||
                    begunSnapshot.SessionGeneration != context.SessionGeneration ||
                    begunSnapshot.ActiveSessionLease != begunSnapshot.SessionLease)
                    throw new InvalidOperationException("Watchdog BeginSession 未发布可绑定的精确会话租约。");
                context.BindSessionLease(begunSnapshot.SessionLease);
                var startTask = TransportEngine.StartAsync();
                var waitCoordinator = new WatchdogAttachWaitCoordinator();
                var compositeResult = await waitCoordinator.WaitAsync(
                    attached.Task,
                    failed.Task,
                    startTask,
                    TimeSpan.FromMilliseconds(SelectAttachDeadlineMilliseconds()),
                    CaptureTransportSnapshot,
                    IsExactAttached).ConfigureAwait(false);
                if (!IsExactAttached(compositeResult))
                    throw new TimeoutException("Watchdog精确Attached等待超过共享传输策略期限。");
                // Exact Attached is the sole point at which a frozen
                // callback scope may be activated.  Attach-before events are
                // retained by the ingress ledger and replayed here.
                ActivateCallbackPipeline(context, compositeResult.Engine);
                return compositeResult.Engine;
            }
            finally
            {
                TransportEngine.StateChanged -= observer;
            }
        }

        private static int SelectAttachDeadlineMilliseconds()
        {
            // The Runtime is a facade: it must not reconstruct a second
            // attach budget from transport primitives.  The protocol policy
            // is the single source of truth for the complete session gate.
            return WatchdogTransportPolicy.SessionAttachDeadline;
        }

        private static bool IsExactAttached(RuntimeTransportSnapshot composite)
        {
            if (composite == null || !composite.IsStable || composite.Context == null ||
                composite.Engine == null || composite.SessionClosing)
                return false;

            var context = composite.Context;
            var engine = composite.Engine;
            if (Volatile.Read(ref context.State.SessionClosing) != 0 ||
                !engine.SessionActive || engine.ActiveSessionLease <= 0 ||
                engine.SessionLease != engine.ActiveSessionLease ||
                !string.Equals(engine.SessionId, context.SessionId, StringComparison.Ordinal) ||
                engine.SessionGeneration != context.SessionGeneration ||
                engine.ActiveConnectionGeneration <= 0 ||
                engine.AttachedConnectionGeneration != engine.ActiveConnectionGeneration ||
                !engine.IsAttached || engine.HasPending || !engine.HasAuthority ||
                engine.IsClosing || engine.TransportFailClosed ||
                !WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                    context.SessionId,
                    engine.AuthoritySessionId,
                    engine.AuthorityProcessId,
                    engine.AuthorityProcessStartUtcTicks,
                    engine.AuthorityInstanceNonce) ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(engine.AuthorityInstanceNonce))
                return false;

            if (context.AuthorityProcessId > 0 &&
                (context.AuthorityProcessId != engine.AuthorityProcessId ||
                 context.AuthorityProcessStartUtcTicks != engine.AuthorityProcessStartUtcTicks ||
                 !string.Equals(context.AuthoritySessionId, engine.AuthoritySessionId, StringComparison.Ordinal) ||
                 !string.Equals(context.AuthorityInstanceNonce, engine.AuthorityInstanceNonce, StringComparison.Ordinal)))
                return false;
            return true;
        }

        private static bool IsShutdownReady(ShutdownReceipt receipt)
        {
            if (receipt == null || !receipt.AllResourcesReleased) return false;
            // Engine's idempotent no-session receipt carries lease 0 and has
            // no captured session to compare against, so SessionDetached is
            // intentionally false in that one empty-state case.  It is safe
            // to begin a first session only when transport is nevertheless
            // detached and every worker/resource is terminal.
            return receipt.SessionDetached ||
                   (receipt.SessionLease == 0 && receipt.TransportDetached);
        }

        private static WatchdogClientTransportCallbacks BuildCallbacks(
            RuntimeTransportSessionContext context)
        {
            return BuildCallbackPipelineCallbacks(context);
        }

        private static WatchdogRunSession CreateRunSession(
            RuntimeTransportSessionContext context,
            bool recovery,
            int attempt)
        {
            using (var process = Process.GetCurrentProcess())
                return new WatchdogRunSession
                {
                    SessionId = context.SessionId,
                    PipeName = context.PipeName,
                    ExecutablePath = process.MainModule?.FileName ?? Assembly.GetEntryAssembly()?.Location,
                    ProcessId = process.Id,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    RecoveryProcess = recovery,
                    RecoveryAttempt = attempt,
                    RelaunchGeneration = context.RelaunchPermitGeneration,
                    RelaunchPermitId = context.RelaunchPermitId,
                    RelaunchPermitNonce = context.RelaunchPermitNonce,
                    SelectedChannels = context.SelectedChannels.ToArray()
                };
        }

        internal static void SetHeartbeatProvider(Func<WatchdogHeartbeat> provider)
        {
            RuntimeTransportSessionContext context;
            lock (Gate)
            {
                _pendingHeartbeatProvider = provider;
                context = _activeContext;
            }
            context?.State.HeartbeatSource.Set(provider);
        }

        internal static bool PersistManualPauseProgress(ManualPauseProgressSnapshot snapshot)
        {
            if (snapshot == null || snapshot.ProgressVersion <= 0) return false;
            RuntimeTransportSessionContext context;
            lock (Gate) context = _activeContext;
            if (context == null || context.SessionLease <= 0) return false;
            try
            {
                var receipt = new WatchdogManualPauseReceipt
                {
                    SessionId = context.SessionId,
                    SessionGeneration = context.SessionGeneration,
                    SessionLease = context.SessionLease,
                    RunId = snapshot.RunId == Guid.Empty ? string.Empty : snapshot.RunId.ToString("N"),
                    RunEpoch = snapshot.RunEpoch,
                    PauseCommandId = snapshot.PauseCommandId == Guid.Empty
                        ? string.Empty
                        : snapshot.PauseCommandId.ToString("N"),
                    Revision = snapshot.ProgressVersion,
                    Stage = (WatchdogManualPauseStage)(int)snapshot.Stage,
                    Channels = snapshot.Channels?.ToArray() ?? Array.Empty<int>(),
                    EnergizedChannels = snapshot.EnergizedChannels?.ToArray() ?? Array.Empty<int>(),
                    MotorsOff = snapshot.MotorsOff,
                    PowerOff = snapshot.PowerOff,
                    PressureSafe = snapshot.PressureSafe,
                    PersistenceDrained = snapshot.PersistenceDrained,
                    SafetyFault = snapshot.SafetyFault,
                    Detail = snapshot.Detail ?? string.Empty,
                    StartedUtcTicks = snapshot.StartedUtc.Ticks,
                    HardDeadlineUtcTicks = snapshot.HardDeadlineUtc.Ticks
                };
                WatchdogManualPauseReceiptStore.WriteThrough(
                    context.JournalDirectory,
                    receipt);
                RecordClientEvent(
                    context,
                    "ManualPauseProgressDurable",
                    $"Revision={receipt.Revision};Stage={receipt.Stage};Run={receipt.RunId}/{receipt.RunEpoch}");
                return true;
            }
            catch (Exception ex)
            {
                RecordClientEvent(context, "ManualPauseProgressPersistenceFailed", ex.GetBaseException().Message);
                RaiseTransportError(context, "ManualPauseProgressPersistenceFailed", ex.GetBaseException().Message);
                return false;
            }
        }

        private static WatchdogHeartbeat CaptureHeartbeat(RuntimeTransportSessionContext context)
        {
            WatchdogHeartbeat heartbeat = null;
            Exception providerError = null;
            lock (HeartbeatCaptureGate)
            {
                try { heartbeat = context.State.HeartbeatSource.Capture(); }
                catch (Exception ex) { providerError = ex; }
                heartbeat ??= new WatchdogHeartbeat();
                if (providerError != null)
                {
                    heartbeat.RecoveryCode = "HeartbeatProviderFault";
                    heartbeat.RecoveryContext = providerError.GetBaseException().Message;
                    RaiseTransportError(context, "HeartbeatProvider", providerError.Message);
                }
                using (var process = Process.GetCurrentProcess())
                {
                    heartbeat.Sequence = Interlocked.Increment(ref context.State.HeartbeatSequence);
                    heartbeat.SessionId = context.SessionId;
                    heartbeat.ProcessId = process.Id;
                    heartbeat.ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                if (string.IsNullOrWhiteSpace(heartbeat.RecoveryProcessSource))
                    heartbeat.RecoveryProcessSource = context.RecoveryProcess
                        ? RecoveryFailurePolicy.RecoveryProcessSource
                        : RecoveryFailurePolicy.InitialProcessSource;
            }
            return heartbeat;
        }

        private static bool TryObserveDurableStopMarker(RuntimeTransportSessionContext context)
        {
            return PublishDurableStopMarker(context);
        }

        internal static void RequestExternalRecovery(string reason) => SendSimple(WatchdogMessageType.ExternalRecoveryRequired, reason);

        internal static void NotifyMainUiReady(string reason)
        {
            RecordClientEvent("MainUiReady", reason ?? "MainWindowShown");
            SendSimple(WatchdogMessageType.MainUiReady, reason ?? "MainWindowShown");
        }

        internal static void NotifyRecoveryAttemptFailed(string reason)
        {
            var classification = RecoveryFailurePolicy.Classify(null, false, reason);
            NotifyRecoveryAttemptFailed(classification.Code, classification.Permanent, reason, reason, string.Empty);
        }

        internal static void NotifyRecoveryAttemptFailed(
            string failureCode,
            bool permanent,
            string reason,
            string detail,
            string contextSha256)
        {
            NotifyRecoveryAttemptFailed(
                CaptureRecoveryFailureReport(failureCode, reason),
                permanent,
                reason,
                detail,
                contextSha256);
        }

        internal static void NotifyRecoveryAttemptFailed(
            RecoveryFailureReport report,
            bool permanent,
            string reason,
            string detail,
            string contextSha256)
        {
            NotifyRecoveryAttemptFailedCoreAsync(
                    report, permanent, reason, detail, contextSha256)
                .GetAwaiter()
                .GetResult();
        }

        internal static Task<RecoveryFailureHandoffResult>
            NotifyRecoveryAttemptFailedAndAwaitReceiptAsync(
                string failureCode,
                bool permanent,
                string reason,
                string detail,
                string contextSha256)
        {
            return NotifyRecoveryAttemptFailedCoreAsync(
                CaptureRecoveryFailureReport(failureCode, reason),
                permanent,
                reason,
                detail,
                contextSha256);
        }

        private static async Task<RecoveryFailureHandoffResult>
            NotifyRecoveryAttemptFailedCoreAsync(
                RecoveryFailureReport report,
                bool permanent,
                string reason,
                string detail,
                string contextSha256)
        {
            var context = CaptureContext();
            if (context == null)
                return new RecoveryFailureHandoffResult
                {
                    Outcome = RecoveryFailureHandoffOutcome.NoActiveSession,
                    Detail = "RuntimeTransportSessionMissing"
                };
            report = RecoveryFailurePolicy.NormalizeReport(report);
            string takeoverCorrelationId;
            string takeoverReason;
            TryCaptureTakeoverContext(context, out takeoverCorrelationId, out takeoverReason);
            lock (context.State.Gate) context.State.LastFailureReport = report.Clone();
            var failureCode = report.RootCode;
            var failureOwner = string.IsNullOrWhiteSpace(takeoverCorrelationId) ? string.Empty : "WatchdogTakeover";
            if (RecoveryFailurePolicy.IsSupersededByWatchdogTakeover(
                    !string.IsNullOrWhiteSpace(takeoverCorrelationId),
                    takeoverCorrelationId,
                    failureOwner,
                    takeoverCorrelationId,
                    failureCode,
                    reason,
                    detail))
            {
                // The existing watchdog takeover correlation already owns
                // the safety action.  Registering a second durable failure
                // would create a second relaunch decision, so this path is a
                // local audit only.
                RecordClientEvent(
                    context,
                    "RecoveryFailureSuperseded",
                    "CorrelationId=" + takeoverCorrelationId +
                    ";Reason=" + (takeoverReason ?? string.Empty));
                FlushClientJournal(context);
                return new RecoveryFailureHandoffResult
                {
                    Outcome = RecoveryFailureHandoffOutcome.SupersededByTakeover,
                    Detail = "TakeoverCorrelationId=" + takeoverCorrelationId
                };
            }
            report = RecoveryFailurePolicy.NormalizeReport(report);
            lock (context.State.Gate) context.State.LastFailureReport = report.Clone();
            RecordClientEvent(context, "RecoveryAttemptFailed", $"Code={failureCode};Permanent={permanent};ContextSha256={contextSha256};{reason}");
            var correlationId = Guid.NewGuid().ToString("N");
            var request = new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryAttemptFailed,
                SessionId = context.SessionId,
                Reason = reason ?? string.Empty,
                RecoveryFailureCode = failureCode,
                RecoveryFailurePermanent = permanent,
                RecoveryFailureDetail = detail ?? string.Empty,
                RecoveryFailureContextSha256 = contextSha256 ?? string.Empty,
                RecoveryFailureOwner = failureOwner,
                RecoveryFailureCorrelationId = takeoverCorrelationId ?? string.Empty,
                RootCode = report.RootCode,
                DeviceOrChannelGroup = report.DeviceOrChannelGroup,
                RunId = report.RunId,
                RecoveryStage = report.RecoveryStage,
                RecoveryProgressToken = report.RecoveryProgressToken,
                RecoveryProcessSource = report.RecoveryProcessSource,
                RecoveryFailureFingerprint = RecoveryFailurePolicy.BuildFingerprint(report),
                CorrelationId = correlationId
            };
            if (!string.IsNullOrWhiteSpace(takeoverCorrelationId))
                RecordClientEvent(context, "RecoveryFailureTakeoverContext", $"Owner={failureOwner};CorrelationId={takeoverCorrelationId};Reason={takeoverReason}");
            var wire = WatchdogProtocol.Serialize(request);
            var requestPayloadSha256 = WatchdogProtocol.ComputeWireSha256(wire);
            var waiter = context.State.RegisterRecoveryFailureReceipt(
                correlationId,
                requestPayloadSha256);
            var started = Stopwatch.StartNew();
            try
            {
                while (started.ElapsedMilliseconds <
                       WatchdogTransportPolicy.RecoveryFailureReceiptDeadlineMs)
                {
                    if (waiter.Completion.Task.IsCompleted)
                        return BuildRecoveryFailureHandoff(
                            await waiter.Completion.Task.ConfigureAwait(false));
                    if (!ReferenceEquals(CaptureContext(), context))
                        throw new InvalidOperationException(
                            "RecoveryFailureContextReplacedBeforeReceipt");

                    var snapshot = CaptureTransportSnapshot();
                    if (snapshot != null && snapshot.IsStable &&
                        ReferenceEquals(snapshot.Context, context) &&
                        snapshot.Engine != null && snapshot.Engine.IsAttached &&
                        snapshot.Engine.ActiveConnectionGeneration > 0 &&
                        snapshot.Engine.AttachedConnectionGeneration ==
                        snapshot.Engine.ActiveConnectionGeneration)
                    {
                        var disposition =
                            TransportEngine.TrySendForSessionWithDisposition(
                                request,
                                context.SessionId,
                                context.SessionGeneration,
                                context.SessionLease);
                        RecordClientEvent(
                            context,
                            "RecoveryFailureRequestSend",
                            "CorrelationId=" + correlationId +
                            ";Disposition=" + disposition);
                        if (disposition == WatchdogSendDisposition.ScopeStale)
                            throw new InvalidOperationException(
                                "RecoveryFailureRequestScopeStale");
                    }

                    var completed = await Task.WhenAny(
                            waiter.Completion.Task,
                            Task.Delay(
                                WatchdogTransportPolicy.RecoveryFailureRequestRetryMs))
                        .ConfigureAwait(false);
                    if (ReferenceEquals(completed, waiter.Completion.Task))
                        return BuildRecoveryFailureHandoff(
                            await waiter.Completion.Task.ConfigureAwait(false));
                }
                throw new TimeoutException(
                    "Watchdog durable recovery failure receipt timeout.");
            }
            catch (Exception ex)
            {
                RecordClientEvent(
                    context,
                    "RecoveryFailureHandoffUnavailable",
                    ex.GetBaseException().Message);
                return new RecoveryFailureHandoffResult
                {
                    Outcome = RecoveryFailureHandoffOutcome.ReceiptUnavailable,
                    Detail = ex.GetBaseException().Message
                };
            }
            finally
            {
                context.State.RemoveRecoveryFailureReceipt(waiter);
                FlushClientJournal(context);
            }
        }

        private static RecoveryFailureHandoffResult BuildRecoveryFailureHandoff(
            RecoveryFailureReceipt receipt)
        {
            var outcome = RecoveryFailureHandoffOutcome.ReceiptUnavailable;
            switch (receipt?.Disposition)
            {
                case RecoveryFailureDispositions.RelaunchApproved:
                    outcome = RecoveryFailureHandoffOutcome.RelaunchApproved;
                    break;
                case RecoveryFailureDispositions.RelaunchAlreadyPending:
                    outcome = RecoveryFailureHandoffOutcome.RelaunchAlreadyPending;
                    break;
                case RecoveryFailureDispositions.CircuitOpen:
                    outcome = RecoveryFailureHandoffOutcome.CircuitOpen;
                    break;
                case RecoveryFailureDispositions.IdentityConflict:
                    outcome = RecoveryFailureHandoffOutcome.IdentityConflict;
                    break;
                case RecoveryFailureDispositions.Superseded:
                    outcome = RecoveryFailureHandoffOutcome.SupersededByTakeover;
                    break;
            }
            return new RecoveryFailureHandoffResult
            {
                Outcome = outcome,
                Receipt = receipt?.Clone(),
                Detail = receipt?.DetailCode ?? "RecoveryFailureReceiptMissing"
            };
        }

        private static void ObserveRecoveryFailureReceipt(
            RuntimeTransportSessionContext context,
            WatchdogMessage message)
        {
            if (context == null || message == null) return;
            var waiter = context.State.FindRecoveryFailureReceipt(
                message.CorrelationId);
            if (waiter == null)
            {
                RecordClientEvent(
                    context,
                    "RecoveryFailureReceiptIgnored",
                    "UnknownCorrelation=" +
                    (message.CorrelationId ?? string.Empty));
                return;
            }
            if (!WatchdogProtocol.TryValidateRecoveryFailureReceipt(
                    message,
                    context.SessionId,
                    waiter.CorrelationId,
                    waiter.RequestPayloadSha256,
                    out var receipt,
                    out var rejection))
            {
                RecordClientEvent(
                    context,
                    "RecoveryFailureReceiptRejected",
                    rejection ?? "InvalidReceipt");
                return;
            }
            if (!receipt.Durable ||
                (!receipt.FailureRegistered &&
                 !string.Equals(
                     receipt.Disposition,
                     RecoveryFailureDispositions.Superseded,
                     StringComparison.Ordinal)))
            {
                RecordClientEvent(
                    context,
                    "RecoveryFailureReceiptRejected",
                    "NonAuthoritativeReceipt");
                return;
            }
            waiter.Completion.TrySetResult(receipt);
        }

        private static RecoveryFailureReport CaptureRecoveryFailureReport(string failureCode, string reason)
        {
            var context = CaptureContext();
            if (context == null)
                return RecoveryFailurePolicy.NormalizeReport(new RecoveryFailureReport
                {
                    RootCode = string.IsNullOrWhiteSpace(failureCode) ? "UnknownFailure" : failureCode
                });
            WatchdogHeartbeat heartbeat = null;
            try
            {
                lock (HeartbeatCaptureGate) heartbeat = context.State.HeartbeatSource.Capture();
            }
            catch (Exception ex) { RaiseTransportError(context, "RecoveryFailureReport", ex.Message); }
            var report = new RecoveryFailureReport
            {
                RootCode = string.IsNullOrWhiteSpace(failureCode) ? "UnknownFailure" : failureCode,
                DeviceOrChannelGroup = heartbeat?.RecoveryIncident,
                RunId = heartbeat?.RunId,
                RunEpoch = heartbeat?.RunEpoch ?? 0,
                RecoveryStage = heartbeat?.RecoveryStage,
                RecoveryProgressToken = heartbeat == null ? string.Empty : heartbeat.RecoveryProgressVersion.ToString(CultureInfo.InvariantCulture),
                RecoveryProcessSource = context.RecoveryProcess ? RecoveryFailurePolicy.RecoveryProcessSource : RecoveryFailurePolicy.InitialProcessSource
            };
            if (string.IsNullOrWhiteSpace(report.DeviceOrChannelGroup))
                report.DeviceOrChannelGroup = string.Join(",", context.SelectedChannels);
            return RecoveryFailurePolicy.NormalizeReport(report);
        }

        internal static void NotifyRecoveryCheckpointValidated(string reason, WatchdogCheckpointMirror mirror)
        {
            SessionLifecycleGate.Wait();
            try
            {
                NotifyRecoveryCheckpointValidatedCore(reason, mirror);
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        private static void NotifyRecoveryCheckpointValidatedCore(
            string reason,
            WatchdogCheckpointMirror mirror)
        {
            var context = CaptureContext();
            if (context == null) return;
            RecordClientEvent(context, "RecoveryCheckpointValidated", reason);
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryCheckpointValidated,
                SessionId = context.SessionId,
                Reason = reason,
                CorrelationId = Guid.NewGuid().ToString("N"),
                CheckpointMirror = mirror
            });
            FlushClientJournal(context);
        }

        internal static void NotifySafetyPreflightPassed(string reason)
        {
            var context = CaptureContext();
            if (context == null) return;
            RecordClientEvent(context, "SafetyPreflightPassed", reason);
            SendSimple(WatchdogMessageType.SafetyPreflightPassed, reason);
            FlushClientJournal(context);
        }

        internal static void NotifyRecoveryBatchCommitted(string reason, long commitGeneration)
        {
            SessionLifecycleGate.Wait();
            try
            {
                NotifyRecoveryBatchCommittedCore(reason, commitGeneration);
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        private static void NotifyRecoveryBatchCommittedCore(string reason, long commitGeneration)
        {
            var context = CaptureContext();
            if (context == null) return;
            WatchdogHeartbeat heartbeat = null;
            try
            {
                lock (HeartbeatCaptureGate)
                    heartbeat = context.State.HeartbeatSource.Capture();
            }
            catch (Exception ex)
            {
                RaiseTransportError(context, "RecoveryBatchCommitContext", ex.Message);
            }
            try
            {
                WatchdogRecoveryCommitMarker.WriteLocal(
                    context.SessionId,
                    commitGeneration,
                    reason,
                    heartbeat?.RunId,
                    heartbeat?.RunEpoch ?? 0,
                    heartbeat?.RecoveryStage);
            }
            catch (Exception ex) { RaiseTransportError(context, "RecoveryCommitMarkerLocal", ex.Message); }
            _ = Task.Run(() =>
            {
                try
                {
                    WatchdogRecoveryCommitMarker.WriteProject(
                        context.JournalDirectory,
                        context.SessionId,
                        commitGeneration,
                        reason,
                        heartbeat?.RunId,
                        heartbeat?.RunEpoch ?? 0,
                        heartbeat?.RecoveryStage);
                }
                catch (Exception ex) { RaiseTransportError(context, "RecoveryCommitMarkerProject", ex.Message); }
            });
            RecordClientEvent(context, "RecoveryBatchCommitted", reason);
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.RecoveryBatchCommitted,
                SessionId = context.SessionId,
                Reason = reason,
                RunId = heartbeat?.RunId ?? string.Empty,
                RecoveryStage = heartbeat?.RecoveryStage ?? string.Empty,
                RecoveryProgressToken = heartbeat == null
                    ? string.Empty
                    : heartbeat.RecoveryProgressVersion.ToString(CultureInfo.InvariantCulture),
                Heartbeat = heartbeat,
                CorrelationId = Guid.NewGuid().ToString("N"),
                RecoveryCommitGeneration = commitGeneration
            });
            FlushClientJournal(context);
        }

        internal static void NotifyBatchStartFailed(string reason)
        {
            var context = CaptureContext();
            if (context == null) return;
            WatchdogHeartbeat heartbeat = null;
            try { lock (HeartbeatCaptureGate) heartbeat = context.State.HeartbeatSource.Capture(); }
            catch (Exception ex) { RaiseTransportError(context, "BatchStartFailureContext", ex.Message); }
            UnattendedRunCheckpoint checkpoint = null;
            try { checkpoint = UnattendedRunCheckpointStore.Load(); }
            catch (Exception ex) { RaiseTransportError(context, "BatchStartFailureCheckpoint", ex.Message); }
            var failureContext = new WatchdogBatchStartFailureContext
            {
                RunId = string.IsNullOrWhiteSpace(heartbeat?.RunId) ? checkpoint?.RunId : heartbeat.RunId,
                CheckpointRunId = checkpoint?.RunId,
                RunEpoch = heartbeat?.RunEpoch ?? checkpoint?.RunEpoch ?? 0,
                FormalRunCommitted = heartbeat?.RunActive == true && string.Equals(heartbeat.Phase, "Formal", StringComparison.OrdinalIgnoreCase),
                CheckpointArmed = checkpoint?.Armed == true,
                RecoveryProcess = context.RecoveryProcess
            };
            var policy = BatchStartTakeoverPolicy.ShouldTakeover(failureContext)
                ? "TakeoverEligible"
                : "SafeIdle:" + BatchStartTakeoverPolicy.DescribeRejection(failureContext);
            RecordClientEvent(context, "BatchStartFailed", policy + ";" + reason);
            Send(new WatchdogMessage
            {
                Type = WatchdogMessageType.BatchStartFailed,
                SessionId = context.SessionId,
                Reason = reason,
                CorrelationId = Guid.NewGuid().ToString("N"),
                BatchStartFailure = failureContext
            });
            FlushClientJournal(context);
        }

        internal static void NotifyManualStop(string reason)
        {
            RecordClientEvent("ManualStopIntent", reason);
            SendSimple(WatchdogMessageType.ManualStopIntent, reason);
            FlushClientJournal();
        }

        internal static void NotifyPhysicalStopConfirmed(string reason) => SendSimple(WatchdogMessageType.PhysicalStopConfirmed, reason);

        internal static void NotifyRunStopped(WatchdogStopSummary summary)
        {
            var context = CaptureContext();
            if (context == null) return;
            var fence = BeginSessionCloseExact(
                context,
                "RunStopped",
                Guid.Empty,
                Guid.Empty,
                0,
                0);
            if (fence.MarkOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch)
                return;
            RecordClientEvent(context, "RunStopped", summary?.Detail);
            Send(context, new WatchdogMessage
            {
                Type = WatchdogMessageType.RunStopped,
                SessionId = context.SessionId,
                StopSummary = summary
            });
            FlushClientJournal(context);
        }

        internal static void NotifyStopCompleted(WatchdogStopSummary summary, string reason)
        {
            var context = CaptureContext();
            if (context == null) return;
            RecordClientEvent(context, "StopCompleted", summary?.Detail ?? reason);
            Send(context, new WatchdogMessage
            {
                Type = WatchdogMessageType.StopCompleted,
                SessionId = context.SessionId,
                Reason = reason,
                StopSummary = summary
            });
        }

        internal static WatchdogSafetyHandoffReceipt RequestSafetyHandoff(
            RuntimeTransportSessionContext context,
            StopSafetyResult safety,
            bool hardwareResourcesReleased)
        {
            if (context == null || context.SessionLease <= 0 || safety == null ||
                safety.SafetyTransactionId == Guid.Empty || !safety.PersistenceBoundaryConfirmed ||
                !hardwareResourcesReleased ||
                Volatile.Read(ref context.State.SessionClosing) == 0 ||
                (context.PipelineState != RuntimeCallbackPipelineState.Closing &&
                 context.PipelineState != RuntimeCallbackPipelineState.Terminal))
                return null;
            try
            {
                WatchdogSafetyHandoffReceipt existing;
                if (WatchdogSafetyHandoffReceiptStore.TryRead(
                        context.JournalDirectory, context.SessionId, out existing))
                {
                    if (existing.SessionGeneration == context.SessionGeneration &&
                        existing.SessionLease == context.SessionLease &&
                        string.Equals(existing.StopSafetyTransactionId,
                            safety.SafetyTransactionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                        return existing;
                    return null;
                }
                var handoffId = Guid.NewGuid().ToString("N");
                WatchdogApplicationExitReceipt applicationExit;
                var hasTypedApplicationExit = WatchdogApplicationExitReceiptStore.TryRead(
                                                  context.JournalDirectory,
                                                  context.SessionId,
                                                  out applicationExit) &&
                                              applicationExit?.SchemaVersion >= 2 &&
                                              applicationExit.SessionGeneration ==
                                              context.SessionGeneration &&
                                              applicationExit.SessionLease ==
                                              context.SessionLease;
                var projectDirectory = ResolveProjectDirectory(context.JournalDirectory);
                var executableDirectory = Path.GetDirectoryName(
                    string.IsNullOrWhiteSpace(context.MainExecutablePath)
                        ? Assembly.GetEntryAssembly()?.Location ?? string.Empty
                        : context.MainExecutablePath) ?? Environment.CurrentDirectory;
                var configSnapshot = WatchdogSafetyConfigSnapshotStore.Create(
                    context.JournalDirectory,
                    handoffId,
                    Path.Combine(executableDirectory, "Config"),
                    string.IsNullOrWhiteSpace(projectDirectory)
                        ? string.Empty
                        : Path.Combine(projectDirectory, "Config"),
                    RuntimeBuildIdentity.Capture().ToStartupLogLine());
                if (configSnapshot?.Succeeded != true)
                    throw new InvalidOperationException(
                        configSnapshot?.Error ?? "SafetyConfigSnapshotUnavailable");
                var receipt = new WatchdogSafetyHandoffReceipt
                {
                    SchemaVersion = 2,
                    SessionId = context.SessionId,
                    SessionGeneration = context.SessionGeneration,
                    SessionLease = context.SessionLease,
                    HandoffId = handoffId,
                    Nonce = Guid.NewGuid().ToString("N"),
                    StopSafetyTransactionId = safety.SafetyTransactionId.ToString("N"),
                    RunId = safety.RunId == Guid.Empty ? string.Empty : safety.RunId.ToString("N"),
                    RunEpoch = safety.RunEpoch,
                    Revision = 1,
                    State = WatchdogSafetyHandoffState.Requested,
                    MotorsOff = safety.MotorOffCommandSucceeded,
                    PowerOff = safety.PowerOffConfirmed,
                    PressureSafe = safety.PressureSafeConfirmed,
                    PersistenceDrained = safety.PersistenceBoundaryConfirmed,
                    LogicalQuiescent = safety.LogicalQuiescenceConfirmed,
                    HardwareResourcesReleased = hardwareResourcesReleased,
                    ExecutionAuthorizationRevoked = true,
                    CallbacksIsolated = true,
                    ProjectDirectory = projectDirectory,
                    MainExecutablePath = context.MainExecutablePath,
                    ConfigSnapshotPath = configSnapshot.ConfigDirectory,
                    ConfigSnapshotManifestPath = configSnapshot.ManifestPath,
                    ConfigSnapshotManifestSha256 = configSnapshot.ManifestSha256,
                    ConfigSnapshotSchemaVersion = 1,
                    RelaunchDisposition = hasTypedApplicationExit
                        ? applicationExit.RelaunchDisposition
                        : WatchdogRelaunchDisposition.Forbidden,
                    RelaunchPermitGeneration = hasTypedApplicationExit
                        ? applicationExit.RelaunchPermitGeneration
                        : 0,
                    RelaunchPermitId = hasTypedApplicationExit
                        ? applicationExit.RelaunchPermitId
                        : string.Empty,
                    RelaunchPermitNonceSha256 = hasTypedApplicationExit
                        ? applicationExit.RelaunchPermitNonceSha256
                        : string.Empty,
                    Detail = "MainProcessRequestedBoundedSafetyHandoff"
                };
                WatchdogSafetyHandoffReceiptStore.WriteThrough(context.JournalDirectory, receipt);
                WatchdogClosingTombstone closing;
                if (WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory, context.SessionId, out closing) &&
                    IsExactClosingTombstoneIdentity(context, closing))
                {
                    closing.SafetyHandoffId = receipt.HandoffId;
                    closing.SafetyOwner = "SafetyHandoff";
                    closing.StateVersion++;
                    WatchdogClosingTombstoneStore.WriteThrough(context.JournalDirectory, closing);
                }
                Send(context, new WatchdogMessage
                {
                    Type = WatchdogMessageType.SafetyHandoffRequested,
                    SessionId = context.SessionId,
                    CorrelationId = receipt.HandoffId,
                    SafetyHandoff = ToWireSafetyHandoff(receipt)
                });
                RecordClientEvent(context, "SafetyHandoffRequested",
                    $"HandoffId={receipt.HandoffId};Revision={receipt.Revision}");
                FlushClientJournal(context);
                return receipt;
            }
            catch (Exception ex)
            {
                RecordClientEvent(context, "SafetyHandoffRequestFailed", ex.GetBaseException().Message);
                return null;
            }
        }

        private static WatchdogSafetyHandoff ToWireSafetyHandoff(
            WatchdogSafetyHandoffReceipt receipt)
        {
            return receipt == null ? null : new WatchdogSafetyHandoff
            {
                SessionId = receipt.SessionId,
                SessionGeneration = receipt.SessionGeneration,
                SessionLease = receipt.SessionLease,
                HandoffId = receipt.HandoffId,
                Nonce = receipt.Nonce,
                StopSafetyTransactionId = receipt.StopSafetyTransactionId,
                RunId = receipt.RunId,
                RunEpoch = receipt.RunEpoch,
                SidecarProcessId = receipt.SidecarProcessId,
                SidecarProcessStartUtcTicks = receipt.SidecarProcessStartUtcTicks,
                WorkerProcessId = receipt.WorkerProcessId,
                WorkerProcessStartUtcTicks = receipt.WorkerProcessStartUtcTicks,
                MotorsOff = receipt.MotorsOff,
                PowerOff = receipt.PowerOff,
                PressureSafe = receipt.PressureSafe,
                PersistenceDrained = receipt.PersistenceDrained,
                LogicalQuiescent = receipt.LogicalQuiescent,
                HardwareResourcesReleased = receipt.HardwareResourcesReleased,
                ExecutionAuthorizationRevoked = receipt.ExecutionAuthorizationRevoked,
                CallbacksIsolated = receipt.CallbacksIsolated,
                ConfigSnapshotManifestSha256 = receipt.ConfigSnapshotManifestSha256,
                RelaunchDisposition = receipt.RelaunchDisposition,
                RelaunchPermitGeneration = receipt.RelaunchPermitGeneration,
                RelaunchPermitId = receipt.RelaunchPermitId,
                RelaunchPermitNonceSha256 = receipt.RelaunchPermitNonceSha256,
                FailureCode = receipt.FailureCode,
                TimestampUtcTicks = receipt.UpdatedUtcTicks
            };
        }

        private static string ResolveProjectDirectory(string journalDirectory)
        {
            if (string.IsNullOrWhiteSpace(journalDirectory)) return string.Empty;
            try
            {
                var resolved = Path.GetFullPath(journalDirectory);
                return string.Equals(Path.GetFileName(resolved), "WatchdogSessions",
                        StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(resolved)?.FullName ?? resolved
                    : resolved;
            }
            catch { return string.Empty; }
        }

        internal static void NotifyRunCompleted()
        {
            var context = CaptureContext();
            if (context == null) return;
            var fence = BeginSessionCloseExact(
                context,
                "FormalRunCompleted",
                Guid.Empty,
                Guid.Empty,
                0,
                0,
                WatchdogExitDisposition.NormalCompletionExit,
                WatchdogRelaunchDisposition.Forbidden);
            if (fence.MarkOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch)
                return;
            RecordClientEvent(context, "RunCompleted", "FormalRunCompleted");
            Send(context, new WatchdogMessage
            {
                Type = WatchdogMessageType.RunCompleted,
                SessionId = context.SessionId,
                Reason = "FormalRunCompleted",
                CorrelationId = Guid.NewGuid().ToString("N")
            });
            FlushClientJournal(context);
        }

        internal static void NotifyApplicationClosing()
        {
            var context = CaptureContext();
            if (context == null) return;
            var fence = BeginSessionCloseExact(
                context,
                "ApplicationClosing",
                Guid.Empty,
                Guid.Empty,
                0,
                0);
            if (fence.MarkOutcome == RuntimeShutdownMarkOutcome.IdentityMismatch)
                return;
            RecordClientEvent(context, "ApplicationClosing", "ApplicationClosing");
            Send(context, new WatchdogMessage
            {
                Type = WatchdogMessageType.ApplicationClosing,
                SessionId = context.SessionId,
                Reason = "ApplicationClosing",
                CorrelationId = Guid.NewGuid().ToString("N")
            });
            FlushClientJournal(context);
        }

        internal static WatchdogApplicationExitReceipt ArmApplicationExitDeadline(
            string reason,
            TimeSpan hardDeadline)
        {
            return ArmApplicationExitDeadline(
                reason,
                hardDeadline,
                RuntimeShutdownIntent.ApplicationExit,
                null);
        }

        internal static WatchdogApplicationExitReceipt ArmApplicationExitDeadline(
            string reason,
            TimeSpan hardDeadline,
            RuntimeShutdownIntent shutdownIntent,
            string takeoverTransactionId)
        {
            var context = CaptureContextOrLastDetached();
            if (context == null || context.SessionLease <= 0) return null;
            try
            {
                var preservePermit =
                    shutdownIntent == RuntimeShutdownIntent.WatchdogTakeoverExit ||
                    shutdownIntent == RuntimeShutdownIntent.WatchdogRecoveryExit;
                WatchdogClosingTombstone durableClosing;
                var durableClosingDisposition =
                    WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out durableClosing) &&
                    durableClosing.SessionGeneration == context.SessionGeneration &&
                    durableClosing.SessionLease == context.SessionLease
                        ? durableClosing.ExitDisposition
                        : WatchdogExitDisposition.OperatorExit;
                var exitDisposition = WatchdogExitDispositionPolicy.ResolveApplicationExit(
                    preservePermit,
                    durableClosingDisposition);
                var relaunchDisposition = preservePermit
                    ? WatchdogRelaunchDisposition.PreserveApprovedPermit
                    : WatchdogRelaunchDisposition.Forbidden;
                if (preservePermit &&
                    !Guid.TryParseExact(
                        takeoverTransactionId ?? string.Empty,
                        "N",
                        out _))
                    return null;

                WatchdogApplicationExitReceipt existing;
                if (WatchdogApplicationExitReceiptStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out existing) &&
                    existing.SessionGeneration == context.SessionGeneration &&
                    existing.SessionLease == context.SessionLease)
                    return existing.SchemaVersion >= 2 &&
                           existing.ExitDisposition == exitDisposition &&
                           existing.RelaunchDisposition == relaunchDisposition
                        ? existing
                        : null;

                var permit = preservePermit
                    ? AwaitApprovedRelaunchPermit(context, TimeSpan.FromSeconds(2))
                    : null;
                if (preservePermit && permit == null) return null;

                var now = DateTime.UtcNow;
                var bounded = hardDeadline <= TimeSpan.Zero ||
                              hardDeadline > TimeSpan.FromSeconds(30)
                    ? TimeSpan.FromSeconds(30)
                    : hardDeadline;
                using (var process = Process.GetCurrentProcess())
                {
                    var receipt = new WatchdogApplicationExitReceipt
                    {
                        SchemaVersion = 2,
                        SessionId = context.SessionId,
                        SessionGeneration = context.SessionGeneration,
                        SessionLease = context.SessionLease,
                        ExitIntentId = Guid.NewGuid().ToString("N"),
                        TakeoverTransactionId = preservePermit
                            ? takeoverTransactionId
                            : string.Empty,
                        ExitDisposition = exitDisposition,
                        RelaunchDisposition = relaunchDisposition,
                        RelaunchPermitGeneration = permit?.Generation ?? 0,
                        RelaunchPermitId = permit?.PermitId ?? string.Empty,
                        RelaunchPermitNonceSha256 = permit == null
                            ? string.Empty
                            : WatchdogTakeoverPermitBindingPolicy.HashNonce(
                                permit.PermitNonce),
                        Revision = 1,
                        State = WatchdogApplicationExitState.Requested,
                        MainProcessId = process.Id,
                        MainProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        RequestedUtcTicks = now.Ticks,
                        DiagnosticDeadlineUtcTicks = now.AddSeconds(25).Ticks,
                        HardDeadlineUtcTicks = now.Add(bounded).Ticks,
                        Reason = reason ?? "ApplicationExitRequested",
                        Detail = "ExactProcessExitDeadlineArmed"
                    };
                    WatchdogApplicationExitReceiptStore.WriteThrough(
                        context.JournalDirectory,
                        receipt);
                    RecordClientEvent(
                        context,
                        "ApplicationExitRequested",
                        $"Intent={receipt.ExitIntentId};PID={receipt.MainProcessId};" +
                        $"Disposition={receipt.ExitDisposition};" +
                        $"Relaunch={receipt.RelaunchDisposition};" +
                        $"PermitGeneration={receipt.RelaunchPermitGeneration};" +
                        $"HardDeadlineUtcTicks={receipt.HardDeadlineUtcTicks};Reason={receipt.Reason}");
                    Send(context, new WatchdogMessage
                    {
                        Type = WatchdogMessageType.ApplicationExitRequested,
                        SessionId = context.SessionId,
                        CorrelationId = receipt.ExitIntentId,
                        Reason = receipt.Reason,
                        ApplicationExit = receipt
                    });
                    FlushClientJournal(context);
                    return receipt;
                }
            }
            catch (Exception ex)
            {
                RecordClientEvent(context, "ApplicationExitDeadlineArmFailed", ex.GetBaseException().Message);
                FlushClientJournal(context);
                return null;
            }
        }

        private static DurableRelaunchAuthorityRecord AwaitApprovedRelaunchPermit(
            RuntimeTransportSessionContext context,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(2)
                : timeout);
            do
            {
                var opened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(
                    context.JournalDirectory,
                    context.SessionId);
                var record = opened?.Authority?.Snapshot;
                if (record != null &&
                    record.State == DurableRelaunchPermitState.Approved &&
                    record.Generation > 0 &&
                    !string.IsNullOrWhiteSpace(record.PermitId) &&
                    !string.IsNullOrWhiteSpace(record.PermitNonce))
                    return record;
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(25);
            } while (true);
            return null;
        }

        internal static void MarkApplicationExitGraceful(string detail)
        {
            UpdateApplicationExitDisposition(
                WatchdogApplicationExitState.GracefulCompleted,
                false,
                detail);
        }

        internal static void MarkApplicationExitDeadlineForced(string detail)
        {
            UpdateApplicationExitDisposition(
                WatchdogApplicationExitState.ForcedDeadlineExit,
                true,
                detail);
        }

        private static void UpdateApplicationExitDisposition(
            WatchdogApplicationExitState state,
            bool terminationRequested,
            string detail)
        {
            var context = CaptureContext();
            if (context == null) return;
            try
            {
                WatchdogApplicationExitReceipt receipt;
                if (!WatchdogApplicationExitReceiptStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out receipt) ||
                    receipt.SessionGeneration != context.SessionGeneration ||
                    receipt.SessionLease != context.SessionLease ||
                    state < receipt.State)
                    return;
                receipt.State = state;
                receipt.ProcessTerminationRequested |= terminationRequested;
                receipt.Revision++;
                receipt.Detail = detail ?? string.Empty;
                WatchdogApplicationExitReceiptStore.WriteThrough(
                    context.JournalDirectory,
                    receipt);
                RecordClientEvent(
                    context,
                    state.ToString(),
                    receipt.Detail);
                FlushClientJournal(context);
            }
            catch (Exception ex)
            {
                RecordClientEvent(
                    context,
                    "ApplicationExitDispositionCommitFailed",
                    ex.GetBaseException().Message);
            }
        }

        private static void UpdateApplicationExitSafety(
            RuntimeTransportSessionContext context,
            StopSafetyResult result)
        {
            if (context == null || result == null) return;
            try
            {
                WatchdogApplicationExitReceipt receipt;
                if (!WatchdogApplicationExitReceiptStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out receipt) ||
                    receipt.SessionGeneration != context.SessionGeneration ||
                    receipt.SessionLease != context.SessionLease)
                    return;
                receipt.StopSafetyTransactionId = result.SafetyTransactionId == Guid.Empty
                    ? receipt.StopSafetyTransactionId
                    : result.SafetyTransactionId.ToString("N");
                receipt.MotorsOff = result.MotorOffCommandSucceeded;
                receipt.PowerOff = result.PowerOffConfirmed;
                receipt.PressureSafe = result.PressureSafeConfirmed;
                receipt.PersistenceDrained = result.PersistenceBoundaryConfirmed;
                receipt.LogicalQuiescent = result.LogicalQuiescenceConfirmed;
                receipt.DataContinuityVerified = !result.DataContinuityCompromised;
                receipt.Revision++;
                receipt.Detail = "FinalStopSafetyResultCommitted";
                WatchdogApplicationExitReceiptStore.WriteThrough(
                    context.JournalDirectory,
                    receipt);
            }
            catch (Exception ex)
            {
                RecordClientEvent(context, "ApplicationExitSafetyCommitFailed", ex.GetBaseException().Message);
            }
        }

        /// <summary>
        /// Sends the protocol close marker using one frozen Engine identity.
        /// Retention shutdown calls this immediately before detaching the
        /// transport; unlike the UI notification path it does not mutate the
        /// process-wide Runtime context or journal state.  A detached/closed
        /// Engine simply returns false and the caller continues collecting its
        /// normal shutdown receipt.
        /// </summary>
        internal static bool SendApplicationClosingForShutdown(
            WatchdogClientTransportEngine engine,
            bool processExitExpected = false)
        {
            return SendApplicationClosingForShutdown(
                engine,
                processExitExpected
                    ? RuntimeShutdownIntent.ApplicationExit
                    : RuntimeShutdownIntent.SessionClose);
        }

        internal static bool SendApplicationClosingForShutdown(
            WatchdogClientTransportEngine engine,
            RuntimeShutdownIntent shutdownIntent)
        {
            if (engine == null) return false;
            var snapshot = engine.CaptureSnapshot();
            if (snapshot == null || !snapshot.SessionActive ||
                !snapshot.IsAttached || snapshot.ActiveSessionLease <= 0 ||
                snapshot.SessionGeneration <= 0 ||
                string.IsNullOrWhiteSpace(snapshot.SessionId))
                return false;

            return engine.TrySendForSession(
                new WatchdogMessage
                {
                    ProtocolVersion = WatchdogProtocol.Version,
                    // Only the final application-exit owner asks the sidecar
                    // to prove that the main PID really disappears.  Ordinary
                    // session shutdown/rebind retains the compatible immediate
                    // ApplicationClosing semantics.
                    Type = SelectShutdownMessageTypeForRetention(
                        shutdownIntent),
                    SessionId = snapshot.SessionId,
                    Reason = "RuntimeShutdown"
                },
                snapshot.SessionId,
                snapshot.SessionGeneration,
                snapshot.ActiveSessionLease);
        }

        internal static string SelectShutdownMessageTypeForRetention(
            bool processExitExpected)
        {
            return SelectShutdownMessageTypeForRetention(
                processExitExpected
                    ? RuntimeShutdownIntent.ApplicationExit
                    : RuntimeShutdownIntent.SessionClose);
        }

        internal static string SelectShutdownMessageTypeForRetention(
            RuntimeShutdownIntent shutdownIntent)
        {
            switch (shutdownIntent)
            {
                case RuntimeShutdownIntent.ApplicationExit:
                    return WatchdogMessageType.ShutdownExpected;
                case RuntimeShutdownIntent.WatchdogTakeoverExit:
                case RuntimeShutdownIntent.WatchdogRecoveryExit:
                    return WatchdogMessageType.WatchdogTakeoverExit;
                default:
                    return WatchdogMessageType.ApplicationClosing;
            }
        }

        private static RuntimeTransportSessionContext CaptureContext()
        {
            lock (Gate) return _activeContext;
        }

        private static RuntimeTransportSessionContext CaptureContextOrLastDetached()
        {
            lock (Gate) return _activeContext ?? _lastDetachedContext;
        }

        /// <summary>
        /// Captures one stable Runtime/Engine composite for a callback.  A
        /// callback may finish its frozen context journal even after this
        /// check fails, but it must not publish a process-global event unless
        /// the active context reference and all three public session
        /// identities still match the same Engine snapshot.
        /// </summary>
        private static bool TryCaptureCurrentContextEngine(
            RuntimeTransportSessionContext context,
            out WatchdogClientTransportSnapshot engine)
        {
            engine = null;
            if (context == null || context.SessionLease <= 0) return false;
            lock (Gate)
            {
                if (!ReferenceEquals(_activeContext, context)) return false;
            }

            var composite = CaptureTransportSnapshot();
            if (composite == null || !composite.IsStable ||
                !ReferenceEquals(composite.Context, context) || composite.Engine == null)
                return false;
            var snapshot = composite.Engine;
            if (!snapshot.SessionActive || snapshot.ActiveSessionLease <= 0 ||
                snapshot.SessionLease != snapshot.ActiveSessionLease ||
                snapshot.SessionLease != context.SessionLease ||
                snapshot.SessionGeneration != context.SessionGeneration ||
                !string.Equals(snapshot.SessionId, context.SessionId, StringComparison.Ordinal))
                return false;

            lock (Gate)
            {
                if (!ReferenceEquals(_activeContext, context)) return false;
            }
            engine = snapshot;
            return true;
        }

        internal static RuntimeShutdownMarkOutcome TryMarkSessionClosing(
            RuntimeTransportSessionContext context)
        {
            var validation = ValidateExactClosingIdentity(context);
            if (validation != RuntimeShutdownMarkOutcome.Marked)
                return validation;
            var mark = TransportEngine.TryMarkSessionClosingExact(
                context.SessionId,
                context.SessionGeneration,
                context.SessionLease);
            if (mark == ExactSessionClosingResult.Marked)
                return RuntimeShutdownMarkOutcome.Marked;
            if (mark == ExactSessionClosingResult.ExactSessionDetached)
                return RuntimeShutdownMarkOutcome.ExactSessionDetached;
            if (mark == ExactSessionClosingResult.IdentityMismatch)
                return RuntimeShutdownMarkOutcome.IdentityMismatch;

            // The Engine may have detached between the stable capture and
            // the mark CAS.  Re-read its immutable snapshot to distinguish a
            // normal no-session race from an identity collision; never close
            // a replacement session on a stale context.
            var after = CaptureTransportSnapshot()?.Engine;
            if (after == null || !after.SessionActive || after.ActiveSessionLease <= 0)
                return RuntimeShutdownMarkOutcome.NoEngineSession;
            return RuntimeShutdownMarkOutcome.IdentityMismatch;
        }

        private static RuntimeShutdownMarkOutcome ValidateExactClosingIdentity(
            RuntimeTransportSessionContext context)
        {
            if (context == null || context.SessionLease <= 0)
                return RuntimeShutdownMarkOutcome.NoEngineSession;
            // Validation is read-only and precedes creation of the durable
            // tombstone.  A stale Runtime context must never fence a newer
            // Engine session merely because both happen to be present.
            var composite = CaptureTransportSnapshot();
            if (composite == null || composite.Engine == null)
                return RuntimeShutdownMarkOutcome.NoEngineSession;
            var snapshot = composite.Engine;
            if (!snapshot.SessionActive || snapshot.ActiveSessionLease <= 0)
                return RuntimeShutdownMarkOutcome.NoEngineSession;
            if (!composite.IsStable || !ReferenceEquals(composite.Context, context))
                return RuntimeShutdownMarkOutcome.IdentityMismatch;
            if (snapshot.SessionLease != snapshot.ActiveSessionLease ||
                snapshot.SessionLease != context.SessionLease ||
                snapshot.SessionGeneration != context.SessionGeneration ||
                !string.Equals(snapshot.SessionId, context.SessionId, StringComparison.Ordinal))
                return RuntimeShutdownMarkOutcome.IdentityMismatch;
            return snapshot.ActiveConnectionGeneration == 0
                ? RuntimeShutdownMarkOutcome.ExactSessionDetached
                : RuntimeShutdownMarkOutcome.Marked;
        }

        internal static RuntimeSessionCloseFenceReceipt BeginSessionCloseExact(
            RuntimeTransportSessionContext context,
            string closeIntent,
            Guid stopSafetyTransactionId,
            Guid stopRunId,
            long stopRunEpoch,
            long stopSafetyBoundaryGeneration,
            WatchdogExitDisposition exitDisposition = WatchdogExitDisposition.OperatorExit,
            WatchdogRelaunchDisposition relaunchDisposition =
                WatchdogRelaunchDisposition.Forbidden)
        {
            var receipt = new RuntimeSessionCloseFenceReceipt { Context = context };
            if (context == null || context.SessionLease <= 0)
            {
                receipt.MarkOutcome = RuntimeShutdownMarkOutcome.NoEngineSession;
                receipt.Error = "Watchdog exact close context is missing.";
                return receipt;
            }

            lock (Gate)
            {
                if (!ReferenceEquals(_activeContext, context))
                    return RejectSessionCloseFence(
                        receipt,
                        context,
                        "ActiveContextChanged");
            }

            WatchdogClosingTombstone previous;
            var hasPrevious = WatchdogClosingTombstoneStore.TryRead(
                context.JournalDirectory,
                context.SessionId,
                out previous);
            WatchdogApplicationExitReceipt typedExit;
            var hasTypedExit = WatchdogApplicationExitReceiptStore.TryRead(
                                   context.JournalDirectory,
                                   context.SessionId,
                                   out typedExit) &&
                               typedExit?.SchemaVersion >= 2 &&
                               typedExit.SessionGeneration == context.SessionGeneration &&
                               typedExit.SessionLease == context.SessionLease;
            if (hasTypedExit)
            {
                exitDisposition = typedExit.ExitDisposition;
                relaunchDisposition = typedExit.RelaunchDisposition;
            }
            if (hasPrevious &&
                (!IsExactClosingTombstoneIdentity(context, previous) ||
                 !IsCompatibleClosingTransaction(
                     previous,
                     stopSafetyTransactionId,
                     stopRunId,
                     stopRunEpoch,
                     stopSafetyBoundaryGeneration)))
                return RejectSessionCloseFence(
                    receipt,
                    context,
                    "DurableCloseFenceIdentityChanged",
                    previous);

            // The Engine identity CAS is the first irreversible in-memory
            // transition for a new fence.  Once this exact Runtime context is
            // already Closing and the same durable transaction exists, do not
            // repeat the Engine CAS: StopCompleted may have closed the pipe and
            // cleared only its connection generation while the public session
            // tuple remains the same.  Revalidate that public tuple (or prove
            // that the Engine has detached) and reuse the original fence.
            var locallyClosing = Volatile.Read(ref context.State.SessionClosing) != 0;
            var mark = hasPrevious && locallyClosing
                ? ValidateExactClosingIdentity(context)
                : TryMarkSessionClosing(context);
            if (mark == RuntimeShutdownMarkOutcome.IdentityMismatch)
                return RejectSessionCloseFence(
                    receipt,
                    context,
                    "EngineIdentityChanged",
                    previous);

            Volatile.Write(ref context.State.SessionClosing, 1);
            context.TrySetPipelineState(RuntimeCallbackPipelineState.Closing);
            context.IngressGate.BeginClosing();

            if (hasPrevious)
            {
                if (previous.SchemaVersion < 4)
                {
                    if (relaunchDisposition ==
                            WatchdogRelaunchDisposition.PreserveApprovedPermit &&
                        !hasTypedExit)
                        return RejectSessionCloseFence(
                            receipt,
                            context,
                            "TakeoverCloseFenceMissingTypedExit",
                            previous);
                    previous.SchemaVersion = 4;
                    previous.ExitDisposition = exitDisposition;
                    previous.RelaunchDisposition = relaunchDisposition;
                    previous.TakeoverTransactionId = hasTypedExit
                        ? typedExit.TakeoverTransactionId
                        : string.Empty;
                    previous.RelaunchPermitGeneration = hasTypedExit
                        ? typedExit.RelaunchPermitGeneration
                        : 0;
                    previous.RelaunchPermitId = hasTypedExit
                        ? typedExit.RelaunchPermitId
                        : string.Empty;
                    previous.RelaunchPermitNonceSha256 = hasTypedExit
                        ? typedExit.RelaunchPermitNonceSha256
                        : string.Empty;
                    previous.StateVersion++;
                    try
                    {
                        previous = WatchdogClosingTombstoneStore.WriteThrough(
                            context.JournalDirectory,
                            previous);
                    }
                    catch (Exception ex)
                    {
                        receipt.Error = ex.GetBaseException().Message;
                        receipt.MarkOutcome =
                            RuntimeShutdownMarkOutcome.TombstonePersistenceFailed;
                        return receipt;
                    }
                }
                receipt.Tombstone = previous;
                receipt.TombstoneDurable = true;
                receipt.MarkOutcome = mark;
                RecordClientEvent(
                    context,
                    "WatchdogClosingTombstoneReused",
                    $"State={previous.State};StateVersion={previous.StateVersion};" +
                    $"Generation={context.SessionGeneration};Lease={context.SessionLease};" +
                    $"Intent={closeIntent};Mark={mark}");
                FlushClientJournal(context);
                return receipt;
            }

            const long version = 1;
            var tombstone = new WatchdogClosingTombstone
            {
                SchemaVersion = 4,
                SessionId = context.SessionId,
                SessionGeneration = context.SessionGeneration,
                SessionLease = context.SessionLease,
                CloseIntent = closeIntent ?? string.Empty,
                ExitDisposition = exitDisposition,
                RelaunchDisposition = relaunchDisposition,
                TakeoverTransactionId = hasTypedExit
                    ? typedExit.TakeoverTransactionId
                    : string.Empty,
                RelaunchPermitGeneration = hasTypedExit
                    ? typedExit.RelaunchPermitGeneration
                    : 0,
                RelaunchPermitId = hasTypedExit
                    ? typedExit.RelaunchPermitId
                    : string.Empty,
                RelaunchPermitNonceSha256 = hasTypedExit
                    ? typedExit.RelaunchPermitNonceSha256
                    : string.Empty,
                StopSafetyTransactionId = stopSafetyTransactionId == Guid.Empty
                    ? previous?.StopSafetyTransactionId ?? string.Empty
                    : stopSafetyTransactionId.ToString("N"),
                StopRunId = stopRunId == Guid.Empty
                    ? previous?.StopRunId ?? string.Empty
                    : stopRunId.ToString("N"),
                StopRunEpoch = stopRunEpoch > 0
                    ? stopRunEpoch
                    : previous?.StopRunEpoch ?? 0,
                StopSafetyBoundaryGeneration = stopSafetyBoundaryGeneration > 0
                    ? stopSafetyBoundaryGeneration
                    : previous?.StopSafetyBoundaryGeneration ?? 0,
                StateVersion = version,
                State = WatchdogClosingTombstoneState.Closing,
                SafetyStage = WatchdogClosingSafetyStage.ClosingIntent,
                SafetyOwner = "MainProcess",
                TerminalReason = closeIntent ?? string.Empty
            };

            try
            {
                receipt.Tombstone = WatchdogClosingTombstoneStore.WriteThrough(
                    context.JournalDirectory,
                    tombstone);
                receipt.TombstoneDurable = true;
            }
            catch (Exception ex)
            {
                receipt.Error = ex.GetBaseException().Message;
                receipt.MarkOutcome = RuntimeShutdownMarkOutcome.TombstonePersistenceFailed;
                try { WriteSessionRevocationMarker(context, "ClosingTombstoneWriteFailed"); }
                catch { }
                RecordClientEvent(
                    context,
                    "WatchdogClosingTombstonePersistenceFailed",
                    DescribeSessionCloseIdentity(context, null) +
                    ";Error=" + receipt.Error);
                FlushClientJournal(context);
                return receipt;
            }

            receipt.MarkOutcome = mark;
            RecordClientEvent(
                context,
                "WatchdogClosingTombstone",
                $"StateVersion={version};Generation={context.SessionGeneration};" +
                $"Lease={context.SessionLease};Intent={closeIntent}");
            FlushClientJournal(context);
            return receipt;
        }

        internal static bool CompleteSessionCloseTombstone(
            RuntimeTransportSessionContext context,
            string terminalReason)
        {
            if (context == null || context.SessionLease <= 0) return true;
            try
            {
                WatchdogClosingTombstone existing;
                if (!WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out existing))
                    return false;
                if (existing.SessionGeneration != context.SessionGeneration ||
                    existing.SessionLease != context.SessionLease)
                    return false;
                if (existing.State == WatchdogClosingTombstoneState.Terminal)
                    return existing.IsSafetyTerminal;
                if (!existing.MotorsOff || !existing.PowerOff || !existing.PressureSafe ||
                    !existing.PersistenceDrained || !existing.LogicalQuiescent)
                    return false;
                existing.State = WatchdogClosingTombstoneState.Terminal;
                existing.SafetyStage = WatchdogClosingSafetyStage.Terminal;
                existing.StateVersion = Math.Max(1, existing.StateVersion + 1);
                existing.TerminalReason = terminalReason ?? "RuntimeShutdownTerminal";
                WatchdogClosingTombstoneStore.WriteThrough(
                    context.JournalDirectory,
                    existing);
                if (!existing.PreservesApprovedPermit)
                    WriteSessionRevocationMarker(
                        context,
                        existing.TerminalReason);
                return true;
            }
            catch { return false; }
        }

        internal static bool AdvanceSessionCloseSafety(
            RuntimeTransportSessionContext context,
            StopSafetyProgressSnapshot progress)
        {
            if (context == null || progress == null || progress.TransactionId == Guid.Empty)
                return false;
            try
            {
                WatchdogClosingTombstone existing;
                if (!WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out existing) ||
                    !IsExactClosingTombstoneIdentity(context, existing) ||
                    !string.Equals(existing.StopSafetyTransactionId,
                        progress.TransactionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (progress.ProgressVersion < existing.ControllerProgressVersion)
                    return false;
                if (progress.ProgressVersion == existing.ControllerProgressVersion &&
                    (int)progress.Stage <= existing.ControllerStopStage)
                    return true;
                existing.SchemaVersion = Math.Max(3, existing.SchemaVersion);
                existing.ControllerStopStage = Math.Max(
                    existing.ControllerStopStage,
                    (int)progress.Stage);
                existing.ControllerProgressVersion = Math.Max(
                    existing.ControllerProgressVersion,
                    progress.ProgressVersion);
                existing.ControllerProgressDetail = progress.Detail ?? string.Empty;
                existing.StateVersion++;
                WatchdogClosingTombstoneStore.WriteThrough(context.JournalDirectory, existing);
                return true;
            }
            catch { return false; }
        }

        internal static bool AdvanceSessionCloseSafety(
            RuntimeTransportSessionContext context,
            StopSafetyResult result)
        {
            if (context == null || result == null || result.SafetyTransactionId == Guid.Empty)
                return false;
            try
            {
                WatchdogClosingTombstone existing;
                if (!WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out existing) ||
                    !IsExactClosingTombstoneIdentity(context, existing) ||
                    !string.Equals(existing.StopSafetyTransactionId,
                        result.SafetyTransactionId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                    return false;
                existing.SchemaVersion = Math.Max(3, existing.SchemaVersion);
                existing.MotorsOff = result.MotorOffCommandSucceeded;
                existing.PowerOff = result.PowerOffConfirmed;
                existing.PressureSafe = result.PressureSafeConfirmed;
                existing.PersistenceDrained = result.PersistenceBoundaryConfirmed;
                existing.LogicalQuiescent = result.LogicalQuiescenceConfirmed;
                existing.DataContinuityVerified = !result.DataContinuityCompromised;
                existing.FinalSafetyResultCommitted = true;
                var finalStage = result.LogicalQuiescenceConfirmed
                    ? WatchdogClosingSafetyStage.LogicalQuiescent
                    : result.PersistenceBoundaryConfirmed
                        ? WatchdogClosingSafetyStage.DataDrained
                        : result.PhysicalSafetyConfirmed
                            ? WatchdogClosingSafetyStage.PhysicalSafe
                            : WatchdogClosingSafetyStage.ClosingIntent;
                existing.SafetyStage = existing.SafetyStage > finalStage
                    ? existing.SafetyStage
                    : finalStage;
                existing.StateVersion++;
                existing.TerminalReason = result.Outcome + ":" + (result.StageError ?? string.Empty);
                WatchdogClosingTombstoneStore.WriteThrough(context.JournalDirectory, existing);
                UpdateApplicationExitSafety(context, result);
                return true;
            }
            catch { return false; }
        }

        internal static bool IsFinalSessionCloseSafetyDurable(
            RuntimeTransportSessionContext context)
        {
            if (context == null || context.SessionLease <= 0) return true;
            try
            {
                WatchdogClosingTombstone existing;
                if (!WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out existing) ||
                    !IsExactClosingTombstoneIdentity(context, existing))
                    return false;
                // Schema 1/2 records predate the explicit marker. Preserve their
                // readability; schema 3 requires the final Controller result when
                // an exact StopSafety transaction is bound to the close fence.
                return existing.SchemaVersion < 3 ||
                       string.IsNullOrWhiteSpace(existing.StopSafetyTransactionId) ||
                       existing.FinalSafetyResultCommitted;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsExactClosingTombstoneIdentity(
            RuntimeTransportSessionContext context,
            WatchdogClosingTombstone tombstone)
        {
            return context != null && tombstone != null &&
                   string.Equals(
                       context.SessionId,
                       tombstone.SessionId,
                       StringComparison.Ordinal) &&
                   context.SessionGeneration == tombstone.SessionGeneration &&
                   context.SessionLease == tombstone.SessionLease;
        }

        private static bool IsCompatibleClosingTransaction(
            WatchdogClosingTombstone tombstone,
            Guid stopSafetyTransactionId,
            Guid stopRunId,
            long stopRunEpoch,
            long stopSafetyBoundaryGeneration)
        {
            if (tombstone == null) return false;
            if (stopSafetyTransactionId != Guid.Empty &&
                !string.Equals(
                    tombstone.StopSafetyTransactionId,
                    stopSafetyTransactionId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (stopRunId != Guid.Empty &&
                !string.Equals(
                    tombstone.StopRunId,
                    stopRunId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (stopRunEpoch > 0 && tombstone.StopRunEpoch != stopRunEpoch)
                return false;
            if (stopSafetyBoundaryGeneration > 0 &&
                tombstone.StopSafetyBoundaryGeneration != stopSafetyBoundaryGeneration)
                return false;
            return true;
        }

        private static RuntimeSessionCloseFenceReceipt RejectSessionCloseFence(
            RuntimeSessionCloseFenceReceipt receipt,
            RuntimeTransportSessionContext context,
            string reason,
            WatchdogClosingTombstone tombstone = null)
        {
            receipt.MarkOutcome = RuntimeShutdownMarkOutcome.IdentityMismatch;
            receipt.Tombstone = tombstone;
            receipt.TombstoneDurable = tombstone != null;
            receipt.Error = reason + ";" +
                            DescribeSessionCloseIdentity(context, tombstone);
            RecordClientEvent(
                context,
                "WatchdogClosingIdentityMismatch",
                receipt.Error);
            FlushClientJournal(context);
            return receipt;
        }

        internal static string DescribeSessionCloseIdentity(
            RuntimeTransportSessionContext context,
            WatchdogClosingTombstone knownTombstone = null)
        {
            RuntimeTransportSnapshot composite = null;
            try { composite = CaptureTransportSnapshot(); }
            catch { }
            var engine = composite?.Engine;
            var tombstone = knownTombstone;
            if (tombstone == null && context != null)
            {
                try
                {
                    WatchdogClosingTombstoneStore.TryRead(
                        context.JournalDirectory,
                        context.SessionId,
                        out tombstone);
                }
                catch { }
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "ExpectedSession={0};ExpectedGeneration={1};ExpectedLease={2};" +
                "ContextStable={3};ContextReferenceExact={4};" +
                "EngineActive={5};EngineAttached={6};CurrentSession={7};" +
                "CurrentGeneration={8};CurrentLease={9};ActiveLease={10};" +
                "TombstoneState={11};TombstoneVersion={12};" +
                "TombstoneGeneration={13};TombstoneLease={14}",
                context?.SessionId ?? string.Empty,
                context?.SessionGeneration ?? 0,
                context?.SessionLease ?? 0,
                composite?.IsStable == true,
                context != null && ReferenceEquals(composite?.Context, context),
                engine?.SessionActive == true,
                engine?.IsAttached == true,
                engine?.SessionId ?? string.Empty,
                engine?.SessionGeneration ?? 0,
                engine?.SessionLease ?? 0,
                engine?.ActiveSessionLease ?? 0,
                tombstone?.State.ToString() ?? "None",
                tombstone?.StateVersion ?? 0,
                tombstone?.SessionGeneration ?? 0,
                tombstone?.SessionLease ?? 0);
        }

        private static bool Send(WatchdogMessage message)
        {
            var context = CaptureContext();
            return Send(context, message);
        }

        private static bool Send(
            RuntimeTransportSessionContext context,
            WatchdogMessage message)
        {
            if (context == null || message == null || context.SessionLease <= 0) return false;
            // TrySendForSession clones/stamps an empty SessionId inside the
            // Engine.  The Runtime deliberately never mutates the caller's
            // shared payload and always supplies the frozen three-part
            // session identity.
            return TransportEngine.TrySendForSession(
                message,
                context.SessionId,
                context.SessionGeneration,
                context.SessionLease);
        }

        private static void SendSimple(string type, string reason)
        {
            var context = CaptureContext();
            if (context == null) return;
            Send(new WatchdogMessage { Type = type, SessionId = context.SessionId, Reason = reason, CorrelationId = Guid.NewGuid().ToString("N") });
        }

        private static void RaiseTransportLost(RuntimeTransportSessionContext context, string reason, string detail)
        {
            PublishTransportDiagnostic(context, "TransportLost", reason, detail, true);
        }

        private static void RaiseTransportError(RuntimeTransportSessionContext context, string reason, string detail)
        {
            PublishTransportDiagnostic(context, "TransportEvent", reason, detail, false);
        }

        private static void RecordClientEvent(string eventType, string detail)
        {
            var context = CaptureContext();
            if (context != null) RecordClientEvent(context, eventType, detail);
        }

        internal static void RecordClientEvent(RuntimeTransportSessionContext context, string eventType, string detail)
        {
            if (context?.Journal == null) return;
            // The event is still written to the frozen context journal when a
            // callback is stale.  AckSequence is copied only from one exact
            // same-session Engine snapshot; stale callbacks therefore cannot
            // attribute a newer session's ACK to the old journal event.
            TryCaptureCurrentContextEngine(context, out var engineSnapshot);
            RecoveryFailureReport report;
            lock (context.State.Gate) report = context.State.LastFailureReport?.Clone();
            try
            {
                using (var process = Process.GetCurrentProcess())
                    context.Journal.Record(new WatchdogJournalEvent
                    {
                        EventSequence = Interlocked.Increment(ref context.State.ClientEventSequence),
                        EventType = eventType ?? string.Empty,
                        State = Volatile.Read(ref context.State.SessionClosing) == 0 ? "Active" : "Closing",
                        Reason = detail ?? string.Empty,
                        SessionId = context.SessionId,
                        ProcessId = process.Id,
                        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        AckSequence = engineSnapshot?.HeartbeatAckSequence ?? 0,
                        RecoveryAttempt = context.RecoveryAttempt,
                        RootCode = report?.RootCode,
                        DeviceOrChannelGroup = report?.DeviceOrChannelGroup,
                        RunId = report?.RunId,
                        RecoveryStage = report?.RecoveryStage,
                        RecoveryProgressToken = report?.RecoveryProgressToken,
                        RecoveryProcessSource = report?.RecoveryProcessSource,
                        RecoveryFailureFingerprint = report == null ? string.Empty : RecoveryFailurePolicy.BuildFingerprint(report)
                    });
            }
            catch { }
        }

        private static void FlushClientJournal()
        {
            var context = CaptureContext();
            if (context != null) FlushClientJournal(context);
        }

        private static void FlushClientJournal(RuntimeTransportSessionContext context)
        {
            try { context?.Journal?.Flush(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) { RaiseTransportError(context, "JournalFlush", ex.Message); }
        }

        private static void WriteSessionRevocationMarker(RuntimeTransportSessionContext context, string reason)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.SessionId)) return;
            try { WatchdogControlMarker.WriteLocal(context.SessionId, reason); }
            catch (Exception ex) { RaiseTransportError(context, "SessionRevocationMarkerLocal", ex.Message); }
            try { context.Journal?.PublishRevocation(reason); }
            catch (Exception ex) { RaiseTransportError(context, "SessionRevocationMarkerProject", ex.Message); }
        }

        internal static ShutdownReceipt ShutdownLocalClientWithReceipt()
        {
            SessionLifecycleGate.Wait();
            try
            {
                return ShutdownRuntimeWithReceiptNoGate()?.EngineReceipt;
            }
            finally
            {
                SessionLifecycleGate.Release();
            }
        }

        internal static void ShutdownLocalClient() => ShutdownLocalClientWithReceipt();
    }
}
