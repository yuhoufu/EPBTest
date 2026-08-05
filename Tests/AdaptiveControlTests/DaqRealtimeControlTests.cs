using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Config;
using Controller;
using Controller.Adaptive;
using DataOperation;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class DaqRealtimeControlTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("控制环顺序容量代次与设备隔离", RingOrderCapacityResetAndIsolation, ref passed);
            Run("控制环并发发布可见性", RingConcurrentVisibility, ref passed);
            Run("20到500ms控制延迟策略", LatencyPolicyFaultInjection, ref passed);
            Run("DAQ事故关联去重优先级与新运行复位", IncidentCorrelationAndPriority, ref passed);
            Run("DAQ事故先断电后发布诊断", DaqSafetyActionsPrecedePublication, ref passed);
            Run("十万稳态样本控制计算无持续分配", AdaptiveHotLoopDoesNotAllocate, ref passed);
            Run("因果中值滤波跨批正确且原地无持续分配", CausalMedianInPlaceIsCorrectAndAllocationFree, ref passed);
            Run("普通轨迹25Hz且动作轨迹不降采样", AdaptiveTraceRateAndActionRetention, ref passed);
            Run("控制诊断记录真实64批容量", ControlDiagnosticsUseRealCapacity, ref passed);
            Run("构建身份包含版本哈希位数与Git状态", BuildIdentityIsAuditable, ref passed);
            Run("最后项目跨版本恢复且不可用时保留选择", LastProjectSelectionSurvivesUpgradeAndUnavailableStorage, ref passed);
            return passed;
        }

        private static void RingOrderCapacityResetAndIsolation()
        {
            var dev1 = new ControlBatchRing(64, 2);
            var dev2 = new ControlBatchRing(64, 2);
            var sample = new FastControlSampleValue[1];
            for (var sequence = 0; sequence < 64; sequence++)
            {
                sample[0] = new FastControlSampleValue(4, sequence);
                Assert(dev1.TryEnqueue(7, DateTime.UtcNow, sequence + 1, sample),
                    "Dev1在容量前拒绝入队");
            }
            Assert(dev1.Depth == 64 && dev1.Capacity == 64, "控制环真实容量或深度错误");
            Assert(!dev1.TryEnqueue(7, DateTime.UtcNow, 100, sample), "满环发生静默覆盖");
            Assert(dev2.Depth == 0, "Dev1积压污染Dev2");

            var destination = new FastControlSampleValue[2];
            for (var sequence = 0; sequence < 64; sequence++)
            {
                Assert(dev1.TryDequeue(destination, out var count, out var generation,
                    out _, out _), "控制环提前变空");
                Assert(count == 1 && generation == 7 && destination[0].Amps == sequence,
                    "控制环FIFO顺序或代次错误");
            }

            sample[0] = new FastControlSampleValue(4, 99);
            Assert(dev1.TryEnqueue(7, DateTime.UtcNow, 1, sample), "旧代次测试入队失败");
            dev1.Reset();
            Assert(dev1.Depth == 0 && !dev1.TryDequeue(destination, out _, out _, out _, out _),
                "新运行复位未清除旧代次批");
            sample[0] = new FastControlSampleValue(4, 100);
            Assert(dev1.TryEnqueue(8, DateTime.UtcNow, 2, sample), "新代次入队失败");
            Assert(dev1.TryDequeue(destination, out _, out var newGeneration, out _, out _) &&
                   newGeneration == 8, "旧代次进入新运行");
        }

        private static void RingConcurrentVisibility()
        {
            const int total = 20000;
            var ring = new ControlBatchRing(64, 1);
            var failure = string.Empty;
            var consumed = 0;
            var producer = new Thread(() =>
            {
                var item = new FastControlSampleValue[1];
                for (var sequence = 0; sequence < total && failure.Length == 0; sequence++)
                {
                    item[0] = new FastControlSampleValue(4, sequence);
                    while (!ring.TryEnqueue(3, DateTime.UtcNow, sequence + 1, item))
                        Thread.Yield();
                }
            });
            var consumer = new Thread(() =>
            {
                var destination = new FastControlSampleValue[1];
                while (consumed < total && failure.Length == 0)
                {
                    if (!ring.TryDequeue(destination, out _, out var generation, out _, out _))
                    {
                        Thread.Yield();
                        continue;
                    }
                    if (generation != 3 || destination[0].Amps != consumed)
                    {
                        failure = $"并发读取不一致 expected={consumed} actual={destination[0].Amps}";
                        return;
                    }
                    consumed++;
                }
            });
            producer.Start();
            consumer.Start();
            Assert(producer.Join(5000) && consumer.Join(5000), "控制环并发测试超时");
            Assert(failure.Length == 0 && consumed == total, failure.Length == 0 ? "批数不完整" : failure);
        }

        private static void LatencyPolicyFaultInjection()
        {
            Assert(Policy(20, true) == ControlLatencyAction.Healthy, "20ms不应预警");
            Assert(Policy(50, true) == ControlLatencyAction.Warning, "50ms应内部预警");
            Assert(Policy(80, true) == ControlLatencyAction.Warning, "80ms应可追赶且不硬停");
            Assert(Policy(120, true) == ControlLatencyAction.HardFault, "120ms带电未硬停");
            Assert(Policy(500, true) == ControlLatencyAction.HardFault, "500ms带电未硬停");
            Assert(Policy(120, false) == ControlLatencyAction.ResynchronizeInactive,
                "120ms断电积压未重同步");
            Assert(Policy(500, false) == ControlLatencyAction.ResynchronizeInactive,
                "500ms断电积压不应报警");
        }

        private static ControlLatencyAction Policy(double ageMs, bool active)
        {
            return ControlLatencyPolicy.Evaluate(ageMs, active, 50, 100);
        }

        private static void IncidentCorrelationAndPriority()
        {
            var latch = new DaqIncidentLatch();
            var run1 = Guid.NewGuid();
            latch.BeginRun(run1, new[] { "Dev1", "Dev2" });
            var first = latch.Observe(run1, "Dev1", 5, "ControlLatencyExceeded", "late",
                DateTime.UtcNow, new[] { 5, 4 });
            Assert(first.IsFirst && first.Context.PrimaryChannel == 4, "首事故或主通道不正确");
            var derived = latch.Observe(run1, "Dev1", 5, "DaqSampleStale", "stale",
                DateTime.UtcNow, new[] { 6 });
            Assert(!derived.IsFirst && derived.Context.CorrelationId == first.Context.CorrelationId,
                "同设备派生故障未复用关联号");
            var upgraded = latch.Observe(run1, "Dev1", 5, "ControlQueueFull", "full",
                DateTime.UtcNow, new[] { 4, 5, 6 });
            Assert(!upgraded.IsFirst && upgraded.PrimaryChanged &&
                   upgraded.Context.PrimaryCode == "ControlQueueFull", "根故障优先级未升级");
            Assert(latch.TryStartSnapshot(run1, "Dev1", out _) &&
                   !latch.TryStartSnapshot(run1, "Dev1", out _), "同事故启动了多个主快照");

            var otherDevice = latch.Observe(run1, "Dev2", 2, "DaqSampleStale", "stale",
                DateTime.UtcNow, new[] { 8 });
            Assert(otherDevice.Context.CorrelationId != first.Context.CorrelationId, "跨设备事故被错误合并");
            var run2 = Guid.NewGuid();
            latch.BeginRun(run2, new[] { "Dev1" });
            var newRun = latch.Observe(run2, "Dev1", 6, "DaqSampleStale", "stale",
                DateTime.UtcNow, new[] { 4 });
            Assert(newRun.IsFirst && newRun.Context.CorrelationId != first.Context.CorrelationId,
                "显式新运行未复位事故锁存");
        }

        private static void DaqSafetyActionsPrecedePublication()
        {
            var order = new List<string>();
            EpbManager.ExecuteDaqFaultSafetyFirst(
                new[] { 4, 5, 6 },
                channel => order.Add("cancel-" + channel),
                channel => order.Add("off-" + channel));
            order.Add("log-ui-snapshot");
            var publishIndex = order.IndexOf("log-ui-snapshot");
            Assert(new[] { 4, 5, 6 }.All(channel => order.IndexOf("off-" + channel) < publishIndex),
                "日志/UI/快照先于安全断电");
        }

        private static void AdaptiveHotLoopDoesNotAllocate()
        {
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            var decision = new EpbAdaptiveDecision();
            var tick = Stopwatch.Frequency;
            machine.ArmForward(tick, 1000, 10000, 15, 1, 3);
            for (var i = 0; i < 5000; i++)
                machine.OnSampleReusable(tick, 1.0, double.NaN, decision);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++)
                machine.OnSampleReusable(tick, 1.0, double.NaN, decision);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 4096, $"十万样本持续分配 {allocated} bytes");
            Assert(machine.RetainedWindowSampleCount <= 1024, "固定窗口超过1024容量");
        }

        private static void CausalMedianInPlaceIsCorrectAndAllocationFree()
        {
            var filter = new ClsDataFilter.MedianStreamCausal(
                2, 2, MedianSelectPointsMode.OnlyPrevious, 5);
            var first = new double[,]
            {
                { 5, 1, 9 },
                { 3, 7, 4 }
            };
            var second = new double[,]
            {
                { 2, 8 },
                { 6, 0 }
            };
            filter.ProcessInPlace(first);
            filter.ProcessInPlace(second);
            AssertRow(first, 0, new[] { 5d, 1d, 5d });
            AssertRow(first, 1, new[] { 3d, 3d, 4d });
            AssertRow(second, 0, new[] { 2d, 8d });
            AssertRow(second, 1, new[] { 6d, 4d });

            var hotFilter = new ClsDataFilter.MedianStreamCausal(
                2, 3, MedianSelectPointsMode.OnlyPrevious, 5);
            var batch = new double[2, 5];
            for (var warmup = 0; warmup < 1000; warmup++)
                hotFilter.ProcessInPlace(batch);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20000; i++)
                hotFilter.ProcessInPlace(batch);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 4096, $"原地中值滤波持续分配 {allocated} bytes");
        }

        private static void AssertRow(double[,] actual, int row, double[] expected)
        {
            for (var column = 0; column < expected.Length; column++)
                Assert(Math.Abs(actual[row, column] - expected[column]) < 1e-12,
                    $"中值滤波结果错误 row={row} column={column} expected={expected[column]} actual={actual[row, column]}");
        }

        private static void AdaptiveTraceRateAndActionRetention()
        {
            var interval = Math.Max(1, Stopwatch.Frequency / 25);
            long lastNormal = 0;
            var normalCount = 0;
            for (var millisecond = 0; millisecond < 1000; millisecond++)
            {
                var tick = 1 + (long)(millisecond * Stopwatch.Frequency / 1000.0);
                if (EpbCycleRunner.ShouldRecordAdaptiveTrace(tick, false, interval, ref lastNormal))
                    normalCount++;
            }
            Assert(normalCount <= 25, $"普通轨迹超过25Hz，实际={normalCount}");
            for (var i = 0; i < 100; i++)
                Assert(EpbCycleRunner.ShouldRecordAdaptiveTrace(i, true, interval, ref lastNormal),
                    "动作轨迹被降采样");

            var buffer = new AdaptiveDecisionTraceBuffer();
            var run = Guid.NewGuid();
            var now = DateTime.UtcNow;
            for (var i = 0; i < 100; i++)
                buffer.Append(new AdaptiveDecisionTraceSample
                {
                    SampleUtc = now.AddMilliseconds(i),
                    RunId = run,
                    Channel = 4,
                    Action = "HardFault",
                    Reason = "Injected"
                });
            var snapshot = buffer.Snapshot(now.AddSeconds(1), run, 4);
            Assert(snapshot.Count(x => x.Action == "HardFault") == 100, "动作轨迹发生丢失");
        }

        private static void ControlDiagnosticsUseRealCapacity()
        {
            var diagnostics = new DaqDiagnosticRing(8);
            diagnostics.Append(new DaqTimingValue
            {
                TimestampUtc = DateTime.UtcNow,
                Device = "Dev1",
                Kind = "Control",
                QueueDepth = 2,
                ControlQueueCapacity = 64
            });
            var snapshot = diagnostics.Snapshot();
            Assert(snapshot.Length == 1 && snapshot[0].ControlQueueCapacity == 64,
                "控制诊断误写后台队列容量");
        }

        private static void BuildIdentityIsAuditable()
        {
            var identity = RuntimeBuildIdentity.Capture();
            var json = identity.ToJson();
            Assert(identity.ProcessBitness == IntPtr.Size * 8, "进程位数记录错误");
            Assert(!string.IsNullOrWhiteSpace(identity.ProductVersion) &&
                   !string.IsNullOrWhiteSpace(identity.ExecutableSha256) &&
                   !string.IsNullOrWhiteSpace(identity.ConfigSha256), "版本或哈希字段缺失");
            Assert(json.Contains("\"gitCommit\"") && json.Contains("\"gitDirty\""),
                "Git构建身份字段缺失");
        }

        private static void LastProjectSelectionSurvivesUpgradeAndUnavailableStorage()
        {
            var testRoot = Path.Combine(
                Path.GetTempPath(),
                "EPBTest-LastProjectSelection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            try
            {
                var storeDir = Path.Combine(testRoot, "Data");
                const string projectName = "10358-029";
                var projectConfig = ConfigLoader.GetProjectTestConfigPath(storeDir, projectName);
                Directory.CreateDirectory(Path.GetDirectoryName(projectConfig));
                var template = Path.GetFullPath(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", "MTTfTest", "Config", "TestConfig.xml"));
                File.Copy(template, projectConfig);

                var statePath = Path.Combine(testRoot, "UserState", "user-state.json");
                Assert(LastProjectSelectionStore.TrySave(
                    storeDir, projectName, out var saveError, statePath), saveError);
                var json = File.ReadAllText(statePath);
                Assert(json.Contains("10358-029") && !json.Contains("DaqControlHardFaultAgeMs"),
                    "用户状态混入程序级安全配置");

                var global = new GlobalConfig
                {
                    Test = new TestConfig { StoreDir = testRoot, TestName = "DefaultProject" }
                };
                var restored = LastProjectSelectionStore.Restore(
                    global, null, Config.NullLogger.Instance, statePath);
                Assert(restored.Restored && global.Test.TestName == projectName &&
                       Path.GetFullPath(global.Test.StoreDir) == Path.GetFullPath(storeDir),
                    "升级后未恢复最后项目");

                File.Move(projectConfig, projectConfig + ".offline");
                var unavailable = LastProjectSelectionStore.Restore(
                    global, null, Config.NullLogger.Instance, statePath);
                Assert(unavailable.SelectionFound && !unavailable.Restored && File.Exists(statePath),
                    "项目暂不可用时错误清除了最后选择");
                File.Move(projectConfig + ".offline", projectConfig);

                var bootstrapState = Path.Combine(testRoot, "BootstrapState", "user-state.json");
                var bootstrap = LastProjectSelectionStore.Restore(
                    new GlobalConfig { Test = new TestConfig() },
                    Path.Combine(storeDir, projectName),
                    Config.NullLogger.Instance,
                    bootstrapState);
                Assert(bootstrap.Restored && bootstrap.UsedBootstrap && File.Exists(bootstrapState),
                    "首次引导项目未写入独立用户状态");
            }
            finally
            {
                if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            }
        }

        private static void Run(string name, Action test, ref int passed)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
