using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;
using MtEmbTest;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Focused production checks for the Host's strict V4 authority boundary.
    /// They use the real file store/factory and authority CAS; no legacy
    /// coordinator or process launcher is substituted.
    /// </summary>
    internal static class StrictHostV4ProductionTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("V2.13.0.43 release assembly identity", WatchdogAssemblyVersionIdentity, ref passed);
            Run("strict bootstrap format2", StrictBootstrapFormat2, ref passed);
            Run("approved intent durable", ApprovedToIntent, ref passed);
            Run("approved permit取消后耐久Superseded且重启不可消费", ApprovedPermitRevocationIsDurable, ref passed);
            Run("RejectedNoWork终态不打开熔断且重启不可消费", RejectedNoWorkIsNeutralTerminal, ref passed);
            Run("closing tombstone双写并保持精确会话身份", ClosingTombstoneIsWriteThrough, ref passed);
            Run("bootstrap outcome绑定子进程身份且HMAC拒绝篡改", BootstrapOutcomeHmacBinding, ref passed);
            Run("started requires durable consume", StartedRequiresDurableConsume, ref passed);
            Run("started attached committed", StartedAttachedCommitted, ref passed);
            Run("recovery commit replaces failed context and second takeover gets fresh permit",
                RecoveryCommitReplacesContextAndMintsSecondPermit, ref passed);
            Run("resume intent no start", ResumeIntentNoStart, ref passed);
            Run("wrong capability rejected", WrongCapabilityRejected, ref passed);
            Run("same op replay", SameOperationReplay, ref passed);
            Run("concurrent same op", ConcurrentSameOperation, ref passed);
            Run("format1 active blocks", Format1ActiveBlocks, ref passed);
            Run("block terminal durable", BlockTerminalDurable, ref passed);
            Run("missing authority failclosed", MissingAuthorityFailClosed, ref passed);
            Run("Host adapter guarded boundary", HostAdapterGuardedBoundary, ref passed);
            Run("Host guarded Process.Start one-shot", HostGuardedProcessStartOneShot, ref passed);
            Run("failure replay freezes reporter identity", FailureReplayFreezesReporterIdentity, ref passed);
            Run("real pipe permanent failure receipt replay", () => RealPipeFailureReceiptReplay(true), ref passed);
            Run("real pipe nonpermanent failure receipt replay", () => RealPipeFailureReceiptReplay(false), ref passed);
            return passed;
        }

        private static void WatchdogAssemblyVersionIdentity()
        {
            var expected = new Version(2, 13, 0, 43);
            Require(typeof(WatchdogProtocol).Assembly.GetName().Version == expected,
                "Protocol assembly version is not V2.13.0.43");
            Require(typeof(WatchdogClientTransportEngine).Assembly.GetName().Version == expected,
                "Client assembly version is not V2.13.0.43");
            Require(typeof(StrictHostV4AuthorityAdapter).Assembly.GetName().Version == expected,
                "Host assembly version is not V2.13.0.43");
            Require(typeof(Main_Frm).Assembly.GetName().Version == expected,
                "Main application assembly version is not V2.13.0.43");
            Require(typeof(EpbManager).Assembly.GetName().Version == expected,
                "Controller assembly version is not V2.13.0.43");
            Require(WatchdogProtocol.Version == 4 &&
                    WatchdogJournalPolicy.CurrentSchemaVersion == 4 &&
                    DurableRelaunchAuthorityV4Validator.RequiredFormatRevision == 2,
                "wire/schema/record compatibility tuple changed");
        }

        private static void FailureReplayFreezesReporterIdentity()
        {
            var correlation = "91919191919191919191919191919191";
            var payload = Sha256Text("failure-replay-payload");
            var record = new DurableRelaunchPermitRecord
            {
                LastFailureOperationId = "81818181818181818181818181818181",
                LastFailureCorrelationId = correlation,
                LastFailurePayloadSha256 = payload,
                LastFailureSessionNonce = "original-reporter-nonce",
                LastFailureProcessId = 1234,
                LastFailureProcessStartUtcTicks = 5678,
                LastFailureConnectionGeneration = 9,
                LastFailureAttemptGeneration = 4
            };

            StrictRecoveryFailureReplayIdentity replay;
            Require(StrictRecoveryFailureReplayIdentity.TryResolve(record, correlation, payload, out replay),
                "exact raw replay did not resolve the frozen reporter tuple");
            Require(replay.OperationId == record.LastFailureOperationId &&
                    replay.SessionNonce == record.LastFailureSessionNonce &&
                    replay.ReporterProcessId == record.LastFailureProcessId &&
                    replay.ReporterProcessStartUtcTicks == record.LastFailureProcessStartUtcTicks &&
                    replay.ConnectionGeneration == record.LastFailureConnectionGeneration &&
                    replay.RecoveryAttemptGeneration == record.LastFailureAttemptGeneration,
                "replay reporter tuple was not reproduced exactly");
            Require(!StrictRecoveryFailureReplayIdentity.TryResolve(record,
                        "92929292929292929292929292929292", payload, out replay),
                "different correlation reused the prior reporter identity");
            Require(!StrictRecoveryFailureReplayIdentity.TryResolve(record,
                        correlation, Sha256Text("different-payload"), out replay),
                "different payload reused the prior reporter identity");
        }

        private static void StrictBootstrapFormat2()
        {
            WithAuthority((dir, session, authority) =>
            {
                Require(authority.Snapshot.RecordFormatRevision == DurableRelaunchAuthorityV4Validator.RequiredFormatRevision, "format2 not written");
                Require(DurableRelaunchAuthorityV4Validator.RequiredFormatRevision == 2, "required format revision is not 2");
            });
        }

        private static void ApprovedToIntent()
        {
            WithAuthority((dir, session, authority) =>
            {
                var decision = authority.RegisterFailureAndDecide(Operation(session, "11111111111111111111111111111111"));
                Require(decision.Durable && decision.ActionAllowed && decision.Record.State == DurableRelaunchPermitState.Approved, "approval not durable");
                var transition = authority.PrepareLaunchIntent(Intent(authority.Snapshot, session));
                Require(transition.Succeeded && transition.Capability != null && transition.Record.State == DurableRelaunchPermitState.LaunchIntent, "intent not durable");
                Require(transition.Record.LaunchIntentId == transition.Capability.IntentId, "intent identity mismatch");
            });
        }

        private static void ApprovedPermitRevocationIsDurable()
        {
            WithAuthority((dir, session, authority) =>
            {
                var decision = authority.RegisterFailureAndDecide(
                    Operation(session, "12121212121212121212121212121212"));
                Require(decision.ActionAllowed &&
                        decision.Record.State == DurableRelaunchPermitState.Approved,
                    "撤销测试未取得Approved permit");
                var revoked = authority.RevokeCurrent("SafeIdleSuperseded");
                Require(revoked.Succeeded &&
                        revoked.Record.State == DurableRelaunchPermitState.Revoked &&
                        revoked.Record.CircuitOpen &&
                        string.Equals(
                            revoked.Record.LastFailureDisposition,
                            RecoveryFailureDispositions.Superseded,
                            StringComparison.Ordinal),
                    "取消后的permit没有耐久提交Revoked/Superseded");
                var reopened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                Require(reopened.Succeeded &&
                        reopened.Authority.Snapshot.State == DurableRelaunchPermitState.Revoked &&
                        !reopened.Authority.ResumeLaunchIntent().Succeeded,
                    "重启后已撤销permit仍可恢复为LaunchIntent");
            });
        }

        private static void RejectedNoWorkIsNeutralTerminal()
        {
            WithAuthority((dir, session, authority) =>
            {
                var decision = authority.RegisterFailureAndDecide(
                    Operation(session, "13131313131313131313131313131313"));
                var failures = decision.Record.ConsecutiveFailures;
                var rejected = authority.RejectCurrentNoWork(
                    new DurableRelaunchPermitIdentity
                    {
                        SessionId = decision.Record.SessionId,
                        Generation = decision.Record.Generation,
                        PermitId = decision.Record.PermitId
                    },
                    "CheckpointDisarmed");
                Require(rejected.Succeeded &&
                        rejected.Record.State == DurableRelaunchPermitState.RejectedNoWork &&
                        !rejected.Record.CircuitOpen &&
                        rejected.Record.ConsecutiveFailures == failures,
                    "RejectedNoWork错误打开熔断或增加失败预算");
                var reopened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                Require(reopened.Succeeded &&
                        reopened.Authority.Snapshot.State == DurableRelaunchPermitState.RejectedNoWork &&
                        !reopened.Authority.ResumeLaunchIntent().Succeeded,
                    "RejectedNoWork重启后仍可恢复启动许可");
            });
        }

        private static void ClosingTombstoneIsWriteThrough()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "closing-tombstone-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            var local = WatchdogJournalPaths.LocalClosingPath(session);
            try
            {
                Directory.CreateDirectory(directory);
                var written = WatchdogClosingTombstoneStore.WriteThrough(
                    directory,
                    new WatchdogClosingTombstone
                    {
                        SessionId = session,
                        SessionGeneration = 3,
                        SessionLease = 9,
                        CloseIntent = "ManualStopIntent",
                        StateVersion = 1,
                        State = WatchdogClosingTombstoneState.Closing,
                        TerminalReason = "ManualStopIntent"
                    });
                WatchdogClosingTombstone read;
                Require(File.Exists(local) &&
                        File.Exists(WatchdogJournalPaths.ProjectClosingPath(directory, session)) &&
                        WatchdogClosingTombstoneStore.TryRead(directory, session, out read) &&
                        read.SessionGeneration == 3 && read.SessionLease == 9 &&
                        written.UpdatedUtcTicks > 0,
                    "closing tombstone未完成本机/项目双写或身份回读");
            }
            finally
            {
                try { if (File.Exists(local)) File.Delete(local); } catch { }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static void BootstrapOutcomeHmacBinding()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "bootstrap-outcome-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            var permit = Guid.NewGuid().ToString("N");
            var nonce = Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(directory);
                using (var process = Process.GetCurrentProcess())
                    RecoveryBootstrapOutcomeStore.WriteThrough(
                        directory,
                        new RecoveryBootstrapReceipt
                        {
                            SessionId = session,
                            PermitGeneration = 4,
                            PermitId = permit,
                            ChildProcessId = process.Id,
                            ChildProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                            CheckpointRevision = 12,
                            Outcome = RecoveryBootstrapOutcome.RejectedNoWork,
                            Reason = "CheckpointDisarmed"
                        },
                        nonce);
                RecoveryBootstrapReceipt verified;
                Require(RecoveryBootstrapOutcomeStore.TryReadVerified(
                            directory, session, 4, permit, nonce, out verified) &&
                        verified.Outcome == RecoveryBootstrapOutcome.RejectedNoWork &&
                        verified.CheckpointRevision == 12,
                    "合法bootstrap outcome未通过HMAC与身份验证");
                var path = RecoveryBootstrapOutcomeStore.GetPath(directory, session, 4);
                File.WriteAllText(
                    path,
                    File.ReadAllText(path).Replace("CheckpointDisarmed", "TamperedReason"),
                    new UTF8Encoding(false));
                Require(!RecoveryBootstrapOutcomeStore.TryReadVerified(
                            directory, session, 4, permit, nonce, out verified),
                    "篡改bootstrap outcome仍通过HMAC验证");
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static void StartedAttachedCommitted()
        {
            WithAuthority((dir, session, authority) =>
            {
                authority.RegisterFailureAndDecide(Operation(session, "22222222222222222222222222222222"));
                var intent = authority.PrepareLaunchIntent(Intent(authority.Snapshot, session));
                var process = Process.GetCurrentProcess();
                var start = process.StartTime.ToUniversalTime().Ticks;
                var consumed = authority.ConsumeLaunchIntent(intent.Capability);
                Require(consumed.Succeeded && consumed.Record.LaunchConsumed,
                    "launch consume CAS failed");
                var childPid = process.Id + 1000;
                var childStart = start + 1;
                var started = authority.CommitStarted(intent.Capability, childPid, childStart);
                Require(started.Succeeded && started.Record.State == DurableRelaunchPermitState.Started, "started CAS failed");
                Require(started.Record.ProcessId == childPid &&
                        started.Record.ProcessStartUtcTicks == childStart &&
                        started.Record.LastFailureProcessId == process.Id &&
                        started.Record.LastFailureProcessStartUtcTicks == start,
                    "reporter and launched identities were conflated");
                var attached = authority.CommitAttached(intent.Capability);
                Require(attached.Succeeded && attached.Record.State == DurableRelaunchPermitState.Attached, "attached CAS failed");
                var committed = authority.CommitCommitted(intent.Capability, "strict-run", 1, "Recovery", "p1", 1);
                Require(committed.Succeeded && committed.Record.State == DurableRelaunchPermitState.Committed, "committed CAS failed");
                Require(committed.Record.AuthorityRevision == 6, "lifecycle revisions not monotonic");
            });
        }

        private static void StartedRequiresDurableConsume()
        {
            WithAuthority((dir, session, authority) =>
            {
                authority.RegisterFailureAndDecide(
                    Operation(session, "20202020202020202020202020202020"));
                var intent = authority.PrepareLaunchIntent(
                    Intent(authority.Snapshot, session));
                var process = Process.GetCurrentProcess();
                var rejected = authority.CommitStarted(
                    intent.Capability,
                    process.Id + 2000,
                    process.StartTime.ToUniversalTime().Ticks + 2);
                Require(!rejected.Succeeded &&
                        string.Equals(rejected.Reason, "LaunchIntentNotConsumed", StringComparison.Ordinal) &&
                        authority.Snapshot.State == DurableRelaunchPermitState.LaunchIntent &&
                        !authority.Snapshot.LaunchConsumed,
                    "CommitStarted bypassed durable consume");
            });
        }

        private static void RecoveryCommitReplacesContextAndMintsSecondPermit()
        {
            WithAuthority((dir, session, authority) =>
            {
                var firstFailure = Operation(
                    session,
                    "23232323232323232323232323232323");
                firstFailure.RunId = "failed-run";
                firstFailure.RunEpoch = 1;
                firstFailure.RecoveryStage = "DoOffConfirmed";
                firstFailure.RecoveryProgressToken = "17";
                var first = authority.RegisterFailureAndDecide(firstFailure);
                Require(first.ActionAllowed && first.Record.Generation == 1,
                    "first failure did not mint generation 1");

                var intent = authority.PrepareLaunchIntent(
                    Intent(authority.Snapshot, session));
                Require(intent.Succeeded, "first launch intent failed");
                Require(authority.ConsumeLaunchIntent(intent.Capability).Succeeded,
                    "first launch intent was not consumed");
                using (var process = Process.GetCurrentProcess())
                {
                    Require(authority.CommitStarted(
                                intent.Capability,
                                process.Id + 3000,
                                process.StartTime.ToUniversalTime().Ticks + 3)
                            .Succeeded,
                        "first recovery process did not reach Started");
                }
                var attached = authority.CommitAttached(intent.Capability);
                Require(attached.Succeeded,
                    "first recovery process did not reach Attached");

                var committedCandidate = attached.Record.Clone();
                committedCandidate.State = DurableRelaunchPermitState.Committed;
                committedCandidate.RunId = "recovered-run";
                committedCandidate.RunEpoch = 3;
                committedCandidate.RecoveryStage = "Rejoining";
                committedCandidate.RecoveryProgressToken = "42";
                committedCandidate.RecoveryCommitGeneration = 1;
                DurableRelaunchAuthorityV4Validator.TryValidateRecord(
                    committedCandidate,
                    session,
                    out var candidateValidation);

                var committed = authority.CommitCommitted(
                    intent.Capability,
                    "recovered-run",
                    3,
                    "Rejoining",
                    "42",
                    1);
                Require(committed.Succeeded &&
                        committed.Record.State == DurableRelaunchPermitState.Committed &&
                        committed.Record.RunId == "recovered-run" &&
                        committed.Record.RunEpoch == 3 &&
                        committed.Record.RecoveryStage == "Rejoining" &&
                        committed.Record.RecoveryProgressToken == "42",
                    "new recovered run context was rejected or not persisted: " +
                    (committed?.Reason ?? "null") + "/candidate=" +
                    (candidateValidation ?? "valid") +
                    $"/circuit={committedCandidate.CircuitOpen}" +
                    $"/gen={committedCandidate.Generation}" +
                    $"/permit={committedCandidate.PermitId}" +
                    $"/nonce={committedCandidate.PermitNonce}" +
                    $"/canonical={committedCandidate.LastFailureCanonicalSha256}" +
                    $"/commitGen={committedCandidate.RecoveryCommitGeneration}" +
                    $"/candidateRun={committedCandidate.RunId}/{committedCandidate.RunEpoch}" +
                    $"/candidateStage={committedCandidate.RecoveryStage}" +
                    $"/candidateProgress={committedCandidate.RecoveryProgressToken}" +
                    $"/consumed={committedCandidate.LaunchConsumed}/" +
                    (committed?.Record?.State.ToString() ?? "no-record") + "/" +
                    (committed?.Record?.RunId ?? "no-run") + "/" +
                    (committed?.Record?.RunEpoch.ToString() ?? "no-epoch") + "/" +
                    (committed?.Record?.RecoveryStage ?? "no-stage") + "/" +
                    (committed?.Record?.RecoveryProgressToken ?? "no-progress"));

                var secondFailure = Operation(
                    session,
                    "24242424242424242424242424242424");
                secondFailure.OperationId = Guid.NewGuid().ToString("N");
                secondFailure.RunId = "recovered-run";
                secondFailure.RunEpoch = 3;
                secondFailure.RecoveryStage = "PowerOffUnconfirmed";
                secondFailure.RecoveryProgressToken = "43";
                var second = authority.RegisterFailureAndDecide(secondFailure);
                Require(second.Durable && second.ActionAllowed &&
                        second.Record.State == DurableRelaunchPermitState.Approved &&
                        second.Record.Generation == 2,
                    "second failure did not mint a fresh consumable permit");
                Require(second.Record.ProcessId == 0 &&
                        second.Record.ProcessStartUtcTicks == 0 &&
                        string.IsNullOrEmpty(second.Record.LaunchIntentId) &&
                        string.IsNullOrEmpty(second.Record.LaunchSpecSha256) &&
                        !second.Record.LaunchConsumed,
                    "generation 2 retained generation 1 launch/process identity");
                var secondIntent = authority.PrepareLaunchIntent(
                    Intent(authority.Snapshot, session));
                Require(secondIntent.Succeeded &&
                        secondIntent.Record.State == DurableRelaunchPermitState.LaunchIntent,
                    "fresh generation could not enter LaunchIntent");
            });
        }

        private static void ResumeIntentNoStart()
        {
            WithAuthority((dir, session, authority) =>
            {
                authority.RegisterFailureAndDecide(Operation(session, "33333333333333333333333333333333"));
                var first = authority.PrepareLaunchIntent(Intent(authority.Snapshot, session));
                Require(first.Succeeded, "intent setup failed");
                var reopened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                Require(reopened.Succeeded, reopened.Reason);
                var resumed = reopened.Authority.ResumeLaunchIntent();
                Require(resumed.Succeeded && resumed.Capability != null && resumed.Record.State == DurableRelaunchPermitState.LaunchIntent, "resume did not read intent");
            });
        }

        private static void WrongCapabilityRejected()
        {
            WithAuthority((dir, session, authority) =>
            {
                authority.RegisterFailureAndDecide(Operation(session, "44444444444444444444444444444444"));
                var first = authority.PrepareLaunchIntent(Intent(authority.Snapshot, session));
                var forged = new DurableLaunchIntentCapability(Intent(authority.Snapshot, session), first.Capability.AuthorityRevision, first.Capability.AuthoritySha256);
                var result = authority.CommitStarted(forged, Process.GetCurrentProcess().Id, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
                Require(!result.Succeeded && authority.Snapshot.State == DurableRelaunchPermitState.LaunchIntent, "forged capability changed state");
            });
        }

        private static void SameOperationReplay()
        {
            WithAuthority((dir, session, authority) =>
            {
                var operation = Operation(session, "55555555555555555555555555555555");
                var first = authority.RegisterFailureAndDecide(operation);
                var second = authority.RegisterFailureAndDecide(operation.Clone());
                Require(first.Durable && second.Durable && first.Record.AuthorityRevision == second.Record.AuthorityRevision &&
                        first.Receipt.DecisionSequence == second.Receipt.DecisionSequence, "same operation was not replayed");
            });
        }

        private static void ConcurrentSameOperation()
        {
            WithAuthority((dir, session, authority) =>
            {
                var operation = Operation(session, "66666666666666666666666666666666");
                var results = new DurableAuthorityDecisionResult[64];
                Parallel.For(0, results.Length, i => results[i] = authority.RegisterFailureAndDecide(operation.Clone()));
                var winner = results[0];
                foreach (var value in results)
                    Require(value.Durable && value.Record.AuthorityRevision == winner.Record.AuthorityRevision && value.Receipt.DecisionSequence == winner.Receipt.DecisionSequence, "concurrent replay diverged");
            });
        }

        private static void Format1ActiveBlocks()
        {
            var session = Guid.NewGuid().ToString("N");
            var json = "{\"SchemaVersion\":4,\"RecordKind\":\"DurableRelaunchAuthority\",\"RecordFormatRevision\":1,\"SessionId\":\"" + session + "\",\"State\":\"Approved\",\"Generation\":1}";
            var result = DurableRelaunchAuthorityV4Validator.ValidateAndMigrate(json, session);
            Require(result.Blocked && result.Migrated && !result.Proven, "format1 active was reopened");
        }

        private static void BlockTerminalDurable()
        {
            WithAuthority((dir, session, authority) =>
            {
                authority.RegisterFailureAndDecide(Operation(session, "77777777777777777777777777777777"));
                var blocked = authority.BlockCurrent("test-block");
                Require(blocked.Succeeded && blocked.Record.State == DurableRelaunchPermitState.Blocked && blocked.Record.CircuitOpen, "blocked marker missing");
                var reopened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                Require(reopened.Succeeded && reopened.Authority.Snapshot.State == DurableRelaunchPermitState.Blocked, "blocked marker did not survive reopen");
            });
        }

        private static void MissingAuthorityFailClosed()
        {
            var result = DurableRelaunchAuthorityV4Factory.TryOpenExisting(Path.Combine(Path.GetTempPath(), "missing-v4-" + Guid.NewGuid().ToString("N")), Guid.NewGuid().ToString("N"));
            Require(!result.Succeeded && result.Blocked && result.Unproven, "missing authority was not fail-closed");
        }

        private static void HostAdapterGuardedBoundary()
        {
            var dir = Path.Combine(Path.GetTempPath(), "strict-host-adapter-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            var process = Process.GetCurrentProcess();
            var executable = Path.GetFullPath(process.MainModule.FileName);
            try
            {
                Directory.CreateDirectory(dir);
                var args = new WatchdogArguments
                {
                    SessionId = session,
                    PipeName = "strict-host-test-pipe",
                    ExecutablePath = executable,
                    JournalDirectory = dir,
                    SidecarInstanceNonce = new string('a', 32),
                    JournalPolicy = new WatchdogJournalPolicy()
                };
                var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                    dir,
                    session,
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    8);
                Require(bootstrap.Succeeded,
                    "runtime-owned strict bootstrap failed: " + bootstrap.Reason);
                var strict = new StrictHostV4AuthorityAdapter(
                    args, process.Id, process.StartTime.ToUniversalTime().Ticks);
                var approval = strict.ApproveOrGetExisting(new DurableRelaunchRequest
                {
                    Fingerprint = "strict-host-test",
                    ProgressToken = "p1",
                    ProcessSource = "strict-test",
                    RunId = "strict-run",
                    RecoveryStage = "Recovery",
                    MaximumProcessRelaunches = 8
                });
                Require(approval.ActionAllowed && approval.Record != null, "strict Host adapter did not approve");
                var intentResult = strict.BeginLaunch(approval.Record.Identity, "--strict-host-test");
                var capability = strict.GetLaunchCapability(approval.Record.Generation);
                Require(intentResult.Succeeded && capability != null, "strict Host adapter did not publish intent");
                var launcher = new GuardedProcessLauncher(
                    strict.IsCapabilityCurrent,
                    strict.ConsumeLaunchIntent);
                var forgedIntent = capability.CloneIntent();
                forgedIntent.IntentId = Guid.NewGuid().ToString("N");
                forgedIntent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(forgedIntent);
                var forged = new DurableLaunchIntentCapability(
                    forgedIntent, capability.AuthorityRevision, capability.AuthoritySha256);
                var rejected = false;
                try { launcher.Start(forged); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "forged capability crossed guarded boundary");
                Require(strict.Snapshot.State == DurableRelaunchPermitState.LaunchIntent &&
                        strict.Snapshot.ProcessId == 0 && strict.IsCapabilityCurrent(capability),
                    "forged launch mutated strict authority");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void HostGuardedProcessStartOneShot()
        {
            var dir = Path.Combine(Path.GetTempPath(), "strict-host-process-" + Guid.NewGuid().ToString("N"));
            var marker = Path.Combine(dir, "guarded-child.txt");
            var session = Guid.NewGuid().ToString("N");
            GuardedProcessOwnerReceipt owner = null;
            try
            {
                Directory.CreateDirectory(dir);
                using (var reporter = Process.GetCurrentProcess())
                {
                    var executable = Path.GetFullPath(reporter.MainModule.FileName);
                    var created = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                        dir, session, reporter.Id,
                        reporter.StartTime.ToUniversalTime().Ticks, 8);
                    Require(created.Succeeded, "strict process bootstrap failed: " + created.Reason);
                    var args = new WatchdogArguments
                    {
                        SessionId = session,
                        PipeName = "strict-host-process-pipe-" + session,
                        ExecutablePath = executable,
                        JournalDirectory = dir,
                        SidecarInstanceNonce = new string('b', 32),
                        JournalPolicy = new WatchdogJournalPolicy()
                    };
                    var strict = new StrictHostV4AuthorityAdapter(
                        args, reporter.Id, reporter.StartTime.ToUniversalTime().Ticks);
                    var approval = strict.ApproveOrGetExisting(new DurableRelaunchRequest
                    {
                        Fingerprint = "guarded-one-shot",
                        ProgressToken = "p1",
                        ProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                        RunId = "guarded-run",
                        RecoveryStage = "Recovery",
                        MaximumProcessRelaunches = 8
                    });
                    Require(approval.ActionAllowed, "strict process approval failed");
                    var launchArguments = "--watchdog-guarded-launch-child \"" + marker + "\"";
                    var intent = strict.BeginLaunch(approval.Record.Identity, launchArguments);
                    var capability = strict.GetLaunchCapability(approval.Record.Generation);
                    Require(intent.Succeeded && capability != null, "strict process intent failed");
                    var launcher = new GuardedProcessLauncher(
                        strict.IsCapabilityCurrent,
                        strict.ConsumeLaunchIntent);
                    owner = launcher.Start(capability);
                    Require(owner != null && owner.Process != null,
                        "guarded Process.Start returned no owner");
                    var secondRejected = false;
                    try { launcher.Start(capability); }
                    catch (InvalidOperationException) { secondRejected = true; }
                    Require(secondRejected,
                        "consumed capability crossed Process.Start twice");
                    var started = strict.CommitStarted(
                        approval.Record.Identity,
                        owner.Process.Id,
                        owner.StartUtcTicks);
                    Require(started.Succeeded &&
                            started.Record.State == DurableRelaunchPermitState.Started,
                        "real child Started identity was not committed");
                    Require(owner.Process.WaitForExit(10000),
                        "guarded child did not exit");
                    Require(File.Exists(marker) && File.ReadAllLines(marker).Length == 1,
                        "guarded child marker did not prove exactly one process body");
                    var reopened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                    Require(reopened.Succeeded, "guarded authority reopen failed");
                    var reconciled = reopened.Authority.ReconcileAfterRestart(
                        (pid, start) => DurableRelaunchProcessObservation.Dead);
                    Require(reconciled.Decision != null &&
                            !reconciled.ActionAllowed &&
                            reconciled.Decision.Durable &&
                            (reconciled.Decision.Record.State == DurableRelaunchPermitState.Failed ||
                             reconciled.Decision.Record.State == DurableRelaunchPermitState.Blocked),
                        "dead guarded child was not durably reconciled");
                }
            }
            finally
            {
                try { owner?.KillExactAndDispose(); } catch { }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private static void RealPipeFailureReceiptReplay(bool permanent)
        {
            var dir = Path.Combine(Path.GetTempPath(), "strict-host-receipt-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            var sidecar = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MTTFTest.Watchdog.exe");
            Process hostProcess = null;
            try
            {
                Directory.CreateDirectory(dir);
                Require(File.Exists(sidecar), "real Sidecar executable missing");
                var options = new WatchdogClientTransportOptions
                {
                    SessionId = session,
                    PipeName = "MTTFTest.StrictReceipt." + session,
                    MainExecutablePath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName),
                    SidecarExecutablePath = sidecar,
                    JournalDirectory = dir,
                    JournalPolicy = new WatchdogJournalPolicy(),
                    SessionGeneration = 1,
                    SelectedChannels = new[] { 4 }
                };
                using (var reporter = Process.GetCurrentProcess())
                {
                    var bootstrap = DurableRelaunchAuthorityV4Factory.TryCreatePristine(
                        dir, session, reporter.Id,
                        reporter.StartTime.ToUniversalTime().Ticks, 3);
                    Require(bootstrap.Succeeded,
                        "real receipt strict bootstrap failed: " + bootstrap.Reason);
                }

                var receiptGate = new object();
                var receipts = new System.Collections.Generic.List<RecoveryFailureReceipt>();
                var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var callbacks = new WatchdogClientTransportCallbacks
                {
                    CreateRunSession = (recovery, attempt) =>
                    {
                        using (var process = Process.GetCurrentProcess())
                            return new WatchdogRunSession
                            {
                                SessionId = session,
                                PipeName = options.PipeName,
                                ExecutablePath = options.MainExecutablePath,
                                ProcessId = process.Id,
                                ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                                RecoveryProcess = false,
                                RecoveryAttempt = 0,
                                SelectedChannels = new[] { 4 }
                            };
                    },
                    CaptureHeartbeat = () => new WatchdogHeartbeat
                    {
                        SessionId = session,
                        Phase = "StrictReceipt",
                        RunId = "strict-receipt-run"
                    },
                    RecoveryFailureReceiptReceived = message =>
                    {
                        if (message?.RecoveryFailureReceipt == null) return;
                        lock (receiptGate)
                            receipts.Add(message.RecoveryFailureReceipt.Clone());
                    },
                    RecordEvent = (eventType, detail) => trace.Enqueue("event:" + eventType + ":" + detail),
                    TransportError = (reason, detail) => trace.Enqueue("error:" + reason + ":" + detail),
                    TransportLost = (reason, detail) => trace.Enqueue("lost:" + reason + ":" + detail),
                    StopAllRequested = (reason, correlation) => { },
                    ObserveDurableStopMarker = () => false
                };

                using (var engine = new WatchdogClientTransportEngine(
                           new SystemSidecarProcessLauncher(),
                           new SystemNamedPipeClientFactory()))
                {
                    engine.BeginSession(options, callbacks);
                    Require(engine.StartAsync().Wait(20000),
                        "real receipt Engine did not attach");
                    var first = engine.CaptureSnapshot();
                    Require(first.IsAttached && first.AuthorityProcessId > 0,
                        "real receipt authority missing");
                    hostProcess = Process.GetProcessById(first.AuthorityProcessId);
                    var correlation = Guid.NewGuid().ToString("N");
                    var report = RecoveryFailurePolicy.NormalizeReport(
                        new RecoveryFailureReport
                        {
                            RootCode = permanent ? "StrictPermanentFailure" : "StrictRetryableFailure",
                            DeviceOrChannelGroup = "EPB4",
                            RunId = "strict-receipt-run",
                            RecoveryStage = "RecoveryAdmission",
                            RecoveryProgressToken = "1",
                            RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource
                        });
                    var request = new WatchdogMessage
                    {
                        ProtocolVersion = WatchdogProtocol.Version,
                        Type = WatchdogMessageType.RecoveryAttemptFailed,
                        SessionId = session,
                        CorrelationId = correlation,
                        Reason = "strict permanent diagnostic",
                        RecoveryFailureCode = report.RootCode,
                        RecoveryFailurePermanent = permanent,
                        RecoveryFailureDetail = permanent ? "strict permanent detail" : "strict retryable detail",
                        RecoveryFailureContextSha256 = new string('C', 64),
                        RecoveryFailureOwner = string.Empty,
                        RecoveryFailureCorrelationId = string.Empty,
                        RootCode = report.RootCode,
                        DeviceOrChannelGroup = report.DeviceOrChannelGroup,
                        RunId = report.RunId,
                        RecoveryStage = report.RecoveryStage,
                        RecoveryProgressToken = report.RecoveryProgressToken,
                        RecoveryProcessSource = report.RecoveryProcessSource,
                        RecoveryFailureFingerprint = RecoveryFailurePolicy.BuildFingerprint(report)
                    };
                    var raw = WatchdogProtocol.Serialize(request);
                    var rawSha = WatchdogProtocol.ComputeWireSha256(raw);
                    Require(WatchdogProtocol.TryParseRecoveryFailureRequestWire(
                                raw, session, out _, out var locallyParsedSha, out var localParseFailure) &&
                            locallyParsedSha == rawSha,
                        "real receipt request is not exact-v3: " + localParseFailure);
                    var firstConnection = first.ActiveConnectionGeneration;
                    Require(engine.TrySendForSessionWithDisposition(
                                request, session, options.SessionGeneration,
                                first.ActiveSessionLease) == WatchdogSendDisposition.Sent,
                        "real receipt request was not sent");
                    Require(WaitUntil(() =>
                    {
                        lock (receiptGate) return receipts.Count >= 1;
                    }, 15000), "real receipt was not returned;trace=" +
                        string.Join("|", trace.ToArray()) + ";authority=" +
                        DescribeAuthority(dir, session));
                    RecoveryFailureReceipt receipt1;
                    lock (receiptGate) receipt1 = receipts[0].Clone();
                    Require(receipt1.Durable && receipt1.FailureRegistered &&
                            receipt1.PermitClosed && receipt1.CircuitOpen == permanent &&
                            receipt1.RequestCorrelationId == correlation &&
                            receipt1.RequestPayloadSha256 == rawSha,
                        "real receipt was not authoritative/exact");
                    var afterFirst = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                    Require(afterFirst.Succeeded &&
                            afterFirst.Authority.Snapshot.CircuitOpen == permanent &&
                            (permanent || afterFirst.Authority.Snapshot.State == DurableRelaunchPermitState.Approved),
                        "real receipt decision was not durable");
                    var revision = afterFirst.Authority.Snapshot.AuthorityRevision;

                    engine.ScheduleReconnect(0);
                    Require(WaitUntil(() =>
                    {
                        var snapshot = engine.CaptureSnapshot();
                        return snapshot.IsAttached &&
                               snapshot.ActiveConnectionGeneration > firstConnection &&
                               snapshot.AuthorityProcessId == first.AuthorityProcessId &&
                               snapshot.AuthorityProcessStartUtcTicks ==
                                   first.AuthorityProcessStartUtcTicks &&
                               snapshot.AuthorityInstanceNonce == first.AuthorityInstanceNonce;
                    }, 20000), "real receipt same-authority reconnect failed");
                    var secondSnapshot = engine.CaptureSnapshot();
                    Require(engine.TrySendForSessionWithDisposition(
                                request, session, options.SessionGeneration,
                                secondSnapshot.ActiveSessionLease) == WatchdogSendDisposition.Sent,
                        "real receipt replay request was not sent");
                    Require(WaitUntil(() =>
                    {
                        lock (receiptGate) return receipts.Count >= 2;
                    }, 15000), "real replay receipt was not returned");
                    RecoveryFailureReceipt receipt2;
                    lock (receiptGate) receipt2 = receipts[1].Clone();
                    Require(receipt2.RequestCorrelationId == receipt1.RequestCorrelationId &&
                            receipt2.RequestPayloadSha256 == receipt1.RequestPayloadSha256 &&
                            receipt2.DecisionSequence == receipt1.DecisionSequence &&
                            receipt2.Disposition == receipt1.Disposition,
                        "same raw request did not replay the exact durable receipt");
                    var afterReplay = DurableRelaunchAuthorityV4Factory.TryOpenExisting(dir, session);
                    Require(afterReplay.Succeeded &&
                            afterReplay.Authority.Snapshot.AuthorityRevision == revision,
                        "receipt replay advanced durable authority");
                    engine.MarkSessionClosing();
                    engine.Send(new WatchdogMessage
                    {
                        ProtocolVersion = WatchdogProtocol.Version,
                        Type = WatchdogMessageType.ApplicationClosing,
                        SessionId = session,
                        Reason = "StrictReceiptComplete"
                    });
                    WaitUntil(() => hostProcess.HasExited, 5000);
                    var shutdown = engine.ShutdownWithReceipt();
                    Require(shutdown != null && shutdown.AllWorkersTerminal &&
                            shutdown.AllResourcesReleased,
                        "real receipt transport did not close");
                }
            }
            finally
            {
                try
                {
                    if (hostProcess != null && !hostProcess.HasExited)
                    {
                        hostProcess.Kill();
                        hostProcess.WaitForExit(5000);
                    }
                }
                catch { }
                try { hostProcess?.Dispose(); } catch { }
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private static bool WaitUntil(Func<bool> predicate, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                try { if (predicate()) return true; } catch { }
                Thread.Sleep(25);
            }
            try { return predicate(); } catch { return false; }
        }

        private static string DescribeAuthority(string directory, string session)
        {
            try
            {
                var opened = DurableRelaunchAuthorityV4Factory.TryOpenExisting(directory, session);
                var record = opened?.Authority?.Snapshot;
                return opened == null ? "null" :
                    opened.Succeeded + "/" + opened.Reason + "/" +
                    (record == null ? "no-record" :
                        record.State + "/rev=" + record.AuthorityRevision +
                        "/disp=" + record.LastFailureDisposition +
                        "/corr=" + record.LastFailureCorrelationId +
                        "/sha=" + record.LastFailurePayloadSha256);
            }
            catch (Exception ex) { return ex.GetBaseException().Message; }
        }

        private static DurableLaunchIntent Intent(DurableRelaunchAuthorityRecord record, string session)
        {
            var exe = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            var hash = Sha256File(exe);
            var intent = new DurableLaunchIntent
            {
                SessionId = session, SessionNonce = new string('a', 32), Generation = record.Generation,
                PermitId = record.PermitId, PermitNonce = record.PermitNonce,
                IntentId = Guid.NewGuid().ToString("N"), ExecutablePath = exe,
                ExecutableSha256 = hash, Arguments = "--strict-v4-test",
                WorkingDirectory = Path.GetDirectoryName(exe),
                LaunchOptionsCanonical = "UseShellExecute=false;CreateNoWindow=true"
            };
            intent.LaunchSpecSha256 = DurableLaunchCanonical.Sha256(intent);
            return intent;
        }

        private static RecoveryFailureOperation Operation(string session, string correlation)
        {
            return new RecoveryFailureOperation
            {
                OperationId = Guid.NewGuid().ToString("N"), SessionId = session, SessionNonce = new string('a', 32),
                SidecarProcessId = Process.GetCurrentProcess().Id,
                SidecarProcessStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                ConnectionGeneration = 1, RecoveryAttemptGeneration = 1,
                RequestCorrelationId = correlation, RequestPayloadSha256 = new string('B', 64),
                FailureCode = "StrictHostFailure", FailureFingerprint = "StrictHostFingerprint",
                DetailCode = "StrictHostFailure", RunId = "strict-run", RunEpoch = 1,
                RecoveryStage = "Recovery", RecoveryProgressToken = "p1",
                RecoveryProcessSource = "StrictHost", DeviceOrChannelGroup = "Host",
                MaximumProcessRelaunches = 8
            };
        }

        private static void WithAuthority(Action<string, string, DurableRelaunchAuthorityV4> action)
        {
            var dir = Path.Combine(Path.GetTempPath(), "strict-host-v4-" + Guid.NewGuid().ToString("N"));
            var session = Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(dir);
                var process = Process.GetCurrentProcess();
                var created = DurableRelaunchAuthorityV4Factory.TryCreatePristine(dir, session, process.Id, process.StartTime.ToUniversalTime().Ticks, 8);
                Require(created.Succeeded, created.Reason);
                action(dir, session, created.Authority);
            }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        }

        private static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
        }

        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty).ToUpperInvariant();
        }

        private static void Run(string name, Action action, ref int passed)
        {
            try { action(); passed++; }
            catch (Exception ex) { throw new InvalidOperationException(name + ": " + ex.Message, ex); }
        }

        private static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail ?? "strict host assertion failed");
        }
    }
}
