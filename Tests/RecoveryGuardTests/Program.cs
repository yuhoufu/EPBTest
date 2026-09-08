using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.RecoveryControl;
using MTTFTest.RecoveryGuard;

namespace RecoveryGuardTests
{
    internal static class Program
    {
        private static int _passed;
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--commissioning-scope")
                {
                    Run("Commissioning is bounded and cannot inherit another authorization", CommissioningScope);
                    Run("Commissioning expiry fences action dispatch", CommissioningExpiry);
                    Run("Late commissioning responses cannot advance a transaction", CommissioningLateAction);
                    Console.WriteLine("PASS " + _passed + "/" + _passed);
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--interrupted-launch")
                {
                    Run("Killed reservation owner cannot create a second launch", InterruptedLaunchOwner);
                    Console.WriteLine("PASS " + _passed + "/" + _passed);
                    return 0;
                }
                if (args.Length == 3 && args[0] == "--reserve-and-wait")
                {
                    var store = new RecoveryControlStore(args[1]);
                    var now = new DateTime(long.Parse(args[2]), DateTimeKind.Utc);
                    store.ReserveLaunch(store.Read().Token(), Guid.NewGuid().ToString("N"), now, now.AddMinutes(1));
                    File.WriteAllText(Path.Combine(args[1], "reserved.ready"), "committed");
                    Thread.Sleep(30000);
                    return 0;
                }
                if (args.Length == 3 && args[0] == "--race-reserve")
                {
                    var store = new RecoveryControlStore(args[1]);
                    var now = new DateTime(long.Parse(args[2]), DateTimeKind.Utc);
                    try { store.ReserveLaunch(store.Read().Token(), Guid.NewGuid().ToString("N"), now, now.AddMinutes(1)); return 0; }
                    catch (InvalidOperationException ex) { if (ex.Message.Contains("InFlight")) return 3; throw; }
                }
                Run("Fresh files alone never establish recovery trust", StaticSnapshotCannotAuthorize);
                Run("Actual channel progress establishes observation", ActualProgressEstablishes);
                Run("One stalled channel is not hidden by another", OneStalledChannel);
                Run("Legitimate bounded pressure hold is not a stall", BoundedWait);
                Run("Repeated publication cannot extend a stage deadline", MovingDeadlineRejected);
                Run("Permanent channel isolation preserves other channels", PermanentChannelIsolation);
                Run("No eligible channel cannot trigger a restart", NoEligibleChannels);
                Run("Terminal participation remains effective after main exit", TerminalThenExit);
                Run("Snapshot replay cannot refresh business progress", SnapshotReplay);
                Run("DAQ generation change requires new samples, not just a reset", NewSampleGeneration);
                Run("Normal failure admission expires without genuine progress", FirstAdmissionExpires);
                Run("59m59s outage can enter cross-boot admission", ShortOutage);
                Run("60m outage expires and remains expired", ExpiryBoundary);
                Run("Clock rollback cannot establish trust", ClockRollback);
                Run("Operator stop between scan and claim wins", StopBeforeClaim);
                Run("Independent stop survives the notifying process handle", IndependentStopIsPersisted);
                Run("Independent pause blocks recovery and requires a new manual intent", IndependentPauseIsPersisted);
                Run("Stop takes priority over a concurrent pause signal", StopWinsPauseSignal);
                Run("A paused main can still independently revoke its original handles", StopAfterPausePersisted);
                Run("In-process continuation keeps authorization and progress baseline", InProcessContinuation);
                Run("Old watchdog sessions cannot obtain a newer run authorization", WatchdogSessionBinding);
                Run("Legacy permit admission rejects Guard ownership and stale identity", LegacyPermitAdmission);
                Run("First learning legacy permit requires exact live current-boot process", LegacyStartupPermitAdmission);
                Run("Legacy permit mutation excludes Claim but does not delay operator stop", LegacyPermitClaimRace);
                Run("A superseded watchdog cannot borrow the Guard launch epoch", GuardExcludesOldWatchdog);
                Run("Late launch consumption rechecks takeover phase, deadline and lease", LaunchConsumptionPhaseFence);
                Run("A created main waits for Verify and cannot wait past revocation or deadline", RecoveredAdmissionGate);
                Run("Manual launch fencing does not require a running trial", ManualLaunchFence);
                Run("Takeover invalidates an already signed manual launch fence", TakeoverFencesManualLaunch);
                Run("Takeover epoch fences the previous main", EpochFencesMain);
                Run("Reading the new epoch does not re-enable the old main", LatestEpochCannotEnableOldMain);
                Run("Stop cancels an in-flight launch reservation", StopBeforeConsume);
                Run("Stop during Process.Start prevents output", StopAfterConsume);
                Run("Same launch request never consumes twice", DuplicateLaunch);
                Run("Launch preparation atomically reserves and binds before external intent", AtomicLaunchPreparation);
                Run("Expired unconsumed reservations release capacity without reusing an operation", ExpiredReservation);
                Run("Expiry and consumption cannot both win", ReservationExpiryRace);
                Run("Concurrent launch requests yield one reservation", ConcurrentLaunch);
                Run("Different processes share the same launch transaction", CrossProcessLaunch);
                Run("Killed reservation owner cannot create a second launch", InterruptedLaunchOwner);
                Run("Commissioning is bounded and cannot inherit another authorization", CommissioningScope);
                Run("Commissioning expiry fences action dispatch", CommissioningExpiry);
                Run("Late commissioning responses cannot advance a transaction", CommissioningLateAction);
                Run("Latest authority damage cannot revive a backup", CorruptAuthority);
                Run("A commit interrupted after head write fails closed", InterruptedCommit);
                Run("Known authority rollback is rejected", AuthorityRollback);
                Run("Registration retry preserves identity and revocation", RegistrationRetry);
                Run("Missing authority is not silently re-registered", MissingAuthority);
                Run("Continuous cooldown beyond 60m retains transaction", ContinuousCooldown);
                Run("Cooldown deadline cannot be skipped by an eager worker", CooldownCannotBeSkipped);
                Run("A cancellation does not implicitly release hardware ownership", CancelledOwnershipNotReleased);
                Run("Inactive cleanup preserves stop and requires retired actors and launches", InactiveOwnershipCleanup);
                Run("Old transaction cannot continue after 60m observation gap", TransactionOutage);
                Run("Lease expiry does not establish old owner exit", LeaseNotExit);
                Run("Worker retirement is durable, stop-aware and cannot renew or replace a live owner", WorkerRetirementFence);
                Run("Idle worker retirement fences Claim and pulses without renewing trial trust", IdleWorkerRetirementFence);
                Run("Exact Guard retirement uses the same process handle and leaves mismatched identities alive", ExactWorkerRetirement);
                Run("An adopted transaction fences the late old owner", AdoptFencesOwner);
                Run("Adopted retired safety is archived before new safety and preserves stop", AdoptedSafetyReconciliation);
                Run("Repeated worker heartbeat does not extend stage deadline", StageDeadline);
                Run("Process creation is not verified business recovery", NoFalseComplete);
                Run("Recovery requires fresh commits from every eligible channel", VerifiedRecovery);
                Run("Already-completed recovery startup closes only its bound authorization", CompletedBeforeAdmission);
                Run("Maintenance inhibits takeover", Maintenance);
                Run("ObserveOnly never claims", ObserveOnly);
                Run("Stopping Guard does not stop an already authorized main", GuardIndependence);
                Run("Windows boot identity is stable and PID identity exact", RealProcessIdentity);
                Run("Execution engine requires actual commit verification before handback", ExecutionVerifiedHandback);
                Run("Execution engine rejects unrecorded process creation", ExecutionRejectsFalseLaunch);
                Run("Execution engine respects a stop during an action", ExecutionStopDuringAction);
                Run("Execution engine keeps supervised cooldown beyond sixty minutes", ExecutionContinuousCooldown);
                Run("Three hundred failures and fresh heartbeat files preserve business progress origin", RepeatedFailuresPreserveBusinessOrigin);
                Run("Execution engine bounds a pending action and preserves uncertain state", ExecutionActionTimeout);
                Run("ObserveOnly execution engine never dispatches actions", ExecutionObserveOnly);
                Run("Scanner requests only eligible workers and reports lease loss without starting duplicates", WorkerDispatchPolicy);
                Run("Worker scheduling requires enabled installation and exact isolated task definition", WorkerDispatchConfiguration);
                Run("Execution engine adopts only an exited owner and reconciles a new epoch", ExecutionOwnerAdoption);
                Run("Completed handback can be reconciled after owner exit without fencing the main", CompletedOwnerReconciliation);
                Run("Verified running main survives owner exit without another output epoch", VerifiedOwnerReconciliation);
                Run("Orphan Verify requires fresh channel evidence, including bounded hold samples", OrphanVerificationFreshness);
                Run("An owned Verify cannot complete using stale commits and freshly republished files", OwnedVerificationFreshness);
                Run("Orphan Verify observes within one durable window without fencing a recovering main", OrphanVerificationObservation);
                Run("Completed handback reconciliation rejects unproven identities and revoked intent", CompletedOwnerReconciliationRejects);
                Run("Verified completion and ownership handback commit atomically", AtomicCompletedHandback);
                Run("Safety preparation stage reads require current owner, phase, deadline and intent", OwnedStageRead);
                Run("RecoverExited engine never dispatches a stop against a live main", ExecutionExitedMode);
                Run("Supervisor actions persist safety identity before execution", SupervisorActionsPersistIdentity);
                Run("Exited attempt archives actions and fences late preparation without reversing stop", ExitedAttemptRestartsSafely);
                Run("Supervisor actions reject a stop during preparation", SupervisorActionsStopWins);
                Run("Supervisor actions reconcile created main without another launch", SupervisorActionsReconcileLaunch);
                Run("Recovery action binding rejects replacement identities", ActionBindingRejectsReplacement);
                Run("Lost launch response resumes reconciliation after cooldown", LostLaunchResponseResumesStage);
                Run("Action resume cannot bypass cooldown or operator stop", ResumeActionRespectsAdmission);
                Run("Session rollover preserves authorization and requires created main proof", SessionRolloverCheckpointProof);
                Run("Session rollover cannot activate after operator stop", SessionRolloverStopWins);
                Run("Late safety response cannot bind across session rollover", LateSafetyResponseSessionFence);
                Run("Late action completion cannot advance a new session", LateCompletionSessionFence);
                Console.WriteLine("PASS " + _passed + "/" + _passed);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void WorkerDispatchPolicy()
        {
            using (var f = new Fixture())
            {
                var requests = 0;
                var decision = new RecoveryDecision { CanClaim = true };
                var state = f.Store.Read();
                Func<RecoveryWorkerDispatchResult> dispatch = () => RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings,
                    false, f.Now, p => ProcessObservation.Exited, () => requests++);
                Check(dispatch().TaskRequested && requests == 1, "eligible candidate requests executor, not recovery success");
                f.Settings.Mode = RecoveryGuardMode.ObserveOnly;
                Check(!dispatch().TaskRequested && requests == 1, "observation never dispatches");
                f.Settings.Mode = RecoveryGuardMode.RecoverStalled;
                Check(!RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings, true, f.Now,
                    p => ProcessObservation.Exited, () => requests++).TaskRequested, "maintenance takes priority");
                f.Claim(); state = f.Store.Read();
                var owned = RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings, false, f.Now,
                    p => ProcessObservation.ExactAlive, () => requests++);
                Check(owned.Code == "ExecutionWorkerRunning" && requests == 1, "living owner keeps single execution");
                f.Now = new DateTime(state.Transaction.LeaseUntilUtcTicks, DateTimeKind.Utc);
                var expired = RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings, false, f.Now,
                    p => ProcessObservation.ExactAlive, () => requests++);
                Check(expired.NeedsAttention && expired.Code == "ExecutionWorkerLeaseExpired" && requests == 1,
                    "expired living owner is not falsely reported healthy or duplicated");
                var retirements = 0;
                var retired = RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings, false, f.Now,
                    p => ProcessObservation.ExactAlive, () => requests++, tx => { retirements++; return true; });
                Check(retired.Code == "ExecutionWorkerRetired;AwaitingNextDispatch" && !retired.NeedsAttention &&
                    retirements == 1 && requests == 1, "retirement waits for a later dispatch instead of racing IgnoreNew task cleanup");
                Check(RecoveryWorkerDispatch.Coordinate(state, decision, f.Settings, false, f.Now,
                    p => ProcessObservation.Unknown, () => requests++).NeedsAttention && requests == 1,
                    "unknown owner cannot be treated as dead");
                Check(dispatch().TaskRequested && requests == 2, "exited owner can be scheduled after lease expiry");
                state.Intent.DesiredState = RecoveryDesiredState.Stopped;
                Check(!dispatch().TaskRequested && requests == 2, "stop prevents another task request");
            }
        }

        private static void WorkerDispatchConfiguration()
        {
            var directory = Path.Combine(Path.GetTempPath(), "Guard 配置验证");
            var executable = Path.Combine(directory, "MTTFTest.RecoveryGuard.exe");
            var settings = Path.Combine(directory, "guard-settings.json");
            var journal = Path.Combine(directory, "execution-journal");
            var json = new JavaScriptSerializer().Serialize(new { schemaVersion = 2, directory,
                executionTask = RecoveryWorkerDispatch.TaskName, executionEnabled = true });
            RecoveryWorkerDispatch.ValidateRegistration(json, executable);
            Throws(() => RecoveryWorkerDispatch.ValidateRegistration(json.Replace("true", "false"), executable), "InstallationNotEnabled");
            Throws(() => RecoveryWorkerDispatch.ValidateRegistration(json, Path.Combine(directory, "other", "MTTFTest.RecoveryGuard.exe")),
                "InstallationNotEnabled");
            Func<string, string> escape = System.Security.SecurityElement.Escape;
            var xml = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Principals><Principal id=\"System\">" +
                "<UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><Enabled>true</Enabled><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowHardTerminate>false</AllowHardTerminate></Settings>" +
                "<Actions Context=\"System\"><Exec><Command>" + escape(executable) + "</Command><WorkingDirectory>" + escape(directory) +
                "</WorkingDirectory><Arguments>" + escape("--execute --settings \"" + settings + "\" --journal \"" + journal + "\"") +
                "</Arguments></Exec></Actions></Task>";
            RecoveryWorkerDispatch.ValidateTask(xml, executable, settings, journal);
            foreach (var invalid in new[] { xml.Replace("<Enabled>true", "<Enabled>false"), xml.Replace("IgnoreNew", "Parallel"),
                xml.Replace("PT0S", "PT45S"), xml.Replace("--execute", "--check"), xml.Replace("S-1-5-18", "S-1-5-19"),
                xml.Replace("</Actions>", "<Exec><Command>other.exe</Command></Exec></Actions>") })
                Throws(() => RecoveryWorkerDispatch.ValidateTask(invalid, executable, settings, journal), "DefinitionMismatch");
            Throws(() => RecoveryWorkerDispatch.RequestInstalledTask(executable, settings, directory), "RequiresInstallationRoot");
        }
        private static void Check(bool condition, string detail)
        {
            if (!condition) throw new Exception("ASSERT: " + detail);
        }
        private static void Throws(Action action, string contains)
        {
            try { action(); }
            catch (Exception ex)
            {
                if (ex.Message.Contains(contains)) return;
                throw new Exception("Wrong failure, expected " + contains, ex);
            }
            throw new Exception("Expected failure: " + contains);
        }
        private static T Clone<T>(T value) => new JavaScriptSerializer().Deserialize<T>(new JavaScriptSerializer().Serialize(value));

        private sealed class Fixture : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "EPB-RecoveryGuard-" + Guid.NewGuid().ToString("N"));
            public readonly DateTime Start = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
            public DateTime Now;
            public RecoveryControlStore Store;
            public RecoveryGuardSettings Settings = new RecoveryGuardSettings { Mode = RecoveryGuardMode.RecoverStalled };
            public RecoveryAuthorizationToken Token;
            public RecoveryProcessIdentity Main;
            public RecoveryProcessIdentity Owner;
            public RecoveryObservationSnapshot Snapshot;
            public Fixture(bool establish = true)
            {
                Now = Start;
                Store = new RecoveryControlStore(Root);
                Main = new RecoveryProcessIdentity
                {
                    ProcessId = 100, StartUtcTicks = Start.AddMinutes(-1).Ticks,
                    ExecutablePath = Path.Combine(Root, "MTTFTest.exe"), BootId = Guid.NewGuid().ToString("N")
                };
                Owner = new RecoveryProcessIdentity
                {
                    ProcessId = 200, StartUtcTicks = Start.AddMinutes(-2).Ticks,
                    ExecutablePath = Path.Combine(Root, "MTTFTest.RecoveryGuard.exe"), BootId = Main.BootId
                };
                Store.Register("test-bench", Main.ExecutablePath);
                Token = Store.BeginManualRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "config-hash", Main, Now);
                Snapshot = new RecoveryObservationSnapshot
                {
                    Authorization = Token, RunId = Store.Read().Intent.RunId, ConfigurationIdentity = "config-hash",
                    MainProcess = Main, Sequence = 1, PublishedUtcTicks = Now.Ticks, SourceUtcTicks = Now.Ticks,
                    SourceVersion = 1, SourceAvailable = true,
                    Channels = new[] { Channel(1), Channel(2) }
                };
                Observe();
                if (establish) { Tick(60, true); Observe(); }
            }
            private static RecoveryChannelProgress Channel(int id) => new RecoveryChannelProgress
            {
                Channel = id, Eligible = true, SampleSequence = 10, ControlSequence = 10, PersistedSequence = 10,
                Stage = "Formal"
            };
            public void Tick(int seconds, bool progress)
            {
                Now = Now.AddSeconds(seconds);
                Snapshot = Clone(Snapshot);
                Snapshot.Sequence++;
                Snapshot.SourceVersion++;
                Snapshot.PublishedUtcTicks = Snapshot.SourceUtcTicks = Now.Ticks;
                if (progress)
                    foreach (var channel in Snapshot.Channels)
                    {
                        channel.SampleSequence += 10;
                        channel.ControlSequence++;
                        channel.PersistedSequence++;
                    }
            }
            public RecoveryDecision Observe(ProcessObservation process = ProcessObservation.ExactAlive, string boot = null) =>
                Store.Observe(Clone(Snapshot), process, boot ?? Owner.BootId, Now, Settings, false);
            public RecoveryTakeoverTransaction Claim()
            {
                Tick(60, false);
                Observe(ProcessObservation.Exited);
                Tick(60, false);
                Check(Observe(ProcessObservation.Exited).CanClaim, "claim should become ready");
                var transaction = Store.Claim(Clone(Snapshot), ProcessObservation.Exited, Owner, Now, Settings, false);
                Token = Store.Read().Token();
                return transaction;
            }
            public void Advance(RecoveryStage next)
            {
                var current = Store.Read().Transaction;
                Store.Advance(current.TransactionId, current.Epoch, Owner, current.Stage, next, "test evidence", Now, Settings);
            }
            public void ReadyLaunch()
            {
                Claim();
                Advance(RecoveryStage.SafeStop);
                Advance(RecoveryStage.Retire);
                Advance(RecoveryStage.Launch);
            }
            public string Reserve()
            {
                var id = Guid.NewGuid().ToString("N");
                Store.ReserveLaunch(Token, id, Now, Now.AddMinutes(1));
                return id;
            }
            public void Stop() => Store.SetOperatorIntent(Token.AuthorizationId, Token.IntentVersion, RecoveryDesiredState.Stopped, "test stop");
            public void Dispose()
            {
                var absolute = Path.GetFullPath(Root);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("EPB-RecoveryGuard-"))
                    throw new Exception("Unsafe test cleanup path");
                Directory.Delete(absolute, true);
            }
        }

        private static void StaticSnapshotCannotAuthorize()
        {
            using (var f = new Fixture(false))
            {
                for (var i = 0; i < 20; i++) { f.Tick(60, false); Check(!f.Observe().CanClaim, "static bytes are not progress"); }
                Check(!f.Store.Read().Observation.Established, "old Armed-equivalent file cannot establish trust");
            }
        }
        private static void ActualProgressEstablishes()
        {
            using (var f = new Fixture()) Check(f.Store.Read().Observation.Established, "real control/sample progress establishes");
        }
        private static void OneStalledChannel()
        {
            using (var f = new Fixture())
            {
                RecoveryDecision decision = null;
                for (var i = 0; i < 11; i++)
                {
                    f.Tick(60, false);
                    f.Snapshot.Channels[0].SampleSequence++;
                    f.Snapshot.Channels[0].ControlSequence++;
                    decision = f.Observe();
                }
                Check(decision.CanClaim, "channel 2 stall must remain visible");
            }
        }
        private static void BoundedWait()
        {
            using (var f = new Fixture(false))
            {
                foreach (var c in f.Snapshot.Channels)
                {
                    c.Stage = "PressureHold";
                    c.StageStartedUtcTicks = f.Now.Ticks;
                    c.StageDeadlineUtcTicks = f.Now.AddMinutes(30).Ticks;
                }
                // Publish a new stable stage once, then samples advance only.
                f.Tick(60, false); f.Observe();
                for (var i = 0; i < 20; i++)
                {
                    f.Tick(60, false);
                    foreach (var c in f.Snapshot.Channels) c.SampleSequence++;
                    Check(f.Observe().Code == "Healthy", "pressure hold within fixed deadline remains valid");
                }
            }
        }
        private static void MovingDeadlineRejected()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, false);
                f.Snapshot.Channels[0].StageDeadlineUtcTicks = f.Now.AddHours(1).Ticks;
                Check(f.Observe().Code == "ChannelProgressInvalid", "cannot keep changing same stage deadline");
            }
        }
        private static void PermanentChannelIsolation()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, true);
                f.Snapshot.Channels[1].Eligible = false;
                f.Snapshot.Channels[1].PermanentlyIsolated = true;
                Check(f.Observe().Code == "Healthy", "other channel remains eligible");
                f.Tick(60, true);
                f.Snapshot.Channels[1].PermanentlyIsolated = false;
                f.Snapshot.Channels[1].Eligible = true;
                Check(f.Observe().Code == "ChannelProgressInvalid", "Guard cannot clear permanent latch");
            }
        }
        private static void NoEligibleChannels()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, false);
                foreach (var c in f.Snapshot.Channels) { c.Eligible = false; c.PermanentlyIsolated = true; }
                Check(f.Observe().Code == "NoEligibleChannels", "isolation is not completion");
            }
        }
        private static void SnapshotReplay()
        {
            using (var f = new Fixture())
            {
                f.Snapshot.Sequence--;
                Check(f.Observe().Code == "SnapshotRollback", "replayed sequence blocked");
            }
        }
        private static void TerminalThenExit()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, false);
                foreach (var c in f.Snapshot.Channels) { c.Eligible = false; c.Completed = true; }
                Check(f.Observe().Code == "AllCompleted", "completed recorded while main alive");
                f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                f.Tick(60, false);
                Check(f.Observe(ProcessObservation.Exited).Code == "AllCompleted", "cannot fall back to older eligible set");
            }
        }
        private static void FirstAdmissionExpires()
        {
            using (var f = new Fixture())
            {
                for (var i = 0; i < 16; i++) { f.Tick(60, false); f.Observe(); }
                Check(f.Observe().Code == "FirstAdmissionExpired", "Guard own scans do not extend first admission");
            }
        }
        private static void NewSampleGeneration()
        {
            using (var f = new Fixture())
            {
                var baseline = f.Store.Read().Observation.LastTrustedRunUtcTicks;
                f.Tick(60, false);
                foreach (var c in f.Snapshot.Channels) { c.SampleGeneration++; c.SampleSequence = 0; }
                Check(f.Observe().Code == "Healthy", "supported DAQ generation reset is not a rollback");
                Check(f.Store.Read().Observation.LastTrustedRunUtcTicks == baseline, "reset itself is not progress");
                f.Tick(60, true); f.Observe();
                Check(f.Store.Read().Observation.LastTrustedRunUtcTicks > baseline, "new real samples restore trusted progress");
            }
        }
        private static void ShortOutage()
        {
            using (var f = new Fixture())
            {
                var boot = Guid.NewGuid().ToString("N");
                f.Now = f.Now.AddSeconds(3599);
                Check(f.Observe(ProcessObservation.Exited, boot).Code == "Suspect", "must not apply 15m normal admission to reboot");
                f.Now = f.Now.AddSeconds(60);
                Check(f.Observe(ProcessObservation.Exited, boot).Code == "FirstAdmissionExpired", "waiting must not extend 60m admission");
            }
        }
        private static void ExpiryBoundary()
        {
            using (var f = new Fixture())
            {
                f.Tick(3600, true);
                Check(f.Observe(ProcessObservation.Exited, Guid.NewGuid().ToString("N")).Code == "SupervisionExpired", "60m boundary");
                f.Tick(1, true);
                Check(f.Observe().Code == "SupervisionExpired", "new timestamps cannot revive expiration");
            }
        }
        private static void ClockRollback()
        {
            using (var f = new Fixture())
            {
                f.Now = f.Now.AddHours(-1);
                Check(f.Observe().Code == "ClockRollback", "clock rollback is not fresh evidence");
            }
        }
        private static void StopBeforeClaim()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                f.Tick(60, false); Check(f.Observe(ProcessObservation.Exited).CanClaim, "ready");
                f.Stop();
                Throws(() => f.Store.Claim(f.Snapshot, ProcessObservation.Exited, f.Owner, f.Now, f.Settings, false), "IntentStopped");
            }
        }
        private static void EpochFencesMain()
        {
            using (var f = new Fixture())
            {
                var old = f.Token;
                f.Claim();
                Throws(() => f.Store.AssertRunAllowed(old, f.Main, f.Now), "AuthorizationRevoked");
            }
        }
        private static void IndependentStopIsPersisted()
        {
            using (var f = new Fixture())
            {
                using (var signal = new RecoveryRevocationSignal(f.Token))
                {
                    signal.Revoke();
                    Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "IndependentSignal");
                    Check(f.Observe().Code == "IndependentStopObserved", "independent receiver must observe stop");
                }
                Check(f.Store.Read().Intent.DesiredState == RecoveryDesiredState.Stopped, "observed stop must be durable before acknowledgment");
                Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "AuthorizationRevoked");
            }
        }
        private static void IndependentPauseIsPersisted()
        {
            using (var f = new Fixture())
            using (var signal = new RecoveryRevocationSignal(f.Token))
            {
                signal.Pause();
                Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "IndependentSignal");
                Check(f.Observe().Code == "IndependentPauseObserved", "pause must block before disk sender finishes");
                var paused = f.Store.Read();
                Check(paused.Intent.DesiredState == RecoveryDesiredState.Paused, "receiver persists pause");
                Throws(() => f.Store.ContinueInProcess(f.Token, paused.Intent.RootRunId,
                    paused.Intent.RunId, paused.Intent.ConfigurationIdentity, f.Main, f.Now), "AuthorizationRevoked");
                var resumed = f.Store.ContinueManually(paused.Intent.AuthorizationId, paused.Intent.IntentVersion,
                    paused.Intent.RunId, f.Main, f.Now);
                Check(resumed.IntentVersion > paused.Intent.IntentVersion, "manual continue advances intent");
                f.Store.AssertRunAllowed(resumed, f.Main, f.Now);
                Check(!RecoveryRevocationSignal.IsPaused(resumed), "old signaled pause cannot revoke a new intent");
                Throws(() => f.Store.SetOperatorIntent(f.Token.AuthorizationId, f.Token.IntentVersion,
                    RecoveryDesiredState.Paused, "late checkpoint"), "Conflict");
                Check(f.Store.Read().Intent.DesiredState == RecoveryDesiredState.Run, "late pause cannot overwrite continue");
            }
        }

        private static void StopWinsPauseSignal()
        {
            using (var f = new Fixture())
            using (var signal = new RecoveryRevocationSignal(f.Token))
            {
                signal.Pause();
                signal.Revoke();
                Check(f.Observe().Code == "IndependentStopObserved", "stop wins when both notifications pending");
                var stopped = f.Store.Read();
                Throws(() => f.Store.SetOperatorIntent(stopped.Intent.AuthorizationId, stopped.Intent.IntentVersion,
                    RecoveryDesiredState.Paused, "stale pause"), "CannotBecomePaused");
                Throws(() => f.Store.ContinueManually(stopped.Intent.AuthorizationId, stopped.Intent.IntentVersion,
                    stopped.Intent.RunId, f.Main, f.Now), "Conflict");
            }
        }

        private static void StopAfterPausePersisted()
        {
            using (var f = new Fixture())
            using (var signal = new RecoveryRevocationSignal(f.Token))
            {
                signal.Pause();
                f.Observe();
                var paused = f.Store.Read();
                signal.Revoke();
                Throws(() => f.Store.ContinueManually(paused.Intent.AuthorizationId, paused.Intent.IntentVersion,
                    paused.Intent.RunId, f.Main, f.Now), "IndependentStop");
                Check(f.Observe().Code == "IndependentStopObserved", "receiver must retain paused writer signal identity");
                Check(f.Store.Read().Intent.DesiredState == RecoveryDesiredState.Stopped, "stop after persisted pause must survive notification loss");
            }
        }

        private static void InactiveOwnershipCleanup()
        {
            foreach (var expired in new[] { false, true })
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, session, f.Now);
                var tx = f.Claim();
                f.Advance(RecoveryStage.SafeStop);
                var safety = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, safety, null, f.Now);
                f.Advance(RecoveryStage.Retire);
                f.Advance(RecoveryStage.Launch);
                var operation = f.Reserve();
                f.Store.ConsumeLaunch(f.Token, operation, f.Now);
                if (expired) { f.Tick(3600, false); f.Observe(ProcessObservation.Exited); }
                else { f.Stop(); f.Tick(200, false); }
                var intent = f.Store.Read().Token();
                Action<ProcessObservation, ProcessObservation> close = (owner, main) => f.Store.ReconcileInactiveTakeover(
                    intent, tx.TransactionId, tx.Epoch, f.Owner, owner, f.Main, main, safety, f.Owner, ProcessObservation.Exited, f.Now);
                Throws(() => close(ProcessObservation.Unknown, ProcessObservation.Exited), "ProcessEvidenceUnproven");
                Throws(() => close(ProcessObservation.Exited, ProcessObservation.ExactAlive), "ProcessEvidenceUnproven");
                Throws(() => close(ProcessObservation.Exited, ProcessObservation.Exited), "ReconciliationUnavailable");
                Check(!f.Store.Read().Transaction.OwnershipReleased, "unknown launch prevents release");
                f.Store.RecordLaunchResult(operation, f.Main, false);
                Check(f.Store.ConfirmLaunchedProcessExited(operation), "fixture launch exit proven");
                close(ProcessObservation.Exited, ProcessObservation.Exited);
                var closed = f.Store.Read();
                Check(closed.Transaction.OwnershipReleased && closed.Transaction.Stage == RecoveryStage.Cancelled &&
                    closed.Intent.DesiredState == RecoveryDesiredState.Stopped && closed.Launches.Single().State == "Exited",
                    "cleanup closes ownership while preserving stop and launch evidence");
                Check(closed.Intent.AuthorizationId == intent.AuthorizationId &&
                    closed.Intent.IntentVersion == intent.IntentVersion + (expired ? 1 : 0), "cleanup never creates fresh authorization");
                var next = f.Store.BeginManualRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "config-hash", f.Main, f.Now);
                Throws(() => close(ProcessObservation.Exited, ProcessObservation.Exited), "ReconciliationUnavailable");
                Check(f.Store.Read().Matches(next), "late cleanup cannot modify a new manual run");
            }
        }

        private static void ReservationExpiryRace()
        {
            using (var f = new Fixture())
            using (var start = new ManualResetEventSlim())
            {
                var operation = f.Reserve();
                var consumed = false;
                var expired = false;
                var consumption = Task.Run(() =>
                {
                    start.Wait();
                    try { f.Store.ConsumeLaunch(f.Token, operation, f.Now); consumed = true; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("ConsumedExpiredOrMissing")) { }
                });
                var expiration = Task.Run(() =>
                {
                    start.Wait();
                    expired = f.Store.ConfirmReservationExpiredBeforeConsumption(operation, f.Now.AddMinutes(1));
                });
                start.Set();
                Task.WaitAll(consumption, expiration);
                Check(consumed != expired, "shared mutex allows exactly one transition");
                var record = f.Store.Read().Launches.Single();
                Check(record.State == (consumed ? "Consumed" : "StartFailed"), "durable result agrees with winner");
                if (consumed) Check(!f.Store.ConfirmReservationExpiredBeforeConsumption(operation, f.Now.AddHours(2)),
                    "unknown consumed result cannot expire into no-start proof");
                else Check(record.FailureEvidence == "ReservationExpiredBeforeConsumption", "failure records precise proof");
            }
        }

        private static void ExpiredReservation()
        {
            using (var f = new Fixture())
            {
                var old = Guid.NewGuid().ToString("N");
                f.Store.ReserveLaunch(f.Token, old, f.Now, f.Now.AddSeconds(10));
                f.Now = f.Now.AddSeconds(11);
                var current = Guid.NewGuid().ToString("N");
                f.Store.ReserveLaunch(f.Token, current, f.Now, f.Now.AddMinutes(1));
                Throws(() => f.Store.ConsumeLaunch(f.Token, old, f.Now), "ConsumedExpiredOrMissing");
                f.Store.ConsumeLaunch(f.Token, current, f.Now);
                f.Store.RecordLaunchResult(current, f.Main, false);
                f.Store.RecordLaunchResult(current, f.Main, false);
                Throws(() => f.Store.RecordLaunchResult(current, f.Owner, false), "NotConsumed");
                Check(f.Store.Read().Launches.Single(l => l.OperationId == old).State == "StartFailed", "only never-consumed expiry releases capacity");
            }
        }

        private static void RecoveredAdmissionGate()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var operation = f.Reserve();
                f.Store.ConsumeLaunch(f.Token, operation, f.Now);
                var next = Clone(f.Main); next.ProcessId++;
                f.Store.RecordLaunchResult(operation, next, false);
                f.Store.BindRecoveredRun(f.Token, operation, Guid.NewGuid().ToString("N"), next, f.Now);
                Check(!f.Store.IsRecoveredRunReadyForAdmission(f.Token, operation, next, f.Now), "Launch is not output admission");
                Throws(() => f.Store.IsRecoveredRunReadyForAdmission(f.Token, operation, f.Main, f.Now), "ProcessMismatch");
                f.Advance(RecoveryStage.Verify);
                Check(f.Store.IsRecoveredRunReadyForAdmission(f.Token, operation, next, f.Now), "Verify admits the bound new process");
                var deadline = new DateTime(f.Store.Read().Transaction.StageDeadlineUtcTicks, DateTimeKind.Utc);
                Throws(() => f.Store.IsRecoveredRunReadyForAdmission(f.Token, operation, next, deadline), "StageOrLeaseExpired");
                f.Stop();
                Throws(() => f.Store.IsRecoveredRunReadyForAdmission(f.Token, operation, next, f.Now), "Revoked");
            }
        }

        private static void LaunchConsumptionPhaseFence()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var id = f.Reserve();
                f.Advance(RecoveryStage.Cooldown);
                Throws(() => f.Store.ConsumeLaunch(f.Token, id, f.Now), "StageNotReady");
                Check(f.Store.Read().Launches.Single().State == "Reserved", "late request must remain unconsumed");
            }
            using (var f = new Fixture())
            {
                f.Settings.StageTimeoutSeconds = 30;
                f.ReadyLaunch();
                var id = Guid.NewGuid().ToString("N");
                f.Store.ReserveLaunch(f.Token, id, f.Now, f.Now.AddMinutes(2));
                var transaction = f.Store.Read().Transaction;
                f.Now = new DateTime(transaction.StageDeadlineUtcTicks, DateTimeKind.Utc);
                Throws(() => f.Store.CaptureGuardLaunchFence(transaction.TransactionId, transaction.Epoch, f.Owner, f.Now), "StageOrLeaseExpired");
                Throws(() => f.Store.ConsumeLaunch(f.Token, id, f.Now), "StageOrLeaseExpired");
            }
            using (var f = new Fixture())
            {
                f.Settings.StageTimeoutSeconds = 300;
                f.Settings.OwnerLeaseSeconds = 120;
                f.ReadyLaunch();
                f.Now = f.Now.AddSeconds(60);
                var id = Guid.NewGuid().ToString("N");
                f.Store.ReserveLaunch(f.Token, id, f.Now, f.Now.AddMinutes(2));
                f.Now = new DateTime(f.Store.Read().Transaction.LeaseUntilUtcTicks, DateTimeKind.Utc);
                Throws(() => f.Store.ConsumeLaunch(f.Token, id, f.Now), "StageOrLeaseExpired");
            }
        }

        private static void GuardExcludesOldWatchdog()
        {
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Store.Read().Intent.RunId, f.Main, session, f.Now);
                f.ReadyLaunch();
                Throws(() => f.Store.CaptureLaunchFence(true, session, f.Now), "SupersededByGuard");
                var takeover = f.Store.Read().Transaction;
                var fence = f.Store.CaptureGuardLaunchFence(takeover.TransactionId, takeover.Epoch, f.Owner, f.Now);
                Check(f.Store.Read().Matches(fence.Authorization), "only current owner may obtain Guard launch authorization");
                Throws(() => f.Store.CaptureGuardLaunchFence(takeover.TransactionId, takeover.Epoch, f.Main, f.Now), "OwnerFenced");
                Throws(() => f.Store.ReleaseCompletedTakeover(takeover.TransactionId, takeover.Epoch, f.Owner), "EvidenceIncomplete");
            }
        }

        private static void ManualLaunchFence()
        {
            using (var f = new Fixture())
            {
                f.Stop();
                var fence = f.Store.CaptureLaunchFence(false, null, f.Now);
                f.Store.AssertLaunchFence(fence, f.Now);
                Check(!fence.IsRecovery, "manual UI admission cannot grant recovery");
                var forged = Clone(fence);
                forged.IsRecovery = true;
                Throws(() => f.Store.AssertLaunchFence(forged, f.Now), "AuthorizationRevoked");
                forged = Clone(fence);
                forged.Authorization.InstallationId = Guid.NewGuid().ToString("N");
                Throws(() => f.Store.AssertLaunchFence(forged, f.Now), "FenceStale");
            }
        }

        private static void TakeoverFencesManualLaunch()
        {
            using (var f = new Fixture())
            {
                var fence = f.Store.CaptureLaunchFence(false, null, f.Now);
                f.Claim();
                Throws(() => f.Store.AssertLaunchFence(fence, f.Now), "FenceStale");
                Throws(() => f.Store.CaptureLaunchFence(false, null, f.Now), "TakeoverInProgress");
                f.Stop();
                Throws(() => f.Store.CaptureLaunchFence(false, null, f.Now), "TakeoverInProgress");
            }
        }

        private static void LegacyStartupPermitAdmission()
        {
            using (var f = new Fixture(false))
            {
                var now = DateTime.UtcNow;
                var process = RecoveryProcessProbe.Current();
                var run = Guid.NewGuid().ToString("N");
                var session = Guid.NewGuid().ToString("N");
                // Isolated store: no production registration or process manipulation.
                f.Store = new RecoveryControlStore(Path.Combine(f.Root, "startup"));
                f.Store.Register("startup-test", process.ExecutablePath);
                var token = f.Store.BeginManualRun(run, run, "config-hash", process, now);
                f.Store.BindWatchdogSession(token, run, process, session, now);
                var calls = 0;
                Func<int> mutation = () => ++calls;
                Check(f.Store.Read().Observation?.Established != true, "Guard has not established observation");
                Check(f.Store.RunLegacyRecoveryAuthorityMutation(session, run, now, mutation) == 1,
                    "first learning failure cannot reach existing Watchdog");
                Check(f.Store.Read().Observation?.Established != true, "legacy permit must not establish Guard trust");
                // Reopen the durable store and exercise the same Supervisor/Agent
                // reservation, consumption and main-admission path after approval.
                f.Store = new RecoveryControlStore(Path.Combine(f.Root, "startup"));
                var fence = f.Store.CaptureLaunchFence(true, session, now);
                var operation = Guid.NewGuid().ToString("N");
                f.Store.ReserveLaunch(token, operation, now, now.AddMinutes(1));
                f.Store.ConsumeLaunch(token, operation, now);
                f.Store.RecordLaunchResult(operation, process, false);
                f.Store.AssertStartedLaunch(fence, operation, process, now);
                f.Store.BindRecoveredRun(token, operation, run, process, now);
                Check(f.Store.IsRecoveredRunReadyForAdmission(token, operation, process, now),
                    "approved first-learning restart cannot reach main admission");
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, run, now.AddMinutes(60), mutation), "Expired");
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, Guid.NewGuid().ToString("N"), now, mutation), "Superseded");
                f.Store.SetOperatorIntent(token.AuthorizationId, token.IntentVersion, RecoveryDesiredState.Stopped, "stop");
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, run, now, mutation), "Revoked");
                Check(calls == 1, "rejected requests entered authority mutation");
                process.StartUtcTicks--;
                var nextRun = Guid.NewGuid().ToString("N");
                var next = f.Store.BeginManualRun(nextRun, nextRun, "config-hash", process, now);
                f.Store.BindWatchdogSession(next, nextRun, process, session, now);
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, nextRun, now, mutation), "Unproven");
                Check(calls == 1, "stale PID identity obtained a new startup admission");
            }
        }

        private static void LegacyPermitAdmission()
        {
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                var run = f.Store.Read().Intent.RunId;
                f.Store.BindWatchdogSession(f.Token, run, f.Main, session, f.Now);
                var calls = 0;
                Func<int> write = () => ++calls;
                Check(f.Store.RunLegacyRecoveryAuthorityMutation(session, run, f.Now, write) == 1,
                    "current legacy writer admitted");
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, Guid.NewGuid().ToString("N"), f.Now, write), "Superseded");
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(Guid.NewGuid().ToString("N"), run, f.Now, write), "Superseded");
                f.Claim();
                Throws(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, run, f.Now, write), "SupersededByGuard");
                Check(calls == 1, "denied writers must not enter strict authority");
            }
        }

        private static void LegacyPermitClaimRace()
        {
            using (var f = new Fixture())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var session = Guid.NewGuid().ToString("N");
                var run = f.Store.Read().Intent.RunId;
                f.Store.BindWatchdogSession(f.Token, run, f.Main, session, f.Now);
                f.Tick(60, false);
                f.Observe(ProcessObservation.Exited);
                f.Tick(60, false);
                Check(f.Observe(ProcessObservation.Exited).CanClaim, "prepare eligible claim");
                var writer = Task.Run(() => f.Store.RunLegacyRecoveryAuthorityMutation(session, run, f.Now, () =>
                {
                    entered.Set();
                    if (!release.Wait(10000)) throw new Exception("test writer release timed out");
                    return true;
                }));
                try
                {
                    Check(entered.Wait(3000), "writer entered");
                    var contender = new RecoveryControlStore(f.Root);
                    Throws(() => contender.Claim(f.Snapshot, ProcessObservation.Exited, f.Owner, f.Now, f.Settings, false), "RecoveryAuthorityBusy");
                    Check(f.Store.Read().Transaction == null, "claim cannot pass admitted permit writer");
                    var stop = Task.Run(() => f.Stop());
                    Check(stop.Wait(2000), "operator stop does not wait for permit I/O");
                }
                finally { release.Set(); writer.GetAwaiter().GetResult(); }
                Throws(() => f.Store.Claim(f.Snapshot, ProcessObservation.Exited, f.Owner, f.Now, f.Settings, false), "ClaimDenied");
            }
        }

        private static void WatchdogSessionBinding()
        {
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                var run = f.Store.Read().Intent.RunId;
                f.Store.BindWatchdogSession(f.Token, run, f.Main, session, f.Now);
                Check(f.Store.Read().Matches(f.Store.AuthorizeWatchdogSession(session, f.Now)), "bound session may request current authorization");
                Throws(() => f.Store.BindWatchdogSession(f.Token, Guid.NewGuid().ToString("N"), f.Main,
                    session, f.Now), "WriterMismatch");
                Throws(() => f.Store.BindWatchdogSession(f.Token, run, f.Owner,
                    session, f.Now), "WriterMismatch");
                f.Stop();
                Throws(() => f.Store.AuthorizeWatchdogSession(session, f.Now), "AuthorizationRevoked");
                var current = f.Store.Read();
                var next = f.Store.BeginManualRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    current.Intent.ConfigurationIdentity, f.Main, f.Now);
                Throws(() => f.Store.AuthorizeWatchdogSession(session, f.Now), "Superseded");
                Throws(() => f.Store.BindWatchdogSession(f.Token, run, f.Main, session, f.Now), "AuthorizationRevoked");
                Check(f.Store.Read().Matches(next), "rejected old session cannot mutate new authorization");
            }
        }

        private static void InProcessContinuation()
        {
            using (var f = new Fixture())
            {
                var state = f.Store.Read();
                f.Store.ContinueInProcess(f.Token, state.Intent.RootRunId, Guid.NewGuid().ToString("N"),
                    state.Intent.ConfigurationIdentity, f.Main, f.Now);
                var continued = f.Store.Read();
                Check(continued.Intent.AuthorizationId == state.Intent.AuthorizationId && continued.Intent.IntentVersion == state.Intent.IntentVersion,
                    "automatic continuation cannot invent a manual authorization");
                Check(continued.Observation.LastVerifiedBusinessCommitUtcTicks == state.Observation.LastVerifiedBusinessCommitUtcTicks,
                    "continuation cannot reset failed-business baseline");
                f.Stop();
                Throws(() => f.Store.ContinueInProcess(f.Token, state.Intent.RootRunId, Guid.NewGuid().ToString("N"),
                    state.Intent.ConfigurationIdentity, f.Main, f.Now), "AuthorizationRevoked");
            }
        }
        private static void StopBeforeConsume()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch(); var id = f.Reserve(); f.Stop();
                Throws(() => f.Store.ConsumeLaunch(f.Token, id, f.Now), "AuthorizationRevoked");
            }
        }
        private static void LatestEpochCannotEnableOldMain()
        {
            using (var f = new Fixture())
            {
                f.Claim();
                Throws(() => f.Store.AssertRunAllowed(f.Store.Read().Token(), f.Main, f.Now), "OutputTakeoverFenced");
            }
        }
        private static void StopAfterConsume()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch(); var id = f.Reserve(); f.Store.ConsumeLaunch(f.Token, id, f.Now); f.Stop();
                var next = Clone(f.Main); next.ProcessId++;
                f.Store.RecordLaunchResult(id, next, false);
                Check(f.Store.Read().Launches.Single().State == "Started", "creation result still recorded after stop");
                Throws(() => f.Store.BindRecoveredRun(f.Token, id, Guid.NewGuid().ToString("N"), next, f.Now), "AuthorizationRevoked");
                Throws(() => f.Store.AssertRunAllowed(f.Token, next, f.Now), "AuthorizationRevoked");
            }
        }
        private static void DuplicateLaunch()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch(); var id = f.Reserve(); f.Store.ConsumeLaunch(f.Token, id, f.Now);
                Check(f.Store.ReserveLaunch(f.Token, id, f.Now, f.Now.AddMinutes(1)).State == "Consumed", "reconcile old operation");
                Throws(() => f.Store.ConsumeLaunch(f.Token, id, f.Now), "ConsumedExpiredOrMissing");
            }
        }
        private static void ConcurrentLaunch()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch(); var successes = 0;
                Parallel.For(0, 8, i =>
                {
                    try { f.Reserve(); Interlocked.Increment(ref successes); }
                    catch (InvalidOperationException ex) { if (!ex.Message.Contains("InFlight")) throw; }
                });
                Check(successes == 1, "one cross-writer reservation");
            }
        }
        private static void CorruptAuthority()
        {
            using (var f = new Fixture())
            {
                var path = Path.Combine(f.Root, "control-state.json");
                File.Copy(path, path + ".bak");
                f.Stop();
                File.WriteAllText(path, "{broken");
                Throws(() => f.Store.Read(), "Invalid");
            }
        }
        private static void CommissioningScope()
        {
            using (var f = new Fixture())
            {
                var state = f.Store.Read();
                var scope = new RecoveryCommissioningScope(state.InstallationId, state.Intent.AuthorizationId,
                    state.Intent.IntentVersion, f.Now, f.Now.AddMinutes(15), RecoveryGuardMode.RecoverExited);
                scope.Demand(state, f.Now);
                scope.Demand(state, f.Now.AddMinutes(15).AddTicks(-1));
                Throws(() => scope.Demand(state, f.Now.AddMinutes(15)), "ExpiredOrClockReversed");
                Throws(() => scope.Demand(state, f.Now.AddTicks(-1)), "ExpiredOrClockReversed");
                Throws(() => new RecoveryCommissioningScope(state.InstallationId, state.Intent.AuthorizationId,
                    state.Intent.IntentVersion, f.Now, f.Now.AddMinutes(16), RecoveryGuardMode.RecoverExited), "Invalid");
                Throws(() => new RecoveryCommissioningScope(state.InstallationId, state.Intent.AuthorizationId,
                    state.Intent.IntentVersion, f.Now, f.Now.AddMinutes(1), RecoveryGuardMode.RecoverStalled), "Invalid");
                var other = Clone(state);
                other.Intent.AuthorizationId = Guid.NewGuid().ToString("N");
                Throws(() => scope.Demand(other, f.Now), "Revoked");
                other = Clone(state); other.InstallationId = Guid.NewGuid().ToString("N");
                Throws(() => scope.Demand(other, f.Now), "Revoked");
                other = Clone(state); other.Intent.IntentVersion++;
                Throws(() => scope.Demand(other, f.Now), "Revoked");
                f.Stop();
                Throws(() => scope.Demand(f.Store.Read(), f.Now), "Revoked");
            }
        }

        private static void CommissioningExpiry()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                f.Settings.Mode = RecoveryGuardMode.RecoverExited;
                var state = f.Store.Read();
                var scope = new RecoveryCommissioningScope(state.InstallationId, state.Intent.AuthorizationId,
                    state.Intent.IntentVersion, f.Now, f.Now.AddSeconds(1), f.Settings.Mode);
                var actions = new ExecutionActions { Handler = (context, token) => throw new Exception("expired commissioning dispatched") };
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, f.Owner, actions, () => f.Now,
                    process => ProcessObservation.Exited, () => { f.Now = f.Now.AddSeconds(1); return false; }, scope);
                Throws(() => Step(engine), "ExpiredOrClockReversed");
                Check(actions.Calls == 0, "expired scope dispatched to Supervisor");
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.SafeStop,
                    "expiry must retain unresolved transaction rather than report successful recovery");
            }
        }

        private static void CommissioningLateAction()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                f.Settings.Mode = RecoveryGuardMode.RecoverExited;
                var state = f.Store.Read();
                var scope = new RecoveryCommissioningScope(state.InstallationId, state.Intent.AuthorizationId,
                    state.Intent.IntentVersion, f.Now, f.Now.AddSeconds(1), f.Settings.Mode);
                var actions = new ExecutionActions { Handler = (context, token) =>
                {
                    f.Now = f.Now.AddSeconds(1);
                    return Task.FromResult(new RecoveryActionResult
                    { Outcome = RecoveryActionOutcome.Completed, Evidence = "late fixture response" });
                } };
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, f.Owner, actions, () => f.Now,
                    process => ProcessObservation.Exited, () => false, scope);
                Throws(() => Step(engine), "ExpiredOrClockReversed");
                Check(actions.Calls == 1 && f.Store.Read().Transaction.Stage == RecoveryStage.SafeStop,
                    "late response advanced expired commissioning");
            }
        }

        private static void InterruptedLaunchOwner()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                using (var child = Process.Start(new ProcessStartInfo
                {
                    FileName = typeof(Program).Assembly.Location,
                    Arguments = "--reserve-and-wait \"" + f.Root + "\" " + f.Now.Ticks,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    try
                    {
                        var ready = Path.Combine(f.Root, "reserved.ready");
                        Check(SpinWait.SpinUntil(() => File.Exists(ready) || child.HasExited, 15000) &&
                              File.Exists(ready) && !child.HasExited, "reservation child did not commit");
                        child.Kill();
                        Check(child.WaitForExit(3000), "test child failed to exit");
                        var reopened = new RecoveryControlStore(f.Root);
                        Check(reopened.Read().Launches.Count == 1, "committed launch lost with process");
                        Throws(() => reopened.ReserveLaunch(reopened.Read().Token(), Guid.NewGuid().ToString("N"),
                            f.Now, f.Now.AddMinutes(1)), "InFlight");
                        Check(reopened.Read().Launches.Count == 1, "interrupted owner caused duplicate launch");
                        var reserved = reopened.Read().Launches.Single();
                        f.Stop();
                        var afterStop = new RecoveryControlStore(f.Root);
                        Check(afterStop.Read().Intent.DesiredState == RecoveryDesiredState.Stopped,
                            "operator stop lost after reopening authority");
                        Throws(() => afterStop.ConsumeLaunch(reserved.Authorization, reserved.OperationId, f.Now),
                            "Revoked");
                        Check(afterStop.Read().Launches.All(item => item.State != "Consumed"),
                            "late owner consumed launch after operator stop");
                    }
                    finally
                    {
                        if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                    }
                }
            }
        }

        private static void CrossProcessLaunch()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var children = new List<Process>();
                try
                {
                    for (var i = 0; i < 4; i++)
                        children.Add(Process.Start(new ProcessStartInfo
                        {
                            FileName = typeof(Program).Assembly.Location,
                            Arguments = "--race-reserve \"" + f.Root + "\" " + f.Now.Ticks,
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                        }));
                    var winners = 0;
                    foreach (var child in children)
                    {
                        Check(child.WaitForExit(15000), "child transaction timeout");
                        if (child.ExitCode == 0) winners++;
                        else Check(child.ExitCode == 3, "loser must report in-flight, not corrupt state");
                    }
                    Check(winners == 1 && f.Store.Read().Launches.Count == 1, "single reservation across processes");
                }
                finally
                {
                    foreach (var child in children)
                    {
                        if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                        child.Dispose();
                    }
                }
            }
        }
        private static void InterruptedCommit()
        {
            using (var f = new Fixture())
            {
                var path = Path.Combine(f.Root, "commit-head.json");
                var json = new JavaScriptSerializer();
                var head = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                head["Revision"] = Convert.ToInt64(head["Revision"]) + 1;
                File.WriteAllText(path, json.Serialize(head));
                Throws(() => f.Store.Read(), "CommitUncertain");
            }
        }
        private static void AuthorityRollback()
        {
            using (var f = new Fixture())
            {
                var path = Path.Combine(f.Root, "control-state.json");
                var old = File.ReadAllText(path); f.Stop(); File.WriteAllText(path, old);
                Throws(() => f.Store.Read(), "CommitUncertain");
            }
        }
        private static void RegistrationRetry()
        {
            using (var f = new Fixture())
            {
                f.Stop(); var before = f.Store.Read();
                var after = f.Store.Register("test-bench", f.Main.ExecutablePath);
                Check(after.InstallationId == before.InstallationId && after.Revision == before.Revision &&
                    after.Intent.DesiredState == RecoveryDesiredState.Stopped, "register is idempotent, not rearm");
            }
        }
        private static void MissingAuthority()
        {
            using (var f = new Fixture())
            {
                File.Delete(Path.Combine(f.Root, "control-state.json"));
                File.Delete(Path.Combine(f.Root, "commit-head.json"));
                try { f.Store.Register("test-bench", f.Main.ExecutablePath); }
                catch (FileNotFoundException) { return; }
                throw new Exception("Cannot replace missing authority with a fresh installation");
            }
        }
        private static void ContinuousCooldown()
        {
            using (var f = new Fixture())
            {
                f.Claim(); var baseline = f.Store.Read().Observation.LastVerifiedBusinessCommitUtcTicks;
                f.Advance(RecoveryStage.Cooldown);
                for (var i = 0; i < 75; i++)
                {
                    f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                    var t = f.Store.Read().Transaction;
                    f.Store.RenewOwner(t.TransactionId, t.Epoch, f.Owner, f.Now, f.Settings);
                }
                Check(f.Observe(ProcessObservation.Exited).CanContinue, "75min supervised failure is not expired");
                Check(f.Store.Read().Observation.LastVerifiedBusinessCommitUtcTicks == baseline, "retry is not business progress");
            }
        }
        private static void TransactionOutage()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.Cooldown); f.Tick(3600, false);
                Check(f.Observe(ProcessObservation.Exited).Code == "SupervisionExpired", "transaction also expires");
                Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "SupervisionExpired");
            }
        }
        private static void CooldownCannotBeSkipped()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.Cooldown);
                Throws(() => f.Advance(RecoveryStage.Claim), "CooldownNotElapsed");
            }
        }
        private static void CancelledOwnershipNotReleased()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Stop();
                Throws(() => f.Store.BeginManualRun(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    "config-hash", f.Main, f.Now), "TakeoverMustBeClosed");
            }
        }
        private static void LeaseNotExit()
        {
            using (var f = new Fixture())
            {
                var t = f.Claim(); f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                Throws(() => f.Store.Adopt(t.TransactionId, t.Epoch, f.Owner, ProcessObservation.ExactAlive, f.Now, f.Settings), "OldOwnerExitUnproven");
            }
        }

        private static void WorkerRetirementFence()
        {
            foreach (var stop in new[] { false, true })
            using (var f = new Fixture())
            {
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var safety = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, safety, null, f.Now);
                Action request = () => f.Store.RequestExpiredWorkerRetirement(tx.TransactionId, tx.Epoch, f.Owner, f.Now);
                Throws(request, "RetirementUnavailable");
                f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                request();
                var marked = f.Store.Read();
                request();
                Check(f.Store.Read().Revision == marked.Revision && marked.Transaction.WorkerRetirementRequestedUtcTicks == f.Now.Ticks &&
                    marked.Transaction.SafetyAuthorityId == safety && marked.LastTakeoverEpoch == tx.Epoch,
                    "retirement is idempotent and preserves old action and epoch for reconciliation");
                Throws(() => f.Store.RenewOwner(tx.TransactionId, tx.Epoch, f.Owner, f.Now.AddSeconds(-2), f.Settings), "OwnerFenced");
                var replacement = Clone(f.Owner); replacement.ProcessId++;
                Throws(() => f.Store.Adopt(tx.TransactionId, tx.Epoch, replacement, ProcessObservation.ExactAlive, f.Now, f.Settings),
                    "OldOwnerExitUnproven");
                if (stop)
                {
                    f.Stop(); Throws(request, "Revoked");
                    Throws(() => f.Store.AssertWorkerRetirement(tx.TransactionId, tx.Epoch, f.Owner, f.Now), "Revoked");
                    continue;
                }
                f.Store.AssertWorkerRetirement(tx.TransactionId, tx.Epoch, f.Owner, f.Now);
                var adopted = f.Store.Adopt(tx.TransactionId, tx.Epoch, replacement, ProcessObservation.Exited, f.Now, f.Settings);
                Check(adopted.WorkerRetirementRequestedUtcTicks == 0 && adopted.Epoch > tx.Epoch && adopted.ActionEpoch == tx.Epoch,
                    "new owner clears only retirement marker and must reconcile old action");
                Throws(() => f.Store.AssertWorkerRetirement(tx.TransactionId, tx.Epoch, f.Owner, f.Now), "RetirementUnavailable");
            }
        }

        private static void ExactWorkerRetirement()
        {
            using (var f = new Fixture())
            {
                var executable = typeof(RecoveryExecutionEngine).Assembly.Location;
                var mutexName = (string)typeof(RecoveryControlStore).GetField("_mutexName",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Store);
                using (var gate = new Mutex(false, mutexName))
                {
                    Check(gate.WaitOne(1000), "hold isolated authority while child opens status");
                    Process child = null;
                    try
                    {
                        child = Process.Start(new ProcessStartInfo(executable, "--status --root \"" + f.Root + "\"")
                            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                              RedirectStandardOutput = true, RedirectStandardError = true });
                        var owner = new RecoveryProcessIdentity { ProcessId = child.Id,
                            StartUtcTicks = child.StartTime.ToUniversalTime().Ticks, ExecutablePath = executable,
                            BootId = RecoveryProcessProbe.ReadBootId() };
                        var wrong = Clone(owner); wrong.StartUtcTicks--;
                        var callbacks = 0;
                        Check(RecoveryWorkerRetirement.RetireExactProcess(wrong, executable, () => callbacks++) && !child.HasExited && callbacks == 0,
                            "different start identity leaves current process untouched");
                        Throws(() => RecoveryWorkerRetirement.RetireExactProcess(owner, executable,
                            () => { throw new InvalidOperationException("operator stop before terminate"); }), "operator stop");
                        Check(!child.HasExited, "last authority check can prevent termination");
                        Check(RecoveryWorkerRetirement.RetireExactProcess(owner, executable, () => callbacks++) && child.WaitForExit(2000) && callbacks == 1,
                            "exact isolated Guard process exits after final authority check");
                        Throws(() => RecoveryWorkerRetirement.RetireExactProcess(RecoveryProcessProbe.Current(), executable, () => { }), "TargetInvalid");
                    }
                    finally
                    {
                        if (child != null) { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } child.Dispose(); }
                        gate.ReleaseMutex();
                    }
                }
            }
        }
        private static void AdoptFencesOwner()
        {
            using (var f = new Fixture())
            {
                var t = f.Claim(); var next = Clone(f.Owner); next.ProcessId++;
                f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                var adopted = f.Store.Adopt(t.TransactionId, t.Epoch, next, ProcessObservation.Exited, f.Now, f.Settings);
                Check(adopted.Stage == t.Stage && adopted.LastProgressUtcTicks == t.LastProgressUtcTicks, "adoption preserves actual stage");
                Throws(() => f.Store.RenewOwner(t.TransactionId, t.Epoch, f.Owner, f.Now, f.Settings), "OwnerFenced");
            }
        }

        private static void AdoptedSafetyReconciliation()
        {
            foreach (var stop in new[] { false, true })
            using (var f = new Fixture())
            {
                f.Settings.CooldownSeconds = 60;
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var safetyId = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop,
                    safetyId, null, f.Now);
                var oldOwner = Clone(f.Owner);
                f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                f.Owner = Clone(oldOwner); f.Owner.ProcessId++;
                var adopted = f.Store.Adopt(tx.TransactionId, tx.Epoch, f.Owner, ProcessObservation.Exited, f.Now, f.Settings);
                f.Advance(RecoveryStage.Cooldown);
                f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                f.Advance(RecoveryStage.Claim); f.Advance(RecoveryStage.SafeStop);
                var before = f.Store.Read();
                var worker = Clone(oldOwner); worker.ProcessId += 10;
                Action reconcile = () => f.Store.ReconcileAdoptedSafetyAction(tx.TransactionId, adopted.Epoch, f.Owner,
                    before.Intent.WatchdogSessionId, tx.Epoch, safetyId, f.Main, ProcessObservation.Exited,
                    worker, ProcessObservation.Exited, f.Now);
                Throws(() => f.Store.ReconcileAdoptedSafetyAction(tx.TransactionId, adopted.Epoch, f.Owner,
                    before.Intent.WatchdogSessionId, tx.Epoch, safetyId, f.Main, ProcessObservation.ExactAlive,
                    worker, ProcessObservation.Exited, f.Now), "ExitUnproven");
                Throws(() => f.Store.ReconcileAdoptedSafetyAction(tx.TransactionId, adopted.Epoch, f.Owner,
                    before.Intent.WatchdogSessionId, tx.Epoch, safetyId, f.Main, ProcessObservation.Exited,
                    worker, ProcessObservation.Unknown, f.Now), "ExitUnproven");
                Throws(() => f.Store.ReconcileAdoptedSafetyAction(tx.TransactionId, adopted.Epoch, f.Owner,
                    before.Intent.WatchdogSessionId, adopted.Epoch, safetyId, f.Main, ProcessObservation.Exited,
                    worker, ProcessObservation.Exited, f.Now), "ReconciliationUnavailable");
                if (stop)
                {
                    f.Stop(); Throws(reconcile, "Revoked");
                    Check(f.Store.Read().Transaction.SafetyAuthorityId == safetyId, "stop preserves the prior action for cleanup");
                    continue;
                }
                var transport = new ActionTransport { OnPrepare = reconcile };
                var result = ProductionActions(f, transport).ExecuteAsync(before, CancellationToken.None).GetAwaiter().GetResult();
                var after = f.Store.Read();
                Check(result.Outcome == RecoveryActionOutcome.Pending && transport.PrepareCalls == 1 && transport.ExecuteCalls == 0 &&
                    transport.LaunchCalls == 0 && after.Transaction.ActionEpoch == 0 && after.Transaction.SafetyAuthorityId == null,
                    "adapter reconciles old action without executing it or claiming safety success");
                Check(after.LastTakeoverEpoch == adopted.Epoch && after.Intent.AuthorizationId == before.Intent.AuthorizationId &&
                    after.Observation.LastVerifiedBusinessCommitUtcTicks == before.Observation.LastVerifiedBusinessCommitUtcTicks &&
                    Directory.GetFiles(Path.Combine(f.Root, "action-history"), "*.adopted-safety.json").Length == 1,
                    "archive preserves authorization, adopted epoch and business clocks");
                Throws(() => f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, oldOwner,
                    RecoveryStage.SafeStop, safetyId, null, f.Now), "OwnerFenced");
                var prepared = ProductionActions(f, new ActionTransport()).ExecuteAsync(after, CancellationToken.None).GetAwaiter().GetResult();
                Check(prepared.Outcome == RecoveryActionOutcome.Pending && f.Store.Read().Transaction.ActionEpoch == adopted.Epoch,
                    "a new safety authority must be prepared and bound to the adopted epoch");
            }
        }
        private static void StageDeadline()
        {
            using (var f = new Fixture())
            {
                var t = f.Claim(); f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                f.Store.RenewOwner(t.TransactionId, t.Epoch, f.Owner, f.Now, f.Settings);
                f.Tick(61, false); f.Observe(ProcessObservation.Exited);
                Throws(() => f.Advance(RecoveryStage.SafeStop), "StageDeadlineExceeded");
                f.Advance(RecoveryStage.Cooldown);
            }
        }
        private static void NoFalseComplete()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch(); f.Advance(RecoveryStage.Verify);
                Throws(() => f.Advance(RecoveryStage.Complete), "BusinessVerificationIncomplete");
            }
        }
        private static void Maintenance()
        {
            using (var f = new Fixture())
            {
                f.Tick(60, false);
                Check(f.Store.Observe(f.Snapshot, ProcessObservation.Exited, f.Owner.BootId, f.Now, f.Settings, true).Code == "MaintenanceInhibited", "maintenance wins");
            }
        }
        private static void CompletedBeforeAdmission()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var operation = f.Reserve();
                var intent = f.Store.Read().Intent;
                var next = Clone(f.Main); next.ProcessId++;
                Action complete = () => f.Store.CompleteRecoveredRunBeforeAdmission(f.Token, operation, next,
                    intent.RootRunId, intent.RunId, intent.ConfigurationIdentity, f.Now);
                Throws(complete, "IdentityMismatch");
                f.Store.ConsumeLaunch(f.Token, operation, f.Now);
                f.Store.RecordLaunchResult(operation, next, false);
                Throws(() => f.Store.CompleteRecoveredRunBeforeAdmission(f.Token, operation, f.Main,
                    intent.RootRunId, intent.RunId, intent.ConfigurationIdentity, f.Now), "IdentityMismatch");
                Throws(() => f.Store.CompleteRecoveredRunBeforeAdmission(f.Token, operation, next,
                    Guid.NewGuid().ToString("N"), intent.RunId, intent.ConfigurationIdentity, f.Now), "IdentityMismatch");
                Throws(() => f.Store.CompleteRecoveredRunBeforeAdmission(f.Token, operation, next,
                    intent.RootRunId, intent.RunId, "other-config", f.Now), "IdentityMismatch");
                complete();
                var state = f.Store.Read();
                Check(state.Intent.DesiredState == RecoveryDesiredState.Completed, "no remaining work closes authorization");
                Check(state.Transaction.Stage == RecoveryStage.Cancelled && !state.Transaction.OwnershipReleased,
                    "business completion cannot fabricate successful recovery or release ownership");
                Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "Revoked");
                f.Store.SetOperatorIntent(state.Intent.AuthorizationId, state.Intent.IntentVersion,
                    RecoveryDesiredState.Stopped, "operator stop");
                Throws(complete, "Revoked");
                Check(f.Store.Read().Intent.DesiredState == RecoveryDesiredState.Stopped, "late completion preserves stop");
            }
        }

        private static void PrepareUnreleasedCompletion(Fixture f, bool releaseOwnership = false, bool stayInVerify = false,
            bool publishVerification = true)
        {
            f.ReadyLaunch();
            var operation = f.Reserve();
            f.Store.ConsumeLaunch(f.Token, operation, f.Now);
            var next = Clone(f.Main); next.ProcessId++;
            var nextRun = Guid.NewGuid().ToString("N");
            f.Store.RecordLaunchResult(operation, next, false);
            f.Store.BindRecoveredRun(f.Token, operation, nextRun, next, f.Now);
            f.Advance(RecoveryStage.Verify);
            f.Snapshot.MainProcess = next;
            f.Snapshot.RunId = nextRun;
            f.Snapshot.Authorization = f.Token;
            if (!publishVerification) return;
            f.Tick(1, true); f.Observe();
            f.Tick(50, true); f.Observe();
            f.Tick(50, true); f.Observe();
            var before = f.Store.Read();
            if (stayInVerify) return;
            f.Store.Advance(before.Transaction.TransactionId, before.Transaction.Epoch, f.Owner,
                RecoveryStage.Verify, RecoveryStage.Complete, "Verified recovery", f.Now, f.Settings, releaseOwnership);
            Check(f.Store.Read().Revision == before.Revision + 1, "completion uses one authority commit");
        }

        private static void AtomicCompletedHandback()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, true);
                var state = f.Store.Read();
                Check(state.Transaction.Stage == RecoveryStage.Complete && state.Transaction.OwnershipReleased,
                    "the only completion commit also releases ownership");
                f.Store.AssertRunAllowed(f.Token, state.Intent.MainProcess, f.Now);
                Check(state.Observation.SuspectCount == 0 && state.Matches(f.Token),
                    "handback resets failure confirmation without revoking the active output token");
            }
        }

        private static void OwnedStageRead()
        {
            using (var f = new Fixture())
            {
                f.Claim();
                f.Advance(RecoveryStage.SafeStop);
                var t = f.Store.Read().Transaction;
                Action read = () => f.Store.ReadOwnedStage(t.TransactionId, t.Epoch, f.Owner, RecoveryStage.SafeStop, f.Now);
                read();
                Throws(() => f.Store.ReadOwnedStage(t.TransactionId, t.Epoch, f.Owner, RecoveryStage.Launch, f.Now), "StageMismatch");
                Throws(() => f.Store.ReadOwnedStage(t.TransactionId, t.Epoch, f.Main, RecoveryStage.SafeStop, f.Now), "OwnerFenced");
                Throws(() => f.Store.ReadOwnedStage(t.TransactionId, t.Epoch, f.Owner, RecoveryStage.SafeStop,
                    new DateTime(t.StageDeadlineUtcTicks, DateTimeKind.Utc)), "StageMismatchOrExpired");
                var intent = f.Store.Read().Intent;
                f.Store.SetOperatorIntent(intent.AuthorizationId, intent.IntentVersion, RecoveryDesiredState.Stopped, "manual stop");
                Throws(read, "Revoked");
            }
        }

        private static void VerifiedOwnerReconciliation()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, stayInVerify: true, publishVerification: false);
                var state = f.Store.Read();
                var tx = state.Transaction;
                f.Now = new DateTime(tx.LeaseUntilUtcTicks, DateTimeKind.Utc).AddSeconds(1);
                Throws(() => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch, f.Owner,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "EvidenceIncomplete");
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Verify && !f.Store.Read().Transaction.OwnershipReleased,
                    "created living process alone cannot complete verification");
            }
            foreach (var stop in new[] { false, true })
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, stayInVerify: true);
                var state = f.Store.Read();
                var tx = state.Transaction;
                Action reconcile = () => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch,
                    f.Owner, ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now);
                Throws(reconcile, "ReconciliationNotAvailable");
                f.Now = new DateTime(Math.Max(tx.LeaseUntilUtcTicks, tx.StageDeadlineUtcTicks), DateTimeKind.Utc).AddSeconds(1);
                f.Tick(1, true); f.Observe();
                state = f.Store.Read();
                Throws(() => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch, f.Owner,
                    ProcessObservation.ExactAlive, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "ProcessEvidenceUnproven");
                Throws(() => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch, f.Main,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "OwnerFenced");
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Verify, "failed handback cannot persist completion");
                Throws(() => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch, f.Owner,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive,
                    new DateTime(state.Observation.LastVerifiedBusinessCommitUtcTicks, DateTimeKind.Utc)
                        .AddSeconds(tx.VerificationEvidenceMaxAgeSeconds)), "EvidenceIncomplete");
                if (stop)
                {
                    f.Store.SetOperatorIntent(state.Intent.AuthorizationId, state.Intent.IntentVersion,
                        RecoveryDesiredState.Stopped, "manual stop before orphan verification");
                    Throws(reconcile, "Revoked");
                    Check(!f.Store.Read().Transaction.OwnershipReleased, "stop needs inactive cleanup");
                    continue;
                }
                f.Tick(1, true);
                f.Store.PublishSnapshot(f.Snapshot);
                var replacement = Clone(f.Owner); replacement.ProcessId++;
                var actions = new ExecutionActions();
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, replacement, actions, () => f.Now,
                    process => process.Matches(f.Owner) ? ProcessObservation.Exited : ProcessObservation.ExactAlive, () => false);
                Check(Step(engine).Code == "VerifiedOwnershipReconciled", "verified main is handed back before adoption");
                Check(f.Now.Ticks > tx.StageDeadlineUtcTicks, "fresh proof still permits handback after the original Verify deadline");
                state = f.Store.Read();
                Check(state.Transaction.Stage == RecoveryStage.Complete && state.Transaction.OwnershipReleased &&
                    state.LastTakeoverEpoch == tx.Epoch && state.Matches(f.Token) && actions.Calls == 0,
                    "handback preserves token and never dispatches hardware actions");
                f.Store.AssertRunAllowed(f.Token, state.Intent.MainProcess, f.Now);
            }
        }

        private static void OrphanVerificationFreshness()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, stayInVerify: true);
                var state = f.Store.Read();
                f.Now = new DateTime(state.Transaction.StageDeadlineUtcTicks, DateTimeKind.Utc).AddSeconds(1);
                f.Tick(1, true); f.Observe();
                state = f.Store.Read();
                var now = f.Now;
                Check(now.Ticks > state.Transaction.StageDeadlineUtcTicks && RecoveryDecisionPolicy.HasFreshVerificationEvidence(state, now),
                    "fresh real progress can close orphan Verify after its old action deadline");
                var legacy = Clone(state); legacy.Transaction.VerificationEvidenceMaxAgeSeconds = 0;
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(legacy, now), "missing frozen freshness window does not adopt a new configuration");
                var missingChannel = Clone(state); missingChannel.Observation.ChannelClocks.RemoveAt(0);
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(missingChannel, now), "aggregate commit count cannot hide missing channel evidence");
                var oldSample = Clone(state); oldSample.Observation.ChannelClocks[0].SampleProgressUtcTicks = state.Transaction.VerificationStartedUtcTicks;
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(oldSample, now), "one old sample channel blocks handback");
                now = new DateTime(state.Observation.LastVerifiedBusinessCommitUtcTicks, DateTimeKind.Utc)
                    .AddSeconds(state.Transaction.VerificationEvidenceMaxAgeSeconds);
                var republished = Clone(state);
                republished.Observation.LastSnapshot.PublishedUtcTicks = now.Ticks;
                republished.Observation.LastSnapshot.SourceUtcTicks = now.Ticks;
                f.Settings.SnapshotMaxAgeSeconds = 300;
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(republished, now),
                    "republishing and widening current configuration cannot refresh frozen channel evidence");
                var hold = Clone(republished);
                foreach (var channel in hold.Observation.LastSnapshot.Channels)
                {
                    channel.Stage = "PressureHold";
                    channel.StageStartedUtcTicks = now.AddSeconds(-10).Ticks;
                    channel.StageDeadlineUtcTicks = now.AddSeconds(300).Ticks;
                }
                foreach (var clock in hold.Observation.ChannelClocks) clock.SampleProgressUtcTicks = now.Ticks;
                Check(RecoveryDecisionPolicy.HasFreshVerificationEvidence(hold, now),
                    "bounded hold with fresh real samples retains the earlier verified commits without inventing new ones");
                hold.Observation.LastSnapshot.Channels[0].StageDeadlineUtcTicks = now.Ticks;
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(hold, now), "expired hold cannot excuse stale control and persisted progress");
                hold = Clone(republished); hold.Observation.LastSnapshot.SourceAvailable = false;
                Check(!RecoveryDecisionPolicy.HasFreshVerificationEvidence(hold, now), "unavailable source cannot complete orphan verification");
                var invalidBudget = new RecoveryGuardSettings { VerificationTimeoutSeconds = 180 };
                Throws(invalidBudget.Validate, "SettingsInvalid");
            }
        }

        private static void OwnedVerificationFreshness()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, stayInVerify: true);
                var tx = f.Store.Read().Transaction;
                f.Tick(60, false); f.Observe();
                f.Store.RenewOwner(tx.TransactionId, tx.Epoch, f.Owner, f.Now, f.Settings);
                f.Tick(60, false); f.Store.PublishSnapshot(f.Snapshot);
                var result = Step(Engine(f, new ExecutionActions()));
                Check(result.Code == "WaitingForBusinessCommit" && f.Store.Read().Transaction.Stage == RecoveryStage.Verify,
                    "unexpired Verify and fresh files cannot reuse stale business progress");
                Throws(() => f.Advance(RecoveryStage.Complete), "BusinessVerificationIncomplete");
            }
        }

        private static void OrphanVerificationObservation()
        {
            foreach (var outcome in new[] { "complete", "timeout", "stop" })
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f, stayInVerify: true, publishVerification: false);
                var original = f.Store.Read();
                var tx = original.Transaction;
                f.Now = new DateTime(tx.LeaseUntilUtcTicks, DateTimeKind.Utc).AddSeconds(1);
                f.Tick(1, true); f.Store.PublishSnapshot(f.Snapshot);
                var observer = Clone(f.Owner); observer.ProcessId++;
                var actions = new ExecutionActions();
                Func<RecoveryExecutionEngine> engine = () => new RecoveryExecutionEngine(f.Store, f.Settings, observer, actions,
                    () => f.Now, p => p.Matches(f.Owner) ? ProcessObservation.Exited : ProcessObservation.ExactAlive, () => false);
                Check(Step(engine()).Code == "WaitingForOrphanVerificationEvidence", "first snapshot establishes an observation, not another actuator owner");
                var waiting = f.Store.Read();
                var deadline = waiting.Transaction.OrphanVerificationUntilUtcTicks;
                Check(deadline == f.Now.Ticks + tx.StageDeadlineUtcTicks - tx.StageStartedUtcTicks &&
                    waiting.Transaction.Epoch == tx.Epoch && waiting.Transaction.Owner.Matches(f.Owner) &&
                    waiting.Transaction.LeaseUntilUtcTicks == tx.LeaseUntilUtcTicks && waiting.Transaction.StageDeadlineUtcTicks == tx.StageDeadlineUtcTicks,
                    "observer records a separate deadline without extending old action or output admission lease");
                f.Store.AssertRunAllowed(f.Token, waiting.Intent.MainProcess, f.Now);
                observer.ProcessId++;
                Check(Step(engine()).Code == "WaitingForOrphanVerificationEvidence" &&
                    f.Store.Read().Transaction.OrphanVerificationUntilUtcTicks == deadline,
                    "restarted observer cannot extend the persisted window");
                if (outcome == "stop")
                {
                    f.Stop();
                    Throws(() => f.Store.TryObserveOrphanVerification(tx.TransactionId, tx.Epoch, f.Owner,
                        ProcessObservation.Exited, waiting.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "Revoked");
                    Check(Step(engine()).StopWorker && actions.Calls == 0, "manual stop ends observation without actions");
                    continue;
                }
                if (outcome == "timeout")
                {
                    for (var i = 0; i < 4; i++)
                    {
                        f.Tick(60, i == 3); f.Store.PublishSnapshot(f.Snapshot);
                        Check(Step(engine()).Code == "WaitingForOrphanVerificationEvidence", "no unbounded retry or early completion");
                    }
                    f.Tick(60, true); f.Store.PublishSnapshot(f.Snapshot); f.Observe();
                    Check(f.Now.Ticks == deadline, "test reaches the exact observation deadline");
                    Throws(() => f.Store.ReconcileVerifiedTakeover(tx.TransactionId, tx.Epoch, f.Owner,
                        ProcessObservation.Exited, waiting.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "EvidenceIncomplete");
                    Check(Step(engine()).Code == "OwnerAdoptedForReconciliation" && f.Store.Read().Transaction.Stage == RecoveryStage.Cooldown &&
                        f.Store.Read().LastTakeoverEpoch > tx.Epoch && f.Store.Read().Launches.Single().State == "Started" && actions.Calls == 0,
                        "deadline ends observation and preserves existing launch for ordinary reconciliation");
                    continue;
                }
                f.Tick(60, true); f.Store.PublishSnapshot(f.Snapshot);
                Check(Step(engine()).Code == "WaitingForOrphanVerificationEvidence", "one verified commit cannot complete");
                f.Tick(60, true); f.Store.PublishSnapshot(f.Snapshot);
                Check(Step(engine()).Code == "VerifiedOwnershipReconciled" && f.Store.Read().Transaction.OwnershipReleased &&
                    f.Store.Read().Matches(f.Token) && actions.Calls == 0, "two real commits hand back without changing the main token");
            }
        }

        private static void CompletedOwnerReconciliation()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f);
                var completed = f.Store.Read().Transaction;
                var replacement = Clone(f.Owner); replacement.ProcessId++;
                f.Now = new DateTime(completed.LeaseUntilUtcTicks, DateTimeKind.Utc);
                f.Tick(1, true);
                f.Store.PublishSnapshot(f.Snapshot);
                var actions = new ExecutionActions();
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, replacement, actions, () => f.Now,
                    process => process.Matches(f.Owner) ? ProcessObservation.Exited : ProcessObservation.ExactAlive, () => false);
                Check(Step(engine).Code == "CompletedOwnershipReconciled", "replacement closes durable completed handback");
                var state = f.Store.Read();
                Check(state.Transaction.OwnershipReleased && state.LastTakeoverEpoch == completed.Epoch &&
                    state.Transaction.Owner.Matches(f.Owner) && state.Matches(f.Token),
                    "terminal reconciliation preserves the running main's token and historical owner");
                Check(actions.Calls == 0 && state.Transaction.Stage == RecoveryStage.Complete,
                    "terminal reconciliation never runs hardware or relaunch actions");
                f.Store.ReconcileCompletedTakeover(completed.TransactionId, completed.Epoch, f.Owner,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now);
            }
        }

        private static void CompletedOwnerReconciliationRejects()
        {
            using (var f = new Fixture())
            {
                PrepareUnreleasedCompletion(f);
                var state = f.Store.Read();
                var t = state.Transaction;
                Action reconcile = () => f.Store.ReconcileCompletedTakeover(t.TransactionId, t.Epoch, f.Owner,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now);
                Throws(reconcile, "ReconciliationNotAvailable");
                f.Now = new DateTime(t.LeaseUntilUtcTicks, DateTimeKind.Utc).AddSeconds(1);
                Throws(() => f.Store.ReconcileCompletedTakeover(t.TransactionId, t.Epoch, f.Owner,
                    ProcessObservation.Unknown, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "ProcessEvidenceUnproven");
                Throws(() => f.Store.ReconcileCompletedTakeover(t.TransactionId, t.Epoch, f.Owner,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.Exited, f.Now), "ProcessEvidenceUnproven");
                Throws(() => f.Store.ReconcileCompletedTakeover(t.TransactionId, t.Epoch, f.Main,
                    ProcessObservation.Exited, state.Intent.MainProcess, ProcessObservation.ExactAlive, f.Now), "OwnerFenced");
                Throws(() => f.Store.ReconcileCompletedTakeover(t.TransactionId, t.Epoch, f.Owner,
                    ProcessObservation.Exited, f.Main, ProcessObservation.ExactAlive, f.Now), "ReconciliationNotAvailable");
                Check(!f.Store.Read().Transaction.OwnershipReleased, "failed evidence cannot release ownership");
                f.Store.SetOperatorIntent(state.Intent.AuthorizationId, state.Intent.IntentVersion,
                    RecoveryDesiredState.Stopped, "manual stop during terminal reconciliation");
                Throws(reconcile, "Revoked");
                Check(!f.Store.Read().Transaction.OwnershipReleased, "stopped completion needs separate cancelled cleanup");
            }
        }

        private static void VerifiedRecovery()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var operation = f.Reserve();
                f.Store.ConsumeLaunch(f.Token, operation, f.Now);
                var next = Clone(f.Main); next.ProcessId++;
                var nextRun = Guid.NewGuid().ToString("N");
                f.Store.RecordLaunchResult(operation, next, false);
                f.Store.BindRecoveredRun(f.Token, operation, nextRun, next, f.Now);
                f.Advance(RecoveryStage.Verify);
                f.Snapshot.MainProcess = next;
                f.Snapshot.RunId = nextRun;
                f.Snapshot.Authorization = f.Token;
                f.Tick(1, true); f.Observe();
                f.Tick(50, true); f.Observe();
                Throws(() => f.Advance(RecoveryStage.Complete), "BusinessVerificationIncomplete");
                f.Tick(50, true); f.Observe();
                f.Advance(RecoveryStage.Complete);
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Complete, "verified commits close recovery");
                var completed = f.Store.Read().Transaction;
                f.Store.ReleaseCompletedTakeover(completed.TransactionId, completed.Epoch, f.Owner);
                Check(f.Store.Read().Transaction.OwnershipReleased, "verified completion hands back normal recovery ownership");
                f.Store.ReleaseCompletedTakeover(completed.TransactionId, completed.Epoch, f.Owner);
                Throws(() => f.Store.ReleaseCompletedTakeover(completed.TransactionId, completed.Epoch, f.Main), "OwnerFenced");
            }
        }
        private static void ObserveOnly()
        {
            using (var f = new Fixture())
            {
                f.Settings.Mode = RecoveryGuardMode.ObserveOnly;
                f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                f.Tick(60, false); Check(f.Observe(ProcessObservation.Exited).Code == "WouldTakeOver", "only observation");
                Check(f.Store.Read().Transaction == null, "no mutation into takeover");
            }
        }
        private static void GuardIndependence()
        {
            using (var f = new Fixture())
            {
                f.Now = f.Now.AddHours(2);
                f.Store.AssertRunAllowed(f.Token, f.Main, f.Now);
                Throws(() => f.Store.AssertLaunchAllowed(f.Token, f.Now), "SupervisionExpired");
            }
        }
        private sealed class ActionTransport : IRecoverySupervisorTransport
        {
            internal readonly string SafetyId = Guid.NewGuid().ToString("N");
            internal int PrepareCalls, ExecuteCalls, LaunchCalls;
            internal Action OnPrepare;
            internal Func<RecoveryGuardLaunchRequest, Task<RecoveryGuardLaunchResponse>> OnLaunch;
            public Task<RecoveryGuardSafetyPrepareResponse> PrepareSafetyAsync(RecoveryGuardSafetyPrepareRequest request, CancellationToken token)
            {
                PrepareCalls++; OnPrepare?.Invoke();
                return Task.FromResult(new RecoveryGuardSafetyPrepareResponse { Accepted = true, SafetyAuthorityId = SafetyId });
            }
            public Task<RecoveryGuardSafetyExecuteResponse> ExecuteSafetyAsync(RecoveryGuardSafetyExecuteRequest request, CancellationToken token)
            {
                ExecuteCalls++;
                Check(request.SafetyAuthorityId == SafetyId, "must reuse persisted safety identity");
                return Task.FromResult(new RecoveryGuardSafetyExecuteResponse { Accepted = true, State = RecoveryGuardSafetyExecutionState.Pending });
            }
            public Task<RecoveryGuardLaunchPreparationResponse> PrepareLaunchAsync(RecoveryGuardLaunchPreparationRequest request, CancellationToken token)
                => throw new Exception("Unexpected launch preparation");
            public Task<RecoveryGuardLaunchResponse> LaunchAsync(RecoveryGuardLaunchRequest request, CancellationToken token)
            { LaunchCalls++; return OnLaunch != null ? OnLaunch(request) : throw new Exception("Unexpected duplicate launch"); }
        }

        private static void LateSafetyResponseSessionFence()
        {
            foreach (var activate in new[] { false, true })
            using (var f = new Fixture())
            {
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, Guid.NewGuid().ToString("N"), f.Now);
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                transport.OnPrepare = () =>
                {
                    var handoff = f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('c', 64), ProcessObservation.Exited, f.Now);
                    if (activate) f.Store.ActivateSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, handoff.OperationId,
                        f.Owner, ProcessObservation.ExactAlive, f.Now);
                };
                var result = ProductionActions(f, transport).ExecuteAsync(f.Store.Read(), CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Outcome == RecoveryActionOutcome.Pending && result.Evidence.Contains("SessionChanged") &&
                    f.Store.Read().Transaction.SafetyAuthorityId == null && transport.ExecuteCalls == 0,
                    "reserved or activated rollover must discard old safety preparation response");
            }
        }

        private static void LateCompletionSessionFence()
        {
            using (var f = new Fixture())
            {
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, Guid.NewGuid().ToString("N"), f.Now);
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var actions = new ExecutionActions { Handler = (state, token) =>
                {
                    var handoff = f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('d', 64), ProcessObservation.Exited, f.Now);
                    f.Store.ActivateSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, handoff.OperationId,
                        f.Owner, ProcessObservation.ExactAlive, f.Now);
                    return Task.FromResult(new RecoveryActionResult { Outcome = RecoveryActionOutcome.Completed, Evidence = "old session response" });
                } };
                var result = Step(Engine(f, actions));
                Check(!result.StopWorker && result.Code.Contains("SessionChanged") && f.Store.Read().Transaction.Stage == RecoveryStage.SafeStop,
                    "old completion cannot advance new session to Retire or terminate the current worker");
            }
        }

        private static void SessionRolloverCheckpointProof()
        {
            using (var f = new Fixture())
            {
                var oldSession = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, oldSession, f.Now);
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var safety = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, safety, null, f.Now);
                var handoff = f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('a', 64), ProcessObservation.Exited, f.Now);
                var replay = f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('a', 64), ProcessObservation.Exited, f.Now);
                Check(replay.NextSessionId == handoff.NextSessionId && handoff.PreviousSafetyAuthorityId == safety,
                    "reservation is idempotent and preserves the retired action identity");
                var host = Clone(f.Owner); host.ProcessId++;
                f.Store.ActivateSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, handoff.OperationId, host, ProcessObservation.ExactAlive, f.Now);
                var state = f.Store.Read();
                Check(state.Intent.AuthorizationId == f.Token.AuthorizationId && state.Intent.RunId == handoff.PreviousRunId &&
                    state.Intent.WatchdogSessionId == handoff.NextSessionId && state.Transaction.SafetyAuthorityId == null,
                    "activation changes coordinator identity without a new user authorization or old safety permission");
                Check(f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('a', 64), ProcessObservation.Exited, f.Now)
                    .OperationId == handoff.OperationId, "lost activation response does not allocate another session");
                var main = Clone(f.Main); main.ProcessId++;
                Action authorize = () => f.Store.AssertCheckpointSessionRollover(oldSession, handoff.NextSessionId, handoff.PreviousRunId,
                    handoff.RootRunId, main, f.Now);
                Throws(authorize, "RolloverUnproven");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, Guid.NewGuid().ToString("N"), null, f.Now);
                f.Advance(RecoveryStage.Retire); f.Advance(RecoveryStage.Launch);
                var id = f.Reserve();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.Launch, f.Store.Read().Transaction.SafetyAuthorityId, id, f.Now);
                Throws(authorize, "RolloverUnproven");
                f.Store.ConsumeLaunch(f.Token, id, f.Now); f.Store.RecordLaunchResult(id, main, false);
                authorize();
                Throws(() => f.Store.AssertCheckpointSessionRollover(oldSession, handoff.NextSessionId, Guid.NewGuid().ToString("N"),
                    handoff.RootRunId, main, f.Now), "RolloverUnproven");
                f.Stop(); Throws(authorize, "Revoked");
            }
        }

        private static void SessionRolloverStopWins()
        {
            using (var f = new Fixture())
            {
                var oldSession = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, oldSession, f.Now);
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var handoff = f.Store.ReserveSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, new string('b', 64), ProcessObservation.Exited, f.Now);
                f.Stop();
                Throws(() => f.Store.ActivateSessionRollover(tx.TransactionId, tx.Epoch, f.Owner, handoff.OperationId,
                    f.Owner, ProcessObservation.ExactAlive, f.Now), "Revoked");
                Check(f.Store.Read().Intent.WatchdogSessionId == oldSession, "stop preserves original coordinator binding");
            }
        }

        private static void LostLaunchResponseResumesStage()
        {
            using (var f = new Fixture())
            {
                f.Settings.CooldownSeconds = 5;
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, transport.SafetyId, null, f.Now);
                f.Advance(RecoveryStage.Retire); f.Advance(RecoveryStage.Launch);
                var id = f.Reserve();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.Launch, transport.SafetyId, id, f.Now);
                var next = Clone(f.Main); next.ProcessId++;
                transport.OnLaunch = request =>
                {
                    f.Store.ConsumeLaunch(f.Token, id, f.Now);
                    f.Store.RecordLaunchResult(id, next, false);
                    throw new IOException("response lost after process creation");
                };
                var actions = ProductionActions(f, transport);
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, f.Owner, actions, () => f.Now,
                    identity => identity.Matches(f.Main) ? ProcessObservation.Exited : ProcessObservation.ExactAlive, () => false);
                Step(engine);
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Cooldown && transport.LaunchCalls == 1,
                    "uncertain response enters cooldown after one creation request");
                var progress = f.Store.Read().Transaction.LastProgressUtcTicks;
                f.Now = f.Now.AddSeconds(5);
                Check(Step(engine).Code == "RecoveryActionResumedForReconciliation", "retry must resume Launch, not start another safety sequence");
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Launch && f.Store.Read().Transaction.LastProgressUtcTicks == progress,
                    "resume must not fabricate substantive progress");
                Step(engine);
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Verify && transport.LaunchCalls == 1 && transport.PrepareCalls == 0,
                    "persisted Started receipt leads to Verify without duplicate process or safety preparation");
            }
        }

        private static void ResumeActionRespectsAdmission()
        {
            using (var f = new Fixture())
            {
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop,
                    Guid.NewGuid().ToString("N"), null, f.Now);
                f.Advance(RecoveryStage.Cooldown);
                Throws(() => f.Store.ResumeRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, f.Now, f.Settings), "ResumeDenied");
                f.Stop();
                Throws(() => f.Store.ResumeRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, f.Now, f.Settings), "Revoked");
            }
        }

        private static SupervisorRecoveryActions ProductionActions(Fixture f, ActionTransport transport) =>
            new SupervisorRecoveryActions(f.Store, transport, () => f.Now,
                identity => identity.Matches(f.Main) ? ProcessObservation.Exited : ProcessObservation.ExactAlive);

        private static void IdleWorkerRetirementFence()
        {
            foreach (var stop in new[] { false, true })
            using (var f = new Fixture())
            {
                var trusted = f.Store.Read().Observation.LastGuardUtcTicks;
                f.Store.PulseExecutionWorker(f.Owner, null, ProcessObservation.Exited, f.Now, 180);
                Check(f.Store.Read().Observation.LastGuardUtcTicks == trusted, "worker pulse never renews trial supervision");
                Throws(() => f.Store.RequestIdleWorkerRetirement(f.Owner, f.Now), "RetirementDenied");
                f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                if (stop)
                {
                    f.Stop();
                    Throws(() => f.Store.RequestIdleWorkerRetirement(f.Owner, f.Now), "Revoked");
                    continue;
                }
                var requested = 0; var retired = 0;
                var decision = new RecoveryDecision { CanClaim = true };
                var result = RecoveryWorkerDispatch.Coordinate(f.Store.Read(), decision, f.Settings, false, f.Now,
                    owner => ProcessObservation.ExactAlive, () => requested++, null,
                    worker => { f.Store.RequestIdleWorkerRetirement(worker.Owner, f.Now); retired++; return true; });
                Check(result.Code == "IdleExecutionWorkerRetired;AwaitingNextDispatch" && retired == 1 && requested == 0,
                    "retirement and task request never occur in one scan");
                f.Store.AssertIdleWorkerRetirement(f.Owner, f.Now);
                Throws(() => f.Store.PulseExecutionWorker(f.Owner, f.Owner, ProcessObservation.ExactAlive, f.Now, 180), "WorkerFenced");
                Throws(() => f.Claim(), "WorkerFenced");
                var old = Clone(f.Owner); f.Owner = Clone(old); f.Owner.ProcessId++;
                Throws(() => f.Store.PulseExecutionWorker(f.Owner, old, ProcessObservation.Unknown, f.Now, 180), "PreviousOwnerUnproven");
                f.Store.PulseExecutionWorker(f.Owner, old, ProcessObservation.Exited, f.Now, 180);
                Throws(() => f.Store.AssertIdleWorkerRetirement(old, f.Now), "RetirementDenied");
                f.Claim();
                f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                Throws(() => f.Store.RequestIdleWorkerRetirement(f.Owner, f.Now), "RetirementDenied");
            }
        }

        private static void AtomicLaunchPreparation()
        {
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, session, f.Now);
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var safety = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, safety, null, f.Now);
                f.Advance(RecoveryStage.Retire); f.Advance(RecoveryStage.Launch);
                var operation = Guid.NewGuid().ToString("N");
                Func<string, string, RecoveryLaunchReservation> prepare = (safetyId, operationId) =>
                    f.Store.ReserveAndBindRecoveryLaunch(f.Token, tx.TransactionId, tx.Epoch, f.Owner,
                        safetyId, operationId, f.Now, f.Now.AddSeconds(60), session);
                Throws(() => prepare(Guid.NewGuid().ToString("N"), operation), "RequiresReconciliation");
                Check(f.Store.Read().Launches.Count == 0 && f.Store.Read().Transaction.LaunchOperationId == null,
                    "invalid safety produces neither reservation nor binding");
                var reserved = prepare(safety, operation);
                var reopened = new RecoveryControlStore(f.Root).Read();
                Check(reopened.Transaction.LaunchOperationId == operation && reopened.Launches.Single().OperationId == operation,
                    "lost response still leaves reservation and binding visible to a new reader");
                f.Tick(10, false);
                Check(prepare(safety, operation).ExpiresUtcTicks == reserved.ExpiresUtcTicks,
                    "preparation replay cannot extend original reservation deadline");
                Throws(() => prepare(safety, Guid.NewGuid().ToString("N")), "RequiresReconciliation");
                f.Tick(50, false);
                Check(f.Store.ConfirmReservationExpiredBeforeConsumption(operation, f.Now), "abandoned preparation expires without guessing process outcome");
                Check(f.Store.Read().MatchesBoundTerminalLaunch(f.Store.Read().Launches.Single()), "expired preparation is a bound terminal action");
                f.Stop();
                Throws(() => prepare(safety, operation), "Revoked");
                Check(f.Store.Read().Launches.Single().State == "StartFailed", "stop cannot revive the prepared operation");
            }
        }

        private static void ExitedAttemptRestartsSafely()
        {
            foreach (var stopped in new[] { false, true })
            foreach (var neverCreated in new[] { false, true })
            foreach (var adoptedOwner in new[] { false, true })
            using (var f = new Fixture())
            {
                var session = Guid.NewGuid().ToString("N");
                f.Store.BindWatchdogSession(f.Token, f.Snapshot.RunId, f.Main, session, f.Now);
                var tx = f.Claim();
                f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, transport.SafetyId, null, f.Now);
                f.Advance(RecoveryStage.Retire);
                f.Advance(RecoveryStage.Launch);
                var operation = f.Reserve();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.Launch, transport.SafetyId, operation, f.Now);
                if (!neverCreated)
                {
                    f.Store.ConsumeLaunch(f.Token, operation, f.Now);
                    f.Store.RecordLaunchResult(operation, f.Main, false);
                }
                Func<ProcessObservation, RecoveryTakeoverTransaction> restart = safety => f.Store.RestartTerminalRecoveryAttempt(
                    tx.TransactionId, tx.Epoch, f.Owner, session, transport.SafetyId, operation, f.Owner, safety, f.Now);
                Throws(() => restart(ProcessObservation.Exited), "RequiresReconciliation");
                if (neverCreated)
                {
                    Check(!f.Store.ConfirmReservationExpiredBeforeConsumption(operation, f.Now), "live reservation stays reserved");
                    f.Tick(60, false);
                    Check(f.Store.ConfirmReservationExpiredBeforeConsumption(operation, f.Now), "expiry produces proof before consumption");
                    Throws(() => f.Store.ConsumeLaunch(f.Token, operation, f.Now), "ConsumedExpiredOrMissing");
                }
                else Check(f.Store.ConfirmLaunchedProcessExited(operation), "fixture instance belongs to an earlier boot");
                if (adoptedOwner)
                {
                    var oldToken = f.Store.Read().Launches.Single().Authorization;
                    f.Settings.CooldownSeconds = 60;
                    f.Tick(181, false); f.Observe(ProcessObservation.Exited);
                    f.Owner = Clone(f.Owner); f.Owner.ProcessId++;
                    tx = f.Store.Adopt(tx.TransactionId, tx.Epoch, f.Owner, ProcessObservation.Exited, f.Now, f.Settings);
                    f.Advance(RecoveryStage.Cooldown);
                    f.Tick(60, false); f.Observe(ProcessObservation.Exited);
                    f.Advance(RecoveryStage.Claim); f.Advance(RecoveryStage.SafeStop);
                    Check(!f.Store.Read().Matches(oldToken), "historical action token remains fenced after adoption");
                    var proof = f.Store.Read();
                    Check(proof.MatchesBoundTerminalLaunch(proof.Launches.Single()), "exact bound terminal action may be reconciled");
                    proof.Transaction.LaunchOperationId = Guid.NewGuid().ToString("N");
                    Check(!proof.MatchesBoundTerminalLaunch(proof.Launches.Single()), "unbound historical operation cannot be reconciled");
                    proof = f.Store.Read(); proof.Launches.Single().Authorization.IntentVersion++;
                    Check(!proof.MatchesBoundTerminalLaunch(proof.Launches.Single()), "another intent cannot be reconciled");
                    proof = f.Store.Read(); proof.Transaction.ActionEpoch = tx.Epoch + 1;
                    Check(!proof.MatchesBoundTerminalLaunch(proof.Launches.Single()), "future action epoch cannot be reconciled");
                }
                Throws(() => restart(ProcessObservation.Unknown), "SafetyExitUnproven");
                if (stopped)
                {
                    f.Stop();
                    Throws(() => restart(ProcessObservation.Exited), "Revoked");
                    Check(f.Store.Read().Transaction.Epoch == tx.Epoch, "stop prevents new attempt");
                    continue;
                }
                var before = f.Store.Read();
                transport.OnPrepare = () => restart(ProcessObservation.Exited);
                var result = ProductionActions(f, transport).ExecuteAsync(before, CancellationToken.None).GetAwaiter().GetResult();
                var after = f.Store.Read();
                Check(result.Outcome == RecoveryActionOutcome.Pending && transport.ExecuteCalls == 0 && transport.LaunchCalls == 0,
                    "late previous preparation cannot execute or bind in the new epoch");
                Check(after.Transaction.Epoch > tx.Epoch && after.Transaction.Stage == RecoveryStage.SafeStop &&
                    after.Transaction.SafetyAuthorityId == null && after.Transaction.LaunchOperationId == null &&
                    after.Transaction.LastProgressUtcTicks == before.Transaction.LastProgressUtcTicks &&
                    after.Launches.Single().State == (neverCreated ? "StartFailed" : "Exited"), "restart preserves history and business progress clocks");
                Check(Directory.GetFiles(Path.Combine(f.Root, "action-history"), "*.json").Length == 1, "prior actions archived");
                Throws(() => restart(ProcessObservation.Exited), "OwnerFenced");
                Check(after.Intent.AuthorizationId == before.Intent.AuthorizationId && after.Intent.IntentVersion == before.Intent.IntentVersion,
                    "retry does not create a new operator authorization");
            }
        }

        private static void SupervisorActionsPersistIdentity()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                var actions = ProductionActions(f, transport);
                var result = actions.ExecuteAsync(f.Store.Read(), CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Outcome == RecoveryActionOutcome.Pending && transport.ExecuteCalls == 0,
                    "preparation must persist before execution");
                Check(f.Store.Read().Transaction.SafetyAuthorityId == transport.SafetyId, "durable safety binding");
                // A new adapter instance reads the persisted identity.
                ProductionActions(f, transport).ExecuteAsync(f.Store.Read(), CancellationToken.None).GetAwaiter().GetResult();
                Check(transport.PrepareCalls == 1 && transport.ExecuteCalls == 1, "restart must not prepare another authority");
            }
        }

        private static void SupervisorActionsStopWins()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                transport.OnPrepare = () => f.Store.SetOperatorIntent(f.Token.AuthorizationId, f.Token.IntentVersion,
                    RecoveryDesiredState.Stopped, "operator");
                Throws(() => ProductionActions(f, transport).ExecuteAsync(f.Store.Read(), CancellationToken.None).GetAwaiter().GetResult(),
                    "AuthorizationRevoked");
                Check(f.Store.Read().Transaction.SafetyAuthorityId == null && transport.ExecuteCalls == 0,
                    "late response cannot bind safety or launch after stop");
            }
        }

        private static void SupervisorActionsReconcileLaunch()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var transport = new ActionTransport();
                var tx = f.Store.Read().Transaction;
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, transport.SafetyId, null, f.Now);
                f.Advance(RecoveryStage.Retire); f.Advance(RecoveryStage.Launch);
                var id = f.Reserve();
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.Launch, transport.SafetyId, id, f.Now);
                var next = Clone(f.Main); next.ProcessId++;
                f.Store.ConsumeLaunch(f.Token, id, f.Now); f.Store.RecordLaunchResult(id, next, false);
                var result = ProductionActions(f, transport).ExecuteAsync(f.Store.Read(), CancellationToken.None).GetAwaiter().GetResult();
                Check(result.Outcome == RecoveryActionOutcome.Completed && result.Process.Matches(next) && transport.LaunchCalls == 0,
                    "shared Started evidence must reconcile without another process request");
            }
        }

        private static void ActionBindingRejectsReplacement()
        {
            using (var f = new Fixture())
            {
                var tx = f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var id = Guid.NewGuid().ToString("N");
                f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop, id, null, f.Now);
                Throws(() => f.Store.BindRecoveryAction(tx.TransactionId, tx.Epoch, f.Owner, RecoveryStage.SafeStop,
                    Guid.NewGuid().ToString("N"), null, f.Now), "RequiresReconciliation");
                Check(f.Store.Read().Transaction.SafetyAuthorityId == id, "rejection preserves original evidence");
            }
        }

        private sealed class ExecutionActions : IRecoveryExecutionActions
        {
            internal Func<RecoveryControlState, CancellationToken, Task<RecoveryActionResult>> Handler;
            internal int Calls;
            public int MaximumCallSeconds => 1;
            public Task<RecoveryActionResult> ExecuteAsync(RecoveryControlState context, CancellationToken cancellationToken)
            {
                Calls++;
                return Handler(context, cancellationToken);
            }
        }

        private static RecoveryExecutionEngine Engine(Fixture f, ExecutionActions actions) => new RecoveryExecutionEngine(
            f.Store, f.Settings, f.Owner, actions, () => f.Now,
            process => process.Matches(f.Main) ? ProcessObservation.Exited : ProcessObservation.ExactAlive, () => false);

        private static RecoveryExecutionStep Step(RecoveryExecutionEngine engine) => engine.StepAsync(CancellationToken.None).GetAwaiter().GetResult();

        private static void ExecutionVerifiedHandback()
        {
            using (var f = new Fixture())
            {
                f.Claim();
                var actions = new ExecutionActions();
                actions.Handler = (context, token) =>
                {
                    var result = new RecoveryActionResult { Outcome = RecoveryActionOutcome.Completed, Evidence = "isolated action evidence" };
                    if (context.Transaction.Stage == RecoveryStage.Launch)
                    {
                        var id = f.Reserve();
                        var next = Clone(f.Main); next.ProcessId++;
                        f.Store.ConsumeLaunch(f.Token, id, f.Now);
                        f.Store.RecordLaunchResult(id, next, false);
                        f.Snapshot.MainProcess = next;
                        f.Snapshot.Authorization = f.Token;
                        f.Snapshot.RunId = Guid.NewGuid().ToString("N");
                        f.Store.BindRecoveredRun(f.Token, id, f.Snapshot.RunId, next, f.Now);
                        result.OperationId = id; result.Process = next;
                    }
                    return Task.FromResult(result);
                };
                var engine = Engine(f, actions);
                Step(engine); Step(engine); Step(engine); Step(engine);
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Verify, "creation only reaches Verify");
                var verifyDeadline = f.Store.Read().Transaction.StageDeadlineUtcTicks;
                Check(Step(engine).Code == "WaitingForBusinessCommit", "window/process existence cannot complete recovery");
                for (var i = 0; i < 3; i++)
                {
                    f.Tick(f.Settings.ScanSeconds, true);
                    f.Store.PublishSnapshot(f.Snapshot);
                    Step(engine);
                    if (i < 2) Check(f.Store.Read().Transaction.Stage == RecoveryStage.Verify &&
                        f.Store.Read().Transaction.StageDeadlineUtcTicks == verifyDeadline,
                        "default scan cadence waits for actual commits without extending its verification deadline");
                }
                var state = f.Store.Read();
                Check(state.Transaction.Stage == RecoveryStage.Complete && state.Transaction.OwnershipReleased,
                    "fresh participating-channel commits must produce durable handback");
                Check(actions.Calls == 3 && state.Intent.DesiredState == RecoveryDesiredState.Run,
                    "only safety, retire and launch dispatch; successful trial stays authorized");
                var oldTransaction = state.Transaction.TransactionId;
                var later = new RecoveryExecutionEngine(f.Store, f.Settings, f.Owner, actions, () => f.Now,
                    process => process.Matches(f.Owner) ? ProcessObservation.ExactAlive : ProcessObservation.Exited, () => false);
                f.Now = f.Now.AddSeconds(60);
                Check(Step(later).Code == "Suspect", "a new outage requires new confirmations after handback");
                f.Store.ReleaseCompletedTakeover(oldTransaction, state.LastTakeoverEpoch, f.Owner);
                Check(f.Store.Read().Observation.SuspectCount == 1, "duplicate handback must not erase a newer observation");
                f.Now = f.Now.AddSeconds(60);
                Check(Step(later).Code == "ClaimCommitted" && f.Store.Read().Transaction.TransactionId != oldTransaction,
                    "released history must not prevent recovery of a later outage");
            }
        }

        private static void ExecutionRejectsFalseLaunch()
        {
            using (var f = new Fixture())
            {
                f.ReadyLaunch();
                var actions = new ExecutionActions { Handler = (state, token) => Task.FromResult(new RecoveryActionResult
                {
                    Outcome = RecoveryActionOutcome.Completed, Evidence = "window exists", OperationId = Guid.NewGuid().ToString("N"), Process = f.Owner
                }) };
                Check(Step(Engine(f, actions)).Code == "LaunchCreationEvidenceUnproven", "unrecorded launch must be blocked");
                Check(f.Store.Read().Transaction.Stage == RecoveryStage.Blocked, "cannot progress to Verify from an assertion");
            }
        }

        private static void ExecutionStopDuringAction()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var actions = new ExecutionActions { Handler = (state, token) =>
                {
                    f.Stop();
                    return Task.FromResult(new RecoveryActionResult { Outcome = RecoveryActionOutcome.Completed, Evidence = "late safe result" });
                } };
                var engine = Engine(f, actions);
                Throws(() => Step(engine), "Revoked");
                Check(Step(engine).StopWorker && actions.Calls == 1 && f.Store.Read().Transaction.Stage == RecoveryStage.Cancelled,
                    "stop wins late response and prevents further actions");
            }
        }

        private static void ExecutionContinuousCooldown()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var originalBusiness = f.Store.Read().Observation.LastVerifiedBusinessCommitUtcTicks;
                var actions = new ExecutionActions { Handler = (state, token) => Task.FromResult(new RecoveryActionResult
                    { Outcome = RecoveryActionOutcome.RetryableFailure, Evidence = "software retry requires cooldown" }) };
                var engine = Engine(f, actions);
                Step(engine);
                for (var i = 0; i < 65; i++) { f.Now = f.Now.AddSeconds(60); Step(engine); }
                var state = f.Store.Read();
                Check(!state.Observation.Expired && state.Intent.DesiredState == RecoveryDesiredState.Run && actions.Calls > 1,
                    "continuous supervision preserves retries beyond sixty minutes");
                Check(state.Observation.LastVerifiedBusinessCommitUtcTicks == originalBusiness, "retry and renewal must not fabricate business progress");
            }
        }

        private static void RepeatedFailuresPreserveBusinessOrigin()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var originalCommit = f.Store.Read().Observation.LastVerifiedBusinessCommitUtcTicks;
                var initialSequence = f.Snapshot.Sequence;
                var originalClocks = f.Store.Read().Observation.ChannelClocks.ToDictionary(c => c.Channel, c => Clone(c));
                var actions = new ExecutionActions { Handler = (state, token) => Task.FromResult(new RecoveryActionResult
                    { Outcome = RecoveryActionOutcome.RetryableFailure, Evidence = "same injected failure" }) };
                var steps = 0;
                var maximumSteps = 300 * ((f.Settings.CooldownSeconds + 59) / 60 + 5);
                var reportedCalls = 0;
                while (actions.Calls < 300 && steps++ < maximumSteps)
                {
                    f.Tick(60, false);
                    // Fresh publication and a new reader/executor must not invent a business commit.
                    f.Store = new RecoveryControlStore(f.Root);
                    f.Observe(ProcessObservation.Exited);
                    Step(Engine(f, actions));
                    var state = f.Store.Read();
                    Check(state.Observation.LastVerifiedBusinessCommitUtcTicks == originalCommit,
                        "repeated failure or fresh heartbeat reset the business progress origin");
                    Check(state.Observation.ChannelClocks.Count == originalClocks.Count &&
                        state.Observation.ChannelClocks.All(c => originalClocks.ContainsKey(c.Channel) &&
                            c.SampleProgressUtcTicks == originalClocks[c.Channel].SampleProgressUtcTicks &&
                            c.ControlProgressUtcTicks == originalClocks[c.Channel].ControlProgressUtcTicks &&
                            c.PersistedProgressUtcTicks == originalClocks[c.Channel].PersistedProgressUtcTicks),
                        "fresh publication must not reset any channel progress clock");
                    Check(!state.Observation.Expired && state.Intent.DesiredState == RecoveryDesiredState.Run &&
                        state.Transaction.Stage != RecoveryStage.Complete,
                        "continuous failed recovery must remain authorized but never report success");
                    if (actions.Calls > reportedCalls && actions.Calls % 100 == 0)
                    {
                        reportedCalls = actions.Calls;
                        Console.WriteLine("PROGRESS RepeatedFailures Attempts=" + reportedCalls + " Steps=" + steps);
                    }
                }
                Check(actions.Calls == 300 && f.Snapshot.Sequence > initialSequence,
                    "bounded scenario must actually dispatch three hundred failures with fresh publications; attempts=" + actions.Calls + " steps=" + steps);
                f.Stop();
                var calls = actions.Calls;
                Check(Step(Engine(f, actions)).StopWorker && actions.Calls == calls,
                    "operator stop must end repeated recovery without one more action");
                Console.WriteLine("METRIC RepeatedFailures Attempts=" + calls + " Steps=" + steps +
                    " SimulatedSeconds=" + (f.Now - f.Start).TotalSeconds);
            }
        }

        private static void ExecutionActionTimeout()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                var actions = new ExecutionActions { Handler = async (state, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return new RecoveryActionResult { Outcome = RecoveryActionOutcome.Completed, Evidence = "unreachable" };
                } };
                var step = Step(Engine(f, actions));
                Check(step.ActionDispatched && f.Store.Read().Transaction.Stage == RecoveryStage.Cooldown,
                    "timed out action remains uncertain and returns to reconciliation after cooldown");
            }
        }

        private static void ExecutionExitedMode()
        {
            using (var f = new Fixture())
            {
                f.Claim(); f.Advance(RecoveryStage.SafeStop);
                f.Settings.Mode = RecoveryGuardMode.RecoverExited;
                var actions = new ExecutionActions { Handler = (state, token) => throw new Exception("live main stop dispatched") };
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, f.Owner, actions, () => f.Now,
                    process => ProcessObservation.ExactAlive, () => false);
                Check(Step(engine).Code == "RecoverExitedWaitingForMainExit" && actions.Calls == 0,
                    "deescalated mode must not continue live-main stop or retirement");
            }
        }

        private static void ExecutionOwnerAdoption()
        {
            using (var f = new Fixture())
            {
                var old = f.Claim();
                f.Now = f.Now.AddSeconds(f.Settings.OwnerLeaseSeconds + 1);
                var replacement = Clone(f.Owner); replacement.ProcessId++;
                var actions = new ExecutionActions { Handler = (state, token) => throw new Exception("adoption dispatched before reconciliation") };
                var engine = new RecoveryExecutionEngine(f.Store, f.Settings, replacement, actions, () => f.Now,
                    process => ProcessObservation.Exited, () => false);
                Check(Step(engine).Code == "OwnerAdoptedForReconciliation", "exited owner can be replaced after lease expiry");
                var current = f.Store.Read().Transaction;
                Check(current.Epoch > old.Epoch && current.Owner.Matches(replacement) && current.Stage == RecoveryStage.Cooldown && actions.Calls == 0,
                    "new epoch must reconcile instead of adopting an old launch as verified success");
            }
        }

        private static void ExecutionObserveOnly()
        {
            using (var f = new Fixture())
            {
                f.Settings.Mode = RecoveryGuardMode.ObserveOnly;
                var actions = new ExecutionActions { Handler = (state, token) => throw new Exception("ObserveOnly dispatched") };
                Check(Step(Engine(f, actions)).StopWorker && actions.Calls == 0 && f.Store.Read().Transaction == null,
                    "commissioning must never claim or actuate");
            }
        }

        private static void RealProcessIdentity()
        {
            var identity = RecoveryProcessProbe.Current();
            Check(identity.BootId == RecoveryProcessProbe.ReadBootId(), "stable boot GUID");
            Check(RecoveryProcessProbe.Observe(identity, identity.BootId) == ProcessObservation.ExactAlive, "current process identity");
            identity.StartUtcTicks--;
            Check(RecoveryProcessProbe.Observe(identity, identity.BootId) == ProcessObservation.Exited, "reused identity cannot match");
        }
    }
}
