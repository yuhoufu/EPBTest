using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using DataOperation;
using MTTFTest.EngineHost;

namespace EpbDiskWriterTests
{
    internal static class EnginePersistenceCompositionTests
    {
        internal static int RunAll()
        {
            Run("EngineProjectPreservesLegacyDataAndNonzeroProgress", PreservesHistory);
            Run("EngineProjectCrashPublicationAndSingleWriter", Publication);
            Run("EngineProjectMissingRingDoesNotCreateOrResizeData", MissingRing);
            Run("EngineProjectCorruptIndexReleasesFailedConstructorHandles", CorruptIndex);
            Run("DurableProgressSurvivesRetentionAndDoesNotCountInterruptedCycle", Retention);
            Run("DurableProgressRollsBackWithFailedCycleTransaction", Rollback);
            Run("EnginePersistenceBoundaryRequiresBothExactDaqPrefixes", Boundary);
            Run("EnginePersistenceBoundaryHonorsTimeoutAndCancellation", Deadline);
            return 8;
        }

        private static void Run(string name, Action<string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.EnginePersistenceTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); Console.WriteLine("PASS " + name); }
            finally
            {
                SQLiteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        private static TestConfig Configuration(string root)
        {
            var config = new TestConfig { StoreDir = root, TestName = "SyntheticProject", TestTarget = 200000, TestPeriod = 15 };
            for (var ch = 1; ch <= 12; ch++)
            {
                var r = config.GetEpbRecord(ch);
                r.RunCount = 21000 + ch; r.MechanicalCycleCount = 21500 + ch;
                r.TotalCount = 200000; r.RunTimeSpan = TimeSpan.FromHours(13 + ch);
                r.Enabled = ch != 6; r.PermanentAlarmLatched = ch == 6;
            }
            return config;
        }

        private static string StoreRoot(TestConfig config) => Path.Combine(config.StoreDir, config.TestName, EngineProjectPersistence.DirectoryName);

        private static void PreservesHistory(string root)
        {
            var config = Configuration(root);
            var oldRoot = Path.Combine(root, config.TestName);
            Directory.CreateDirectory(oldRoot);
            var oldIndex = Path.Combine(oldRoot, "index.db");
            File.WriteAllText(oldIndex, "synthetic legacy index: must not be opened or mutated");
            var oldBytes = File.ReadAllBytes(oldIndex);
            using (var store = EngineProjectPersistence.Open(config, null, 1))
            {
                for (var ch = 1; ch <= 12; ch++)
                {
                    Assert(store.Recorder.GetLastCycleNumber(ch) == 21000 + ch, "ImportedAllocationFloorMismatch");
                    Assert(store.Recorder.GetCompletedFormalCycleCount(ch) == 21000 + ch, "ImportedFormalCountMismatch");
                    Assert(store.Recorder.GetMechanicalCycleCompletedCount(ch) == 21500 + ch, "ImportedMechanicalCountMismatch");
                }
                var t = DateTime.UtcNow;
                Complete(store.Writer, 4, 21005, t);
                store.Writer.BeginCycle(4, 21006, t.AddSeconds(2));
                store.Writer.WriteSample(4, t.AddSeconds(2.1), 9, 60);
                // Leave an interrupted circle for restart recovery.
            }
            var reopenedConfig = Configuration(root); // Simulate XML lagging the durable SQLite commit.
            using (var store = EngineProjectPersistence.Open(reopenedConfig, null))
            {
                Assert(store.Recorder.GetLastCycleNumber(4) == 21006, "InterruptedNumberReused");
                Assert(reopenedConfig.GetEpbRecord(4).RunCount == 21005, "InterruptedCircleCountedAsCompleted");
                Assert(reopenedConfig.GetEpbRecord(4).MechanicalCycleCount == 21505, "MechanicalProgressNotRecovered");
                Assert(reopenedConfig.GetEpbRecord(4).RunTimeSpan == TimeSpan.FromHours(17), "HistoricalTimeReset");
                Assert(reopenedConfig.GetEpbRecord(6).PermanentAlarmLatched && !reopenedConfig.GetEpbRecord(6).Enabled, "IsolationReset");
                Assert(new FileInfo(Path.Combine(store.RootDirectory, "EPB4_sliding.dat")).Length == 1024 * 1024, "ReopenResizedRing");
                store.Writer.ExportCompletedCycleTo(4, 21005, Path.Combine(root, "Evidence"), true, true);
                Assert(Directory.GetFiles(Path.Combine(root, "Evidence"), "*.bin", SearchOption.AllDirectories).Any(), "CompletedSamplesNotRecoverable");
            }
            Assert(oldBytes.SequenceEqual(File.ReadAllBytes(oldIndex)), "LegacyIndexMutated");
        }

        private static void Publication(string root)
        {
            var config = Configuration(root);
            Expect<IOException>(() => EngineProjectPersistence.Open(config, null, 1, point =>
            { if (point == "BeforePublish") throw new IOException("synthetic kill"); }));
            Assert(!Directory.Exists(StoreRoot(config)), "UnpublishedStoreBecameCurrent");
            Expect<IOException>(() => EngineProjectPersistence.Open(config, null, 1, point =>
            { if (point == "Published") throw new IOException("synthetic kill"); }));
            using (var store = EngineProjectPersistence.Open(config, null, 1))
            {
                Expect<IOException>(() => EngineProjectPersistence.Open(config, null, 1));
                Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21004, "BaselineAppliedTwice");
                Expect<InvalidOperationException>(() => store.Writer.ImportProgressBaseline(new int[12], new long[12]));
            }
            using (var store = EngineProjectPersistence.Open(config, null, 1))
                Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21004, "OwnerLockNotReleased");
        }

        private static void MissingRing(string root)
        {
            var config = Configuration(root);
            using (EngineProjectPersistence.Open(config, null, 1)) { }
            var ring = Path.Combine(StoreRoot(config), "EPB8_sliding.dat");
            File.Move(ring, ring + ".preserved");
            var index = Path.Combine(StoreRoot(config), "index.db");
            var before = File.ReadAllBytes(index);
            Expect<InvalidDataException>(() => EngineProjectPersistence.Open(config, null, 1));
            Assert(!File.Exists(ring) && before.SequenceEqual(File.ReadAllBytes(index)), "MissingRingSilentlyRecreatedOrIndexChanged");
            File.Move(ring + ".preserved", ring);
            using (var stream = new FileStream(ring, FileMode.Open, FileAccess.Write)) stream.SetLength(17);
            Expect<InvalidDataException>(() => EngineProjectPersistence.Open(config, null, 1));
            Assert(new FileInfo(ring).Length == 17, "DamagedRingSilentlyResized");
        }

        private static void CorruptIndex(string root)
        {
            var config = Configuration(root);
            using (EngineProjectPersistence.Open(config, null, 1)) { }
            var index = Path.Combine(StoreRoot(config), "index.db");
            File.Move(index, index + ".good");
            File.WriteAllText(index, new string('x', 8192));
            Expect<SQLiteException>(() => EngineProjectPersistence.Open(config, null, 1));
            File.Move(index, index + ".bad");
            File.Move(index + ".good", index);
            using (var store = EngineProjectPersistence.Open(config, null, 1))
                Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21004, "FailedConstructorLeakedIndexOrRingHandles");
        }

        private static void Retention(string root)
        {
            var config = Configuration(root);
            using (var store = EngineProjectPersistence.Open(config, null, 1))
            {
                Complete(store.Writer, 4, 21005, DateTime.UtcNow);
                var t = DateTime.UtcNow.AddSeconds(3);
                var q = store.Writer.BeginLearningCycle(4, t);
                store.Writer.WriteSample(4, t.AddMilliseconds(1), 1, 3);
                store.Writer.MarkMechanicalCycleCompleted(4, q, t.AddMilliseconds(2));
                store.Writer.AbortCycle(4, q, 1, t.AddMilliseconds(2), "qualification_completed");
                Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21005, "QualificationCountedAsFormal");
                Sql(store.RootDirectory, "DELETE FROM epb_cycles WHERE epb_id=4");
                Assert(store.Recorder.GetMechanicalCycleCompletedCount(4) == 21506, "RetentionDecrementedMechanicalProgress");
            }
            using (var store = EngineProjectPersistence.Open(config, null, 1))
            {
                Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21005, "RetentionLostFormalProgressAfterRestart");
                Assert(store.Recorder.GetLastCycleNumber(4) == 21005, "RetentionReusedAllocatedNumber");
                Assert(store.Recorder.GetMechanicalCycleCompletedCount(4) == 21506, "RestartLostMechanicalProgress");
            }
        }

        private static void Rollback(string root)
        {
            var config = Configuration(root);
            using var store = EngineProjectPersistence.Open(config, null, 1);
            var t = DateTime.UtcNow;
            store.Writer.BeginCycle(4, 21005, t);
            store.Writer.WriteSample(4, t.AddMilliseconds(1), 1, 1);
            Sql(store.RootDirectory, "CREATE TRIGGER synthetic_failure AFTER UPDATE ON epb_cycles WHEN NEW.status='completed' BEGIN SELECT RAISE(ABORT,'synthetic commit failure'); END;");
            Expect<SQLiteException>(() => store.Writer.CompleteCycle(4, 21005, 1, t.AddMilliseconds(2)));
            Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21004, "RolledBackCycleIncrementedProgress");
            Sql(store.RootDirectory, "DROP TRIGGER synthetic_failure");
            store.Writer.CompleteCycle(4, 21005, 1, t.AddMilliseconds(2));
            Expect<InvalidOperationException>(() => store.Writer.CompleteCycle(4, 21005, 1, t.AddMilliseconds(2)));
            Assert(store.Recorder.GetCompletedFormalCycleCount(4) == 21005, "RepeatedCompletionCountedTwice");
        }

        private static void Boundary(string root)
        {
            var calls = 0;
            var boundary = new EnginePersistenceBoundary((a, b, timeout, token) =>
            { calls++; Assert(a == 421 && b == 731 && timeout == 10000, "WrongFrozenDaqPrefix"); return Task.FromResult(true); });
            boundary.FlushAsync(Prefix(), CancellationToken.None).GetAwaiter().GetResult();
            Expect<InvalidOperationException>(() => boundary.FlushAsync(new Dictionary<string, long> { ["Dev1"] = 421 }, CancellationToken.None).GetAwaiter().GetResult());
            Assert(calls == 1, "MissingPrefixTreatedAsZero");
            boundary = new EnginePersistenceBoundary((a, b, timeout, token) => Task.FromResult(false));
            Expect<TimeoutException>(() => boundary.FlushAsync(Prefix(), CancellationToken.None).GetAwaiter().GetResult());
        }

        private static void Deadline(string root)
        {
            var never = new TaskCompletionSource<bool>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var boundary = new EnginePersistenceBoundary((a, b, timeout, token) => never.Task, 50);
            Expect<TimeoutException>(() => boundary.FlushAsync(Prefix(), CancellationToken.None).GetAwaiter().GetResult());
            Assert(watch.ElapsedMilliseconds < 2000, "UnresponsivePublisherBlockedBoundary");
            never.SetException(new IOException("late I/O failure must be observed"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Expect<OperationCanceledException>(() => boundary.FlushAsync(Prefix(), canceled.Token).GetAwaiter().GetResult());
        }

        private static Dictionary<string, long> Prefix() => new Dictionary<string, long> { ["Dev1"] = 421, ["Dev2"] = 731 };
        private static void Complete(EpbDiskWriter writer, int channel, int cycle, DateTime t)
        {
            writer.BeginCycle(channel, cycle, t); writer.WriteSample(channel, t.AddMilliseconds(1), 4.25, 62);
            writer.MarkMechanicalCycleCompleted(channel, cycle, t.AddMilliseconds(2));
            writer.CompleteCycle(channel, cycle, 1, t.AddMilliseconds(2));
        }
        private static void Sql(string root, string sql)
        {
            using var connection = new SQLiteConnection("Data Source=" + Path.Combine(root, "index.db") + ";Pooling=False");
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    }
}
