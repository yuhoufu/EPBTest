using System;
using System.Globalization;

namespace MTTFTest.Watchdog.Protocol
{
    // Operator input contains no Owner, destination Run or launch authority.
    public sealed class ProjectSwitchRequest
    {
        public int ContractVersion { get; set; } = 1;
        public string EngineInstanceId { get; set; } = string.Empty;
        public long BaseSelectionRevision { get; set; }
        public long BaseConfigurationRevision { get; set; }
        public string BaseConfigurationSha256 { get; set; } = string.Empty;
        public string SourceProjectFileSha256 { get; set; } = string.Empty;
        public string TargetConfigurationPath { get; set; } = string.Empty;
        public string TargetProjectFileSha256 { get; set; } = string.Empty;
        public ProjectCreationRequest Creation { get; set; }
        public ProjectResetRequest Reset { get; set; }

        public ProjectSwitchRequest Clone()
        {
            var copy = (ProjectSwitchRequest)MemberwiseClone(); copy.Creation = Creation?.Clone(); copy.Reset = Reset?.Clone(); return copy;
        }
        public bool IsStructurallyValid() => ContractVersion == 1 && RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
            BaseSelectionRevision > 0 && BaseConfigurationRevision >= 0 &&
            ProjectSwitchPlan.IsSha256(BaseConfigurationSha256) && ProjectSwitchPlan.IsSha256(SourceProjectFileSha256) &&
            (Reset == null || Creation == null && Reset.IsStructurallyValid(TargetConfigurationPath) && TargetProjectFileSha256 == SourceProjectFileSha256) &&
            (Creation == null ? ProjectSwitchPlan.IsSha256(TargetProjectFileSha256) :
                string.IsNullOrEmpty(TargetProjectFileSha256) && Creation.IsStructurallyValid(TargetConfigurationPath)) &&
            ProjectSwitchPlan.IsProjectConfigurationPath(TargetConfigurationPath);

        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("\n",
            ContractVersion.ToString(CultureInfo.InvariantCulture), EngineInstanceId,
            BaseSelectionRevision.ToString(CultureInfo.InvariantCulture), BaseConfigurationRevision.ToString(CultureInfo.InvariantCulture),
            BaseConfigurationSha256, SourceProjectFileSha256, TargetConfigurationPath, TargetProjectFileSha256) +
            (Creation == null ? string.Empty : "\nCreate\n" + Creation.ComputeSha256()) +
            (Reset == null ? string.Empty : "\nReset\n" + Reset.ComputeSha256()));
    }

    public sealed class ProjectSwitchReceipt
    {
        public int ContractVersion { get; set; } = 1;
        public string PlanSha256 { get; set; } = string.Empty;
        public string PreparedDocumentSha256 { get; set; } = string.Empty;
        public string EngineInstanceId { get; set; } = string.Empty;
        public RecoveryIdentity Identity { get; set; }
        public bool SelectionCommitted { get; set; }

        public bool Matches(RecoveryCommand command) => ContractVersion == 1 && command?.IsStructurallyValid() == true &&
            command.ProjectSwitch != null && PlanSha256 == command.ProjectSwitch.ComputeSha256() &&
            Identity?.IsStructurallyValid() == true && Identity.ToCanonicalString() == command.Identity.ToCanonicalString() &&
            RecoveryProtocolV7.IsGuid(EngineInstanceId) &&
            (command.Kind == RecoveryCommandKind.PrepareProjectSwitch
                ? !SelectionCommitted && EngineInstanceId == command.ProjectSwitch.SourceEngineInstanceId &&
                    ProjectSwitchPlan.IsSha256(PreparedDocumentSha256)
                : command.Kind == RecoveryCommandKind.ActivateProjectSwitch && SelectionCommitted &&
                    EngineInstanceId != command.ProjectSwitch.SourceEngineInstanceId &&
                    PreparedDocumentSha256 == command.ProjectSwitchPreparedSha256);
    }
}
