using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Controller;
using DataOperation;
using IO.NI;

namespace AdaptiveControlTests
{
    internal static class DaqPersistenceCoordinatorTests
    {
        internal static void PauseAndRecoverAfterLowWater()
        {
            var recorder = new BlockingRecorder(150);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 2, 1, 100, 100, 2000, 2);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;
            coordinator.Enqueue(NewBatch("Dev1", 1));
            coordinator.Enqueue(NewBatch("Dev1", 2));
            coordinator.Enqueue(NewBatch("Dev1", 3));
            WaitUntil(() => states.Any(x => x.State == DaqPersistenceState.Paused), 2000,
                "持久化积压未进入安全暂停");
            var paused = states.First(x => x.State == DaqPersistenceState.Paused);
            coordinator.SuppressAfter("Dev1", DateTime.UtcNow.AddSeconds(-1), paused.CorrelationId);
            WaitUntil(() => recorder.WriteCount >= 3, 3000,
                "持久化队列未排空");
            coordinator.Enqueue(NewBatch("Dev1", 4));
            coordinator.Enqueue(NewBatch("Dev1", 5));
            WaitUntil(() => states.Any(x => x.State == DaqPersistenceState.Recovered), 2000,
                "低水位和连续新鲜批次满足后未自动恢复");
        }

        internal static void HardCapacityKeepsRealFaultCode()
        {
            var recorder = new BlockingRecorder(500);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                2, 1, 0, 1000, 100, 2000, 1);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;
            var producer = Task.Run(() =>
            {
                for (var sequence = 1; sequence <= 8; sequence++)
                    Assert(coordinator.Enqueue(NewBatch("Dev1", sequence)),
                        $"容量背压期间批次{sequence}被丢弃");
            });
            WaitUntil(
                () => states.Any(x => x.State == DaqPersistenceState.Failed && x.Code == "DaqPersistenceQueueFull"),
                2000,
                "持久化硬容量故障未使用独立 DaqPersistenceQueueFull 分类");
            Assert(producer.Wait(8000), "容量恢复后生产者仍未解除背压");
            WaitUntil(() => Volatile.Read(ref recorder.WriteCount) >= 8, 3000,
                "容量恢复后未把全部八批按FIFO写入");
            Assert(states.Count(x =>
                       x.State == DaqPersistenceState.Failed &&
                       x.Code == "DaqPersistenceQueueFull") == 1,
                "同一次持久化容量满重复发布 Failed，可能造成 UI/恢复任务风暴");
            var snapshot = coordinator.GetSnapshot("Dev1");
            Assert(snapshot.OverCapacityDroppedBatchCount == 0 &&
                   snapshot.DiscardedGenerationBatchCount == 0,
                "容量满背压仍发生了已接收批次丢弃");
        }

        internal static void GenerationChangePreservesAcceptedFifo()
        {
            var recorder = new GatedFailureRecorder();
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 6, 4, 1000, 100, 3000, 1);

            Assert(coordinator.Enqueue(NewBatch("Dev1", 1, 1)), "旧代次首批未入队");
            Assert(coordinator.Enqueue(NewBatch("Dev1", 2, 1)), "旧代次第二批未入队");
            Thread.Sleep(100);
            coordinator.AcceptGeneration("Dev1", 2);
            Assert(coordinator.Enqueue(NewBatch("Dev1", 3, 2)), "新代次批次未入队");

            recorder.AllowWrites();
            WaitUntil(() => Volatile.Read(ref recorder.SuccessCount) >= 3, 5000,
                "代次切换后旧代次已接收批次未全部真实补写");
            var snapshot = coordinator.GetSnapshot("Dev1");
            Assert(snapshot.Sequence >= 3 && snapshot.DiscardedGenerationBatchCount == 0,
                "代次切换伪装推进持久化边界或丢弃旧批次");
        }

        internal static void DiskPauseMatrixRemainsBoundedAndRecovers()
        {
            foreach (var delayMs in new[] { 50, 100, 500, 1500 })
            {
                var recorder = new BlockingRecorder(delayMs);
                using var coordinator = new DaqPersistenceCoordinator(
                    () => recorder,
                    Config.NullLogger.Instance,
                    8, 2, 1, 100, 50, 8000, 1);
                var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
                coordinator.StateChanged += states.Enqueue;
                var producer = Stopwatch.StartNew();
                for (var sequence = 1; sequence <= 3; sequence++)
                    Assert(coordinator.Enqueue(NewBatch("Dev1", sequence)),
                        $"磁盘暂停{delayMs}ms时批次{sequence}被拒绝");
                producer.Stop();
                Assert(producer.ElapsedMilliseconds < 100,
                    $"磁盘暂停{delayMs}ms反向阻塞生产线程{producer.ElapsedMilliseconds}ms");
                WaitUntil(
                    () => Volatile.Read(ref recorder.WriteCount) >= 3,
                    delayMs * 3 + 3000,
                    $"磁盘暂停{delayMs}ms后队列未排空");
                Assert(!states.Any(state => state.State == DaqPersistenceState.Failed),
                    $"磁盘暂停{delayMs}ms被错误升级为持久化失败");
                Assert(states.Count(state => state.State == DaqPersistenceState.Paused) == 1,
                    $"磁盘暂停{delayMs}ms没有且仅有一次安全暂停");

                Assert(coordinator.Enqueue(NewBatch("Dev1", 4)),
                    $"磁盘暂停{delayMs}ms排空后新鲜批次被拒绝");
                WaitUntil(
                    () => states.Any(state => state.State == DaqPersistenceState.Recovered),
                    delayMs + 3000,
                    $"磁盘暂停{delayMs}ms排空后未恢复");
                Assert(coordinator.DrainAsync(delayMs + 3000).GetAwaiter().GetResult(),
                    $"磁盘暂停{delayMs}ms恢复后仍未排空");
            }
        }

        internal static void DiagnosticObserverFailureDoesNotRetryWrite()
        {
            var recorder = new BlockingRecorder(0);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8,
                4,
                1,
                1000,
                100,
                2000,
                1,
                persistenceTiming: (_, _, _, _, _, _) =>
                    throw new InvalidOperationException("diagnostic failed"));

            coordinator.Enqueue(NewBatch("Dev1", 1));
            WaitUntil(() => Volatile.Read(ref recorder.WriteCount) >= 1, 2000,
                "诊断观察者异常后数据没有完成写盘");
            Thread.Sleep(300);
            Assert(Volatile.Read(ref recorder.WriteCount) == 1,
                "诊断观察者异常被误判为写盘失败并重复写入");
        }

        internal static void MappingFailureRecoversBeforeSafetyPause()
        {
            var recorder = new RecoverableMappingRecorder();
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 4, 1, 1000, 100, 2000, 1);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;

            coordinator.Enqueue(NewBatch("Dev1", 1));
            WaitUntil(() => Volatile.Read(ref recorder.SuccessCount) == 1, 2000,
                "映射故障自愈后当前批次未重试成功");
            Thread.Sleep(100);
            Assert(recorder.WriteAttempts == 2, "映射故障没有且仅有一次重试");
            Assert(recorder.RecoveryCount == 1, "写盘器进程内自愈次数错误");
            Assert(!states.Any(state =>
                    state.State == DaqPersistenceState.Paused ||
                    state.State == DaqPersistenceState.Failed),
                "可恢复映射故障误触发了DAQ停机链");
        }

        internal static void RecoveryTimeoutRetainsBatchUntilStorageReturns()
        {
            var recorder = new GatedFailureRecorder();
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 6, 4, 1000, 1000, 1000, 1);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;

            Assert(coordinator.Enqueue(NewBatch("Dev1", 1)), "首批未进入持久化队列");
            Assert(coordinator.Enqueue(NewBatch("Dev1", 2)), "第二批未进入持久化队列");
            WaitUntil(
                () => states.Any(state =>
                    state.State == DaqPersistenceState.Failed &&
                    state.Code == "DaqPersistenceRecoveryTimeout"),
                3000,
                "超过恢复时限后未发布持久化失败状态");

            var timedOut = states.First(state =>
                state.State == DaqPersistenceState.Failed &&
                state.Code == "DaqPersistenceRecoveryTimeout");
            Assert(timedOut.Device == "Dev1", "失败状态设备号被已释放批次污染");
            var failedSnapshot = coordinator.GetSnapshot("Dev1");
            Assert(failedSnapshot.State == DaqPersistenceState.Failed,
                "超时失败没有保留在结构化持久化状态中");
            Assert(failedSnapshot.Sequence == 0,
                $"未写成功的首批被伪装成已持久化：Sequence={failedSnapshot.Sequence}");
            Assert(!coordinator.WaitForPersistedAsync("Dev1", 1, 50, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "未写成功的首批错误越过持久化等待边界");

            // 模拟截止后新批次被明确抑制：其较大序号可以先推进处理序号，但不能越过
            // 前方仍失败/排队的真实写入。封圈必须使用 DurableBoundary 而不是只看序号。
            coordinator.SuppressAfter(
                "Dev1",
                DateTime.UtcNow.AddSeconds(-1),
                timedOut.CorrelationId);
            Assert(coordinator.Enqueue(NewBatch("Dev1", 3)), "截止后抑制批次未被处理");
            Assert(coordinator.GetSnapshot("Dev1").Sequence == 3,
                "抑制批次没有模拟出处理序号先行场景");
            Assert(!coordinator.WaitForDurableBoundaryAsync(
                    "Dev1", 3, 50, CancellationToken.None).GetAwaiter().GetResult(),
                "仅处理序号先行时错误允许截止圈封存");

            recorder.AllowWrites();
            WaitUntil(() => Volatile.Read(ref recorder.SuccessCount) == 2, 3000,
                "存储恢复后未按 FIFO 补写两个保留批次");
            Assert(coordinator.WaitForPersistedAsync("Dev1", 2, 2000, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "补写完成后持久化边界未推进到第二批");
            Assert(coordinator.WaitForDurableBoundaryAsync(
                    "Dev1", 3, 2000, CancellationToken.None).GetAwaiter().GetResult(),
                "原批次和排队批次未全部补写，不能进入封圈边界");
            WaitUntil(
                () => coordinator.GetSnapshot("Dev1").State == DaqPersistenceState.Recovered,
                2000,
                "原批次补写成功后持久化状态未恢复");
            Assert(states.Any(state =>
                       state.State == DaqPersistenceState.Recovered &&
                       state.Device == "Dev1"),
                "恢复事件设备号被已释放批次污染");
            Assert(states.Count(state =>
                       state.State == DaqPersistenceState.Failed &&
                       state.Code == "DaqPersistenceRecoveryTimeout") == 1,
                "持续失败期间重复发布超时事件造成恢复风暴");

            var neverRecovers = new GatedFailureRecorder();
            var canceled = new DaqPersistenceCoordinator(
                () => neverRecovers,
                Config.NullLogger.Instance,
                8, 6, 4, 1000, 1000, 1000, 1);
            try
            {
                var canceledStates = new ConcurrentQueue<DaqPersistenceStateChanged>();
                canceled.StateChanged += canceledStates.Enqueue;
                Assert(canceled.Enqueue(NewBatch("Dev2", 9)), "取消场景批次未进入队列");
                WaitUntil(
                    () => canceledStates.Any(state =>
                        state.State == DaqPersistenceState.Failed &&
                        state.Code == "DaqPersistenceRecoveryTimeout"),
                    3000,
                    "取消场景未到达持久化超时状态");
                Assert(!canceled.ShutdownAsync(50).GetAwaiter().GetResult(),
                    "未排空的持久化批次错误允许关闭worker");
                Assert(canceled.GetSnapshot("Dev2").Sequence == 0,
                    "关闭超时后仍未写入的批次被伪装成已持久化");

                neverRecovers.AllowWrites();
                WaitUntil(() => Volatile.Read(ref neverRecovers.SuccessCount) == 1, 3000,
                    "关闭超时后writer没有保持存活并补写原批次");
                Assert(canceled.Enqueue(NewBatch("Dev2", 10)),
                    "关闭超时后writer被提前Dispose，无法接纳恢复验证批次");
                WaitUntil(() => Volatile.Read(ref neverRecovers.SuccessCount) == 2, 3000,
                    "关闭超时后的恢复验证批次未写入");
                Assert(canceled.ShutdownAsync(2000).GetAwaiter().GetResult(),
                    "数据补写完成后持久化worker仍无法正常关闭");
            }
            finally
            {
                canceled.Dispose();
            }
        }

        internal static void DurablePrefixAllowsHealthyLaterTrafficButRejectsSuppression()
        {
            var recorder = new PrefixGateRecorder(3);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 6, 4, 1000, 100, 2000, 1);

            Assert(coordinator.Enqueue(NewBatch("Dev1", 1)), "前缀批次1未入队");
            Assert(coordinator.Enqueue(NewBatch("Dev1", 2)), "前缀批次2未入队");
            Assert(coordinator.Enqueue(NewBatch("Dev1", 3)), "后续健康批次3未入队");
            WaitUntil(() => Volatile.Read(ref recorder.StartCount) >= 3, 2000,
                "第三批未进入在途门控");

            Assert(coordinator.WaitForDurablePrefixAsync(
                    "Dev1", 2, 500, CancellationToken.None).GetAwaiter().GetResult(),
                "前两批已写入时被后续更高序号在途批次错误阻塞");
            Assert(!coordinator.WaitForDurablePrefixAsync(
                    "Dev1", 3, 50, CancellationToken.None).GetAwaiter().GetResult(),
                "目标批次仍在途时耐久前缀错误放行");

            recorder.Release();
            Assert(coordinator.WaitForDurablePrefixAsync(
                    "Dev1", 3, 2000, CancellationToken.None).GetAwaiter().GetResult(),
                "第三批写入后耐久前缀未放行");

            coordinator.SuppressAfter("Dev1", DateTime.UtcNow, Guid.NewGuid());
            Assert(!coordinator.WaitForDurablePrefixAsync(
                    "Dev1", 3, 50, CancellationToken.None).GetAwaiter().GetResult(),
                "DAQ准入抑制期间错误复用通道级耐久前缀门禁");
        }

        internal static void DiskWriterUsesLazyTransactionalViewsAndCanResetThem()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-mapping-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new DataRetentionPolicy
                {
                    DataStorePath = root,
                    IndexAndExportPath = root,
                    FileSizeMb = 20,
                    RetainAllData = true
                };
                using (var writer = new EpbDiskWriter(policy))
                {
                    var viewsField = typeof(EpbDiskWriter).GetField(
                        "_views", BindingFlags.Instance | BindingFlags.NonPublic);
                    var mmfsField = typeof(EpbDiskWriter).GetField(
                        "_mmfs", BindingFlags.Instance | BindingFlags.NonPublic);
                    var statesField = typeof(EpbDiskWriter).GetField(
                        "_states", BindingFlags.Instance | BindingFlags.NonPublic);
                    var lengthsField = typeof(EpbDiskWriter).GetField(
                        "_viewLengths", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert(viewsField != null && mmfsField != null && statesField != null && lengthsField != null,
                        "无法检查写盘映射内部状态");

                    var views = (Array)viewsField.GetValue(writer);
                    Assert(Enumerable.Range(1, 12).All(ch => views.GetValue(ch) == null),
                        "写盘器构造阶段仍预映射了未启用通道");

                    writer.StartFreeRun(1);
                    var timestamp = new[] { DateTime.UtcNow };
                    var current = new[] { 1.0 };
                    var pressure = new[] { 2.0 };
                    writer.WriteBatch(1, timestamp, current, pressure);
                    views = (Array)viewsField.GetValue(writer);
                    Assert(views.GetValue(1) != null, "活动通道未延迟创建映射视图");
                    Assert(Enumerable.Range(2, 11).All(ch => views.GetValue(ch) == null),
                        "单通道写入意外映射了其他通道");
                    var lengths = (long[])lengthsField.GetValue(writer);
                    Assert(lengths[1] <= 8L * 1024 * 1024,
                        $"短视图超过8MB上限：{lengths[1]}");

                    Assert(writer.TryRecoverStorageMappings(
                            new OutOfMemoryException("模拟x86地址空间不足"),
                            out var detail),
                        "地址空间故障未被识别为可恢复映射故障");
                    Assert(!string.IsNullOrWhiteSpace(detail), "映射自愈未返回诊断摘要");
                    views = (Array)viewsField.GetValue(writer);
                    Assert(Enumerable.Range(1, 12).All(ch => views.GetValue(ch) == null),
                        "映射自愈未释放全部短视图");
                    writer.WriteBatch(1, timestamp, current, pressure);
                    Assert(((Array)viewsField.GetValue(writer)).GetValue(1) != null,
                        "映射自愈后下一批未按需重建视图");

                    // 强制下一次重映射失败，验证新视图创建失败不会先关闭旧访问器。
                    var states = (Array)statesField.GetValue(writer);
                    var state = states.GetValue(1);
                    var totalWrittenField = state.GetType().GetField(
                        "TotalWritten", BindingFlags.Instance | BindingFlags.Public);
                    Assert(totalWrittenField != null, "无法设置环形写入位置");
                    var mmfs = (Array)mmfsField.GetValue(writer);
                    ((IDisposable)mmfs.GetValue(1)).Dispose();
                    totalWrittenField.SetValue(state, 9L * 1024 * 1024 / 32);
                    var remapFailed = false;
                    try { writer.WriteBatch(1, timestamp, current, pressure); }
                    catch (ObjectDisposedException) { remapFailed = true; }
                    Assert(remapFailed, "模拟重映射失败未生效");
                    totalWrittenField.SetValue(state, 0L);
                    writer.WriteBatch(1, timestamp, current, pressure);
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        internal static void DiskWriterClosesInterruptedRunningCyclesOnStartup()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-startup-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new DataRetentionPolicy
                {
                    DataStorePath = root,
                    IndexAndExportPath = root,
                    FileSizeMb = 1,
                    RetainAllData = true
                };
                var startedUtc = DateTime.UtcNow.AddHours(-3);
                using (var first = new EpbDiskWriter(policy))
                {
                    first.BeginCycle(4, 101, startedUtc);
                    first.WriteBatch(
                        4,
                        new[] { startedUtc },
                        new[] { 1.25 },
                        new[] { 70.0 });
                    // 模拟上一进程未走 Complete/Abort 即结束。
                }

                using (var recovered = new EpbDiskWriter(policy))
                {
                    var connectionField = typeof(EpbDiskWriter).GetField(
                        "_conn", BindingFlags.Instance | BindingFlags.NonPublic);
                    var connection = connectionField?.GetValue(recovered) as IDbConnection;
                    Assert(connection != null, "无法读取恢复后的SQLite连接");
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT status FROM epb_cycles WHERE epb_id=4 AND cycle_number=101;";
                    Assert(
                        string.Equals(
                            Convert.ToString(command.ExecuteScalar()),
                            "aborted_on_startup",
                            StringComparison.OrdinalIgnoreCase),
                        "新进程未将历史running圈收口为aborted_on_startup");
                }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        internal static void ActiveCycleLimitPublishesLifecycleIdentity()
        {
            const int epbId = 8;
            const int cycle = 506;
            const int limit = 120000;
            var recorder = new ActiveCycleLimitRecorder(epbId, cycle, limit);
            using var coordinator = new DaqPersistenceCoordinator(
                () => recorder,
                Config.NullLogger.Instance,
                8, 4, 1, 1000, 100, 2000, 1);
            var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
            coordinator.StateChanged += states.Enqueue;

            coordinator.Enqueue(NewBatch("Dev2", 506));
            WaitUntil(
                () => states.Any(state =>
                    state.State == DaqPersistenceState.Failed &&
                    state.Code == "ActiveCycleDataLimitExceeded"),
                2000,
                "活动圈上限没有发布独立生命周期故障");
            var failed = states.First(state =>
                state.State == DaqPersistenceState.Failed &&
                state.Code == "ActiveCycleDataLimitExceeded");
            Assert(failed.EpbId == epbId, "活动圈上限事件丢失EPB标识");
            Assert(failed.CycleNumber == cycle, "活动圈上限事件丢失圈号");
            Assert(failed.RecordLimit == limit, "活动圈上限事件丢失样本限制");
        }

        internal static void SixChannelRealtimePersistenceStaysAhead()
        {
            SixChannelRealtimePersistenceStaysAhead(10);
        }

        internal static void SixChannelRealtimePersistenceStaysAhead(int wallClockSeconds)
        {
            const int sampleRateHz = 2000;
            const int samplesPerBatch = 20;
            const int batchIntervalMs = 10;
            if (wallClockSeconds < 1 || wallClockSeconds > 3600)
                throw new ArgumentOutOfRangeException(
                    nameof(wallClockSeconds),
                    "负载测试时长必须在1到3600秒之间。");
            var batchCount = wallClockSeconds * 1000 / batchIntervalMs;
            var expectedSamplesPerChannel = sampleRateHz * wallClockSeconds;
            var channels = new[] { 4, 5, 8, 9, 10, 11 };
            var root = Path.Combine(
                Path.GetTempPath(),
                "epb-six-channel-persistence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new DataRetentionPolicy
                {
                    DataStorePath = root,
                    IndexAndExportPath = root,
                    FileSizeMb = 1,
                    RetainAllData = true,
                    MaxActiveCycleRecords = 37500
                };
                using (var writer = new EpbDiskWriter(policy))
                {
                    var recorder = new DiskWriterRecorderAdapter(writer);
                    var startUtc = DateTime.UtcNow;
                    const int cycleSeconds = 15;
                    var batchesPerCycle = cycleSeconds * 1000 / batchIntervalMs;
                    var currentCycle = 1;
                    var currentCycleSamples = 0;
                    foreach (var channel in channels)
                        recorder.BeginCycle(channel, currentCycle, startUtc);

                    var states = new ConcurrentQueue<DaqPersistenceStateChanged>();
                    using (var coordinator = new DaqPersistenceCoordinator(
                               () => recorder,
                               Config.NullLogger.Instance,
                               256,
                               128,
                               16,
                               1000,
                               100,
                               5000,
                               3))
                    {
                        coordinator.StateChanged += states.Enqueue;
                        var maxDev1Depth = 0;
                        var maxDev2Depth = 0;
                        var dev1Channels = new[] { 4, 5 };
                        var dev2Channels = new[] { 8, 9, 10, 11 };
                        var producerClock = Stopwatch.StartNew();
                        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
                        {
                            if (batchIndex > 0 && batchIndex % batchesPerCycle == 0)
                            {
                                Assert(
                                    coordinator.DrainAsync(5000).GetAwaiter().GetResult(),
                                    $"第{currentCycle}圈封口前持久化队列未在5秒内排空");
                                var cycleEndUtc = startUtc.AddMilliseconds(
                                    batchIndex * batchIntervalMs);
                                foreach (var channel in channels)
                                {
                                    var actual = recorder.GetCurrentCycleSampleCount(channel);
                                    Assert(actual == currentCycleSamples,
                                        $"EPB{channel}第{currentCycle}圈样本数错误：" +
                                        $"Expected={currentCycleSamples} Actual={actual}");
                                    recorder.CompleteCycle(
                                        channel, currentCycle, actual, cycleEndUtc);
                                    recorder.BeginCycle(
                                        channel, currentCycle + 1, cycleEndUtc);
                                }
                                currentCycle++;
                                currentCycleSamples = 0;
                            }
                            var sampleOffset = batchIndex * samplesPerBatch;
                            Assert(
                                coordinator.Enqueue(NewLoadBatch(
                                    "Dev1",
                                    batchIndex + 1,
                                    startUtc,
                                    sampleOffset,
                                    samplesPerBatch,
                                    dev1Channels)),
                                "Dev1 六通道负载批次被拒绝");
                            Assert(
                                coordinator.Enqueue(NewLoadBatch(
                                    "Dev2",
                                    batchIndex + 1,
                                    startUtc,
                                    sampleOffset,
                                    samplesPerBatch,
                                    dev2Channels)),
                                "Dev2 六通道负载批次被拒绝");
                            currentCycleSamples += samplesPerBatch;

                            maxDev1Depth = Math.Max(
                                maxDev1Depth,
                                coordinator.GetSnapshot("Dev1").QueueDepth);
                            maxDev2Depth = Math.Max(
                                maxDev2Depth,
                                coordinator.GetSnapshot("Dev2").QueueDepth);

                            var targetMs = (batchIndex + 1L) * batchIntervalMs;
                            while (producerClock.ElapsedMilliseconds < targetMs)
                            {
                                var remaining = targetMs - producerClock.ElapsedMilliseconds;
                                if (remaining > 1) Thread.Sleep(1);
                                else Thread.SpinWait(100);
                            }
                        }

                        var producerElapsedMs = producerClock.Elapsed.TotalMilliseconds;
                        var drainClock = Stopwatch.StartNew();
                        Assert(
                            coordinator.DrainAsync(5000).GetAwaiter().GetResult(),
                            "六通道2kHz负载结束后5秒内仍未排空持久化队列");
                        drainClock.Stop();
                        var expectedWallClockMs = wallClockSeconds * 1000.0;
                        Assert(
                            producerElapsedMs < expectedWallClockMs + 5000,
                            $"{wallClockSeconds}秒实时生产阶段耗时异常：{producerElapsedMs:F1}ms");
                        Assert(
                            maxDev1Depth < 64 && maxDev2Depth < 64,
                            $"六通道实时负载未保持在64批控制线内：" +
                            $"Dev1={maxDev1Depth} Dev2={maxDev2Depth}");
                        Assert(
                            !states.Any(state =>
                                state.State == DaqPersistenceState.Paused ||
                                state.State == DaqPersistenceState.Failed),
                            "六通道实时负载触发了持久化暂停或失败：" +
                            string.Join(" | ", states
                                .Where(state =>
                                    state.State == DaqPersistenceState.Paused ||
                                    state.State == DaqPersistenceState.Failed)
                                .Select(state =>
                                    $"{state.Device}/{state.Code}/Depth={state.QueueDepth}/" +
                                    $"Age={state.OldestBatchAgeMs:F1}ms/{state.Reason}")));
                        var checkpointField = typeof(EpbDiskWriter).GetField(
                            "_progressCheckpointTransactionCount",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        var checkpointTransactions = Convert.ToInt64(
                            checkpointField?.GetValue(writer) ?? -1L);
                        var expectedCheckpointTransactions = wallClockSeconds * 2L;
                        Assert(
                            checkpointTransactions >= expectedCheckpointTransactions - 2 &&
                            checkpointTransactions <= expectedCheckpointTransactions + 2,
                            $"{wallClockSeconds}秒六通道运行的SQLite进度事务不在约2次/秒范围：" +
                            $"Actual={checkpointTransactions}");
                        Console.WriteLine(
                            $"METRIC SixChannelPersistence " +
                            $"Producer={producerElapsedMs:F1}ms Drain={drainClock.Elapsed.TotalMilliseconds:F1}ms " +
                            $"MaxDepthDev1={maxDev1Depth} MaxDepthDev2={maxDev2Depth} " +
                            $"ProgressTransactions={checkpointTransactions} " +
                            $"SamplesPerChannel={expectedSamplesPerChannel}");
                    }

                    var endUtc = startUtc.AddSeconds(wallClockSeconds);
                    foreach (var channel in channels)
                    {
                        var actual = recorder.GetCurrentCycleSampleCount(channel);
                        Assert(
                            actual == currentCycleSamples,
                            $"EPB{channel}末圈样本数错误：Expected={currentCycleSamples} Actual={actual}");
                        recorder.CompleteCycle(channel, currentCycle, actual, endUtc);
                        Assert(recorder.GetLastCycleNumber(channel) == currentCycle,
                            $"EPB{channel} 完成圈索引未提交");
                    }

                    var connectionField = typeof(EpbDiskWriter).GetField(
                        "_conn",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var connection = connectionField?.GetValue(writer) as IDbConnection;
                    Assert(connection != null, "无法取得SQLite连接执行完整性检查");
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA integrity_check;";
                        Assert(
                            string.Equals(
                                Convert.ToString(command.ExecuteScalar()),
                                "ok",
                                StringComparison.OrdinalIgnoreCase),
                            "六通道写盘后的SQLite integrity_check未通过");
                    }
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT COALESCE(SUM(sample_count), 0) FROM epb_cycles " +
                            "WHERE status='completed';";
                        Assert(
                            Convert.ToInt64(command.ExecuteScalar()) ==
                            (long)channels.Length * expectedSamplesPerChannel,
                            "六通道全部完成圈的总样本数未完整写入SQLite");
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch
                {
                    // 临时压力测试目录清理失败不掩盖原始测试结论。
                }
            }
        }

        private static DaqDiskBatch NewLoadBatch(
            string device,
            long sequence,
            DateTime startUtc,
            int sampleOffset,
            int sampleCount,
            int[] epbChannels)
        {
            var timestamps = ArrayPool<DateTime>.Shared.Rent(sampleCount);
            var pressure = ArrayPool<double>.Shared.Rent(sampleCount);
            var channels = ArrayPool<DaqDiskChannelBatch>.Shared.Rent(epbChannels.Length);
            for (var sample = 0; sample < sampleCount; sample++)
            {
                timestamps[sample] = startUtc.AddTicks((sampleOffset + sample) * 5000L);
                pressure[sample] = 50.0;
            }
            for (var channelIndex = 0; channelIndex < epbChannels.Length; channelIndex++)
            {
                var currents = ArrayPool<double>.Shared.Rent(sampleCount);
                for (var sample = 0; sample < sampleCount; sample++)
                    currents[sample] = 1.0 + channelIndex * 0.1;
                channels[channelIndex] = new DaqDiskChannelBatch(
                    epbChannels[channelIndex],
                    currents);
            }
            return DaqDiskBatch.Rent(
                device,
                1,
                sequence,
                sampleCount,
                timestamps,
                channels,
                epbChannels.Length,
                string.Equals(device, "Dev1", StringComparison.OrdinalIgnoreCase) ? pressure : null,
                string.Equals(device, "Dev2", StringComparison.OrdinalIgnoreCase) ? pressure : null,
                Stopwatch.GetTimestamp());
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static DaqDiskBatch NewBatch(string device, long sequence, long generation = 0)
        {
            var now = DateTime.UtcNow;
            var timestamps = ArrayPool<DateTime>.Shared.Rent(1);
            var currents = ArrayPool<double>.Shared.Rent(1);
            var pressures = ArrayPool<double>.Shared.Rent(1);
            timestamps[0] = now;
            currents[0] = 1;
            pressures[0] = 10;
            return new DaqDiskBatch(
                device,
                generation,
                sequence,
                1,
                timestamps,
                new[] { new DaqDiskChannelBatch(device == "Dev1" ? 4 : 8, currents) },
                device == "Dev1" ? pressures : null,
                device == "Dev2" ? pressures : null,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }

        private static void WaitUntil(Func<bool> predicate, int timeoutMs, string message)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                if (predicate()) return;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException(message);
        }

        private sealed class BlockingRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder
        {
            private readonly int _delayMs;
            internal BlockingRecorder(int delayMs) { _delayMs = delayMs; }
            internal int WriteCount;

            public void WriteDeviceBatch(DateTime[] timestampsUtc, IReadOnlyList<EpbChannelDiskBatch> channels, int count)
            {
                Thread.Sleep(_delayMs);
                Interlocked.Increment(ref WriteCount);
            }
            public void WriteBatch(int epbId, DateTime[] timestampsUtc, double[] currents, double[] pressures, int count)
                => Thread.Sleep(_delayMs);
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures)
                => Thread.Sleep(_delayMs);
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }

        private sealed class RecoverableMappingRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder, IRecoverableCycleRecorder
        {
            internal int WriteAttempts;
            internal int RecoveryCount;
            internal int SuccessCount;

            public void WriteDeviceBatch(DateTime[] timestampsUtc, IReadOnlyList<EpbChannelDiskBatch> channels, int count)
            {
                if (Interlocked.Increment(ref WriteAttempts) == 1)
                    throw new ObjectDisposedException("UnmanagedMemoryAccessor");
                Interlocked.Increment(ref SuccessCount);
            }

            public bool TryRecoverStorage(Exception cause, out string detail)
            {
                Interlocked.Increment(ref RecoveryCount);
                detail = "test mapping reset";
                return true;
            }

            public void WriteBatch(int epbId, DateTime[] timestampsUtc, double[] currents, double[] pressures, int count) { }
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures) { }
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }

        private sealed class GatedFailureRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder
        {
            private int _allowWrites;
            internal int SuccessCount;

            internal void AllowWrites() => Volatile.Write(ref _allowWrites, 1);

            public void WriteDeviceBatch(
                DateTime[] timestampsUtc,
                IReadOnlyList<EpbChannelDiskBatch> channels,
                int count)
            {
                if (Volatile.Read(ref _allowWrites) == 0)
                    throw new IOException("simulated storage unavailable");
                Interlocked.Increment(ref SuccessCount);
            }

            public void WriteBatch(int epbId, DateTime[] timestampsUtc, double[] currents, double[] pressures, int count) { }
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures) { }
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }

        private sealed class PrefixGateRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder
        {
            private readonly int _blockedWriteNumber;
            private readonly ManualResetEventSlim _release = new(false);
            internal int StartCount;

            internal PrefixGateRecorder(int blockedWriteNumber)
            {
                _blockedWriteNumber = blockedWriteNumber;
            }

            internal void Release() => _release.Set();

            public void WriteDeviceBatch(
                DateTime[] timestampsUtc,
                IReadOnlyList<EpbChannelDiskBatch> channels,
                int count)
            {
                var ordinal = Interlocked.Increment(ref StartCount);
                if (ordinal == _blockedWriteNumber && !_release.Wait(3000))
                    throw new TimeoutException("prefix test gate timeout");
            }

            public void WriteBatch(int epbId, DateTime[] timestampsUtc, double[] currents, double[] pressures, int count) { }
            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures) { }
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }

        private sealed class ActiveCycleLimitRecorder : IEpbCycleRecorder, IBatchedEpbCycleRecorder
        {
            private readonly int _epbId;
            private readonly int _cycle;
            private readonly int _limit;

            internal ActiveCycleLimitRecorder(int epbId, int cycle, int limit)
            {
                _epbId = epbId;
                _cycle = cycle;
                _limit = limit;
            }

            public void WriteDeviceBatch(
                DateTime[] timestampsUtc,
                IReadOnlyList<EpbChannelDiskBatch> channels,
                int count)
                => throw new ActiveCycleDataLimitExceededException(_epbId, _cycle, _limit);

            public void WriteBatch(
                int epbId,
                DateTime[] timestampsUtc,
                double[] currents,
                double[] pressures,
                int count)
                => throw new ActiveCycleDataLimitExceededException(_epbId, _cycle, _limit);

            public void WriteBatch(int epbId, DateTime[] tsUtc, double[] currents, double[] groupPressures)
                => throw new ActiveCycleDataLimitExceededException(_epbId, _cycle, _limit);
            public void SealCycleWindow(int epbId, int cycleNumber, DateTime endUtc) { }
            public void BeginCycle(int epbId, int cycleNumber, DateTime utcNow) { }
            public int BeginLearningCycle(int epbId, DateTime utcNow) => -1;
            public int GetCurrentCycleSampleCount(int epbId) => 0;
            public void CompleteCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public void AlarmCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow) { }
            public AlarmCycleSnapshotEvidence SealAndExportAlarmCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc) => new();
            public AlarmCycleSnapshotEvidence SealAndExportCycle(int epbId, int cycleNumber, string exportDir, DateTime fallbackEndUtc, string status) => new();
            public void AbortCycle(int epbId, int cycleNumber, int finalN, DateTime utcNow, string status) { }
            public void FlushRecent(int epbId, int lastNCycles) { }
            public int GetLastCycleNumber(int ch) => 0;
            public void FlushRecentTo(int epbId, int lastNCycles, string exportDir, bool includeRunningCycle) { }
        }
    }
}
