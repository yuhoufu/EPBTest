using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using Controller.Adaptive;
using DataOperation;
using IO.NI;
using Timing;

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
            Run("恢复轮询跨多批仍按连续序号累计", RecoveryVerifierAcceptsBurstProgress, ref passed);
            Run("恢复连续性故障后重新建立干净窗口", RecoveryVerifierResetsOnRealDiscontinuity, ref passed);
            Run("控制积压先追最新而回调故障才重建", DaqFastResyncRecreatePolicy, ref passed);
            Run("软件恢复失败持续自维护且仅硬件证据报警", DaqSelfMaintenancePolicy, ref passed);
            Run("恢复阶段只在终态导出完整重证据", IncidentSnapshotHeavyEvidencePolicy, ref passed);
            Run("DAQ恢复先恢复安全电源再做机械定位", DaqRecoveryPrerequisiteOrder, ref passed);
            Run("UI发布限频不影响首批和周期后批次", UiDispatchGateUsesMonotonicRateLimit, ref passed);
            Run("DAQ陈旧根因区分回调与控制消费", DaqStaleRootClassification, ref passed);
            Run("DAQ批次和兼容队列包装不再持续分配", DaqBatchObjectsAreReusableValueBacked, ref passed);
            Run("旧原始二进制写入池化后格式保持不变", LegacyRawWriterKeepsBinaryFormat, ref passed);
            Run("标定前原始数据复制后不被原地标定污染", OwnedRawBatchPreservesPreCalibrationValues, ref passed);
            Run("Stat流式中值保留跨批次尾部", StreamingStatMedianCarriesTailAcrossBatches, ref passed);
            Run("恢复后定时器只在未来完整周期锚点执行", TimerResumesAtFutureCompleteBoundary, ref passed);
            Run("暂停数据链必须越过捕获边界且无在途Raw", PauseDrainRequiresAllPipelineBoundaries, ref passed);
            Run("恢复成功超时停止硬件确认并发只提交一个终态", RecoveryTerminalGateCommitsExactlyOnce, ref passed);
            Run("DAQ探测能力缺失不能误确认为硬件拔除", ProbeCapabilityMissingIsNotHardwareEvidence, ref passed);
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
            Run("Dev1与Dev2相反ppm连续72小时独立锁定", ClockTimelinesTrackOppositePpmFor72Hours, ref passed);
            Run("现场追赶回调不再触发时间轴故障", ClockTimelineReplaysFieldCatchUpWithoutFault, ref passed);
            Run("UTC前后跳变不改变单调采样时间轴", ClockTimelineIgnoresUtcJumps, ref passed);
            Run("时钟越界必须连续确认才失效", ClockTimelineRequiresConsecutiveInvalidEvidence, ref passed);
            Run("锁定后低延迟包络连续超前才触发恢复", ClockTimelineRequiresSustainedResidualLead, ref passed);
            Run("DAQ时钟恢复十分钟前三次允许第四次锁存", ClockRecoveryAttemptWindowIsBounded, ref passed);
            Run("自适应时钟十万批稳态无持续分配", ClockTimelineHotLoopDoesNotAllocate, ref passed);
            Run("旧TimelineFuture标志不再使有效电流失效", LegacyTimelineFlagIsDiagnosticOnly, ref passed);
            Run("控制批携带序号单调时钟和原始尾部", ControlBatchCarriesIdentityClockAndRawTail, ref passed);
            Run("快速控制复制完成后才移交后台原始批次", RawBatchOwnershipTransfersAfterControlCopy, ref passed);
            Run("Dev1和Dev2各十万批所有权移交不污染快速证据", RawBatchOwnershipStressForBothDevices, ref passed);
            Run("V2.10.2.1现场二次换算序列修复后不再过流", FieldIncidentReplayStaysBelowOverCurrent, ref passed);
            Run("控制消费拒绝重复倒序和非递增tick", ControlBatchIdentityRejectsInvalidOrder, ref passed);
            Run("每设备快速滤波隔离并按代次复位", FastFiltersAreIsolatedAndResettable, ref passed);
            Run("快速滤波十万次稳态更新无持续分配", FastFilterHotLoopDoesNotAllocate, ref passed);
            Run("启动定位相同批次不重复计数", StartupClassifierIgnoresDuplicateBatch, ref passed);
            Run("未来墙钟不改变控制经过时间", FutureWallClockDoesNotChangeControlElapsed, ref passed);
            Run("快速过流由全速率证据最终归因", FastTripClassificationUsesFullRateEvidence, ref passed);
            return passed;
        }

        private static void PauseDrainRequiresAllPipelineBoundaries()
        {
            Assert(TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 100, 200),
                "全部边界已发布且无Raw在途时未判定排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    99, 100, 200, 200, 100, 200),
                "Dev1尚未越过捕获边界时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 199, 200, 100, 200),
                "Dev2尚未越过捕获边界时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 99, 200),
                "Dev1 Raw尚未移交到写盘队列时错误完成暂停排空");
            Assert(!TwoDeviceAiAcquirer.IsBackgroundPipelineDrained(
                    100, 100, 200, 200, 100, 199),
                "Dev2 Raw尚未移交到写盘队列时错误完成暂停排空");
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

        private static void RecoveryVerifierAcceptsBurstProgress()
        {
            var verifier = new DaqRecoveryFreshnessVerifier(6, 10);
            verifier.Seed(Freshness(6, 100, 1000, 0));
            Assert(!verifier.Observe(Freshness(6, 103, 1010, 0)),
                "首个+3突发被过早判定完成");
            Assert(verifier.FreshCallbacks == 3 && verifier.FirstVerifiedSequence == 101,
                $"+3突发没有按真实连续批数累计，Count={verifier.FreshCallbacks}");
            Assert(verifier.Observe(Freshness(6, 110, 1020, 0)),
                "轮询跨过连续+7批后未满足10批恢复条件");
            Assert(verifier.FreshCallbacks == 10 && verifier.LastVerifiedSequence == 110,
                "恢复序号范围记录错误");
        }

        private static void RecoveryVerifierResetsOnRealDiscontinuity()
        {
            var verifier = new DaqRecoveryFreshnessVerifier(6, 3);
            verifier.Seed(Freshness(6, 200, 2000, 0));
            Assert(!verifier.Observe(Freshness(6, 202, 2010, 1)),
                "真实连续性故障被误判为恢复完成");
            Assert(verifier.FreshCallbacks == 0 && verifier.FirstVerifiedSequence == 0,
                "连续性故障后恢复窗口未清零");
            Assert(verifier.Observe(Freshness(6, 205, 2020, 1)),
                "故障后新的3个连续批次未重新建立恢复窗口");

            var stale = Freshness(6, 206, 2030, 1);
            stale.IsFresh = false;
            Assert(!verifier.Observe(stale) && verifier.FreshCallbacks == 0,
                "陈旧样本没有清除恢复证据");
        }

        private static DaqFreshnessSnapshot Freshness(
            long generation,
            long sequence,
            long arrivalTicks,
            long discontinuities)
        {
            return new DaqFreshnessSnapshot
            {
                Device = "Dev2",
                Generation = generation,
                LastProcessedSequence = sequence,
                LastArrivalMonotonicTicks = arrivalTicks,
                ControlDiscontinuityCount = discontinuities,
                LastControlDiscontinuitySequence = discontinuities > 0 ? sequence : 0,
                IsFresh = true
            };
        }

        private static void DaqFastResyncRecreatePolicy()
        {
            Assert(!EpbManager.RequiresDaqTaskRecreate("ControlLatencyExceeded"),
                "控制积压仍被强制Stop/Start，未先丢旧追新");
            Assert(!EpbManager.RequiresDaqTaskRecreate("ControlQueueFull"),
                "控制队列积压未走快速重同步");
            Assert(EpbManager.RequiresDaqTaskRecreate("DaqCallbackStale"),
                "真实回调中断未要求DAQ任务重建");
            Assert(EpbManager.RequiresDaqTaskRecreate("DaqClockModelInvalid"),
                "时钟模型失效未要求DAQ任务重建");
        }

        private static void DaqSelfMaintenancePolicy()
        {
            Assert(DaqRecoveryFailurePolicy.Evaluate(0, false) ==
                   DaqRecoveryFailureDisposition.ContinueSelfMaintenance,
                "普通软件恢复失败被升级成报警停机");
            Assert(DaqRecoveryFailurePolicy.Evaluate(1, true) ==
                   DaqRecoveryFailureDisposition.ContinueSelfMaintenance,
                "单份硬件证据被错误锁存报警");
            Assert(DaqRecoveryFailurePolicy.Evaluate(2, true) ==
                   DaqRecoveryFailureDisposition.ConfirmedHardwareAlarm,
                "两份独立硬件失效证据未触发硬件报警");
            Assert(EpbManager.GetDaqSelfMaintenanceDelayMs(1) == 1000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(2) == 2000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(3) == 5000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(4) == 10000 &&
                   EpbManager.GetDaqSelfMaintenanceDelayMs(20) == 30000,
                "自维护退避不是1/2/5/10/30秒有界序列");
        }

        private static void IncidentSnapshotHeavyEvidencePolicy()
        {
            Assert(!EpbManager.ShouldIncludeFullDaqIncidentEvidence("00-trigger") &&
                   !EpbManager.ShouldIncludeFullDaqIncidentEvidence("40-self-maintenance") &&
                   EpbManager.ShouldIncludeFullDaqIncidentEvidence("90-recovered") &&
                   EpbManager.ShouldIncludeFullDaqIncidentEvidence("90-hardware-confirmed"),
                "恢复关键窗口仍会重复导出完整诊断和最近圈证据");
            Assert(EpbManager.ShouldIncludeDaqTimingEvidence("00-trigger") &&
                   !EpbManager.ShouldIncludeDaqTimingEvidence("40-self-maintenance") &&
                   EpbManager.ShouldIncludeDaqTimingEvidence("90-recovered"),
                "触发瞬间未保留轻量时序证据，或维护阶段仍在重复导出");
        }

        private static void DaqRecoveryPrerequisiteOrder()
        {
            var order = new List<string>();
            EpbManager.ExecuteDaqRecoveryRejoinPrerequisitesAsync(
                    new[] { 10, 8, 8 },
                    (channels, _) =>
                    {
                        order.Add("Power:" + string.Join(",", channels));
                        return Task.CompletedTask;
                    },
                    (channels, _) =>
                    {
                        order.Add("Mechanical:" + string.Join(",", channels));
                        return Task.CompletedTask;
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert(order.SequenceEqual(new[] { "Power:8,10", "Mechanical:8,10" }),
                "DAQ恢复仍在电源安全预检前执行机械定位");
            Assert(EpbManager.ShouldDeferIndependentRejoinForDaq(true) &&
                   !EpbManager.ShouldDeferIndependentRejoinForDaq(false),
                "通道级恢复没有正确服从DAQ组恢复所有权");
        }

        private static void IncidentCorrelationAndPriority()
        {
            var latch = new DaqIncidentLatch();
            var run1 = Guid.NewGuid();
            latch.BeginRun(run1, new[] { "Dev1", "Dev2" });
            var first = latch.Observe(run1, "Dev1", 5, "ControlLatencyExceeded", "late",
                DateTime.UtcNow, new[] { 5, 4 }, primaryChannel: 5);
            Assert(first.IsFirst && first.Context.PrimaryChannel == 5, "首事故或触发通道不正确");
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

        private static void OwnedRawBatchPreservesPreCalibrationValues()
        {
            var source = new[,]
            {
                { 1.25, 2.5, 3.75 },
                { -4.0, 5.125, 6.25 }
            };
            using (var owned = OwnedDaqRawBatch.CopyFrom(
                       "Dev1", source, DateTime.UtcNow, DateTime.UtcNow.AddMilliseconds(-10)))
            {
                for (var channel = 0; channel < source.GetLength(0); channel++)
                for (var sample = 0; sample < source.GetLength(1); sample++)
                {
                    var expected = source[channel, sample];
                    source[channel, sample] = expected * 1000 + 7;
                    Assert(Math.Abs(owned[channel, sample] - expected) < 1e-12,
                        "池化原始缓冲被后续原地标定修改。");
                }

                Assert(owned.ChannelCount == 2 && owned.SampleCount == 3 && owned.Device == "Dev1",
                    "池化原始批次身份或维度错误。");
            }
        }

        private static void StreamingStatMedianCarriesTailAcrossBatches()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "epb-stat-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 2, directory)
                {
                    medianLens = 3,
                    eMBToDaqCurrentChannel = new SortedDictionary<string, int>
                    {
                        ["EPB1_current"] = 0
                    }
                };
                context.paraNameToScale["EPB1_current"] = 1;
                context.paraNameToOffset["EPB1_current"] = 0;
                context.paraNameToZeroValue["EPB1_current"] = 0;

                context.EnqueueStatData(new[,] { { 100.0, 1.0 } }, DateTime.UtcNow);
                context.EnqueueStatData(new[,] { { 2.0 } }, DateTime.UtcNow.AddMilliseconds(2));
                context.FlushStatToDiskAsync().GetAwaiter().GetResult();

                var file = Directory.GetFiles(directory, "DAQ_Dev1_Stat.bin").Single();
                using (var reader = new BinaryReader(File.OpenRead(file)))
                {
                    Assert(reader.ReadInt32() == 1, "Stat记录计数格式发生变化。");
                    reader.ReadInt64();
                    var maximum = reader.ReadDouble();
                    var minimum = reader.ReadDouble();
                    Assert(Math.Abs(maximum - 2.0) < 1e-12 && Math.Abs(minimum - 2.0) < 1e-12,
                        $"跨批次中值尾部丢失：Min={minimum}, Max={maximum}。");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }

        private static void TimerResumesAtFutureCompleteBoundary()
        {
            var timer = new HighPrecisionTimer(500, OverrunPolicy.AlignToWallClock);
            using (var firstStarted = new ManualResetEventSlim(false))
            using (var releaseFirst = new ManualResetEventSlim(false))
            using (var secondStarted = new ManualResetEventSlim(false))
            {
                var clock = Stopwatch.StartNew();
                long secondAt = -1;
                var running = timer.StartAsync(2, 0, (cycle, token) =>
                {
                    if (cycle == 1)
                    {
                        firstStarted.Set();
                        releaseFirst.Wait(token);
                    }
                    else
                    {
                        secondAt = clock.ElapsedMilliseconds;
                        secondStarted.Set();
                    }
                    return Task.FromResult(true);
                });

                Assert(firstStarted.Wait(TimeSpan.FromSeconds(2)), "首个周期未进入测试阻塞点。");
                timer.Pause();
                releaseFirst.Set();
                Thread.Sleep(50);
                var resumedAt = clock.ElapsedMilliseconds;
                timer.ResumeAtNextBoundary(220);

                Assert(secondStarted.Wait(TimeSpan.FromSeconds(2)), "恢复后的未来完整周期未执行。");
                running.GetAwaiter().GetResult();
                var delay = secondAt - resumedAt;
                Assert(delay >= 180 && delay < 1500,
                    $"定时器恢复后沿用旧半圈或锚点异常：Delay={delay}ms。");
            }
        }

        private static void RecoveryTerminalGateCommitsExactlyOnce()
        {
            var gate = new DaqRecoveryTerminalGate();
            var winners = 0;
            using (var start = new ManualResetEventSlim(false))
            {
                var tasks = Enumerable.Range(0, 64).Select(index => Task.Run(() =>
                {
                    start.Wait();
                    var proposed = (DaqRecoveryTerminal)(index % 4 + 1);
                    if (gate.TryCommit(proposed)) Interlocked.Increment(ref winners);
                })).ToArray();
                start.Set();
                Task.WaitAll(tasks);
            }
            Assert(winners == 1, $"并发恢复终态提交次数错误：{winners}。");
            Assert(gate.Current != DaqRecoveryTerminal.None,
                "并发恢复没有留下唯一可审计终态。");
            Assert(!gate.TryCommit(DaqRecoveryTerminal.SystemFault),
                "恢复终态提交后仍可被迟到超时覆盖。");
        }

        private static void ProbeCapabilityMissingIsNotHardwareEvidence()
        {
            var unavailable = new DaqHardwareProbeResult
            {
                Device = "Dev1",
                EnumerationSucceeded = true,
                DevicePresent = true,
                SelfTestAttempted = false,
                SelfTestSucceeded = false
            };
            var absent = new DaqHardwareProbeResult
            {
                Device = "Dev1",
                EnumerationSucceeded = true,
                DevicePresent = false
            };
            Assert(!unavailable.IndependentFailureConfirmed &&
                   unavailable.ToEvidence().Code == "DeviceSelfTestUnavailable",
                "DAQmx版本能力缺失被误当成设备硬件故障。");
            Assert(absent.IndependentFailureConfirmed,
                "DAQmx明确枚举缺失未形成独立硬件证据。");
        }

        private static void UiDispatchGateUsesMonotonicRateLimit()
        {
            var gate = new PeriodicDispatchGate(25);
            var interval = Stopwatch.Frequency / 25;
            var start = Stopwatch.Frequency;
            Assert(gate.TryAcquire(start), "首个UI批次未发布");
            Assert(!gate.TryAcquire(start + interval / 2), "限频窗口内重复发布");
            Assert(gate.TryAcquire(start + interval + 1), "限频窗口后未发布");
            gate.Reset();
            Assert(gate.TryAcquire(start + interval + 2), "复位后首批未立即发布");
        }

        private static void DaqStaleRootClassification()
        {
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 1167,
                ControlEnqueueAgeMs = 1167,
                ControlProcessedAgeMs = 1167
            }) == "DaqCallbackStale", "回调空窗未识别为根故障");
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 10,
                ControlEnqueueAgeMs = 120,
                ControlProcessedAgeMs = 130
            }) == "ControlEnqueueStale", "回调到控制入队停顿未识别");
            Assert(EpbManager.ClassifyDaqStaleRoot(new DaqFreshnessSnapshot
            {
                CallbackAgeMs = 10,
                ControlEnqueueAgeMs = 10,
                ControlProcessedAgeMs = 120
            }) == "ControlProcessingStale", "控制消费停顿未识别");
        }

        private static void DaqBatchObjectsAreReusableValueBacked()
        {
            Assert(typeof(DaqAIData).IsValueType, "旧原始/统计队列仍为每批创建引用对象");

            DaqDiskBatch Rent(long sequence)
            {
                var timestamps = ArrayPool<DateTime>.Shared.Rent(1);
                var currents = ArrayPool<double>.Shared.Rent(1);
                var channels = ArrayPool<DaqDiskChannelBatch>.Shared.Rent(1);
                timestamps[0] = DateTime.UtcNow;
                currents[0] = sequence;
                channels[0] = new DaqDiskChannelBatch(4, currents);
                return DaqDiskBatch.Rent(
                    "Dev1", 1, sequence, 1, timestamps, channels, 1,
                    null, null, Stopwatch.GetTimestamp(),
                    2000.0302, ClockState.Locked, 15.1, -25.4, 60);
            }

            var first = Rent(1);
            first.Dispose();
            var second = Rent(2);
            Assert(ReferenceEquals(first, second), "持久化批次对象池未复用对象");
            Assert(Math.Abs(second.EffectiveSampleRateHz - 2000.0302) < 1e-9 &&
                   second.ClockState == ClockState.Locked &&
                   Math.Abs(second.EstimatedSkewPpm - 15.1) < 1e-9,
                "持久化批次没有携带归档时钟元数据");
            second.Dispose();
        }

        private static void LegacyRawWriterKeepsBinaryFormat()
        {
            var root = Path.Combine(Path.GetTempPath(), "EPBTest-RawWriter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var context = new DaqAIContext("Dev1", 10, 60, 0.5, 2, 2, root);
                var now = DateTime.Now;
                context.EnqueueRawData(new double[,] { { 0, 0 }, { 0, 0 } }, now, now.AddMilliseconds(-1));
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();
                context.EnqueueRawData(new double[,] { { 1.25, 2.5 }, { -3.75, 4.5 } },
                    now.AddMilliseconds(1), now);
                context.FlushRawToDiskAsync().GetAwaiter().GetResult();

                var path = Path.Combine(root, "DAQ_Dev1_Raw_1.bin");
                using var stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                Assert(stream.Length == 56, $"原始二进制长度变化：{stream.Length}");
                Assert(reader.ReadInt32() == 1, "原始二进制计数器布局变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble() - 1.25) < 1e-12 &&
                       Math.Abs(reader.ReadDouble() + 3.75) < 1e-12,
                    "原始二进制首样本布局变化");
                Assert(reader.ReadInt32() == 1, "原始二进制第二样本计数器变化");
                _ = reader.ReadInt64();
                Assert(Math.Abs(reader.ReadDouble() - 2.5) < 1e-12 &&
                       Math.Abs(reader.ReadDouble() - 4.5) < 1e-12,
                    "原始二进制第二样本布局变化");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
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
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, 2000);
            var first = timeline.Advance(
                20, origin.AddMilliseconds(500),
                originTick + Stopwatch.Frequency / 2);
            var second = timeline.Advance(
                20, origin.AddMilliseconds(501),
                originTick + (long)(0.501 * Stopwatch.Frequency));
            Assert(first.BatchEndUtc == origin.AddMilliseconds(10) &&
                   second.BatchEndUtc == origin.AddMilliseconds(20),
                "追赶回调错误贴到主机时间后继续推进");
            Assert(!first.RequiresRecovery && !second.RequiresRecovery && first.ArrivalDelayMs > 400,
                "历史积压批被误判为未来样本");
        }

        private static void ClockTimelinesTrackOppositePpmFor72Hours()
        {
            const double nominalRate = 2000;
            const double dev1Ppm = 15.1;
            const double dev2Ppm = -29.5;
            const int hours = 72;
            var origin = new DateTime(2026, 8, 5, 2, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 10L;
            var dev1 = new ClockDisciplinedSampleTimeline();
            var dev2 = new ClockDisciplinedSampleTimeline();
            dev1.Reset(origin, originTick, nominalRate);
            dev2.Reset(origin, originTick, nominalRate);
            long dev1Samples = 0;
            long dev2Samples = 0;
            var dev1Previous = origin;
            var dev2Previous = origin;
            ClockDisciplinedTimelineResult dev1Result = default;
            ClockDisciplinedTimelineResult dev2Result = default;
            var dev1Rate = nominalRate * (1 + dev1Ppm / 1_000_000.0);
            var dev2Rate = nominalRate * (1 + dev2Ppm / 1_000_000.0);

            for (var second = 0; second < hours * 3600; second++)
            {
                dev1Samples += 2000;
                dev2Samples += 2000;
                dev1Result = AdvanceClock(
                    dev1, origin, originTick, dev1Samples, 2000, dev1Rate, 26, second % 3 == 0 ? 0.3 : 0);
                dev2Result = AdvanceClock(
                    dev2, origin, originTick, dev2Samples, 2000, dev2Rate, 29, second % 5 == 0 ? 0.4 : 0);
                Assert(dev1Result.BatchEndUtc > dev1Previous && dev2Result.BatchEndUtc > dev2Previous,
                    "72小时模拟中批次时间没有严格递增");
                Assert(!dev1Result.RequiresRecovery && !dev2Result.RequiresRecovery,
                    "合理ppm漂移被误判为时钟模型失效");
                dev1Previous = dev1Result.BatchEndUtc;
                dev2Previous = dev2Result.BatchEndUtc;
            }

            Assert(dev1Result.ClockState == ClockState.Locked &&
                   dev2Result.ClockState == ClockState.Locked,
                "72小时后设备时钟没有锁定");
            Assert(Math.Abs(dev1Result.EstimatedSkewPpm - dev1Ppm) < 1.0,
                $"Dev1 ppm估计不准确：{dev1Result.EstimatedSkewPpm:F3}");
            Assert(Math.Abs(dev2Result.EstimatedSkewPpm - dev2Ppm) < 1.0,
                $"Dev2 ppm估计不准确：{dev2Result.EstimatedSkewPpm:F3}");
            Assert(dev1Result.TotalSamples == (long)hours * 3600 * 2000 &&
                   dev2Result.TotalSamples == (long)hours * 3600 * 2000,
                "72小时模拟存在样本丢失");
        }

        private static void ClockTimelineReplaysFieldCatchUpWithoutFault()
        {
            const double nominalRate = 2000;
            var actualRate = nominalRate * (1 + 15.1 / 1_000_000.0);
            var origin = new DateTime(2026, 8, 5, 6, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 20L;
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, nominalRate);
            long totalSamples = 0;
            ClockDisciplinedTimelineResult result = default;
            for (var second = 0; second < 29 * 60; second++)
            {
                totalSamples += 2000;
                result = AdvanceClock(timeline, origin, originTick, totalSamples, 2000, actualRate, 26, 0);
            }

            var callbackTick = originTick + (long)Math.Round(
                (totalSamples / actualRate + 0.026) * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero);
            var intervalsMs = new[] { 15.047, 1.334, 0.142 };
            var sequenceCount = 0;
            foreach (var intervalMs in intervalsMs)
            {
                totalSamples += 20;
                callbackTick += (long)Math.Round(
                    intervalMs / 1000.0 * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                result = timeline.Advance(20, origin, callbackTick);
                Assert(!result.RequiresRecovery, "现场追赶回调被误判为时钟模型硬故障");
                sequenceCount++;
            }
            Assert(sequenceCount == 3 && result.TotalSamples == totalSamples,
                "现场175218~175220追赶批次没有全部保留");
        }

        private static void ClockTimelineIgnoresUtcJumps()
        {
            var origin = new DateTime(2026, 8, 5, 7, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 30L;
            var baseline = new ClockDisciplinedSampleTimeline();
            var jumped = new ClockDisciplinedSampleTimeline();
            baseline.Reset(origin, originTick, 2000);
            jumped.Reset(origin, originTick, 2000);
            long samples = 0;
            for (var second = 1; second <= 120; second++)
            {
                samples += 2000;
                var callbackTick = originTick + (long)Math.Round(
                    (second + 0.025) * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                var normalUtc = origin.AddSeconds(second).AddMilliseconds(25);
                var jumpedUtc = second < 40
                    ? normalUtc
                    : second < 80
                        ? normalUtc.AddHours(6)
                        : normalUtc.AddHours(-6);
                var normal = baseline.Advance(2000, normalUtc, callbackTick);
                var shifted = jumped.Advance(2000, jumpedUtc, callbackTick);
                Assert(normal.BatchEndUtc == shifted.BatchEndUtc &&
                       normal.BatchEndMonotonicTicks == shifted.BatchEndMonotonicTicks &&
                       normal.ClockState == shifted.ClockState,
                    "UTC跳变污染了采样时间轴或时钟状态");
            }
        }

        private static void ClockTimelineRequiresConsecutiveInvalidEvidence()
        {
            var options = new ClockDisciplineOptions(
                estimatorWindowSeconds: 30,
                warmupSeconds: 10,
                maxAbsSkewPpm: 250,
                maxCorrectionPpmPerUpdate: 5,
                residualHardLimitMs: 100,
                invalidConfirmations: 10);
            var origin = new DateTime(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 40L;
            var timeline = new ClockDisciplinedSampleTimeline(options);
            timeline.Reset(origin, originTick, 2000);
            var outOfSpecRate = 2000 * (1 + 1000.0 / 1_000_000.0);
            long samples = 0;
            ClockDisciplinedTimelineResult result = default;
            var firstInvalidSecond = -1;
            for (var second = 0; second < 40; second++)
            {
                samples += 2000;
                result = AdvanceClock(timeline, origin, originTick, samples, 2000, outOfSpecRate, 25, 0);
                if (result.RequiresRecovery)
                {
                    firstInvalidSecond = second;
                    break;
                }
            }
            Assert(firstInvalidSecond >= 19,
                $"时钟异常未经过10次连续确认即失效：second={firstInvalidSecond}");

            var transient = new ClockDisciplinedSampleTimeline(options);
            transient.Reset(origin, originTick, 2000);
            samples = 0;
            for (var second = 0; second < 45; second++)
            {
                samples += 2000;
                var rate = second == 20 ? outOfSpecRate : 2000;
                result = AdvanceClock(transient, origin, originTick, samples, 2000, rate, 25, 0);
                Assert(!result.RequiresRecovery, "单次采样时钟离群错误触发恢复");
            }
        }

        private static void ClockTimelineRequiresSustainedResidualLead()
        {
            var options = new ClockDisciplineOptions(
                estimatorWindowSeconds: 60,
                warmupSeconds: 30,
                maxAbsSkewPpm: 250,
                maxCorrectionPpmPerUpdate: 5,
                residualHardLimitMs: 100,
                invalidConfirmations: 10);
            var origin = new DateTime(2026, 8, 5, 8, 30, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 45L;
            var timeline = new ClockDisciplinedSampleTimeline(options);
            timeline.Reset(origin, originTick, 2000);
            long samples = 0;
            var lockedSeen = false;
            var lockedFitsBeforeInvalid = 0;
            ClockDisciplinedTimelineResult result = default;

            // 保持名义采样率不变，只让低延迟包络持续领先100ms以上。
            // 暖机阶段不得失效；锁定后仍须连续10次确认才进入恢复。
            for (var second = 1; second <= 90; second++)
            {
                samples += 2000;
                var callbackSeconds = samples / 2000.0 - 0.150;
                var callbackTick = originTick + (long)Math.Round(
                    callbackSeconds * Stopwatch.Frequency,
                    MidpointRounding.AwayFromZero);
                result = timeline.Advance(2000, origin.AddSeconds(callbackSeconds), callbackTick);
                if (result.ClockState == ClockState.Locked)
                {
                    lockedSeen = true;
                    lockedFitsBeforeInvalid++;
                }
                if (result.RequiresRecovery) break;
            }

            Assert(lockedSeen, "持续residual超前在模型锁定前错误进入恢复");
            Assert(result.RequiresRecovery && result.ResidualMs > 100,
                "锁定后的持续residual超前没有触发DAQ时钟恢复");
            Assert(lockedFitsBeforeInvalid >= 9,
                $"residual超前未经过连续确认即失效：lockedFits={lockedFitsBeforeInvalid}");
        }

        private static void ClockRecoveryAttemptWindowIsBounded()
        {
            var window = new DaqRecoveryAttemptWindow(3, TimeSpan.FromMinutes(10));
            var start = Stopwatch.Frequency * 100L;
            Assert(window.TryRegister("Dev1", start, out var first) && first == 1,
                "第一次时钟恢复被拒绝");
            Assert(window.TryRegister("Dev1", start + Stopwatch.Frequency, out var second) && second == 2,
                "第二次时钟恢复被拒绝");
            Assert(window.TryRegister("Dev1", start + 2 * Stopwatch.Frequency, out var third) && third == 3,
                "第三次时钟恢复被拒绝");
            Assert(!window.TryRegister("Dev1", start + 3 * Stopwatch.Frequency, out var fourth) && fourth == 3,
                "10分钟内第四次时钟恢复没有锁存");
            Assert(window.TryRegister("Dev2", start + 3 * Stopwatch.Frequency, out var isolated) && isolated == 1,
                "Dev1恢复计数污染Dev2");
            Assert(window.TryRegister(
                       "Dev1",
                       start + (long)(TimeSpan.FromMinutes(12).TotalSeconds * Stopwatch.Frequency) + 1,
                       out var expired) && expired == 1,
                "10分钟窗口过期后恢复次数没有释放");
        }

        private static void ClockTimelineHotLoopDoesNotAllocate()
        {
            var origin = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
            var originTick = Stopwatch.Frequency * 50L;
            var timeline = new ClockDisciplinedSampleTimeline();
            timeline.Reset(origin, originTick, 2000);
            long samples = 0;
            for (var i = 0; i < 10000; i++)
            {
                samples += 20;
                AdvanceClock(timeline, origin, originTick, samples, 20, 2000.03, 25, 0);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            ClockDisciplinedTimelineResult result = default;
            for (var i = 0; i < 100000; i++)
            {
                samples += 20;
                result = AdvanceClock(timeline, origin, originTick, samples, 20, 2000.03, 25, 0);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated <= 256,
                $"自适应时钟热路径发生持续分配：{allocated} bytes");
            Assert(result.ClockState == ClockState.Locked && !result.RequiresRecovery,
                "稳态无分配测试中的时钟状态异常");
        }

        private static ClockDisciplinedTimelineResult AdvanceClock(
            ClockDisciplinedSampleTimeline timeline,
            DateTime origin,
            long originTick,
            long totalSamples,
            int sampleCount,
            double actualSampleRateHz,
            double baseLatencyMs,
            double jitterMs)
        {
            var callbackSeconds = totalSamples / actualSampleRateHz +
                                  (baseLatencyMs + jitterMs) / 1000.0;
            var callbackTick = originTick + (long)Math.Round(
                callbackSeconds * Stopwatch.Frequency,
                MidpointRounding.AwayFromZero);
            var callbackUtc = origin.AddTicks((long)Math.Round(
                callbackSeconds * TimeSpan.TicksPerSecond,
                MidpointRounding.AwayFromZero));
            return timeline.Advance(sampleCount, callbackUtc, callbackTick);
        }

        private static void LegacyTimelineFlagIsDiagnosticOnly()
        {
            var sample = new FastEpbCurrentSample(
                4,
                12.5,
                12.5,
                DateTime.UtcNow,
                Stopwatch.GetTimestamp(),
                3,
                100,
                FastSignalQualityFlags.TimelineFuture);
            Assert(sample.IsControlUsable,
                "兼容保留的TimelineFuture数值仍错误阻断控制数据");
            Assert(!FastPathTripClassifier.HasInvalidControlQuality(
                    FastSignalQualityFlags.TimelineFuture),
                "过流归因仍把归档时间偏差当作电流信号无效");
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
            // 控制环必须持有自己的尾点副本。后台取得 raw 所有权并原地换算后，
            // 不得反向污染已发布的快速证据。
            raw[0, 19] = 19.0;
            var destination = new FastControlSampleValue[1];
            var rawTail = new double[ControlBatchRing.RawTailCapacity];
            Assert(ring.TryDequeue(destination, rawTail, out var count, out var rawCount, out var actual),
                "结构化控制批出环失败");
            Assert(count == 1 && rawCount == 20 && actual.Generation == 7 &&
                   actual.SourceSequence == 123 && actual.CaptureMonotonicTicks == 1000 &&
                   Math.Abs(rawTail[19] - 1.9) < 1e-12,
                "控制批身份、单调时钟或原始尾部损坏");
        }

        private static void RawBatchOwnershipTransfersAfterControlCopy()
        {
            var type = typeof(TwoDeviceAiAcquirer);
            var callback = type.GetMethod("OnAiBatch", BindingFlags.Instance | BindingFlags.NonPublic);
            var enqueueControl = type.GetMethod("EnqueueForControl", BindingFlags.Instance | BindingFlags.NonPublic);
            var enqueueProcessing = type.GetMethod(
                "EnqueueForProcessing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert(callback != null && enqueueControl != null && enqueueProcessing != null,
                "无法读取DAQ原始批次所有权方法");

            var il = callback.GetMethodBody()?.GetILAsByteArray();
            Assert(il != null && il.Length > 0, "OnAiBatch没有可审计的IL");
            var controlOffset = FindMetadataTokenOffset(il, enqueueControl.MetadataToken);
            var processingOffset = FindMetadataTokenOffset(il, enqueueProcessing.MetadataToken);
            Assert(controlOffset >= 0 && processingOffset >= 0,
                "OnAiBatch未同时调用控制环复制和后台发布");
            Assert(controlOffset < processingOffset,
                "后台在控制环复制完成前取得raw引用，可能触发电压到安培二次换算");

            // 回放 EPB5 的典型批次：-0.7515V 只允许按 10A/V 换算一次。
            const double voltage = -0.7515;
            const double scale = 10.0;
            const double intercept = -0.02411;
            var once = voltage * scale + intercept;
            var twice = once * scale + intercept;
            Assert(Math.Abs(once + 7.53911) < 1e-9 && Math.Abs(twice + 75.41521) < 1e-9,
                "现场二次换算回放基准错误");
        }

        private static int FindMetadataTokenOffset(byte[] il, int metadataToken)
        {
            var token = BitConverter.GetBytes(metadataToken);
            for (var offset = 1; offset <= il.Length - token.Length; offset++)
            {
                var matches = true;
                for (var index = 0; index < token.Length; index++)
                {
                    if (il[offset + index] == token[index]) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                // call(0x28) 和 callvirt(0x6f) 的四字节操作数均为方法元数据令牌。
                if (il[offset - 1] == 0x28 || il[offset - 1] == 0x6f) return offset;
            }
            return -1;
        }

        private static void RawBatchOwnershipStressForBothDevices()
        {
            ExerciseRawOwnershipTransfer("Dev1", 5, -0.7515, -0.02411);
            ExerciseRawOwnershipTransfer("Dev2", 11, -0.485, -0.02123);
        }

        private static void ExerciseRawOwnershipTransfer(
            string device,
            int channel,
            double baseVoltage,
            double intercept)
        {
            const int batchCount = 100000;
            const double scale = 10.0;
            var raw = new double[1, ControlBatchRing.RawTailCapacity];
            var ring = new ControlBatchRing(2, 1);
            var samples = new FastControlSampleValue[1];
            var destination = new FastControlSampleValue[1];
            var rawTail = new double[ControlBatchRing.RawTailCapacity];
            var barrier = new Barrier(2);
            var copyFailure = string.Empty;

            // 后台只能在第一个栅栏之后取得 raw 所有权，并在第二个栅栏前完成原地换算。
            // 主线程在转移前完成代表值计算和 RawTail 深复制，形成确定性竞态回归。
            var processingThread = new Thread(() =>
            {
                for (var batch = 0; batch < batchCount; batch++)
                {
                    barrier.SignalAndWait();
                    for (var column = 0; column < raw.GetLength(1); column++)
                        raw[0, column] = raw[0, column] * scale + intercept;
                    barrier.SignalAndWait();
                }
            }) { IsBackground = true, Name = device + "-RawOwnershipTest" };
            processingThread.Start();

            for (var batch = 0; batch < batchCount; batch++)
            {
                var voltage = baseVoltage + ((batch % 7) - 3) * 0.0001;
                for (var column = 0; column < raw.GetLength(1); column++)
                    raw[0, column] = voltage;
                var representative = voltage * scale + intercept;
                samples[0] = new FastControlSampleValue(0, channel, 0, representative);
                var tick = batch + 1L;
                var metadata = new FastControlBatchMetadata(
                    2,
                    batch + 1L,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    tick,
                    tick,
                    0,
                    Thread.CurrentThread.ManagedThreadId,
                    FastSignalQualityFlags.None);

                if (!ring.TryEnqueue(metadata, samples, raw) && copyFailure.Length == 0)
                    copyFailure = device + " 控制批在所有权转移前入环失败";

                barrier.SignalAndWait();
                barrier.SignalAndWait();

                if (!ring.TryDequeue(
                        destination,
                        rawTail,
                        out var count,
                        out var rawCount,
                        out var actual))
                {
                    if (copyFailure.Length == 0) copyFailure = device + " 控制批出环失败";
                    continue;
                }

                if (copyFailure.Length == 0 &&
                    (count != 1 || rawCount != ControlBatchRing.RawTailCapacity ||
                     actual.SourceSequence != batch + 1L ||
                     Math.Abs(destination[0].RepresentativeA - representative) > 1e-12 ||
                     Math.Abs(rawTail[ControlBatchRing.RawTailCapacity - 1] - voltage) > 1e-12))
                    copyFailure = device + " 后台原地换算污染了快速代表值或原始尾点";
            }

            processingThread.Join();
            barrier.Dispose();
            Assert(copyFailure.Length == 0, copyFailure);
        }

        private static void FieldIncidentReplayStaysBelowOverCurrent()
        {
            ReplayCorrectedIncidentSequence(
                "EPB5_current",
                -0.02411,
                new[]
                {
                    -7.515635319637731, -7.596068084299695, -75.1804631963773,
                    -7.4094640702839385, -72.12401813922267, -72.22053745681703,
                    -68.52063028236668, -69.45365035244546, -6.84643471765019,
                    -6.688786498912741, -6.547224833107684, -6.653396082461477
                });
            ReplayCorrectedIncidentSequence(
                "EPB11_current",
                -0.02123,
                new[]
                {
                    -48.5411549511214, -4.977261573819928, -4.810236135542877,
                    -4.678543001516741, -4.858416550430488, -0.04679911698775366,
                    0.8598304469755665, 0.08489401703838259
                });
        }

        private static void ReplayCorrectedIncidentSequence(
            string channelKey,
            double intercept,
            double[] observedRepresentatives)
        {
            const double scale = 10.0;
            const double overCurrentLimitA = 18.0;
            var filter = new ClsDataFilter.FastFilter(9, 0.4, 0);
            var machine = new EpbAdaptiveCurrentStateMachine(new EpbAdaptiveProfile());
            var start = Stopwatch.Frequency;
            machine.ArmForward(start, 1000, 10000, 15, 0, 3);
            var maxRepresentative = 0.0;
            var maxFiltered = 0.0;
            var overCurrentFault = false;

            for (var index = 0; index < observedRepresentatives.Length; index++)
            {
                var observed = observedRepresentatives[index];
                // 现场绝对值超过 18A 的代表值具有二次换算指纹；逆推一次得到真实工程值，
                // 再还原成回调应独占的原始电压，走修复后的单次换算路径。
                var expectedOnce = Math.Abs(observed) >= overCurrentLimitA
                    ? (observed - intercept) / scale
                    : observed;
                var rawVoltage = (expectedOnce - intercept) / scale;
                var representative = rawVoltage * scale + intercept;
                var tick = start + (index + 1L) * Stopwatch.Frequency / 100;
                var filtered = filter.Update(channelKey, representative, tick);
                var decision = machine.OnSample(tick, filtered, Math.Abs(representative));
                maxRepresentative = Math.Max(maxRepresentative, Math.Abs(representative));
                maxFiltered = Math.Max(maxFiltered, Math.Abs(filtered));
                if (decision.Reason?.IndexOf(
                        "OverCurrent3Samples",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    overCurrentFault = true;

                Assert(Math.Abs(representative - expectedOnce) < 1e-10,
                    channelKey + " 现场批次未严格执行一次换算");
            }

            Assert(maxRepresentative < overCurrentLimitA && maxFiltered < overCurrentLimitA,
                channelKey + " 修复后仍产生39A、55A或75A二次换算峰值");
            Assert(!overCurrentFault, channelKey + " 修复后的现场序列仍触发OverCurrent3Samples");
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
