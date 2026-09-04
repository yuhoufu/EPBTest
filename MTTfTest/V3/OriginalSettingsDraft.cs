using System;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    // A local edit buffer, not a configuration writer. Telemetry changes and host
    // replacement in the same run do not invalidate it; metadata/run changes do.
    internal sealed class OriginalSettingsDraft
    {
        private readonly string _sessionId;
        private readonly string _runId;
        private readonly long _runEpoch;
        private readonly bool _daq;
        internal TestConfigurationCommit Commit { get; }

        internal OriginalSettingsDraft(EngineUiSnapshot snapshot, bool daq = false)
        {
            _daq = daq;
            if ((daq ? snapshot?.DaqConfiguration?.IsStructurallyValid() : snapshot?.TestConfiguration?.IsStructurallyValid()) != true ||
                snapshot.Engine?.IsStructurallyValid() != true)
                throw new InvalidOperationException("SettingsSnapshotInvalid");
            _sessionId = snapshot.Engine.SessionId; _runId = snapshot.Engine.RunId; _runEpoch = snapshot.Engine.RunEpoch;
            Commit = new TestConfigurationCommit
            {
                BaseConfigurationRevision = daq ? snapshot.DaqConfigurationRevision : snapshot.ConfigurationRevision,
                BaseConfigurationSha256 = daq ? snapshot.DaqConfigurationSha256 : snapshot.ConfigurationSha256,
                Configuration = daq ? null : snapshot.TestConfiguration.Clone(),
                DaqConfiguration = daq ? snapshot.DaqConfiguration.Clone() : null
            };
            if (!Commit.IsStructurallyValid()) throw new InvalidOperationException("SettingsRevisionInvalid");
        }

        internal bool SameRun(EngineUiSnapshot snapshot) => snapshot?.Engine != null &&
            snapshot.Engine.SessionId == _sessionId && snapshot.Engine.RunId == _runId && snapshot.Engine.RunEpoch == _runEpoch;

        internal bool IsCurrent(EngineUiSnapshot snapshot) => SameRun(snapshot) &&
            (_daq ? snapshot.DaqConfigurationRevision : snapshot.ConfigurationRevision) == Commit.BaseConfigurationRevision &&
            string.Equals(_daq ? snapshot.DaqConfigurationSha256 : snapshot.ConfigurationSha256, Commit.BaseConfigurationSha256, StringComparison.OrdinalIgnoreCase);

        internal bool IsApplied(EngineUiSnapshot snapshot) => SameRun(snapshot) &&
            (_daq ? snapshot.DaqConfigurationRevision : snapshot.ConfigurationRevision) > Commit.BaseConfigurationRevision &&
            string.Equals(_daq ? snapshot.DaqConfigurationSha256 : snapshot.ConfigurationSha256,
                _daq ? Commit.DaqConfiguration.ComputeSha256() : Commit.Configuration.ComputeSha256(), StringComparison.OrdinalIgnoreCase);
    }
}
