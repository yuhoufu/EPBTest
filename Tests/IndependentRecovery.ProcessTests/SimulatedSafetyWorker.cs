using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using MTTFTest.SafetyAgent;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    // Only the test executable routes to this adapter. Production SafetyAgent's
    // entrypoint always selects ProductionSafetyHardwareFactory.
    internal static class SimulatedSafetyWorker
    {
        internal static int Run(string commandPath)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) return 77;
            IndependentProtectedFiles.RequireTrustedFile(commandPath);
            var command = BoundedJson.Read<IndependentSafetyWorkerCommand>(commandPath);
            command.Validate(DateTime.UtcNow.Ticks);
            var marker = Path.Combine(command.ConfigDirectory, "SIMULATED-HARDWARE-ONLY.txt");
            IndependentProtectedFiles.RequireTrustedFile(marker);
            if (new FileInfo(marker).Length > 128) throw new InvalidDataException("SimulationMarkerOversize");
            var scenario = File.ReadAllText(marker);
            if (scenario != "success" && scenario != "pressure-failure" && scenario != "hang")
                throw new InvalidDataException("SimulationScenarioInvalid");
            return IndependentSafetyWorker.Run(commandPath, new Factory(scenario));
        }

        private sealed class Factory : ISafetyHardwareFactory
        {
            private readonly string _scenario;
            internal Factory(string scenario) { _scenario = scenario; }
            public ISafetyHardware Create(string directory, SafetyRuntimeSnapshot runtime)
                => new Hardware(_scenario, directory);
        }
        private sealed class Hardware : ISafetyHardware
        {
            private readonly string _scenario;
            private readonly string _directory;
            internal Hardware(string scenario, string directory) { _scenario = scenario; _directory = directory; }
            public bool ConfirmPowerOff()
            {
                File.WriteAllText(Path.Combine(_directory, "simulated-power-entered.txt"), "SIMULATED_NO_HARDWARE");
                if (_scenario == "hang") Thread.Sleep(Timeout.Infinite);
                return true;
            }
            public bool ConfirmDoOff() => true;
            public bool ConfirmAoZero() => true;
            public bool ConfirmPressureSafe() => _scenario != "pressure-failure";
            public void Dispose() { }
        }

        internal static int RunMatrix(string root)
        {
            root = Path.GetFullPath(root);
            if (!Path.GetFileName(root).StartsWith("safety-worker-matrix-", StringComparison.Ordinal))
                throw new InvalidOperationException("Isolated matrix directory required");
            IndependentProtectedFiles.RequireTrustedDirectory(root);
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("SYSTEM matrix required");
            string executable;
            using (var process = Process.GetCurrentProcess()) executable = process.MainModule.FileName;
            var passed = 0;
            foreach (var name in new[] { "power-success", "outputs-success", "pressure-failure", "hang" })
            {
                var directory = Path.Combine(root, name);
                if (Directory.Exists(directory)) throw new InvalidOperationException("Matrix case already exists");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SIMULATED-HARDWARE-ONLY.txt"), name.EndsWith("success") ? "success" : name);
                var files = new[] { "AIConfig.xml", "AOConfig.xml", "DOConfig.xml", "PowerSupplyConfig.xml" }.Select(file =>
                {
                    var path = Path.Combine(directory, file); File.WriteAllText(path, "<simulation-only/>");
                    return new IndependentSafetyConfigFile { Name = file, Sha256 = SupervisorProtocol.ComputeSha256(path) };
                }).ToArray();
                var now = DateTime.UtcNow.Ticks;
                var deadline = now + TimeSpan.FromMilliseconds(name == "hang" ? 2000 : 5000).Ticks;
                var command = new IndependentSafetyWorkerCommand
                {
                    StageNonce = Guid.NewGuid().ToString("N"), ConfigDirectory = directory,
                    Files = files, IssuedUtcTicks = now, DeadlineUtcTicks = deadline,
                    Transaction = new IndependentRecoveryTransaction
                    {
                        Revision = 1, Generation = 1, IntentRevision = 1, RunEpoch = 1,
                        RunId = Guid.NewGuid().ToString("N"), RequestId = Guid.NewGuid().ToString("N"),
                        ExecutorIdentity = "SIMULATION", Channels = new[] { 7, 8, 9 },
                        Phase = name == "power-success" ? IndependentRecoveryPhase.PowerOff : IndependentRecoveryPhase.OutputsSafe,
                        PhaseDeadlineUtcTicks = deadline, LastAttemptUtcTicks = now, AttemptsUtcTicks = new[] { now }
                    },
                    Runtime = new SafetyRuntimeSnapshot
                    {
                        SampleRateHz = 2000, SamplesPerChannel = 20, PressureChannels = new[] { "Pressure_1" },
                        ReleaseSafePressureBar = new[] { 1d }, PressureSampleMaxAgeMs = 100,
                        ReleaseStableMs = 300, ReleaseTimeoutMs = 5000
                    }
                };
                var commandPath = Path.Combine(directory, "command.json"); BoundedJson.Write(commandPath, command);
                int pid; long start; IndependentOperationResult result;
                using (var operation = new IndependentSafetyWorkerOperation(executable, SupervisorProtocol.ComputeSha256(executable), commandPath))
                {
                    pid = operation.WorkerPid; start = operation.WorkerStartUtcTicks;
                    BoundedJson.Write(Path.Combine(directory, "worker-owner.json"), new { pid, startUtcTicks = start });
                    do { result = operation.Poll(DateTime.UtcNow.Ticks); if (result == IndependentOperationResult.Pending) Thread.Sleep(20); }
                    while (result == IndependentOperationResult.Pending);
                    if ((result == IndependentOperationResult.Completed) != name.EndsWith("success") ||
                        !File.Exists(Path.Combine(directory, "simulated-power-entered.txt")))
                        throw new InvalidOperationException("Safety matrix result mismatch: " + name + ":" + operation.Detail);
                    if (name == "hang" && (DateTime.UtcNow.Ticks < deadline || File.Exists(commandPath + ".receipt.json")))
                        throw new InvalidOperationException("Hang did not reach external deadline or emitted a receipt");
                    passed++;
                }
                try
                {
                    using (var process = Process.GetProcessById(pid))
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == start)
                            throw new InvalidOperationException("Matrix worker still alive: " + pid);
                }
                catch (ArgumentException) { }
                passed++;
            }
            BoundedJson.Write(Path.Combine(root, "matrix-result.json"), new { passed, hardware = "SIMULATED_NO_HARDWARE" });
            return passed;
        }
    }
}
