using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog.Client
{
    public enum ExactSessionClosingResult
    {
        Marked = 0,
        ExactSessionDetached = 1,
        NoEngineSession = 2,
        IdentityMismatch = 3
    }

    /// <summary>
    /// Controls whether a client is allowed to create a new sidecar when the
    /// first pipe connection is unavailable.  The normal initial-session
    /// policy preserves the historical launch-on-missing-pipe behaviour;
    /// recovery sessions may explicitly require an already validated
    /// authority and therefore fail closed instead of launching a helper.
    /// </summary>
    public enum WatchdogLaunchPolicy
    {
        LaunchIfPipeUnavailable = 0,
        AttachExistingAuthorityOnly = 1
    }

    public sealed class WatchdogClientTransportOptions
    {
        public string SessionId { get; set; }
        public string PipeName { get; set; }
        public string MainExecutablePath { get; set; }
        public string SidecarExecutablePath { get; set; }
        public string JournalDirectory { get; set; }
        public WatchdogJournalPolicy JournalPolicy { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public bool RecoveryProcess { get; set; }
        public int RecoveryAttempt { get; set; }
        public long SessionGeneration { get; set; }
        public WatchdogLaunchPolicy LaunchPolicy { get; set; } =
            WatchdogLaunchPolicy.LaunchIfPipeUnavailable;
        public SidecarIdentityStateMachine.AuthoritySnapshot AuthoritySeed { get; set; }
        public string AuthoritySeedRejection { get; set; }
    }

    public sealed class WatchdogClientTransportCallbacks
    {
        public Func<bool, int, WatchdogRunSession> CreateRunSession { get; set; }
        public Func<WatchdogHeartbeat> CaptureHeartbeat { get; set; }
        public Action<string, string> RecordEvent { get; set; }
        public Action<string, string> TransportError { get; set; }
        public Action<string, string> TransportLost { get; set; }
        public Action<string, string> StopAllRequested { get; set; }
        public Action<WatchdogMessage> RecoveryFailureReceiptReceived { get; set; }
        public Func<bool> ObserveDurableStopMarker { get; set; }
    }

    public enum WatchdogSendDisposition
    {
        Sent = 0,
        ScopeStale = 1,
        AdmissionBusy = 2,
        TransportUnavailable = 3,
        TransportWriteFailed = 4
    }

    public enum WatchdogConnectFailureKind
    {
        Unknown = 0,
        PipeUnavailable = 1,
        ProtocolRejected = 2,
        IdentityRejected = 3,
        HandshakeRejected = 4,
        SessionRevoked = 5,
        InvalidConfiguration = 6,
        TransportFailure = 7,
        Cancelled = 8,
        LaunchFailure = 9
    }

    /// <summary>
    /// A classified connection failure.  The reconnect policy must only
    /// launch a helper for PipeUnavailable after authority/pending liveness
    /// has been checked; protocol, identity and session failures are
    /// fail-closed and must never trigger a second helper.
    /// </summary>
    public sealed class WatchdogConnectException : IOException
    {
        public WatchdogConnectFailureKind Kind { get; }

        public WatchdogConnectException(
            WatchdogConnectFailureKind kind,
            string message,
            Exception inner = null)
            : base(message ?? string.Empty, inner)
        {
            Kind = kind;
        }
    }

    /// <summary>
    /// Immutable point-in-time view of the client transport lifecycle.  The
    /// engine owns the mutable state; callers receive only this detached
    /// value and therefore cannot observe a half-updated connection state.
    /// </summary>
    public sealed class WatchdogClientTransportSnapshot
    {
        public long CapturedUtcTicks { get; }
        public string SessionId { get; }
        public long SessionGeneration { get; }
        public long SessionLease { get; }
        public long ActiveSessionLease { get; }
        public bool SessionActive { get; }
        public long ConnectionGeneration { get; }
        public long ActiveConnectionGeneration { get; }
        public long AttachedConnectionGeneration { get; }
        public long MonitorGeneration { get; }
        public bool MonitorActive { get; }
        public long ReconnectGeneration { get; }
        public bool ReconnectActive { get; }
        public bool IsAttached { get; }
        public bool HasPending { get; }
        public bool HasAuthority { get; }
        public bool IsClosing { get; }
        public bool IsSafeDegraded { get; }
        public bool TransportFailClosed { get; }
        public WatchdogConnectFailureKind TransportFailureKind { get; }
        public string TransportFailureDetail { get; }
        public bool LaunchActive { get; }
        public bool LaunchClosureBlocked { get; }
        public string LaunchClosureDetail { get; }
        public bool ConnectActive { get; }
        public long ConnectGeneration { get; }
        public int ReconnectAttempt { get; }
        public int OwnedHandleCount { get; }
        public long HeartbeatAckSequence { get; }
        public long LastHeartbeatAckUtcTicks { get; }
        public long HeartbeatAckAgeMilliseconds { get; }
        public int PendingProcessId { get; }
        public long PendingProcessStartUtcTicks { get; }
        public string PendingSessionId { get; }
        public long PendingSessionGeneration { get; }
        public string PendingInstanceNonce { get; }
        public int AuthorityProcessId { get; }
        public long AuthorityProcessStartUtcTicks { get; }
        public string AuthoritySessionId { get; }
        public string AuthorityInstanceNonce { get; }

        public WatchdogClientTransportSnapshot(
            long capturedUtcTicks,
            string sessionId,
            long sessionGeneration,
            long connectionGeneration,
            long attachedConnectionGeneration,
            bool isAttached,
            bool hasPending,
            bool hasAuthority,
            bool isClosing,
            bool isSafeDegraded,
            int reconnectAttempt,
            string pendingInstanceNonce,
            string authorityInstanceNonce)
            : this(
                capturedUtcTicks,
                sessionId,
                sessionGeneration,
                0,
                0,
                connectionGeneration,
                connectionGeneration,
                attachedConnectionGeneration,
                0,
                false,
                0,
                false,
                isAttached,
                hasPending,
                hasAuthority,
                isClosing,
                isSafeDegraded,
                reconnectAttempt,
                0,
                0,
                0,
                pendingInstanceNonce,
                0,
                0,
                null,
                0,
                0,
                0,
                null,
                authorityInstanceNonce)
        {
        }

        public WatchdogClientTransportSnapshot(
            long capturedUtcTicks,
            string sessionId,
            long sessionGeneration,
            long sessionLease,
            long activeSessionLease,
            long connectionGeneration,
            long activeConnectionGeneration,
            long attachedConnectionGeneration,
            long monitorGeneration,
            bool monitorActive,
            long reconnectGeneration,
            bool reconnectActive,
            bool isAttached,
            bool hasPending,
            bool hasAuthority,
            bool isClosing,
            bool isSafeDegraded,
            int reconnectAttempt,
            int ownedHandleCount,
            long heartbeatAckSequence,
            long lastHeartbeatAckUtcTicks,
            string pendingInstanceNonce,
            int pendingProcessId,
            long pendingProcessStartUtcTicks,
            string pendingSessionId,
            long pendingSessionGeneration,
            int authorityProcessId,
            long authorityProcessStartUtcTicks,
            string authoritySessionId,
            string authorityInstanceNonce,
            bool transportFailClosed = false,
            WatchdogConnectFailureKind transportFailureKind = WatchdogConnectFailureKind.Unknown,
            string transportFailureDetail = null,
            bool launchActive = false,
            bool launchClosureBlocked = false,
            string launchClosureDetail = null,
            bool connectActive = false,
            long connectGeneration = 0)
        {
            CapturedUtcTicks = capturedUtcTicks;
            SessionId = sessionId ?? string.Empty;
            SessionGeneration = sessionGeneration;
            SessionLease = sessionLease;
            ActiveSessionLease = activeSessionLease;
            SessionActive = activeSessionLease > 0;
            ConnectionGeneration = connectionGeneration;
            ActiveConnectionGeneration = activeConnectionGeneration;
            AttachedConnectionGeneration = attachedConnectionGeneration;
            MonitorGeneration = monitorGeneration;
            MonitorActive = monitorActive;
            ReconnectGeneration = reconnectGeneration;
            ReconnectActive = reconnectActive;
            IsAttached = isAttached;
            HasPending = hasPending;
            HasAuthority = hasAuthority;
            IsClosing = isClosing;
            IsSafeDegraded = isSafeDegraded;
            TransportFailClosed = transportFailClosed;
            TransportFailureKind = transportFailureKind;
            TransportFailureDetail = transportFailureDetail ?? string.Empty;
            LaunchActive = launchActive;
            LaunchClosureBlocked = launchClosureBlocked;
            LaunchClosureDetail = launchClosureDetail ?? string.Empty;
            ConnectActive = connectActive;
            ConnectGeneration = connectGeneration;
            ReconnectAttempt = reconnectAttempt;
            OwnedHandleCount = ownedHandleCount;
            HeartbeatAckSequence = heartbeatAckSequence;
            LastHeartbeatAckUtcTicks = lastHeartbeatAckUtcTicks;
            HeartbeatAckAgeMilliseconds = lastHeartbeatAckUtcTicks > 0
                ? Math.Max(0L, (DateTime.UtcNow.Ticks - lastHeartbeatAckUtcTicks) / TimeSpan.TicksPerMillisecond)
                : long.MaxValue;
            PendingProcessId = pendingProcessId;
            PendingProcessStartUtcTicks = pendingProcessStartUtcTicks;
            PendingSessionId = pendingSessionId ?? string.Empty;
            PendingSessionGeneration = pendingSessionGeneration;
            PendingInstanceNonce = pendingInstanceNonce ?? string.Empty;
            AuthorityProcessId = authorityProcessId;
            AuthorityProcessStartUtcTicks = authorityProcessStartUtcTicks;
            AuthoritySessionId = authoritySessionId ?? string.Empty;
            AuthorityInstanceNonce = authorityInstanceNonce ?? string.Empty;
        }
    }

    /// <summary>
    /// Immutable notification emitted after the engine commits a lifecycle
    /// transition.  StateChanged observers are called outside the engine
    /// gate and must treat Snapshot as the sole source of transport state.
    /// </summary>
    public sealed class WatchdogClientTransportEvent
    {
        public long Sequence { get; }
        public long UtcTicks { get; }
        public string EventType { get; }
        public string Detail { get; }
        public WatchdogClientTransportSnapshot Snapshot { get; }

        public WatchdogClientTransportEvent(
            long sequence,
            long utcTicks,
            string eventType,
            string detail,
            WatchdogClientTransportSnapshot snapshot)
        {
            Sequence = sequence;
            UtcTicks = utcTicks;
            EventType = eventType ?? string.Empty;
            Detail = detail ?? string.Empty;
            Snapshot = snapshot;
        }
    }

    public sealed class SidecarProcessLaunchRequest
    {
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
    }

    public sealed class SidecarProcessLaunchResult : IDisposable
    {
        public Process Process { get; internal set; }
        public SidecarProcessHandleOwner Owner { get; internal set; }

        public void Dispose()
        {
            try { Owner?.Dispose(); } catch { }
            Owner = null;
            try { Process?.Dispose(); } catch { }
            Process = null;
        }
    }

    public interface ISidecarProcessLauncher
    {
        Task<SidecarProcessLaunchResult> LaunchAsync(
            SidecarProcessLaunchRequest request,
            CancellationToken cancellationToken);
    }

    public interface INamedPipeClientFactory
    {
        System.IO.Pipes.NamedPipeClientStream Create(string pipeName);
    }
}
