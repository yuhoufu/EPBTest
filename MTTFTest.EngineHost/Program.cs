using System;
using System.IO;
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
                string pipeName = null;
                string singletonName = null;
                string receiptRootDirectory = null;
                var testInstance = simulation
                    ? Environment.GetEnvironmentVariable(
                        "MTTFTEST_ENGINEHOST_TEST_INSTANCE")
                    : null;
                if (!string.IsNullOrWhiteSpace(testInstance))
                {
                    if (!RecoveryProtocolV7.IsGuid(testInstance))
                        throw new InvalidDataException(
                            "EngineHostTestInstanceInvalid");
                    var testRoot = Environment.GetEnvironmentVariable(
                        "MTTFTEST_ENGINEHOST_TEST_ROOT");
                    if (string.IsNullOrWhiteSpace(testRoot))
                        throw new InvalidDataException(
                            "EngineHostTestRootMissing");
                    pipeName = EngineHostProtocol.PipeName + ".test." + testInstance;
                    singletonName = "Local\\MTTFTest.EngineHost.V3.Test." + testInstance;
                    receiptRootDirectory = Path.Combine(
                        Path.GetFullPath(testRoot),
                        "engine-receipts");
                }
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
                           simulation,
                           pipeName,
                           singletonName,
                           receiptRootDirectory))
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
