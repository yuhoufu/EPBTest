using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace EpbDiskWriterTests
{
    internal static partial class Program
    {
        private static int RunLearningTimingProbe()
        {
            var configuredLaps = Environment.GetEnvironmentVariable("EPB_LEARNING_PROBE_LAPS");
            var laps = int.TryParse(configuredLaps, out var requestedLaps)
                ? Math.Max(1, Math.Min(120, requestedLaps)) : 6;
            // Isolated synthetic workload, never opens a field project or hardware.
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.FileSizeMb = 100;
                var timingGate = new object();
                var slow = new System.Collections.Generic.List<EpbWriteTiming>();
                long batches = 0;
                double maximum = 0;
                policy.WriteTimingSink = timing =>
                {
                    lock (timingGate)
                    {
                        if (timing.Operation == "DeviceBatch") batches++;
                        maximum = Math.Max(maximum, timing.TotalMs);
                        if (timing.TotalMs >= 50 && slow.Count < 200) slow.Add(timing);
                    }
                };
                using var writer = new EpbDiskWriter(policy);
                Task.WaitAll(new[] { new[] { 4, 5 }, new[] { 7, 8, 9, 12 } }.Select((channels, device) => Task.Run(() =>
                {
                    long sequence = 0;
                    for (var lap = 1; lap <= laps; lap++)
                    {
                        var start = DateTime.UtcNow;
                        foreach (var channel in channels) writer.BeginCycle(channel, -lap, start);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        for (var batch = 0; batch < 500; batch++)
                        {
                            var timestamps = Enumerable.Range(0, 20)
                                .Select(i => start.AddMilliseconds(batch * 10 + i * 0.5)).ToArray();
                            var values = channels.Select(ch => new EpbChannelDiskBatch(ch,
                                Enumerable.Repeat((double)ch, 20).ToArray(), new double[20])).ToArray();
                            writer.WriteDeviceBatch("Probe" + device, 1, ++sequence, timestamps, values, channels.Length, 20);
                            var wait = (batch + 1) * 10 - (int)clock.ElapsedMilliseconds;
                            if (wait > 0) Thread.Sleep(wait);
                        }
                        foreach (var channel in channels)
                        {
                            Assert(writer.GetCurrentCycleSampleCount(channel) == 10000, "学习探测样本数不一致");
                            var evidence = writer.SealAndExportCycle(channel, -lap,
                                Path.Combine(root, "learning", "EPB" + channel, "Cycle" + lap),
                                start.AddSeconds(5), "learning_completed");
                            Assert(evidence.IsValid && evidence.SampleCount == 10000,
                                "学习探测封存导出失败或样本边界改变");
                        }
                    }
                })).ToArray());
                Console.WriteLine(System.FormattableString.Invariant($"LearningProbe Batches={batches};MaxMs={maximum:F3};CapturedSlow={slow.Count}"));
                foreach (var t in slow.OrderByDescending(item => item.TotalMs).Take(20))
                    Console.WriteLine(System.FormattableString.Invariant(
                        $"Slow Operation={t.Operation};Device={t.Device};Sequence={t.Sequence};Total={t.TotalMs:F3};ChannelGate={t.ChannelGateWaitMs:F3};RawStage={t.RawStageMs:F3};RawAppend={t.RawAppendMs:F3};AppendGate={t.RawAppendGateWaitMs:F3};Remap={t.ViewRemapMs:F3};Write={t.RingWriteMs:F3};Checkpoint={t.CheckpointMs:F3};IndexCommit={t.IndexCommitMs:F3};Prune={t.RawPruneMs:F3}"));
            });
            return 0;
        }

        private static void RunV217PersistenceTests()
        {
            Run("学习慢导出不占用同设备写入锁且领取和快照保持唯一", SlowSealDoesNotBlockDeviceWriter);
            Run("关闭等待学习导出失败终态且不提前释放数据库", DisposeWaitsForSealFailure);
            Run("V3 已提交帧复用拒绝变更且失败后可重试", CommittedDeviceFrameRejectsMutation);
            Run("V3 慢环形刷盘不阻塞后续圈且日志重放保留数据", SlowRingFlushPreservesReplayAndProgress);
            Run("V3 环形刷盘失败保留日志并可恢复", FailedRingFlushRetainsEvidence);
            Run("V3 学习批次合并耐久事务保持各通道时间切片", LearningBatchStagesExactSlices);
            Run("I0046 多帧checkpoint按圈归并仍保留最大耐久前缀", CheckpointCoalescingPreservesPrefix);
            Run("I0046 写盘阶段诊断隔离且空批次幂等", StorageTimingCannotChangeDurability);
            Run("V217 原始日志修复被清零的环形记录且重放不重复计数", RawJournalReplaysExactPositions);
            Run("V217 断序异常圈终结且同批健康通道继续写入", SequenceGapDoesNotPoisonDeviceQueue);
            Run("V217 未应用日志容量不足保留原证据", RawJournalCapacityPreservesEvidence);
            Run("V217 历史基线加耐久回执且不把圈号当次数", MechanicalBaselineSurvivesCheckpointLag);
            Run("V217 非法数据耐久隔离且健康后继继续", InvalidBatchDoesNotPoisonQueue);
            Run("V217 已耐久未应用批次停止时重放且后圈不覆盖", StagedBatchIsAppliedBeforeAbort);
        }

        private static void SlowRingFlushPreservesReplayAndProgress()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var time = DateTime.UtcNow;
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var writer = new EpbDiskWriter(policy);
                Task next = null;
                try
                {
                    typeof(EpbDiskWriter).GetField("_rawCheckpointFlushTestHook",
                        BindingFlags.Instance | BindingFlags.NonPublic).SetValue(writer,
                        new Action<int>(_ => { entered.Set(); release.Wait(); }));
                    writer.BeginCycle(1, 100, time);
                    WriteSamples(writer, 1, 3, time);
                    writer.CompleteCycle(1, 100, 3, time.AddMilliseconds(3));
                    Assert(entered.Wait(3000), "后台刷盘未进入");
                    next = Task.Run(() =>
                    {
                        writer.BeginCycle(1, 101, time.AddSeconds(1));
                        WriteSamples(writer, 1, 3, time.AddSeconds(1));
                        writer.CompleteCycle(1, 101, 3, time.AddSeconds(1).AddMilliseconds(3));
                    });
                    Assert(next.Wait(3000), "环形刷盘占住后续写入/封圈");
                    writer.PersistLatestCyclesNow(1, 1, "delete");
                    Assert(Scalar(policy, "SELECT COUNT(*) FROM epb_cycles WHERE cycle_number=100") == 1,
                        "后台刷盘未完成即清理其圈索引");
                    using var db = new SQLiteConnection("Data Source=" +
                        Path.Combine(policy.IndexAndExportPath, "raw-journal.db") + ";Pooling=False;");
                    db.Open();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM raw_frames";
                    Assert(Convert.ToInt64(cmd.ExecuteScalar()) > 0, "刷盘未完成即删除恢复日志");
                }
                finally
                {
                    release.Set();
                    next?.GetAwaiter().GetResult();
                    writer.Dispose();
                }
                var path = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                    stream.Write(new byte[6 * SampleRecord.Size], 0, 6 * SampleRecord.Size);
                using var recovered = new EpbDiskWriter(policy);
                foreach (var cycle in new[] { 100, 101 })
                {
                    var export = Path.Combine(root, "async-replay-" + cycle);
                    recovered.ExportCycleAttemptTo(1, cycle, export, true, true);
                    AssertCsvCycle(export, 1, cycle, 3);
                }
            });
        }

        private static void SlowSealDoesNotBlockDeviceWriter()
        {
            WithRoot(root =>
            {
                using var writer = new EpbDiskWriter(NewPolicy(root));
                var start = DateTime.UtcNow;
                var sealingCycle = writer.BeginLearningCycle(4, start);
                writer.BeginLearningCycle(5, start);
                WriteSamples(writer, 4, 8, start);
                WriteSamples(writer, 5, 8, start);
                using var exporting = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                typeof(EpbDiskWriter).GetField("_sealExportTestHook", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(writer, (Action)(() =>
                    {
                        exporting.Set();
                        if (!release.Wait(10000)) throw new TimeoutException("Seal test release missing");
                    }));
                var seal = Task.Run(() => writer.SealAndExportCycle(4, sealingCycle,
                    Path.Combine(root, "slow-export"), start.AddSeconds(1), "learning_completed"));
                Task write = null;
                try
                {
                    Assert(exporting.Wait(5000), "导出没有进入阻塞点");
                    var timestamps = Enumerable.Range(0, 20).Select(i => start.AddSeconds(2).AddMilliseconds(i)).ToArray();
                    var channels = new[] { 4, 5 }.Select(ch => new EpbChannelDiskBatch(ch,
                        Enumerable.Repeat(1.0, 20).ToArray(), new double[20])).ToArray();
                    write = Task.Run(() => writer.WriteDeviceBatch("Dev1", 1, 1, timestamps, channels, 2, 20));
                    Assert(write.Wait(1000), "一个通道的文件导出阻塞了同设备健康通道写入");
                    Assert(writer.GetCurrentCycleSampleCount(4) == 8, "已领取的封存快照仍被追加");
                    Assert(writer.GetCurrentCycleSampleCount(5) == 28, "健康通道样本缺失");
                    var duplicate = writer.SealAndExportCycle(4, sealingCycle,
                        Path.Combine(root, "duplicate"), start, "learning_completed");
                    Assert(!duplicate.WasClaimed, "导出途中重复领取了圈");
                    Assert(writer.CaptureCommittedCycleProgress(4).SuccessfulCommits == 0,
                        "导出验证前发布了成功进度");
                }
                finally
                {
                    release.Set();
                    Task.WaitAll(new[] { seal, write ?? Task.CompletedTask });
                }
                Assert(seal.Result.IsValid && seal.Result.SampleCount == 8, "冻结快照封存不完整");
                Assert(writer.CaptureCommittedCycleProgress(4).SuccessfulCommits == 1,
                    "真实导出成功后没有唯一提交");
                Assert(writer.BeginLearningCycle(4, start.AddSeconds(3)) != sealingCycle,
                    "封存后不能开始新圈");
            });
        }

        private static void DisposeWaitsForSealFailure()
        {
            WithRoot(root =>
            {
                var writer = new EpbDiskWriter(NewPolicy(root));
                var start = DateTime.UtcNow;
                var cycle = writer.BeginLearningCycle(4, start);
                WriteSamples(writer, 4, 8, start);
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                typeof(EpbDiskWriter).GetField("_sealExportTestHook", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(writer, (Action)(() =>
                    {
                        entered.Set();
                        if (!release.Wait(10000)) throw new TimeoutException("Release missing");
                        throw new IOException("Injected export failure");
                    }));
                var seal = Task.Run(() => writer.SealAndExportCycle(4, cycle,
                    Path.Combine(root, "failed-export"), start.AddSeconds(1), "learning_completed"));
                Task dispose = null;
                try
                {
                    Assert(entered.Wait(5000), "导出未进入受控失败点");
                    dispose = Task.Run(() => writer.Dispose());
                    Assert(SpinWait.SpinUntil(() => (int)typeof(EpbDiskWriter)
                        .GetField("_disposeRequested", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(writer) == 1, 5000),
                        "关闭任务未开始");
                    Assert(!dispose.Wait(100), "导出仍在执行时数据库已被关闭");
                }
                finally
                {
                    release.Set();
                    Task.WaitAll(new[] { seal, dispose ?? Task.CompletedTask });
                    writer.Dispose();
                }
                Assert(!seal.Result.IsValid, "失败导出被记为成功");
                Assert(writer.CaptureCommittedCycleProgress(4).SuccessfulCommits == 0,
                    "失败导出发布了成功进度");
                using var reopened = new EpbDiskWriter(NewPolicy(root));
                Assert(Scalar(NewPolicy(root), $"SELECT COUNT(*) FROM epb_cycles WHERE epb_id=4 AND cycle_number={cycle} AND status='learning_failed'") == 1,
                    "关闭前没有提交失败终态");
            });
        }

        private static void FailedRingFlushRetainsEvidence()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var time = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    typeof(EpbDiskWriter).GetField("_rawCheckpointFlushTestHook",
                        BindingFlags.Instance | BindingFlags.NonPublic).SetValue(writer,
                        new Action<int>(_ => { throw new IOException("InjectedFlushFailure"); }));
                    writer.BeginCycle(1, 100, time);
                    WriteSamples(writer, 1, 3, time);
                    writer.CompleteCycle(1, 100, 3, time.AddMilliseconds(3));
                    var field = typeof(EpbDiskWriter).GetField("_pendingRawCheckpoints",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var pending = ((Array)field.GetValue(writer)).GetValue(1);
                    var flush = (Task)pending.GetType().GetField("Flush",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(pending);
                    Assert(SpinWait.SpinUntil(() => flush.IsCompleted, 3000), "故障刷盘未结束");
                    Assert(flush.IsFaulted, "注入刷盘故障未生效");
                    var checkpoint = typeof(EpbDiskWriter).GetMethod("CheckpointRawJournal",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var rejected = false;
                    try { checkpoint.Invoke(writer, new object[] { 1, true, false }); }
                    catch (TargetInvocationException ex) when (ex.InnerException is IOException)
                    { rejected = true; }
                    Assert(rejected, "后台刷盘失败被吞掉并继续回收日志");
                }
                using var recovered = new EpbDiskWriter(policy);
                var export = Path.Combine(root, "failed-flush-replay");
                recovered.ExportCycleAttemptTo(1, 100, export, true, true);
                AssertCsvCycle(export, 1, 100, 3);
            });
        }

        private static void CommittedDeviceFrameRejectsMutation()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycle(4, -702, time);
                var currents = new[] { 4d };
                var batch = new[] { new EpbChannelDiskBatch(4, currents, new[] { 0d }) };
                var hook = typeof(EpbDiskWriter).GetField("_deviceRawCommittedTestHook",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                hook.SetValue(writer, (Action)(() => currents[0] = 99));
                var rejected = false;
                try { writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, batch, 1, 1); }
                catch (InvalidDataException ex) { rejected = ex.Message.Contains("RawAttemptIdentityConflict"); }
                Assert(rejected && writer.GetCurrentCycleSampleCount(4) == 0,
                    "已提交凭据接受不同数据或提前推进计数");
                hook.SetValue(writer, null);
                currents[0] = 4;
                writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, batch, 1, 1);
                Assert(writer.GetCurrentCycleSampleCount(4) == 1, "失败后遗留凭据阻止精确重试");
                writer.CompleteCycle(4, -702, 1, time.AddMilliseconds(1));
                var export = Path.Combine(root, "committed-retry");
                writer.ExportCycleAttemptTo(4, -702, export, true, true);
                AssertCsvCycle(export, 4, -702, 1);
            });
        }

        private static void LearningBatchStagesExactSlices()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                EpbWriteTiming observed = null;
                policy.WriteTimingSink = value => observed = value;
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycle(4, -701, time.AddMilliseconds(1));
                writer.BeginCycle(5, -701, time.AddMilliseconds(2));
                var samples = new[] { time, time.AddMilliseconds(1), time.AddMilliseconds(2) };
                writer.WriteDeviceBatch("Dev1", 1, 1, samples, new[]
                {
                    new EpbChannelDiskBatch(4, new[] { 1d, 2d, 3d }, new[] { 0d, 0d, 0d }),
                    new EpbChannelDiskBatch(5, new[] { 4d, 5d, 6d }, new[] { 0d, 0d, 0d })
                }, 2, 3);
                Assert(observed.Succeeded && observed.RawStageMs > 0 && observed.RawCommitMs > 0,
                    "学习批次未走设备级耐久事务");
                Assert(observed.RawAppendMs > 0 && observed.RawAppendGateWaitMs >= 0 &&
                       observed.RingWriteMs > 0 && observed.RawAppendMs >= observed.RawAppendGateWaitMs,
                    "学习批次遗漏原始日志追加或实际映射写入诊断");
                Assert(writer.GetCurrentCycleSampleCount(4) == 2 && writer.GetCurrentCycleSampleCount(5) == 1,
                    "学习开始边界的时间切片改变");
                writer.CompleteCycle(4, -701, 2, time.AddMilliseconds(3));
                writer.CompleteCycle(5, -701, 1, time.AddMilliseconds(3));
                Assert(Scalar(policy, "SELECT SUM(sample_count) FROM epb_cycles WHERE cycle_number=-701") == 3,
                    "学习样本封圈数量改变");
            });
        }

        private static void CheckpointCoalescingPreservesPrefix()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycleAtDaqBoundary(4, 402, time, "Dev1", 1, 0);
                var channels = new[] { new EpbChannelDiskBatch(4, new[] { 4d }, new[] { 0d }) };
                for (var sequence = 1; sequence <= 101; sequence++)
                    writer.WriteDeviceBatch("Dev1", 1, sequence,
                        new[] { time.AddMilliseconds(sequence) }, channels, 1, 1);
                writer.CompleteCycle(4, 402, 101, time.AddMilliseconds(102));
                Assert(Scalar(policy, "SELECT sample_count FROM epb_cycles WHERE epb_id=4 AND cycle_number=402") == 101,
                    "按圈归并checkpoint丢失最大耐久前缀");
                var export = Path.Combine(root, "coalesced");
                writer.ExportCycleAttemptTo(4, 402, export, true, true);
                AssertCsvCycle(export, 4, 402, 101);
            });
        }

        private static void StorageTimingCannotChangeDurability()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                EpbWriteTiming observed = null;
                policy.WriteTimingSink = value => { observed = value; throw new Exception("诊断故障"); };
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycleAtDaqBoundary(4, 401, time, "Dev1", 1, 0);
                var channels = new[] { new EpbChannelDiskBatch(4, new[] { 4d }, new[] { 0d }) };
                writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, channels, 1, 1);
                Assert(observed != null && observed.Succeeded && observed.Device == "Dev1" &&
                       observed.Sequence == 1 && observed.ThreadId > 0 && observed.RawStageMs > 0 &&
                       observed.RawCommitMs > 0 && observed.CheckpointMs > 0,
                    "真实写盘阶段或身份缺失");
                writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, channels, 1, 1);
                Assert(observed.Succeeded && observed.RawStageMs == 0 &&
                       writer.GetCurrentCycleSampleCount(4) == 1, "重复序列仍提交原始事务或重复记账");
                var failed = false;
                try { writer.WriteDeviceBatch("Dev1", 1, 3, new[] { time }, channels, 1, 1); }
                catch (DaqSequenceGapException) { failed = true; }
                Assert(failed && !observed.Succeeded && observed.Sequence == 3,
                    "故障批次被诊断回调错误认定成功");
            });
        }

        private static long Scalar(DataRetentionPolicy policy, string sql)
        {
            using var conn = new SQLiteConnection($"Data Source={Path.Combine(policy.IndexAndExportPath, "index.db")};Pooling=False;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        private static void RawJournalReplaysExactPositions()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                var time = DateTime.UtcNow;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 100, time);
                    WriteSamples(writer, 1, 3, time);
                    WriteSamples(writer, 1, 3, time.AddMilliseconds(3));
                    writer.MarkMechanicalCycleCompleted(1, 100, time.AddMilliseconds(20));
                    writer.MarkMechanicalCycleCompleted(1, 100, time.AddMilliseconds(20));
                }
                var journalPath = Path.Combine(policy.IndexAndExportPath, "raw-journal.db");
                var savedJournal = File.ReadAllBytes(journalPath);
                var dataPath = Path.Combine(policy.DataStorePath, "EPB1_sliding.dat");
                using (var file = new FileStream(dataPath, FileMode.Open, FileAccess.Write))
                    file.Write(new byte[6 * SampleRecord.Size], 0, 6 * SampleRecord.Size);
                for (var pass = 0; pass < 2; pass++)
                {
                    if (pass > 0) File.WriteAllBytes(journalPath, savedJournal); // crash after apply, before reclaim
                    using var recovered = new EpbDiskWriter(policy);
                    var export = Path.Combine(root, "replay-" + pass);
                    recovered.ExportCycleAttemptTo(1, 100, export, true, true);
                    AssertCsvCycle(export, 1, 100, 6);
                    Assert(recovered.GetMechanicalCycleCompletedCount(1) == 1, "重放或重复机械回执增加动作次数");
                    Assert(Scalar(policy, "SELECT COUNT(*) FROM epb_cycles WHERE status='aborted_on_startup'") == 1,
                        "重放把中断圈变成合格圈");
                }
            });
        }

        private static void SequenceGapDoesNotPoisonDeviceQueue()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycleAtDaqBoundary(4, 101, time, "Dev1", 1, 1);
                writer.BeginCycleAtDaqBoundary(5, 102, time, "Dev1", 1, 2);
                var channels = new[] { new EpbChannelDiskBatch(4, new[] { 4d }, new[] { 0d }),
                    new EpbChannelDiskBatch(5, new[] { 5d }, new[] { 0d }) };
                var caught = false;
                try { writer.WriteDeviceBatch("Dev1", 1, 3, new[] { time }, channels, 2, 1); }
                catch (DaqSequenceGapException) { caught = true; }
                Assert(caught, "真实断序未形成明确异常终态");
                writer.WriteDeviceBatch("Dev1", 1, 3, new[] { time }, channels, 2, 1);
                writer.WriteDeviceBatch("Dev1", 1, 4, new[] { time.AddMilliseconds(1) }, channels, 2, 1);
                Assert(writer.GetCurrentCycleSampleCount(5) == 2, "健康通道被坏队首阻塞");
                var invalidCompletion = false;
                try { writer.CompleteCycle(4, 101, 2, time); }
                catch (InvalidDataException) { invalidCompletion = true; }
                Assert(invalidCompletion, "异常终态被控制层当成有效完成回执");
                writer.AbortCycle(4, 101, 2, time, "failed");
                Assert(Scalar(policy, "SELECT COUNT(*) FROM epb_cycles WHERE epb_id=4 AND status='data_gap' AND gap_start_sequence=2 AND gap_end_sequence=2") == 1,
                    "后续收尾覆盖了持久缺口");
                Assert(Scalar(policy, "SELECT mechanical_completed FROM epb_cycles WHERE epb_id=4") == 0, "异常圈凭空产生机械回执");
            });
        }

        private static void StagedBatchIsAppliedBeforeAbort()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycleAtDaqBoundary(4, 301, time, "Dev1", 1, 0);
                var boundaryType = typeof(EpbDiskWriter).GetNestedType("DeviceBatchBoundary", BindingFlags.NonPublic);
                var boundary = Activator.CreateInstance(boundaryType, "Dev1", 1L, 1L);
                var stage = typeof(EpbDiskWriter).GetMethod("StageDeviceRawJournal", BindingFlags.NonPublic | BindingFlags.Instance);
                stage.Invoke(writer, new object[] { boundary, new[] { time, time.AddMilliseconds(1) },
                    new[] { new EpbChannelDiskBatch(4, new[] { 4d, 5d }, new[] { 0d, 0d }) }, 1, 2 });
                Assert(writer.GetCurrentCycleSampleCount(4) == 0, "注入点已经应用而不是耐久未应用");
                writer.AbortCycle(4, 301, 0, time, "failed");
                writer.BeginCycleAtDaqBoundary(4, 302, time, "Dev1", 1, 1);
                writer.WriteDeviceBatch("Dev1", 1, 2, new[] { time.AddMilliseconds(2) },
                    new[] { new EpbChannelDiskBatch(4, new[] { 9d }, new[] { 0d }) }, 1, 1);
                writer.AbortCycle(4, 302, 1, time, "failed");
                var export = Path.Combine(root, "staged-abort");
                writer.ExportCycleAttemptTo(4, 301, export, true, true);
                AssertCsvCycle(export, 4, 301, 2);
            });
        }

        private static void InvalidBatchDoesNotPoisonQueue()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                var time = DateTime.UtcNow;
                writer.BeginCycleAtDaqBoundary(4, 201, time, "Dev1", 1, 0);
                writer.BeginCycleAtDaqBoundary(5, 202, time, "Dev1", 1, 0);
                var invalid = new[] { new EpbChannelDiskBatch(4, new[] { double.NaN }, new[] { 0d }),
                    new EpbChannelDiskBatch(5, new[] { 5d }, new[] { 0d }) };
                var failed = false;
                try { writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, invalid, 2, 1); }
                catch (DaqSequenceGapException) { failed = true; }
                Assert(failed, "非法样本未形成可观察的故障");
                writer.WriteDeviceBatch("Dev1", 1, 1, new[] { time }, invalid, 2, 1);
                var valid = new[] { new EpbChannelDiskBatch(4, new[] { 4d }, new[] { 0d }), invalid[1] };
                writer.WriteDeviceBatch("Dev1", 1, 2, new[] { time.AddMilliseconds(1) }, valid, 2, 1);
                Assert(writer.GetCurrentCycleSampleCount(5) == 2, "健康通道或后继被阻塞");
                writer.AbortCycle(4, 201, 1, time, "failed");
                Assert(Scalar(policy, "SELECT COUNT(*) FROM epb_cycles WHERE epb_id=4 AND status='data_gap' AND termination_reason='InvalidDaqBatch'") == 1,
                    "非法圈被伪造成完成");
                using var db = new SQLiteConnection($"Data Source={Path.Combine(policy.IndexAndExportPath, "raw-journal.db")};Pooling=False;");
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM invalid_batches WHERE channel=4 AND length(evidence)>0";
                Assert(Convert.ToInt32(cmd.ExecuteScalar()) == 1, "原始异常证据未耐久保留或重复隔离");
            });
        }

        private static void MechanicalBaselineSurvivesCheckpointLag()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using (var writer = new EpbDiskWriter(policy))
                {
                    Assert(writer.ReconcileMechanicalBaseline(1, 1000) == 1000, "历史次数丢失");
                    writer.BeginCycle(1, 9999, DateTime.UtcNow);
                    Assert(writer.GetMechanicalCycleCompletedCount(1) == 1000, "尝试圈号伪造次数");
                    Assert(writer.TryRecordMechanicalCompletion(1, 9999, DateTime.UtcNow), "机械回执未首次提交");
                    Assert(!writer.TryRecordMechanicalCompletion(1, 9999, DateTime.UtcNow), "重复回执再次提交");
                }
                using var reopened = new EpbDiskWriter(policy);
                Assert(reopened.ReconcileMechanicalBaseline(1, 1000) == 1001, "重启未补齐落后检查点");
                Assert(reopened.GetCycleAccounting(1).ValidFormal == 0, "中断圈当成有效正式圈");
            });
        }

        private static void RawJournalCapacityPreservesEvidence()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                policy.RawJournalMaxBytes = 3 * SampleRecord.Size;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 103, DateTime.UtcNow);
                    WriteSamples(writer, 1, 3, DateTime.UtcNow);
                    var failed = false;
                    try { WriteSamples(writer, 1, 1, DateTime.UtcNow); }
                    catch (IOException ex) { failed = ex.Message.Contains("RawJournalCapacityExceeded"); }
                    Assert(failed && writer.GetCurrentCycleSampleCount(1) == 3, "未处理日志被覆盖或假推进");
                }
                using var recovered = new EpbDiskWriter(policy);
                var export = Path.Combine(root, "capacity-replay");
                recovered.ExportCycleAttemptTo(1, 103, export, true, true);
                AssertCsvCycle(export, 1, 103, 3);
            });
        }
    }
}
