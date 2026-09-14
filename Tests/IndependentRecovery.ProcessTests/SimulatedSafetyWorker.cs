using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
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
                => new Hardware(_scenario);
        }
        private sealed class Hardware : ISafetyHardware
        {
            private readonly string _scenario;
            internal Hardware(string scenario) { _scenario = scenario; }
            public bool ConfirmPowerOff()
            {
                if (_scenario == "hang") Thread.Sleep(Timeout.Infinite);
                return true;
            }
            public bool ConfirmDoOff() => true;
            public bool ConfirmAoZero() => true;
            public bool ConfirmPressureSafe() => _scenario != "pressure-failure";
            public void Dispose() { }
        }
    }
}
