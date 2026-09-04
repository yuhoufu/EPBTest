using System;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var passed = args.Length == 1 && args[0] == "--project-selection" ? EngineProjectSelectionTests.RunAll() :
                    PressureMaintenanceExecutorTests.RunAll() + PressureMaintenanceTransportTests.RunAll() + EngineSafetyHandoffTests.RunAll() + EngineManualBatchTests.RunAll() + EngineRecoveryExecutionGateTests.RunAll() + ProductionPipeClientTests.RunAll() + EngineHostTests.RunAll() + EngineConfigurationTests.RunAll() + EngineDaqConfigurationTests.RunAll() + EngineAoConfigurationTests.RunAll() + EngineProjectSelectionTests.RunAll();
                Console.WriteLine("PASS " + passed + "/" + passed);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + ex);
                return 1;
            }
        }
    }
}
