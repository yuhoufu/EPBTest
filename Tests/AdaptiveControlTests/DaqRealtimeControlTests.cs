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
            Run("DAQ生产区拒绝重叠回调", ProducerGateRejectsOverlap, ref passed);
            Run("采样时间按设备起点和累计样本推进", AcquisitionTimelineNeverSnapsCatchUpToFuture, ref passed);
            Run("控制批携带序号单调时钟和原始尾部", ControlBatchCarriesIdentityClockAndRawTail, ref passed);
            Run("控制消费拒绝重复倒序和非递增tick", ControlBatchIdentityRejectsInvalidOrder, ref passed);
            Run("每设备快速滤波隔离并按代次复位", FastFiltersAreIsolatedAndResettable, ref passed);
            Run("快速滤波十万次稳态更新无持续分配", FastFilterHotLoopDoesNotAllocate, ref passed);
            Run("启动定位相同批次不重复计数", StartupClassifierIgnoresDuplicateBatch, ref passed);
            Run("未来墙钟不改变控制经过时间", FutureWallClockDoesNotChangeControlElapsed, ref passed);
            Run("快速过流由全速率证据最终归因", FastTripClassificationUsesFullRateEvidence, ref passed);
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

        private static void ProducerGateRejectsOverlap()
        {
            var gate = new DaqCallbackProducerGate();
            Assert(gate.TryEnter(), "首个生产者未取得门");
            var rejected = 0;
            var workers = Enumerable.Range(0, 8)
                .Select(_ => new Thread(() =>
                {
                    for (var attempt = 0; attempt < 1000; attempt++)
                    {
                        if (!gate.TryEnter()) Interlocked.Increment(ref rejected);
                        else gate.Exit();
                    }
                }))
                .ToArray();
            foreach (var worker in workers) worker.Start();
            foreach (var worker in workers) Assert(worker.Join(1000), "重叠生产者测试超时");
            var expectedRejected = workers.Length * 1000;
            Assert(rejected == expectedRejected && gate.ReentryCount == expectedRejected,
                "重叠生产者未全部拒绝或重入计数错误");
            gate.Exit();
            Assert(gate.TryEnter(), "生产区退出后无法重新进入");
            gate.Exit();
        }

        private static void AcquisitionTimelineNeverSnapsCatchUpToFuture()
        {
            var origin = new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency;
            var timeline = new DeviceSampleTimeline();
            timeline.Reset(origin, originTick);
            var first = timeline.Advance(
                20, 2000, origin.AddMilliseconds(500),
                originTick + Stopwatch.Frequency / 2, 20);
            var second = timeline.Advance(
                20, 2000, origin.AddMilliseconds(501),
                originTick + (long)(0.501 * Stopwatch.Frequency), 20);
            Assert(first.BatchEndUtc == origin.AddMilliseconds(10) &&
                   second.BatchEndUtc == origin.AddMilliseconds(20),
                "追赶回调错误贴到主机时间后继续推进");
            Assert(!first.IsFuture && !second.IsFuture && first.ArrivalDelayMs > 400,
                "历史积压批被误判为未来样本");

            var invalid = new DeviceSampleTimeline();
            invalid.Reset(origin, originTick);
            var future = invalid.Advance(
                1040, 2000, origin.AddMilliseconds(20),
                originTick + Stopwatch.Frequency / 50, 20);
            Assert(future.IsFuture && future.SampleLeadMs >= 499,
                "未来约500ms的样本时间没有触发不变量");
        }

        private static void ControlBatchCarriesIdentityClockAndRawTail()
        {
            var ring = new ControlBatchRing(4, 1);
            var raw = new double[1, 20];
            for (var i = 0; i < 20; i++) raw[0, i] = i / 10.0;
            var values = new[] { new FastControlSampleValue(0, 4, 0, 12.5) };
            var metadata = new FastControlBatchMetadata(
                7, 123, DateTime.UtcNow, DateTime.UtcNow,
                1000, 1100, 0, 42, FastSignalQualityFlags.None);
            Assert(ring.TryEnqueue(metadata, values, raw), "结构化控制批入环失败");
            var destination = new FastControlSampleValue[1];
            var rawTail = new double[ControlBatchRing.RawTailCapacity];
            Assert(ring.TryDequeue(destination, rawTail, out var count, out var rawCount, out var actual),
                "结构化控制批出环失败");
            Assert(count == 1 && rawCount == 20 && actual.Generation == 7 &&
                   actual.SourceSequence == 123 && actual.CaptureMonotonicTicks == 1000 &&
                   Math.Abs(rawTail[19] - 1.9) < 1e-12,
                "控制批身份、单调时钟或原始尾部损坏");
        }

        private static void FastFiltersAreIsolatedAndResettable()
        {
            var dev1 = new ClsDataFilter.FastFilter(3, 0.5, 0);
            var dev2 = new ClsDataFilter.FastFilter(3, 0.5, 0);
            var tick = Stopwatch.Frequency;
            dev1.Update("EPB4_current", 50, tick);
            var dev2Value = dev2.Update("EPB8_current", 2, tick);
            Assert(Math.Abs(dev2Value - 2) < 1e-12, "Dev1快速值污染Dev2滤波状态");
            dev1.Update("EPB4_current", 60, tick + 1);
            dev1.Reset();
            var resetValue = dev1.Update("EPB4_current", 3, tick + 2);
            Assert(Math.Abs(resetValue - 3) < 1e-12, "代次复位后仍继承旧快速滤波状态");
        }

        private static void ControlBatchIdentityRejectsInvalidOrder()
        {
            var validator = new ControlBatchIdentityValidator();
            FastControlBatchMetadata Metadata(long generation, long sequence, long tick) =>
                new FastControlBatchMetadata(
                    generation, sequence, DateTime.UtcNow, DateTime.UtcNow,
                    tick, tick + 1, 0, 7, FastSignalQualityFlags.None);

            Assert(validator.Validate(Metadata(1, 10, 100)) == ControlBatchIdentityResult.GenerationChanged,
                "首批未建立代次身份");
            Assert(validator.Validate(Metadata(1, 11, 110)) == ControlBatchIdentityResult.Accepted,
                "连续批次被拒绝");
            Assert(validator.Validate(Metadata(1, 11, 120)) == ControlBatchIdentityResult.Duplicate,
                "重复批次未拒绝");
            Assert(validator.Validate(Metadata(1, 9, 130)) == ControlBatchIdentityResult.OutOfOrder,
                "倒序批次未拒绝");
            Assert(validator.Validate(Metadata(1, 12, 110)) == ControlBatchIdentityResult.MonotonicTickInvalid,
                "非递增回调tick未拒绝");
            Assert(validator.Validate(Metadata(1, 13, 130)) == ControlBatchIdentityResult.Gap,
                "序号缺口未识别");
            validator.Reset();
            Assert(validator.Validate(Metadata(2, 1, 10)) == ControlBatchIdentityResult.GenerationChanged,
                "新代次未复位批次身份");
        }

        private static void FastFilterHotLoopDoesNotAllocate()
        {
            var filter = new ClsDataFilter.FastFilter(9, 0.4, 0);
            var tick = Stopwatch.Frequency;
            for (var i = 0; i < 100; i++)
                filter.Update("EPB4_current", i % 20, tick + i);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100000; i++)
                filter.Update("EPB4_current", i % 20, tick + 100 + i);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 1024,
                $"快速滤波稳态热路径产生持续托管分配：{allocated} bytes");
        }

        private static void StartupClassifierIgnoresDuplicateBatch()
        {
            var classifier = new StartupPositioningCurrentClassifier(100, 14.2, 18, 3);
            Assert(classifier.Evaluate(10, 120, 20) == StartupCurrentClassification.None,
                "首个过流样本过早触发");
            Assert(classifier.Evaluate(10, 130, 20) == StartupCurrentClassification.None &&
                   classifier.Evaluate(10, 140, 20) == StartupCurrentClassification.None,
                "相同批次被重复累计");
            Assert(classifier.Evaluate(11, 150, 20) == StartupCurrentClassification.None,
                "第二个独立批次过早触发");
            Assert(classifier.Evaluate(12, 160, 20) == StartupCurrentClassification.OverCurrent,
                "三个独立批次未触发过流");
        }

        private static void FutureWallClockDoesNotChangeControlElapsed()
        {
            var start = Stopwatch.Frequency;
            var sample = new FastEpbCurrentSample(
                4, 5, 5, DateTime.UtcNow.AddMilliseconds(500),
                start + Stopwatch.Frequency / 20, 1, 1, FastSignalQualityFlags.None);
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            machine.ArmForward(start, 100, 1000, 15, 0, 3);
            var decision = machine.OnSample(sample.CaptureMonotonicTicks, sample.CurrentA);
            Assert(decision.ElapsedMs >= 49 && decision.ElapsedMs <= 51 &&
                   decision.Stage == EpbCurrentStage.Inrush,
                "未来墙钟跨过了100ms浪涌窗口");
        }

        private static void FastTripClassificationUsesFullRateEvidence()
        {
            var invalid62 = FastPathTripClassifier.ClassifyOverCurrent(
                62.333, 18, 14.780, 10, 1, 100, FastSignalQualityFlags.None);
            var invalid96 = FastPathTripClassifier.ClassifyOverCurrent(
                95.873, 18, 15.53, 10, 1, 100, FastSignalQualityFlags.AdcNearRail);
            var confirmed = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 18.5, 10, 1, 100, FastSignalQualityFlags.None);
            var unavailable = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, double.NaN, double.PositiveInfinity, 1, 100, FastSignalQualityFlags.None);
            var stale = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 20, 101, 1, 100, FastSignalQualityFlags.None);
            var invalidTick = FastPathTripClassifier.ClassifyOverCurrent(
                20, 18, 20, 10, 1, 100, FastSignalQualityFlags.MonotonicTickInvalid);
            Assert(invalid62.Classification == FastPathTripClassification.FastPathSignalInvalid &&
                   invalid96.Classification == FastPathTripClassification.FastPathSignalInvalid,
                "09:08事故特征仍被归类为真实过流");
            Assert(confirmed.Classification == FastPathTripClassification.ConfirmedOverCurrent,
                "真实全速率过流未确认");
            Assert(unavailable.Classification == FastPathTripClassification.FastPathEvidenceUnavailable &&
                   stale.Classification == FastPathTripClassification.FastPathEvidenceUnavailable,
                "证据缺失或过期未按失效安全归类");
            Assert(invalidTick.Classification == FastPathTripClassification.FastPathSignalInvalid,
                "单调时钟异常未归类为快速信号无效");

            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            machine.ArmForward(Stopwatch.Frequency, 100, 1000, 15, 0, 3);
            var qualityFault = machine.OnInvalidFastSignalReusable(
                Stopwatch.Frequency + 1,
                double.NaN,
                FastSignalQualityFlags.NonFinite,
                new EpbAdaptiveDecision());
            Assert(qualityFault.HardFault &&
                   qualityFault.Reason.Contains("FastPathSignalInvalid"),
                "带电阶段无效快速样本没有直接触发安全故障");
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
