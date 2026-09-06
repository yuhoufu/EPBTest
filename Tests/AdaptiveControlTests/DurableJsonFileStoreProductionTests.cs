using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    /// <summary>
    /// Acceptance tests for the production DurableJsonFileStore.  The only
    /// injected pieces are final file edges, monotonic time, and mutex
    /// admission; the transaction/CAS/retry algorithm remains production.
    /// </summary>
    internal static class DurableJsonFileStoreProductionTests
    {
        internal static int RunAll()
        {
            var passed = 0;
            Run("16 writer/8 reader真实并发", ConcurrentWritersReaders, ref passed);
            Run("8 child进程各125次真实文件写", ChildWriters, ref passed);
            Run("共享句柄占用后Busy/释放重试", HeldSharedHandles, ref passed);
            Run("真实named mutex Busy后重试", MutexBusyRetry, ref passed);
            Run("Replace暂态失败在共享期限内重试", ReplaceRetryDeadline, ref passed);
            Run("Create/Replace/readback/flush故障矩阵", FailureMatrix, ref passed);
            Run("last-good与非法JSON保持安全", LastGoodAndInvalidJson, ref passed);
            Run("精确child PID/start回收", ExactChildCrashCleanup, ref passed);
            Run("owned temp隔离且不删除target/bak", CleanupIsolation, ref passed);
            Run("void兼容API与typed API", Compatibility, ref passed);
            return passed;
        }

        internal static int RunStress()
        {
            ConcurrentWritersReaders();
            ChildWriters();
            Console.WriteLine("DURABLE_JSON_STRESS_PASS=2");
            return 2;
        }

        internal static int RunTail()
        {
            var passed = 0;
            Run("共享句柄占用后Busy/释放重试", HeldSharedHandles, ref passed);
            Run("真实named mutex Busy后重试", MutexBusyRetry, ref passed);
            Run("Replace暂态失败在共享期限内重试", ReplaceRetryDeadline, ref passed);
            Run("Create/Replace/readback/flush故障矩阵", FailureMatrix, ref passed);
            Run("last-good与非法JSON保持安全", LastGoodAndInvalidJson, ref passed);
            Run("精确child PID/start回收", ExactChildCrashCleanup, ref passed);
            Run("owned temp隔离且不删除target/bak", CleanupIsolation, ref passed);
            Run("void兼容API与typed API", Compatibility, ref passed);
            return passed;
        }

        internal static int RunChild(string[] args)
        {
            try
            {
                if (args.Length >= 2 && args[1].Equals("crash", StringComparison.OrdinalIgnoreCase))
                {
                    Thread.Sleep(60000);
                    return 0;
                }
                if (args.Length >= 5 && args[1].Equals("crash-phase", StringComparison.OrdinalIgnoreCase))
                    return RunCrashPhaseChild(args);
                if (args.Length < 4) return 2;
                var directory = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
                var start = int.Parse(args[2]);
                var count = int.Parse(args[3]);
                var path = Path.Combine(directory, "shared.json");
                for (var i = 0; i < count; i++)
                {
                    var revision = start + i;
                    var bytes = Encoding.UTF8.GetBytes("{\"Revision\":" + revision +
                        ",\"Pid\":" + Process.GetCurrentProcess().Id + "}");
                    var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, bytes);
                    if (!result.Succeeded) return 10;
                }
                return 0;
            }
            catch { return 11; }
        }

        private static int RunCrashPhaseChild(string[] args)
        {
            var directory = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
            var phase = (DurableJsonWritePhase)Enum.Parse(
                typeof(DurableJsonWritePhase), args[3], true);
            var token = args[4];
            var readyName = CrashEventName("ready", token);
            var releaseName = CrashEventName("release", token);
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName))
            using (var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName))
            {
                var path = Path.Combine(directory, "crash.json");
                var signal = new NamedPhaseSignal(phase, ready, release);
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(
                    path,
                    Json(2, phase.ToString()),
                    new WindowsDurableJsonFileIo(),
                    new SystemDurableJsonClock(),
                    null,
                    signal);
                return result.Succeeded ? 0 : 12;
            }
        }

        private static void ConcurrentWritersReaders()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "shared.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(0, "seed"));
                var errors = 0;
                var nextRevision = 0;
                var writers = Enumerable.Range(0, 16).Select(writer => Task.Run(() =>
                {
                    for (var i = 0; i < 250; i++)
                    {
                        var revision = Interlocked.Increment(ref nextRevision);
                        var written = false;
                        for (var retry = 0; retry < 12 && !written; retry++)
                        {
                            var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(revision, "w" + writer));
                            written = result.Succeeded;
                            if (!written) Thread.Sleep(5);
                        }
                        if (!written) Interlocked.Increment(ref errors);
                    }
                })).ToArray();
                var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(() =>
                {
                    var local = 0;
                    while (writers.Any(x => !x.IsCompleted) || local < 300)
                    {
                        var valid = false;
                        for (var retry = 0; retry < 4 && !valid; retry++)
                        {
                            var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                                x => x.Revision, path, path + ".bak");
                            valid = read.Value != null && read.Status == DurableJsonReadStatus.Valid;
                            if (!valid) Thread.Sleep(2);
                        }
                        if (!valid)
                            Interlocked.Increment(ref errors);
                        local++;
                    }
                })).ToArray();
                Task.WaitAll(writers);
                Task.WaitAll(readers);
                var final = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(x => x.Revision, path, path + ".bak");
                Assert(errors == 0 && final.Value != null && final.Status == DurableJsonReadStatus.Valid,
                    "16x250/8-reader errors=" + errors);
            }
            finally { TryDelete(directory); }
        }

        private static void ChildWriters()
        {
            var directory = TempDirectory();
            var children = new List<Process>();
            CancellationTokenSource cancellation = null;
            Task reader = null;
            var invalidReads = 0;
            try
            {
                Directory.CreateDirectory(directory);
                var sharedPath = Path.Combine(directory, "shared.json");
                DurableJsonFileStore.WriteAtomicWithBackup(sharedPath, Json(0, "seed"));
                cancellation = new CancellationTokenSource();
                reader = Task.Run(() =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                            x => x.Revision, sharedPath, sharedPath + ".bak");
                        if (read.Status != DurableJsonReadStatus.Valid || read.Value == null)
                            Interlocked.Increment(ref invalidReads);
                    }
                });
                var executable = Process.GetCurrentProcess().MainModule.FileName;
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
                for (var child = 0; child < 8; child++)
                {
                    var info = new ProcessStartInfo(executable,
                        "--durable-json-store-child " + encoded + " " + (child * 125) + " 125")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    var process = Process.Start(info);
                    Assert(process != null, "child process did not start");
                    children.Add(process);
                }
                foreach (var child in children)
                {
                    Assert(child.WaitForExit(120000), "child writer timeout pid=" + child.Id);
                    var error = child.StandardError.ReadToEnd();
                    Assert(child.ExitCode == 0, "child writer exit=" + child.ExitCode + " " + error);
                    child.Dispose();
                }
                cancellation.Cancel();
                Assert(reader.Wait(10000), "parent reader did not stop after child writers");
                var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                    x => x.Revision, Path.Combine(directory, "shared.json"), Path.Combine(directory, "shared.json.bak"));
                Assert(read.Status == DurableJsonReadStatus.Valid && read.Value != null && invalidReads == 0,
                    "child writers/readers observed an invalid window: " + invalidReads);
            }
            finally
            {
                cancellation?.Cancel();
                try { reader?.Wait(10000); } catch { }
                foreach (var child in children)
                {
                    try { if (!child.HasExited) child.Kill(); } catch { }
                    try { child.Dispose(); } catch { }
                }
                TryDelete(directory);
            }
        }

        private static void HeldSharedHandles()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "held.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "blocked"));
                    Assert(!result.Succeeded &&
                           (result.Status == DurableJsonWriteStatus.Failed || result.Status == DurableJsonWriteStatus.OutcomeUnknown),
                        "held shared handle was silently treated as success");
                }
                var retry = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "released"));
                Assert(retry.Succeeded, "released shared handle did not permit retry: " + retry.Status + "/" + retry.Detail);
                using (var primary = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var backup = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    var shared = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(3, "shared"));
                    var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                        x => x.Revision, path, path + ".bak");
                    Assert(shared.Succeeded && read.Value?.Revision == 3 &&
                           read.Status == DurableJsonReadStatus.Valid,
                        "shared read/delete handles blocked a complete generation: " + shared.Status);
                }
            }
            finally { TryDelete(directory); }
        }

        private static void MutexBusyRetry()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "mutex.json");
                var io = new WindowsDurableJsonFileIo();
                var clock = new ManualClock();
                var admissions = 0;
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(1, "retry"), io, clock,
                    timeout => Interlocked.Increment(ref admissions) <= 2 ? false : true);
                Assert(result.Succeeded && admissions >= 3 && clock.Sleeps > 0,
                    "mutex Busy retry did not recover: " + result.Status + "/" + admissions);

                using (var acquired = new ManualResetEventSlim(false))
                using (var release = new ManualResetEventSlim(false))
                {
                    var holder = Task.Run(() =>
                    {
                        using (var held = new Mutex(false, DurableJsonFileStore.CanonicalWriterMutexName(path)))
                        {
                            held.WaitOne();
                            acquired.Set();
                            release.Wait();
                            held.ReleaseMutex();
                        }
                    });
                    Assert(acquired.Wait(1000), "real canonical writer mutex was not acquired");
                    var busy = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "busy"));
                    Assert(busy.Status == DurableJsonWriteStatus.Busy && !busy.Succeeded,
                        "real held mutex did not return Busy: " + busy.Status);
                    release.Set();
                    Assert(holder.Wait(5000), "real mutex holder did not release");
                }
                var after = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "released"));
                Assert(after.Succeeded, "real mutex release did not permit retry: " + after.Status);
            }
            finally { TryDelete(directory); }
        }

        private static void ReplaceRetryDeadline()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "replace.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var io = new RecordingIo { FailReplaceCount = 3 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(result.Succeeded && io.ReplaceCalls >= 4,
                    "replace retry did not complete within shared transaction: " + result.Status + "/" + io.ReplaceCalls);

                var throwingIo = new RecordingIo { ThrowAfterReplaceCount = 1 };
                var converged = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(3, "os-replace-then-throw"),
                    throwingIo, new ManualClock(), null);
                var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                    x => x.Revision, path, path + ".bak");
                Assert(converged.Succeeded && read.Value?.Revision == 3 &&
                       read.Status == DurableJsonReadStatus.Valid,
                    "successful OS replace followed by injected throw did not converge: " + converged.Status);
            }
            finally { TryDelete(directory); }
        }

        private static void FailureMatrix()
        {
            RunCreateFailureCase();
            RunFlushFailureCase();
            RunReplaceFailureCase();
            RunReadbackUnknownCase();
            RunFinalVerificationAndTempReadbackCase();
        }

        private static void RunCreateFailureCase()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "create.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var before = File.ReadAllBytes(path);
                var io = new RecordingIo { FailCreateCount = 1000 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(!result.Succeeded && result.FailureKind == DurableJsonWriteFailureKind.CreateFailed &&
                       File.ReadAllBytes(path).SequenceEqual(before),
                    "create failure changed primary bytes");
            }
            finally { TryDelete(directory); }
        }

        private static void RunFlushFailureCase()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "flush.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var before = File.ReadAllBytes(path);
                var io = new RecordingIo { FailFlushCount = 1000 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(!result.Succeeded && result.FailureKind == DurableJsonWriteFailureKind.FlushFailed &&
                       File.ReadAllBytes(path).SequenceEqual(before),
                    "flush failure changed primary bytes");
            }
            finally { TryDelete(directory); }
        }

        private static void RunReplaceFailureCase()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "replace-failure.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var before = File.ReadAllBytes(path);
                var io = new RecordingIo { FailReplaceCount = 1000 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(!result.Succeeded && result.FailureKind == DurableJsonWriteFailureKind.ReplaceFailed &&
                       File.ReadAllBytes(path).SequenceEqual(before),
                    "replace failure changed primary bytes");
            }
            finally { TryDelete(directory); }
        }

        private static void RunReadbackUnknownCase()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "readback.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var io = new RecordingIo { FailReadCount = 1000 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(result.Status == DurableJsonWriteStatus.OutcomeUnknown && result.OutcomeUnknown &&
                       result.FailureKind == DurableJsonWriteFailureKind.ReadFailed,
                    "unobservable readback did not return OutcomeUnknown: " + result.Status);
            }
            finally { TryDelete(directory); }
        }

        private static void RunFinalVerificationAndTempReadbackCase()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "verification.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var corruptBackup = new DelegatePhaseSignal((phase, target, temporary) =>
                {
                    if (phase == DurableJsonWritePhase.PrimaryAfterPublish)
                        File.WriteAllText(target + ".bak", "{broken", new UTF8Encoding(false));
                });
                var corrupt = DurableJsonFileStore.TryWriteAtomicWithBackup(
                    path, Json(2, "corrupt-backup"), new WindowsDurableJsonFileIo(),
                    new ManualClock(), null, corruptBackup);
                Assert(!corrupt.Succeeded && corrupt.Status == DurableJsonWriteStatus.Failed &&
                       corrupt.FailureKind == DurableJsonWriteFailureKind.ReadbackFailed &&
                       corrupt.Detail.StartsWith("VerificationFailed:", StringComparison.Ordinal),
                    "corrupt final backup was committed: " + corrupt.Status + "/" + corrupt.Detail);

                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var unknownIo = new RecordingIo();
                var unknownBackup = new DelegatePhaseSignal((phase, target, temporary) =>
                {
                    if (phase == DurableJsonWritePhase.PrimaryAfterPublish)
                        unknownIo.FailReadCount = 100000;
                });
                var unknown = DurableJsonFileStore.TryWriteAtomicWithBackup(
                    path, Json(2, "unknown-backup"), unknownIo, new ManualClock(), null, unknownBackup);
                Assert(unknown.Status == DurableJsonWriteStatus.OutcomeUnknown && unknown.OutcomeUnknown,
                    "unknown final backup was not fail-closed: " + unknown.Status);

                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                File.WriteAllText(path + ".bak", "{broken", new UTF8Encoding(false));
                var before = File.ReadAllBytes(path);
                var shortTemp = new RecordingIo
                {
                    TemporaryOverride = Encoding.UTF8.GetBytes("{\"")
                };
                var shortResult = DurableJsonFileStore.TryWriteAtomicWithBackup(
                    path, Json(2, "short-temp"), shortTemp, new ManualClock(), null);
                Assert(!shortResult.Succeeded &&
                       File.ReadAllBytes(path).SequenceEqual(before) &&
                       File.ReadAllText(path + ".bak") == "{broken",
                    "short/mismatched temporary was published");

                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                File.WriteAllText(path + ".bak", "{broken", new UTF8Encoding(false));
                var hashTemp = new RecordingIo
                {
                    TemporaryOverride = Json(999, "wrong-hash")
                };
                var hashResult = DurableJsonFileStore.TryWriteAtomicWithBackup(
                    path, Json(2, "hash-temp"), hashTemp, new ManualClock(), null);
                Assert(!hashResult.Succeeded && File.ReadAllBytes(path).SequenceEqual(before),
                    "hash-mismatched temporary was published");
            }
            finally { TryDelete(directory); }
        }

        private static void LastGoodAndInvalidJson()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "last-good.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "v1"));
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(2, "v2"));
                File.WriteAllText(path, "{broken", new UTF8Encoding(false));
                var read = DurableJsonFileStore.ReadFirstValid<RevisionProbe>(path, path + ".bak");
                Assert(read.Value != null && read.Value.Revision == 1 &&
                       read.Attempts.Count >= 2 && read.Attempts[0].Status == DurableJsonReadStatus.JsonInvalid,
                    "last-good backup was not selected after invalid primary");
                var before = File.ReadAllBytes(path);
                var invalid = DurableJsonFileStore.TryWriteAtomicWithBackup(path,
                    Encoding.UTF8.GetBytes("{not-json"));
                Assert(invalid.Status == DurableJsonWriteStatus.InvalidJson && File.ReadAllBytes(path).SequenceEqual(before),
                    "invalid JSON write altered primary");
            }
            finally { TryDelete(directory); }
        }

        private static void ExactChildCrashCleanup()
        {
            var executable = Process.GetCurrentProcess().MainModule.FileName;
            var info = new ProcessStartInfo(executable, "--durable-json-store-child crash")
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var child = Process.Start(info))
            {
                Assert(child != null, "crash child did not start");
                var pid = child.Id;
                var started = child.StartTime;
                Thread.Sleep(50);
                try { child.Kill(); } catch { }
                Assert(child.WaitForExit(5000), "exact crash child did not exit");
                // Only the exact child was touched; retaining the captured
                // PID/start proof makes accidental broad process cleanup
                // impossible in this test.
                Assert(pid > 0 && started != default(DateTime), "child identity was not captured");
            }
            CrashAtProductionPhases();
        }

        private static void CrashAtProductionPhases()
        {
            var phases = new[]
            {
                DurableJsonWritePhase.BackupAfterFlush,
                DurableJsonWritePhase.BackupAfterPublish,
                DurableJsonWritePhase.PrimaryAfterFlush,
                DurableJsonWritePhase.PrimaryAfterPublish
            };
            foreach (var phase in phases)
            {
                var directory = TempDirectory();
                Process child = null;
                RecordingIo recoveryIo = null;
                string otherTargetTemp = null;
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, "crash.json");
                    DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "baseline"));
                    if (phase == DurableJsonWritePhase.BackupAfterFlush ||
                        phase == DurableJsonWritePhase.BackupAfterPublish)
                        File.Delete(path + ".bak");
                    if (phase == DurableJsonWritePhase.BackupAfterFlush)
                    {
                        var otherPath = Path.Combine(directory, "other-target.json");
                        otherTargetTemp = OwnedTempPath(otherPath, "primary");
                        File.WriteAllBytes(otherTargetTemp, Json(7, "other-target-temp"));
                    }

                    var token = Guid.NewGuid().ToString("N");
                    using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
                               CrashEventName("ready", token)))
                    using (var release = new EventWaitHandle(false, EventResetMode.ManualReset,
                               CrashEventName("release", token)))
                    {
                        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(directory));
                        var info = new ProcessStartInfo(
                            Process.GetCurrentProcess().MainModule.FileName,
                            "--durable-json-store-child crash-phase " + encoded + " " +
                            phase + " " + token)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        child = Process.Start(info);
                        Assert(child != null, "crash phase child did not start: " + phase);
                        var pid = child.Id;
                        var startTicks = child.StartTime.Ticks;
                        Assert(ready.WaitOne(10000), "crash phase signal timeout: " + phase);
                        Assert(Process.GetProcessById(pid).StartTime.Ticks == startTicks,
                            "crash phase identity changed before kill: " + phase);
                        if (phase == DurableJsonWritePhase.BackupAfterFlush)
                        {
                            var targetTempsBeforeKill = OwnedTempFiles(directory, path);
                            Assert(targetTempsBeforeKill.Length == 1 &&
                                   targetTempsBeforeKill[0].IndexOf("-backup-", StringComparison.Ordinal) >= 0,
                                "BackupAfterFlush did not produce its owned temp before exact kill");
                        }
                        child.Kill();
                        Assert(child.WaitForExit(10000), "crash phase child did not exit: " + phase);
                        release.Set();
                        child.Dispose();
                        child = null;
                    }

                    if (phase == DurableJsonWritePhase.BackupAfterFlush)
                        recoveryIo = new RecordingIo();
                    var recovery = recoveryIo == null
                        ? DurableJsonFileStore.TryWriteAtomicWithBackup(
                            path, Json(3, "recovered-" + phase))
                        : DurableJsonFileStore.TryWriteAtomicWithBackup(
                            path, Json(3, "recovered-" + phase), recoveryIo,
                            new SystemDurableJsonClock(), null);
                    var read = DurableJsonFileStore.ReadLatestValid<RevisionProbe>(
                        x => x.Revision, path, path + ".bak");
                    Assert(recovery.Succeeded && read.Value?.Revision == 3 &&
                           read.Status == DurableJsonReadStatus.Valid,
                        "abandoned mutex/last-good recovery failed: " + phase + "/" + recovery.Status);
                    if (phase == DurableJsonWritePhase.BackupAfterFlush)
                    {
                        Assert(OwnedTempFiles(directory, path).Length == 0,
                            "recovery left the crashed target temp behind");
                        Assert(!string.IsNullOrEmpty(otherTargetTemp) && File.Exists(otherTargetTemp),
                            "recovery removed another target's temp");
                        Assert(File.Exists(path) && File.Exists(path + ".bak"),
                            "recovery removed primary or backup");
                        Assert(recoveryIo.Deleted.All(x =>
                                !string.Equals(Path.GetFullPath(x), Path.GetFullPath(path),
                                    StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(Path.GetFullPath(x), Path.GetFullPath(path + ".bak"),
                                    StringComparison.OrdinalIgnoreCase)),
                            "recovery entered primary/backup into temp deletion");
                    }
                }
                finally
                {
                    try { if (child != null && !child.HasExited) child.Kill(); } catch { }
                    try { if (child != null) child.WaitForExit(2000); } catch { }
                    try { child?.Dispose(); } catch { }
                    TryDelete(directory);
                }
            }
        }

        private static string CrashEventName(string kind, string token) =>
            "Local\\MTTFTest-DurableJsonCrash-" + kind + "-" + token;

        private static string TargetHash(string path) => DurableJsonFileStore.ComputeSha256(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));

        private static string[] OwnedTempFiles(string directory, string targetPath)
        {
            var prefix = ".durable-json-" + TargetHash(targetPath) + "-";
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, prefix + "*.tmp")
                : Array.Empty<string>();
        }

        private static string OwnedTempPath(string targetPath, string phase)
        {
            var directory = Path.GetDirectoryName(targetPath);
            return Path.Combine(directory,
                ".durable-json-" + TargetHash(targetPath) + "-pid999-" + phase + "-" +
                Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void CleanupIsolation()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "cleanup.json");
                DurableJsonFileStore.WriteAtomicWithBackup(path, Json(1, "old"));
                var backup = path + ".bak";
                var targetHash = DurableJsonFileStore.ComputeSha256(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
                var otherPath = Path.Combine(directory, "other.json");
                var otherHash = DurableJsonFileStore.ComputeSha256(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(otherPath).ToUpperInvariant()));
                var ownedStale = Path.Combine(directory,
                    ".durable-json-" + targetHash + "-pid999-backup-stale.tmp");
                var otherStale = Path.Combine(directory,
                    ".durable-json-" + otherHash + "-pid999-primary-stale.tmp");
                File.WriteAllBytes(ownedStale, Json(0, "stale"));
                File.WriteAllBytes(otherStale, Json(0, "other"));
                var io = new RecordingIo { FailReplaceCount = 1000 };
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, Json(2, "new"), io,
                    new ManualClock(), null);
                Assert(!result.Succeeded && File.Exists(path) && File.Exists(backup),
                    "failure removed primary or backup");
                Assert(!File.Exists(ownedStale) && File.Exists(otherStale),
                    "cleanup removed the wrong target temp or retained the owned stale temp");
                Assert(io.Deleted.All(x => Path.GetFileName(x).StartsWith(".durable-json-", StringComparison.Ordinal)) &&
                       io.Deleted.All(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) &&
                       io.Deleted.All(x => !string.Equals(Path.GetFullPath(x), Path.GetFullPath(backup), StringComparison.OrdinalIgnoreCase)),
                    "cleanup deleted a non-owned path");
            }
            finally { TryDelete(directory); }
        }

        private static void Compatibility()
        {
            var directory = TempDirectory();
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "compat.json");
                var first = Json(1, "void");
                DurableJsonFileStore.WriteAtomicWithBackup(path, first);
                var second = Json(2, "typed");
                var result = DurableJsonFileStore.TryWriteAtomicWithBackup(path, second);
                Assert(result.Succeeded && result.Sha256 == DurableJsonFileStore.ComputeSha256(second),
                    "typed API did not report exact SHA");
                var threw = false;
                try { DurableJsonFileStore.WriteAtomicWithBackup(path, Encoding.UTF8.GetBytes("bad")); }
                catch (DurableJsonFileStoreException) { threw = true; }
                Assert(threw, "void compatibility API did not surface typed exception");
            }
            finally { TryDelete(directory); }
        }

        private static byte[] Json(int revision, string writer) =>
            Encoding.UTF8.GetBytes("{\"Revision\":" + revision + ",\"Writer\":\"" + writer + "\"}");

        private static string TempDirectory() => Path.Combine(Path.GetTempPath(),
            "epb-durable-json-" + Guid.NewGuid().ToString("N"));

        private static void TryDelete(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
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

        private sealed class RevisionProbe
        {
            public int Revision { get; set; }
            public string Writer { get; set; }
        }

        private sealed class ManualClock : IDurableJsonClock
        {
            public long Timestamp { get; private set; }
            public long Frequency => 1000;
            public int Sleeps { get; private set; }
            public void Sleep(int milliseconds)
            {
                Sleeps++;
                Timestamp += Math.Max(1, milliseconds);
            }
        }

        private sealed class NamedPhaseSignal : IDurableJsonPhaseSignal
        {
            private readonly DurableJsonWritePhase _phase;
            private readonly EventWaitHandle _ready;
            private readonly EventWaitHandle _release;

            internal NamedPhaseSignal(
                DurableJsonWritePhase phase,
                EventWaitHandle ready,
                EventWaitHandle release)
            {
                _phase = phase;
                _ready = ready;
                _release = release;
            }

            public void Signal(DurableJsonWritePhase phase, string targetPath, string temporaryPath)
            {
                if (phase != _phase) return;
                _ready.Set();
                _release.WaitOne(60000);
            }
        }

        private sealed class DelegatePhaseSignal : IDurableJsonPhaseSignal
        {
            private readonly Action<DurableJsonWritePhase, string, string> _callback;

            internal DelegatePhaseSignal(Action<DurableJsonWritePhase, string, string> callback)
            {
                _callback = callback;
            }

            public void Signal(DurableJsonWritePhase phase, string targetPath, string temporaryPath)
            {
                _callback(phase, targetPath, temporaryPath);
            }
        }

        private sealed class RecordingIo : IDurableJsonFileIo
        {
            private readonly WindowsDurableJsonFileIo _inner = new WindowsDurableJsonFileIo();
            internal int FailCreateCount;
            internal int FailFlushCount;
            internal int FailReplaceCount;
            internal int FailReadCount;
            internal int ReplaceCalls;
            internal int ThrowAfterReplaceCount;
            internal byte[] TemporaryOverride;
            internal readonly List<string> Deleted = new List<string>();

            public bool Exists(string path) => _inner.Exists(path);

            public byte[] ReadAllBytes(string path)
            {
                if (FailReadCount > 0)
                {
                    FailReadCount--;
                    throw new IOException("InjectedReadFailure");
                }
                return _inner.ReadAllBytes(path);
            }

            public void CreateAndFlush(string path, byte[] bytes)
            {
                if (FailCreateCount > 0)
                {
                    FailCreateCount--;
                    throw new IOException("InjectedCreateFailure");
                }
                if (FailFlushCount > 0)
                {
                    FailFlushCount--;
                    throw new IOException("InjectedFlushFailure");
                }
                _inner.CreateAndFlush(path, bytes);
                if (TemporaryOverride != null)
                    File.WriteAllBytes(path, TemporaryOverride);
            }

            public void ReplaceNoBackup(string temporaryPath, string targetPath)
            {
                ReplaceCalls++;
                if (FailReplaceCount > 0)
                {
                    FailReplaceCount--;
                    throw new IOException("InjectedReplaceFailure");
                }
                _inner.ReplaceNoBackup(temporaryPath, targetPath);
                if (ThrowAfterReplaceCount > 0)
                {
                    ThrowAfterReplaceCount--;
                    throw new IOException("InjectedAfterReplaceFailure");
                }
            }

            public void MoveNew(string temporaryPath, string targetPath) => _inner.MoveNew(temporaryPath, targetPath);

            public void DeleteOwnedTemp(string path)
            {
                Deleted.Add(path);
                _inner.DeleteOwnedTemp(path);
            }

            public IEnumerable<string> EnumerateOwnedTemps(string directory, string canonicalTargetHash) =>
                _inner.EnumerateOwnedTemps(directory, canonicalTargetHash);
        }
    }
}
