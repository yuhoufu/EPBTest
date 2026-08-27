using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;
using MTTFTest.Watchdog;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Deterministic host/bootstrap seam tests.  They exercise the production
    /// schema4 bootstrap and durable coordinator used by WatchdogHost; no
    /// duplicate recovery algorithm is maintained in the test.
    /// </summary>
    internal static class WatchdogHostIntegrationTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("Host schema4 bootstrap先于Sidecar且只允许一次", BootstrapIsSchema4AndSingleUse, ref passed);
            Run("Host bootstrap非法身份故障闭锁", InvalidBootstrapFailsClosed, ref passed);
            Run("Host生产编排注入端口仍由durable permit唯一授权", ProductionOrchestratorUsesInjectedPorts, ref passed);
            Run("Host生产接管端到端严格Dump到Permit到Terminate到Launch",
                ProductionTakeoverPipelineIsMonotonic,
                ref passed);
            Run("Host生产接管阶段回退明确拒绝且无副作用",
                ProductionTakeoverRegressionIsObservable,
                ref passed);
            Run("Host恢复通道意图从初始进程冻结并跨Sidecar重载保持",
                RecoveryChannelIntentIsFrozenAndDurable,
                ref passed);
            Run("有效活动Run快照不被空RunId或恢复子进程心跳覆盖",
                LastVerifiedActiveRunSurvivesCleanupHeartbeat,
                ref passed);
            return passed;
        }

        private static void LastVerifiedActiveRunSurvivesCleanupHeartbeat()
        {
            var runId = Guid.NewGuid().ToString("N");
            var journal = new WatchdogJournal
            {
                SessionId = "verified-active-run",
                CurrentPid = 701,
                CurrentProcessStartUtcTicks = 7001,
                LastCheckpointMirror = new WatchdogCheckpointMirror
                {
                    Armed = true,
                    SessionId = "verified-active-run",
                    RunId = runId,
                    RunEpoch = 17,
                    Revision = 9,
                    Sha256 = "checkpoint-sha",
                    SelectedChannels = new[] { 4, 5, 11, 12 }
                }
            };
            var active = new WatchdogHeartbeat
            {
                Sequence = 88,
                ProcessId = 701,
                ProcessStartUtcTicks = 7001,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = runId,
                RunEpoch = 17,
                RunActive = true,
                EnabledChannels = new[] { 4, 5, 11, 12 },
                RecoveryEligibleChannels = new[] { 4, 5, 11 },
                CompletedChannels = new[] { 12 },
                ManuallyDisabledChannels = new[] { 5 },
                PermanentAlarmedChannels = new[] { 11 }
            };
            Assert(WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       active,
                       identityValidated: true) &&
                   journal.LastVerifiedActiveRun.SelectedChannels.SequenceEqual(
                       new[] { 4, 5, 11, 12 }) &&
                   journal.LastVerifiedActiveRun.CheckpointRevision == 9,
                "有效InitialProcess活动运行没有保存匹配checkpoint身份");

            var cleanup = new WatchdogHeartbeat
            {
                Sequence = 89,
                ProcessId = 701,
                ProcessStartUtcTicks = 7001,
                RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                RunId = string.Empty,
                RunEpoch = 0,
                RunActive = false
            };
            journal.LastHeartbeat = cleanup;
            Assert(!WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       cleanup,
                       identityValidated: true),
                "StopAll空RunId心跳覆盖了最后有效活动运行");
            var recoveryChild = new WatchdogHeartbeat
            {
                Sequence = 90,
                ProcessId = 702,
                ProcessStartUtcTicks = 7002,
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = runId,
                RunEpoch = 18,
                RunActive = true,
                ManuallyDisabledChannels = Enumerable.Range(1, 12).ToArray()
            };
            Assert(!WatchdogRecoveryChannelIntentPolicy.TryCaptureLastVerifiedActiveRun(
                       journal,
                       recoveryChild,
                       identityValidated: true),
                "Recovery child污染了InitialProcess活动运行快照");
            journal.CurrentPid = 702;
            journal.CurrentProcessStartUtcTicks = 7002;
            journal.LastHeartbeat = recoveryChild;
            var resolution = WatchdogRecoveryChannelIntentPolicy.Resolve(journal);
            Assert(resolution.Succeeded &&
                   resolution.ExcludedChannels.SequenceEqual(new[] { 5, 11, 12 }) &&
                   journal.FrozenExcludedSourceRunId == runId &&
                   journal.FrozenExcludedSourceRunEpoch == 17,
                "清场空RunId后首次拉起未优先使用LastVerifiedActiveRun");

            var fallback = new WatchdogJournal
            {
                SessionId = "checkpoint-fallback",
                CurrentPid = 801,
                CurrentProcessStartUtcTicks = 8001,
                LastCheckpointMirror = new WatchdogCheckpointMirror
                {
                    Armed = true,
                    SessionId = "checkpoint-fallback",
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 3,
                    SelectedChannels = new[] { 4, 5 }
                }
            };
            Assert(WatchdogRecoveryChannelIntentPolicy.Resolve(fallback).Succeeded,
                "无活动快照时未使用身份匹配且Armed的checkpoint mirror回退");
        }

        private static void ProductionOrchestratorUsesInjectedPorts()
        {
            var store = new InMemoryDurableRelaunchStore();
            var launcher = new CountingLauncher();
            var orchestrator = new HostRelaunchOrchestrator(
                "host-production-seam",
                store,
                3,
                launcher);
            var request = new DurableRelaunchRequest
            {
                Fingerprint = "host-fingerprint",
                ProgressToken = "daq-stage-1",
                ProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = "run-host",
                RecoveryStage = "DaqRecovery",
                MaximumProcessRelaunches = 3
            };
            var approvals = new DurableRelaunchResult[64];
            System.Threading.Tasks.Parallel.For(0, approvals.Length, index =>
            {
                approvals[index] = orchestrator.ApproveOrGetExisting(request);
            });
            Assert(approvals.Count(item => item.Succeeded && !item.Existing) == 1,
                "Host生产编排在并发failure下批准了多个permit");
            var approved = orchestrator.Snapshot;
            var begins = new DurableRelaunchResult[64];
            System.Threading.Tasks.Parallel.For(0, begins.Length, index =>
            {
                begins[index] = orchestrator.BeginLaunch(approved.Identity);
            });
            Assert(begins.Count(item => item.ActionAllowed) == 1,
                "Host生产编排重复释放了BeginLaunch动作");
            var startInfo = new ProcessStartInfo { FileName = "injected-test-launcher" };
            orchestrator.Start(startInfo);
            Assert(launcher.StartCount == 1, "注入Process launcher没有被生产编排使用");
            Assert(orchestrator.CommitStarted(approved.Identity, 301, 3001).ActionAllowed,
                "Host生产编排没有提交Started身份");
            Assert(orchestrator.CommitAttached(approved.Identity, 301, 3001).ActionAllowed,
                "Host生产编排没有提交Attached身份");
            Assert(orchestrator.CommitRecoveryBatch(
                       approved.Identity,
                       "run-host",
                       "daq-stage-2",
                       2).ActionAllowed &&
                   orchestrator.Snapshot.State == DurableRelaunchPermitState.Committed,
                "Host生产编排没有以RecoveryBatch提交Committed");
        }

        private static void RecoveryChannelIntentIsFrozenAndDurable()
        {
            var journal = new WatchdogJournal
            {
                CurrentPid = 101,
                CurrentProcessStartUtcTicks = 1001,
                RelaunchState = DurableRelaunchPermitState.Approved.ToString(),
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 101,
                    ProcessStartUtcTicks = 1001,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 7,
                    RunActive = true,
                    ManuallyDisabledChannels = new[] { 4, 2, 4 },
                    CompletedChannels = new[] { 1 },
                    PermanentAlarmedChannels = new[] { 12, 2 }
                }
            };
            var persistenceCalls = 0;
            var first = WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                journal,
                () =>
                {
                    persistenceCalls++;
                    return true;
                });
            Assert(first.Succeeded && first.NewlyFrozen &&
                   first.ExcludedChannels.SequenceEqual(new[] { 1, 2, 4, 12 }) &&
                   journal.RecoveryChannelIntentFrozen &&
                   journal.FrozenExcludedSourceProcessId == 101 &&
                   journal.FrozenExcludedSourceProcessStartUtcTicks == 1001 &&
                   journal.FrozenExcludedSourceRunEpoch == 7 &&
                   persistenceCalls == 1,
                "初始进程恢复通道意图未排序去重并冻结完整身份。");

            var serializer = new JavaScriptSerializer();
            var reloaded = serializer.Deserialize<WatchdogJournal>(
                serializer.Serialize(journal));
            reloaded.CurrentPid = 202;
            reloaded.CurrentProcessStartUtcTicks = 2002;
            reloaded.LastHeartbeat = new WatchdogHeartbeat
            {
                ProcessId = 202,
                ProcessStartUtcTicks = 2002,
                RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                RunId = journal.LastHeartbeat.RunId,
                RunEpoch = 8,
                ManuallyDisabledChannels = Enumerable.Range(1, 12).ToArray(),
                CompletedChannels = Enumerable.Range(1, 12).ToArray(),
                PermanentAlarmedChannels = Enumerable.Range(1, 12).ToArray()
            };
            var child = WatchdogRecoveryChannelIntentPolicy.Resolve(reloaded);
            Assert(child.Succeeded && !child.NewlyFrozen &&
                   child.ExcludedChannels.SequenceEqual(new[] { 1, 2, 4, 12 }),
                "恢复子进程全禁用心跳污染了冻结通道集合，或Sidecar重载丢失集合。");

            var empty = new WatchdogJournal
            {
                CurrentPid = 303,
                CurrentProcessStartUtcTicks = 3003,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 303,
                    ProcessStartUtcTicks = 3003,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 1,
                    RunActive = true
                }
            };
            var emptyResult = WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                empty,
                () => true);
            Assert(emptyResult.Succeeded && emptyResult.NewlyFrozen &&
                   empty.RecoveryChannelIntentFrozen &&
                   emptyResult.ExcludedChannels.Length == 0,
                "空排除集合没有用冻结标志持久区分。");

            var persistenceFailure = new WatchdogJournal
            {
                CurrentPid = 505,
                CurrentProcessStartUtcTicks = 5005,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 505,
                    ProcessStartUtcTicks = 5005,
                    RecoveryProcessSource = RecoveryFailurePolicy.InitialProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 3,
                    RunActive = true
                }
            };
            var failedPersistenceCalls = 0;
            var failedPersistence =
                WatchdogRecoveryChannelIntentPolicy.ResolveAndPersist(
                    persistenceFailure,
                    () =>
                    {
                        failedPersistenceCalls++;
                        return false;
                    });
            Assert(!failedPersistence.Succeeded &&
                   failedPersistence.FailureReason ==
                   "FrozenExcludedChannelsPersistenceFailed" &&
                   failedPersistenceCalls == 1,
                "冻结集合同步持久化失败未阻断首个launch intent。");

            reloaded.FrozenExcludedChannels = new[] { 1, 1, 13 };
            Assert(!WatchdogRecoveryChannelIntentPolicy.Resolve(reloaded).Succeeded,
                "非法、重复或越界的冻结集合未fail closed。");

            var missing = new WatchdogJournal
            {
                CurrentPid = 404,
                CurrentProcessStartUtcTicks = 4004,
                LastHeartbeat = new WatchdogHeartbeat
                {
                    ProcessId = 404,
                    ProcessStartUtcTicks = 4004,
                    RecoveryProcessSource = RecoveryFailurePolicy.RecoveryProcessSource,
                    RunId = Guid.NewGuid().ToString("N"),
                    RunEpoch = 2,
                    RunActive = true
                }
            };
            var missingResult = WatchdogRecoveryChannelIntentPolicy.Resolve(missing);
            Assert(!missingResult.Succeeded &&
                   missingResult.FailureReason ==
                   "FrozenExcludedChannelsInitialSourceInvalid",
                "恢复代次缺失冻结证据时未fail closed。");

            missing.LastHeartbeat.RecoveryProcessSource =
                RecoveryFailurePolicy.InitialProcessSource;
            missing.RelaunchState = DurableRelaunchPermitState.Started.ToString();
            var startedWithoutFreeze =
                WatchdogRecoveryChannelIntentPolicy.Resolve(missing);
            Assert(!startedWithoutFreeze.Succeeded &&
                   startedWithoutFreeze.FailureReason ==
                   "FrozenExcludedChannelsMissingAfterRecoveryStarted",
                "已进入Started代次但缺失冻结证据时未fail closed。");
        }

        private static void ProductionTakeoverPipelineIsMonotonic()
        {
            var coordinator = new TakeoverTransactionCoordinator();
            Assert(coordinator.TryBegin("host-takeover", "authority", out var lease),
                "Host生产接管事务无法创建");
            var events = new List<string>();
            var result = AutomaticTakeoverStageExecutor.ExecuteAsync(
                    coordinator,
                    lease,
                    () => coordinator.IsAuthorized(lease),
                    () => { events.Add("dump"); return System.Threading.Tasks.Task.CompletedTask; },
                    () => { events.Add("permit"); return 17; },
                    () => { events.Add("terminate"); return true; },
                    permit =>
                    {
                        events.Add("launch:" + permit);
                        return System.Threading.Tasks.Task.CompletedTask;
                    },
                    (current, requested, failure) =>
                        events.Add($"rejected:{current}:{requested}:{failure}"))
                .GetAwaiter()
                .GetResult();
            Assert(result.Succeeded && result.PermitGeneration == 17 &&
                   lease.Stage == TakeoverTransactionStage.Relaunching &&
                   events.SequenceEqual(new[] { "dump", "permit", "terminate", "launch:17" }),
                "生产接管没有按Dump→Permit→Terminate→Launch执行：" +
                string.Join(",", events));
            coordinator.Complete(lease);
        }

        private static void ProductionTakeoverRegressionIsObservable()
        {
            var coordinator = new TakeoverTransactionCoordinator();
            Assert(coordinator.TryBegin("host-regression", "authority", out var lease) &&
                   coordinator.TryAdvance(lease, TakeoverTransactionStage.RelaunchPermit),
                "阶段回退测试无法建立错误前态");
            var actions = 0;
            TakeoverTransactionStage current = 0;
            TakeoverTransactionStage requested = 0;
            var result = AutomaticTakeoverStageExecutor.ExecuteAsync(
                    coordinator,
                    lease,
                    () => true,
                    () => { Interlocked.Increment(ref actions); return System.Threading.Tasks.Task.CompletedTask; },
                    () => { Interlocked.Increment(ref actions); return 1; },
                    () => { Interlocked.Increment(ref actions); return true; },
                    _ => { Interlocked.Increment(ref actions); return System.Threading.Tasks.Task.CompletedTask; },
                    (observed, attempted, _) =>
                    {
                        current = observed;
                        requested = attempted;
                    })
                .GetAwaiter()
                .GetResult();
            Assert(!result.Succeeded && actions == 0 &&
                   current == TakeoverTransactionStage.RelaunchPermit &&
                   requested == TakeoverTransactionStage.DumpCapture,
                "生产接管阶段30→20未明确拒绝或仍执行了副作用");
            coordinator.Complete(lease);
        }

        private sealed class CountingLauncher : ISystemRelaunchProcessLauncher
        {
            internal int StartCount;

            public Process Start(ProcessStartInfo startInfo)
            {
                Interlocked.Increment(ref StartCount);
                return null;
            }

            public void KillExact(Process process) { }
        }

        private static void BootstrapIsSchema4AndSingleUse()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "mttf-watchdog-bootstrap-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var startTicks = process.StartTime.ToUniversalTime().Ticks;
                    Assert(
                        WatchdogJournalBootstrap.TryCreateNew(
                            directory,
                            "session-bootstrap",
                            process.Id,
                            startTicks,
                            out var json,
                            out var error),
                        "schema4 bootstrap失败：" + error);
                    Assert(json.Contains("\"SchemaVersion\":" +
                               WatchdogJournalPolicy.CurrentSchemaVersion) &&
                           json.Contains("\"RelaunchState\":\"None\"") &&
                           json.Contains("\"BootstrapRevision\":1"),
                        "bootstrap没有生成schema4/None/revision1权威快照");
                    Assert(!WatchdogJournalBootstrap.TryCreateNew(
                               directory,
                               "session-bootstrap",
                               process.Id,
                               startTicks,
                               out _,
                               out error) &&
                           error == "BootstrapSessionAlreadyExists",
                        "同Session bootstrap允许覆盖既有快照");
                }
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            }
        }

        private static void InvalidBootstrapFailsClosed()
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory);
            Assert(!WatchdogJournalBootstrap.TryCreateNew(
                       root,
                       "session-invalid-bootstrap",
                       0,
                       0,
                       out _,
                       out var error) &&
                   error == "BootstrapIdentityInvalid",
                "非法bootstrap身份没有拒绝启动");
        }

        private static void Run(string name, Action test, ref int passed)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
                throw;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
