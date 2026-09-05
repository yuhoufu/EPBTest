using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Config;
using Controller;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class V216StabilityTests
    {
        internal static int RunAll()
        {
            StartupRetryCommitsBeforeWorkerReturns();
            StartupRetryCancellationCannotReauthorize();
            StartupRetryOldEpochCannotReauthorize();
            StartupRetryRevocationCannotReauthorize();
            ConcurrentStartupRetriesKeepTheirOwners();
            DaqReadQueueDoesNotRunBusinessOnReader();
            DaqFreshnessUsesOneCommittedBatch();
            DaqOldGenerationCannotPublish();
            FormalClosureLifetimeIsBounded();
            TerminalSessionsNeverRestart();
            AuthorityReceiptRoundTripPreservesHash();
            OverdueStaggerDoesNotCaptureCallerContext();
            return 12;
        }

        private sealed class HeldSynchronizationContext : SynchronizationContext
        {
            private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posts =
                new System.Collections.Concurrent.ConcurrentQueue<Action>();
            internal int PostCount;
            public override void Post(SendOrPostCallback callback, object state)
            {
                Interlocked.Increment(ref PostCount);
                _posts.Enqueue(() => callback(state));
            }
            internal void Drain()
            {
                while (_posts.TryDequeue(out var action)) action();
            }
        }

        private static IEnumerable<int> DelayedStaggerChannels()
        {
            // Force enumeration past the executor's 2 ms anchor margin.
            Thread.Sleep(20);
            yield return 4;
        }

        private static void OverdueStaggerDoesNotCaptureCallerContext()
        {
            var context = new HeldSynchronizationContext();
            var previous = SynchronizationContext.Current;
            var plan = ElectricalStaggerPlanner.Build(new[] { 4 },
                new[] { new ElectricalGroup { Id = 2, StaggerMs = 800, Members = { 4, 5, 6 } } }, 15000);
            var started = 0;
            Task run = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                run = ElectricalStaggerExecutor.RunAsync(DelayedStaggerChannels(), plan,
                    DateTime.UtcNow.AddSeconds(-10), (channel, token) =>
                    {
                        Interlocked.Increment(ref started);
                        return Task.CompletedTask;
                    }, CancellationToken.None);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            var completedWithoutPump = run.Wait(2000);
            // Release old-code continuations before reporting the regression failure.
            context.Drain();
            Assert(run.Wait(2000), "错峰执行无法完成回归清理");
            Assert(completedWithoutPump && context.PostCount == 0 && started == 1,
                "过期相位捕获调用线程上下文，界面同步等待导致启动互锁");
            Console.WriteLine("PASS V216 T03 过期错峰不依赖调用线程消息泵");
        }

        private static void TerminalSessionsNeverRestart()
        {
            var session = Guid.NewGuid().ToString("N");
            var manifest = new WatchdogSessionManifest
            { SessionId = session, TerminalUtc = DateTime.UtcNow.ToString("O"), TerminalState = "TerminalSessionObservedOnStartup" };
            Assert(SupervisorSessionRetirement.Decide(session, manifest, null, null) == "Retired", "终态会话仍重启");
            var closing = new WatchdogClosingTombstone
            { SessionId = session, State = WatchdogClosingTombstoneState.Closing };
            Assert(SupervisorSessionRetirement.Decide(session, manifest, closing, null) == "Blocked", "缺少安全证据仍重复重启或伪报安全");
            var handoff = new WatchdogSafetyHandoffReceipt
            { SessionId = session, State = WatchdogSafetyHandoffState.Accepted, RelaunchDisposition = WatchdogRelaunchDisposition.Forbidden };
            Assert(SupervisorSessionRetirement.Decide(session, manifest, closing, handoff) == "Recovering", "人工停止取消了独立安全接管");
            Assert(!handoff.CanExitApplication, "未落盘的安全接管被当成退出授权");
            for (var tick = 0; tick < 60; tick++)
                Assert(SupervisorSessionRetirement.SuppressesRestart("Retired") &&
                    SupervisorSessionRetirement.SuppressesRestart("Blocked"), "退休状态在监控轮询中复活");
            Assert(SupervisorProtocol.CompatibilityFamily == "EPB-V2.17" &&
                SupervisorProtocol.PipeName.EndsWith("V217", StringComparison.Ordinal), "协议混入旧组件");
            var changed = new WatchdogSafetyHandoffReceipt { SessionGeneration = 19, Nonce = "must-not-log-capability" };
            var differences = SupervisorSafetyAuthorityStore.DescribeIdentityDifferences(handoff, changed);
            Assert(differences.Contains("SessionGeneration") && differences.Contains("Nonce") &&
                !differences.Contains(changed.Nonce), "身份错误缺少具体字段或泄漏许可");
            var marker = Path.Combine(Path.GetTempPath(), "V216-Maintenance-" + Guid.NewGuid().ToString("N"));
            try
            {
                WatchdogMaintenancePolicy.AssertMainLaunchAllowed(marker);
                File.WriteAllText(marker, "{\"CleanupCompleted\":false}");
                var blocked = false;
                try { WatchdogMaintenancePolicy.AssertMainLaunchAllowed(marker); }
                catch (InvalidOperationException) { blocked = true; }
                Assert(blocked, "持久维护标记未阻止主程序拉起");
            }
            finally { if (File.Exists(marker)) File.Delete(marker); }
            Console.WriteLine("PASS V216 T11/T12/T14 终态退休、证据阻断与独立安全意图");
        }

        private static void AuthorityReceiptRoundTripPreservesHash()
        {
            var serializer = new JavaScriptSerializer();
            var random = new Random(216);
            for (var index = 0; index < 10000; index++)
            {
                var elapsed = index == 0 ? 77.679864632748 : random.NextDouble() * 100;
                var receipt = new WatchdogSafetyHandoffReceipt { StageMonotonicElapsedMs = elapsed };
                Assert(SupervisorSafetyAuthorityStore.SerializeReceipt(receipt).StartsWith("{\"AttemptCount\":", StringComparison.Ordinal),
                    "权威回执未按固定Ordinal字段顺序序列化");
                var expected = SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt);
                for (var round = 0; round < 3; round++)
                {
                    receipt = serializer.Deserialize<WatchdogSafetyHandoffReceipt>(serializer.Serialize(receipt));
                    Assert(BitConverter.DoubleToInt64Bits(receipt.StageMonotonicElapsedMs) == BitConverter.DoubleToInt64Bits(elapsed) &&
                        SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt) == expected,
                        "JSON往返改变未被篡改回执的耗时或哈希: " + index + "/" + round +
                        " ExpectedValue=" + elapsed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) +
                        " ActualValue=" + receipt.StageMonotonicElapsedMs.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) +
                        " ExpectedHash=" + expected + " ActualHash=" + SupervisorSafetyAuthorityStore.ComputeReceiptSha256(receipt));
                }
            }
            Console.WriteLine("PASS V216 T11 10000耗时三轮JSON往返保持回执位值及权威哈希");
        }

        private static void DaqReadQueueDoesNotRunBusinessOnReader()
        {
            using (var entered = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                var readerThread = Thread.CurrentThread.ManagedThreadId;
                var consumed = 0;
                Exception workerFailure = null;
                var dispatcher = new DaqReadDispatcher<object>("V216-DaqReadBridge", 2, frame =>
                {
                    Assert(Thread.CurrentThread.ManagedThreadId != readerThread, "读取线程执行了订阅业务");
                    entered.Set(); release.Wait(); Interlocked.Increment(ref consumed);
                }, error => workerFailure = error);
                try
                {
                    Assert(dispatcher.TryPublish(new object()) && entered.Wait(3000), "读取交接未运行");
                    var watch = Stopwatch.StartNew();
                    Assert(dispatcher.TryPublish(new object()) && dispatcher.TryPublish(new object()), "有界队列提前拒绝");
                    Assert(!dispatcher.TryPublish(new object()) && dispatcher.Depth == 2, "队满没有明确拒绝");
                    Assert(watch.ElapsedMilliseconds < 250, "读取准入等待被阻塞的消费者");
                }
                finally { dispatcher.Complete(); release.Set(); }
                Assert(dispatcher.Quiesced.Wait(3000) && consumed == 3 && workerFailure == null, "读取交接丢失已接纳批次");
                Assert(!dispatcher.TryPublish(new object()), "已关闭代际仍接纳读取");
                Console.WriteLine("PASS V216 T04/T06 有界读取交接、队满证据与代际关闭");
            }
        }

        private static void DaqOldGenerationCannotPublish()
        {
            using (var fixture = new StartupFixture())
            {
                var acquirer = fixture.Acquirer;
                var generation = typeof(TwoDeviceAiAcquirer).GetField("_generationDev1", BindingFlags.NonPublic | BindingFlags.Instance);
                generation.SetValue(acquirer, 10L);
                var publications = 0;
                Assert(acquirer.TryPublishCurrentGeneration("Dev1", 10, () => publications++), "当前代际无法提交");
                generation.SetValue(acquirer, 11L);
                Assert(!acquirer.TryPublishCurrentGeneration("Dev1", 10, () => publications++) && publications == 1,
                    "旧代际处理返回后更新新采样状态");
                Assert(acquirer.TryPublishCurrentGeneration("Dev1", 11, () => publications++) && publications == 2,
                    "旧代际拒绝污染了新任务发布");
                Console.WriteLine("PASS V216 T06 真实采集器发布门拒绝旧代际写入");
            }
        }

        private static void DaqFreshnessUsesOneCommittedBatch()
        {
            Assert(DaqBacklogPolicy.Milliseconds(80, 2000) == 40 &&
                !DaqBacklogPolicy.Reject(80, 2000) && DaqBacklogPolicy.Reject(202, 2000), "驱动积压没有按时间量判断");
            var now = Stopwatch.GetTimestamp();
            foreach (var age in new[] { 0.9, 40, 101, 250, 300, 1100, 2600 })
            {
                var sampleTick = now - (long)(Stopwatch.Frequency * age / 1000);
                var batch = new FastControlBatchMetadata(5, 11, DateTime.UtcNow, DateTime.UtcNow,
                    sampleTick, now, 0, 2000, ClockState.WarmingUp, 0, 0, 0, 1,
                    FastSignalQualityFlags.None, 80, 3, DaqReaderLagState.Healthy);
                var publication = new DaqControlPublication(batch, now);
                var snapshot = new DaqFreshnessSnapshot { ReaderLagState = DaqReaderLagState.Stale, BufferedSamples = 999 };
                publication.Apply(snapshot, 5, now, 100);
                Assert(snapshot.IsFresh == (age <= 100) && snapshot.BufferedSamples == 80 &&
                    snapshot.ReaderLagState == DaqReaderLagState.Healthy && snapshot.LastProcessedSequence == 11,
                    "新鲜度混用了不同批次的时间与质量: " + age);
                publication.Apply(snapshot, 6, now, 100);
                Assert(!snapshot.IsFresh && snapshot.RejectionReason == "GenerationMismatch", "旧代际发布仍允许准入");
            }
            Console.WriteLine("PASS V216 T04/T05/T06 40ms积压、100/250ms门限及0.3/1.1/2.6秒超龄");
        }

        private static void FormalClosureLifetimeIsBounded()
        {
            var sampled = new List<WeakReference>();
            for (var slot = 0; slot < 200000; slot++)
            {
                var transaction = new FormalSafetyClosureTransaction();
                var calls = 0;
                Func<Task<FormalSafetyClosureObservation>> close = () =>
                {
                    calls++;
                    return Task.FromResult(new FormalSafetyClosureObservation
                    { MotorOffConfirmed = true, HydraulicReleased = true, PersistenceRequired = false });
                };
                var normal = transaction.RunAsync(close);
                var fallback = transaction.RunAsync(close);
                Assert(ReferenceEquals(normal, fallback) && calls == 1 && normal.Result.IsClosed,
                    "正式圈normal/fallback重复收尾");
                if (slot % 1000 == 0) sampled.Add(new WeakReference(transaction));
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert(sampled.Count(reference => reference.IsAlive) <= 1, "已完成圈的事务被长期持有");
            Console.WriteLine("PASS V216 T07/T15 200000槽共用收尾与生命周期回收");
        }

        private static void StartupRetryCommitsBeforeWorkerReturns()
        {
            using (var fixture = new StartupFixture())
            {
                fixture.Retry(4, 1, CancellationToken.None).GetAwaiter().GetResult();
                fixture.AssertReady(4);
                Console.WriteLine("PASS V216 T01 真实启动重试先提交RetryReady再完成worker");
            }
        }

        private static void StartupRetryCancellationCannotReauthorize()
        {
            using (var fixture = new StartupFixture())
            using (var cts = new CancellationTokenSource())
            {
                var task = fixture.Retry(4, 500, cts.Token);
                fixture.WaitRecovering(4);
                cts.Cancel();
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Assert(fixture.State(4).State != ChannelRuntimeState.Starting,
                    "取消后启动worker重新授权");
                Assert(!fixture.Manager.HasActiveRecoveryContract(4, 41), "取消后owner残留");
                Console.WriteLine("PASS V216 T02 真实启动重试取消不重新授权");
            }
        }

        private static void StartupRetryRevocationCannotReauthorize()
        {
            using (var fixture = new StartupFixture())
            {
                var task = fixture.Retry(4, 200, CancellationToken.None);
                fixture.WaitRecovering(4);
                fixture.Manager.RevokeExecutionForExternalRecovery("V216InjectedRevocation");
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                Assert(fixture.Manager.RequiresProcessRestart && fixture.State(4).ReasonCode != "StartupPositioningRetryReady",
                    "撤权后重试重新授权");
                Console.WriteLine("PASS V216 T02 真实外部撤权与启动重试竞争保持禁止上电");
            }
        }

        private static void StartupRetryOldEpochCannotReauthorize()
        {
            using (var fixture = new StartupFixture())
            {
                var task = fixture.Retry(4, 200, CancellationToken.None);
                fixture.WaitRecovering(4);
                fixture.SetField("_runEpoch", 42L);
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
                Assert(fixture.State(4).State != ChannelRuntimeState.Starting,
                    "旧epoch启动worker重新授权");
                Assert(!fixture.Manager.HasActiveRecoveryContract(4, 41),
                    "旧epoch已退出worker仍持有恢复租约");
                Assert(fixture.Manager.DrainBackgroundTasksAsync(2000).GetAwaiter().GetResult(),
                    "旧epoch恢复监督任务没有完成退出");
                Console.WriteLine("PASS V216 T02 旧代际启动重试不重新授权");
            }
        }

        private static void ConcurrentStartupRetriesKeepTheirOwners()
        {
            using (var fixture = new StartupFixture())
            {
                Task.WhenAll(StartupFixture.Channels.Select(channel =>
                    fixture.Retry(channel, 20, CancellationToken.None))).GetAwaiter().GetResult();
                foreach (var channel in StartupFixture.Channels) fixture.AssertReady(channel);
                Console.WriteLine("PASS V216 T03 六通道真实启动重试互不覆盖owner");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class OffWriter : IHighPriorityOffPhysicalWriter
        {
            public bool TryWrite(string deviceName, IReadOnlyList<int> channels, bool[] nextStates)
            {
                Assert(nextStates.All(value => !value), "重试OFF物理边界包含上电位");
                return true;
            }
        }

        private sealed class StartupFixture : IDisposable
        {
            internal TwoDeviceAiAcquirer Acquirer => _acq;
            internal static readonly int[] Channels = { 4, 5, 7, 8, 9, 12 };
            internal readonly EpbManager Manager;
            private readonly Guid _runId = Guid.NewGuid();
            private readonly DoController _do;
            private readonly AoController _ao;
            private readonly TwoDeviceAiAcquirer _acq;

            internal StartupFixture()
            {
                var config = new GlobalConfig
                {
                    AO = new AoConfig(), DO = new DoConfig(),
                    Test = new TestConfig { TestName = "V216StartupSeam", TestTarget = 1, TestPeriod = 15 }
                };
                foreach (var record in config.Test.EnsureEpbRecords(12))
                    record.Enabled = Channels.Contains(record.Id);
                for (var i = 0; i < Channels.Length; i++)
                    config.DO.Epb.Add(new DoEpbRecord
                    {
                        Channel = Channels[i], Enabled = true,
                        Pos = "Dev1/port0/line" + (i * 2),
                        Neg = "Dev1/port0/line" + (i * 2 + 1), Default = "全关"
                    });
                _do = new DoController(config.DO) { HighPriorityOffPhysicalWriter = new OffWriter() };
                _do.ConfigureLogicalDeviceContextForAcceptance();
                _ao = new AoController(config.AO);
                _acq = new TwoDeviceAiAcquirer(new AiConfigDetail
                {
                    Records = new List<AiConfigDetailRecord>
                    {
                        new AiConfigDetailRecord { 序号 = 1, 物理通道 = "Dev1/ai0", 参数名 = "EPB4_current", 单位 = "A", 变换斜率 = 1, 是否启用 = 1 },
                        new AiConfigDetailRecord { 序号 = 2, 物理通道 = "Dev2/ai0", 参数名 = "EPB10_current", 单位 = "A", 变换斜率 = 1, 是否启用 = 1 }
                    }
                }, 2000, 100, 3);
                Manager = new EpbManager(config, _do, _ao, _acq, Config.NullLogger.Instance);
                SetField("_activeBatchId", _runId);
                SetField("_runEpoch", 41L);
            }

            internal void SetField(string name, object value) => typeof(EpbManager)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Manager, value);

            internal Task Retry(int channel, int delayMs, CancellationToken token) =>
                (Task)typeof(EpbManager).GetMethod("RunStartupPositioningRetryIncidentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Manager,
                    new object[] { channel, _runId, 1, "InjectedSampleStale", "一次采样瞬态", delayMs, token, "V216RetryOff" });

            internal ChannelRuntimeStateChangedEvent State(int channel) =>
                Manager.GetChannelRuntimeStates().Single(state => state.Channel == channel);

            internal void WaitRecovering(int channel) => Assert(SpinWait.SpinUntil(
                () => Manager.GetChannelRuntimeStates().Any(state => state.Channel == channel &&
                    state.State == ChannelRuntimeState.Recovering), 3000), "真实入口未进入Recovering");

            internal void AssertReady(int channel)
            {
                var state = State(channel);
                Assert(state.State == ChannelRuntimeState.Starting &&
                    state.ReasonCode == "StartupPositioningRetryReady" && state.RunId == _runId && state.RunEpoch == 41,
                    "重试没有在worker返回前提交RetryReady: " + state.State + "/" + state.ReasonCode);
                Assert(!Manager.HasActiveRecoveryContract(channel, 41), "就绪后owner未释放");
            }

            public void Dispose()
            {
                Manager.ReleaseHardwareForRestart();
                _acq.Dispose(); _ao.Dispose(); _do.Dispose();
            }
        }
    }
}
