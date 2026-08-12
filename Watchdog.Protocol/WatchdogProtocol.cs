using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogProtocol
    {
        public const int Version = 1;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string Serialize(WatchdogMessage message) => Json.Serialize(message);
        public static WatchdogMessage Deserialize(string value) => Json.Deserialize<WatchdogMessage>(value);
    }

    public static class WatchdogMessageType
    {
        public const string Attach = "Attach";
        public const string Attached = "Attached";
        public const string Heartbeat = "Heartbeat";
        public const string Ping = "Ping";
        public const string Pong = "Pong";
        public const string ExternalRecoveryRequired = "ExternalRecoveryRequired";
        public const string RequestStopAll = "RequestStopAll";
        public const string StopCompleted = "StopCompleted";
        public const string ManualStopRequested = "ManualStopRequested";
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
        public int[] ManuallyDisabledChannels { get; set; } = Array.Empty<int>();
        public bool RecoveryActive { get; set; }
        public string RecoveryCode { get; set; }
        public string RecoveryStage { get; set; }
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
        public string Detail { get; set; }
    }
}
