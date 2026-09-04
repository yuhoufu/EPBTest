using System;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    // File executor only. The host execution fence establishes these facts;
    // only Recovery Kernel chooses the plan and the next process/Run.
    internal sealed class EngineProjectSwitchExecutor
    {
        private readonly EngineProjectSelectionStore _selection;
        internal EngineProjectSwitchExecutor(EngineProjectSelectionStore selection) =>
            _selection = selection ?? throw new ArgumentNullException(nameof(selection));

        internal ProjectSwitchReceipt ExecuteSource(RecoveryCommand command, EngineTestConfiguration configuration,
            long configurationRevision, EngineProjectSwitchBoundary boundary, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (command?.IsStructurallyValid() != true || command.ProjectSwitch == null ||
                !command.ProjectSwitch.MatchesSource(command.Identity) ||
                (command.Kind != RecoveryCommandKind.PrepareProjectSwitch && command.Kind != RecoveryCommandKind.AbortProjectSwitch))
                throw new InvalidOperationException("ProjectSourceCommandInvalid");
            if (boundary?.IsClosed != true) throw new InvalidOperationException("ProjectSourceBoundaryNotClosed");
            if (command.Kind == RecoveryCommandKind.AbortProjectSwitch)
            {
                _selection.AbortPrepared(command.ProjectSwitch, token);
                return null;
            }
            if (configuration?.IsStructurallyValid() != true || configurationRevision != command.ProjectSwitch.BaseConfigurationRevision ||
                configuration.ComputeSha256() != command.ProjectSwitch.BaseConfigurationSha256)
                throw new InvalidOperationException("ProjectSwitchConfigurationRevisionConflict");
            var digest = _selection.Prepare(command.ProjectSwitch, boundary, token: token);
            return new ProjectSwitchReceipt { Identity = command.Identity.Clone(),
                PlanSha256 = command.ProjectSwitch.ComputeSha256(), PreparedDocumentSha256 = digest,
                EngineInstanceId = command.ProjectSwitch.SourceEngineInstanceId, SelectionCommitted = false };
        }
    }
}
