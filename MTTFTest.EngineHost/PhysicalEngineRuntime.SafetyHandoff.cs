using System;
using System.Threading;
using System.Threading.Tasks;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed partial class PhysicalEngineRuntime
    {
        private volatile bool _releasedForSafety;
        public bool HardwareRecompositionReady => _releasedForSafety && _testConfiguration?.IsStructurallyValid() == true;

        // Called by the host only after the executor fence has joined the old
        // command and submitted its final OFF. Releasing during the first OFF
        // would race that command's cleanup and invalidate the second OFF.
        public Task PrepareSafetyHandoffAsync(RecoveryCommand command, EngineHardwareCommandResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!EngineHostProtocol.RequiresIndependentSafetyHandoff(command.Kind))
                throw new InvalidOperationException("IndependentSafetyHandoffCommandInvalid");
            if (!_releasedForSafety)
            {
                DisposeHardware(preserveAlarmPanel: true);
                // DisposeHardware checks the manager aggregate AND drains all
                // project writers before dropping their references.
                _releasedForSafety = result.DataBoundaryClosed && result.LogicalQuiescent && result.ExecutionAuthorizationRevoked;
                if (_releasedForSafety) RefreshProjectSelection(); // Hash the final file after the last writer drain.
            }
            result.NativeResourcesReleased = true;
            result.CallbacksIsolated = true;
            // Native closure alone cannot establish the logical/data boundary.
            result.LogicalQuiescent &= result.ExecutionAuthorizationRevoked;
            // Successful ownership handoff is not an in-process physical proof.
            // Supervisor acknowledges Stop only after its independent read-back.
            result.Succeeded = _releasedForSafety;
            result.Detail += ";HardwareResourcesReleased;CallbacksExited";
            return Task.CompletedTask;
        }

        private void RecordStoppedCheckpoint(RecoveryCommand command)
        {
            if (_checkpointStore?.Snapshot() == null) return; // No energized run has created a checkpoint yet.
            _checkpointStore.Update(command.Identity, value =>
            {
                value.ActiveCycleInvalidated = true;
                value.State = command.Kind == RecoveryCommandKind.StopByOperator ? "StoppedByOperator" : "SafeIdle";
                return true;
            }, command.Kind.ToString()); // Persistence failure must propagate, never certify a data boundary.
        }

        private static EngineHardwareCommandResult ReleasedIdleResult(string detail) => new EngineHardwareCommandResult
        {
            Succeeded = true, Detail = detail, DataBoundaryClosed = true,
            LogicalQuiescent = true, ExecutionAuthorizationRevoked = true
            // No cached output/pressure readings are promoted to current proof.
        };

        private EngineHardwareCommandResult CommitReleasedConfiguration(RecoveryCommand command, CancellationToken token)
        {
            if (!HardwareRecompositionReady || _manager != null || _progressPublisher != null || _rawPersistence != null || _projectPersistence != null)
                return Failed("ConfigurationRequiresReleasedHardwareAndWriters");
            if (command.OperatorTransaction.TestConfiguration.AoCalibration != null)
            {
                if (_aoConfigurationStore == null) return Failed("AoConfigurationStoreUnavailable");
                _aoConfiguration = _aoConfigurationStore.Commit(command.OperatorTransaction, token,
                    candidate => EngineAoConfigurationStore.ValidateOperatingPressures(candidate, _testConfiguration.Hydraulics));
                _identity = command.Identity.Clone();
                return ReleasedIdleResult("AoCalibrationCommitted;HardwareRemainsReleasedUntilSupervisorPreflight");
            }
            if (command.OperatorTransaction.TestConfiguration.DaqConfiguration != null)
            {
                if (_daqConfigurationStore == null) return Failed("DaqConfigurationStoreUnavailable");
                _daqConfiguration = _daqConfigurationStore.Commit(command.OperatorTransaction, token);
                _identity = command.Identity.Clone();
                return ReleasedIdleResult("DaqConfigurationCommitted;HardwareRemainsReleasedUntilSupervisorPreflight");
            }
            // The atomic store retains the same command record on a receipt retry.
            if (_aoConfigurationStore == null) return Failed("AoConfigurationStoreUnavailable");
            var activeAo = _aoConfigurationStore.Read();
            EngineAoConfigurationStore.ValidateOperatingPressures(activeAo.Configuration,
                command.OperatorTransaction.TestConfiguration.Configuration.Hydraulics);
            var committed = _testConfigurationStore.Commit(command.OperatorTransaction);
            var captured = EngineTestConfigurationStore.Capture(committed);
            _checkpointStore.RebindStoppedConfiguration(command.Identity, committed, captured.ComputeSha256());
            _displayConfig.Test = committed;
            _testConfiguration = captured;
            _configurationRevision = _testConfigurationStore.ReadRevision();
            _identity = command.Identity.Clone();
            RefreshProjectSelection();
            return ReleasedIdleResult("ConfigurationCommitted;HardwareRemainsReleasedUntilSupervisorPreflight");
        }

        private EngineHardwareCommandResult ExecuteReleasedProjectSwitch(RecoveryCommand command, CancellationToken token)
        {
            var checkpoint = _checkpointStore?.Snapshot();
            var boundary = new EngineProjectSwitchBoundary
            {
                // The normal execution lease is exclusive with the joined safety
                // executor which released native handles before this command.
                ExecutorQuiescent = _releasedForSafety && _manager == null,
                NativeResourcesReleased = _releasedForSafety && _manager == null && _do == null && _ao == null && _acquirer == null,
                WritersClosed = _progressPublisher == null && _rawPersistence == null && _projectPersistence == null,
                CheckpointClosed = _checkpointStore != null && (checkpoint == null || checkpoint.IsValidFor(command.Identity) &&
                    checkpoint.ActiveCycleInvalidated && (checkpoint.State == "SafeIdle" || checkpoint.State == "StoppedByOperator"))
            };
            var receipt = new EngineProjectSwitchExecutor(_projectSelectionStore).ExecuteSource(command, _testConfiguration,
                _configurationRevision, boundary, token);
            RefreshProjectSelection();
            var result = ReleasedIdleResult(command.Kind == RecoveryCommandKind.PrepareProjectSwitch
                ? "ProjectPrepared;SourceRetained;NoLaunchAuthorityCreated" : "ProjectPreparationAborted;SourceRetained");
            result.ProjectSwitch = receipt;
            return result;
        }
    }
}
