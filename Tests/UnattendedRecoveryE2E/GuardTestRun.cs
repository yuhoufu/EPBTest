using System;
using System.IO;
using System.Linq;
using System.Text;
using MTTFTest.RecoveryControl;

namespace MTTFTest.UnattendedRecoveryTestMain
{
    // No hardware is accessed here. These commits are fixture data, never
    // evidence of physical safe pressure or of a production test cycle.
    internal sealed class GuardTestRun
    {
        private readonly object _gate = new object();
        private readonly RecoveryControlStore _store;
        private readonly RecoveryProcessIdentity _process;
        private readonly RecoveryAuthorizationToken _token;
        private readonly string _runId;
        private readonly string _configuration;
        private readonly string _operationId;
        private readonly string _ledgerPath;
        private long _sequence;
        private bool _stopped;

        internal GuardTestRun(RecoveryControlStore store, string runId,
            string sessionId, string project, bool recovered)
        {
            _store = store;
            _process = RecoveryProcessProbe.Current();
            _runId = runId;
            var state = store.Read();
            if (recovered)
            {
                var launch = state.Launches.SingleOrDefault(item =>
                    item.State == "Started" && item.Process?.Matches(_process) == true);
                if (launch == null) throw new InvalidOperationException("E2EGuardStartedLaunchMissing");
                _token = launch.Authorization;
                _operationId = launch.OperationId;
                _configuration = state.Intent.ConfigurationIdentity;
                store.BindRecoveredRun(_token, _operationId, runId, _process, DateTime.UtcNow);
            }
            else
            {
                _configuration = "E2E-NoHardware-Fixture-V1";
                _token = store.BeginManualRun(runId, runId, _configuration, _process, DateTime.UtcNow);
            }
            store.BindWatchdogSession(_token, runId, _process, sessionId, DateTime.UtcNow);
            _ledgerPath = Path.Combine(project, "e2e-guard-commits-" + runId + ".log");
        }

        internal bool Stopped { get { lock (_gate) return _stopped; } }
        internal long Sequence { get { lock (_gate) return _sequence; } }

        internal void Stop()
        {
            // A watchdog stop request fences this process immediately. It is
            // not necessarily an operator cancellation of the whole trial.
            lock (_gate) _stopped = true;
        }

        internal bool TryCommit()
        {
            lock (_gate)
            {
                if (_stopped) return false;
                if (_operationId != null && !_store.IsRecoveredRunReadyForAdmission(
                    _token, _operationId, _process, DateTime.UtcNow)) return false;
                _store.AssertRunAllowed(_token, _process, DateTime.UtcNow);
                var next = checked(_sequence + 1);
                var bytes = Encoding.UTF8.GetBytes(_runId + "\t" + next + "\t" +
                    DateTime.UtcNow.ToString("O") + "\tNoHardwareFixture\r\n");
                using (var stream = new FileStream(_ledgerPath, FileMode.Append,
                    FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // A stop racing the flush must not publish a runnable commit.
                _store.AssertRunAllowed(_token, _process, DateTime.UtcNow);
                var now = DateTime.UtcNow;
                _store.PublishSnapshot(new RecoveryObservationSnapshot
                {
                    Authorization = _token, RunId = _runId,
                    ConfigurationIdentity = _configuration, MainProcess = _process,
                    Sequence = next, PublishedUtcTicks = now.Ticks,
                    SourceUtcTicks = now.Ticks, SourceVersion = next,
                    SourceAvailable = true, Stage = "E2EFixtureCommitted",
                    Channels = new[] { new RecoveryChannelProgress
                    {
                        Channel = 4, Eligible = true, SampleGeneration = 1,
                        SampleSequence = next, ControlSequence = next,
                        PersistedSequence = next, Stage = "E2EFixtureCommitted"
                    } }
                });
                _sequence = next;
                return true;
            }
        }
    }
}
