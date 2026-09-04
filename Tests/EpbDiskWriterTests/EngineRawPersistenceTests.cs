using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Config;
using DataOperation;
using MTTFTest.EngineHost;

namespace EpbDiskWriterTests
{
    internal static class EngineRawPersistenceTests
    {
        internal static int RunAll()
        {
            Run("EngineRawPreflushDrainsTailAndPreservesSampleOrder", Tail);
            Run("EngineRawCapacityRejectsWithoutLosingOwnership", Capacity);
            Run("EngineRawMappingRejectsWithoutDisposingCallerBatch", Mapping);
            Run("EngineRawBackgroundWriterRunsWithoutUi", Background);
            Run("EngineRawBackgroundFailureReportsOnceAndRetainsEvidence", Failure);
            Run("RawActualIoOutlivesDeadlineAndBlocksTeardown", LateIo);
            Run("RawBoundaryCancellationCannotClaimDurability", Cancellation);
            Run("StatWriteFailureRestoresAggregateAndDoesNotClaimSuccess", StatFailure);
            return 8;
        }

        private static void Run(string name, Action<string> test)
        {
            var root = Path.Combine(Path.GetTempPath(), "MTTFTest.EngineRawTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); Console.WriteLine("PASS " + name); }
            finally { Directory.Delete(root, true); }
        }

        private static AiConfigDetail Configuration() => new AiConfigDetail
        {
            Records = new List<AiConfigDetailRecord>
            {
                new AiConfigDetailRecord { 序号 = 3, 物理通道 = "Dev2/ai0", 参数名 = "EPB7_current", 是否启用 = 1, 变换斜率 = 3 },
                new AiConfigDetailRecord { 序号 = 2, 物理通道 = "Dev1/ai1", 参数名 = "Pressure_1", 是否启用 = 1, 变换斜率 = 2 },
                new AiConfigDetailRecord { 序号 = 1, 物理通道 = "Dev1/ai0", 参数名 = "EPB1_current", 是否启用 = 1, 变换斜率 = 2 }
            }
        };

        private static EngineRawPersistence Create(string root, bool background = false,
            Action<string, int, int> full = null, Action<Exception> fault = null, Action<string> warning = null)
        {
            var config = Path.Combine(root, "AIConfig.xml");
            if (!File.Exists(config)) File.WriteAllText(config, "<synthetic-config />");
            return new EngineRawPersistence(root, Configuration(), new DaqRuntimeSettings(1000, 2), 60,
                new[] { config }, full, fault ?? (ex => { }), warning, background);
        }

        private static OwnedDaqRawBatch Batch(string device, int channels, int samples, double value, long sequence = 1)
        {
            var data = new double[channels, samples];
            for (var row = 0; row < channels; row++) for (var col = 0; col < samples; col++) data[row, col] = value + 10 * row + col;
            var now = DateTime.Now; return OwnedDaqRawBatch.CopyFrom(device, data, now, now.AddMilliseconds(-samples), sequence);
        }

        private static Dictionary<string, long> Prefix() => new Dictionary<string, long> { ["Dev1"] = 5, ["Dev2"] = 6 };
        private static Task<bool> Drained(long a, long b, int timeout, CancellationToken token) => Task.FromResult(true);
        private static string RawFile(EngineRawPersistence raw, string device) => Path.Combine(raw.RootDirectory, "DAQ_" + device + "_Raw_1.bin");
        private static void Stop(EngineRawPersistence raw) => Assert(raw.StopAfterAcquisitionAsync(3000).GetAwaiter().GetResult(), "RawOwnerDidNotStop");

        private static void Tail(string root)
        {
            var raw = Create(root);
            var first = Batch("Dev1", 2, 2, 3); var second = Batch("Dev2", 1, 3, 7);
            try
            {
                raw.Enqueue(first); raw.Enqueue(second);
                raw.FlushBoundaryAsync(Prefix(), (a, b, timeout, token) =>
                {
                    Assert(a == 5 && b == 6 && new FileInfo(RawFile(raw, "Dev1")).Length == 56, "DrainRanBeforeDownstreamCapacityFreed");
                    raw.Enqueue(Batch("Dev1", 2, 1, 42, 5)); return Task.FromResult(true);
                }, CancellationToken.None).GetAwaiter().GetResult();
                var data = File.ReadAllBytes(RawFile(raw, "Dev1"));
                Assert(data.Length == 84 && BitConverter.ToDouble(data, 12) == 3 && BitConverter.ToDouble(data, 20) == 13 &&
                    BitConverter.ToDouble(data, 40) == 4 && BitConverter.ToDouble(data, 68) == 42, "RawSamplesScaledReorderedOrTailLost");
                Assert(new FileInfo(RawFile(raw, "Dev2")).Length == 60, "SecondDeviceWidthWrong");
                Assert(File.ReadAllBytes(Path.Combine(root, "AIConfig.xml")).SequenceEqual(File.ReadAllBytes(Path.Combine(raw.RootDirectory, "Config", "AIConfig.xml"))), "RawConfigurationSnapshotChanged");
                Expect<ObjectDisposedException>(() => { var values = first.Values; });
                raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult();
                Assert(new FileInfo(RawFile(raw, "Dev1")).Length == 84, "RepeatedFlushDuplicatedRawSamples");
            }
            finally { Stop(raw); first.Dispose(); second.Dispose(); }
        }

        private static void Capacity(string root)
        {
            var full = 0; var raw = Create(root, full: (device, depth, capacity) => Interlocked.Increment(ref full));
            using var rejected = Batch("Dev1", 2, 1, 999);
            try
            {
                for (var i = 0; i < 256; i++) raw.Enqueue(Batch("Dev1", 2, 1, i));
                Expect<TimeoutException>(() => raw.Enqueue(rejected));
                Assert(full == 1 && rejected.Values[0] == 999, "RejectedBatchOwnershipLostOrNoCapacityObservation");
                raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult();
                var data = File.ReadAllBytes(RawFile(raw, "Dev1"));
                Assert(data.Length == 256 * 28 && BitConverter.ToDouble(data, 12) == 0 && BitConverter.ToDouble(data, 255 * 28 + 12) == 255, "AcceptedRawFifoDropped");
            }
            finally { Stop(raw); }
        }

        private static void Mapping(string root)
        {
            var raw = Create(root); using var bad = Batch("Dev1", 1, 2, 9);
            try { Expect<InvalidDataException>(() => raw.Enqueue(bad)); Assert(bad.Values[0] == 9, "RejectedMappingDisposedCaller"); }
            finally { Stop(raw); }
            using var late = Batch("Dev1", 2, 1, 1);
            Expect<ObjectDisposedException>(() => raw.Enqueue(late)); Assert(late.Values[0] == 1, "ClosedRawOwnerStoleBatch");
        }

        private static void Background(string root)
        {
            var raw = Create(root, true); var batch = Batch("Dev1", 2, 2, 1);
            try
            {
                raw.Enqueue(batch);
                Assert(SpinWait.SpinUntil(() => File.Exists(RawFile(raw, "Dev1")) && new FileInfo(RawFile(raw, "Dev1")).Length == 56, 3000), "NoBackgroundRawWriter");
                raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally { Stop(raw); batch.Dispose(); }
        }

        private static void Failure(string root)
        {
            var faults = 0; var warnings = 0; using var signaled = new ManualResetEventSlim();
            var raw = Create(root, true, fault: ex => { Interlocked.Increment(ref faults); signaled.Set(); }, warning: message => Interlocked.Increment(ref warnings));
            var batch = Batch("Dev1", 2, 2, 1);
            try
            {
                Directory.CreateDirectory(RawFile(raw, "Dev1")); raw.Enqueue(batch);
                Assert(signaled.Wait(3000), "RawIoFailureWasSwallowed");
                Assert(batch.Values[0] == 1 && faults == 1, "FailedWriteLostOwnedBatch");
            }
            finally { Stop(raw); }
            Assert(faults == 1 && warnings == 1, "RawFailureStormOrUnreportedAbandon");
            Expect<ObjectDisposedException>(() => { var values = batch.Values; });
        }

        private static void LateIo(string root)
        {
            var context = new DaqAIContext("Dev1", 256, 60, 1, 1, 1, root);
            var original = new TaskCompletionSource<bool>();
            var method = typeof(DaqAIContext).GetMethod("RunWithDeadlineAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task)method.Invoke(context, new object[] { new Func<CancellationToken, Task>(_ => original.Task), 30, CancellationToken.None, "synthetic stuck I/O" });
            Expect<TimeoutException>(() => task.GetAwaiter().GetResult());
            Assert(!context.WaitForIoQuiescenceAsync(30).GetAwaiter().GetResult(), "DeadlinePretendedRealIoHadStopped");
            Expect<InvalidOperationException>(() => context.ReleasePendingRawAfterShutdown());
            original.SetResult(true);
            Assert(context.WaitForIoQuiescenceAsync(1000).GetAwaiter().GetResult(), "CompletedIoNeverQuiesced");
            Assert(context.ReleasePendingRawAfterShutdown() == 0, "EmptyContextDiscardedPhantomBatch");
        }

        private static void Cancellation(string root)
        {
            var raw = Create(root); var batch = Batch("Dev1", 2, 2, 5);
            try
            {
                raw.Enqueue(batch); using var canceled = new CancellationTokenSource(); canceled.Cancel();
                Expect<OperationCanceledException>(() => raw.FlushBoundaryAsync(Prefix(), Drained, canceled.Token).GetAwaiter().GetResult());
                Assert(!File.Exists(RawFile(raw, "Dev1")) && batch.Values[0] == 5, "CanceledFlushClaimedOrLostRaw");
                raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult();
            }
            finally { Stop(raw); batch.Dispose(); }
        }

        private static void StatFailure(string root)
        {
            var raw = Create(root);
            try
            {
                var path = Path.Combine(raw.RootDirectory, "DAQ_Dev1_Stat.bin"); Directory.CreateDirectory(path);
                raw.Enqueue(Batch("Dev1", 2, 10, 1));
                Expect<UnauthorizedAccessException>(() => raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult());
                Directory.Move(path, path + ".obstruction");
                raw.FlushBoundaryAsync(Prefix(), Drained, CancellationToken.None).GetAwaiter().GetResult();
                var bytes = File.ReadAllBytes(path);
                Assert(bytes.Length == 44 && BitConverter.ToInt32(bytes, 0) == 1, "FailedStatDiscardedAggregateOrAdvancedCounter");
            }
            finally { Stop(raw); }
        }

        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
    }
}
