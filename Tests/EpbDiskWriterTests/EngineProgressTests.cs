using System;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using DataOperation;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace EpbDiskWriterTests
{
    internal static class EngineProgressTests
    {
        internal static int RunAll()
        {
            Run("RuntimeClockIsMonotonicAndExcludesPausedIntervals", Clock);
            Run("EngineProgressPublishesRealDurableCountsWithoutUi", Projection);
            Run("ProgressSaveCrashReplayPreservesMetadataAndIsolation", SaveCrash);
            Run("ProgressSaveSharesIsolationFileTransactionLock", SharedLock);
            Run("BlockedProgressIoCannotBlockSnapshotsOrReleaseOwnership", BlockedIo);
            Run("FailedProgressPublisherReportsOneFaultAndStops", Failure);
            Run("LegacyV3CheckpointSeparatesFormalAndMechanicalProgress", CheckpointMigration);
            Run("RunTimeLedgerSurvivesRestartAndAbsoluteReplay", TimeLedger);
            Run("CheckpointTargetsMatchControllerAcrossConfigurationRebind", TargetRebind);
            return 9;
        }

        private static void TargetRebind(string root)
        {
            var config = Fixture(root, out _); var identity = Identity();
            config.IsSameCycleForAllEpb = true; config.TestTarget = 400000;
            config.GetEpbRecord(4).TotalCount = 300000;
            config.GetEpbRecord(5).TotalCount = 0;
            var store = new EngineRunCheckpointStore(identity.SessionId, Path.Combine(root, "checkpoints"));
            store.LoadOrCreate(identity, config, new string('A', 64));
            Assert(store.Snapshot().RemainingFormalCycles[3] == 300000 - 21504 &&
                store.Snapshot().RemainingFormalCycles[4] == 400000 - 21505, "InitialTargetMismatch");
            store.Update(identity, value => { value.State = "StoppedByOperator"; return true; }, "test stopped");
            config.TestTarget = 500000;
            store.RebindStoppedConfiguration(identity, config, new string('B', 64));
            Assert(store.Snapshot().RemainingFormalCycles[3] == 300000 - 21504 &&
                store.Snapshot().RemainingFormalCycles[4] == 500000 - 21505, "RebindChangedExplicitChannelTarget");
            var before = store.Snapshot(); store.RefreshFormalProgress(identity, config); var after = store.Snapshot();
            Assert(before.RemainingFormalCycles.SequenceEqual(after.RemainingFormalCycles), "ProgressRefreshChangedTargetMeaning");
        }

        private static void Run(string name, Action<string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.EngineProgressTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); Console.WriteLine("PASS " + name); }
            finally { SQLiteConnection.ClearAllPools(); Directory.Delete(root, true); }
        }

        private static TestConfig Fixture(string root, out string path)
        {
            var repository = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            path = Path.Combine(root, "SyntheticProject", "Config", "TestConfig.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.Copy(Path.Combine(repository, "MTTfTest", "Config", "TestConfig.xml"), path);
            var config = ConfigLoader.LoadTest(path, null);
            config.StoreDir = root; config.TestName = "SyntheticProject";
            for (var ch = 1; ch <= 12; ch++)
            {
                var record = config.GetEpbRecord(ch); record.RunCount = 21000 + ch;
                record.MechanicalCycleCount = 21500 + ch; record.RunTimeSpan = TimeSpan.FromHours(13 + ch);
                record.Enabled = ch != 6; record.PermanentAlarmLatched = ch == 6;
            }
            ConfigLoader.SaveTest(path, config);
            return config;
        }

        private static RecoveryIdentity Identity() => new RecoveryIdentity
        {
            SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(), IncidentId = RecoveryProtocolV7.NewId(),
            RunEpoch = 1, Generation = 1, Revision = 1, ResourceScope = "System"
        };

        private static void Clock(string root)
        {
            long now = 100;
            var clock = new EngineRunTimeClock(Enumerable.Repeat(TimeSpan.FromHours(13).Ticks, 12).ToArray(), () => now, 100);
            clock.SetActive(4, true); now += 200;
            var expected = TimeSpan.FromHours(13).Ticks + TimeSpan.FromSeconds(2).Ticks;
            Assert(clock.Capture()[3] == expected && clock.Capture()[3] == expected, "RepeatedSnapshotAddsTime");
            clock.SetActive(4, false); now += 500;
            Assert(clock.Capture()[3] == expected, "PausedIntervalCounted");
            clock.SetActive(5, true); now += 100;
            Assert(clock.Capture()[4] == TimeSpan.FromHours(13).Ticks + TimeSpan.TicksPerSecond, "ChannelClockCrossed");
            now -= 100;
            Assert(clock.Capture()[4] == TimeSpan.FromHours(13).Ticks + TimeSpan.TicksPerSecond, "BackwardsTimestampChangedTime");
            clock.SetActive(4, true); now += 200;
            Assert(clock.Capture()[3] == expected + TimeSpan.TicksPerSecond, "ResumeAddedPausedInterval");
        }

        private static void Projection(string root)
        {
            var config = Fixture(root, out var path);
            using var storage = EngineProjectPersistence.Open(config, null, 1);
            var configuration = new EngineTestConfigurationStore(path);
            var hash = EngineTestConfigurationStore.Capture(config).ComputeSha256();
            var id = Identity(); var checkpoints = new EngineRunCheckpointStore(id.SessionId, Path.Combine(root, "checkpoints"));
            checkpoints.LoadOrCreate(id, config, hash);
            long now = 100;
            var clock = new EngineRunTimeClock(storage.Writer.ReadDurableProgress().Select(row => row.RunTimeTicks).ToArray(), () => Interlocked.Read(ref now), 100);
            var faults = 0;
            var publisher = new EngineProgressPublisher(() =>
            {
                storage.Writer.AdvanceRunTime(clock.Capture()); return storage.Writer.ReadDurableProgress();
            }, rows =>
            {
                configuration.SaveProgress(0, rows.Select(r => r.FormalCompleted).ToArray(), rows.Select(r => r.MechanicalCompleted).ToArray(), rows.Select(r => r.RunTimeTicks).ToArray());
                checkpoints.RefreshFormalProgress(id, ConfigLoader.LoadTest(path, null));
            }, ex => Interlocked.Increment(ref faults));
            try
            {
                clock.SetActive(4, true); Interlocked.Add(ref now, 200);
                var t = DateTime.UtcNow; var q = storage.Writer.BeginLearningCycle(4, t);
                storage.Writer.WriteSample(4, t.AddMilliseconds(1), 1, 2);
                storage.Writer.MarkMechanicalCycleCompleted(4, q, t.AddMilliseconds(2));
                storage.Writer.AbortCycle(4, q, 1, t.AddMilliseconds(2), "qualification_completed");
                publisher.FlushAsync(3000, CancellationToken.None).GetAwaiter().GetResult();
                Assert(checkpoints.Snapshot().FormalCyclesSinceRecovery == 0, "QualificationConsumedFormalRecoveryBudget");
                storage.Writer.BeginCycle(4, 21005, t.AddSeconds(1));
                storage.Writer.WriteSample(4, t.AddSeconds(1.1), 2, 4);
                storage.Writer.MarkMechanicalCycleCompleted(4, 21005, t.AddSeconds(1.2));
                storage.Writer.CompleteCycle(4, 21005, 1, t.AddSeconds(1.2));
                for (var i = 0; i < 5000; i++) publisher.Request();
                publisher.FlushAsync(3000, CancellationToken.None).GetAwaiter().GetResult();
                var saved = ConfigLoader.LoadTest(path, null); var snapshot = checkpoints.Snapshot();
                Assert(saved.GetEpbRecord(4).RunCount == 21005 && snapshot.FormalCyclesCompleted[3] == 21005 &&
                    snapshot.MechanicalCyclesCompleted[3] == 21506 && snapshot.FormalCyclesSinceRecovery == 1, "DurableProjectionWrongCounts");
                Assert(saved.GetEpbRecord(4).RunTimeSpan == TimeSpan.FromHours(17) + TimeSpan.FromSeconds(2), "RuntimeNotProjected");
                Assert(saved.GetEpbRecord(6).PermanentAlarmLatched && !saved.GetEpbRecord(6).Enabled &&
                    EngineTestConfigurationStore.Capture(saved).ComputeSha256() == hash && configuration.ReadRevision() == 0, "ProjectionChangedConfigOrIsolation");
                var copy = publisher.Snapshot(); copy[3].FormalCompleted = 0;
                Assert(publisher.Snapshot()[3].FormalCompleted == 21005 && publisher.Healthy && faults == 0, "MutableOrFailedProgressSnapshot");
                clock.SetActive(4, false); Interlocked.Add(ref now, 500);
                publisher.FlushAsync(3000, CancellationToken.None).GetAwaiter().GetResult();
                Assert(publisher.Snapshot()[3].RunTimeTicks == (TimeSpan.FromHours(17) + TimeSpan.FromSeconds(2)).Ticks, "StoppedRuntimeAdvanced");
            }
            finally { Assert(publisher.StopAsync(3000).GetAwaiter().GetResult(), "PublisherDidNotStop"); }
        }

        private static void SaveCrash(string root)
        {
            var config = Fixture(root, out var path); var store = new EngineTestConfigurationStore(path);
            var formal = Enumerable.Range(1, 12).Select(ch => 21001 + ch).ToArray();
            var mechanical = Enumerable.Range(1, 12).Select(ch => 21501L + ch).ToArray();
            var time = Enumerable.Range(1, 12).Select(ch => TimeSpan.FromHours(14 + ch).Ticks).ToArray();
            var bytes = File.ReadAllBytes(path);
            Expect<IOException>(() => store.SaveProgress(0, formal, mechanical, time, point =>
            { if (point == "ProgressPrepared") throw new IOException("synthetic kill"); }));
            Assert(bytes.SequenceEqual(File.ReadAllBytes(path)), "PreparedProgressChangedCurrentXml");
            Expect<IOException>(() => store.SaveProgress(0, formal, mechanical, time, point =>
            { if (point == "ProgressCommitted") throw new IOException("synthetic kill"); }));
            store.SaveProgress(0, formal, mechanical, time); // Same facts, not another increment.
            var saved = ConfigLoader.LoadTest(path, null);
            Assert(saved.GetEpbRecord(4).RunCount == 21005 && saved.GetEpbRecord(4).RunTimeSpan == TimeSpan.FromHours(18), "ProgressReplayDoubleCounted");
            var after = File.ReadAllBytes(path);
            Expect<InvalidOperationException>(() => store.SaveProgress(1, formal, mechanical, time));
            Assert(after.SequenceEqual(File.ReadAllBytes(path)) && saved.GetEpbRecord(6).PermanentAlarmLatched, "RevisionConflictChangedXml");
        }

        private static void SharedLock(string root)
        {
            Fixture(root, out var path); var store = new EngineTestConfigurationStore(path);
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var isolation = Task.Run(() => ConfigLoader.WithTestFileLock(path, () =>
            {
                entered.Set(); release.Wait(); var current = ConfigLoader.LoadTest(path, null);
                current.GetEpbRecord(9).PermanentAlarmLatched = true; current.GetEpbRecord(9).Enabled = false;
                ConfigLoader.SaveTest(path, current); return true;
            }));
            Assert(entered.Wait(2000), "IsolationLockNotEntered");
            var progress = Task.Run(() => store.SaveProgress(0, Enumerable.Repeat(22000, 12).ToArray(),
                Enumerable.Repeat(23000L, 12).ToArray(), Enumerable.Repeat(TimeSpan.FromHours(40).Ticks, 12).ToArray()));
            try { Assert(!progress.Wait(50), "ProgressBypassedIsolationLock"); }
            finally { release.Set(); }
            Assert(Task.WaitAll(new Task[] { isolation, progress }, 3000), "SharedFileLockDeadlock");
            var saved = ConfigLoader.LoadTest(path, null);
            Assert(saved.GetEpbRecord(9).PermanentAlarmLatched && !saved.GetEpbRecord(9).Enabled && saved.GetEpbRecord(9).RunCount == 22000,
                "ProgressOverwroteIsolationCommit");
        }

        private static EpbDurableProgress[] EmptyProgress() => Enumerable.Range(1, 12).Select(ch => new EpbDurableProgress { Channel = ch }).ToArray();

        private static void BlockedIo(string root)
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var calls = 0;
            var publisher = new EngineProgressPublisher(EmptyProgress, rows => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); }, ex => { });
            try
            {
                publisher.Request(); Assert(entered.Wait(2000), "PublisherNotEntered");
                for (var i = 0; i < 10000; i++) publisher.Request();
                var watch = Stopwatch.StartNew();
                Assert(publisher.Snapshot() == null && !publisher.Healthy && watch.ElapsedMilliseconds < 100, "SnapshotWaitedForDisk");
                Expect<TimeoutException>(() => publisher.FlushAsync(50, CancellationToken.None).GetAwaiter().GetResult());
                Assert(!publisher.StopAsync(50).GetAwaiter().GetResult() && calls == 1, "BlockedWriterReleasedOrMultipleWorkersStarted");
            }
            finally { release.Set(); Assert(publisher.StopAsync(2000).GetAwaiter().GetResult(), "ReleasedWriterStillRunning"); }
        }

        private static void Failure(string root)
        {
            var faults = 0;
            var publisher = new EngineProgressPublisher(EmptyProgress, rows => { throw new IOException("synthetic full disk"); }, ex => Interlocked.Increment(ref faults));
            try
            {
                Expect<InvalidOperationException>(() => publisher.FlushAsync(3000, CancellationToken.None).GetAwaiter().GetResult());
                Assert(publisher.StopAsync(2000).GetAwaiter().GetResult() && faults == 1 && !publisher.Healthy && publisher.Snapshot() == null, "FailedProgressLookedHealthyOrStormed");
            }
            finally { publisher.StopAsync(2000).GetAwaiter().GetResult(); }
        }

        private static void CheckpointMigration(string root)
        {
            var config = Fixture(root, out _); var id = Identity(); var hash = EngineTestConfigurationStore.Capture(config).ComputeSha256();
            var store = new EngineRunCheckpointStore(id.SessionId, root); store.LoadOrCreate(id, config, hash);
            store.Update(id, checkpoint =>
            {
                checkpoint.ProgressSchemaVersion = 0; checkpoint.FormalCyclesCompleted[3] = 21504;
                checkpoint.FormalCyclesSinceRecovery = 100; checkpoint.QualificationCyclesCompleted = 2;
                checkpoint.State = "Running"; checkpoint.ActiveCycleInvalidated = false; return true;
            }, "synthetic old V3 checkpoint");
            var reopened = new EngineRunCheckpointStore(id.SessionId, root);
            var result = reopened.LoadOrCreate(id, config, hash);
            Assert(result.ProgressSchemaVersion == 1 && result.FormalCyclesCompleted[3] == 21004 && result.MechanicalCyclesCompleted[3] == 21504 &&
                result.FormalCyclesSinceRecovery == 0 && result.QualificationCyclesCompleted == 0 && result.ActiveCycleInvalidated, "OldMechanicalProgressBecameFormalAuthority");
            using var blockedFile = new FileStream(Path.Combine(root, "run-" + id.SessionId + ".v7.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert(reopened.Snapshot().FormalCyclesCompleted[3] == 21004, "CheckpointSnapshotReadDisk");
        }

        private static void TimeLedger(string root)
        {
            var config = Fixture(root, out _);
            var ticks = Enumerable.Range(1, 12).Select(ch => TimeSpan.FromHours(15 + ch).Ticks + 12345).ToArray();
            using (var storage = EngineProjectPersistence.Open(config, null, 1))
            {
                storage.Writer.AdvanceRunTime(ticks); storage.Writer.AdvanceRunTime(ticks); storage.Writer.AdvanceRunTime(new long[12]);
                Assert(storage.Writer.ReadDurableProgress()[3].RunTimeTicks == ticks[3], "AbsoluteTimeReplayChangedLedger");
            }
            using (var storage = EngineProjectPersistence.Open(config, null, 1))
                Assert(storage.Writer.ReadDurableProgress()[3].RunTimeTicks == ticks[3], "RestartLostRuntimeTicks");
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    }
}
