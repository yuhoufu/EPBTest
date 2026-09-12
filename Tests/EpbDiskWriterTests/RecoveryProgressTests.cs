using System;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace EpbDiskWriterTests
{
    internal static partial class Program
    {
        private static void RunRecoveryProgressTests()
        {
            Run("Guard 只观察真正提交的机械圈与成功终态", RecoveryProgressTracksDurableCommits);
            Run("Guard 提交快照不等待数据库锁", RecoveryProgressDoesNotQueryDatabase);
            Run("Guard 不把重开项目历史圈数当作新进展", RecoveryProgressDoesNotReplayHistory);
            Run("Guard 数据库提交失败不得发布完成", RecoveryProgressRejectsFailedCommit);
        }

        private static void RecoveryProgressTracksDurableCommits()
        {
            WithRoot(root =>
            {
                using var writer = new EpbDiskWriter(NewPolicy(root));
                var now = DateTime.UtcNow;
                writer.BeginCycle(1, 1, now);
                writer.WriteBatch(1, new[] { now }, new[] { 1d }, new[] { 0d }, 1);
                Assert(writer.CaptureCommittedCycleProgress(1).Sequence == 0, "写样本不能伪报成功圈");
                Assert(writer.TryRecordMechanicalCompletion(1, 1, now), "首次机械回执应成功");
                var mechanical = writer.CaptureCommittedCycleProgress(1);
                Assert(mechanical.MechanicalCommits == 1 && mechanical.FormalCommits == 0, "机械事实与正式完成必须区分");
                Assert(!writer.TryRecordMechanicalCompletion(1, 1, now), "机械回执应幂等");
                Assert(writer.CaptureCommittedCycleProgress(1).Sequence == mechanical.Sequence, "重复机械回执不能增加进展");
                writer.CompleteCycle(1, 1, 1, now);
                var completed = writer.CaptureCommittedCycleProgress(1);
                Assert(completed.SuccessfulCommits == 1 && completed.FormalCommits == 1 && completed.CommittedUtcTicks > 0,
                    "已成功提交的终态未发布");
                Assert(mechanical.SuccessfulCommits == 0, "已返回快照不能被后续更新修改");
                writer.BeginCycle(1, 2, now.AddSeconds(1));
                writer.AbortCycle(1, 2, 0, now.AddSeconds(1), "failed");
                Assert(writer.CaptureCommittedCycleProgress(1).FormalCommits == 1, "失败圈不能伪报正式提交");
                Assert(writer.CaptureCommittedCycleProgress(2).Sequence == 0, "不能替另一个通道发布进展");
            });
        }

        private static void RecoveryProgressDoesNotQueryDatabase()
        {
            WithRoot(root =>
            {
                using var writer = new EpbDiskWriter(NewPolicy(root));
                var gate = typeof(EpbDiskWriter).GetField("_dbGate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(writer);
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var blocker = Task.Run(() => { lock (gate) { held.Set(); release.Wait(); } });
                try
                {
                    Assert(held.Wait(3000), "数据库锁注入未就绪");
                    var reader = Task.Run(() => writer.CaptureCommittedCycleProgress(1));
                    Assert(reader.Wait(1000) && reader.Result.Sequence == 0, "只读提交快照被数据库锁阻塞");
                }
                finally { release.Set(); blocker.GetAwaiter().GetResult(); }
            });
        }

        private static void RecoveryProgressDoesNotReplayHistory()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                string firstWriter;
                using (var writer = new EpbDiskWriter(policy))
                {
                    writer.BeginCycle(1, 1, DateTime.UtcNow);
                    writer.TryRecordMechanicalCompletion(1, 1, DateTime.UtcNow);
                    firstWriter = writer.CaptureCommittedCycleProgress(1).WriterId;
                }
                using (var reopened = new EpbDiskWriter(policy))
                {
                    var snapshot = reopened.CaptureCommittedCycleProgress(1);
                    Assert(snapshot.WriterId != firstWriter && snapshot.Sequence == 0 && snapshot.MechanicalCommits == 0,
                        "重新打开项目不能把已有回执当作当前进展");
                    Assert(reopened.GetMechanicalCycleCompletedCount(1) == 1, "历史机械事实必须仍保留");
                }
            });
        }

        private static void RecoveryProgressRejectsFailedCommit()
        {
            WithRoot(root =>
            {
                var policy = NewPolicy(root);
                using var writer = new EpbDiskWriter(policy);
                writer.BeginCycle(1, 1, DateTime.UtcNow);
                using var connection = new SQLiteConnection($"Data Source={Path.Combine(policy.IndexAndExportPath, "index.db")};Pooling=False;");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER deny_guard_receipt BEFORE UPDATE OF mechanical ON cycle_receipts BEGIN SELECT RAISE(ABORT, 'guard-test-rollback'); END;";
                command.ExecuteNonQuery();
                var failed = false;
                try { writer.TryRecordMechanicalCompletion(1, 1, DateTime.UtcNow); }
                catch (SQLiteException) { failed = true; }
                Assert(failed && writer.CaptureCommittedCycleProgress(1).Sequence == 0, "失败事务不能发布机械进展");
                command.CommandText = "DROP TRIGGER deny_guard_receipt;";
                command.ExecuteNonQuery();
                Assert(writer.TryRecordMechanicalCompletion(1, 1, DateTime.UtcNow), "恢复后应能提交原回执");
                Assert(writer.CaptureCommittedCycleProgress(1).MechanicalCommits == 1, "重试只计算成功的一次提交");
            });
        }
    }
}
