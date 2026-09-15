using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using MTTFTest.FallbackGuard;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentExecutorRuntimeTests
    {
        internal static int RunAll()
        {
            var passed = 2;
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!identity.IsSystem)
                {
                    var rejected = false;
                    try { using (var runtime = new IndependentExecutorRuntime("missing-registration.json")) { } }
                    catch (UnauthorizedAccessException) { rejected = true; }
                    Assert(rejected, "non-SYSTEM runtime admitted before registration validation");
                    Console.WriteLine("PASS 独立执行运行时先检查SYSTEM身份");
                    passed++;
                }
                else Console.WriteLine("SKIP non-SYSTEM admission check: current harness is SYSTEM");
            }
            using (var process = Process.GetCurrentProcess())
            {
                var expected = new IndependentProcessIdentity
                {
                    Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                    SessionToken = Guid.NewGuid().ToString("N")
                };
                Assert(IndependentExecutorRuntime.ExactProcessAlive(expected) == true, "own exact identity missing");
                expected.StartUtcTicks--;
                Assert(IndependentExecutorRuntime.ExactProcessAlive(expected) == false, "PID reuse identity accepted");
                expected.StartUtcTicks++;
                expected.ExecutablePath = Path.Combine(Path.GetDirectoryName(expected.ExecutablePath), "other.exe");
                Assert(IndependentExecutorRuntime.ExactProcessAlive(expected) == false, "wrong executable accepted");
            }
            Console.WriteLine("PASS 独立执行运行时核对精确进程启动时间和路径");
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                throw new InvalidOperationException("EPB_TEST_ARTIFACT_ROOT required"), "executor-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var baseline = new IndependentVerificationBaseline
            {
                RequestId = Guid.NewGuid().ToString("N"), Generation = 3,
                Database = new RecoveryDatabaseSnapshot
                {
                    DatabasePath = Path.Combine(root, "index.db"), CreationUtcTicks = 1,
                    Channels = new[] { 7, 8 }.Select(c => new RecoveryDatabaseLane
                    { Channel = c, FormalRecords = Array.Empty<RecoveryFormalRecord>() }).ToArray()
                }
            };
            var file = Path.Combine(root, "baseline.json");
            BoundedJson.Write(file, baseline);
            baseline = BoundedJson.Read<IndependentVerificationBaseline>(file);
            baseline.ValidateAgainst(baseline.Database.DatabasePath, 1, baseline.RequestId, 3, new[] { 8, 7 });
            Reject(() => baseline.ValidateAgainst(baseline.Database.DatabasePath, 1, Guid.NewGuid().ToString("N"), 3, new[] { 7, 8 }));
            Reject(() => baseline.ValidateAgainst(baseline.Database.DatabasePath, 1, baseline.RequestId, 4, new[] { 7, 8 }));
            Reject(() => baseline.ValidateAgainst(baseline.Database.DatabasePath, 2, baseline.RequestId, 3, new[] { 7, 8 }));
            Reject(() => baseline.ValidateAgainst(baseline.Database.DatabasePath, 1, baseline.RequestId, 3, new[] { 7 }));
            Console.WriteLine("PASS 独立恢复验收基线跨进程序列化且不跨事务或目标复用");
            var observation = IndependentExecutorObservation.Capture(Guid.NewGuid().ToString("N"), null,
                DateTime.UtcNow.Ticks, new string('x', 700));
            Assert(observation.SchemaVersion == 2 && observation.BindingState == "AwaitingRunIntent" &&
                observation.ControllerPid == 0 && observation.RunId == null && !observation.LastTransactionVerified &&
                observation.Detail.Length == 512, "empty executor observation invented recovery or unbounded detail");
            Reject(() => IndependentExecutorObservation.Capture("invalid", null, DateTime.UtcNow.Ticks, ""));
            passed++;
            Console.WriteLine("PASS 独立观察无运行意图时不伪造绑定或恢复成功");
            var startup = new IndependentProjectState
            {
                StartupDeadlineUtcTicks = 1000,
                Intent = new IndependentRunIntent { RunId = "current", RunEpoch = 2 },
                Controller = new IndependentProcessIdentity { Pid = 10, StartUtcTicks = 20 },
                Transaction = new IndependentRecoveryTransaction
                {
                    Phase = IndependentRecoveryPhase.Verifying, RunId = "current", RunEpoch = 2,
                    ReplacementPid = 10, ReplacementStartUtcTicks = 20
                }
            };
            Assert(IndependentExecutorRuntime.IsStartupBudgetActive(startup, 100), "unverified learning lost startup budget");
            startup.Transaction.Phase = IndependentRecoveryPhase.Verified;
            Assert(!IndependentExecutorRuntime.IsStartupBudgetActive(startup, 100), "verified formal run retained learning grace");
            startup.Transaction.RunId = "old";
            Assert(IndependentExecutorRuntime.IsStartupBudgetActive(startup, 100), "old run proof ended new learning budget");
            startup.Transaction.RunId = "current";
            startup.Transaction.RunEpoch = 1;
            Assert(IndependentExecutorRuntime.IsStartupBudgetActive(startup, 100), "old epoch proof reused");
            startup.Transaction.RunEpoch = 2;
            startup.Transaction.ReplacementStartUtcTicks = 19;
            Assert(IndependentExecutorRuntime.IsStartupBudgetActive(startup, 100), "reused PID proof ended learning");
            Assert(!IndependentExecutorRuntime.IsStartupBudgetActive(startup, 1000), "startup grace extended past deadline");
            passed += 6;
            Console.WriteLine("PASS verified current run exits learning budget; old run, epoch and process proofs rejected");
            return passed;
        }
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Mismatched recovery baseline accepted");
        }
    }
}
