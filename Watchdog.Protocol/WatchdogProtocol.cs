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
        public const string MainUiReady = "MainUiReady";
        public const string Heartbeat = "Heartbeat";
        public const string HeartbeatAck = "HeartbeatAck";
        public const string Ping = "Ping";
        public const string Pong = "Pong";
        public const string ExternalRecoveryRequired = "ExternalRecoveryRequired";
        public const string RecoveryCheckpointValidated = "RecoveryCheckpointValidated";
        public const string SafetyPreflightPassed = "SafetyPreflightPassed";
        public const string RecoveryBatchCommitted = "RecoveryBatchCommitted";
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
        /// <summary>机器可判定的恢复失败码；旧客户端缺失时由 sidecar 兼容分类。</summary>
        public string RecoveryFailureCode { get; set; }
        /// <summary>确定性配置/包/检查点不变量错误不得通过重启同一进程重试。</summary>
        public bool RecoveryFailurePermanent { get; set; }
        public string RecoveryFailureDetail { get; set; }
        public string RecoveryFailureContextSha256 { get; set; }
        /// <summary>
        /// Heartbeat sequence acknowledged by the sidecar.  It is deliberately
        /// additive so old binaries can continue to deserialize the protocol.
        /// </summary>
        public long AckSequence { get; set; }
        public long RecoveryCommitGeneration { get; set; }
        public WatchdogRunSession Session { get; set; }
        public WatchdogHeartbeat Heartbeat { get; set; }
        public WatchdogStopSummary StopSummary { get; set; }
        public WatchdogCheckpointMirror CheckpointMirror { get; set; }
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
        /// <summary>每次创建恢复进程时递增，恢复成功后也不回退，用于审计。</summary>
        public long RelaunchGeneration { get; set; }
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
        /// <summary>
        /// Operator-requested graceful pause is an intentional quiescent mode,
        /// not an external recovery stage.  The sidecar must never infer a
        /// stalled recovery from this state while heartbeats remain healthy.
        /// </summary>
        public bool ManualPauseActive { get; set; }
        public bool ManualPausePending { get; set; }
        /// <summary>控制器发布的人工暂停权威阶段。</summary>
        public string ManualPauseStage { get; set; }
        /// <summary>阶段或仍带电通道集合发生收敛时单调递增。</summary>
        public long ManualPauseProgressVersion { get; set; }
        /// <summary>人工暂停阶段开始的 UTC DateTime ticks。</summary>
        public long ManualPauseStageStartedUtc { get; set; }
        /// <summary>控制器按周期和控制硬时限计算的暂停硬截止 UTC ticks。</summary>
        public long ManualPauseHardDeadlineUtc { get; set; }
        /// <summary>控制器明确判定人工暂停不能安全收口。</summary>
        public bool ManualPauseSafetyFault { get; set; }
        public string ManualPauseSafetyFaultReason { get; set; }
        public int[] ManualPauseEnergizedChannels { get; set; } = Array.Empty<int>();
        public string RecoveryCode { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryIncident { get; set; }
        public string RecoveryContext { get; set; }
        public int StageOrdinal { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        /// <summary>安全切断阶段已获得全部受影响电源组 OFF 的权威确认。</summary>
        public bool OutputsConfirmedOff { get; set; }
        /// <summary>仍在安全切断阶段且至少一个受影响电源组 OFF 未确认。</summary>
        public bool PowerOffUnconfirmed { get; set; }
        /// <summary>
        /// UTC DateTime ticks.  A process-local Stopwatch value cannot be
        /// compared across the main process and the sidecar.
        /// </summary>
        public long PauseSince { get; set; }
        public long PowerDisableSince { get; set; }
        public long RecoveryProgressVersion { get; set; }
        /// <summary>控制器恢复流水线硬截止 UTC DateTime ticks。</summary>
        public long RecoveryHardDeadlineUtc { get; set; }
        /// <summary>恢复批次真正提交后递增，并在后续心跳重复发送直到 Sidecar 观察到。</summary>
        public long RecoveryBatchCommitGeneration { get; set; }
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
        /// <summary>正常活动机械圈，仅用于残留/运行诊断，不参与 RecoveryActive。</summary>
        public int ActiveCycleCount { get; set; }
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
        public WatchdogChannelProgress[] ChannelProgress { get; set; } =
            Array.Empty<WatchdogChannelProgress>();
        public string MotorState { get; set; }
        public string PowerState { get; set; }
        public string PressureState { get; set; }
        public string RawState { get; set; }
        public string PersistenceState { get; set; }
        public string ContinuityState { get; set; }
        public string LogicalState { get; set; }
        public bool ManualStopRequested { get; set; }
        /// <summary>恢复进程保持断能并在原进程低频探测硬件，不允许学习。</summary>
        public bool HardwareUnavailable { get; set; }
        public string HardwareFailureFingerprint { get; set; }
        public string HardwareFailureDetail { get; set; }
        public int HardwareProbeAttempt { get; set; }
        public long HardwareNextProbeUtc { get; set; }
        public bool RunActive { get; set; }
    }

    public sealed class WatchdogChannelProgress
    {
        public int Channel { get; set; }
        public string State { get; set; }
        public long StateRevision { get; set; }
        public long StateSinceUtcTicks { get; set; }
        public bool TimerActive { get; set; }
        public bool RunnerActive { get; set; }
        public bool Energized { get; set; }
        public long LastMechanicalCompletedUtcTicks { get; set; }
        public long MechanicalCompletedCount { get; set; }
        public int ConsecutiveSoftwareAbortCount { get; set; }
        public long DoCommandSequence { get; set; }
        public long PeakCutoffGeneration { get; set; }
        public long PeakCutoffSequence { get; set; }
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

    public sealed class WatchdogCheckpointMirror
    {
        public int SchemaVersion { get; set; }
        public long Revision { get; set; }
        public bool Armed { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string SessionId { get; set; }
        public string StoreDir { get; set; }
        public string TestName { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public Dictionary<string, int> RemainingFormalCycles { get; set; } =
            new Dictionary<string, int>();
        public string SourcePath { get; set; }
        public string Sha256 { get; set; }
        public string UpdatedUtc { get; set; }
    }
}
