using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class I0049RecoveryTests
    {
        internal static int RunAll()
        {
            SafetyAgentDigestBinding();
            TerminalAuthorityDoesNotMaskNextFault();
            RetiredExecutionMustRebuildBeforePower();
            HealthEndpointReportsWorkProgress();
            HealthProbeHasReadDeadline();
            SupervisorReturnsExistingHostBinding();
            StatusWriteRecoversAfterReaderReleases();
            StatusWriteTimesOutWithoutDestroyingPreviousState();
            StatusReaderAllowsAtomicReplacement();
            Console.WriteLine("PASS I0049 9/9 摘要准入、交接终态、许可重建、健康进度、超时、既有权威绑定与状态文件占用");
            return 9;
        }

        internal static int RunSoak(string evidenceDirectory)
        {
            var root = Path.GetFullPath(evidenceDirectory);
            Directory.CreateDirectory(root);
            var clock = Stopwatch.StartNew();
            var started = DateTime.UtcNow;
            var rounds = 0;
            void Save(string state, string error = null)
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new
                    {
                        version = "2.17.3.0", state, startedUtc = started.ToString("O"),
                        updatedUtc = DateTime.UtcNow.ToString("O"), elapsedSeconds = clock.Elapsed.TotalSeconds,
                        requiredSeconds = 21600, rounds, processId = process.Id,
                        processStartTicks = process.StartTime.ToUniversalTime().Ticks,
                        privateBytes = process.PrivateMemorySize64, handles = process.HandleCount,
                        hardwareTestPerformed = false, error
                    });
                    var path = Path.Combine(root, "soak-status.json");
                    WriteStatusAtomically(path, json, TimeSpan.FromSeconds(5), ex =>
                        Console.WriteLine("SOAK_STATUS_RETRY utc={0:O} hresult=0x{1:X8} path={2} reason={3}",
                            DateTime.UtcNow, ex.HResult, path, ex.Message));
                }
            }
            try
            {
                Save("RUNNING");
                while (clock.Elapsed < TimeSpan.FromHours(6))
                {
                    RunAll();
                    V217StabilityTests.RunAll();
                    WatchdogClientTransportProductionTests.DeadAuthorityRebindsBeforeConnecting();
                    WatchdogClientTransportProductionTests.AuthorityExitsDuringAttachedRetriesBinding();
                    DaqPersistenceCoordinatorTests.PauseAndRecoverAfterLowWater();
                    DaqPersistenceCoordinatorTests.RecoveryTimeoutRetainsBatchUntilStorageReturns();
                    rounds++;
                    Save("RUNNING");
                    if (File.Exists(Path.Combine(root, "stop.request")))
                    { Save("STOPPED_NOT_PASSED"); return 2; }
                    Thread.Sleep(30000);
                }
                Save("PASSED");
                return 0;
            }
            catch (Exception ex)
            {
                // Reporting failure must never replace the original test failure.
                Console.Error.WriteLine("SOAK_FAILURE " + ex);
                try { Save("FAILED", ex.ToString()); }
                catch (Exception reportingError) { Console.Error.WriteLine("SOAK_FAILURE_REPORT " + reportingError); }
                throw;
            }
        }

        private static void WriteStatusAtomically(string path, string json, TimeSpan budget, Action<IOException> onRetry)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var timer = Stopwatch.StartNew();
            try
            {
                File.WriteAllText(temp, json);
                while (true)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException ex)
                    {
                        if (timer.Elapsed >= budget)
                            throw new IOException("Soak status atomic publish exceeded retry budget: " + path, ex);
                        onRetry?.Invoke(ex);
                        Thread.Sleep((int)Math.Min(100, Math.Max(1, (budget - timer.Elapsed).TotalMilliseconds)));
                    }
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception cleanupError) { Console.Error.WriteLine("SOAK_STATUS_TEMP_CLEANUP " + cleanupError); }
            }
        }

        private static void StatusWriteRecoversAfterReaderReleases()
        {
            var root = Path.Combine(Path.GetTempPath(), "I0049-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "status.json");
            try
            {
                WriteStatusAtomically(path, "old", TimeSpan.FromSeconds(1), null);
                var retries = 0;
                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    WriteStatusAtomically(path, "new", TimeSpan.FromSeconds(2), ex =>
                    {
                        retries++;
                        Assert(File.ReadAllText(path) == "old", "重试前必须保留原状态");
                        reader.Dispose();
                    });
                }
                Assert(retries > 0 && File.ReadAllText(path) == "new", "读者释放后应自动完成原子替换");
                Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "成功后不应遗留临时文件");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void StatusWriteTimesOutWithoutDestroyingPreviousState()
        {
            var root = Path.Combine(Path.GetTempPath(), "I0049-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "status.json");
            try
            {
                File.WriteAllText(path, "old");
                var timer = Stopwatch.StartNew();
                IOException failure = null;
                using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { WriteStatusAtomically(path, "new", TimeSpan.FromMilliseconds(200), null); }
                    catch (IOException ex) { failure = ex; }
                }
                Assert(failure?.InnerException is IOException, "持续占用应报告真实 IO 原因");
                Assert(timer.Elapsed < TimeSpan.FromSeconds(3), "持续占用不能无限等待");
                Assert(File.ReadAllText(path) == "old", "失败不能删除或截断原状态");
                Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "失败后应清理临时文件");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void StatusReaderAllowsAtomicReplacement()
        {
            var root = Path.Combine(Path.GetTempPath(), "I0049-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "status.json");
            try
            {
                File.WriteAllText(path, "old");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    var retries = 0;
                    WriteStatusAtomically(path, "new", TimeSpan.FromSeconds(1), ex => retries++);
                    Assert(retries == 0, "共享删除的读者不应阻塞原子替换");
                    Assert(reader.ReadToEnd() == "old" && File.ReadAllText(path) == "new",
                        "在途读者及新读者应分别读取完整快照");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static void SafetyAgentDigestBinding()
        {
            const string path = @"D:\MTTFTest\Current\MTTFTest.SafetyAgent.exe";
            const string hash = "485469f428a1d5ef5e55b15ecaae8279dafb89fc9ef9f1c893472217029e5cac";
            Assert(SupervisorServiceRuntime.IsSafetyAgentExecutableBound(path, hash, path, hash.ToUpperInvariant()),
                "I0049 等价摘要被安全代理准入拒绝");
            Assert(!SupervisorServiceRuntime.IsSafetyAgentExecutableBound(path, hash, path, "0" + hash.Substring(1)),
                "实际摘要改变仍被接受");
            Assert(!SupervisorServiceRuntime.IsSafetyAgentExecutableBound(path, hash, path + ".other", hash),
                "不同可执行文件路径被接受");
            Assert(!SupervisorServiceRuntime.IsSafetyAgentExecutableBound(path, new string('x', 64), path, new string('x', 64)),
                "非法摘要格式被接受");
        }

        private static void SupervisorReturnsExistingHostBinding()
        {
            var actualNonce = Guid.NewGuid().ToString("N");
            var response = new SupervisorSessionLaunchResponse
            {
                Accepted = true, Detail = SupervisorProtocol.SessionHostBinding(actualNonce)
            };
            using (var stream = new MemoryStream())
            {
                response.WriteTo(new BinaryWriter(stream));
                stream.Position = 0;
                var received = SupervisorSessionLaunchResponse.ReadFrom(new BinaryReader(stream));
                Assert(SupervisorProtocol.ReadSessionHostBinding(received.Detail) == actualNonce &&
                       stream.Position == stream.Length, "既有权威 nonce 未通过原 v7 响应布局传递");
            }
            Assert(SupervisorProtocol.ReadSessionHostBinding("SupervisorOwnedSessionHost") == null,
                "旧监督响应兼容性被破坏");
            var rejected = false;
            try { SupervisorProtocol.ReadSessionHostBinding(SupervisorProtocol.SessionHostBindingPrefix + "invalid"); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "非法监督绑定 nonce 未拒绝");
        }

        private static void TerminalAuthorityDoesNotMaskNextFault()
        {
            Assert(!WatchdogHost.ShouldWaitForSafetyHandoff(true, false, false),
                "权威完成、镜像 Accepted 仍跳过正常监督");
            Assert(WatchdogHost.ShouldWaitForSafetyHandoff(false, true, false), "新交接没有保留等待");
            Assert(WatchdogHost.ShouldWaitForSafetyHandoff(false, false, false), "在途交接被提前跳过");
            Assert(!WatchdogHost.ShouldWaitForSafetyHandoff(false, false, true), "镜像终态仍阻止监督");
        }

        private static void RetiredExecutionMustRebuildBeforePower()
        {
            var fence = new ChannelExecutionFence();
            var old = fence.Authorize(4, 1);
            Assert(fence.RevokeIfCurrent(old), "无法撤销退场执行许可");
            var captured = fence.Capture(4);
            Assert(EpbManager.GetExecutionRejoinRejection(true, fence.IsCurrent(captured), true, false) ==
                   "ExecutionPermitRevoked", "退场后 Capture 被误认为重新授权");
            var fresh = fence.Authorize(4, 2);
            Assert(fence.IsCurrent(fresh) && !fence.IsCurrent(old) && !fence.RevokeIfCurrent(old),
                "迟到旧任务撤销了安全重建后的新许可");
            Assert(EpbManager.GetExecutionRejoinRejection(false, true, true, false) == "ChannelDisabled" &&
                   EpbManager.GetExecutionRejoinRejection(true, true, false, false) == "RunEpochChanged" &&
                   EpbManager.GetExecutionRejoinRejection(true, true, true, true) == "ProcessRestartRequired",
                "重新上电拒绝原因未区分");
        }

        private static void HealthEndpointReportsWorkProgress()
        {
            var name = "MTTFTest.Health.Test." + Guid.NewGuid().ToString("N");
            long progress = DateTime.UtcNow.Ticks;
            using (var process = Process.GetCurrentProcess())
            using (var endpoint = new RecoveryHealthEndpoint(name, () => Interlocked.Read(ref progress), () => "TestRecovery"))
            {
                var snapshot = RecoveryHealthEndpoint.Probe(name);
                var started = process.StartTime.ToUniversalTime().Ticks;
                Assert(snapshot.Matches(process.Id, started, DateTime.UtcNow), "正常工作进度未通过健康检查");
                Assert(!snapshot.Matches(process.Id, started + 1, DateTime.UtcNow), "健康检查接受了复用 PID");
                Interlocked.Exchange(ref progress, DateTime.UtcNow.AddSeconds(-20).Ticks);
                var stalled = RecoveryHealthEndpoint.Probe(name);
                Assert(!stalled.Matches(process.Id, started, DateTime.UtcNow), "IPC 活着但业务循环已停滞被误判为健康");
            }
        }

        private static void HealthProbeHasReadDeadline()
        {
            var name = "MTTFTest.Health.Silent." + Guid.NewGuid().ToString("N");
            using (var server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                       PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var accepted = server.WaitForConnectionAsync();
                var clock = Stopwatch.StartNew();
                var probe = Task.Run(() =>
                {
                    try { RecoveryHealthEndpoint.Probe(name, 150); return false; }
                    catch (IOException) { return true; }
                    catch (ObjectDisposedException) { return true; }
                    catch (OperationCanceledException) { return true; }
                });
                Assert(accepted.Wait(2000) && probe.Wait(3000) && probe.Result && clock.ElapsedMilliseconds < 3000,
                    "健康检查连接成功后被不回包对端永久阻塞");
            }
        }
    }
}
