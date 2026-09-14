using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class IndependentBoundedWorkerTests
    {
        public sealed class FenceProbe
        {
            public string InstallationId { get; set; }
            public IndependentProcessIdentity Identity { get; set; }
        }
        internal static int Child(string mode, string evidence)
        {
            if (mode == "exit") return 0;
            if (mode == "started") { File.WriteAllText(evidence, "executed"); return 0; }
            if (mode == "fail") return 17;
            if (mode == "lease")
            {
                var installationId = Guid.NewGuid().ToString("N");
                var lease = new IndependentExecutorLease(Path.GetDirectoryName(evidence), installationId);
                File.WriteAllText(evidence, installationId);
                Thread.Sleep(Timeout.Infinite);
                GC.KeepAlive(lease);
            }
            if (mode == "fence")
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var probe = new FenceProbe
                    {
                        InstallationId = Guid.NewGuid().ToString("N"),
                        Identity = new IndependentProcessIdentity
                        { Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                            WindowsSessionId = process.SessionId, ExecutablePath = Executable,
                            SessionToken = Guid.NewGuid().ToString("N") }
                    };
                    IndependentExecutionFence.AttachCurrent(probe.InstallationId, probe.Identity);
                    BoundedJson.Write(evidence, probe);
                    while (true)
                    {
                        try { IndependentExecutionFence.RequireCurrentAuthority(); }
                        catch (InvalidOperationException)
                        {
                            // Original power-supply boundary must observe the same fence.
                            try { FallbackPowerBoundary.ValidateCurrentProcess(); }
                            catch (InvalidOperationException) { return 0; }
                            return 19;
                        }
                        Thread.Sleep(10);
                    }
                }
            }
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
            var admittedPath = Path.Combine(root, "admitted.txt");
            var registrationObserved = false;
            using (var admitted = new IndependentBoundedWorker(Executable,
                "--independent-worker-child started \"" + admittedPath + "\"", Path.GetDirectoryName(Executable), 10000,
                beforeResume: (pid, ticks) =>
                {
                    using (var suspended = Process.GetProcessById(pid))
                        if (suspended.StartTime.ToUniversalTime().Ticks != ticks)
                            throw new Exception("suspended child identity mismatch");
                    if (File.Exists(admittedPath)) throw new Exception("child executed before registration");
                    registrationObserved = true;
                }))
            {
                Until(() => admitted.Poll() != IndependentWorkerState.Running, "registered child did not finish");
                if (!registrationObserved || admitted.Poll() != IndependentWorkerState.Completed || !File.Exists(admittedPath))
                    throw new Exception("registered child did not execute after admission");
            }
            var rejectedPath = Path.Combine(root, "rejected.txt");
            var rejectedPid = 0; long rejectedTicks = 0;
            var rejectedRegistration = false;
            try
            {
                using (var rejectedChild = new IndependentBoundedWorker(Executable,
                    "--independent-worker-child started \"" + rejectedPath + "\"", Path.GetDirectoryName(Executable), 10000,
                    beforeResume: (pid, ticks) =>
                    {
                        rejectedPid = pid; rejectedTicks = ticks;
                        throw new InvalidOperationException("InjectedRegistrationRejected");
                    })) { }
            }
            catch (InvalidOperationException error) when (error.Message == "InjectedRegistrationRejected")
            { rejectedRegistration = true; }
            if (!rejectedRegistration || rejectedPid == 0) throw new Exception("registration rejection not observed");
            Until(() => Gone(rejectedPid, rejectedTicks), "rejected suspended child survived");
            if (File.Exists(rejectedPath)) throw new Exception("rejected child executed");
            using (var session = IndependentBoundedWorker.StartSession(Executable,
                "--independent-worker-child hang \"" + root + "\"", Path.GetDirectoryName(Executable), (pid, ticks) => { }))
            {
                Thread.Sleep(10200);
                if (session.Poll() != IndependentWorkerState.Running)
                    throw new Exception("long-lived session inherited the registration deadline");
                session.Dispose();
                Until(() => Gone(session.ProcessId, session.StartUtcTicks), "session survived lifetime owner disposal");
            }
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
            using (var target = Start("hang", root, 20000))
            using (var unrelated = Start("hang", root, 20000))
            {
                var session = Process.GetCurrentProcess().SessionId;
                var identity = new IndependentProcessIdentity
                { Pid = target.ProcessId, StartUtcTicks = target.StartUtcTicks, WindowsSessionId = session,
                    ExecutablePath = Executable, SessionToken = Guid.NewGuid().ToString("N") };
                var stale = new IndependentProcessIdentity
                { Pid = target.ProcessId, StartUtcTicks = target.StartUtcTicks - 1, WindowsSessionId = session,
                    ExecutablePath = Executable, SessionToken = identity.SessionToken };
                using (var reused = new IndependentProcessRetirement(stale, stale, Executable, 1000))
                    if (reused.Poll() != IndependentOperationResult.Completed || Gone(target.ProcessId, target.StartUtcTicks))
                        throw new Exception("reused PID process terminated or old identity not retired");
                var wrongSession = new IndependentProcessIdentity
                { Pid = target.ProcessId, StartUtcTicks = target.StartUtcTicks, WindowsSessionId = session + 1,
                    ExecutablePath = Executable, SessionToken = identity.SessionToken };
                var rejected = false;
                try { using (var wrong = new IndependentProcessRetirement(wrongSession, wrongSession, Executable, 1000)) { } }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || Gone(target.ProcessId, target.StartUtcTicks)) throw new Exception("wrong OS session terminated");
                rejected = false;
                try { using (var wrong = new IndependentProcessRetirement(identity, stale, Executable, 1000)) { } }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || Gone(target.ProcessId, target.StartUtcTicks)) throw new Exception("wrong durable binding terminated");
                using (var retirement = new IndependentProcessRetirement(identity, identity, Executable, 5000))
                {
                    Until(() => retirement.Poll() != IndependentOperationResult.Pending, "exact retirement hung");
                    if (retirement.Poll() != IndependentOperationResult.Completed) throw new Exception("exact retirement failed");
                    Until(() => Gone(target.ProcessId, target.StartUtcTicks), "exact target survived");
                    if (Gone(unrelated.ProcessId, unrelated.StartUtcTicks)) throw new Exception("same-name unrelated process terminated");
                }
                using (var repeated = new IndependentProcessRetirement(identity, identity, Executable, 1000))
                    if (repeated.Poll() != IndependentOperationResult.Completed) throw new Exception("repeated retirement not idempotent");
            }
            var fencePath = Path.Combine(root, "fence.json");
            using (var fenced = Start("fence", fencePath, 20000))
            {
                Until(() => File.Exists(fencePath), "fence child did not bind");
                var probe = BoundedJson.Read<FenceProbe>(fencePath);
                var wrong = new IndependentProcessIdentity
                { Pid = probe.Identity.Pid, StartUtcTicks = probe.Identity.StartUtcTicks, WindowsSessionId = probe.Identity.WindowsSessionId,
                    ExecutablePath = probe.Identity.ExecutablePath, SessionToken = Guid.NewGuid().ToString("N") };
                var rejected = false;
                try { IndependentExecutionFence.Revoke(probe.InstallationId, wrong); }
                catch (WaitHandleCannotBeOpenedException) { rejected = true; }
                if (!rejected || Gone(fenced.ProcessId, fenced.StartUtcTicks)) throw new Exception("wrong fence session accepted");
                IndependentExecutionFence.Revoke(probe.InstallationId, probe.Identity);
                Until(() => fenced.Poll() != IndependentWorkerState.Running, "external revocation not observed");
                if (fenced.Poll() != IndependentWorkerState.Completed) throw new Exception("power boundary ignored independent fence");
                IndependentExecutionFence.Revoke(probe.InstallationId, probe.Identity);
            }
            var leasePath = Path.Combine(root, "lease-owner.txt");
            string ReadLease()
            {
                using (var file = new FileStream(Path.Combine(root, "independent-executor.lease"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(file))
                {
                    if (file.Length > 2048) throw new Exception("lease evidence not bounded");
                    return reader.ReadToEnd();
                }
            }
            using (var owner = Start("lease", leasePath, 20000))
            {
                Until(() => File.Exists(leasePath) && new FileInfo(leasePath).Length == 32, "lease owner did not publish");
                var installation = File.ReadAllText(leasePath);
                var before = ReadLease();
                foreach (var id in new[] { installation, Guid.NewGuid().ToString("N") })
                {
                    var refused = false;
                    try { using (var duplicate = new IndependentExecutorLease(root, id)) { } }
                    catch (IOException) { refused = true; }
                    if (!refused || ReadLease() != before)
                        throw new Exception("duplicate executor acquired lease or overwrote owner evidence");
                }
                var otherProject = Path.Combine(root, "other-project");
                Directory.CreateDirectory(otherProject);
                using (var other = new IndependentExecutorLease(otherProject, installation)) other.RequireHeld();
                owner.Dispose();
                Until(() => Gone(owner.ProcessId, owner.StartUtcTicks), "lease owner survived crash injection");
                IndependentExecutorLease replacement = null;
                Until(() => (replacement = IndependentExecutorLease.TryAcquire(root, installation)) != null,
                    "executor lease not released after owner crash");
                if (replacement.ExecutorIdentity != "IndependentExecutor:" + installation)
                    throw new Exception("executor identity not stable across replacement");
                replacement.RequireHeld();
                System.Threading.Tasks.Task.Run(() => replacement.Dispose()).GetAwaiter().GetResult();
                var rejected = false;
                try { replacement.RequireHeld(); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new Exception("disposed executor lease still grants authority");
                using (var next = new IndependentExecutorLease(root, installation)) next.RequireHeld();
            }
            return 20;
        }
    }
}
