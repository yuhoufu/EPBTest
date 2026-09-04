using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class EngineUiKernelState
    {
        public bool Available { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public long RunEpoch { get; set; }
        public long Revision { get; set; }
        public long CapturedUtcTicks { get; set; }
        public SystemTerminalState DesiredState { get; set; }
        public int ActiveIncidentCount { get; set; }
        public bool CommandPending { get; set; }
        public RecoveryStage OperatorStage { get; set; }
        public string ManualBatchEngineInstanceId { get; set; } = string.Empty;
        public string ManualBatchIncidentId { get; set; } = string.Empty;
        public string ManualBatchOwnerId { get; set; } = string.Empty;
        public int ManualPausedChannelsMask { get; set; }
        public string[] IsolatedResources { get; set; } = Array.Empty<string>();
        public string[] QualificationRetryEligibleScopes { get; set; } = Array.Empty<string>();
        public PressureMaintenanceLease PressureMaintenance { get; set; }
        public RecoveryStage PressureMaintenanceStage { get; set; }
        public string Reason { get; set; } = string.Empty;
        public bool IsIdle => Available && ActiveIncidentCount == 0 && !CommandPending;
        public bool CanClose => IsIdle && DesiredState == SystemTerminalState.StoppedByOperator;
        public bool CanStart => IsIdle && (DesiredState == SystemTerminalState.StoppedByOperator || DesiredState == SystemTerminalState.SafeIdleAlarmed);
        public bool IsFresh(long now) => Available && CapturedUtcTicks <= now && now - CapturedUtcTicks < TimeSpan.FromSeconds(3).Ticks;
        public bool IsStructurallyValid() => !Available || RecoveryProtocolV7.IsGuid(SessionId) && RecoveryProtocolV7.IsGuid(RunId) &&
            RunEpoch > 0 && Revision > 0 && EngineUiContract.IsUtcTicks(CapturedUtcTicks) && ActiveIncidentCount >= 0 &&
            Enum.IsDefined(typeof(SystemTerminalState), DesiredState) && DesiredState != SystemTerminalState.Unknown &&
            (OperatorStage == RecoveryStage.None ||
                (OperatorStage == RecoveryStage.OperatorPausePending || OperatorStage == RecoveryStage.OperatorPaused ||
                 OperatorStage == RecoveryStage.OperatorChannelsHeld || OperatorStage == RecoveryStage.OperatorResumeChecking) &&
                ActiveIncidentCount == 1 && RecoveryProtocolV7.IsGuid(ManualBatchEngineInstanceId) &&
                RecoveryProtocolV7.IsGuid(ManualBatchIncidentId) && RecoveryProtocolV7.IsGuid(ManualBatchOwnerId)) &&
            ManualPausedChannelsMask >= 0 && ManualPausedChannelsMask <= 4095 &&
            IsolatedResources != null && IsolatedResources.Length <= 64 &&
            QualificationRetryEligibleScopes != null && QualificationRetryEligibleScopes.Length <= 12 &&
            IsolatedResources.All(value => RecoveryProtocolV7.HasText(value) && value.Length <= 128) &&
            QualificationRetryEligibleScopes.All(value => IsolatedResources.Contains(value, StringComparer.OrdinalIgnoreCase)) &&
            IsolatedResources.Distinct(StringComparer.OrdinalIgnoreCase).Count() == IsolatedResources.Length &&
            QualificationRetryEligibleScopes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == QualificationRetryEligibleScopes.Length &&
            (PressureMaintenance == null ? PressureMaintenanceStage == RecoveryStage.None :
                PressureMaintenance.IsStructurallyValid() && PressureMaintenance.SessionId == SessionId &&
                PressureMaintenance.RunId == RunId && PressureMaintenance.RunEpoch == RunEpoch && ActiveIncidentCount == 1 &&
                OperatorStage == RecoveryStage.None && (PressureMaintenanceStage == RecoveryStage.AwaitingSafetyProof ||
                PressureMaintenanceStage == RecoveryStage.PressureMaintenancePreparing || PressureMaintenanceStage == RecoveryStage.PressureMaintenanceReady ||
                PressureMaintenanceStage == RecoveryStage.PressureMaintenanceExecuting || PressureMaintenanceStage == RecoveryStage.PressureMaintenanceStopping));
    }

    public sealed class SupervisorUiStateResponse
    {
        public const string Magic = "MTTF-SUPERVISOR-UI-STATE-RESPONSE-V7-UI1";
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string Detail { get; set; } = string.Empty;
        public EngineUiKernelState State { get; set; } = new EngineUiKernelState();

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(Magic); writer.Write(RequestId); writer.Write(ChallengeNonce); writer.Write(Accepted); writer.Write(Detail);
            writer.Write(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(State)); writer.Flush();
        }
        public static SupervisorUiStateResponse ReadFrom(BinaryReader reader)
        {
            if (SupervisorSessionLaunchRequest.ReadBoundedString(reader) != Magic) throw new InvalidDataException("UiStateResponseMagicInvalid");
            return new SupervisorUiStateResponse
            {
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader), Accepted = reader.ReadBoolean(),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                State = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<EngineUiKernelState>(SupervisorSessionLaunchRequest.ReadBoundedString(reader))
            };
        }
    }
}
