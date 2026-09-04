using System;
using System.Globalization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>Durable admission result; Accepted is not a claim that physical stopping has completed.</summary>
    public sealed class OperatorCommandAdmission
    {
        public string CommandId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long IssuedUtcTicks { get; set; }
        public long CommittedRevision { get; set; }
        public bool Accepted { get; set; }
        public string IncidentId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public SystemTerminalState DesiredState { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public OperatorCommand PanelTransaction { get; set; }
        public OperatorCommand ProjectTransaction { get; set; }
        public OperatorCommand ConfigurationTransaction { get; set; }
        public OperatorCommand MaintenanceTransaction { get; set; }
        public string DestinationRunId { get; set; } = string.Empty;
        public long DestinationRunEpoch { get; set; }
        public bool ExecutionCompleted { get; set; }
        public bool ExecutionSucceeded { get; set; }
        public long ExecutionCompletedUtcTicks { get; set; }

        public OperatorCommandAdmission Clone()
        {
            var copy = (OperatorCommandAdmission)MemberwiseClone();
            copy.PanelTransaction = PanelTransaction?.Clone();
            copy.ProjectTransaction = ProjectTransaction?.Clone();
            copy.ConfigurationTransaction = ConfigurationTransaction?.Clone();
            copy.MaintenanceTransaction = MaintenanceTransaction?.Clone();
            return copy;
        }

        public static string GetFingerprint(OperatorCommand command) => SupervisorProtocol.ComputeTextSha256(
            string.Join("|", command.SchemaVersion.ToString(CultureInfo.InvariantCulture), command.CommandId,
                command.SessionId, command.RunId, command.RunEpoch.ToString(CultureInfo.InvariantCulture),
                command.BaseRevision.ToString(CultureInfo.InvariantCulture), command.PayloadSha256,
                ((int)command.Kind).ToString(CultureInfo.InvariantCulture), command.IssuedUtcTicks.ToString(CultureInfo.InvariantCulture)));

        public bool Matches(OperatorCommand command) => command != null &&
            string.Equals(Fingerprint, GetFingerprint(command), StringComparison.Ordinal);

        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(CommandId) &&
            RecoveryProtocolV7.IsGuid(SessionId) && RecoveryProtocolV7.IsGuid(RunId) && RunEpoch > 0 &&
            IssuedUtcTicks > 0 && CommittedRevision > 0 && RecoveryFailureReceipt.IsSha256(Fingerprint) &&
            (MaintenanceTransaction == null || Accepted && ConfigurationTransaction == null && ProjectTransaction == null && PanelTransaction == null &&
                MaintenanceTransaction.IsStructurallyValid() && PressureMaintenanceProtocol.IsOperation(MaintenanceTransaction.Kind) &&
                Matches(MaintenanceTransaction) && MaintenanceTransaction.CommandId == CommandId && MaintenanceTransaction.SessionId == SessionId &&
                MaintenanceTransaction.RunId == RunId && MaintenanceTransaction.RunEpoch == RunEpoch && MaintenanceTransaction.IssuedUtcTicks == IssuedUtcTicks &&
                RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId) &&
                (ExecutionCompleted ? EngineUiContract.IsUtcTicks(ExecutionCompletedUtcTicks) : !ExecutionSucceeded && ExecutionCompletedUtcTicks == 0)) &&
            (ConfigurationTransaction == null || Accepted && PanelTransaction == null && ProjectTransaction == null &&
                ConfigurationTransaction.Kind == OperatorCommandKind.CommitConfiguration && ConfigurationTransaction.IsStructurallyValid() &&
                Matches(ConfigurationTransaction) && ConfigurationTransaction.CommandId == CommandId && ConfigurationTransaction.SessionId == SessionId &&
                ConfigurationTransaction.RunId == RunId && ConfigurationTransaction.RunEpoch == RunEpoch && ConfigurationTransaction.IssuedUtcTicks == IssuedUtcTicks &&
                RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId) &&
                (ExecutionCompleted ? EngineUiContract.IsUtcTicks(ExecutionCompletedUtcTicks) : !ExecutionSucceeded && ExecutionCompletedUtcTicks == 0)) &&
            (ProjectTransaction == null ? string.IsNullOrEmpty(DestinationRunId) && DestinationRunEpoch == 0 :
                Accepted && PanelTransaction == null && ProjectTransaction.IsStructurallyValid() &&
                ProjectTransaction.Kind == OperatorCommandKind.SwitchProject && Matches(ProjectTransaction) &&
                ProjectTransaction.CommandId == CommandId && ProjectTransaction.SessionId == SessionId &&
                ProjectTransaction.RunId == RunId && ProjectTransaction.RunEpoch == RunEpoch && ProjectTransaction.IssuedUtcTicks == IssuedUtcTicks &&
                RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId) &&
                RecoveryProtocolV7.IsGuid(DestinationRunId) && DestinationRunId != RunId && RunEpoch < long.MaxValue && DestinationRunEpoch == RunEpoch + 1 &&
                (ExecutionCompleted ? EngineUiContract.IsUtcTicks(ExecutionCompletedUtcTicks) : !ExecutionSucceeded && ExecutionCompletedUtcTicks == 0)) &&
            (ProjectTransaction != null || ConfigurationTransaction != null || MaintenanceTransaction != null || (PanelTransaction == null
                ? !ExecutionCompleted && !ExecutionSucceeded && ExecutionCompletedUtcTicks == 0 &&
                    (!Accepted || RecoveryProtocolV7.IsGuid(IncidentId) && RecoveryProtocolV7.IsGuid(OwnerId))
                : Accepted && PanelTransaction.IsStructurallyValid() && AlarmPanelCommand.IsPanelOperation(PanelTransaction.Kind) &&
                    Matches(PanelTransaction) && PanelTransaction.CommandId == CommandId &&
                    PanelTransaction.SessionId == SessionId && PanelTransaction.RunId == RunId && PanelTransaction.RunEpoch == RunEpoch &&
                    PanelTransaction.IssuedUtcTicks == IssuedUtcTicks &&
                    string.IsNullOrEmpty(IncidentId) && string.IsNullOrEmpty(OwnerId) &&
                    (ExecutionCompleted ? EngineUiContract.IsUtcTicks(ExecutionCompletedUtcTicks) : !ExecutionSucceeded && ExecutionCompletedUtcTicks == 0)));
    }
}
