using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog.Client
{
    internal interface IWatchdogPipeWritePort
    {
        Task WriteLineAsync(StreamWriter writer, string payload);
    }

    internal sealed class WatchdogPipeWritePort : IWatchdogPipeWritePort
    {
        public Task WriteLineAsync(StreamWriter writer, string payload) =>
            writer.WriteLineAsync(payload);
    }

    /// <summary>
    /// The sole owner of the client-side Sidecar transport lifecycle.
    /// Runtime supplies journal/UI/business callbacks; this class owns pipe,
    /// process, pending/authority identity, handshake, monitor and reconnect
    /// state.  No callback is invoked while this engine holds its state gate.
    /// </summary>
    public sealed class WatchdogClientTransportEngine : IDisposable
    {
        private sealed class PendingHelper
        {
            internal readonly SidecarProcessLaunchResult Launch;
            internal readonly int ProcessId;
            internal readonly long StartUtcTicks;
            internal readonly string SessionId;
            internal readonly long SessionGeneration;
            internal readonly string InstanceNonce;

            internal PendingHelper(
                SidecarProcessLaunchResult launch,
                int processId,
                long startUtcTicks,
                string sessionId,
                long sessionGeneration,
                string instanceNonce)
            {
                Launch = launch;
                ProcessId = processId;
                StartUtcTicks = startUtcTicks;
                SessionId = sessionId ?? string.Empty;
                SessionGeneration = sessionGeneration;
                InstanceNonce = instanceNonce ?? string.Empty;
            }

            internal Process Process => Launch?.Process;
        }

        private sealed class LaunchReservation
        {
            internal enum OutcomeKind
            {
                Succeeded = 0,
                FrozenFailure = 1,
                Cancelled = 2
            }

            internal sealed class Outcome
            {
                internal readonly OutcomeKind Kind;
                internal readonly bool Succeeded;
                internal readonly Exception Error;

                internal Outcome(OutcomeKind kind, Exception error)
                {
                    Kind = kind;
                    Succeeded = kind == OutcomeKind.Succeeded;
                    Error = error;
                }
            }

            internal readonly long LaunchLease;
            internal readonly long SessionLease;
            internal readonly WatchdogClientTransportOptions Options;
            internal readonly int ParentProcessId;
            internal readonly long ParentProcessStartUtcTicks;
            internal readonly string InstanceNonce;
            internal readonly TaskCompletionSource<Outcome> Completion;
            internal readonly CancellationTokenSource Lifetime;
            private int _settled;

            internal LaunchReservation(
                long launchLease,
                long sessionLease,
                WatchdogClientTransportOptions options,
                int parentProcessId,
                long parentProcessStartUtcTicks,
                string instanceNonce,
                CancellationTokenSource lifetime)
            {
                LaunchLease = launchLease;
                SessionLease = sessionLease;
                Options = options;
                ParentProcessId = parentProcessId;
                ParentProcessStartUtcTicks = parentProcessStartUtcTicks;
                InstanceNonce = instanceNonce ?? string.Empty;
                Completion = new TaskCompletionSource<Outcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            }

            internal void Cancel() { try { Lifetime.Cancel(); } catch { } }

            internal void Complete()
            {
                TrySettle(new Outcome(OutcomeKind.Succeeded, null));
            }

            internal void Fail(Exception error)
            {
                var cancelled = error is OperationCanceledException ||
                    (error is WatchdogConnectException connect &&
                     connect.Kind == WatchdogConnectFailureKind.Cancelled);
                TrySettle(new Outcome(
                    cancelled ? OutcomeKind.Cancelled : OutcomeKind.FrozenFailure,
                    error ?? new InvalidOperationException(
                        "Sidecar launch transaction failed without an exception.")));
            }

            private void TrySettle(Outcome outcome)
            {
                if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0)
                    return;
                try { Lifetime.Dispose(); } catch { }
                Completion.TrySetResult(outcome);
            }
        }

        private sealed class ConnectReservation
        {
            internal readonly long Generation;
            internal readonly long SessionLease;
            internal readonly TaskCompletionSource<bool> Completion;

            internal ConnectReservation(
                long generation,
                long sessionLease)
            {
                Generation = generation;
                SessionLease = sessionLease;
                Completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal void PublishFailure(Exception exception)
            {
                if (exception == null) throw new ArgumentNullException(nameof(exception));
                if (!Completion.TrySetException(exception)) return;

                // The owner observes and rethrows the ConnectOwnerAsync failure, while
                // concurrent joiners observe this separate reservation task. A cold
                // start commonly has no joiners, so explicitly observe the published
                // fault here as well; otherwise the TCS task can surface later through
                // TaskScheduler.UnobservedTaskException even though the expected first
                // PipeUnavailable result was already handled by connect-first launch.
                _ = Completion.Task.Exception;
            }
        }

        private enum ReconnectAttemptDisposition
        {
            Succeeded = 0,
            Retry = 1,
            FailClosed = 2
        }

        private enum ReconnectScheduleDisposition
        {
            Rejected = 0,
            Started = 1,
            AlreadySupervised = 2
        }

        private enum AuthorityLiveness
        {
            Missing = 0,
            Alive = 1,
            Unknown = 2,
            ConfirmedDead = 3
        }

        private enum SendDisposition
        {
            Sent = 0,
            ScopeStale = 1,
            AdmissionBusy = 2,
            TransportUnavailable = 3,
            TransportWriteFailed = 4
        }

        private enum TransportBreakOrigin
        {
            Send = 1,
            Reader = 2,
            Monitor = 3,
            Reconnect = 4
        }

        private sealed class TransportBreakScope
        {
            internal TransportBreakScope(
                TransportBreakOrigin origin,
                long sessionLease,
                long connectionGeneration,
                object connectionIdentity)
            {
                Origin = origin;
                SessionLease = sessionLease;
                ConnectionGeneration = connectionGeneration;
                ConnectionIdentity = connectionIdentity;
            }

            internal TransportBreakOrigin Origin { get; }
            internal long SessionLease { get; }
            internal long ConnectionGeneration { get; }
            internal object ConnectionIdentity { get; }
        }

        private sealed class SendRequest
        {
            private const int Pending = 0;
            private const int Writing = 1;
            private const int Cancelled = 2;
            private int _writeState = Pending;

            internal readonly string Payload;
            internal readonly bool Lifecycle;
            internal readonly bool CompletesConnectionLifecycle;
            internal readonly StreamWriter Writer;
            internal readonly long SessionLease;
            internal readonly long ConnectionGeneration;
            internal readonly object ConnectionIdentity;
            internal readonly TaskCompletionSource<SendDisposition> Completion =
                new TaskCompletionSource<SendDisposition>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal SendRequest(
                string payload,
                bool lifecycle,
                bool completesConnectionLifecycle,
                StreamWriter writer,
                long sessionLease,
                long connectionGeneration,
                object connectionIdentity)
            {
                Payload = payload ?? string.Empty;
                Lifecycle = lifecycle;
                CompletesConnectionLifecycle = completesConnectionLifecycle;
                Writer = writer;
                SessionLease = sessionLease;
                ConnectionGeneration = connectionGeneration;
                ConnectionIdentity = connectionIdentity;
            }

            internal bool TryBeginWrite()
            {
                return Interlocked.CompareExchange(
                           ref _writeState,
                           Writing,
                           Pending) == Pending;
            }

            internal bool TryCancelBeforeWrite(SendDisposition disposition)
            {
                if (Interlocked.CompareExchange(
                        ref _writeState,
                        Cancelled,
                        Pending) != Pending)
                    return false;
                Completion.TrySetResult(disposition);
                return true;
            }
        }

        /// <summary>
        /// Exact-connection send owner.  The queues bound admission while one
        /// worker owns every WriteLineAsync task for the connection.  Eight
        /// control slots are isolated from normal traffic so a heartbeat
        /// backlog cannot starve StopCompleted/ShutdownExpected.
        /// </summary>
        private sealed class SendQueueOwner
        {
            internal const int NormalCapacity = 248;
            internal const int LifecycleCapacity = 8;

            private readonly object _gate = new object();
            private readonly Queue<SendRequest> _lifecycle = new Queue<SendRequest>();
            private readonly Queue<SendRequest> _normal = new Queue<SendRequest>();
            internal readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
            internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
            internal readonly StreamWriter Writer;
            internal readonly long SessionLease;
            internal readonly long ConnectionGeneration;
            internal readonly object ConnectionIdentity;
            internal Task WorkerTask;
            internal Task InflightWriteTask;
            private int _accepting = 1;

            internal SendQueueOwner(
                StreamWriter writer,
                long sessionLease,
                long connectionGeneration,
                object connectionIdentity)
            {
                Writer = writer ?? throw new ArgumentNullException(nameof(writer));
                SessionLease = sessionLease;
                ConnectionGeneration = connectionGeneration;
                ConnectionIdentity = connectionIdentity ??
                    throw new ArgumentNullException(nameof(connectionIdentity));
            }

            internal bool TryEnqueue(SendRequest request)
            {
                if (request == null) return false;
                lock (_gate)
                {
                    if (_accepting == 0) return false;
                    var queue = request.Lifecycle ? _lifecycle : _normal;
                    var capacity = request.Lifecycle ? LifecycleCapacity : NormalCapacity;
                    if (queue.Count >= capacity) return false;
                    queue.Enqueue(request);
                }
                try { Signal.Release(); }
                catch (ObjectDisposedException) { return false; }
                return true;
            }

            internal bool TryDequeue(out SendRequest request)
            {
                lock (_gate)
                {
                    if (_lifecycle.Count > 0)
                    {
                        request = _lifecycle.Dequeue();
                        return true;
                    }
                    if (_normal.Count > 0)
                    {
                        request = _normal.Dequeue();
                        return true;
                    }
                    request = null;
                    return false;
                }
            }

            internal void SetInflight(Task writeTask)
            {
                lock (_gate) InflightWriteTask = writeTask;
            }

            internal void ClearInflight(Task writeTask)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(InflightWriteTask, writeTask))
                        InflightWriteTask = null;
                }
            }

            internal Task CaptureInflight()
            {
                lock (_gate) return InflightWriteTask;
            }

            internal void StopAccepting(SendDisposition pendingDisposition)
            {
                List<SendRequest> pending;
                lock (_gate)
                {
                    _accepting = 0;
                    pending = _lifecycle.Concat(_normal).ToList();
                    _lifecycle.Clear();
                    _normal.Clear();
                }
                foreach (var request in pending)
                {
                    if (!request.TryCancelBeforeWrite(pendingDisposition))
                        request.Completion.TrySetResult(pendingDisposition);
                }
                try { Lifetime.Cancel(); } catch { }
                try { Signal.Release(); } catch { }
            }

            internal bool IsTerminal
            {
                get
                {
                    var worker = WorkerTask;
                    var write = CaptureInflight();
                    return (worker == null || worker.IsCompleted) &&
                           (write == null || write.IsCompleted);
                }
            }
        }

        private enum HeartbeatSendDisposition
        {
            Sent = 0,
            ScopeStale = 1,
            CaptureFailed = 2,
            AdmissionBusy = 3,
            TransportFailed = 4
        }

        private readonly object _gate = new object();
        private readonly object _heartbeatCaptureGate = new object();
        private readonly object _stateEventDispatchGate = new object();
        private readonly SortedDictionary<long, WatchdogClientTransportEvent> _pendingStateEvents =
            new SortedDictionary<long, WatchdogClientTransportEvent>();
        private readonly ISidecarProcessLauncher _launcher;
        private readonly INamedPipeClientFactory _pipeFactory;
        private readonly SidecarIdentityStateMachine _identity = new SidecarIdentityStateMachine();

        private WatchdogClientTransportOptions _options;
        private WatchdogClientTransportCallbacks _callbacks = new WatchdogClientTransportCallbacks();
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private SendQueueOwner _sendQueueOwner;
        private Task _sendTask;
        private CancellationTokenSource _lifetime;
        private CancellationTokenSource _sessionLifetime;
        private TaskCompletionSource<bool> _attached;
        private Task _readerTask;
        private object _readerTaskIdentity;
        private Task _heartbeatTask;
        private object _heartbeatTaskIdentity;
        private long _connectionGeneration;
        private long _activeConnectionGeneration;
        private object _activeConnectionIdentity;
        private long _activeSessionGeneration;
        private long _sessionLeaseCounter;
        private long _activeSessionLease;
        private long _attachedConnectionGeneration;
        private long _heartbeatSequence;
        private long _lastHeartbeatAckUtcTicks;
        private long _lastHeartbeatAckSequence;
        private long _monitorGeneration;
        private Task _monitorTask;
        private object _monitorTaskIdentity;
        private long _reconnectGeneration;
        private Task _reconnectTask;
        private object _reconnectTaskIdentity;
        private int _reconnectAttempt;
        private int _transportLostReported;
        private int _sendFailureReported;
        private int _launchGate;
        private int _sessionClosing;
        private int _safeDegraded;
        private int _transportFailClosed;
        private WatchdogConnectFailureKind _transportFailureKind;
        private string _transportFailureDetail;
        private int _pingResponseScheduled;
        private object _pingResponseTaskIdentity;
        private PendingHelper _pending;
        private long _launchLeaseCounter;
        private LaunchReservation _launchReservation;
        private Task _launchTask;
        private LaunchReservation _retainedLaunchReservation;
        private Task _retainedLaunchTask;
        private long _launchTaskSessionLease;
        private int _launchClosureActive;
        private int _launchClosureBlocked;
        private string _launchClosureDetail;
        private long _connectLeaseCounter;
        private ConnectReservation _connectReservation;
        private Task _connectTask;
        private long _connectGeneration;
        private long _stateEventSequence;
        private long _stateEventDeliveredSequence;
        private bool _stateEventDispatching;
        private readonly IWatchdogPipeWritePort _writePort;

        /// <summary>
        /// Raised after a transport lifecycle transition has committed. The
        /// observer is invoked outside the engine gate and receives an
        /// immutable snapshot.
        /// </summary>
        public event Action<WatchdogClientTransportEvent> StateChanged;

        public WatchdogClientTransportEngine(
            ISidecarProcessLauncher launcher = null,
            INamedPipeClientFactory pipeFactory = null)
            : this(launcher, pipeFactory, null)
        {
        }

        internal WatchdogClientTransportEngine(
            ISidecarProcessLauncher launcher,
            INamedPipeClientFactory pipeFactory,
            IWatchdogPipeWritePort writePort)
        {
            _launcher = launcher ?? new SystemSidecarProcessLauncher();
            _pipeFactory = pipeFactory ?? new SystemNamedPipeClientFactory();
            _writePort = writePort ?? new WatchdogPipeWritePort();
        }

        public bool IsAttached
        {
            get
            {
                lock (_gate)
                {
                    return _activeSessionLease > 0 &&
                           _activeConnectionGeneration > 0 &&
                           _attachedConnectionGeneration == _activeConnectionGeneration &&
                           _pipe?.IsConnected == true;
                }
            }
        }

        private bool IsExactAttachedForSession(
            long sessionLease,
            WatchdogClientTransportOptions options)
        {
            if (options == null || sessionLease <= 0) return false;
            lock (_gate)
            {
                return _activeSessionLease == sessionLease &&
                       _activeSessionGeneration == options.SessionGeneration &&
                       _activeConnectionGeneration > 0 &&
                       _attachedConnectionGeneration == _activeConnectionGeneration &&
                       _pipe?.IsConnected == true &&
                       _sessionClosing == 0 &&
                       _transportFailClosed == 0;
            }
        }

        public bool IsClosing => Volatile.Read(ref _sessionClosing) != 0;
        public bool IsSafeDegraded => Volatile.Read(ref _safeDegraded) != 0;
        public bool IsTransportFailClosed => Volatile.Read(ref _transportFailClosed) != 0;
        public string SessionId { get { lock (_gate) return _options?.SessionId; } }
        public long SessionGeneration { get { lock (_gate) return _options?.SessionGeneration ?? 0; } }

        public WatchdogClientTransportSnapshot CaptureSnapshot()
        {
            lock (_gate) return CaptureSnapshotLocked();
        }

        private WatchdogClientTransportSnapshot CaptureSnapshotLocked()
        {
            var pending = _identity.Pending;
            var authority = _identity.Authority;
            return new WatchdogClientTransportSnapshot(
                DateTime.UtcNow.Ticks,
                _options?.SessionId,
                _options?.SessionGeneration ?? 0,
                _sessionLeaseCounter,
                _activeSessionLease,
                _connectionGeneration,
                _activeConnectionGeneration,
                _attachedConnectionGeneration,
                _monitorGeneration,
                _monitorTask != null && !_monitorTask.IsCompleted,
                _reconnectGeneration,
                _reconnectTask != null && !_reconnectTask.IsCompleted,
                _pipe?.IsConnected == true && _attachedConnectionGeneration != 0,
                pending != null,
                authority != null,
                Volatile.Read(ref _sessionClosing) != 0,
                Volatile.Read(ref _safeDegraded) != 0,
                Volatile.Read(ref _reconnectAttempt),
                SidecarProcessHandleOwner.LiveOwnedCount,
                Interlocked.Read(ref _lastHeartbeatAckSequence),
                Interlocked.Read(ref _lastHeartbeatAckUtcTicks),
                pending?.InstanceNonce,
                pending?.ProcessId ?? 0,
                pending?.ProcessStartUtcTicks ?? 0,
                pending?.SessionId,
                pending?.SessionGeneration ?? 0,
                authority?.ProcessId ?? 0,
                authority?.ProcessStartUtcTicks ?? 0,
                authority?.SessionId,
                authority?.InstanceNonce,
                Volatile.Read(ref _transportFailClosed) != 0,
                _transportFailureKind,
                _transportFailureDetail,
                Volatile.Read(ref _launchClosureActive) != 0 ||
                    _launchTask != null || _retainedLaunchTask != null,
                Volatile.Read(ref _launchClosureBlocked) != 0,
                _launchClosureDetail,
                _connectReservation != null,
                _connectGeneration);
        }

        private void PublishStateChanged(string eventType, string detail)
        {
            WatchdogClientTransportEvent notification;
            lock (_gate)
            {
                notification = ReserveStateChangedLocked(eventType, detail);
            }

            EnqueueStateChanged(notification);
        }

        private WatchdogClientTransportEvent ReserveStateChangedLocked(
            string eventType,
            string detail)
        {
            return new WatchdogClientTransportEvent(
                ++_stateEventSequence,
                DateTime.UtcNow.Ticks,
                eventType,
                detail,
                CaptureSnapshotLocked());
        }

        private void EnqueueStateChanged(WatchdogClientTransportEvent notification)
        {
            var startDispatch = false;
            lock (_stateEventDispatchGate)
            {
                _pendingStateEvents[notification.Sequence] = notification;
                if (!_stateEventDispatching)
                {
                    _stateEventDispatching = true;
                    startDispatch = true;
                }
            }
            if (startDispatch) DrainStateChangedEvents();
        }

        private void DrainStateChangedEvents()
        {
            while (true)
            {
                WatchdogClientTransportEvent notification;
                lock (_stateEventDispatchGate)
                {
                    var next = _stateEventDeliveredSequence + 1;
                    if (!_pendingStateEvents.TryGetValue(next, out notification))
                    {
                        _stateEventDispatching = false;
                        return;
                    }
                    _pendingStateEvents.Remove(next);
                    _stateEventDeliveredSequence = next;
                }

                // Never invoke observers while holding the dispatch gate.  An
                // observer may synchronously produce another transition; that
                // transition is queued and drained by this same worker.
                try { StateChanged?.Invoke(notification); } catch { }
            }
        }

        public void BeginSession(
            WatchdogClientTransportOptions options,
            WatchdogClientTransportCallbacks callbacks)
        {
            var frozenOptions = CloneAndValidateOptions(options);
            var frozenCallbacks = CloneAndValidateCallbacks(callbacks);
            Shutdown();
            ThrowIfLaunchClosureBlocked();
            long sessionLease;
            WatchdogClientTransportEvent sessionStartedEvent;
            var attachExistingOnly = frozenOptions.LaunchPolicy ==
                WatchdogLaunchPolicy.AttachExistingAuthorityOnly;
            var seedFailure = attachExistingOnly && frozenOptions.AuthoritySeed == null
                ? "AttachExistingAuthorityOnly:AuthoritySeedMissing"
                : null;
            lock (_gate)
            {
                _options = frozenOptions;
                _callbacks = frozenCallbacks;
                _activeSessionLease = checked(++_sessionLeaseCounter);
                _sessionClosing = 0;
                _safeDegraded = 0;
                _transportFailClosed = 0;
                _transportFailureKind = WatchdogConnectFailureKind.Unknown;
                _transportFailureDetail = string.Empty;
                _transportLostReported = 0;
                _sendFailureReported = 0;
                _reconnectAttempt = 0;
                _activeConnectionGeneration = 0;
                _activeSessionGeneration = frozenOptions.SessionGeneration;
                _attachedConnectionGeneration = 0;
                _heartbeatSequence = 0;
                _lastHeartbeatAckSequence = 0;
                _lastHeartbeatAckUtcTicks = DateTime.UtcNow.Ticks;
                _pingResponseScheduled = 0;
                _pingResponseTaskIdentity = null;
                _identity.Reset();
                _pending = null;
                _sessionLifetime = new CancellationTokenSource();
                sessionLease = _activeSessionLease;
                sessionStartedEvent = ReserveStateChangedLocked("SessionStarted", frozenOptions.SessionId);
            }

            var seed = frozenOptions.AuthoritySeed;
            if (seed != null)
            {
                var seedValid = ValidateAuthorityCandidate(seed, out var reason);
                var seedAccepted = false;
                var seedError = string.Empty;
                Action<string, string> seedRecord = null;
                string seedRecordEvent = null;
                string seedRecordDetail = null;
                lock (_gate)
                {
                    if (seedValid &&
                        _activeSessionLease == sessionLease &&
                        IsCurrentSessionLeaseLocked(sessionLease) &&
                        string.Equals(_options?.SessionId, frozenOptions.SessionId, StringComparison.Ordinal))
                    {
                        seedAccepted = _identity.TrySeedValidatedAuthority(
                            seed,
                            frozenOptions.SessionId,
                            out seedError);
                        if (seedAccepted)
                        {
                            seedRecord = _callbacks.RecordEvent;
                            seedRecordEvent = "AuthoritySeeded";
                            seedRecordDetail = "PID=" + seed.ProcessId;
                        }
                        else
                        {
                            seedRecord = _callbacks.RecordEvent;
                            seedRecordEvent = "RecoveryAuthorityRejected";
                            seedRecordDetail = frozenOptions.AuthoritySeedRejection ?? seedError ?? seedErrorOrDefault(seed);
                        }
                    }
                    else if (_activeSessionLease == sessionLease &&
                             string.Equals(_options?.SessionId, frozenOptions.SessionId, StringComparison.Ordinal))
                    {
                        if (attachExistingOnly && string.IsNullOrWhiteSpace(seedFailure))
                            seedFailure = "AttachExistingAuthorityOnly:" +
                                (frozenOptions.AuthoritySeedRejection ?? reason ?? seedErrorOrDefault(seed));
                        seedRecord = _callbacks.RecordEvent;
                        seedRecordEvent = "RecoveryAuthorityRejected";
                        seedRecordDetail = frozenOptions.AuthoritySeedRejection ?? reason ?? seedErrorOrDefault(seed);
                    }
                }
                if (attachExistingOnly && !seedAccepted && string.IsNullOrWhiteSpace(seedFailure))
                    seedFailure = "AttachExistingAuthorityOnly:" +
                        (frozenOptions.AuthoritySeedRejection ?? reason ?? seedErrorOrDefault(seed));
                try { seedRecord?.Invoke(seedRecordEvent, seedRecordDetail ?? string.Empty); } catch { }
            }

            EnqueueStateChanged(sessionStartedEvent);
            if (attachExistingOnly && !string.IsNullOrWhiteSpace(seedFailure))
                EnterTransportFailClosed(
                    sessionLease,
                    WatchdogConnectFailureKind.InvalidConfiguration,
                    seedFailure);
        }

        private static string seedErrorOrDefault(SidecarIdentityStateMachine.AuthoritySnapshot seed)
        {
            return seed == null ? string.Empty : "恢复参数中的Sidecar身份未通过权威校验。";
        }

        public async Task StartAsync()
        {
            ThrowIfTransportFailClosed();
            ThrowIfLaunchClosureBlocked();
            await ConnectFirstOrLaunchAsync().ConfigureAwait(false);
        }

        public async Task ConnectFirstOrLaunchAsync()
        {
            ThrowIfTransportFailClosed();
            ThrowIfLaunchClosureBlocked();
            var options = GetOptions();
            long sessionLease = GetSessionLease();
            if (IsExactAttachedForSession(sessionLease, options))
                return;
            WatchdogConnectException firstError;
            try
            {
                await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
                return;
            }
            catch (WatchdogConnectException ex)
            {
                firstError = ex;
            }

            if (IsExactAttachedForSession(sessionLease, options))
                return;

            if (!IsCurrentSessionLease(sessionLease))
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.SessionRevoked,
                    "Watchdog首次连接完成后Session已失效。");

            if (IsTransportFailClosedKind(firstError.Kind))
            {
                EnterTransportFailClosed(
                    sessionLease,
                    firstError.Kind,
                    firstError.GetBaseException().Message);
                throw firstError;
            }
            if (firstError.Kind != WatchdogConnectFailureKind.PipeUnavailable)
                throw firstError;

            // An attach-only recovery session may reconnect to the exact
            // validated authority, but it is never allowed to create a new
            // helper.  Probe the frozen PID/start identity outside the gate;
            // a confirmed death (including PID reuse) is a sticky identity
            // failure, while an alive or temporarily uninspectable authority
            // is supervised by the single reconnect worker.
            if (options.LaunchPolicy == WatchdogLaunchPolicy.AttachExistingAuthorityOnly)
            {
                var authorityLiveness = ProbeAuthorityLiveness(sessionLease, out var authority);
                if (authorityLiveness == AuthorityLiveness.ConfirmedDead ||
                    authorityLiveness == AuthorityLiveness.Missing)
                {
                    var detail = authorityLiveness == AuthorityLiveness.ConfirmedDead
                        ? "AttachExistingAuthorityOnly权威进程已退出或PID已复用。"
                        : "AttachExistingAuthorityOnly缺少可验证权威身份。";
                    ClearAuthorityIfCurrent(sessionLease, authority, detail);
                    EnterTransportFailClosed(
                        sessionLease,
                        WatchdogConnectFailureKind.IdentityRejected,
                        detail);
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.IdentityRejected,
                        detail,
                        firstError);
                }

                if (EnsureSingleReconnectSupervisor(sessionLease))
                    return;
                throw firstError;
            }

            if (!IsCurrentSessionLease(sessionLease) || ObserveStopMarker(sessionLease) || IsClosing || IsSafeDegraded)
                throw firstError;

            var lastError = firstError;
            if (IsAuthorityAlive())
            {
                for (var attempt = 0; attempt < WatchdogTransportPolicy.ReconnectMaxAttempts; attempt++)
                {
                    if (!IsCurrentSessionLease(sessionLease) || ObserveStopMarker(sessionLease) || IsClosing || IsSafeDegraded)
                        throw lastError;
                    if (!IsAuthorityAlive()) break;
                    await Task.Delay(WatchdogTransportPolicy.SelectReconnectBackoffMs(attempt)).ConfigureAwait(false);
                    if (!IsCurrentSessionLease(sessionLease))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog权威重连等待期间Session已失效。");
                    try
                    {
                        await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
                        return;
                    }
                    catch (WatchdogConnectException ex)
                    {
                        if (ex.Kind != WatchdogConnectFailureKind.PipeUnavailable) throw;
                        lastError = ex;
                        if (IsCurrentSessionLease(sessionLease))
                            RaiseErrorWithCallbacks(
                                CaptureSessionCallbacks(sessionLease),
                                "ConnectFirstAuthorityAlive",
                                ex.GetBaseException().Message);
                    }
                }
                if (IsCurrentSessionLease(sessionLease) && IsAuthorityAlive())
                {
                    if (EnsureSingleReconnectSupervisor(sessionLease)) return;
                    throw lastError;
                }
            }

            if (IsPendingAlive())
            {
                for (var attempt = 0; attempt < WatchdogTransportPolicy.ReconnectMaxAttempts; attempt++)
                {
                    if (!IsCurrentSessionLease(sessionLease) || ObserveStopMarker(sessionLease) || IsClosing || IsSafeDegraded)
                        throw lastError;
                    if (!IsPendingAlive()) break;
                    await Task.Delay(WatchdogTransportPolicy.SelectReconnectBackoffMs(attempt)).ConfigureAwait(false);
                    if (!IsCurrentSessionLease(sessionLease))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog pending重连等待期间Session已失效。");
                    try
                    {
                        await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
                        return;
                    }
                    catch (WatchdogConnectException ex)
                    {
                        if (ex.Kind != WatchdogConnectFailureKind.PipeUnavailable) throw;
                        lastError = ex;
                        if (IsCurrentSessionLease(sessionLease))
                            RaiseErrorWithCallbacks(
                                CaptureSessionCallbacks(sessionLease),
                                "ConnectFirstPendingHelper",
                                ex.GetBaseException().Message);
                    }
                }
                if (IsCurrentSessionLease(sessionLease) && IsPendingAlive())
                {
                    if (EnsureSingleReconnectSupervisor(sessionLease)) return;
                    throw lastError;
                }
            }

            if (!IsCurrentSessionLease(sessionLease))
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.SessionRevoked,
                    "Watchdog Sidecar启动前Session已失效。");
            try
            {
                await LaunchSidecarIfAuthorized(
                    sessionLease,
                    options.LaunchPolicy).ConfigureAwait(false);
            }
            catch (WatchdogConnectException) { throw; }
            catch (Exception ex)
            {
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.LaunchFailure,
                    "Watchdog Sidecar启动失败：" + ex.GetBaseException().Message,
                    ex);
            }
            try
            {
                await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
            }
            catch (WatchdogConnectException ex) when (ex.Kind == WatchdogConnectFailureKind.PipeUnavailable)
            {
                // A freshly launched helper may need time to create its pipe.
                // Keep exactly one supervised reconnect worker for that
                // pending process; never leave its owned handle unattended.
                if (IsCurrentSessionLease(sessionLease) && IsPendingAlive() &&
                    TryScheduleReconnect(
                        WatchdogTransportPolicy.ReconnectInitial250,
                        sessionLease) != ReconnectScheduleDisposition.Rejected)
                {
                    try
                    {
                        CaptureSessionCallbacks(sessionLease)?.RecordEvent?.Invoke(
                            "StartDeferred",
                            "Sidecar pending pipe尚未就绪，已转入唯一重连监督Worker。");
                    }
                    catch { }
                    return;
                }
                if (IsCurrentSessionLease(sessionLease)) CloseFailedStartup();
                throw;
            }
        }

        public bool Send(WatchdogMessage message)
        {
            if (message == null) return false;
            string sessionId;
            long sessionGeneration;
            long sessionLease;
            lock (_gate)
            {
                sessionId = _options?.SessionId;
                sessionGeneration = _activeSessionGeneration;
                sessionLease = _activeSessionLease;
            }
            return TrySendForSession(
                message,
                sessionId,
                sessionGeneration,
                sessionLease);
        }

        /// <summary>
        /// Sends only when the caller still owns the exact public session
        /// identity it captured.  The session lease and the connection
        /// identity are captured under the engine gate; all I/O remains
        /// outside the gate and is guarded again by SendCore.
        /// </summary>
        [Obsolete(
            "Compatibility overload only; production callers must pass the captured session lease.")]
        public bool TrySendForSession(
            WatchdogMessage message,
            string expectedSessionId,
            long expectedSessionGeneration)
        {
            long sessionLease;
            lock (_gate)
            {
                sessionLease = _activeSessionLease;
            }
            return TrySendForSession(
                message,
                expectedSessionId,
                expectedSessionGeneration,
                sessionLease);
        }

        /// <summary>
        /// Sends only when all three public session identities captured by the
        /// caller still match under the engine gate.  A payload with no
        /// SessionId is copied and stamped inside the engine; the caller's
        /// mutable message is never changed.  A non-empty mismatching
        /// SessionId is rejected before the writer is captured.
        /// </summary>
        public bool TrySendForSession(
            WatchdogMessage message,
            string expectedSessionId,
            long expectedSessionGeneration,
            long expectedSessionLease)
        {
            return TrySendForSessionWithDisposition(
                       message,
                       expectedSessionId,
                       expectedSessionGeneration,
                       expectedSessionLease) == WatchdogSendDisposition.Sent;
        }

        public WatchdogSendDisposition TrySendForSessionWithDisposition(
            WatchdogMessage message,
            string expectedSessionId,
            long expectedSessionGeneration,
            long expectedSessionLease)
        {
            if (message == null || string.IsNullOrWhiteSpace(expectedSessionId) ||
                expectedSessionGeneration <= 0 || expectedSessionLease <= 0)
                return WatchdogSendDisposition.ScopeStale;

            long connectionGeneration;
            object connectionIdentity;
            var stampSessionId = false;
            lock (_gate)
            {
                if (_options == null ||
                    _activeSessionLease != expectedSessionLease ||
                    !string.Equals(_options.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                    _activeSessionGeneration != expectedSessionGeneration ||
                    _options.SessionGeneration != expectedSessionGeneration)
                    return WatchdogSendDisposition.ScopeStale;
                if (!string.IsNullOrEmpty(message.SessionId) &&
                    !string.Equals(message.SessionId, expectedSessionId, StringComparison.Ordinal))
                    return WatchdogSendDisposition.ScopeStale;
                stampSessionId = string.IsNullOrEmpty(message.SessionId);
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
            }

            var payload = message;
            if (stampSessionId)
            {
                payload = CloneMessageWithSessionId(message, expectedSessionId);
                if (payload == null) return WatchdogSendDisposition.TransportWriteFailed;
            }
            return ToPublicDisposition(SendCoreWithDisposition(
                payload,
                expectedSessionLease,
                connectionGeneration,
                connectionIdentity));
        }

        private static WatchdogSendDisposition ToPublicDisposition(
            SendDisposition disposition)
        {
            switch (disposition)
            {
                case SendDisposition.Sent: return WatchdogSendDisposition.Sent;
                case SendDisposition.AdmissionBusy: return WatchdogSendDisposition.AdmissionBusy;
                case SendDisposition.TransportUnavailable: return WatchdogSendDisposition.TransportUnavailable;
                case SendDisposition.TransportWriteFailed: return WatchdogSendDisposition.TransportWriteFailed;
                default: return WatchdogSendDisposition.ScopeStale;
            }
        }

        private static WatchdogMessage CloneMessageWithSessionId(
            WatchdogMessage message,
            string sessionId)
        {
            try
            {
                var clone = WatchdogProtocol.Deserialize(WatchdogProtocol.Serialize(message));
                if (clone == null) return null;
                clone.SessionId = sessionId;
                return clone;
            }
            catch
            {
                return null;
            }
        }

        private bool SendForConnection(
            WatchdogMessage message,
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity)
        {
            return SendCore(message, sessionLease, connectionGeneration, connectionIdentity);
        }

        private bool SendCore(
            WatchdogMessage message,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            return SendCoreWithDisposition(
                    message,
                    expectedSessionLease,
                    expectedConnectionGeneration,
                    expectedConnectionIdentity) == SendDisposition.Sent;
        }

        private SendDisposition SendCoreWithDisposition(
            WatchdogMessage message,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            if (message == null) return SendDisposition.TransportWriteFailed;
            StreamWriter writer;
            SendQueueOwner sendOwner;
            long capturedSessionLease;
            long capturedConnectionGeneration;
            object capturedConnectionIdentity;
            var guarded = expectedSessionLease > 0 || expectedConnectionGeneration > 0 || expectedConnectionIdentity != null;
            lock (_gate)
            {
                if (guarded && !IsExpectedSendScopeLocked(
                        expectedSessionLease,
                        expectedConnectionGeneration,
                        expectedConnectionIdentity))
                    return SendDisposition.ScopeStale;
                writer = _writer;
                sendOwner = _sendQueueOwner;
                capturedSessionLease = _activeSessionLease;
                capturedConnectionGeneration = _activeConnectionGeneration;
                capturedConnectionIdentity = _activeConnectionIdentity;
            }
            if (writer == null || sendOwner == null)
            {
                TryCommitSendFailure(
                    writer,
                    capturedSessionLease,
                    capturedConnectionGeneration,
                    capturedConnectionIdentity,
                    "SendNoWriter",
                    message.Type.ToString());
                BreakTransport(
                    writer,
                    capturedSessionLease,
                    capturedConnectionGeneration,
                    capturedConnectionIdentity,
                    null,
                    "SendNoWriter",
                    message.Type.ToString());
                return SendDisposition.TransportUnavailable;
            }

            string payload;
            try
            {
                payload = WatchdogProtocol.Serialize(message);
                lock (_gate)
                {
                    if (!ReferenceEquals(_writer, writer) ||
                        !ReferenceEquals(_sendQueueOwner, sendOwner) ||
                        (guarded && !IsExpectedSendScopeLocked(
                            expectedSessionLease,
                            expectedConnectionGeneration,
                            expectedConnectionIdentity)))
                        return SendDisposition.ScopeStale;
                }

                var request = new SendRequest(
                    WatchdogWireFrame.Encode(payload),
                    IsLifecycleSend(message.Type),
                    CompletesConnectionLifecycle(message.Type),
                    writer,
                    capturedSessionLease,
                    capturedConnectionGeneration,
                    capturedConnectionIdentity);
                if (!sendOwner.TryEnqueue(request))
                    return SendDisposition.AdmissionBusy;

                var waitMs = WatchdogTransportPolicy.SendWriteTimeoutMs +
                             WatchdogTransportPolicy.SendGateWaitMs;
                if (!request.Completion.Task.Wait(waitMs))
                {
                    // AdmissionBusy is truthful only while this request is
                    // still queued.  Once the writer owns it, returning false
                    // would allow the same frame to arrive after the caller
                    // has already acted on an apparent failure.
                    if (request.TryCancelBeforeWrite(SendDisposition.AdmissionBusy))
                        return SendDisposition.AdmissionBusy;

                    if (!request.Completion.Task.Wait(waitMs))
                    {
                        BreakTransport(
                            writer,
                            capturedSessionLease,
                            capturedConnectionGeneration,
                            capturedConnectionIdentity,
                            sendOwner,
                            "SendCompletionTimeout",
                            message.Type.ToString());
                        request.Completion.TrySetResult(
                            SendDisposition.TransportWriteFailed);
                    }
                }
                return request.Completion.Task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                TryCommitSendFailure(
                    writer,
                    capturedSessionLease,
                    capturedConnectionGeneration,
                    capturedConnectionIdentity,
                    "Send",
                    ex.GetBaseException().Message);
                BreakTransport(
                    writer,
                    capturedSessionLease,
                    capturedConnectionGeneration,
                    capturedConnectionIdentity,
                    null,
                    "SendWriteFailed",
                    ex.GetBaseException().Message);
                return SendDisposition.TransportWriteFailed;
            }
        }

        private static bool IsLifecycleSend(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.StopCompleted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ManualStopRequested, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ManualStopIntent, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.PhysicalStopConfirmed, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RunStopped, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.RunCompleted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ApplicationClosing, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ShutdownExpected, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.WatchdogTakeoverExit, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.SafetyHandoffRequested, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.SafetyHandoffAccepted, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.SafetyHandoffCompleted, StringComparison.Ordinal);
        }

        private void RunSendQueue(SendQueueOwner owner)
        {
            try
            {
                while (!owner.Lifetime.IsCancellationRequested)
                {
                    if (!owner.TryDequeue(out var request))
                    {
                        owner.Signal.Wait(owner.Lifetime.Token);
                        continue;
                    }
                    if (!request.TryBeginWrite())
                    {
                        request.Completion.TrySetResult(SendDisposition.AdmissionBusy);
                        continue;
                    }
                    if (!IsCurrentSendOwner(owner, request))
                    {
                        request.Completion.TrySetResult(SendDisposition.ScopeStale);
                        continue;
                    }

                    Task writeTask = null;
                    try
                    {
                        writeTask = _writePort.WriteLineAsync(owner.Writer, request.Payload);
                        owner.SetInflight(writeTask);
                        if (!writeTask.Wait(WatchdogTransportPolicy.SendWriteTimeoutMs))
                        {
                            ObserveLateWriteTask(writeTask);
                            TryCommitSendFailure(
                                request.Writer,
                                request.SessionLease,
                                request.ConnectionGeneration,
                                request.ConnectionIdentity,
                                "Send",
                                "命名管道写入超时。");
                            request.Completion.TrySetResult(SendDisposition.TransportWriteFailed);
                            owner.StopAccepting(SendDisposition.ScopeStale);
                            BreakTransport(
                                request.Writer,
                                request.SessionLease,
                                request.ConnectionGeneration,
                                request.ConnectionIdentity,
                                owner,
                                "SendWriteFailed",
                                "命名管道写入超时。",
                                fromSendWorker: true);
                            return;
                        }

                        writeTask.GetAwaiter().GetResult();
                        owner.ClearInflight(writeTask);
                        var stillCurrent = TryCommitSendSuccess(
                            request.Writer,
                            request.SessionLease,
                            request.ConnectionGeneration,
                            request.ConnectionIdentity);
                        // ApplicationClosing/ShutdownExpected are allowed to make the
                        // peer retire the exact connection as soon as the frame is
                        // consumed.  A successful physical write remains a successful
                        // send even when the reader observes that expected retirement
                        // before this worker can commit the non-essential health reset.
                        // No state is written back unless the exact generation is still
                        // current, so this does not let an old writer pollute a new one.
                        request.Completion.TrySetResult(
                            stillCurrent || request.CompletesConnectionLifecycle
                                ? SendDisposition.Sent
                                : SendDisposition.ScopeStale);
                    }
                    catch (Exception ex)
                    {
                        if (writeTask != null)
                        {
                            ObserveLateWriteTask(writeTask);
                            if (writeTask.IsCompleted) owner.ClearInflight(writeTask);
                        }
                        var detail = ex.GetBaseException().Message;
                        TryCommitSendFailure(
                            request.Writer,
                            request.SessionLease,
                            request.ConnectionGeneration,
                            request.ConnectionIdentity,
                            "Send",
                            detail);
                        request.Completion.TrySetResult(SendDisposition.TransportWriteFailed);
                        owner.StopAccepting(SendDisposition.ScopeStale);
                        BreakTransport(
                            request.Writer,
                            request.SessionLease,
                            request.ConnectionGeneration,
                            request.ConnectionIdentity,
                            owner,
                            "SendWriteFailed",
                            detail,
                            fromSendWorker: true);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected exact-connection shutdown.
            }
            finally
            {
                owner.StopAccepting(SendDisposition.ScopeStale);
            }
        }

        private bool IsCurrentSendOwner(SendQueueOwner owner, SendRequest request)
        {
            lock (_gate)
            {
                return ReferenceEquals(_sendQueueOwner, owner) &&
                       ReferenceEquals(_writer, request.Writer) &&
                       IsCurrentConnectionLocked(
                           request.ConnectionGeneration,
                           _activeSessionGeneration,
                           request.SessionLease,
                           request.ConnectionIdentity) &&
                       (_sessionClosing == 0 || request.Lifecycle);
            }
        }

        private static bool CompletesConnectionLifecycle(string messageType)
        {
            return string.Equals(messageType, WatchdogMessageType.ApplicationClosing, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.ShutdownExpected, StringComparison.Ordinal) ||
                   string.Equals(messageType, WatchdogMessageType.WatchdogTakeoverExit, StringComparison.Ordinal);
        }

        private static void ObserveLateWriteTask(Task writeTask)
        {
            if (writeTask == null) return;
            if (writeTask.IsCompleted)
            {
                try { _ = writeTask.Exception; } catch { }
                return;
            }
            try
            {
                writeTask.ContinueWith(
                    completed => { try { _ = completed.Exception; } catch { } },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch { }
        }

        private static bool StopAndJoinSendOwner(SendQueueOwner owner, int timeoutMs)
        {
            if (owner == null) return true;
            owner.StopAccepting(SendDisposition.ScopeStale);
            WaitTaskBounded(owner.WorkerTask, timeoutMs);
            var inflight = owner.CaptureInflight();
            WaitTaskBounded(inflight, timeoutMs);
            return owner.IsTerminal;
        }

        private void TryCommitSendFailure(
            StreamWriter expectedWriter,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity,
            string reason,
            string detail)
        {
            WatchdogClientTransportCallbacks callbacks = null;
            var committed = false;
            lock (_gate)
            {
                var exactNoConnection = expectedSessionLease > 0 &&
                    _activeSessionLease == expectedSessionLease &&
                    expectedConnectionGeneration == 0 &&
                    expectedConnectionIdentity == null &&
                    _activeConnectionGeneration == 0 &&
                    _activeConnectionIdentity == null &&
                    _activeConnectionGeneration <= 0;
                var exactConnection = expectedSessionLease > 0 &&
                    expectedConnectionGeneration > 0 &&
                    expectedConnectionIdentity != null &&
                    IsCurrentConnectionLocked(
                        expectedConnectionGeneration,
                        _activeSessionGeneration,
                        expectedSessionLease,
                        expectedConnectionIdentity);
                if ((exactConnection || exactNoConnection) &&
                    ReferenceEquals(_writer, expectedWriter) &&
                    _sessionClosing == 0 &&
                    _sendFailureReported == 0)
                {
                    _sendFailureReported = 1;
                    callbacks = _callbacks;
                    committed = true;
                }
            }
            if (committed)
                RaiseErrorWithCallbacks(callbacks, reason, detail);
        }

        private bool TryCommitSendSuccess(
            StreamWriter expectedWriter,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_writer, expectedWriter) ||
                    expectedSessionLease <= 0 ||
                    !IsCurrentConnectionLocked(
                        expectedConnectionGeneration,
                        _activeSessionGeneration,
                        expectedSessionLease,
                        expectedConnectionIdentity))
                    return false;
                Interlocked.Exchange(ref _sendFailureReported, 0);
                return true;
            }
        }

        public void ScheduleReconnect(int delayMs)
        {
            long sessionLease;
            long connectionGeneration;
            object connectionIdentity;
            lock (_gate)
            {
                sessionLease = _activeSessionLease;
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
            }
            TryScheduleReconnect(
                delayMs,
                sessionLease,
                connectionGeneration,
                connectionIdentity);
        }

        private ReconnectScheduleDisposition TryScheduleReconnect(
            int delayMs,
            long expectedSessionLease = 0,
            long expectedConnectionGeneration = 0,
            object expectedConnectionIdentity = null,
            object expectedWorkerIdentity = null,
            bool expectedConnectionClosed = false)
        {
            long attachOnlySessionLease;
            var attachOnlyReconnect = TryCaptureAttachOnlyReconnectScope(
                    expectedSessionLease,
                    expectedConnectionGeneration,
                    expectedConnectionIdentity,
                    expectedWorkerIdentity,
                    expectedConnectionClosed,
                    out attachOnlySessionLease);
            var started = false;
            WatchdogClientTransportEvent startedEvent = null;
            lock (_gate)
            {
                if (_sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0 ||
                    _sessionLifetime == null)
                    return ReconnectScheduleDisposition.Rejected;
                if (_options?.LaunchPolicy == WatchdogLaunchPolicy.AttachExistingAuthorityOnly &&
                    (!attachOnlyReconnect ||
                     attachOnlySessionLease != _activeSessionLease))
                    return ReconnectScheduleDisposition.Rejected;
                if (expectedSessionLease > 0 && !IsCurrentSessionLeaseLocked(expectedSessionLease))
                    return ReconnectScheduleDisposition.Rejected;
                if (expectedConnectionGeneration > 0)
                {
                    if (expectedSessionLease <= 0 || expectedConnectionIdentity == null)
                        return ReconnectScheduleDisposition.Rejected;
                    if (expectedConnectionClosed)
                    {
                        if (_activeConnectionGeneration != 0 || _activeConnectionIdentity != null ||
                            _connectionGeneration < expectedConnectionGeneration)
                            return ReconnectScheduleDisposition.Rejected;
                    }
                    else if (!IsCurrentConnectionLocked(
                                 expectedConnectionGeneration,
                                 _activeSessionGeneration,
                                 expectedSessionLease,
                                 expectedConnectionIdentity))
                        return ReconnectScheduleDisposition.Rejected;
                }
                if (expectedWorkerIdentity != null &&
                    !ReferenceEquals(_monitorTaskIdentity, expectedWorkerIdentity) &&
                    !ReferenceEquals(_reconnectTaskIdentity, expectedWorkerIdentity))
                    return ReconnectScheduleDisposition.Rejected;
                if (_reconnectTask != null && !_reconnectTask.IsCompleted)
                    return ReconnectScheduleDisposition.AlreadySupervised;
                var generation = ++_reconnectGeneration;
                var sessionLease = _activeSessionLease;
                var sessionToken = _sessionLifetime.Token;
                var taskIdentity = new object();
                var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _reconnectTaskIdentity = taskIdentity;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await startGate.Task.ConfigureAwait(false);
                        if (delayMs > 0) await Task.Delay(delayMs, sessionToken).ConfigureAwait(false);
                        if (!IsCurrentReconnectWorker(generation, sessionLease, taskIdentity)) return;
                        for (var attempt = 0; attempt < WatchdogTransportPolicy.ReconnectMaxAttempts; attempt++)
                        {
                            if (IsClosing || IsSafeDegraded ||
                                !IsCurrentReconnectWorker(generation, sessionLease, taskIdentity)) return;
                            if (!SetReconnectAttemptIfCurrent(generation, sessionLease, taskIdentity, attempt + 1)) return;
                            if (attempt > 0)
                                await Task.Delay(
                                    WatchdogTransportPolicy.SelectReconnectBackoffMs(attempt - 1),
                                    sessionToken).ConfigureAwait(false);
                            if (!IsCurrentReconnectWorker(generation, sessionLease, taskIdentity)) return;
                            var attemptDisposition = await ReconnectOnceAsync(
                                    generation,
                                    sessionLease,
                                    taskIdentity)
                                .ConfigureAwait(false);
                            if (attemptDisposition == ReconnectAttemptDisposition.Succeeded ||
                                attemptDisposition == ReconnectAttemptDisposition.FailClosed)
                                return;
                        }
                        if (IsCurrentReconnectWorker(generation, sessionLease, taskIdentity))
                            EnterSafeDegraded(generation, sessionLease, taskIdentity);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        if (IsCurrentReconnectWorker(generation, sessionLease, taskIdentity))
                        {
                            RaiseErrorWithCallbacks(
                                CaptureReconnectCallbacks(sessionLease, taskIdentity),
                                "ReconnectWorker",
                                ex.GetBaseException().Message);
                            EnterSafeDegraded(generation, sessionLease, taskIdentity);
                        }
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (_reconnectGeneration == generation &&
                                _activeSessionLease == sessionLease &&
                                ReferenceEquals(_reconnectTaskIdentity, taskIdentity))
                            {
                                _reconnectAttempt = 0;
                                _reconnectTask = null;
                                _reconnectTaskIdentity = null;
                            }
                        }
                    }
                });
                _reconnectTask = task;
                startGate.TrySetResult(true);
                started = true;
                startedEvent = ReserveStateChangedLocked(
                    "ReconnectStarted",
                    "Generation=" + generation.ToString(CultureInfo.InvariantCulture));
            }
            if (started)
                EnqueueStateChanged(startedEvent);
            return started
                ? ReconnectScheduleDisposition.Started
                : ReconnectScheduleDisposition.Rejected;
        }

        private bool EnsureSingleReconnectSupervisor(long sessionLease)
        {
            var disposition = TryScheduleReconnect(
                WatchdogTransportPolicy.ReconnectInitial250,
                sessionLease);
            return disposition == ReconnectScheduleDisposition.Started ||
                   disposition == ReconnectScheduleDisposition.AlreadySupervised;
        }

        public void CloseFailedStartup()
        {
            PendingHelper pending;
            LaunchReservation launchReservation;
            Task launchTask;
            long sessionLease;
            var pendingPid = 0;
            WatchdogClientTransportEvent pendingClearedEvent = null;
            lock (_gate)
            {
                sessionLease = _activeSessionLease;
                pending = _pending;
                launchReservation = _launchReservation ?? _retainedLaunchReservation;
                launchTask = _launchTask ?? _retainedLaunchTask;
                _pending = null;
                if (pending != null)
                {
                    pendingPid = pending.ProcessId;
                    _identity.TryClearPending(ToPendingSnapshot(pending));
                    pendingClearedEvent = ReserveStateChangedLocked(
                        "PendingCleared",
                        "PID=" + pendingPid + ";Reason=StartupFailure");
                }
                _launchReservation = null;
                _launchGate = 0;
                _pingResponseScheduled = 0;
                _pingResponseTaskIdentity = null;
            }
            CloseCurrentTransport(sessionLease);
            DisposePending(pending, true);
            CancelAndJoinLaunch(
                launchReservation,
                launchTask,
                "LaunchClosureDeadlineExceeded:StartupFailure");
            if (pendingClearedEvent != null)
                EnqueueStateChanged(pendingClearedEvent);
            lock (_gate)
            {
                if (!_identity.HasAuthority)
                    _identity.Reset();
            }
            // A startup failure is a terminal transaction boundary.  Tear
            // down the session/reconnect worker as well as the pending helper
            // so no task can continue operating on a half-initialized session.
            Shutdown();
        }

        /// <summary>
        /// Marks a session closing only if the expected public identity is
        /// still current.  An old callback cannot close a newer session.
        /// </summary>
        [Obsolete(
            "Compatibility overload only; production callers must pass the captured session lease.")]
        public bool TryMarkSessionClosing(
            string expectedSessionId,
            long expectedSessionGeneration)
        {
            long sessionLease;
            lock (_gate)
            {
                sessionLease = _activeSessionLease;
            }
            return TryMarkSessionClosing(
                expectedSessionId,
                expectedSessionGeneration,
                sessionLease);
        }

        /// <summary>
        /// Marks the session closing only when SessionId, generation and the
        /// captured lease all match atomically.  A stale callback from an
        /// older lease cannot close a newer BeginSession.
        /// </summary>
        public bool TryMarkSessionClosing(
            string expectedSessionId,
            long expectedSessionGeneration,
            long expectedSessionLease)
        {
            return TryMarkSessionClosingExact(
                       expectedSessionId,
                       expectedSessionGeneration,
                       expectedSessionLease) == ExactSessionClosingResult.Marked;
        }

        public ExactSessionClosingResult TryMarkSessionClosingExact(
            string expectedSessionId,
            long expectedSessionGeneration,
            long expectedSessionLease)
        {
            if (string.IsNullOrWhiteSpace(expectedSessionId) ||
                expectedSessionGeneration <= 0 || expectedSessionLease <= 0)
                return ExactSessionClosingResult.NoEngineSession;
            lock (_gate)
            {
                if (_options == null || _activeSessionLease <= 0)
                    return ExactSessionClosingResult.NoEngineSession;
                if (_activeSessionLease != expectedSessionLease ||
                    !string.Equals(_options.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                    _options.SessionGeneration != expectedSessionGeneration)
                    return ExactSessionClosingResult.IdentityMismatch;
                if (_activeConnectionGeneration == 0 &&
                    _activeConnectionIdentity == null &&
                    _activeSessionGeneration == 0)
                {
                    _sessionClosing = 1;
                    return ExactSessionClosingResult.ExactSessionDetached;
                }
                if (_activeSessionGeneration != expectedSessionGeneration)
                    return ExactSessionClosingResult.IdentityMismatch;
                _sessionClosing = 1;
                return ExactSessionClosingResult.Marked;
            }
        }

        public void MarkSessionClosing()
        {
            lock (_gate)
            {
                // Keep the legacy no-argument API idempotent.  When a
                // session exists it still takes the same gate as the
                // identity-bound API; with no session it simply records the
                // local closing intent for compatibility.
                _sessionClosing = 1;
            }
        }

        /// <summary>
        /// Atomically detaches the current session, performs all potentially
        /// blocking cancellation/disposal outside the engine gate, then
        /// returns immutable evidence of the detached worker set.  This is the
        /// only production shutdown implementation; the void overloads below
        /// are compatibility facades.
        /// </summary>
        public ShutdownReceipt ShutdownWithReceipt()
        {
            Volatile.Write(ref _sessionClosing, 1);
            PendingHelper pending;
            CancellationTokenSource lifetime;
            CancellationTokenSource sessionLifetime;
            NamedPipeClientStream pipe;
            StreamReader reader;
            StreamWriter writer;
            SendQueueOwner sendOwner;
            Task readerTask;
            Task heartbeatTask;
            Task sendTask;
            Task monitorTask;
            Task reconnectTask;
            Task connectTask;
            Task launchTask;
            Task activeLaunchTask;
            Task retainedLaunchTask;
            ConnectReservation connectReservation;
            LaunchReservation launchReservation;
            LaunchReservation activeLaunchReservation;
            LaunchReservation retainedLaunchReservation;
            SidecarProcessHandleOwner capturedPendingOwner;
            int ownedHandleCountBefore;
            long capturedSessionLease;
            bool hadSession;
            bool hadPending;
            bool hadAuthority;
            SidecarIdentityStateMachine.AuthoritySnapshot authority;
            WatchdogClientTransportEvent pendingClearedEvent = null;
            WatchdogClientTransportEvent authorityClearedEvent = null;
            WatchdogClientTransportEvent shutdownEvent = null;
            lock (_gate)
            {
                hadSession = _options != null || _sessionLifetime != null ||
                    _lifetime != null || _pipe != null || _reader != null ||
                    _writer != null || _pending != null ||
                    _readerTask != null || _heartbeatTask != null ||
                    _sendQueueOwner != null || _sendTask != null ||
                    _monitorTask != null || _reconnectTask != null ||
                    _connectTask != null || _launchTask != null ||
                    _retainedLaunchTask != null || _connectReservation != null ||
                    _launchReservation != null || _retainedLaunchReservation != null ||
                    _activeSessionLease > 0;
                capturedSessionLease = _activeSessionLease > 0
                    ? _activeSessionLease
                    : _sessionLeaseCounter;
                ownedHandleCountBefore = SidecarProcessHandleOwner.LiveOwnedCount;
                pending = _pending;
                capturedPendingOwner = pending?.Launch?.Owner;
                hadPending = pending != null || _identity.Pending != null;
                authority = _identity.Authority;
                hadAuthority = authority != null;
                _pending = null;
                lifetime = _lifetime;
                sessionLifetime = _sessionLifetime;
                pipe = _pipe;
                reader = _reader;
                writer = _writer;
                sendOwner = _sendQueueOwner;
                readerTask = _readerTask;
                heartbeatTask = _heartbeatTask;
                sendTask = _sendTask;
                monitorTask = _monitorTask;
                reconnectTask = _reconnectTask;
                connectTask = _connectTask;
                activeLaunchReservation = _launchReservation;
                retainedLaunchReservation = _retainedLaunchReservation;
                activeLaunchTask = _launchTask;
                retainedLaunchTask = _retainedLaunchTask;
                connectReservation = _connectReservation;
                launchReservation = activeLaunchReservation ?? retainedLaunchReservation;
                launchTask = activeLaunchTask ?? retainedLaunchTask;
                _lifetime = null;
                _sessionLifetime = null;
                _pipe = null;
                _reader = null;
                _writer = null;
                _sendQueueOwner = null;
                _sendTask = null;
                _attached = null;
                _activeConnectionGeneration = 0;
                _activeConnectionIdentity = null;
                _activeSessionGeneration = 0;
                _activeSessionLease = 0;
                _readerTask = null;
                _readerTaskIdentity = null;
                _heartbeatTask = null;
                _heartbeatTaskIdentity = null;
                _monitorTask = null;
                _reconnectTask = null;
                _monitorTaskIdentity = null;
                _reconnectTaskIdentity = null;
                _launchReservation = null;
                _launchGate = 0;
                _connectReservation = null;
                _connectTask = null;
                _pingResponseScheduled = 0;
                _pingResponseTaskIdentity = null;
                _identity.Reset();
                _options = null;
                if (hadPending)
                    pendingClearedEvent = ReserveStateChangedLocked("PendingCleared", "Reason=Shutdown");
                if (hadAuthority)
                    authorityClearedEvent = ReserveStateChangedLocked(
                        "AuthorityCleared",
                        "PID=" + authority.ProcessId + ";Reason=Shutdown");
                if (hadSession)
                    shutdownEvent = ReserveStateChangedLocked("Shutdown", "TransportSessionClosed");
            }
            sendOwner?.StopAccepting(SendDisposition.ScopeStale);
            CancelNoDispose(lifetime);
            CancelNoDispose(sessionLifetime);
            TryDispose(pipe);
            TryDispose(reader);
            TryDispose(writer);
            DisposePending(pending, true);
                    StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(monitorTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(reconnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(connectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    // ConnectOwnerAsync may finish just before its public
                    // ConnectAsync owner publishes the reservation outcome.
                    // The reservation completion is the externally visible
                    // lifecycle boundary, so a shutdown receipt must join it
                    // as well instead of sampling it immediately after only
                    // the inner owner task has become terminal.
                    WaitTaskBounded(
                        connectReservation?.Completion.Task,
                        WatchdogTransportPolicy.ConnectFailureJoin1000);
            CancelAndJoinLaunch(
                launchReservation,
                launchTask,
                "LaunchClosureDeadlineExceeded:Shutdown");
            TryDispose(lifetime);
            TryDispose(sessionLifetime);
            if (pendingClearedEvent != null) EnqueueStateChanged(pendingClearedEvent);
            if (authorityClearedEvent != null) EnqueueStateChanged(authorityClearedEvent);
            if (shutdownEvent != null) EnqueueStateChanged(shutdownEvent);

            WorkerTerminationState workerTermination;
            bool sessionDetached;
            lock (_gate)
            {
                // The second gate read is deliberately after all OS/task
                // work.  It prevents a receipt from claiming release while a
                // captured lease still has an engine-owned resource attached.
                var launchTerminal =
                    (launchTask == null || launchTask.IsCompleted) &&
                    (launchReservation == null || launchReservation.Completion.Task.IsCompleted);
                var launchReservationTerminal =
                    activeLaunchReservation == null ||
                    activeLaunchReservation.Completion.Task.IsCompleted;
                launchReservationTerminal = launchReservationTerminal &&
                    (retainedLaunchReservation == null ||
                     retainedLaunchReservation.Completion.Task.IsCompleted);
                var retainedTaskTerminal = retainedLaunchTask == null || retainedLaunchTask.IsCompleted;
                var retainedLaunchTerminal =
                    retainedTaskTerminal &&
                    (retainedLaunchReservation == null ||
                     retainedLaunchReservation.Completion.Task.IsCompleted) &&
                    _retainedLaunchReservation == null &&
                    _retainedLaunchTask == null;
                var pendingOwnerReleased = _pending == null && !_identity.HasPending;
                var capturedOwnerReleased = capturedPendingOwner == null ||
                    capturedPendingOwner.IsDisposed;
                var transportDetached = _pipe == null && _reader == null &&
                    _writer == null && _activeConnectionGeneration == 0 &&
                    _activeConnectionIdentity == null && _attached == null;
                var readerTerminal = readerTask == null || readerTask.IsCompleted;
                var heartbeatTerminal = heartbeatTask == null || heartbeatTask.IsCompleted;
                var monitorTerminal = monitorTask == null || monitorTask.IsCompleted;
                var reconnectTerminal = reconnectTask == null || reconnectTask.IsCompleted;
                var connectTerminal = (connectTask == null || connectTask.IsCompleted) &&
                    (connectReservation == null ||
                     connectReservation.Completion.Task.IsCompleted);
                var sendTerminal = sendOwner == null ||
                    ((sendTask == null || sendTask.IsCompleted) && sendOwner.IsTerminal);
                // A retained launch is a sixth-worker resource as well as a
                // reservation.  Keep LaunchTerminal false until its retained
                // task has actually settled, even if the active task was
                // already detached before the timeout path registered it.
                if (!retainedTaskTerminal || !retainedLaunchTerminal)
                    launchTerminal = false;
                workerTermination = new WorkerTerminationState(
                    capturedSessionLease,
                    DateTime.UtcNow.Ticks,
                    readerTerminal,
                    heartbeatTerminal,
                    monitorTerminal,
                    reconnectTerminal,
                    connectTerminal,
                    launchTerminal,
                    sendTerminal,
                    launchReservationTerminal,
                    retainedLaunchTerminal,
                    pendingOwnerReleased,
                    transportDetached,
                    SidecarProcessHandleOwner.LiveOwnedCount,
                    ownedHandleCountBefore,
                    capturedOwnerReleased);
                sessionDetached = _activeSessionLease != capturedSessionLease &&
                    _pipe == null && _pending == null && !_identity.HasAuthority;
            }
            return new ShutdownReceipt(
                capturedSessionLease,
                workerTermination.CapturedUtcTicks,
                workerTermination,
                shutdownEvent != null,
                sessionDetached);
        }

        public void Shutdown() => ShutdownWithReceipt();

        public void Dispose() => ShutdownWithReceipt();

        private async Task ConnectAsync(string sessionId, string pipeName, long sessionLease)
        {
            ConnectReservation reservation;
            Task existingTask;
            lock (_gate)
            {
                if (!IsCurrentSessionLeaseLocked(sessionLease) ||
                    _sessionClosing != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0)
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog连接准入时Session已失效。",
                        null);

                if (_connectReservation != null)
                {
                    if (_connectReservation.SessionLease != sessionLease)
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog连接准入被其他Session占用。",
                            null);
                    existingTask = _connectReservation.Completion.Task;
                    reservation = null;
                }
                else
                {
                    reservation = new ConnectReservation(
                        checked(++_connectLeaseCounter),
                        sessionLease);
                    _connectReservation = reservation;
                    _connectGeneration = reservation.Generation;
                    existingTask = null;
                }
            }

            // A concurrent Start/reconnect in the same session is already
            // supervised by the first connector.  Await its immutable
            // completion instead of opening a second pipe.
            if (reservation == null)
            {
                await existingTask.ConfigureAwait(false);
                return;
            }

            Task ownerTask = null;
            try
            {
                ownerTask = ConnectOwnerAsync(sessionId, pipeName, sessionLease);
                lock (_gate)
                {
                    if (ReferenceEquals(_connectReservation, reservation))
                        _connectTask = ownerTask;
                }
                await ownerTask.ConfigureAwait(false);
                reservation.Completion.TrySetResult(true);
            }
            catch (WatchdogConnectException ex)
            {
                // Publish the sticky fail-closed state before completing the
                // shared reservation.  Joiners must observe the durable
                // first-failure snapshot before they receive the same
                // exception; otherwise a caller can race into a second
                // launch/reconnect while the owner is still unwinding.
                if (IsTransportFailClosedKind(ex.Kind))
                    EnterTransportFailClosed(sessionLease, ex.Kind, ex.Message);
                reservation.PublishFailure(ex);
                throw;
            }
            catch (Exception ex)
            {
                reservation.PublishFailure(ex);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connectReservation, reservation))
                    {
                        _connectReservation = null;
                        _connectTask = null;
                    }
                }
            }
        }

        private async Task ConnectOwnerAsync(string sessionId, string pipeName, long sessionLease)
        {
            NamedPipeClientStream pipe = null;
            StreamReader reader = null;
            StreamWriter writer = null;
            CancellationTokenSource lifetime = null;
            long connectionGeneration = 0;
            long sessionGeneration = 0;
            object connectionIdentity = null;
            Task readerTask = null;
            Task heartbeatTask = null;
            SendQueueOwner sendOwner = null;
            Task sendTask = null;
            Task pipeConnectTask = null;
            var connectionTransferred = false;
            var stage = WatchdogConnectFailureKind.PipeUnavailable;
            try
            {
                if (!IsCurrentSessionLease(sessionLease))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog连接所属Session已失效。");

                try
                {
                    pipe = _pipeFactory.Create(pipeName);
                    pipeConnectTask = Task.Run(() => pipe.Connect(
                        WatchdogTransportPolicy.PipeConnect5000));
                    if (await Task.WhenAny(
                            pipeConnectTask,
                            Task.Delay(WatchdogTransportPolicy.Guard5500)).ConfigureAwait(false) != pipeConnectTask)
                        throw new TimeoutException("Watchdog命名管道连接超时。");
                    await pipeConnectTask.ConfigureAwait(false);
                    if (!IsCurrentSessionLease(sessionLease))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog命名管道连接完成后Session已失效。");
                }
                catch (WatchdogConnectException) { throw; }
                catch (Exception ex)
                {
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.PipeUnavailable,
                        "Watchdog命名管道不可用：" + ex.GetBaseException().Message,
                        ex);
                }

                stage = WatchdogConnectFailureKind.TransportFailure;
                reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                lifetime = new CancellationTokenSource();
                // 令牌必须由这一代连接在 CTS 退休前冻结。异步 lambda 若在
                // CloseTransportOnly 已 Dispose CTS 后才求值 lifetime.Token，
                // 会产生 ObjectDisposedException 并成为未观察异常。
                var lifetimeToken = lifetime.Token;
                var attached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    if (!IsCurrentSessionLeaseLocked(sessionLease))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog连接在安装前所属Session已失效。");
                    sessionGeneration = _options.SessionGeneration;
                    connectionGeneration = ++_connectionGeneration;
                    connectionIdentity = new object();
                    _pipe = pipe;
                    _reader = reader;
                    _writer = writer;
                    _lifetime = lifetime;
                    _attached = attached;
                    _activeConnectionGeneration = connectionGeneration;
                    _activeConnectionIdentity = connectionIdentity;
                    _readerTaskIdentity = connectionIdentity;
                    _heartbeatTaskIdentity = connectionIdentity;
                    _activeSessionGeneration = sessionGeneration;
                    _attachedConnectionGeneration = 0;
                    _lastHeartbeatAckUtcTicks = DateTime.UtcNow.Ticks;
                    _lastHeartbeatAckSequence = 0;
                    _sendFailureReported = 0;
                    // Publish writer and its exact-generation queue owner in
                    // one atomic state transition.  Guarded callers are
                    // synchronous and may arrive as soon as _writer becomes
                    // visible; exposing the writer first would let them see
                    // SendNoWriter and tear down the new reconnect before the
                    // dedicated owner thread has even been installed.
                    sendOwner = new SendQueueOwner(
                        writer,
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity);
                    _sendQueueOwner = sendOwner;
                    connectionTransferred = true;
                }

                // TrySendForSession is a synchronous compatibility boundary. Under a
                // Parallel.For (or a saturated UI/control worker pool), every caller can
                // occupy a ThreadPool worker while waiting for this queue. The exact-
                // connection writer must therefore own a dedicated thread and must not
                // require a ThreadPool continuation to make the physical pipe write.
                sendTask = Task.Factory.StartNew(
                    () => RunSendQueue(sendOwner),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                sendOwner.WorkerTask = sendTask;
                lock (_gate)
                {
                    if (!IsCurrentConnectionLocked(
                            connectionGeneration,
                            sessionGeneration,
                            sessionLease,
                            connectionIdentity) ||
                        !ReferenceEquals(_sendQueueOwner, sendOwner))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog发送worker安装期间Session已失效。");
                    _sendTask = sendTask;
                }

                readerTask = Task.Run(() => ReaderLoopAsync(
                    lifetimeToken,
                    reader,
                    sessionId,
                    connectionGeneration,
                    sessionGeneration,
                    sessionLease,
                    connectionIdentity));
                lock (_gate)
                {
                    if (IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                        _readerTask = readerTask;
                }
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog Reader启动期间Session已失效。");

                WatchdogClientTransportOptions options;
                WatchdogClientTransportCallbacks callbacks;
                Func<bool, int, WatchdogRunSession> createRunSession;
                lock (_gate)
                {
                    if (!IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog Attach准备期间Session已失效。");
                    options = _options;
                    callbacks = _callbacks;
                    createRunSession = callbacks?.CreateRunSession;
                }
                if (createRunSession == null)
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.InvalidConfiguration,
                        "Watchdog Attach 缺少 CreateRunSession 回调。");
                WatchdogRunSession session;
                try
                {
                    session = createRunSession(options.RecoveryProcess, options.RecoveryAttempt);
                }
                catch (Exception ex)
                {
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.HandshakeRejected,
                        "CreateRunSession 回调失败：" + ex.GetBaseException().Message,
                        ex);
                }
                if (session == null)
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.InvalidConfiguration,
                        "CreateRunSession 未返回有效Session。");
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity) ||
                    !SendForConnection(new WatchdogMessage
                {
                    Type = WatchdogMessageType.Attach,
                    SessionId = sessionId,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    Session = session
                }, sessionLease, connectionGeneration, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.TransportFailure,
                        "Watchdog Attach 消息发送失败。");
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog Attach发送后Session已失效。");
                try { callbacks.RecordEvent?.Invoke("AttachSent", options.RecoveryProcess ? "RecoveryProcess" : "MainProcess"); } catch { }
                stage = WatchdogConnectFailureKind.HandshakeRejected;
                var timeout = Task.Delay(WatchdogTransportPolicy.Handshake5000);
                if (await Task.WhenAny(attached.Task, timeout).ConfigureAwait(false) != attached.Task)
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.HandshakeRejected,
                        "Watchdog Attached 握手超时。");
                // A fail-closed Reader completes the local handshake TCS with
                // the typed first failure before detaching the connection.
                // Preserve that outcome for ConnectAsync; only synthesize
                // SessionRevoked when the connection disappeared without a
                // completed handshake result.
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity) &&
                    !attached.Task.IsCompleted)
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog Attached等待完成后Session已失效。");
                try
                {
                    if (!await attached.Task.ConfigureAwait(false))
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.HandshakeRejected,
                            "Watchdog Attached 握手被拒绝。");
                }
                catch (WatchdogConnectException) { throw; }
                catch (Exception ex)
                {
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.HandshakeRejected,
                        "Watchdog Attached 握手失败：" + ex.GetBaseException().Message,
                        ex);
                }
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog Attached 后Session已失效。");

                if (!SendHeartbeatSnapshot("Attached", sessionLease, connectionGeneration, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.TransportFailure,
                        "Attached 后首个Watchdog心跳发送失败。");
                heartbeatTask = Task.Run(() => HeartbeatLoopAsync(
                    lifetimeToken,
                    connectionGeneration,
                    sessionLease,
                    connectionIdentity));
                lock (_gate)
                {
                    if (IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                        _heartbeatTask = heartbeatTask;
                }
                if (!IsCurrentConnection(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Watchdog Heartbeat启动期间Session已失效。");
                StartTransportMonitor(sessionLease, connectionGeneration, connectionIdentity);
            }
            catch (WatchdogConnectException ex)
            {
                CleanupConnectionFailure(connectionGeneration, sessionLease, connectionIdentity, ex.Kind);
                if (!connectionTransferred)
                {
                    CancelNoDispose(lifetime);
                    TryDispose(reader);
                    TryDispose(writer);
                    TryDispose(pipe);
                    StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    TryDispose(lifetime);
                }
                else
                {
                    // The active connection may have been revoked between
                    // task creation and its CAS into the engine.  Its local
                    // task was never transferred to the active owner, so
                    // still join it here after the exact-connection cleanup.
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                }
                throw;
            }
            catch (OperationCanceledException ex)
            {
                CleanupConnectionFailure(connectionGeneration, sessionLease, connectionIdentity, WatchdogConnectFailureKind.Cancelled);
                if (!connectionTransferred)
                {
                    CancelNoDispose(lifetime);
                    TryDispose(reader);
                    TryDispose(writer);
                    TryDispose(pipe);
                    StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    TryDispose(lifetime);
                }
                else
                {
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                }
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.Cancelled,
                    "Watchdog连接被取消。",
                    ex);
            }
            catch (Exception ex)
            {
                var kind = stage == WatchdogConnectFailureKind.PipeUnavailable
                    ? WatchdogConnectFailureKind.PipeUnavailable
                    : stage;
                CleanupConnectionFailure(connectionGeneration, sessionLease, connectionIdentity, kind);
                if (!connectionTransferred)
                {
                    CancelNoDispose(lifetime);
                    TryDispose(reader);
                    TryDispose(writer);
                    TryDispose(pipe);
                    StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    TryDispose(lifetime);
                }
                else
                {
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(pipeConnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                }
                throw new WatchdogConnectException(
                    kind,
                    ex.GetBaseException().Message,
                    ex);
            }
        }

        private void CleanupConnectionFailure(
            long connectionGeneration,
            long sessionLease,
            object connectionIdentity,
            WatchdogConnectFailureKind kind)
        {
            if (connectionGeneration > 0)
                CloseTransportOnly(sessionLease, connectionGeneration, connectionIdentity);
        }

        private async Task LaunchSidecarIfAuthorized(
            long expectedSessionLease,
            WatchdogLaunchPolicy expectedPolicy)
        {
            PendingHelper observedPending;
            long sessionLease;
            bool authorityWon;
            WatchdogClientTransportOptions options;
            LaunchReservation reservation = null;
            Task joinLaunchTask = null;
            Task<LaunchReservation.Outcome> joinLaunchCompletion = null;
            WatchdogClientTransportEvent pendingClearedEvent = null;
            Exception reservationFailure = null;
            lock (_gate)
            {
                if (!IsCurrentSessionLeaseLocked(expectedSessionLease))
                {
                    reservationFailure = new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Sidecar启动前Session lease已失效。");
                }
                else if (expectedPolicy != WatchdogLaunchPolicy.LaunchIfPipeUnavailable ||
                         _options?.LaunchPolicy != WatchdogLaunchPolicy.LaunchIfPipeUnavailable)
                {
                    reservationFailure = new WatchdogConnectException(
                        WatchdogConnectFailureKind.InvalidConfiguration,
                        "当前Session策略禁止启动Sidecar。");
                }
                else if (_launchClosureActive != 0 ||
                    _sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0)
                    reservationFailure = new InvalidOperationException(
                        "Session已关闭、安全降级或Launch关闭尚未收口，禁止启动Sidecar。");
                observedPending = _pending;
                sessionLease = expectedSessionLease;
                authorityWon = _identity.HasAuthority;
                options = _options;
                if (reservationFailure == null && !authorityWon)
                {
                    var activeLaunch = _launchReservation;
                    var activeLaunchTask = _launchTask;
                    if (activeLaunch != null && activeLaunch.SessionLease == sessionLease)
                    {
                        if (activeLaunchTask != null)
                            joinLaunchTask = activeLaunchTask;
                        else
                            joinLaunchCompletion = activeLaunch.Completion.Task;
                    }
                    else if (activeLaunchTask != null &&
                             !_launchTask.IsCompleted &&
                             _launchTaskSessionLease == sessionLease)
                        joinLaunchTask = activeLaunchTask;
                    else if (_launchGate != 0 || activeLaunch != null ||
                             (activeLaunchTask != null && !_launchTask.IsCompleted))
                        reservationFailure = new IOException("Sidecar helper已在其他Session启动，拒绝重复启动。");
                }
            }
            if (reservationFailure != null) throw reservationFailure;
            if (joinLaunchTask != null)
            {
                await joinLaunchTask.ConfigureAwait(false);
                return;
            }
            if (joinLaunchCompletion != null)
            {
                await AwaitLaunchCompletionAsync(joinLaunchCompletion).ConfigureAwait(false);
                return;
            }
            if (authorityWon) return;

            if (observedPending != null)
            {
                // Process identity inspection is deliberately outside the
                // engine gate.  Re-enter the gate only to CAS the exact
                // pending snapshot before clearing its reservation.
                var pendingAlive = IsProcessIdentityAlive(
                    observedPending.ProcessId,
                    observedPending.StartUtcTicks);
                var pendingConfirmedDead = !pendingAlive && IsProcessIdentityConfirmedDead(
                    observedPending.ProcessId,
                    observedPending.StartUtcTicks);
                if (pendingAlive || !pendingConfirmedDead) return;
                lock (_gate)
                {
                    if (_activeSessionLease != sessionLease)
                        throw new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Sidecar启动清理pending前Session lease已失效。");
                    if (!ReferenceEquals(_pending, observedPending)) return;
                    _pending = null;
                    _identity.TryClearPending(ToPendingSnapshot(observedPending));
                    pendingClearedEvent = ReserveStateChangedLocked(
                        "PendingCleared",
                        "PID=" + observedPending.ProcessId + ";Reason=ProcessExited");
                }
                DisposePending(observedPending, false);
                EnqueueStateChanged(pendingClearedEvent);
            }

            if (!IsCurrentSessionLease(sessionLease))
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.SessionRevoked,
                    "Sidecar reservation前Session lease已失效。");
            if (options == null || !File.Exists(options.SidecarExecutablePath))
                throw new FileNotFoundException("未找到独立看门狗程序。", options?.SidecarExecutablePath);

            int parentPid;
            long parentStartTicks;
            using (var current = Process.GetCurrentProcess())
            {
                parentPid = current.Id;
                parentStartTicks = current.StartTime.ToUniversalTime().Ticks;
            }
            lock (_gate)
            {
                if (!IsCurrentSessionLeaseLocked(sessionLease))
                    reservationFailure = new WatchdogConnectException(
                        WatchdogConnectFailureKind.SessionRevoked,
                        "Sidecar reservation前Session lease已失效。");
                else if (expectedPolicy != WatchdogLaunchPolicy.LaunchIfPipeUnavailable ||
                         _options?.LaunchPolicy != WatchdogLaunchPolicy.LaunchIfPipeUnavailable)
                    reservationFailure = new WatchdogConnectException(
                        WatchdogConnectFailureKind.InvalidConfiguration,
                        "Sidecar reservation前当前Session策略已变更，禁止启动。");
                else if (_launchClosureActive != 0 ||
                    _sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0)
                    reservationFailure = new InvalidOperationException(
                        "Session已关闭、安全降级或Launch关闭尚未收口，禁止启动Sidecar。");
                if (reservationFailure == null && (_identity.HasAuthority || _pending != null))
                    return;
                if (reservationFailure == null && (_launchGate != 0 || _launchReservation != null ||
                    (_launchTask != null && !_launchTask.IsCompleted)))
                {
                    if (_launchReservation != null &&
                        _launchReservation.SessionLease == sessionLease)
                    {
                        if (_launchTask != null)
                            joinLaunchTask = _launchTask;
                        else
                            joinLaunchCompletion = _launchReservation.Completion.Task;
                    }
                    else if (_launchTask != null &&
                             _launchTaskSessionLease == sessionLease)
                        joinLaunchTask = _launchTask;
                    else
                        reservationFailure = new IOException("Sidecar helper已在其他Session启动，拒绝重复启动。");
                }
                if (reservationFailure == null && joinLaunchTask == null)
                {
                    options = _options;
                    var nonce = Guid.NewGuid().ToString("N");
                    reservation = new LaunchReservation(
                        checked(++_launchLeaseCounter),
                        _activeSessionLease,
                        options,
                        parentPid,
                        parentStartTicks,
                        nonce,
                        new CancellationTokenSource());
                    _launchReservation = reservation;
                    _launchGate = 1;
                }
            }

            if (reservationFailure != null) throw reservationFailure;

            if (joinLaunchTask != null)
            {
                await joinLaunchTask.ConfigureAwait(false);
                return;
            }
            if (joinLaunchCompletion != null)
            {
                await AwaitLaunchCompletionAsync(joinLaunchCompletion).ConfigureAwait(false);
                return;
            }

            var launchTask = LaunchSidecarCoreAsync(reservation, options);
            lock (_gate)
            {
                if (ReferenceEquals(_launchReservation, reservation))
                {
                    _launchTask = launchTask;
                    _launchTaskSessionLease = reservation.SessionLease;
                }
                else if (ReferenceEquals(_retainedLaunchReservation, reservation))
                {
                    _retainedLaunchTask = launchTask;
                    _launchTaskSessionLease = reservation.SessionLease;
                }
            }
            try
            {
                await launchTask.ConfigureAwait(false);
            }
            finally
            {
                CompleteLaunchTask(reservation, launchTask);
            }
        }

        private async Task LaunchSidecarCoreAsync(
            LaunchReservation reservation,
            WatchdogClientTransportOptions options)
        {
            try
            {
                SidecarProcessLaunchResult launch = null;
                try
                {
                    launch = await _launcher.LaunchAsync(new SidecarProcessLaunchRequest
                    {
                        ExecutablePath = options.SidecarExecutablePath,
                        WorkingDirectory = Path.GetDirectoryName(options.MainExecutablePath) ?? Environment.CurrentDirectory,
                        Arguments = BuildSidecarArguments(
                            reservation.ParentProcessId,
                            reservation.ParentProcessStartUtcTicks,
                            options,
                            reservation.InstanceNonce)
                    }, reservation.Lifetime.Token).ConfigureAwait(false);
                    reservation.Lifetime.Token.ThrowIfCancellationRequested();
                    if (launch == null || launch.Process == null)
                        throw new InvalidOperationException("Sidecar process start returned null.");
                }
                catch (OperationCanceledException ex)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_launchReservation, reservation))
                        {
                            _launchReservation = null;
                            _launchGate = 0;
                        }
                    }
                    DisposeLaunch(launch, true);
                    var cancelled = new WatchdogConnectException(
                        WatchdogConnectFailureKind.Cancelled,
                        "Watchdog Sidecar启动被取消，已收口进程资源。",
                        ex);
                    reservation.Fail(cancelled);
                    throw cancelled;
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_launchReservation, reservation))
                        {
                            _launchReservation = null;
                            _launchGate = 0;
                        }
                    }
                    DisposeLaunch(launch, true);
                    var launchFailure = new WatchdogConnectException(
                        WatchdogConnectFailureKind.LaunchFailure,
                        "Watchdog Sidecar启动失败：" + ex.GetBaseException().Message,
                        ex);
                    reservation.Fail(launchFailure);
                    throw launchFailure;
                }

                PendingHelper bound = null;
                var stale = false;
                Exception bindFailure = null;
                try
                {
                    var launchProcessId = launch.Process.Id;
                    var startTicks = launch.Process.StartTime.ToUniversalTime().Ticks;
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_launchReservation, reservation) ||
                            !IsCurrentSessionLeaseLocked(reservation.SessionLease) ||
                            _sessionClosing != 0 || _safeDegraded != 0 ||
                            Volatile.Read(ref _transportFailClosed) != 0)
                        {
                            stale = true;
                        }
                        else if (_identity.HasAuthority || _pending != null)
                        {
                            bindFailure = new IOException("Sidecar pending reservation在启动期间已被其他权威占用。");
                        }
                        else if (!_identity.TryReservePending(
                            launchProcessId,
                            startTicks,
                            options.SessionId,
                            options.SessionGeneration,
                            reservation.InstanceNonce,
                            out _,
                            out var rejection))
                        {
                            bindFailure = new IOException("Sidecar pending reservation被拒绝：" + rejection);
                        }
                        else
                        {
                            bound = new PendingHelper(
                                launch,
                                launchProcessId,
                                startTicks,
                                options.SessionId,
                                options.SessionGeneration,
                                reservation.InstanceNonce);
                            _pending = bound;
                            launch = null;
                        }
                        if (ReferenceEquals(_launchReservation, reservation))
                        {
                            _launchReservation = null;
                            _launchGate = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    bindFailure = ex;
                    lock (_gate)
                    {
                        if (ReferenceEquals(_launchReservation, reservation))
                        {
                            _launchReservation = null;
                            _launchGate = 0;
                        }
                    }
                }

                if (stale || bindFailure != null)
                {
                    DisposeLaunch(launch, true);
                    if (stale)
                    {
                        reservation.Fail(new WatchdogConnectException(
                            WatchdogConnectFailureKind.Cancelled,
                            "Sidecar launch reservation已失效，启动结果未提交。"));
                        return;
                    }
                    reservation.Fail(bindFailure);
                    throw bindFailure;
                }

                WatchdogClientTransportEvent pendingPublishedEvent;
                WatchdogClientTransportCallbacks launchCallbacks;
                lock (_gate)
                {
                    launchCallbacks = _callbacks;
                    pendingPublishedEvent = ReserveStateChangedLocked(
                        "PendingPublished",
                        "PID=" + bound.ProcessId + ";Nonce=" + reservation.InstanceNonce);
                }
                try
                {
                    launchCallbacks?.RecordEvent?.Invoke(
                        "SidecarHelperLaunched",
                        "PID=" + bound.ProcessId + ";Nonce=" + reservation.InstanceNonce);
                }
                catch { }
                EnqueueStateChanged(pendingPublishedEvent);
            }
            catch (Exception ex)
            {
                reservation.Fail(ex);
                throw;
            }
            finally
            {
                reservation.Complete();
            }
        }

        private static async Task AwaitLaunchCompletionAsync(
            Task<LaunchReservation.Outcome> completion)
        {
            var outcome = await completion.ConfigureAwait(false);
            if (outcome != null &&
                outcome.Kind == LaunchReservation.OutcomeKind.Succeeded)
                return;
            throw outcome?.Error ?? new WatchdogConnectException(
                WatchdogConnectFailureKind.LaunchFailure,
                "Sidecar launch transaction ended without a successful outcome.");
        }

        private async Task ReaderLoopAsync(
            CancellationToken token,
            StreamReader reader,
            string sessionId,
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            var unexpectedExit = false;
            var unexpectedDetail = string.Empty;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        unexpectedExit = true;
                        unexpectedDetail = "Reader返回EOF。";
                        break;
                    }
                    if (!WatchdogWireFrame.TryDecode(
                            line,
                            out line,
                            out var frameFailure))
                    {
                        var frameError = new WatchdogConnectException(
                            WatchdogConnectFailureKind.ProtocolRejected,
                            "Watchdog传输帧不完整或校验失败：" + frameFailure);
                        var callbacks = CaptureConnectionCallbacks(
                            connectionGeneration,
                            sessionGeneration,
                            sessionLease,
                            connectionIdentity);
                        try { callbacks?.RecordEvent?.Invoke(
                            "TransportInterruptedPartialFrame", frameError.Message); } catch { }
                        FailAttached(
                            frameError,
                            connectionGeneration,
                            sessionGeneration,
                            sessionLease,
                            connectionIdentity);
                        EnterTransportFailClosed(
                            sessionLease,
                            frameError.Kind,
                            frameError.Message);
                        return;
                    }
                    var connectionCallbacks = CaptureConnectionCallbacks(
                        connectionGeneration,
                        sessionGeneration,
                        sessionLease,
                        connectionIdentity);
                    if (connectionCallbacks == null) break;
                    WatchdogMessage message;
                    if (WatchdogProtocol.TryPeekWireMessageType(line, out var wireType) &&
                        string.Equals(wireType,
                            WatchdogMessageType.RecoveryAttemptFailedReceipt,
                            StringComparison.Ordinal))
                    {
                        if (!WatchdogProtocol.TryParseRecoveryFailureReceiptWire(
                                line,
                                out message,
                                out _,
                                out var receiptParseFailure))
                        {
                            var receiptError = new WatchdogConnectException(
                                WatchdogConnectFailureKind.ProtocolRejected,
                                "Watchdog恢复失败回执解析失败：" + receiptParseFailure);
                            try { connectionCallbacks.RecordEvent?.Invoke(
                                "RecoveryFailureReceiptRejected", receiptError.Message); } catch { }
                            EnterTransportFailClosed(
                                sessionLease,
                                receiptError.Kind,
                                receiptError.Message);
                            return;
                        }
                    }
                    else try { message = WatchdogProtocol.Deserialize(line); }
                    catch (Exception ex)
                    {
                        var parseFailure = new WatchdogConnectException(
                            WatchdogConnectFailureKind.ProtocolRejected,
                            "Watchdog消息解析失败：" + ex.GetBaseException().Message,
                            ex);
                        try { connectionCallbacks.RecordEvent?.Invoke("ProtocolRejected", parseFailure.Message); } catch { }
                        FailAttached(parseFailure, connectionGeneration, sessionGeneration, sessionLease, connectionIdentity);
                        EnterTransportFailClosed(sessionLease, parseFailure.Kind, parseFailure.Message);
                        return;
                    }
                    if (message == null) continue;
                    if (!WatchdogProtocol.IsSupportedVersion(message.ProtocolVersion))
                    {
                        var error = "拒绝不支持的Watchdog协议版本：" + message.ProtocolVersion + "，当前仅接受" + WatchdogProtocol.Version + "。";
                        try { connectionCallbacks.RecordEvent?.Invoke("ProtocolVersionRejected", error); } catch { }
                        TaskCompletionSource<bool> completion = null;
                        lock (_gate)
                        {
                            if (IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity)) completion = _attached;
                        }
                        completion?.TrySetException(new WatchdogConnectException(
                            WatchdogConnectFailureKind.ProtocolRejected,
                            error));
                        EnterTransportFailClosed(
                            sessionLease,
                            WatchdogConnectFailureKind.ProtocolRejected,
                            error);
                        return;
                    }
                    // Attached carries three independent Session aliases and
                    // must be classified by ValidateAttachedIdentity as an
                    // identity-contract failure.  Other Host messages remain
                    // session-scoped and a mismatched SessionId revokes the
                    // current transport.
                    if (message.Type != WatchdogMessageType.Attached &&
                        !string.Equals(message.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        var revoked = new WatchdogConnectException(
                            WatchdogConnectFailureKind.SessionRevoked,
                            "Watchdog消息SessionId不匹配当前Session。");
                        try { connectionCallbacks.RecordEvent?.Invoke("SessionRevoked", revoked.Message); } catch { }
                        FailAttached(revoked, connectionGeneration, sessionGeneration, sessionLease, connectionIdentity);
                        EnterTransportFailClosed(sessionLease, revoked.Kind, revoked.Message);
                        return;
                    }
                    if (message.Type == WatchdogMessageType.Attached)
                    {
                        if (!ValidateAttachedIdentity(message, sessionId, connectionGeneration, sessionGeneration,
                                sessionLease, connectionIdentity, out var pid, out var start, out var authoritySession, out var nonce,
                                out var failureKind, out var error))
                        {
                            try { connectionCallbacks.RecordEvent?.Invoke("AttachedRejected", error); } catch { }
                            var attachedFailure = new WatchdogConnectException(failureKind, error);
                            FailAttached(attachedFailure,
                                connectionGeneration, sessionGeneration, sessionLease, connectionIdentity);
                            EnterTransportFailClosed(sessionLease, failureKind, error);
                            return;
                        }
                        TaskCompletionSource<bool> completion = null;
                        PendingHelper promoted = null;
                        WatchdogClientTransportEvent promotedEvent = null;
                        WatchdogClientTransportEvent attachedEvent = null;
                        WatchdogClientTransportCallbacks attachedCallbacks = null;
                        string rejection = null;
                        var rejectionKind = WatchdogConnectFailureKind.HandshakeRejected;
                        lock (_gate)
                        {
                            if (!IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity)) return;
                            if (_attachedConnectionGeneration == connectionGeneration)
                            {
                                rejection = "同一管道连接重复Attached。";
                            }
                            else if (!_identity.TryPromoteAttached(
                                    new SidecarIdentityStateMachine.AuthoritySnapshot(pid, start, authoritySession, nonce),
                                    sessionId,
                                    sessionGeneration,
                                    out var promotionError))
                            {
                                rejection = promotionError;
                            }
                            if (rejection == null)
                            {
                                _attachedConnectionGeneration = connectionGeneration;
                                if (_pending != null &&
                                    _pending.ProcessId == pid &&
                                    _pending.StartUtcTicks == start &&
                                    string.Equals(_pending.SessionId, sessionId, StringComparison.Ordinal) &&
                                    string.Equals(_pending.InstanceNonce, nonce, StringComparison.Ordinal))
                                {
                                    promoted = _pending;
                                    _pending = null;
                                }
                                completion = _attached;
                                attachedCallbacks = _callbacks;
                                promotedEvent = ReserveStateChangedLocked(
                                    "AuthorityPromoted",
                                    "PID=" + pid + ";StartUtcTicks=" + start);
                                attachedEvent = ReserveStateChangedLocked(
                                    "Attached",
                                    "HandshakeCompleted;AuthorityPid=" + pid);
                            }
                        }
                        if (rejection != null)
                        {
                            try { connectionCallbacks.RecordEvent?.Invoke("AttachedRejected", rejection); } catch { }
                            var attachedFailure = new WatchdogConnectException(rejectionKind, rejection);
                            FailAttached(attachedFailure,
                                connectionGeneration, sessionGeneration, sessionLease, connectionIdentity);
                            EnterTransportFailClosed(sessionLease, rejectionKind, rejection);
                            return;
                        }
                        DisposePending(promoted, false);
                        completion?.TrySetResult(true);
                        EnqueueStateChanged(promotedEvent);
                        try
                        {
                            attachedCallbacks?.RecordEvent?.Invoke(
                                "Attached",
                                "HandshakeCompleted;AuthorityPid=" + pid + ";AuthorityStartUtcTicks=" + start);
                        }
                        catch { }
                        EnqueueStateChanged(attachedEvent);
                    }
                    else if (message.Type == WatchdogMessageType.HeartbeatAck)
                    {
                        TryCommitHeartbeatAck(
                            message.AckSequence,
                            sessionLease,
                            connectionGeneration,
                            connectionIdentity);
                    }
                    else if (message.Type == WatchdogMessageType.Ping)
                    {
                        var pingTaskIdentity = new object();
                        var pingAccepted = false;
                        lock (_gate)
                        {
                            if (IsCurrentConnectionLocked(
                                    connectionGeneration,
                                    sessionGeneration,
                                    sessionLease,
                                    connectionIdentity) &&
                                _pingResponseScheduled == 0)
                            {
                                _pingResponseScheduled = 1;
                                _pingResponseTaskIdentity = pingTaskIdentity;
                                pingAccepted = true;
                            }
                        }
                        if (pingAccepted)
                        {
                            var pingReason = message.Reason ?? "PingRefresh";
                            _ = Task.Run(() =>
                            {
                                try
                                {
                                    if (!IsExpectedConnectionCurrent(sessionLease, connectionGeneration, connectionIdentity)) return;
                                    SendHeartbeatSnapshot(
                                        pingReason,
                                        sessionLease,
                                        connectionGeneration,
                                        connectionIdentity);
                                    if (!IsExpectedConnectionCurrent(sessionLease, connectionGeneration, connectionIdentity)) return;
                                    SendForConnection(
                                        new WatchdogMessage
                                        {
                                            Type = WatchdogMessageType.Pong,
                                            SessionId = sessionId,
                                            Reason = "Alive"
                                        },
                                        sessionLease,
                                        connectionGeneration,
                                        connectionIdentity);
                                }
                                catch (Exception ex)
                                {
                                    if (IsExpectedConnectionCurrent(sessionLease, connectionGeneration, connectionIdentity))
                                        RaiseErrorWithCallbacks(
                                            connectionCallbacks,
                                            "PingResponse",
                                            ex.GetBaseException().Message);
                                }
                                finally
                                {
                                    lock (_gate)
                                    {
                                        if (_activeSessionLease == sessionLease &&
                                            _activeConnectionGeneration == connectionGeneration &&
                                            ReferenceEquals(_activeConnectionIdentity, connectionIdentity) &&
                                            ReferenceEquals(_pingResponseTaskIdentity, pingTaskIdentity))
                                        {
                                            _pingResponseScheduled = 0;
                                            _pingResponseTaskIdentity = null;
                                        }
                                    }
                                }
                            });
                        }
                    }
                    else if (message.Type == WatchdogMessageType.RequestStopAll)
                    {
                        try { connectionCallbacks.StopAllRequested?.Invoke(message.Reason, message.CorrelationId); }
                        catch (Exception ex)
                        {
                            RaiseErrorWithCallbacks(
                                connectionCallbacks,
                                "StopAllRequestedHandler",
                                ex.GetBaseException().Message);
                        }
                    }
                    else if (message.Type == WatchdogMessageType.RecoveryAttemptFailedReceipt)
                    {
                        try
                        {
                            connectionCallbacks.RecoveryFailureReceiptReceived?.Invoke(message);
                        }
                        catch (Exception ex)
                        {
                            RaiseErrorWithCallbacks(
                                connectionCallbacks,
                                "RecoveryFailureReceiptHandler",
                                ex.GetBaseException().Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                unexpectedExit = true;
                unexpectedDetail = ex.GetBaseException().Message;
                TaskCompletionSource<bool> completion = null;
                lock (_gate)
                {
                    if (IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease, connectionIdentity))
                        completion = _attached;
                }
                completion?.TrySetException(ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (_activeConnectionGeneration == connectionGeneration &&
                        _activeSessionLease == sessionLease &&
                        ReferenceEquals(_activeConnectionIdentity, connectionIdentity) &&
                        ReferenceEquals(_readerTaskIdentity, connectionIdentity))
                    {
                        _readerTask = null;
                        _readerTaskIdentity = null;
                    }
                }
                if (unexpectedExit && !token.IsCancellationRequested)
                    HandleUnexpectedConnectionLoss(
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity,
                        "ReaderLoopClosed",
                        unexpectedDetail);
            }
        }

        private async Task HeartbeatLoopAsync(
            CancellationToken token,
            long connectionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            var unexpectedExit = false;
            var unexpectedDetail = string.Empty;
            try
            {
                while (!token.IsCancellationRequested && !IsClosing)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;
                    if (!IsExpectedConnectionCurrent(sessionLease, connectionGeneration, connectionIdentity)) break;
                    var heartbeatDisposition = SendHeartbeatSnapshotWithDisposition(
                        "Periodic",
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity);
                    if (heartbeatDisposition == HeartbeatSendDisposition.ScopeStale)
                        break;
                    if (heartbeatDisposition == HeartbeatSendDisposition.AdmissionBusy ||
                        heartbeatDisposition == HeartbeatSendDisposition.CaptureFailed)
                        continue;
                    if (heartbeatDisposition == HeartbeatSendDisposition.TransportFailed)
                    {
                        unexpectedExit = true;
                        unexpectedDetail = "周期Heartbeat发送失败。";
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                unexpectedExit = true;
                unexpectedDetail = ex.GetBaseException().Message;
            }
            finally
            {
                lock (_gate)
                {
                    if (_activeConnectionGeneration == connectionGeneration &&
                        _activeSessionLease == sessionLease &&
                        ReferenceEquals(_activeConnectionIdentity, connectionIdentity) &&
                        ReferenceEquals(_heartbeatTaskIdentity, connectionIdentity))
                    {
                        _heartbeatTask = null;
                        _heartbeatTaskIdentity = null;
                    }
                }
                if (unexpectedExit && !token.IsCancellationRequested)
                    HandleUnexpectedConnectionLoss(
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity,
                        "HeartbeatLoop",
                        unexpectedDetail);
            }
        }

        private bool TryCommitHeartbeatAck(
            long ackSequence,
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity)
        {
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(
                        connectionGeneration,
                        _activeSessionGeneration,
                        sessionLease,
                        connectionIdentity) ||
                    ackSequence <= _lastHeartbeatAckSequence)
                    return false;
                _lastHeartbeatAckSequence = ackSequence;
                _lastHeartbeatAckUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            }
        }

        private bool SendHeartbeatSnapshot(
            string trigger,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            return SendHeartbeatSnapshotWithDisposition(
                    trigger,
                    expectedSessionLease,
                    expectedConnectionGeneration,
                    expectedConnectionIdentity) == HeartbeatSendDisposition.Sent;
        }

        private HeartbeatSendDisposition SendHeartbeatSnapshotWithDisposition(
            string trigger,
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            lock (_heartbeatCaptureGate)
            {
                var guarded = expectedSessionLease > 0 || expectedConnectionGeneration > 0 || expectedConnectionIdentity != null;
                WatchdogClientTransportCallbacks callbacks;
                WatchdogClientTransportOptions options;
                string sessionId;
                lock (_gate)
                {
                    if (guarded && !IsCurrentConnectionLocked(
                            expectedConnectionGeneration,
                            _activeSessionGeneration,
                            expectedSessionLease,
                            expectedConnectionIdentity))
                        return HeartbeatSendDisposition.ScopeStale;
                    callbacks = _callbacks;
                    options = _options;
                    sessionId = _options?.SessionId;
                }
                if (callbacks == null || options == null || string.IsNullOrWhiteSpace(sessionId))
                    return HeartbeatSendDisposition.CaptureFailed;

                WatchdogHeartbeat heartbeat = null;
                Exception providerError = null;
                try { heartbeat = callbacks.CaptureHeartbeat(); }
                catch (Exception ex) { providerError = ex; }
                if (heartbeat == null && providerError == null)
                    providerError = new InvalidOperationException("CaptureHeartbeat回调返回空值。");
                if (providerError != null)
                {
                    if (!guarded || IsExpectedConnectionCurrent(
                            expectedSessionLease,
                            expectedConnectionGeneration,
                            expectedConnectionIdentity))
                        try { callbacks.TransportError?.Invoke("HeartbeatProvider", providerError.GetBaseException().Message); } catch { }
                    return HeartbeatSendDisposition.CaptureFailed;
                }

                int processId;
                long processStartTicks;
                try
                {
                    using (var process = Process.GetCurrentProcess())
                    {
                        processId = process.Id;
                        processStartTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                }
                catch (Exception ex)
                {
                    try { callbacks.TransportError?.Invoke("HeartbeatProvider", ex.GetBaseException().Message); } catch { }
                    return HeartbeatSendDisposition.CaptureFailed;
                }

                lock (_gate)
                {
                    if (guarded && !IsCurrentConnectionLocked(
                            expectedConnectionGeneration,
                            _activeSessionGeneration,
                            expectedSessionLease,
                            expectedConnectionIdentity))
                        return HeartbeatSendDisposition.ScopeStale;
                    heartbeat.Sequence = ++_heartbeatSequence;
                    heartbeat.SessionId = sessionId;
                    heartbeat.ProcessId = processId;
                    heartbeat.ProcessStartUtcTicks = processStartTicks;
                }
                if (string.IsNullOrWhiteSpace(heartbeat.RecoveryProcessSource))
                    heartbeat.RecoveryProcessSource = options.RecoveryProcess
                        ? "RecoveryProcess"
                        : "InitialProcess";
                if (!string.Equals(trigger, "Periodic", StringComparison.Ordinal))
                {
                    var detail = "Trigger=" + trigger + ";Sequence=" + heartbeat.Sequence;
                    try { callbacks.RecordEvent?.Invoke("HeartbeatRefreshSent", detail); } catch { }
                }
                var sendDisposition = SendCoreWithDisposition(
                    new WatchdogMessage
                    {
                        Type = WatchdogMessageType.Heartbeat,
                        SessionId = sessionId,
                        Heartbeat = heartbeat
                    },
                    expectedSessionLease,
                    expectedConnectionGeneration,
                    expectedConnectionIdentity);
                switch (sendDisposition)
                {
                    case SendDisposition.Sent:
                        return HeartbeatSendDisposition.Sent;
                    case SendDisposition.ScopeStale:
                        return HeartbeatSendDisposition.ScopeStale;
                    case SendDisposition.AdmissionBusy:
                        return HeartbeatSendDisposition.AdmissionBusy;
                    default:
                        return HeartbeatSendDisposition.TransportFailed;
                }
            }
        }

        private void FailAttached(
            Exception error,
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            TaskCompletionSource<bool> completion = null;
            lock (_gate)
            {
                if (IsCurrentConnectionLocked(
                        connectionGeneration,
                        sessionGeneration,
                        sessionLease,
                        connectionIdentity))
                    completion = _attached;
            }
            completion?.TrySetException(error ?? new InvalidOperationException("Attached握手失败。"));
        }

        private bool ValidateAttachedIdentity(
            WatchdogMessage message,
            string expectedSession,
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity,
            out int pid,
            out long start,
            out string authoritySession,
            out string nonce,
            out WatchdogConnectFailureKind failureKind,
            out string rejection)
        {
            pid = message?.SidecarProcessId ?? 0;
            nonce = message?.SidecarInstanceNonce;
            authoritySession = message?.SidecarAuthoritySessionId;
            start = message?.SidecarProcessStartUtcTicks ?? 0;
            rejection = string.Empty;
            failureKind = WatchdogConnectFailureKind.IdentityRejected;
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(
                        connectionGeneration,
                        sessionGeneration,
                        sessionLease,
                        connectionIdentity))
                {
                    failureKind = WatchdogConnectFailureKind.SessionRevoked;
                    rejection = "Attached 所属连接代次已失效";
                    return false;
                }
            }
            if (message == null)
            {
                rejection = "Attached消息为空";
                return false;
            }
            var sessionAlias = message.SessionId;
            var authorityAlias = message.SidecarAuthoritySessionId;
            var sidecarAlias = message.SidecarSessionId;
            if (string.IsNullOrWhiteSpace(sessionAlias) ||
                string.IsNullOrWhiteSpace(authorityAlias) ||
                string.IsNullOrWhiteSpace(sidecarAlias))
            {
                rejection = "Attached缺少完整SessionId/SidecarAuthoritySessionId/SidecarSessionId。";
                return false;
            }
            if (!string.Equals(sessionAlias, authorityAlias, StringComparison.Ordinal) ||
                !string.Equals(sessionAlias, sidecarAlias, StringComparison.Ordinal) ||
                !string.Equals(sessionAlias, expectedSession, StringComparison.Ordinal))
            {
                rejection = "Attached SessionId/AuthoritySessionId 不匹配";
                return false;
            }
            authoritySession = authorityAlias;
            // Attached is a wire contract, not a best-effort compatibility
            // payload.  All aliases are required and must carry the exact
            // same identity.  Falling back to one populated alias would let
            // a stale/partial response promote a helper with unverifiable
            // provenance.
            if (message.SidecarProcessStartUtcTicks <= 0 ||
                message.SidecarStartUtcTicks <= 0 ||
                message.StartUtcTicks <= 0)
            {
                rejection = "Attached缺少完整Sidecar StartUtcTicks aliases";
                return false;
            }
            if (message.SidecarProcessStartUtcTicks != message.SidecarStartUtcTicks ||
                message.SidecarProcessStartUtcTicks != message.StartUtcTicks)
            {
                rejection = "Attached Sidecar StartUtcTicks 字段不一致";
                return false;
            }
            if (string.IsNullOrWhiteSpace(message.SidecarInstanceNonce) ||
                string.IsNullOrWhiteSpace(message.InstanceNonce))
            {
                rejection = "Attached缺少完整Sidecar nonce aliases";
                return false;
            }
            if (!string.Equals(message.SidecarInstanceNonce, message.InstanceNonce, StringComparison.Ordinal))
            {
                rejection = "Attached SidecarInstanceNonce/InstanceNonce 不一致";
                return false;
            }
            nonce = message.SidecarInstanceNonce;
            start = message.SidecarProcessStartUtcTicks;
            if (!WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                    expectedSession, authoritySession, pid, start, nonce) ||
                !WatchdogProcessIdentityPolicy.IsValidChallengeNonce(nonce))
            {
                rejection = "Attached 缺少有效 Sidecar PID/StartUtcTicks/InstanceNonce";
                return false;
            }
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited || !WatchdogProcessIdentityPolicy.Matches(
                            pid, start, process.Id, process.StartTime.ToUniversalTime().Ticks))
                    {
                        rejection = "Attached Sidecar PID/StartUtcTicks 身份不匹配";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                rejection = "Attached Sidecar PID不可验证：" + ex.GetBaseException().Message;
                return false;
            }
            lock (_gate)
            {
                if (!IsCurrentConnectionLocked(
                        connectionGeneration,
                        sessionGeneration,
                        sessionLease,
                        connectionIdentity))
                {
                    failureKind = WatchdogConnectFailureKind.SessionRevoked;
                    rejection = "Attached 所属连接代次已失效";
                    return false;
                }
                var accepted = _identity.CanAcceptAttached(
                    new SidecarIdentityStateMachine.AuthoritySnapshot(pid, start, authoritySession, nonce),
                    expectedSession,
                    sessionGeneration,
                    out rejection);
                if (!accepted) failureKind = WatchdogConnectFailureKind.IdentityRejected;
                return accepted;
            }
        }

        private async Task ReconnectOnceAsyncCore(
            long workerGeneration,
            long sessionLease,
            object reconnectTaskIdentity)
        {
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity))
                return;
            if (!CloseCurrentTransportForWorker(sessionLease, reconnectTaskIdentity)) return;
            WatchdogClientTransportOptions options;
            lock (_gate)
            {
                if (!IsCurrentReconnectWorkerLocked(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
                options = _options;
            }
            try
            {
                await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
                if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
                EmitReconnectSuccess(workerGeneration, sessionLease, reconnectTaskIdentity);
                return;
            }
            catch (WatchdogConnectException ex)
            {
                if (ex.Kind != WatchdogConnectFailureKind.PipeUnavailable) throw;
                var authorityAlive = IsAuthorityAlive();
                var pendingAlive = IsPendingAlive();
                if (options?.LaunchPolicy == WatchdogLaunchPolicy.AttachExistingAuthorityOnly &&
                    !authorityAlive)
                {
                    throw new WatchdogConnectException(
                        WatchdogConnectFailureKind.IdentityRejected,
                        "AttachExistingAuthorityOnly权威进程已退出或PID已复用；禁止启动新Sidecar。",
                        ex);
                }
                if (authorityAlive || pendingAlive)
                {
                    if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
                    RaiseErrorWithCallbacks(
                        CaptureReconnectCallbacks(sessionLease, reconnectTaskIdentity),
                        authorityAlive ? "ConnectFirstAuthorityAlive" : "ConnectFirstPendingHelper",
                        ex.GetBaseException().Message);
                    throw;
                }
                if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
                RaiseErrorWithCallbacks(
                    CaptureReconnectCallbacks(sessionLease, reconnectTaskIdentity),
                    "WatchdogPipeConnectFailed",
                    ex.GetBaseException().Message);
            }
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity) ||
                ObserveStopMarker(sessionLease) || IsClosing || IsSafeDegraded) return;
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
            if (options?.LaunchPolicy == WatchdogLaunchPolicy.AttachExistingAuthorityOnly)
            {
                // Recovery sessions may reconnect to the exact validated
                // authority, but they are never allowed to create a helper.
                // Returning PipeUnavailable lets the single supervisor apply
                // bounded backoff/exhaustion instead of launching.
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.PipeUnavailable,
                    "AttachExistingAuthorityOnly权威仍存活但管道暂不可用；不启动新Sidecar。");
            }
            try
            {
                await LaunchSidecarIfAuthorized(
                    sessionLease,
                    options.LaunchPolicy).ConfigureAwait(false);
            }
            catch (WatchdogConnectException) { throw; }
            catch (Exception ex)
            {
                throw new WatchdogConnectException(
                    WatchdogConnectFailureKind.LaunchFailure,
                    "Watchdog Sidecar启动失败：" + ex.GetBaseException().Message,
                    ex);
            }
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
            await ConnectAsync(options.SessionId, options.PipeName, sessionLease).ConfigureAwait(false);
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity)) return;
            EmitReconnectSuccess(workerGeneration, sessionLease, reconnectTaskIdentity);
        }

        private async Task<ReconnectAttemptDisposition> ReconnectOnceAsync(
            long workerGeneration,
            long sessionLease,
            object reconnectTaskIdentity)
        {
            if (!IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity) ||
                IsClosing || IsSafeDegraded)
                return ReconnectAttemptDisposition.Succeeded;
            try
            {
                await ReconnectOnceAsyncCore(workerGeneration, sessionLease, reconnectTaskIdentity).ConfigureAwait(false);
                return ReconnectAttemptDisposition.Succeeded;
            }
            catch (WatchdogConnectException ex) when (IsReconnectFailClosedKind(ex.Kind))
            {
                if (IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity))
                {
                    EnterTransportFailClosed(
                        sessionLease,
                        ex.Kind,
                        ex.GetBaseException().Message);
                }
                return ReconnectAttemptDisposition.FailClosed;
            }
            catch (WatchdogConnectException ex) when (ex.Kind == WatchdogConnectFailureKind.PipeUnavailable)
            {
                if (IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity))
                    RaiseErrorWithCallbacks(
                        CaptureReconnectCallbacks(sessionLease, reconnectTaskIdentity),
                        "WatchdogReconnectPipeUnavailable",
                        ex.GetBaseException().Message);
                return ReconnectAttemptDisposition.Retry;
            }
            catch (WatchdogConnectException ex) when (ex.Kind == WatchdogConnectFailureKind.Cancelled)
            {
                // Cancellation is an explicit session-termination boundary,
                // not a transport failure and must never be retried.
                return ReconnectAttemptDisposition.Succeeded;
            }
            catch (Exception ex)
            {
                if (IsCurrentReconnectWorker(workerGeneration, sessionLease, reconnectTaskIdentity))
                    RaiseErrorWithCallbacks(
                        CaptureReconnectCallbacks(sessionLease, reconnectTaskIdentity),
                        "WatchdogReconnectFailed",
                        ex.GetBaseException().Message);
                return ReconnectAttemptDisposition.Retry;
            }
        }

        private static bool IsReconnectFailClosedKind(WatchdogConnectFailureKind kind)
        {
            return kind == WatchdogConnectFailureKind.ProtocolRejected ||
                   kind == WatchdogConnectFailureKind.IdentityRejected ||
                   kind == WatchdogConnectFailureKind.HandshakeRejected ||
                   kind == WatchdogConnectFailureKind.SessionRevoked ||
                   kind == WatchdogConnectFailureKind.InvalidConfiguration;
        }

        private bool IsCurrentReconnectWorkerLocked(
            long workerGeneration,
            long sessionLease,
            object taskIdentity)
        {
            return _reconnectGeneration == workerGeneration &&
                   _activeSessionLease == sessionLease &&
                   ReferenceEquals(_reconnectTaskIdentity, taskIdentity);
        }

        private bool IsCurrentReconnectWorker(
            long workerGeneration,
            long sessionLease,
            object taskIdentity)
        {
            lock (_gate)
            {
                return IsCurrentReconnectWorkerLocked(workerGeneration, sessionLease, taskIdentity);
            }
        }

        private bool CloseCurrentTransportForWorker(long sessionLease, object taskIdentity)
        {
            long connectionGeneration;
            object connectionIdentity;
            lock (_gate)
            {
                if (_activeSessionLease != sessionLease ||
                    !ReferenceEquals(_reconnectTaskIdentity, taskIdentity))
                    return false;
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
            }
            if (connectionGeneration <= 0 || connectionIdentity == null) return true;
            return CloseTransportOnly(sessionLease, connectionGeneration, connectionIdentity);
        }

        private void EmitReconnectSuccess(
            long workerGeneration,
            long sessionLease,
            object taskIdentity)
        {
            WatchdogClientTransportCallbacks callbacks;
            WatchdogClientTransportEvent reconnectedEvent;
            long connectionGeneration;
            object connectionIdentity;
            lock (_gate)
            {
                if (!IsCurrentReconnectWorkerLocked(workerGeneration, sessionLease, taskIdentity)) return;
                _transportLostReported = 0;
                _reconnectAttempt = 0;
                // Reconnect success is the terminal ownership boundary, not
                // merely a progress event.  Retire the exact supervisor under
                // the same gate that publishes the reconnected snapshot so
                // observers can never see a live connection with a stale
                // recovery owner that is only waiting for an async finally.
                _reconnectTask = null;
                _reconnectTaskIdentity = null;
                callbacks = _callbacks;
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
                reconnectedEvent = ReserveStateChangedLocked(
                    "Reconnected",
                    "Generation=" + workerGeneration.ToString(CultureInfo.InvariantCulture));
            }
            try { callbacks?.RecordEvent?.Invoke("TransportEvent", "WatchdogReconnected:同一Session重连成功。"); } catch { }
            try { callbacks?.TransportError?.Invoke("WatchdogReconnected", "同一Session重连成功。"); } catch { }
            EnqueueStateChanged(reconnectedEvent);
            if (SendHeartbeatSnapshot(
                    "ImmediateStateSnapshot",
                    sessionLease,
                    connectionGeneration,
                    connectionIdentity))
            {
                WatchdogClientTransportEvent snapshotEvent;
                lock (_gate)
                    snapshotEvent = ReserveStateChangedLocked(
                        "ImmediateStateSnapshot",
                        "ConnectionGeneration=" + connectionGeneration.ToString(CultureInfo.InvariantCulture));
                try { callbacks?.RecordEvent?.Invoke(
                    "ImmediateStateSnapshot", "ReconnectSuccess"); } catch { }
                EnqueueStateChanged(snapshotEvent);
            }
        }

        private bool SetReconnectAttemptIfCurrent(
            long workerGeneration,
            long sessionLease,
            object taskIdentity,
            int attempt)
        {
            lock (_gate)
            {
                if (_reconnectGeneration != workerGeneration ||
                    _activeSessionLease != sessionLease ||
                    !ReferenceEquals(_reconnectTaskIdentity, taskIdentity))
                    return false;
                _reconnectAttempt = attempt;
                return true;
            }
        }

        private async Task TransportMonitorAsync(
            CancellationToken token,
            long monitorGeneration,
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity,
            object taskIdentity)
        {
            try
            {
                while (!token.IsCancellationRequested && IsCurrentSessionLease(sessionLease))
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    if (!IsCurrentMonitorWorker(
                            monitorGeneration,
                            sessionLease,
                            connectionGeneration,
                            connectionIdentity,
                            taskIdentity))
                        break;
                    var ackTicks = Interlocked.Read(ref _lastHeartbeatAckUtcTicks);
                    var age = DateTime.UtcNow - new DateTime(ackTicks, DateTimeKind.Utc);
                    if (age.TotalMilliseconds < WatchdogTransportPolicy.ClientHeartbeatAckRetireMs)
                        continue;
                    var authorityAlive = IsAuthorityAlive();
                    if (!IsCurrentMonitorWorker(
                            monitorGeneration,
                            sessionLease,
                            connectionGeneration,
                            connectionIdentity,
                            taskIdentity))
                        break;
                    HandleUnexpectedConnectionLoss(
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity,
                        authorityAlive
                            ? "WatchdogHeartbeatAckTimeout"
                            : "WatchdogProcessExited",
                        "AckAgeMs=" + age.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) +
                        ";AuthorityAlive=" + authorityAlive,
                        taskIdentity);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsCurrentMonitorWorker(
                        monitorGeneration,
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity,
                        taskIdentity))
                    RaiseErrorWithCallbacks(
                        CaptureConnectionCallbacks(connectionGeneration, sessionLease, connectionIdentity),
                        "TransportMonitor",
                        ex.GetBaseException().Message);
            }
            finally
            {
                lock (_gate)
                {
                    if (_monitorGeneration == monitorGeneration &&
                        _activeSessionLease == sessionLease &&
                        _activeConnectionGeneration == connectionGeneration &&
                        ReferenceEquals(_activeConnectionIdentity, connectionIdentity) &&
                        ReferenceEquals(_monitorTaskIdentity, taskIdentity))
                    {
                        _monitorTask = null;
                        _monitorTaskIdentity = null;
                    }
                }
            }
        }

        private void StartTransportMonitor(
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity)
        {
            lock (_gate)
            {
                if (_sessionLifetime == null || _sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0 ||
                    (_monitorTask != null && !_monitorTask.IsCompleted) ||
                    !IsCurrentConnectionLocked(
                        connectionGeneration,
                        _activeSessionGeneration,
                        sessionLease,
                        connectionIdentity)) return;
                var generation = ++_monitorGeneration;
                var taskIdentity = new object();
                var sessionToken = _sessionLifetime.Token;
                var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _monitorTaskIdentity = taskIdentity;
                var task = Task.Run(async () =>
                {
                    await startGate.Task.ConfigureAwait(false);
                    await TransportMonitorAsync(
                        sessionToken,
                        generation,
                        sessionLease,
                        connectionGeneration,
                        connectionIdentity,
                        taskIdentity).ConfigureAwait(false);
                });
                _monitorTask = task;
                startGate.TrySetResult(true);
            }
        }

        private bool IsCurrentMonitorWorker(
            long monitorGeneration,
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity,
            object taskIdentity)
        {
            lock (_gate)
            {
                return _monitorGeneration == monitorGeneration &&
                       _activeSessionLease == sessionLease &&
                       _activeConnectionGeneration == connectionGeneration &&
                       ReferenceEquals(_activeConnectionIdentity, connectionIdentity) &&
                       ReferenceEquals(_monitorTaskIdentity, taskIdentity);
            }
        }

        private void EnterSafeDegraded(
            long workerGeneration,
            long sessionLease,
            object workerTaskIdentity)
        {
            PendingHelper pending = null;
            WatchdogClientTransportCallbacks callbacks = null;
            WatchdogClientTransportEvent pendingClearedEvent = null;
            WatchdogClientTransportEvent safeDegradedEvent = null;
            var detail = "同一Session重连超过" + WatchdogTransportPolicy.ReconnectMaxAttempts +
                         "次，已进入一次性安全降级；WorkerGeneration=" + workerGeneration;
            lock (_gate)
            {
                if (_activeSessionLease != sessionLease ||
                    !ReferenceEquals(_reconnectTaskIdentity, workerTaskIdentity) ||
                    _safeDegraded != 0)
                    return;
                callbacks = _callbacks;
                _safeDegraded = 1;
                _sessionClosing = 1;
                pending = _pending;
                _pending = null;
                if (pending != null)
                {
                    _identity.TryClearPending(ToPendingSnapshot(pending));
                    pendingClearedEvent = ReserveStateChangedLocked(
                        "PendingCleared",
                        "PID=" + pending.ProcessId + ";Reason=ReconnectExhausted");
                }
                safeDegradedEvent = ReserveStateChangedLocked("SafeDegraded", detail);
            }
            DisposePending(pending, true);
            if (pendingClearedEvent != null)
                EnqueueStateChanged(pendingClearedEvent);
            try { callbacks?.RecordEvent?.Invoke("TransportSafeDegraded", detail); } catch { }
            try
            {
                callbacks?.RecordEvent?.Invoke(
                    "TransportEvent",
                    "WatchdogReconnectExhaustedSafeIdle:" + detail);
            }
            catch { }
            try { callbacks?.TransportError?.Invoke("WatchdogReconnectExhaustedSafeIdle", detail); } catch { }
            if (IsCurrentReconnectWorker(workerGeneration, sessionLease, workerTaskIdentity))
                ReportTransportLostOnce(
                    "WatchdogReconnectExhaustedSafeIdle",
                    detail,
                    sessionLease,
                    0,
                    null,
                    workerTaskIdentity);
            EnqueueStateChanged(safeDegradedEvent);
        }

        private void EnterTransportFailClosed(
            long sessionLease,
            WatchdogConnectFailureKind failureKind,
            string detail)
        {
            NamedPipeClientStream pipe;
            StreamReader reader;
            StreamWriter writer;
            SendQueueOwner sendOwner;
            CancellationTokenSource lifetime;
            CancellationTokenSource sessionLifetime;
            Task readerTask;
            Task heartbeatTask;
            Task sendTask;
            Task monitorTask;
            Task reconnectTask;
            Task connectTask;
            ConnectReservation connectReservation;
            LaunchReservation launchReservation;
            Task launchTask;
            WatchdogClientTransportCallbacks callbacks;
            WatchdogClientTransportEvent failClosedEvent;
            lock (_gate)
            {
                if (!IsCurrentSessionLeaseLocked(sessionLease) ||
                    Volatile.Read(ref _transportFailClosed) != 0)
                    return;

                _transportFailClosed = 1;
                _transportFailureKind = failureKind;
                _transportFailureDetail = detail ?? string.Empty;
                _sessionClosing = 1;
                callbacks = _callbacks;

                pipe = _pipe;
                reader = _reader;
                writer = _writer;
                sendOwner = _sendQueueOwner;
                lifetime = _lifetime;
                sessionLifetime = _sessionLifetime;
                readerTask = _readerTask;
                heartbeatTask = _heartbeatTask;
                sendTask = _sendTask;
                monitorTask = _monitorTask;
                reconnectTask = _reconnectTask;
                connectTask = _connectTask;
                connectReservation = _connectReservation;
                launchReservation = _launchReservation ?? _retainedLaunchReservation;
                launchTask = _launchTask ?? _retainedLaunchTask;

                _pipe = null;
                _reader = null;
                _writer = null;
                _sendQueueOwner = null;
                _sendTask = null;
                _lifetime = null;
                _sessionLifetime = null;
                _attached = null;
                _readerTask = null;
                _readerTaskIdentity = null;
                _heartbeatTask = null;
                _heartbeatTaskIdentity = null;
                _monitorTask = null;
                _monitorTaskIdentity = null;
                _reconnectTask = null;
                _reconnectTaskIdentity = null;
                _activeConnectionGeneration = 0;
                _activeConnectionIdentity = null;
                _activeSessionGeneration = 0;
                _attachedConnectionGeneration = 0;
                _pingResponseScheduled = 0;
                _pingResponseTaskIdentity = null;
                _launchReservation = null;
                _launchGate = 0;
                _connectReservation = null;
                _connectTask = null;
                failClosedEvent = ReserveStateChangedLocked(
                    "TransportFailClosed",
                    failureKind + ":" + (_transportFailureDetail ?? string.Empty));
            }

            sendOwner?.StopAccepting(SendDisposition.ScopeStale);
            CancelNoDispose(lifetime);
            CancelNoDispose(sessionLifetime);
            TryDispose(pipe);
            TryDispose(reader);
            TryDispose(writer);
                    StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(monitorTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(reconnectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(connectTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
                    WaitTaskBounded(
                        connectReservation?.Completion.Task,
                        WatchdogTransportPolicy.ConnectFailureJoin1000);
            CancelAndJoinLaunch(
                launchReservation,
                launchTask,
                "LaunchClosureDeadlineExceeded:TransportFailClosed");
            TryDispose(lifetime);
            TryDispose(sessionLifetime);

            RaiseErrorWithCallbacks(callbacks, "WatchdogTransportFailClosed", failureKind + ":" + detail);
            EnqueueStateChanged(failClosedEvent);
        }

        private void ThrowIfTransportFailClosed()
        {
            WatchdogConnectFailureKind kind;
            string detail;
            lock (_gate)
            {
                if (_transportFailClosed == 0) return;
                kind = _transportFailureKind;
                detail = _transportFailureDetail;
            }
            throw new WatchdogConnectException(
                kind,
                "Watchdog transport已进入sticky fail-closed：" + kind + ":" + detail);
        }

        private void ThrowIfLaunchClosureBlocked()
        {
            string detail;
            lock (_gate)
            {
                if (_launchClosureActive == 0) return;
                detail = _launchClosureDetail;
            }
            throw new WatchdogConnectException(
                WatchdogConnectFailureKind.LaunchFailure,
                "Watchdog Sidecar启动关闭尚未收口：LaunchClosureBlocked:" + detail);
        }

        private WatchdogClientTransportEvent ReleaseRetainedLaunchLocked(
            LaunchReservation expectedReservation,
            Task expectedTask,
            bool requireCompleted)
        {
            var retainedReservation = _retainedLaunchReservation;
            var retainedTask = _retainedLaunchTask;
            if (retainedReservation == null && retainedTask == null)
                return null;
            if (expectedReservation != null && retainedReservation != null &&
                !ReferenceEquals(expectedReservation, retainedReservation))
                return null;
            if (expectedTask != null && retainedTask != null &&
                !ReferenceEquals(expectedTask, retainedTask))
                return null;
            if (requireCompleted &&
                ((retainedTask != null && !retainedTask.IsCompleted) ||
                 (retainedReservation != null &&
                  !retainedReservation.Completion.Task.IsCompleted)))
                return null;

            _retainedLaunchReservation = null;
            _retainedLaunchTask = null;
            if (_launchClosureActive == 0)
                return null;
            _launchClosureActive = 0;
            return ReserveStateChangedLocked(
                "LaunchClosureReleased",
                "Launch资源已终结并释放；历史阻断标记保留。");
        }

        private void ReleaseRetainedLaunchOutside(
            LaunchReservation expectedReservation,
            Task expectedTask)
        {
            WatchdogClientTransportEvent releasedEvent;
            lock (_gate)
            {
                releasedEvent = ReleaseRetainedLaunchLocked(
                    expectedReservation,
                    expectedTask,
                    true);
                if (releasedEvent != null &&
                    (expectedTask == null || ReferenceEquals(_launchTask, expectedTask)))
                {
                    _launchTask = null;
                    _launchTaskSessionLease = 0;
                }
            }
            if (releasedEvent != null)
                EnqueueStateChanged(releasedEvent);
        }

        private void CompleteLaunchTask(
            LaunchReservation reservation,
            Task launchTask)
        {
            WatchdogClientTransportEvent releasedEvent = null;
            lock (_gate)
            {
                if (ReferenceEquals(_launchTask, launchTask))
                {
                    _launchTask = null;
                    _launchTaskSessionLease = 0;
                }
                if (ReferenceEquals(_launchReservation, reservation))
                {
                    _launchReservation = null;
                    _launchGate = 0;
                }
                releasedEvent = ReleaseRetainedLaunchLocked(
                    reservation,
                    launchTask,
                    false);
            }
            if (releasedEvent != null)
                EnqueueStateChanged(releasedEvent);
        }

        private void CancelAndJoinLaunch(
            LaunchReservation reservation,
            Task launchTask,
            string reason)
        {
            reservation?.Cancel();
            bool completed;
            lock (_gate)
            {
                completed = IsLaunchClosureCompleteLocked(launchTask, reservation);
            }
            if (!completed)
            {
                WaitTaskBounded(launchTask, WatchdogTransportPolicy.LaunchClosureJoin1000);
                WaitTaskBounded(
                    reservation?.Completion?.Task,
                    WatchdogTransportPolicy.LaunchClosureJoin1000);
                lock (_gate)
                {
                    completed = IsLaunchClosureCompleteLocked(launchTask, reservation);
                }
            }

            WatchdogClientTransportEvent lifecycleEvent = null;
            bool registerContinuation = false;
            lock (_gate)
            {
                if (completed)
                {
                    lifecycleEvent = ReleaseRetainedLaunchLocked(
                        reservation,
                        launchTask,
                        true);
                    if (lifecycleEvent == null && _launchClosureActive != 0 &&
                        _retainedLaunchReservation == null &&
                        _retainedLaunchTask == null)
                    {
                        _launchClosureActive = 0;
                        lifecycleEvent = ReserveStateChangedLocked(
                            "LaunchClosureReleased",
                            "Launch资源已终结并释放；历史阻断标记保留。");
                    }
                    if (launchTask != null && ReferenceEquals(_launchTask, launchTask))
                    {
                        _launchTask = null;
                        _launchTaskSessionLease = 0;
                    }
                }
                else
                {
                    var wasAlreadyBlocked = _launchClosureActive != 0;
                    _launchClosureBlocked = 1;
                    _launchClosureActive = 1;
                    if (string.IsNullOrEmpty(_launchClosureDetail))
                        _launchClosureDetail = reason ?? "LaunchClosureDeadlineExceeded";
                    _retainedLaunchReservation = reservation;
                    _retainedLaunchTask = launchTask;
                    registerContinuation = true;
                    if (!wasAlreadyBlocked)
                        lifecycleEvent = ReserveStateChangedLocked(
                            "LaunchClosureBlocked",
                            _launchClosureDetail);
                }
            }
            if (lifecycleEvent != null)
                EnqueueStateChanged(lifecycleEvent);

            if (completed)
            {
                TryDispose(reservation?.Lifetime);
                return;
            }

            // The timeout is sticky, but completion remains deterministic:
            // whichever task represents the launch transaction will release
            // the retained owner exactly once after it finishes.
            var completionTask = launchTask ?? reservation?.Completion?.Task;
            if (registerContinuation && completionTask != null)
            {
                try
                {
                    completionTask.ContinueWith(
                        _ => ReleaseRetainedLaunchOutside(reservation, launchTask),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                catch { }
            }
        }

        private bool IsLaunchClosureCompleteLocked(
            Task launchTask,
            LaunchReservation reservation)
        {
            return (launchTask == null || launchTask.IsCompleted) &&
                   (reservation == null || reservation.Completion.Task.IsCompleted);
        }

        private static bool IsTransportFailClosedKind(WatchdogConnectFailureKind kind)
        {
            return kind == WatchdogConnectFailureKind.ProtocolRejected ||
                   kind == WatchdogConnectFailureKind.IdentityRejected ||
                   kind == WatchdogConnectFailureKind.HandshakeRejected ||
                   kind == WatchdogConnectFailureKind.SessionRevoked ||
                   kind == WatchdogConnectFailureKind.InvalidConfiguration;
        }

        private bool CloseCurrentTransport(long expectedSessionLease)
        {
            long connectionGeneration;
            object connectionIdentity;
            lock (_gate)
            {
                if (expectedSessionLease <= 0 ||
                    !IsCurrentSessionLeaseLocked(expectedSessionLease) ||
                    _activeConnectionGeneration <= 0 ||
                    _activeConnectionIdentity == null)
                    return false;
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
            }
            return CloseTransportOnly(expectedSessionLease, connectionGeneration, connectionIdentity);
        }

        private bool CloseTransportOnly(
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity,
            bool fromSendWorker = false)
        {
            NamedPipeClientStream pipe;
            StreamReader reader;
            StreamWriter writer;
            SendQueueOwner sendOwner;
            CancellationTokenSource lifetime;
            Task readerTask;
            Task heartbeatTask;
            lock (_gate)
            {
                if (expectedSessionLease <= 0 || expectedConnectionGeneration <= 0 || expectedConnectionIdentity == null ||
                    _activeSessionLease != expectedSessionLease ||
                    _activeConnectionGeneration != expectedConnectionGeneration ||
                    !ReferenceEquals(_activeConnectionIdentity, expectedConnectionIdentity))
                    return false;
                pipe = _pipe;
                reader = _reader;
                writer = _writer;
                sendOwner = _sendQueueOwner;
                lifetime = _lifetime;
                readerTask = _readerTask;
                heartbeatTask = _heartbeatTask;
                _pipe = null;
                _reader = null;
                _writer = null;
                _sendQueueOwner = null;
                _sendTask = null;
                _lifetime = null;
                _attached = null;
                _pingResponseScheduled = 0;
                _pingResponseTaskIdentity = null;
                _readerTask = null;
                _readerTaskIdentity = null;
                _heartbeatTask = null;
                _heartbeatTaskIdentity = null;
                _activeConnectionGeneration = 0;
                _activeConnectionIdentity = null;
                _activeSessionGeneration = 0;
                _attachedConnectionGeneration = 0;
            }
            sendOwner?.StopAccepting(SendDisposition.ScopeStale);
            CancelNoDispose(lifetime);
            TryDispose(pipe);
            TryDispose(reader);
            TryDispose(writer);
            if (!fromSendWorker)
                StopAndJoinSendOwner(sendOwner, WatchdogTransportPolicy.ConnectFailureJoin1000);
            WaitTaskBounded(readerTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
            WaitTaskBounded(heartbeatTask, WatchdogTransportPolicy.ConnectFailureJoin1000);
            TryDispose(lifetime);
            return true;
        }

        private void BreakTransport(
            StreamWriter expectedWriter,
            long expectedSessionLease = 0,
            long expectedConnectionGeneration = 0,
            object expectedConnectionIdentity = null,
            object expectedWorkerIdentity = null,
            string reason = "TransportWriteFailed",
            string detail = "命名管道写入失败。",
            bool fromSendWorker = false)
        {
            long sessionLease;
            long connectionGeneration;
            object connectionIdentity;
            TransportBreakScope breakScope;
            lock (_gate)
            {
                if (!ReferenceEquals(_writer, expectedWriter) ||
                    _activeSessionLease <= 0 ||
                    _activeConnectionGeneration <= 0 ||
                    _activeConnectionIdentity == null ||
                    (expectedSessionLease > 0 &&
                     !IsCurrentConnectionLocked(
                         expectedConnectionGeneration,
                         _activeSessionGeneration,
                         expectedSessionLease,
                         expectedConnectionIdentity)))
                    return;
                sessionLease = _activeSessionLease;
                connectionGeneration = _activeConnectionGeneration;
                connectionIdentity = _activeConnectionIdentity;
                breakScope = new TransportBreakScope(
                    fromSendWorker ? TransportBreakOrigin.Send : TransportBreakOrigin.Reader,
                    sessionLease,
                    connectionGeneration,
                    connectionIdentity);
            }
            if (CloseTransportOnly(
                    breakScope.SessionLease,
                    breakScope.ConnectionGeneration,
                    breakScope.ConnectionIdentity,
                    fromSendWorker))
            {
                // The exact connection CAS above is the authorization boundary.
                // A send owner is not a monitor/reconnect worker identity and must
                // never be compared with those worker slots after detachment.
                ReportTransportLostOnce(
                    reason,
                    detail,
                    breakScope.SessionLease,
                    0,
                    null,
                    null);
                TryScheduleReconnect(
                    WatchdogTransportPolicy.ReconnectInitial250,
                    breakScope.SessionLease,
                    breakScope.ConnectionGeneration,
                    breakScope.ConnectionIdentity,
                    null,
                    true);
            }
        }

        private AuthorityLiveness ProbeAuthorityLiveness(
            long expectedSessionLease,
            out SidecarIdentityStateMachine.AuthoritySnapshot authority)
        {
            lock (_gate)
            {
                authority = _identity.Authority;
                if (_activeSessionLease != expectedSessionLease ||
                    expectedSessionLease <= 0 ||
                    authority == null ||
                    !string.Equals(authority.SessionId, _options?.SessionId, StringComparison.Ordinal))
                    return AuthorityLiveness.Missing;
            }

            if (IsProcessIdentityAlive(authority.ProcessId, authority.ProcessStartUtcTicks))
                return AuthorityLiveness.Alive;
            return IsProcessIdentityConfirmedDead(
                authority.ProcessId,
                authority.ProcessStartUtcTicks)
                ? AuthorityLiveness.ConfirmedDead
                : AuthorityLiveness.Unknown;
        }

        private void ClearAuthorityIfCurrent(
            long expectedSessionLease,
            SidecarIdentityStateMachine.AuthoritySnapshot authority,
            string reason)
        {
            if (authority == null) return;
            WatchdogClientTransportEvent clearedEvent = null;
            lock (_gate)
            {
                if (_activeSessionLease == expectedSessionLease &&
                    ReferenceEquals(_identity.Authority, authority))
                {
                    _identity.TryClearAuthority(authority);
                    clearedEvent = ReserveStateChangedLocked(
                        "AuthorityCleared",
                        "PID=" + authority.ProcessId + ";Reason=" + (reason ?? string.Empty));
                }
            }
            if (clearedEvent != null)
                EnqueueStateChanged(clearedEvent);
        }

        private bool IsAuthorityAlive()
        {
            SidecarIdentityStateMachine.AuthoritySnapshot authority;
            long sessionLease;
            string sessionId;
            lock (_gate)
            {
                authority = _identity.Authority;
                sessionLease = _activeSessionLease;
                sessionId = _options?.SessionId;
            }
            if (authority == null || sessionLease <= 0 || !string.Equals(authority.SessionId, sessionId, StringComparison.Ordinal)) return false;
            var liveness = ProbeAuthorityLiveness(sessionLease, out var probedAuthority);
            if (liveness == AuthorityLiveness.ConfirmedDead)
            {
                ClearAuthorityIfCurrent(
                    sessionLease,
                    probedAuthority ?? authority,
                    "ProcessExited");
                return false;
            }
            return liveness == AuthorityLiveness.Alive ||
                   liveness == AuthorityLiveness.Unknown;
        }

        /// <summary>
        /// The single connection-loss boundary for reader EOF/exception,
        /// heartbeat send failure, and monitor timeout.  Detachment is first
        /// performed by an exact connection CAS; only its winner publishes
        /// TransportLost and admits the one reconnect worker.  This keeps a
        /// late reader/heartbeat/monitor callback from closing or scheduling
        /// against a replacement connection.
        /// </summary>
        private void HandleUnexpectedConnectionLoss(
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity,
            string reason,
            string detail,
            object expectedWorkerIdentity = null)
        {
            if (sessionLease <= 0 || connectionGeneration <= 0 || connectionIdentity == null)
                return;
            if (!CloseTransportOnly(
                    sessionLease,
                    connectionGeneration,
                    connectionIdentity))
                return;

            // The exact connection has already been detached, so the
            // session lease (and optional worker identity) is the remaining
            // scope guard for the event.  A replacement session cannot
            // inherit this event because its lease differs.
            ReportTransportLostOnce(
                reason,
                detail,
                sessionLease,
                0,
                null,
                expectedWorkerIdentity);

            if (!IsCurrentSessionLease(sessionLease) ||
                IsClosing || IsSafeDegraded || IsTransportFailClosed)
                return;

            TryScheduleReconnect(
                WatchdogTransportPolicy.ReconnectInitial250,
                sessionLease,
                connectionGeneration,
                connectionIdentity,
                expectedWorkerIdentity,
                true);
        }

        private bool IsPendingAlive()
        {
            PendingHelper pending;
            long sessionLease;
            lock (_gate)
            {
                pending = _pending;
                sessionLease = _activeSessionLease;
            }
            if (pending == null || sessionLease <= 0) return false;
            if (IsProcessIdentityAlive(pending.ProcessId, pending.StartUtcTicks)) return true;
            if (!IsProcessIdentityConfirmedDead(pending.ProcessId, pending.StartUtcTicks)) return true;
            var cleared = false;
            WatchdogClientTransportEvent clearedEvent = null;
            lock (_gate)
            {
                if (_activeSessionLease != sessionLease || !ReferenceEquals(_pending, pending)) return false;
                _pending = null;
                _identity.TryClearPending(ToPendingSnapshot(pending));
                cleared = true;
                clearedEvent = ReserveStateChangedLocked(
                    "PendingCleared",
                    "PID=" + pending.ProcessId + ";Reason=ProcessExited");
            }
            DisposePending(pending, false);
            if (cleared)
                EnqueueStateChanged(clearedEvent);
            return false;
        }

        private bool ValidateAuthorityCandidate(
            SidecarIdentityStateMachine.AuthoritySnapshot authority,
            out string rejection)
        {
            rejection = string.Empty;
            if (!WatchdogProcessIdentityPolicy.HasAuthoritativeIdentity(
                    authority?.SessionId,
                    authority?.SessionId,
                    authority?.ProcessId ?? 0,
                    authority?.ProcessStartUtcTicks ?? 0,
                    authority?.InstanceNonce))
            {
                rejection = "恢复参数中的Sidecar身份不完整";
                return false;
            }
            if (!IsProcessIdentityAlive(authority.ProcessId, authority.ProcessStartUtcTicks))
            {
                rejection = "恢复参数中的Sidecar进程已退出或PID已复用";
                return false;
            }
            return true;
        }

        private bool IsProcessIdentityAlive(int pid, long startTicks)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited && WatchdogProcessIdentityPolicy.Matches(
                        pid, startTicks, process.Id, process.StartTime.ToUniversalTime().Ticks);
            }
            catch { return false; }
        }

        private bool IsProcessIdentityConfirmedDead(int pid, long startTicks)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return true;
                    return !WatchdogProcessIdentityPolicy.Matches(
                        pid,
                        startTicks,
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks);
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch
            {
                // Access/inspection failures are not proof of death.  Keep
                // the identity reserved and fail closed instead of launching
                // a second helper.
                return false;
            }
        }

        private bool IsCurrentConnection(
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease)
        {
            lock (_gate) return IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease);
        }

        private bool IsCurrentConnection(
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            lock (_gate)
            {
                return IsCurrentConnectionLocked(
                    connectionGeneration,
                    sessionGeneration,
                    sessionLease,
                    connectionIdentity);
            }
        }

        private bool IsExpectedConnectionCurrent(
            long sessionLease,
            long connectionGeneration,
            object connectionIdentity)
        {
            if (sessionLease <= 0 || connectionGeneration <= 0 || connectionIdentity == null)
                return false;
            lock (_gate)
            {
                return IsCurrentConnectionLocked(
                    connectionGeneration,
                    _activeSessionGeneration,
                    sessionLease,
                    connectionIdentity);
            }
        }

        private bool IsCurrentConnectionLocked(
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease)
        {
            return _activeConnectionGeneration == connectionGeneration &&
                   _activeSessionGeneration == sessionGeneration &&
                   IsCurrentSessionLeaseLocked(sessionLease);
        }

        private bool IsCurrentConnectionLocked(
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            return IsCurrentConnectionLocked(connectionGeneration, sessionGeneration, sessionLease) &&
                   connectionIdentity != null &&
                   ReferenceEquals(_activeConnectionIdentity, connectionIdentity);
        }

        private bool IsExpectedSendScopeLocked(
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity)
        {
            if (expectedSessionLease <= 0 ||
                !IsCurrentSessionLeaseLocked(expectedSessionLease))
                return false;
            if (expectedConnectionGeneration <= 0 && expectedConnectionIdentity == null)
                return _activeConnectionGeneration <= 0 &&
                       _activeConnectionIdentity == null;
            return IsCurrentConnectionLocked(
                expectedConnectionGeneration,
                _activeSessionGeneration,
                expectedSessionLease,
                expectedConnectionIdentity);
        }

        private bool IsCurrentSessionLease(long sessionLease)
        {
            lock (_gate) return IsCurrentSessionLeaseLocked(sessionLease);
        }

        private bool TryCaptureAttachOnlyReconnectScope(
            long expectedSessionLease,
            long expectedConnectionGeneration,
            object expectedConnectionIdentity,
            object expectedWorkerIdentity,
            bool expectedConnectionClosed,
            out long sessionLease)
        {
            sessionLease = 0;
            SidecarIdentityStateMachine.AuthoritySnapshot authority;
            long capturedSessionLease;
            lock (_gate)
            {
                if (_options?.LaunchPolicy != WatchdogLaunchPolicy.AttachExistingAuthorityOnly ||
                    _sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0 ||
                    _sessionLifetime == null || _activeSessionLease <= 0)
                    return false;
                if (expectedSessionLease > 0 &&
                    !IsCurrentSessionLeaseLocked(expectedSessionLease))
                    return false;
                authority = _identity.Authority;
                capturedSessionLease = _activeSessionLease;
            }

            // The OS probe must stay outside _gate.  Re-enter below and
            // compare the exact authority reference before reserving a
            // reconnect worker; a stale probe may never authorize a new
            // worker for a replacement identity.
            var authorityLiveness = ProbeAuthorityLiveness(
                capturedSessionLease,
                out var probedAuthority);
            if (authorityLiveness == AuthorityLiveness.ConfirmedDead ||
                authorityLiveness == AuthorityLiveness.Missing)
            {
                var detail = authorityLiveness == AuthorityLiveness.ConfirmedDead
                    ? "AttachExistingAuthorityOnly权威进程已退出或PID已复用。"
                    : "AttachExistingAuthorityOnly缺少可验证权威身份。";
                ClearAuthorityIfCurrent(capturedSessionLease, probedAuthority ?? authority, detail);
                EnterTransportFailClosed(
                    capturedSessionLease,
                    WatchdogConnectFailureKind.IdentityRejected,
                    detail);
                return false;
            }

            lock (_gate)
            {
                if (_options?.LaunchPolicy != WatchdogLaunchPolicy.AttachExistingAuthorityOnly ||
                    _sessionClosing != 0 || _safeDegraded != 0 ||
                    Volatile.Read(ref _transportFailClosed) != 0 ||
                    _sessionLifetime == null || _activeSessionLease != capturedSessionLease ||
                    !ReferenceEquals(_identity.Authority, authority))
                    return false;
                if (expectedConnectionGeneration > 0)
                {
                    if (expectedSessionLease <= 0 || expectedConnectionIdentity == null)
                        return false;
                    if (expectedConnectionClosed)
                    {
                        if (_activeConnectionGeneration != 0 ||
                            _activeConnectionIdentity != null ||
                            _connectionGeneration < expectedConnectionGeneration)
                            return false;
                    }
                    else if (!IsCurrentConnectionLocked(
                                 expectedConnectionGeneration,
                                 _activeSessionGeneration,
                                 expectedSessionLease,
                                 expectedConnectionIdentity))
                        return false;
                }
                if (expectedWorkerIdentity != null &&
                    !ReferenceEquals(_monitorTaskIdentity, expectedWorkerIdentity) &&
                    !ReferenceEquals(_reconnectTaskIdentity, expectedWorkerIdentity))
                    return false;
                sessionLease = capturedSessionLease;
                return true;
            }
        }

        private bool IsCurrentSessionLeaseLocked(long sessionLease)
        {
            return _options != null && _activeSessionLease > 0 && _activeSessionLease == sessionLease;
        }

        private long GetSessionLease()
        {
            lock (_gate)
            {
                if (_options == null || _activeSessionLease <= 0)
                    throw new InvalidOperationException("Watchdog transport session is not configured.");
                return _activeSessionLease;
            }
        }

        private WatchdogClientTransportOptions GetOptions()
        {
            lock (_gate)
            {
                if (_options == null) throw new InvalidOperationException("Watchdog transport session is not configured.");
                return _options;
            }
        }

        private static WatchdogClientTransportOptions CloneAndValidateOptions(
            WatchdogClientTransportOptions source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireText(source.SessionId, nameof(source.SessionId));
            RequireText(source.PipeName, nameof(source.PipeName));
            RequireText(source.MainExecutablePath, nameof(source.MainExecutablePath));
            RequireText(source.SidecarExecutablePath, nameof(source.SidecarExecutablePath));
            RequireText(source.JournalDirectory, nameof(source.JournalDirectory));
            if (source.SessionGeneration <= 0)
                throw new ArgumentOutOfRangeException(nameof(source.SessionGeneration));
            if (source.RecoveryAttempt < 0)
                throw new ArgumentOutOfRangeException(nameof(source.RecoveryAttempt));
            if (!Enum.IsDefined(typeof(WatchdogLaunchPolicy), source.LaunchPolicy))
                throw new ArgumentOutOfRangeException(nameof(source.LaunchPolicy));
            if (source.SelectedChannels == null)
                throw new ArgumentNullException(nameof(source.SelectedChannels));
            var channels = source.SelectedChannels.ToArray();
            if (channels.Any(channel => channel <= 0) || channels.Distinct().Count() != channels.Length)
                throw new ArgumentException("SelectedChannels必须为正数且不能重复。", nameof(source.SelectedChannels));

            var policy = source.JournalPolicy ?? new WatchdogJournalPolicy();
            ValidateJournalPolicy(policy);
            var frozenSeed = source.AuthoritySeed == null
                ? null
                : new SidecarIdentityStateMachine.AuthoritySnapshot(
                    source.AuthoritySeed.ProcessId,
                    source.AuthoritySeed.ProcessStartUtcTicks,
                    source.AuthoritySeed.SessionId,
                    source.AuthoritySeed.InstanceNonce);
            return new WatchdogClientTransportOptions
            {
                SessionId = source.SessionId,
                PipeName = source.PipeName,
                MainExecutablePath = source.MainExecutablePath,
                SidecarExecutablePath = source.SidecarExecutablePath,
                JournalDirectory = source.JournalDirectory,
                JournalPolicy = new WatchdogJournalPolicy
                {
                    RetentionDays = policy.RetentionDays,
                    RetainSessionCount = policy.RetainSessionCount,
                    MaxTotalBytes = policy.MaxTotalBytes,
                    MaxSessionBytes = policy.MaxSessionBytes,
                    HeartbeatCheckpointSeconds = policy.HeartbeatCheckpointSeconds,
                    EmergencySpoolMaxBytes = policy.EmergencySpoolMaxBytes
                },
                SelectedChannels = channels,
                RecoveryProcess = source.RecoveryProcess,
                RecoveryAttempt = source.RecoveryAttempt,
                SessionGeneration = source.SessionGeneration,
                LaunchPolicy = source.LaunchPolicy,
                AuthoritySeed = frozenSeed,
                AuthoritySeedRejection = source.AuthoritySeedRejection
            };
        }

        private static WatchdogClientTransportCallbacks CloneAndValidateCallbacks(
            WatchdogClientTransportCallbacks source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            RequireCallback(source.CreateRunSession, nameof(source.CreateRunSession));
            RequireCallback(source.CaptureHeartbeat, nameof(source.CaptureHeartbeat));
            RequireCallback(source.RecordEvent, nameof(source.RecordEvent));
            RequireCallback(source.TransportError, nameof(source.TransportError));
            RequireCallback(source.TransportLost, nameof(source.TransportLost));
            RequireCallback(source.StopAllRequested, nameof(source.StopAllRequested));
            RequireCallback(source.ObserveDurableStopMarker, nameof(source.ObserveDurableStopMarker));
            return new WatchdogClientTransportCallbacks
            {
                CreateRunSession = source.CreateRunSession,
                CaptureHeartbeat = source.CaptureHeartbeat,
                RecordEvent = source.RecordEvent,
                TransportError = source.TransportError,
                TransportLost = source.TransportLost,
                StopAllRequested = source.StopAllRequested,
                ObserveDurableStopMarker = source.ObserveDurableStopMarker,
                // Recovery-failure receipts are an optional additive v3
                // callback for ordinary transport consumers, but when a
                // Runtime supplies it the frozen callback set must retain it.
                // Dropping it here makes a durable Host decision invisible
                // and forces the caller into a false 90-second timeout.
                RecoveryFailureReceiptReceived =
                    source.RecoveryFailureReceiptReceived
            };
        }

        private static void RequireCallback(Delegate callback, string name)
        {
            if (callback == null)
                throw new ArgumentException("Watchdog transport callback不能为空。", name);
        }

        private static void RequireText(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Watchdog transport option不能为空。", name);
        }

        private static void ValidateJournalPolicy(WatchdogJournalPolicy policy)
        {
            if (policy.RetentionDays < 1 || policy.RetentionDays > 3650 ||
                policy.RetainSessionCount < 1 || policy.RetainSessionCount > 10000 ||
                policy.MaxTotalBytes < 16L * 1024L * 1024L ||
                policy.MaxTotalBytes > 16L * 1024L * 1024L * 1024L ||
                policy.MaxSessionBytes < 4L * 1024L * 1024L ||
                policy.MaxSessionBytes > 1024L * 1024L * 1024L ||
                policy.MaxSessionBytes > policy.MaxTotalBytes ||
                policy.HeartbeatCheckpointSeconds < 10 ||
                policy.HeartbeatCheckpointSeconds > 3600 ||
                policy.EmergencySpoolMaxBytes < 1024L * 1024L ||
                policy.EmergencySpoolMaxBytes > 1024L * 1024L * 1024L)
                throw new ArgumentException("WatchdogJournalPolicy超出允许范围。", nameof(policy));
        }

        private bool ObserveStopMarker(long expectedSessionLease = 0)
        {
            Func<bool> observer;
            WatchdogClientTransportCallbacks callbacks;
            lock (_gate)
            {
                if (expectedSessionLease > 0 && !IsCurrentSessionLeaseLocked(expectedSessionLease)) return true;
                observer = _callbacks.ObserveDurableStopMarker;
                callbacks = _callbacks;
            }
            try { return observer?.Invoke() == true; }
            catch (Exception ex)
            {
                RaiseErrorWithCallbacks(callbacks, "DurableStopMarkerHandler", ex.GetBaseException().Message);
                return false;
            }
        }

        private void ReportTransportLostOnce(
            string reason,
            string detail,
            long expectedSessionLease = 0,
            long expectedConnectionGeneration = 0,
            object expectedConnectionIdentity = null,
            object expectedWorkerIdentity = null)
        {
            WatchdogClientTransportCallbacks callbacks;
            WatchdogClientTransportEvent transportEvent;
            var eventDetail = (reason ?? string.Empty) + ":" + (detail ?? string.Empty);
            lock (_gate)
            {
                if (expectedSessionLease > 0 &&
                    (_activeSessionLease != expectedSessionLease ||
                     (expectedConnectionGeneration > 0 &&
                      !IsCurrentConnectionLocked(
                          expectedConnectionGeneration,
                          _activeSessionGeneration,
                          expectedSessionLease,
                          expectedConnectionIdentity)) ||
                     (expectedWorkerIdentity != null &&
                      !ReferenceEquals(_monitorTaskIdentity, expectedWorkerIdentity) &&
                      !ReferenceEquals(_reconnectTaskIdentity, expectedWorkerIdentity))))
                    return;
                if (_transportLostReported != 0) return;
                _transportLostReported = 1;
                callbacks = _callbacks;
                transportEvent = ReserveStateChangedLocked("TransportLost", eventDetail);
            }
            try { callbacks?.RecordEvent?.Invoke("TransportLost", eventDetail); } catch { }
            try { callbacks?.TransportLost?.Invoke(reason, detail); } catch { }
            EnqueueStateChanged(transportEvent);
        }

        private void RaiseErrorOnce(string reason, string detail, ref int gate)
        {
            if (Interlocked.CompareExchange(ref gate, 1, 0) == 0) RaiseError(reason, detail);
        }

        private WatchdogClientTransportCallbacks CaptureConnectionCallbacks(
            long connectionGeneration,
            long sessionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            lock (_gate)
            {
                return IsCurrentConnectionLocked(
                           connectionGeneration,
                           sessionGeneration,
                           sessionLease,
                           connectionIdentity)
                    ? _callbacks
                    : null;
            }
        }

        private WatchdogClientTransportCallbacks CaptureConnectionCallbacks(
            long connectionGeneration,
            long sessionLease,
            object connectionIdentity)
        {
            lock (_gate)
            {
                return _activeSessionLease == sessionLease &&
                       _activeConnectionGeneration == connectionGeneration &&
                       ReferenceEquals(_activeConnectionIdentity, connectionIdentity)
                    ? _callbacks
                    : null;
            }
        }

        private WatchdogClientTransportCallbacks CaptureReconnectCallbacks(
            long sessionLease,
            object taskIdentity)
        {
            lock (_gate)
            {
                return _activeSessionLease == sessionLease &&
                       ReferenceEquals(_reconnectTaskIdentity, taskIdentity)
                    ? _callbacks
                    : null;
            }
        }

        private WatchdogClientTransportCallbacks CaptureSessionCallbacks(long sessionLease)
        {
            lock (_gate)
            {
                return IsCurrentSessionLeaseLocked(sessionLease) ? _callbacks : null;
            }
        }

        private static void RaiseErrorWithCallbacks(
            WatchdogClientTransportCallbacks callbacks,
            string reason,
            string detail)
        {
            if (callbacks == null) return;
            var safeDetail = detail ?? string.Empty;
            try { callbacks.RecordEvent?.Invoke("TransportEvent", (reason ?? string.Empty) + ":" + safeDetail); } catch { }
            try { callbacks.TransportError?.Invoke(reason, safeDetail); } catch { }
        }

        private void RaiseError(string reason, string detail)
        {
            RecordEvent("TransportEvent", (reason ?? string.Empty) + ":" + (detail ?? string.Empty));
            try { _callbacks.TransportError?.Invoke(reason, detail ?? string.Empty); } catch { }
        }

        private void RecordEvent(string eventType, string detail)
        {
            try { _callbacks.RecordEvent?.Invoke(eventType, detail ?? string.Empty); } catch { }
        }

        private string BuildSidecarArguments(int parentPid, long parentStartTicks, WatchdogClientTransportOptions options, string nonce)
        {
            var policy = options.JournalPolicy ?? new WatchdogJournalPolicy();
            return string.Format(
                CultureInfo.InvariantCulture,
                "--parent-pid {0} --parent-start-ticks {1} --session {2} --pipe {3} --executable {4} " +
                "--journal-directory {5} --journal-retention-days {6} --journal-retain-sessions {7} " +
                "--journal-max-total-bytes {8} --journal-max-session-bytes {9} " +
                "--journal-heartbeat-checkpoint-seconds {10} --journal-emergency-spool-max-bytes {11} " +
                "--sidecar-instance-nonce {12}",
                parentPid,
                parentStartTicks,
                Quote(options.SessionId),
                Quote(options.PipeName),
                Quote(options.MainExecutablePath),
                Quote(WatchdogJournalPaths.ValidateProjectDirectory(options.JournalDirectory)),
                policy.RetentionDays,
                policy.RetainSessionCount,
                policy.MaxTotalBytes,
                policy.MaxSessionBytes,
                policy.HeartbeatCheckpointSeconds,
                policy.EmergencySpoolMaxBytes,
                Quote(nonce));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static SidecarIdentityStateMachine.PendingSnapshot ToPendingSnapshot(PendingHelper pending)
        {
            return new SidecarIdentityStateMachine.PendingSnapshot(
                pending.ProcessId,
                pending.StartUtcTicks,
                pending.SessionId,
                pending.SessionGeneration,
                pending.InstanceNonce);
        }

        private static void DisposePending(PendingHelper pending, bool kill)
        {
            if (pending == null) return;
            DisposeLaunch(pending.Launch, kill);
        }

        private static void DisposeLaunch(SidecarProcessLaunchResult launch, bool kill)
        {
            if (launch == null) return;
            try
            {
                if (kill && launch.Process != null && !launch.Process.HasExited)
                {
                    launch.Process.Kill();
                    try
                    {
                        launch.Process.WaitForExit(WatchdogTransportPolicy.ProcessExitJoin1000);
                    }
                    catch { }
                }
            }
            catch { }
            try { launch.Dispose(); } catch { }
        }

        private static void WaitTaskBounded(Task task, int milliseconds)
        {
            if (task == null || task.IsCompleted || task.Id == Task.CurrentId) return;
            try { task.Wait(milliseconds); } catch { }
        }

        private static void CancelNoDispose(CancellationTokenSource source)
        {
            try { source?.Cancel(); } catch { }
        }

        private static void TryCancel(CancellationTokenSource source)
        {
            try { source?.Cancel(); } catch { }
            try { source?.Dispose(); } catch { }
        }

        private static void TryDispose(IDisposable disposable)
        {
            try { disposable?.Dispose(); } catch { }
        }
    }
}
