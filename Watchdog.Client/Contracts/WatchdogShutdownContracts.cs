using System;

namespace MTTFTest.Watchdog.Client
{
    /// <summary>
    /// Immutable termination evidence for the seven engine workers.  A worker
    /// is terminal only after the task captured by ShutdownWithReceipt has
    /// completed; no Task is exposed to callers, so the receipt remains a
    /// stable value object rather than a second lifecycle control surface.
    /// </summary>
    public sealed class WorkerTerminationState
    {
        public long SessionLease { get; }
        public long CapturedUtcTicks { get; }
        public bool ReaderTerminal { get; }
        public bool HeartbeatTerminal { get; }
        public bool MonitorTerminal { get; }
        public bool ReconnectTerminal { get; }
        public bool ConnectTerminal { get; }
        public bool LaunchTerminal { get; }
        public bool SendTerminal { get; }
        public bool LaunchReservationTerminal { get; }
        public bool RetainedLaunchTerminal { get; }
        public bool PendingOwnerReleased { get; }
        public bool TransportDetached { get; }
        public bool CapturedOwnerReleased { get; }
        public int OwnedHandleCountBefore { get; }
        public int OwnedHandleCountAfter { get; }
        public int LiveOwnedHandleCount { get; }
        public bool AllWorkersTerminal { get; }
        public bool AllResourcesReleased { get; }

        public WorkerTerminationState(
            long sessionLease,
            long capturedUtcTicks,
            bool readerTerminal,
            bool heartbeatTerminal,
            bool monitorTerminal,
            bool reconnectTerminal,
            bool connectTerminal,
            bool launchTerminal,
            bool launchReservationTerminal,
            bool retainedLaunchTerminal,
            bool pendingOwnerReleased,
            bool transportDetached,
            int liveOwnedHandleCount,
            int ownedHandleCountBefore = 0,
            bool capturedOwnerReleased = true)
            : this(
                sessionLease,
                capturedUtcTicks,
                readerTerminal,
                heartbeatTerminal,
                monitorTerminal,
                reconnectTerminal,
                connectTerminal,
                launchTerminal,
                sendTerminal: true,
                launchReservationTerminal,
                retainedLaunchTerminal,
                pendingOwnerReleased,
                transportDetached,
                liveOwnedHandleCount,
                ownedHandleCountBefore,
                capturedOwnerReleased)
        {
        }

        /// <summary>
        /// Full constructor used by the production engine.  The compatibility
        /// overload above treats transports created before the dedicated send
        /// worker existed as already send-terminal.
        /// </summary>
        public WorkerTerminationState(
            long sessionLease,
            long capturedUtcTicks,
            bool readerTerminal,
            bool heartbeatTerminal,
            bool monitorTerminal,
            bool reconnectTerminal,
            bool connectTerminal,
            bool launchTerminal,
            bool sendTerminal,
            bool launchReservationTerminal,
            bool retainedLaunchTerminal,
            bool pendingOwnerReleased,
            bool transportDetached,
            int liveOwnedHandleCount,
            int ownedHandleCountBefore = 0,
            bool capturedOwnerReleased = true)
        {
            SessionLease = sessionLease;
            CapturedUtcTicks = capturedUtcTicks;
            ReaderTerminal = readerTerminal;
            HeartbeatTerminal = heartbeatTerminal;
            MonitorTerminal = monitorTerminal;
            ReconnectTerminal = reconnectTerminal;
            ConnectTerminal = connectTerminal;
            LaunchTerminal = launchTerminal;
            SendTerminal = sendTerminal;
            LaunchReservationTerminal = launchReservationTerminal;
            RetainedLaunchTerminal = retainedLaunchTerminal;
            PendingOwnerReleased = pendingOwnerReleased;
            TransportDetached = transportDetached;
            CapturedOwnerReleased = capturedOwnerReleased;
            OwnedHandleCountBefore = Math.Max(0, ownedHandleCountBefore);
            OwnedHandleCountAfter = Math.Max(0, liveOwnedHandleCount);
            LiveOwnedHandleCount = Math.Max(0, liveOwnedHandleCount);
            AllWorkersTerminal = ReaderTerminal && HeartbeatTerminal &&
                MonitorTerminal && ReconnectTerminal && ConnectTerminal &&
                LaunchTerminal && SendTerminal;
            AllResourcesReleased = AllWorkersTerminal &&
                LaunchReservationTerminal && RetainedLaunchTerminal &&
                PendingOwnerReleased && TransportDetached &&
                CapturedOwnerReleased;
        }
    }

    /// <summary>
    /// The value returned by the sole production shutdown implementation.
    /// It describes the exact detached session lease and the post-cleanup
    /// state; it deliberately contains no Task or mutable engine object.
    /// </summary>
    public sealed class ShutdownReceipt
    {
        public long SessionLease { get; }
        public long CapturedUtcTicks { get; }
        public WorkerTerminationState WorkerTermination { get; }
        public bool ShutdownEventPublished { get; }
        public bool SessionDetached { get; }

        public bool AllWorkersTerminal =>
            WorkerTermination != null && WorkerTermination.AllWorkersTerminal;
        public bool AllResourcesReleased =>
            WorkerTermination != null && WorkerTermination.AllResourcesReleased;
        public int LiveOwnedHandleCount =>
            WorkerTermination?.LiveOwnedHandleCount ?? 0;
        public bool LaunchReservationTerminal =>
            WorkerTermination != null && WorkerTermination.LaunchReservationTerminal;
        public bool RetainedLaunchTerminal =>
            WorkerTermination != null && WorkerTermination.RetainedLaunchTerminal;
        public bool PendingOwnerReleased =>
            WorkerTermination != null && WorkerTermination.PendingOwnerReleased;
        public bool TransportDetached =>
            WorkerTermination != null && WorkerTermination.TransportDetached;

        public ShutdownReceipt(
            long sessionLease,
            long capturedUtcTicks,
            WorkerTerminationState workerTermination,
            bool shutdownEventPublished,
            bool sessionDetached)
        {
            SessionLease = sessionLease;
            CapturedUtcTicks = capturedUtcTicks;
            WorkerTermination = workerTermination ?? throw new ArgumentNullException(nameof(workerTermination));
            ShutdownEventPublished = shutdownEventPublished;
            SessionDetached = sessionDetached;
        }
    }
}
