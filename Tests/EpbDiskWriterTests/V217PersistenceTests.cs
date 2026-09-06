using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using DataOperation;

namespace EpbDiskWriterTests
{
    internal static partial class Program
    {
        private static void RunV217PersistenceTests()
        {
            Run("I0046 多帧checkpoint按圈归并仍保留最大耐久前缀", CheckpointCoalescingPreservesPrefix);
            Run("I0046 写盘阶段诊断隔离且空批次幂等", StorageTimingCannotChangeDurability);
            Run("V217 原始日志修复被清零的环形记录且重放不重复计数", RawJournalReplaysExactPositions);
            Run("V217 断序异常圈终结且同批健康通道继续写入", SequenceGapDoesNotPoisonDeviceQueue);
            Run("V217 未应用日志容量不足保留原证据", RawJournalCapacityPreservesEvidence);
            Run("V217 历史基线加耐久回执且不把圈号当次数", MechanicalBaselineSurvivesCheckpointLag);
            Run("V217 非法数据耐久隔离且健康后继继续", InvalidBatchDoesNotPoisonQueue);
            Run("V217 已耐久未应用批次停止时重放且后圈不覆盖", StagedBatchIsAppliedBeforeAbort);
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
