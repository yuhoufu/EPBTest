using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed partial class PhysicalEngineRuntime
    {
        private EngineRunTimeClock _runTimeClock;
        private EngineProgressPublisher _progressPublisher;

        private void StartProgressPublisher()
        {
            var test = _config.Test;
            var writer = _projectPersistence.Writer;
            var configuration = _testConfigurationStore;
            var revision = _configurationRevision;
            var identity = _identity.Clone();
            var clock = new EngineRunTimeClock(writer.ReadDurableProgress().Select(row => row.RunTimeTicks).ToArray());
            _runTimeClock = clock;
            _progressPublisher = new EngineProgressPublisher(() =>
            {
                writer.AdvanceRunTime(clock.Capture());
                return writer.ReadDurableProgress();
            }, rows =>
            {
                // Save ONLY progress fields under the same path lock as config
                // and isolation. An explicit config commit stops this publisher.
                configuration.SaveProgress(revision, rows.Select(row => row.FormalCompleted).ToArray(),
                    rows.Select(row => row.MechanicalCompleted).ToArray(), rows.Select(row => row.RunTimeTicks).ToArray());
                foreach (var row in rows)
                {
                    var record = test.GetEpbRecord(row.Channel);
                    record.RunCount = Math.Max(record.RunCount, row.FormalCompleted);
                    record.ReconcileMechanicalCycleCount(row.MechanicalCompleted);
                    record.RunTime = TimeSpan.FromTicks(Math.Max(record.RunTimeSpan.Ticks, row.RunTimeTicks)).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
                }
                var checkpoint = _checkpointStore.Snapshot();
                if (checkpoint != null && checkpoint.ProgressSchemaVersion == 1)
                    _checkpointStore.RefreshFormalProgress(identity, test);
            }, ex => PublishProgressFault(identity, ex));
            foreach (var state in _manager.GetChannelRuntimeStates()) OnProgressChannelState(state);
        }

        private void OnProgressChannelState(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null) return;
            var active = state.State == ChannelRuntimeState.Starting || state.State == ChannelRuntimeState.Learning ||
                state.State == ChannelRuntimeState.Qualification || state.State == ChannelRuntimeState.Running ||
                state.State == ChannelRuntimeState.WarningRunning || state.State == ChannelRuntimeState.PausePending ||
                state.State == ChannelRuntimeState.ResumeChecking || state.State == ChannelRuntimeState.WaitingForSlotBarrier;
            _runTimeClock?.SetActive(state.Channel, active);
            _progressPublisher?.Request();
        }

        private void OnProgressFormalCompleted(int channel, int sessionCount) => _progressPublisher?.Request();
        private void OnProgressMechanicalCompleted(int channel, CycleAttemptKind kind, int cycle) => _progressPublisher?.Request();

        private Task FlushProgressAsync(CancellationToken token)
            => _progressPublisher?.FlushAsync(10000, token) ?? Task.CompletedTask;

        private void PublishProgressFault(RecoveryIdentity identity, Exception error,
            string faultCode = "ProgressPersistenceUnavailable", string property = "DurableProgressProjectionFailed")
        {
            var faultIdentity = identity.Clone(); faultIdentity.IncidentId = RecoveryProtocolV7.NewId(); faultIdentity.ResourceScope = "System";
            FaultObserved?.Invoke(new FaultObservation
            {
                Identity = faultIdentity, ObservationId = RecoveryProtocolV7.NewId(), ResourceKind = ResourceKind.System,
                ResourceId = "System", FaultCode = faultCode, ObservableProperty = property,
                Detail = error.GetBaseException().Message, Severity = FaultSeverity.RecoveryRequired, ScopeProven = false,
                DataBoundaryClosed = false, SafetyChainHealthy = true, ObservedUtcTicks = DateTime.UtcNow.Ticks
            });
            _log.Error("EngineHost持久化失败；已发布事实，未自行重启或复跑。", "落盘", error);
        }
    }
}
