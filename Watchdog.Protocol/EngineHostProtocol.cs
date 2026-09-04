using System;

namespace MTTFTest.Watchdog.Protocol
{
    public static class EngineHostProtocol
    {
        public const int SchemaVersion = RecoveryProtocolV7.SchemaVersion;
        public const string PipeName = RecoveryProtocolV7.EnginePipeName;
        public const string UiPipeSuffix = ".ui.v1";
        public const string UiPipeName = PipeName + UiPipeSuffix;
        public const string PanelPipeSuffix = ".panel.v1";
        public const string PanelPipeName = PipeName + PanelPipeSuffix;
        public const string SafetyPipeSuffix = ".safety.v1";
        public const string SafetyPipeName = PipeName + SafetyPipeSuffix;
        public const string SupervisorReadPipeSuffix = ".supervisor-read.v1";
        public const string SupervisorReadPipeName = PipeName + SupervisorReadPipeSuffix;
        public const string MaintenanceLeasePipeSuffix = ".maintenance-lease.v1";
        public const string MaintenanceLeasePipeName = PipeName + MaintenanceLeasePipeSuffix;
        public const int MaximumRequestBytes = 4 * 1024 * 1024;
        public static bool IsPrioritySafetyCommand(RecoveryCommandKind kind) =>
            kind == RecoveryCommandKind.StopByOperator || kind == RecoveryCommandKind.DisableOutputs ||
            kind == RecoveryCommandKind.EnterSafeIdle || kind == RecoveryCommandKind.SealActiveCycle || kind == RecoveryCommandKind.StopMaintenanceOutput;
        public static bool RequiresIndependentSafetyHandoff(RecoveryCommandKind kind) =>
            kind == RecoveryCommandKind.DisableOutputs || kind == RecoveryCommandKind.StopByOperator ||
            kind == RecoveryCommandKind.EnterSafeIdle || kind == RecoveryCommandKind.CommitTestConfiguration;
    }

    public enum EngineHostRequestKind
    {
        None = 0,
        ReadLatestSnapshot = 1,
        ExecuteRecoveryCommand = 2,
        ExecuteOperatorCommand = 3,
        ReadLatestTelemetry = 4,
        Ping = 5,
        ReadLatestFault = 6,
        ReadUiSnapshot = 7,
        ReadUiLogs = 8,
        ExecutePanelCommand = 9,
        UpdateMaintenanceLease = 10
    }

    public sealed class EngineHostRequest
    {
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public EngineHostRequestKind Kind { get; set; }
        public RecoveryCommand RecoveryCommand { get; set; }
        public OperatorCommand OperatorCommand { get; set; }
        public EngineUiLogQuery UiLogQuery { get; set; }
        public PressureMaintenanceLease MaintenanceLease { get; set; }

        public bool IsStructurallyValid()
        {
            if (SchemaVersion != EngineHostProtocol.SchemaVersion ||
                !RecoveryProtocolV7.IsGuid(RequestId) ||
                Kind == EngineHostRequestKind.None || !Enum.IsDefined(typeof(EngineHostRequestKind), Kind))
                return false;
            if (Kind == EngineHostRequestKind.UpdateMaintenanceLease)
                return MaintenanceLease?.IsStructurallyValid() == true && RecoveryCommand == null && OperatorCommand == null && UiLogQuery == null;
            if (MaintenanceLease != null) return false;
            if (Kind == EngineHostRequestKind.ReadUiLogs)
                return UiLogQuery?.IsStructurallyValid() == true && RecoveryCommand == null && OperatorCommand == null;
            if (UiLogQuery != null) return false;
            if (Kind == EngineHostRequestKind.ExecuteRecoveryCommand)
                return OperatorCommand == null && RecoveryCommand?.IsStructurallyValid() == true;
            if (Kind == EngineHostRequestKind.ExecuteOperatorCommand)
                return RecoveryCommand == null && OperatorCommand?.IsStructurallyValid() == true;
            if (Kind == EngineHostRequestKind.ExecutePanelCommand)
                return RecoveryCommand == null && OperatorCommand?.IsStructurallyValid() == true &&
                    AlarmPanelCommand.IsPanelOperation(OperatorCommand.Kind);
            return RecoveryCommand == null && OperatorCommand == null;
        }
    }

    public sealed class RecoveryCommandReceipt
    {
        public ProjectSwitchReceipt ProjectSwitch { get; set; }
        public EngineHardwareHandoff HardwareHandoff { get; set; }
        public ManualChannelState ManualChannels { get; set; }
        public PressureMaintenanceExecutionReceipt PressureMaintenance { get; set; }
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

    // Resource ownership evidence, never proof of physical voltage/pressure.
    // Missing fields in older schema-7 clients intentionally fail closed.
    public sealed class EngineHardwareHandoff
    {
        public int ContractVersion { get; set; } = 1;
        public RecoveryIdentity Identity { get; set; }
        public string CommandId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string EngineInstanceId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public bool LogicalQuiescent { get; set; }
        public bool NativeResourcesReleased { get; set; }
        public bool CallbacksIsolated { get; set; }
        public bool ExecutorQuiescent { get; set; }
        public long CapturedUtcTicks { get; set; }

        public bool Matches(RecoveryCommand command, string engineInstanceId)
        {
            return ContractVersion == 1 && command?.IsStructurallyValid() == true &&
                Identity?.IsStructurallyValid() == true &&
                Identity.ToCanonicalString() == command.Identity.ToCanonicalString() &&
                CommandId == command.CommandId && IdempotencyKey == command.IdempotencyKey &&
                OwnerId == command.OwnerId &&
                RecoveryProtocolV7.IsGuid(EngineInstanceId) && EngineInstanceId == engineInstanceId &&
                CapturedUtcTicks > 0 && CapturedUtcTicks <= command.DeadlineUtcTicks &&
                CapturedUtcTicks <= DateTime.UtcNow.Ticks;
        }

        public bool ResourcesTransferable => LogicalQuiescent && NativeResourcesReleased &&
            CallbacksIsolated && ExecutorQuiescent;
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
        public PressureMaintenanceLease MaintenanceLease { get; set; }
        public int SchemaVersion { get; set; } = EngineHostProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public EngineStateSnapshot Snapshot { get; set; }
        public EngineTelemetryFrame Telemetry { get; set; }
        public RecoveryCommandReceipt RecoveryReceipt { get; set; }
        public FaultObservation FaultObservation { get; set; }
        public EngineUiSnapshot UiSnapshot { get; set; }
        public EngineUiLogPage UiLogPage { get; set; }
        public OperatorExecutionReceipt OperatorReceipt { get; set; }
    }
}
