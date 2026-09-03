using System;

namespace MTTFTest.Watchdog.Protocol
{
    public static class EngineHostProtocol
    {
        public const int SchemaVersion = RecoveryProtocolV7.SchemaVersion;
        public const string PipeName = RecoveryProtocolV7.EnginePipeName;
        public const int MaximumRequestBytes = 4 * 1024 * 1024;
    }

    public enum EngineHostRequestKind
    {
        None = 0,
        ReadLatestSnapshot = 1,
        ExecuteRecoveryCommand = 2,
        ExecuteOperatorCommand = 3,
        ReadLatestTelemetry = 4,
        Ping = 5,
        ReadLatestFault = 6
    }

    public sealed class EngineHostRequest
    {
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public EngineHostRequestKind Kind { get; set; }
        public RecoveryCommand RecoveryCommand { get; set; }
        public OperatorCommand OperatorCommand { get; set; }

        public bool IsStructurallyValid()
        {
            if (SchemaVersion != EngineHostProtocol.SchemaVersion ||
                !RecoveryProtocolV7.IsGuid(RequestId) ||
                Kind == EngineHostRequestKind.None)
                return false;
            if (Kind == EngineHostRequestKind.ExecuteRecoveryCommand)
                return RecoveryCommand?.IsStructurallyValid() == true;
            if (Kind == EngineHostRequestKind.ExecuteOperatorCommand)
                return OperatorCommand?.IsStructurallyValid() == true;
            return RecoveryCommand == null && OperatorCommand == null;
        }
    }

    public sealed class RecoveryCommandReceipt
    {
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public string CommandId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public bool Succeeded { get; set; }
        public string Detail { get; set; } = string.Empty;
        public bool OutputsOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool DataBoundaryClosed { get; set; }
        public bool ExecutionAuthorizationRevoked { get; set; }
        public int QualificationCyclesCompleted { get; set; }
        public int FormalCyclesCompleted { get; set; }
        public long StableSinceUtcTicks { get; set; }
        public bool InterruptedCycleCounted { get; set; }
        public long CompletedUtcTicks { get; set; }
    }

    public sealed class EngineTelemetryFrame
    {
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public long Sequence { get; set; }
        public long CapturedUtcTicks { get; set; }
        public double[] Currents { get; set; } = Array.Empty<double>();
        public double[] Pressures { get; set; } = Array.Empty<double>();
        public int[] FormalCycleCounts { get; set; } = Array.Empty<int>();
        public int QualificationCyclesCompleted { get; set; }
        public int FormalCyclesSinceRecovery { get; set; }
        public long StableSinceUtcTicks { get; set; }
    }

    public sealed class EngineHostResponse
    {
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public EngineStateSnapshot Snapshot { get; set; }
        public EngineTelemetryFrame Telemetry { get; set; }
        public RecoveryCommandReceipt RecoveryReceipt { get; set; }
        public FaultObservation FaultObservation { get; set; }
    }
}
