using System;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                args = args ?? Array.Empty<string>();
                var sessionId = Read(args, "--session") ?? RecoveryProtocolV7.NewId();
                var runId = Read(args, "--run") ?? RecoveryProtocolV7.NewId();
                var epochText = Read(args, "--epoch");
                if (!long.TryParse(epochText, out var runEpoch) || runEpoch <= 0)
                    runEpoch = 1;
                var simulation = args.Any(value => string.Equals(
                    value, "--simulation", StringComparison.OrdinalIgnoreCase));
                if (!simulation && !EngineLaunchCapabilityGate.Validate(args, out var failure))
                {
                    EngineHostLog.Error("EngineLaunchCapabilityRejected:" + failure, null);
                    return 4;
                }
                using (var runtime = new EngineHostRuntime(
                           sessionId,
                           runId,
                           runEpoch,
                           simulation
                               ? (IEngineHardwareRuntime)new SimulatedEngineHardwareRuntime()
                               : new PhysicalEngineRuntime(),
                           simulation))
                    return runtime.Run();
            }
            catch (Exception ex)
            {
                EngineHostLog.Error("EngineHostFatal", ex);
                return 2;
            }
        }

        private static string Read(string[] args, string name)
        {
            for (var index = 0; index + 1 < args.Length; index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1]?.Trim('"');
            return null;
        }
    }
}
