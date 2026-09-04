using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Durable handoff description; neither a launch capability nor a run permit.
    public sealed class ProjectSwitchPlan
    {
        public int ContractVersion { get; set; } = 1;
        public string OperatorCommandId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string SourceEngineInstanceId { get; set; } = string.Empty;
        public RecoveryIdentity SourceIdentity { get; set; }
        public string NextRunId { get; set; } = string.Empty;
        public long NextRunEpoch { get; set; }
        public long BaseSelectionRevision { get; set; }
        public string SourceProjectFileSha256 { get; set; } = string.Empty;
        public string TargetConfigurationPath { get; set; } = string.Empty;
        public string TargetProjectFileSha256 { get; set; } = string.Empty;
        public ProjectCreationRequest Creation { get; set; }
        public ProjectResetRequest Reset { get; set; }
        public long BaseConfigurationRevision { get; set; }
        public string BaseConfigurationSha256 { get; set; } = string.Empty;
        public string[] IsolatedResources { get; set; } = Array.Empty<string>();

        public ProjectSwitchPlan Clone()
        {
            var copy = (ProjectSwitchPlan)MemberwiseClone();
            copy.SourceIdentity = SourceIdentity?.Clone();
            copy.IsolatedResources = IsolatedResources?.ToArray();
            copy.Creation = Creation?.Clone();
            copy.Reset = Reset?.Clone();
            return copy;
        }

        public static bool IsSha256(string value) => RecoveryFailureReceipt.IsSha256(value);

        public bool IsStructurallyValid() => ContractVersion == 1 &&
            RecoveryProtocolV7.IsGuid(OperatorCommandId) && RecoveryProtocolV7.IsGuid(OwnerId) &&
            RecoveryProtocolV7.IsGuid(SourceEngineInstanceId) && SourceIdentity?.IsStructurallyValid() == true &&
            SourceIdentity.ResourceScope == "System" && SourceIdentity.RunEpoch < long.MaxValue &&
            RecoveryProtocolV7.IsGuid(NextRunId) && NextRunId != SourceIdentity.RunId &&
            NextRunEpoch == SourceIdentity.RunEpoch + 1 && BaseSelectionRevision > 0 &&
            RecoveryFailureReceipt.IsSha256(SourceProjectFileSha256) &&
            (Reset == null || Creation == null && Reset.IsStructurallyValid(TargetConfigurationPath) && TargetProjectFileSha256 == SourceProjectFileSha256) &&
            (Creation == null ? IsSha256(TargetProjectFileSha256) : string.IsNullOrEmpty(TargetProjectFileSha256) &&
                Creation.IsStructurallyValid(TargetConfigurationPath)) && IsProjectConfigurationPath(TargetConfigurationPath) &&
            BaseConfigurationRevision >= 0 && (string.IsNullOrEmpty(BaseConfigurationSha256) || IsSha256(BaseConfigurationSha256)) &&
            IsolatedResources != null && IsolatedResources.Length <= 64 &&
            IsolatedResources.All(value => RecoveryProtocolV7.HasText(value) && value.Length <= 128) &&
            IsolatedResources.Distinct(StringComparer.OrdinalIgnoreCase).Count() == IsolatedResources.Length;

        public bool MatchesSource(RecoveryIdentity identity) => identity?.IsStructurallyValid() == true &&
            SourceIdentity != null && identity.SessionId == SourceIdentity.SessionId && identity.RunId == SourceIdentity.RunId &&
            identity.RunEpoch == SourceIdentity.RunEpoch;

        public bool Binds(RecoveryIdentity identity, string owner, OperatorCommand command) => IsStructurallyValid() &&
            owner == OwnerId && command?.Kind == OperatorCommandKind.SwitchProject && command.IsStructurallyValid() &&
            command.CommandId == OperatorCommandId && command.SessionId == SourceIdentity.SessionId &&
            command.RunId == SourceIdentity.RunId && command.RunEpoch == SourceIdentity.RunEpoch &&
            command.ProjectSwitch.EngineInstanceId == SourceEngineInstanceId &&
            command.ProjectSwitch.BaseSelectionRevision == BaseSelectionRevision &&
            command.ProjectSwitch.BaseConfigurationRevision == BaseConfigurationRevision &&
            command.ProjectSwitch.BaseConfigurationSha256 == BaseConfigurationSha256 &&
            command.ProjectSwitch.SourceProjectFileSha256 == SourceProjectFileSha256 &&
            command.ProjectSwitch.TargetConfigurationPath == TargetConfigurationPath &&
            command.ProjectSwitch.TargetProjectFileSha256 == TargetProjectFileSha256 &&
            command.ProjectSwitch.Creation?.ComputeSha256() == Creation?.ComputeSha256() &&
            command.ProjectSwitch.Reset?.ComputeSha256() == Reset?.ComputeSha256() &&
            (MatchesSource(identity) || MatchesDestination(identity)) && identity.ResourceScope == "System" &&
            identity.IncidentId == SourceIdentity.IncidentId && identity.Generation == SourceIdentity.Generation;

        public bool MatchesDestination(RecoveryIdentity identity) => IsStructurallyValid() &&
            identity?.IsStructurallyValid() == true && identity.SessionId == SourceIdentity.SessionId &&
            identity.RunId == NextRunId && identity.RunEpoch == NextRunEpoch;

        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256(string.Join("\n",
            ContractVersion.ToString(CultureInfo.InvariantCulture), OperatorCommandId, OwnerId, SourceEngineInstanceId,
            SourceIdentity?.ToCanonicalString(), NextRunId, NextRunEpoch.ToString(CultureInfo.InvariantCulture),
            BaseSelectionRevision.ToString(CultureInfo.InvariantCulture), SourceProjectFileSha256,
            TargetConfigurationPath, TargetProjectFileSha256, BaseConfigurationRevision.ToString(CultureInfo.InvariantCulture),
            BaseConfigurationSha256, string.Join("|", IsolatedResources ?? Array.Empty<string>())) +
            (Creation == null ? string.Empty : "\nCreate\n" + Creation.ComputeSha256()) +
            (Reset == null ? string.Empty : "\nReset\n" + Reset.ComputeSha256()));

        public static bool IsProjectConfigurationPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Length < 4 ||
                    !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0 ||
                    path.Any(c => c < 32 || c == '|' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>')) return false;
                return string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(path), "TestConfig.xml", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Config", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
    }
}
