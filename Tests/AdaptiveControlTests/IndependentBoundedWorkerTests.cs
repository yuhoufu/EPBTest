using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentBoundedWorkerTests
    {
        internal static int Child(string mode, string evidence)
        {
            if (mode == "exit") return 0;
            if (mode == "fail") return 17;
            if (mode == "owner")
            {
                // Intentionally skip Dispose: killing the owner must retire its
                // hanging native worker through kernel handle cleanup.
                var worker = new IndependentBoundedWorker(Executable,
                    "--independent-worker-child hang \"" + evidence + "\"", Path.GetDirectoryName(Executable), 300000);
                WriteIdentity(evidence, worker.ProcessId, worker.StartUtcTicks);
                Thread.Sleep(Timeout.Infinite);
                GC.KeepAlive(worker);
            }
            if (mode == "tree")
            {
                using (var child = Process.Start(new ProcessStartInfo(Executable,
                    "--independent-worker-child hang \"" + evidence + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    WriteIdentity(evidence, child.Id, child.StartTime.ToUniversalTime().Ticks);
                    Thread.Sleep(Timeout.Infinite);
                }
            }
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        private static string Executable => Process.GetCurrentProcess().MainModule.FileName;
        private static void WriteIdentity(string path, int pid, long ticks)
        {
            File.WriteAllText(path + ".tmp", pid + "|" + ticks);
            File.Move(path + ".tmp", path);
        }
        private static bool Gone(int pid, long ticks)
        {
            try { using (var p = Process.GetProcessById(pid)) return p.HasExited || p.StartTime.ToUniversalTime().Ticks != ticks; }
            catch (ArgumentException) { return true; }
        }
        private static void Until(Func<bool> condition, string reason)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            { if (clock.ElapsedMilliseconds > 10000) throw new Exception(reason); Thread.Sleep(20); }
        }
        internal static int RunAll()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ??
                Path.GetTempPath(), "independent-workers-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            IndependentBoundedWorker Start(string mode, string path, int deadline) => new IndependentBoundedWorker(
                Executable, "--independent-worker-child " + mode + " \"" + path + "\"", root, deadline);
            using (var success = Start("exit", root, 10000))
            {
                Until(() => success.Poll() != IndependentWorkerState.Running, "successful worker hung");
                if (success.Poll() != IndependentWorkerState.Completed) throw new Exception("success not reported");
            }
            using (var failed = Start("fail", root, 10000))
            {
                Until(() => failed.Poll() != IndependentWorkerState.Running, "failed worker hung");
                if (failed.Poll() != IndependentWorkerState.Failed || failed.ExitCode != 17)
                    throw new Exception("failure converted to safety success");
            }
            using (var hung = Start("hang", root, 200))
            {
                Until(() => hung.Poll() == IndependentWorkerState.TimedOut, "native hang not bounded");
                Until(() => Gone(hung.ProcessId, hung.StartUtcTicks), "timed out worker survived");
            }
            var treePath = Path.Combine(root, "tree.txt");
            using (var tree = Start("tree", treePath, 20000))
            {
                Until(() => File.Exists(treePath) && new FileInfo(treePath).Length > 0, "descendant did not start");
                var identity = File.ReadAllText(treePath).Split('|');
                tree.Dispose();
                Until(() => Gone(int.Parse(identity[0]), long.Parse(identity[1])), "descendant survived job close");
                Until(() => Gone(tree.ProcessId, tree.StartUtcTicks), "parent survived job close");
            }
            var ownerPath = Path.Combine(root, "owner.txt");
            using (var owner = Process.Start(new ProcessStartInfo(Executable,
                "--independent-worker-child owner \"" + ownerPath + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                try
                {
                    Until(() => File.Exists(ownerPath) && new FileInfo(ownerPath).Length > 0, "owner worker did not start");
                    var identity = File.ReadAllText(ownerPath).Split('|');
                    owner.Kill();
                    if (!owner.WaitForExit(5000)) throw new Exception("owner did not exit");
                    Until(() => Gone(int.Parse(identity[0]), long.Parse(identity[1])), "worker survived executor crash");
                }
                finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } }
            }
            return 5;
        }
    }
}
