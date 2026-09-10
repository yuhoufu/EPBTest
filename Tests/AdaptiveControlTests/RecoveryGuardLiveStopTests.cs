using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Controller;
using MTEmbTest;
using MTTFTest.RecoveryControl;

namespace AdaptiveControlTests
{
    internal static class RecoveryGuardLiveStopTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Check("仅当前阶段旧核心退出等待允许重新检查", f =>
            {
                var pending = new RecoveryStopPendingException();
                Require(RecoveryGuardRuntime.ShouldRetryAutomaticStop(pending, "tx:1:2", "tx:1:2"));
                Require(!RecoveryGuardRuntime.ShouldRetryAutomaticStop(pending, "tx:1:2", "tx:2:3"));
                Require(!RecoveryGuardRuntime.ShouldRetryAutomaticStop(pending, null, null));
                Require(!RecoveryGuardRuntime.ShouldRetryAutomaticStop(
                    new InvalidOperationException(pending.Message), "tx:1:2", "tx:1:2"));
                Require(!RecoveryGuardRuntime.ShouldRetryAutomaticStop(
                    new TimeoutException(), "tx:1:2", "tx:1:2"));
            }, ref passed);
            foreach (var change in new[] { "valid", "run", "epoch", "transaction", "generation", "missing" })
                Check("停止后恢复只接受同一保留事务 " + change, f =>
                {
                    var run = f.Result.RunId;
                    var tx = f.Result.SafetyTransactionId;
                    f.Result.RunEpoch = 1;
                    switch (change)
                    {
                        case "run": f.Result.RunId = Guid.NewGuid(); break;
                        case "epoch": f.Result.RunEpoch++; break;
                        case "transaction": f.Result.SafetyTransactionId = Guid.NewGuid(); break;
                        case "generation": f.Result.SafetyBoundaryGeneration++; break;
                    }
                    Require(EpbManager.MatchesRetainedRecoveryStop(run, run, 1, tx, 1,
                        change == "missing" ? null : f.Result) == (change == "valid"));
                }, ref passed);
            foreach (var change in new[] { "valid", "run", "epoch", "owner", "orphan", "scope", "inactive", "deadline", "unowned" })
                Check("恢复阶段映射限定同一运行所有者与参与范围 " + change, f =>
                {
                    var run = Guid.NewGuid(); var owner = Guid.NewGuid();
                    var channel = new ChannelRuntimeStateChangedEvent
                    {
                        Channel = 1, State = ChannelRuntimeState.Recovering, RunEpoch = 2,
                        RecoveryOwnerKind = RecoveryOwnerKind.DaqRecovery, RecoveryOwnerId = owner,
                        RecoveryOwnerGeneration = 2, RecoveryTargetPhase = RecoveryTargetPhase.Formal
                    };
                    var source = new InfrastructureRecoverySource
                    {
                        ActiveRecovery = true, RunId = run, RunEpoch = 2, CorrelationId = owner,
                        ExpectedRecoveryChannels = new[] { 1 }, RecoveringChannels = new[] { 1 },
                        Stage = "Qualification", StageOrdinal = 3, StageStartedUtcTicks = f.Now.Ticks,
                        HardDeadlineUtcTicks = f.Now.AddSeconds(20).Ticks,
                        RecoveryHardDeadlineUtcTicks = f.Now.AddSeconds(10).Ticks
                    };
                    switch (change)
                    {
                        case "run": source.RunId = Guid.NewGuid(); break;
                        case "epoch": channel.RunEpoch++; break;
                        case "owner": source.CorrelationId = Guid.NewGuid(); break;
                        case "orphan": source.OrphanRecoveryChannels = new[] { 1 }; break;
                        case "scope": source.ExpectedRecoveryChannels = new[] { 2 }; break;
                        case "inactive": source.ActiveRecovery = false; break;
                        case "deadline": source.HardDeadlineUtcTicks = 0; break;
                        case "unowned": channel.RecoveryOwnerKind = RecoveryOwnerKind.Unknown; break;
                    }
                    var projected = RecoveryGuardRuntime.TryProjectRecoveryStage(channel, source, run, 2,
                        out var stage, out var start, out var deadline);
                    Require(projected == (change == "valid"));
                    if (!projected) Require(stage == null && start == 0 && deadline == 0);
                    else
                    {
                        Require(start == f.Now.Ticks && deadline == f.Now.AddSeconds(10).Ticks);
                        source.ProgressVersion++;
                        Require(RecoveryGuardRuntime.TryProjectRecoveryStage(channel, source, run, 2,
                            out var again, out var againStart, out var againDeadline));
                        Require(stage == again && start == againStart && deadline == againDeadline);
                        AssertProjectedStagePolicy(stage, start, deadline);
                    }
                }, ref passed);
            foreach (var failure in new[] { "DO-false", "DO-throw", "AO-false" })
                Check("卸压输出失败不得伪报已发出 " + failure, f =>
                {
                    var doCalls = 0; var aoCalls = 0; var rejected = false;
                    try
                    {
                        HydraulicController.ConfirmReleaseOutputCommands(() =>
                        {
                            doCalls++;
                            if (failure == "DO-throw") throw new InvalidOperationException("hardware failure");
                            return failure != "DO-false";
                        }, () => { aoCalls++; return failure != "AO-false"; });
                    }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected && doCalls == 1 && aoCalls == 1);
                }, ref passed);
            Check("安全与数据已确认时不等待旧逻辑owner", f =>
            {
                Require(!f.Result.LogicalQuiescenceConfirmed && f.Result.RequiresProcessRestart);
                Require(f.Run()); Require(f.Stops == 1 && f.Exits == 1);
                Require(f.State.Intent.DesiredState == RecoveryDesiredState.Run);
            }, ref passed);
            foreach (var field in new[] { "current", "pressure", "power", "motor", "persistence", "raw", "gap", "cached", "old-result" })
                Check("不完整或旧停止结果不得退出 " + field, f =>
                {
                    switch (field)
                    {
                        case "current": f.Result.CurrentSafeConfirmed = false; break;
                        case "pressure": f.Result.PressureSafeConfirmed = false; break;
                        case "power": f.Result.PowerOffConfirmed = false; break;
                        case "motor": f.Result.MotorOffCommandSucceeded = false; break;
                        case "persistence": f.Result.PersistenceBoundaryConfirmed = false; break;
                        case "raw": f.Result.RawStorageFlushed = false; break;
                        case "gap": f.Result.DataContinuityCompromised = true; break;
                        case "cached": f.Result.ReusedPreviousResult = true; break;
                        case "old-result": f.Result.StartedUtc = f.Now.AddSeconds(-1); break;
                    }
                    Require(!f.Run() && f.Exits == 0);
                    var expectedReason = field == "cached" ? "PreviousStopResultRequiresFreshEvidence" :
                        field == "old-result" ? "StopEvidenceTimeInvalid" :
                        field == "persistence" || field == "raw" || field == "gap"
                            ? "AcceptedDataBoundaryUnconfirmed" : "CurrentPressureOrOffCommandUnconfirmed";
                    Require(f.RejectionReason == expectedReason);
                }, ref passed);
            foreach (var change in new[] { "stop", "pause", "epoch", "owner", "session", "process", "stage", "configuration" })
                Check("安全停止期间身份或人工意图变化禁止退出 " + change, f =>
                {
                    Require(!f.Run(() =>
                    {
                        switch (change)
                        {
                            case "stop": f.State.Intent.DesiredState = RecoveryDesiredState.Stopped; break;
                            case "pause": f.State.Intent.DesiredState = RecoveryDesiredState.Paused; break;
                            case "epoch": f.State.LastTakeoverEpoch++; f.State.Transaction.Epoch++; break;
                            case "owner": f.State.Transaction.Owner.ProcessId++; break;
                            case "session": f.State.Intent.WatchdogSessionId = Guid.NewGuid().ToString("N"); break;
                            case "process": f.State.Intent.MainProcess.StartUtcTicks++; break;
                            case "stage": f.State.Transaction.Stage = RecoveryStage.Cooldown; break;
                            case "configuration": f.State.Intent.ConfigurationIdentity = "changed"; break;
                        }
                    }) && f.Exits == 0);
                }, ref passed);
            Check("过期接管不触发停止", f =>
            {
                f.State.Transaction.LeaseUntilUtcTicks = f.Now.Ticks;
                Require(!f.Run() && f.Stops == 0 && f.Exits == 0);
            }, ref passed);
            Check("旧事务结果不归属于当前接管", f =>
            {
                f.Result.CorrelationId = Guid.NewGuid().ToString("N");
                Require(!f.Run() && f.Exits == 0);
            }, ref passed);
            Check("owner无精确存活证据不触发停止", f =>
                Require(!f.Run(ownerAlive: false) && f.Stops == 0 && f.Exits == 0), ref passed);
            foreach (var mode in new[] { "safe", "unsafe", "stale-owner" })
                Check("隔离子进程退出门禁 " + mode, f => RunChildProcess(mode), ref passed);
            return passed;
        }

        private static void AssertProjectedStagePolicy(string stage, long started, long deadline)
        {
            foreach (var mode in new[] { "progress", "sample-stalled", "expired" })
            {
                var root = Path.Combine(Path.GetTempPath(), "GuardProjectionPolicy-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var store = new RecoveryControlStore(root);
                    var main = RecoveryProcessProbe.Current();
                    var now = new DateTime(started, DateTimeKind.Utc);
                    var run = Guid.NewGuid().ToString("N");
                    store.Register("projection-test", main.ExecutablePath);
                    var token = store.BeginManualRun(run, run, "projection-test-config", main, now);
                    var settings = new RecoveryGuardSettings
                    {
                        Mode = RecoveryGuardMode.RecoverStalled, ScanSeconds = 1,
                        SnapshotMaxAgeSeconds = 2, BusinessStallSeconds = 3, FirstAdmissionSeconds = 60
                    };
                    RecoveryDecision decision = null;
                    for (var tick = 0; tick <= (mode == "expired" ? 15 : 8); tick++)
                    {
                        now = new DateTime(started, DateTimeKind.Utc).AddSeconds(tick);
                        var snapshot = new RecoveryObservationSnapshot
                        {
                            Authorization = token, RunId = run, ConfigurationIdentity = "projection-test-config",
                            MainProcess = main, Sequence = tick + 1, SourceVersion = tick + 1,
                            SourceAvailable = true, PublishedUtcTicks = now.Ticks, SourceUtcTicks = now.Ticks,
                            Channels = new[] { new RecoveryChannelProgress
                            {
                                Channel = 1, Eligible = true, SampleGeneration = 1,
                                SampleSequence = 10 + (mode == "sample-stalled" ? Math.Min(1, tick) : tick),
                                ControlSequence = 10, PersistedSequence = 10,
                                Stage = stage, StageStartedUtcTicks = started, StageDeadlineUtcTicks = deadline
                            } }
                        };
                        decision = store.Observe(snapshot, ProcessObservation.ExactAlive,
                            main.BootId, now, settings, false);
                        if (mode == "progress" && tick > 0) Require(decision.Code == "Healthy");
                    }
                    Require(mode == "progress" ? decision.Code == "Healthy" : decision.CanClaim);
                }
                finally
                {
                    // Unique test-only store; never points at a real installation.
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
        }

        private static void RunChildProcess(string mode)
        {
            var owner = RecoveryProcessProbe.Current();
            using (var child = Process.Start(new ProcessStartInfo
            {
                FileName = owner.ExecutablePath,
                Arguments = "--guard-live-stop-child " + owner.ProcessId + " " +
                    owner.StartUtcTicks.ToString(CultureInfo.InvariantCulture) + " " + mode,
                UseShellExecute = false, CreateNoWindow = true
            }))
            {
                Require(child != null);
                try
                {
                    Require(child.WaitForExit(10000));
                    Require(child.ExitCode == (mode == "safe" ? 73 : 74));
                }
                finally
                {
                    // Only the test process created by this invocation is cleaned up.
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                }
            }
        }

        internal static int RunChild(string ownerPid, string ownerTicks, string mode)
        {
            var current = RecoveryProcessProbe.Current();
            var owner = new RecoveryProcessIdentity
            {
                ProcessId = int.Parse(ownerPid, CultureInfo.InvariantCulture),
                StartUtcTicks = long.Parse(ownerTicks, CultureInfo.InvariantCulture),
                ExecutablePath = current.ExecutablePath, BootId = current.BootId
            };
            if (mode == "stale-owner") owner.StartUtcTicks++;
            else if (mode != "safe" && mode != "unsafe") return 75;
            var fixture = new Fixture(owner);
            // Explicit simulated stop evidence: this test exercises real process
            // identity and Environment.Exit, not hardware or persisted authority.
            if (mode == "unsafe") fixture.Result.CurrentSafeConfirmed = false;
            var retired = fixture.Run(retire: () => Environment.Exit(73),
                probe: identity => RecoveryProcessProbe.Observe(identity,
                    RecoveryProcessProbe.ReadBootId()) == ProcessObservation.ExactAlive);
            Require(!retired);
            Require(fixture.Stops == (mode == "stale-owner" ? 0 : 1));
            return 74;
        }

        private sealed class Fixture
        {
            internal readonly DateTime Now = DateTime.UtcNow;
            internal readonly RecoveryControlState State;
            internal readonly StopSafetyResult Result;
            private readonly RecoveryGuardLiveStop _request;
            internal int Stops;
            internal int Exits;
            internal string RejectionReason => _request.RejectionReason;

            internal Fixture(RecoveryProcessIdentity actualOwner = null)
            {
                var main = RecoveryProcessProbe.Current();
                var owner = actualOwner ?? new RecoveryProcessIdentity
                {
                    ProcessId = main.ProcessId + 1, StartUtcTicks = main.StartUtcTicks,
                    BootId = main.BootId, ExecutablePath = main.ExecutablePath
                };
                var run = Guid.NewGuid();
                var token = new RecoveryAuthorizationToken
                {
                    InstallationId = Guid.NewGuid().ToString("N"), AuthorizationId = Guid.NewGuid().ToString("N"),
                    IntentVersion = 1, TakeoverEpoch = 0
                };
                State = new RecoveryControlState
                {
                    InstallationId = token.InstallationId, MainExecutablePath = main.ExecutablePath, LastTakeoverEpoch = 1,
                    Intent = new RecoveryRunIntent
                    {
                        AuthorizationId = token.AuthorizationId, IntentVersion = 1, RunId = run.ToString("N"),
                        RootRunId = run.ToString("N"), ConfigurationIdentity = "immutable-config",
                        MainProcess = main, WatchdogSessionId = Guid.NewGuid().ToString("N"), DesiredState = RecoveryDesiredState.Run
                    },
                    Transaction = new RecoveryTakeoverTransaction
                    {
                        TransactionId = Guid.NewGuid().ToString("N"), AuthorizationId = token.AuthorizationId,
                        IntentVersion = 1, Epoch = 1, Owner = owner, Stage = RecoveryStage.SafeStop,
                        StageStartedUtcTicks = Now.AddSeconds(-1).Ticks, StageDeadlineUtcTicks = Now.AddMinutes(1).Ticks,
                        LeaseUntilUtcTicks = Now.AddMinutes(1).Ticks
                    }
                };
                _request = RecoveryGuardLiveStop.TryCreate(State, token, main, run.ToString("N"), Now);
                Require(_request != null);
                Result = new StopSafetyResult
                {
                    Source = StopSource.SystemFault, RunId = run, CorrelationId = State.Transaction.TransactionId,
                    SafetyTransactionId = Guid.NewGuid(), SafetyBoundaryGeneration = 1,
                    StartedUtc = Now, CompletedUtc = Now, MotorOffCommandSucceeded = true, PowerOffConfirmed = true,
                    CurrentSafeConfirmed = true, PressureSafeConfirmed = true, PersistenceBoundaryConfirmed = true,
                    RawStorageFlushed = true, RequiresProcessRestart = true, LogicalQuiescenceConfirmed = false
                };
            }

            internal bool Run(Action duringStop = null, bool ownerAlive = true,
                Action retire = null, Func<RecoveryProcessIdentity, bool> probe = null) => _request.RunAsync(
                () => State, context =>
                {
                    Require(context.Source == StopSource.SystemFault && context.Initiator == "RecoveryGuardAutomaticSafeStop");
                    Require(context.RunId == State.Intent.RunId && context.CorrelationId == State.Transaction.TransactionId);
                    Stops++; duringStop?.Invoke(); return Task.FromResult(Result);
                }, retire ?? (() => Exits++), probe ?? (process => ownerAlive), () => Now).GetAwaiter().GetResult();
        }

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Guard协作安全退出断言失败");
        }

        private static void Check(string name, Action<Fixture> test, ref int passed)
        {
            test(new Fixture()); passed++; Console.WriteLine("PASS " + name);
        }
    }
}
