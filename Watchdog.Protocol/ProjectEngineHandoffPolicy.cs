using System;

namespace MTTFTest.Watchdog.Protocol
{
    public static class ProjectEngineHandoffPolicy
    {
        public static bool IsSource(RecoveryCommand command, EngineStateSnapshot snapshot) =>
            command?.IsStructurallyValid() == true && command.Kind == RecoveryCommandKind.ActivateProjectSwitch &&
            snapshot?.IsStructurallyValid() == true && IsFresh(snapshot) &&
            snapshot.SessionId == command.ProjectSwitch.SourceIdentity.SessionId && snapshot.RunId == command.ProjectSwitch.SourceIdentity.RunId &&
            snapshot.RunEpoch == command.ProjectSwitch.SourceIdentity.RunEpoch && snapshot.EngineInstanceId == command.ProjectSwitch.SourceEngineInstanceId &&
            snapshot.RecoveryOwnerId == command.OwnerId && snapshot.RecoveryIncidentId == command.Identity.IncidentId &&
            !snapshot.OutputsEnergized && !snapshot.HardwareInitialized && snapshot.HardwareRecompositionReady &&
            (snapshot.State == SystemTerminalState.StoppedByOperator || snapshot.State == SystemTerminalState.SafeIdleAlarmed);

        public static bool IsDestination(RecoveryCommand command, EngineStateSnapshot snapshot) =>
            command?.IsStructurallyValid() == true && command.Kind == RecoveryCommandKind.ActivateProjectSwitch &&
            snapshot?.IsStructurallyValid() == true && IsFresh(snapshot) &&
            snapshot.SessionId == command.Identity.SessionId && snapshot.RunId == command.Identity.RunId && snapshot.RunEpoch == command.Identity.RunEpoch &&
            snapshot.EngineInstanceId != command.ProjectSwitch.SourceEngineInstanceId && snapshot.ProjectActivation?.Matches(command) == true &&
            snapshot.HardwareInitialized && !snapshot.OutputsEnergized && snapshot.State == SystemTerminalState.SafeIdleAlarmed;

        private static bool IsFresh(EngineStateSnapshot snapshot) => snapshot.CapturedUtcTicks <= DateTime.UtcNow.Ticks &&
            DateTime.UtcNow.Ticks - snapshot.CapturedUtcTicks <= TimeSpan.FromSeconds(3).Ticks;

        public static RecoveryCommandReceipt DestinationReceipt(RecoveryCommand command, EngineStateSnapshot snapshot)
        {
            if (!IsDestination(command, snapshot)) throw new InvalidOperationException("ProjectDestinationAdmissionInvalid");
            return new RecoveryCommandReceipt { CommandId = command.CommandId, IdempotencyKey = command.IdempotencyKey,
                Succeeded = true, DataBoundaryClosed = true, ExecutionAuthorizationRevoked = true, CompletedUtcTicks = DateTime.UtcNow.Ticks,
                Detail = "ProjectDestinationInitialized;FinalIndependentStopPending",
                ProjectSwitch = new ProjectSwitchReceipt { Identity = command.Identity.Clone(), PlanSha256 = command.ProjectSwitch.ComputeSha256(),
                    PreparedDocumentSha256 = command.ProjectSwitchPreparedSha256, EngineInstanceId = snapshot.EngineInstanceId, SelectionCommitted = true } };
        }
    }
}
