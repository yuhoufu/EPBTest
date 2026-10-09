using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using MTEmbTest;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class RecoveryCommitRoutingTests
    {
        internal static int RunAll()
        {
            SenderSources();
            OriginProofAndMarkers();
            ScopedTerminalDoesNotCommitAuthority();
            Console.WriteLine("PASS recovery commit routing 3/3");
            return 3;
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static void SenderSources()
        {
            var sends = 0;
            Action publish = () => sends++;
            RecoveryStartupCommitPolicy.PublishLegacyCommit(RecoveryStartupSource.IndependentExecutor, publish);
            RecoveryStartupCommitPolicy.PublishLegacyCommit(RecoveryStartupSource.SoftwareCheckpoint, publish);
            Require(sends == 0, "Independent/software startup invoked legacy generation/marker side effects");
            RecoveryStartupCommitPolicy.PublishLegacyCommit(RecoveryStartupSource.LegacyWatchdog, publish);
            Require(sends == 1, "Legacy startup lost its commit");
            var rejected = false;
            try { RecoveryStartupCommitPolicy.PublishLegacyCommit((RecoveryStartupSource)99, publish); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected && sends == 1, "Unknown origin generated legacy evidence");
        }

        private static void OriginProofAndMarkers()
        {
            var session = Guid.NewGuid().ToString("N");
            var independentRun = Guid.NewGuid().ToString("N");
            var request = Guid.NewGuid().ToString("N");
            var controller = new IndependentProcessIdentity
            {
                Pid = 123, StartUtcTicks = 456, ExecutablePath = @"D:\test\main.exe",
                WindowsSessionId = 1, SessionToken = Guid.NewGuid().ToString("N")
            };
            var heartbeat = new WatchdogHeartbeat
            {
                SessionId = session, ProcessId = 123, ProcessStartUtcTicks = 456,
                RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1,
                RunActive = true, Phase = "Formal", RecoveryBatchCommitGeneration = 82024
            };
            var state = new IndependentProjectState
            {
                Controller = controller,
                Intent = new IndependentRunIntent { RunId = independentRun, RunEpoch = 2 },
                Ticket = new IndependentLaunchTicket
                { Consumer = controller, RequestId = request, Generation = 1, DispatchStartedUtcTicks = 450 },
                Transaction = new IndependentRecoveryTransaction
                {
                    RequestId = request, Generation = 1, RunId = independentRun, RunEpoch = 2,
                    ReplacementPid = 123, ReplacementStartUtcTicks = 456, Phase = IndependentRecoveryPhase.Verifying
                },
                SessionProcesses = new[] { new IndependentSessionProcess
                {
                    Role = "Watchdog", ParentPid = 123, ParentStartUtcTicks = 456,
                    Process = new IndependentProcessIdentity { Pid = 234, StartUtcTicks = 567, SessionToken = session }
                } }
            };
            Func<bool> proof = () => WatchdogHost.ProvesIndependentRecoveryOrigin(
                state, heartbeat, controller.ExecutablePath, session, 234, 567);
            Require(proof(), "Exact independent origin rejected because two protocol RunIds differ");
            Require(state.Transaction.Phase == IndependentRecoveryPhase.Verifying,
                "Protocol observation falsely verified independent transaction");
            heartbeat.ProcessStartUtcTicks++;
            Require(!proof(), "PID reuse was accepted");
            heartbeat.ProcessStartUtcTicks--;
            state.Ticket.RequestId = "wrong";
            Require(!proof(), "Different transaction ticket accepted");
            state.Ticket.RequestId = request;
            state.SessionProcesses[0].Process.SessionToken = "other-session";
            Require(!proof(), "Different sidecar session accepted");
            state.SessionProcesses[0].Process.SessionToken = session;
            state.Transaction.OperatorStopOnly = true;
            Require(!proof(), "Safety-only transaction accepted as recovery origin");
            state.Transaction.OperatorStopOnly = false;
            state.Ticket.Revoked = true;
            state.Intent.ManualStopped = true;
            Require(proof() && state.Ticket.Revoked && state.Intent.ManualStopped,
                "Observing historical origin changed stop authority");

            var marker = new WatchdogRecoveryCommitEvidence
            {
                SchemaVersion = 2, Generation = 82024, GeneratedUtcTicks = 600,
                RunId = heartbeat.RunId, RunEpoch = heartbeat.RunEpoch, Stage = ""
            };
            Require(WatchdogHost.ResolveRecoveryCommitStage(heartbeat) == "FormalBatchStarted" &&
                WatchdogHost.MatchesIndependentRecoveryMarker(marker, heartbeat), "rc.2 empty-stage marker lost");
            marker.Generation++;
            Require(!WatchdogHost.MatchesIndependentRecoveryMarker(marker, heartbeat), "Future marker swallowed");
            marker.Generation--;
            marker.RunEpoch++;
            Require(!WatchdogHost.MatchesIndependentRecoveryMarker(marker, heartbeat), "Foreign run marker swallowed");
            marker.RunEpoch--;
            marker.Legacy = true;
            Require(!WatchdogHost.MatchesIndependentRecoveryMarker(marker, heartbeat), "Identity-less marker treated as proof");
        }

        private static void ScopedTerminalDoesNotCommitAuthority()
        {
            var directory = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                Path.GetTempPath(), "commit-routing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using (var process = Process.GetCurrentProcess())
            {
                var session = Guid.NewGuid().ToString("N");
                var start = process.StartTime.ToUniversalTime().Ticks;
                var args = new WatchdogArguments
                {
                    SessionId = session, ExecutablePath = process.MainModule.FileName,
                    ParentPid = process.Id, ParentStartTicks = start,
                    SidecarInstanceNonce = Guid.NewGuid().ToString("N"),
                    JournalDirectory = directory, JournalPolicy = new WatchdogJournalPolicy()
                };
                var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(directory, session,
                    process.Id, start, 8);
                Require(bootstrap.Succeeded, "Fixture bootstrap failed");
                var authority = new StrictHostV4AuthorityAdapter(args, process.Id, start);
                Require(WatchdogHost.IsUnusedLegacyPermit(authority.Snapshot), "Pristine None not recognized");
                var heartbeat = new WatchdogHeartbeat
                {
                    SessionId = session, ProcessId = process.Id, ProcessStartUtcTicks = start,
                    RunId = Guid.NewGuid().ToString("N"), RunEpoch = 1, RunActive = true, Phase = "Formal",
                    RecoveryBatchCommitGeneration = 82024
                };
                var key = WatchdogHost.RecoveryCommitObservationKey(heartbeat, 82024);
                // The constructor is deliberately not used: it starts UI supervision.
                // Exercise the actual receiver with a durable terminal observation reloaded.
                var journal = new WatchdogJournal
                {
                    SessionId = session, CurrentPid = process.Id, CurrentProcessStartUtcTicks = start,
                    State = "Formal", ConsecutiveStartupFailures = 2, NotApplicableRecoveryCommits = new[] { key },
                    LastHeartbeat = heartbeat
                };
                var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                journal = json.Deserialize<WatchdogJournal>(json.Serialize(journal));
                var host = (WatchdogHost)FormatterServices.GetUninitializedObject(typeof(WatchdogHost));
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(WatchdogHost).GetField(name, flags).SetValue(host, value);
                Set("_args", args); Set("_journal", journal); Set("_journalGate", new object());
                Set("_relaunchCoordinator", authority);
                var finish = typeof(WatchdogHost).GetMethod("TryFinishIndependentRecoveryCommit", flags);
                bool Observe(long generation) => (bool)finish.Invoke(host,
                    new object[] { generation, heartbeat, "FormalBatchStarted" });
                for (var i = 0; i < 1000; i++) Require(Observe(82024), "Repeated receipt was not terminal");
                journal.LastHeartbeat.RunEpoch = 9;
                Require(Observe(82024), "Previously proven late message lost terminal state after a new run");
                var markerMethod = typeof(WatchdogHost).GetMethod("TryFinishIndependentRecoveryMarker", flags);
                var marker = new WatchdogRecoveryCommitEvidence
                {
                    Generation = 82024, RunId = heartbeat.RunId, RunEpoch = 1,
                    GeneratedUtcTicks = DateTime.UtcNow.Ticks, Stage = ""
                };
                Require((bool)markerMethod.Invoke(host, new object[] { marker, journal.LastHeartbeat }),
                    "Previously proven rc.2 marker lost terminal state after a new run");
                journal.LastHeartbeat.RunEpoch = 1;
                Require(journal.LastRecoveryBatchCommitGeneration == 0 && journal.ConsecutiveStartupFailures == 2 &&
                    journal.State == "Formal" && authority.Snapshot.State == DurableRelaunchPermitState.None,
                    "NotApplicable mutated accepted generation, budget, business or permit");
                // Suppress filesystem proof for unknown keys; they must not hit the prior receipt.
                Set("_nextIndependentCommitProofTimestamp", long.MaxValue);
                Require(!Observe(82025), "Future generation deduplicated");
                heartbeat.RunEpoch++;
                Require(!Observe(82024), "Different run epoch deduplicated");
                heartbeat.RunEpoch--;
                var approval = authority.ApproveOrGetExisting(new DurableRelaunchRequest
                {
                    Fingerprint = "next", ProgressToken = "p", ProcessSource = "test",
                    RunId = heartbeat.RunId, RecoveryStage = "Recovery", MaximumProcessRelaunches = 8
                });
                Require(approval.ActionAllowed, "Later permit fixture failed: " + approval.Reason);
                Require(!Observe(82024), "Real later permit swallowed by receipt");
                var launch = authority.BeginLaunch(approval.Record.Identity, "--test-only");
                var capability = authority.GetLaunchCapability(approval.Record.Generation);
                Require(launch.Succeeded && capability != null && authority.ConsumeLaunchIntent(capability),
                    "Legacy launch intent could not be consumed");
                Require(authority.CommitStarted(approval.Record.Identity, process.Id + 100000, start + 1).Succeeded &&
                    authority.CommitAttached(approval.Record.Identity, process.Id + 100000, start + 1).Succeeded,
                    "Legacy permit did not reach Attached");
                var newRun = Guid.NewGuid().ToString("N");
                Require(authority.CommitRecoveryBatch(approval.Record.Identity, newRun, 2,
                    "FormalBatchStarted", "42", 82025).Succeeded, "Attached to Committed failed");
                var commit = typeof(WatchdogHost).GetMethod("TryCommitRecoveryBatch", flags);
                bool Replay(string run, long epoch, long generation) => (bool)commit.Invoke(host,
                    new object[] { run, epoch, "FormalBatchStarted", "42", generation });
                Require(Replay(newRun, 2, 82025) && Replay(newRun, 2, 82025), "Legacy duplicate was not idempotent");
                Require(!Replay(Guid.NewGuid().ToString("N"), 2, 82025) &&
                    !Replay(newRun, 3, 82025) && !Replay(newRun, 2, 82026),
                    "Legacy wrong RunId/RunEpoch/future generation accepted");
            }
        }
    }
}
