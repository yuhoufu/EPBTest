using System;
using System.Globalization;

namespace MTTFTest.Watchdog.Protocol
{
    public static class PressureMaintenanceProtocol
    {
        public const int Version = 1;
        public const int HeartbeatLeaseSeconds = 10;
        public const int MaximumSessionSeconds = 1800;
        public const int MinimumHeartbeatIntervalMilliseconds = 500;
        public const double MaximumPressureBar = 120;
        public static bool IsOperation(OperatorCommandKind kind) => kind == OperatorCommandKind.BeginPressureMaintenance ||
            kind == OperatorCommandKind.SetMaintenancePressure || kind == OperatorCommandKind.StopMaintenanceOutput ||
            kind == OperatorCommandKind.EndPressureMaintenance;
        public static bool IsExecution(RecoveryCommandKind kind) => kind == RecoveryCommandKind.PreparePressureMaintenance ||
            kind == RecoveryCommandKind.SetMaintenancePressure || kind == RecoveryCommandKind.StopMaintenanceOutput;
    }

    // The transport must authenticate this exact UI PID/start time. UI cannot supply
    // a lease expiry, safety proof, resource generation, owner for Begin, or output voltage.
    public sealed class PressureMaintenanceCommand
    {
        public string EngineInstanceId { get; set; } = string.Empty;
        public int UiProcessId { get; set; }
        public long UiProcessStartUtcTicks { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public int HydraulicId { get; set; }
        public double PressureBar { get; set; }
        public PressureMaintenanceCommand Clone() => (PressureMaintenanceCommand)MemberwiseClone();
        public bool IsStructurallyValid(OperatorCommandKind kind) => PressureMaintenanceProtocol.IsOperation(kind) &&
            RecoveryProtocolV7.IsGuid(EngineInstanceId) && UiProcessId > 0 && EngineUiContract.IsUtcTicks(UiProcessStartUtcTicks) &&
            HydraulicId >= 1 && HydraulicId <= 2 && EngineUiContract.IsFinite(PressureBar) && PressureBar >= 0 &&
            PressureBar <= PressureMaintenanceProtocol.MaximumPressureBar &&
            (kind == OperatorCommandKind.SetMaintenancePressure || PressureBar == 0) &&
            (kind == OperatorCommandKind.BeginPressureMaintenance ? string.IsNullOrEmpty(IncidentId) && string.IsNullOrEmpty(OwnerId) :
                RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId));
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("|", "PressureMaintenanceV1",
            EngineInstanceId, UiProcessId.ToString(CultureInfo.InvariantCulture), UiProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
            IncidentId, OwnerId, HydraulicId.ToString(CultureInfo.InvariantCulture), PressureBar.ToString("R", CultureInfo.InvariantCulture)));
        public bool Binds(PressureMaintenanceLease lease, OperatorCommandKind kind) => IsStructurallyValid(kind) && lease != null &&
            EngineInstanceId == lease.EngineInstanceId && UiProcessId == lease.UiProcessId && UiProcessStartUtcTicks == lease.UiProcessStartUtcTicks &&
            HydraulicId == lease.HydraulicId && (kind == OperatorCommandKind.BeginPressureMaintenance || IncidentId == lease.IncidentId && OwnerId == lease.OwnerId);
    }

    // Supervisor-owned, bounded authority. A heartbeat can extend only this same
    // session, never beyond AbsoluteDeadlineUtcTicks or after revocation/expiry.
    public sealed class PressureMaintenanceLease
    {
        public int ContractVersion { get; set; } = PressureMaintenanceProtocol.Version;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string EngineInstanceId { get; set; } = string.Empty;
        public long Generation { get; set; } = 1;
        public int UiProcessId { get; set; }
        public long UiProcessStartUtcTicks { get; set; }
        public int HydraulicId { get; set; }
        public long Revision { get; set; }
        public long CreatedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }
        public long AbsoluteDeadlineUtcTicks { get; set; }
        public long LastHeartbeatSequence { get; set; }
        public long LastHeartbeatUtcTicks { get; set; }
        public bool Revoked { get; set; }
        public SystemTerminalState ExitState { get; set; } = SystemTerminalState.StoppedByOperator;
        public string ExitReason { get; set; } = string.Empty;
        public PressureMaintenanceLease Clone() => (PressureMaintenanceLease)MemberwiseClone();
        public bool IsStructurallyValid() => ContractVersion == PressureMaintenanceProtocol.Version &&
            RecoveryProtocolV7.IsGuid(SessionId) && RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
            RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId) && RecoveryProtocolV7.IsGuid(EngineInstanceId) && Generation > 0 &&
            UiProcessId > 0 && EngineUiContract.IsUtcTicks(UiProcessStartUtcTicks) && HydraulicId >= 1 && HydraulicId <= 2 && Revision > 0 &&
            EngineUiContract.IsUtcTicks(CreatedUtcTicks) && EngineUiContract.IsUtcTicks(AbsoluteDeadlineUtcTicks) &&
            AbsoluteDeadlineUtcTicks > CreatedUtcTicks && AbsoluteDeadlineUtcTicks - CreatedUtcTicks <= TimeSpan.FromSeconds(PressureMaintenanceProtocol.MaximumSessionSeconds).Ticks &&
            ExpiresUtcTicks >= CreatedUtcTicks && ExpiresUtcTicks <= AbsoluteDeadlineUtcTicks &&
            LastHeartbeatSequence >= 0 && LastHeartbeatUtcTicks >= CreatedUtcTicks && LastHeartbeatUtcTicks <= AbsoluteDeadlineUtcTicks &&
            ExpiresUtcTicks - LastHeartbeatUtcTicks <= TimeSpan.FromSeconds(PressureMaintenanceProtocol.HeartbeatLeaseSeconds).Ticks &&
            (Revoked || ExpiresUtcTicks >= LastHeartbeatUtcTicks) &&
            (ExitState == SystemTerminalState.StoppedByOperator || ExitState == SystemTerminalState.SafeIdleAlarmed) && ExitReason?.Length <= 2048;
        public bool Binds(RecoveryIdentity identity, string owner) => IsStructurallyValid() && identity?.IsStructurallyValid() == true &&
            identity.ResourceScope == "System" && identity.SessionId == SessionId && identity.RunId == RunId && identity.RunEpoch == RunEpoch &&
            identity.IncidentId == IncidentId && owner == OwnerId && identity.Generation == Generation;
        public bool IsLive(long now) => !Revoked && now >= CreatedUtcTicks && now < ExpiresUtcTicks && now < AbsoluteDeadlineUtcTicks;
        public bool SameSession(PressureMaintenanceLease other) => other != null && SessionId == other.SessionId && RunId == other.RunId &&
            RunEpoch == other.RunEpoch && IncidentId == other.IncidentId && OwnerId == other.OwnerId && EngineInstanceId == other.EngineInstanceId && Generation == other.Generation &&
            UiProcessId == other.UiProcessId && UiProcessStartUtcTicks == other.UiProcessStartUtcTicks && HydraulicId == other.HydraulicId &&
            CreatedUtcTicks == other.CreatedUtcTicks && AbsoluteDeadlineUtcTicks == other.AbsoluteDeadlineUtcTicks;
        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("|", "PressureLeaseV1", SessionId, RunId,
            RunEpoch.ToString(CultureInfo.InvariantCulture), IncidentId, OwnerId, EngineInstanceId, Generation.ToString(CultureInfo.InvariantCulture),
            UiProcessId.ToString(CultureInfo.InvariantCulture), UiProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
            HydraulicId.ToString(CultureInfo.InvariantCulture), Revision.ToString(CultureInfo.InvariantCulture),
            CreatedUtcTicks.ToString(CultureInfo.InvariantCulture), ExpiresUtcTicks.ToString(CultureInfo.InvariantCulture),
            AbsoluteDeadlineUtcTicks.ToString(CultureInfo.InvariantCulture), LastHeartbeatSequence.ToString(CultureInfo.InvariantCulture),
            LastHeartbeatUtcTicks.ToString(CultureInfo.InvariantCulture), Revoked ? "1" : "0", ((int)ExitState).ToString(CultureInfo.InvariantCulture), ExitReason));
    }

    public sealed class PressureMaintenanceHeartbeat
    {
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string EngineInstanceId { get; set; } = string.Empty;
        public int UiProcessId { get; set; }
        public long UiProcessStartUtcTicks { get; set; }
        public long Sequence { get; set; }
        public long Generation { get; set; } = 1;
        public long IssuedUtcTicks { get; set; }
        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(SessionId) && RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
            RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId) && RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
            UiProcessId > 0 && EngineUiContract.IsUtcTicks(UiProcessStartUtcTicks) && Generation > 0 && Sequence > 0 && EngineUiContract.IsUtcTicks(IssuedUtcTicks);
        public bool Binds(PressureMaintenanceLease lease) => IsStructurallyValid() && lease != null && SessionId == lease.SessionId &&
            RunId == lease.RunId && RunEpoch == lease.RunEpoch && IncidentId == lease.IncidentId && OwnerId == lease.OwnerId &&
            EngineInstanceId == lease.EngineInstanceId && UiProcessId == lease.UiProcessId && UiProcessStartUtcTicks == lease.UiProcessStartUtcTicks && Generation == lease.Generation;
    }

    // Hardware execution result, not an independent physical safety proof.
    public sealed class PressureMaintenanceExecutionReceipt
    {
        public string EngineInstanceId { get; set; } = string.Empty;
        public long LeaseRevision { get; set; }
        public long LocalLeaseExpiresUtcTicks { get; set; }
        public int HydraulicId { get; set; }
        public bool OutputActive { get; set; }
        public double CommandPressureBar { get; set; }
        public double Voltage { get; set; }
        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(EngineInstanceId) && LeaseRevision > 0 &&
            EngineUiContract.IsUtcTicks(LocalLeaseExpiresUtcTicks) && HydraulicId >= 1 && HydraulicId <= 2 &&
            EngineUiContract.IsFinite(CommandPressureBar) && CommandPressureBar >= 0 && CommandPressureBar <= PressureMaintenanceProtocol.MaximumPressureBar &&
            EngineUiContract.IsFinite(Voltage) && Voltage >= -10 && Voltage <= 10 && (OutputActive || CommandPressureBar == 0 && Voltage == 0);
    }
}
