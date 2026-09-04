using System;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // These values must be part of the EngineHost role's sealed launch arguments.
    // They describe an already prepared transaction and confer no run permission.
    public sealed class EngineProjectActivation
    {
        public int ContractVersion { get; set; } = 1;
        public string OperatorCommandId { get; set; } = string.Empty;
        public string PreparedDocumentSha256 { get; set; } = string.Empty;
        public string PlanSha256 { get; set; } = string.Empty;
        public EngineProjectActivation Clone() => (EngineProjectActivation)MemberwiseClone();
        public bool IsStructurallyValid() => ContractVersion == 1 && RecoveryProtocolV7.IsGuid(OperatorCommandId) &&
            ProjectSwitchPlan.IsSha256(PreparedDocumentSha256) && ProjectSwitchPlan.IsSha256(PlanSha256);
        public bool Matches(RecoveryCommand command) => IsStructurallyValid() && command?.IsStructurallyValid() == true &&
            command.Kind == RecoveryCommandKind.ActivateProjectSwitch &&
            OperatorCommandId == command.ProjectSwitch.OperatorCommandId &&
            PreparedDocumentSha256 == command.ProjectSwitchPreparedSha256 && PlanSha256 == command.ProjectSwitch.ComputeSha256();

        public string ToArguments()
        {
            if (!IsStructurallyValid()) throw new InvalidOperationException("ProjectActivationArgumentsInvalid");
            return " --project-command " + OperatorCommandId + " --project-prepared " + PreparedDocumentSha256 + " --project-plan " + PlanSha256;
        }

        public static EngineProjectActivation FromCommand(RecoveryCommand command)
        {
            if (command?.IsStructurallyValid() != true || command.Kind != RecoveryCommandKind.ActivateProjectSwitch)
                throw new InvalidOperationException("ProjectActivationCommandInvalid");
            return new EngineProjectActivation { OperatorCommandId = command.ProjectSwitch.OperatorCommandId,
                PreparedDocumentSha256 = command.ProjectSwitchPreparedSha256, PlanSha256 = command.ProjectSwitch.ComputeSha256() };
        }

        public static EngineProjectActivation ParseArguments(string[] args)
        {
            args = args ?? Array.Empty<string>();
            var names = new[] { "--project-command", "--project-prepared", "--project-plan" };
            if (!args.Any(value => names.Contains(value, StringComparer.OrdinalIgnoreCase))) return null;
            var values = new string[3];
            for (var index = 0; index < names.Length; index++)
            {
                var found = Enumerable.Range(0, args.Length).Where(i => string.Equals(args[i], names[index], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 1 || found[0] == args.Length - 1) throw new InvalidOperationException("ProjectActivationArgumentsAmbiguous");
                values[index] = args[found[0] + 1];
            }
            var result = new EngineProjectActivation { OperatorCommandId = values[0], PreparedDocumentSha256 = values[1], PlanSha256 = values[2] };
            if (!result.IsStructurallyValid()) throw new InvalidOperationException("ProjectActivationArgumentsInvalid");
            return result;
        }
    }
}
