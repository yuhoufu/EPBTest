using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogProtocol
    {
        public const int Version = 2;
        public const int MinimumCompatibleVersion = 1;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string Serialize(WatchdogMessage message) => Json.Serialize(message);
        public static WatchdogMessage Deserialize(string value) => Json.Deserialize<WatchdogMessage>(value);
    }

    public static class WatchdogMessageType
    {
        public const string Attach = "Attach";
        public const string Attached = "Attached";
        public const string Heartbeat = "Heartbeat";
        public const string HeartbeatAck = "HeartbeatAck";
        public const string Ping = "Ping";
        public const string Pong = "Pong";
        public const string ExternalRecoveryRequired = "ExternalRecoveryRequired";
        public const string RecoveryAttemptFailed = "RecoveryAttemptFailed";
        public const string BatchStartFailed = "BatchStartFailed";
        public const string RequestStopAll = "RequestStopAll";
        public const string StopCompleted = "StopCompleted";
        public const string ManualStopRequested = "ManualStopRequested";
        public const string ManualStopIntent = "ManualStopIntent";
        public const string PhysicalStopConfirmed = "PhysicalStopConfirmed";
        public const string RunStopped = "RunStopped";
        public const string RunCompleted = "RunCompleted";
        public const string ApplicationClosing = "ApplicationClosing";
        public const string ShutdownExpected = "ShutdownExpected";
    }

    public sealed class WatchdogMessage
    {
        public int ProtocolVersion { get; set; } = WatchdogProtocol.Version;
        public string Type { get; set; }
        public string SessionId { get; set; }
        public string CorrelationId { get; set; }
        public string Reason { get; set; }
        /// <summary>
        /// Heartbeat sequence acknowledged by the sidecar.  It is deliberately
        /// additive so old binaries can continue to deserialize the protocol.
        /// </summary>
        public long AckSequence { get; set; }
        public WatchdogRunSession Session { get; set; }
        public WatchdogHeartbeat Heartbeat { get; set; }
        public WatchdogStopSummary StopSummary { get; set; }
    }

    public sealed class WatchdogRunSession
    {
        public string SessionId { get; set; }
        public string PipeName { get; set; }
        public string ExecutablePath { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int RecoveryAttempt { get; set; }
        public bool RecoveryProcess { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
    }

    public sealed class WatchdogHeartbeat
    {
        public long Sequence { get; set; }
        public string SessionId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string Phase { get; set; }
        public int[] EnabledChannels { get; set; } = Array.Empty<int>();
        public int[] EligibleChannels { get; set; } = Array.Empty<int>();
        public int[] CompletedChannels { get; set; } = Array.Empty<int>();
        public int[] AlarmedChannels { get; set; } = Array.Empty<int>();
        /// <summary>结构化硬件锁存通道；Watchdog只排除这一集合。</summary>
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        /// <summary>主程序已完成资格筛选、允许恢复的通道。</summary>
        public int[] RecoveryEligibleChannels { get; set; } = Array.Empty<int>();
        public int[] ManuallyDisabledChannels { get; set; } = Array.Empty<int>();
        public bool RecoveryActive { get; set; }
        public string RecoveryCode { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryIncident { get; set; }
        public string RecoveryContext { get; set; }
        public int StageOrdinal { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        /// <summary>
        /// UTC DateTime ticks.  A process-local Stopwatch value cannot be
        /// compared across the main process and the sidecar.
        /// </summary>
        public long PauseSince { get; set; }
        public long PowerDisableSince { get; set; }
        public long RecoveryProgressVersion { get; set; }
        public long StageStartedMonotonic { get; set; }
        public long Dev1CallbackGapCount { get; set; }
        public long Dev2CallbackGapCount { get; set; }
        public int Dev1Generation { get; set; }
        public int Dev2Generation { get; set; }
        public Dictionary<string, long> FrozenBoundary { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Persisted { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Head { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> InFlight { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, int> QueueDepth { get; set; } = new Dictionary<string, int>();
        public int DaqRecoveryCount { get; set; }
        public int SoftwareRecoveryCount { get; set; }
        public int RecoveryOwnerCount { get; set; }
        public bool StopAllActive { get; set; }
        public string StopStage { get; set; }
        public long StopStartedUtc { get; set; }
        public long StopStageStartedUtc { get; set; }
        public long StopProgressVersion { get; set; }
        public bool StopPhysicalSafe { get; set; }
        public int TimerCount { get; set; }
        public int RunnerCount { get; set; }
        public int EnergizedChannelCount { get; set; }
        public long CompletedCycleCount { get; set; }
        public int ExpectedCyclePeriodMs { get; set; }
        public int StopCtsCount { get; set; }
        public int CyclePauseCtsCount { get; set; }
        public string MotorState { get; set; }
        public string PowerState { get; set; }
        public string PressureState { get; set; }
        public string RawState { get; set; }
        public string PersistenceState { get; set; }
        public string ContinuityState { get; set; }
        public string LogicalState { get; set; }
        public bool ManualStopRequested { get; set; }
        public bool RunActive { get; set; }
    }

    public sealed class WatchdogStopSummary
    {
        public bool MotorOffConfirmed { get; set; }
        public bool PowerOffConfirmed { get; set; }
        public bool PressureSafeConfirmed { get; set; }
        public bool RawDrained { get; set; }
        public bool PersistenceConfirmed { get; set; }
        public bool ContinuityConfirmed { get; set; }
        public bool LogicalQuiescenceConfirmed { get; set; }
        public bool RequiresProcessRestart { get; set; }
        public bool TimedOut { get; set; }
        public string Outcome { get; set; }
        public string LastStage { get; set; }
        public string Detail { get; set; }
    }
}
