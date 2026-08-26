using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
            return passed;
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
