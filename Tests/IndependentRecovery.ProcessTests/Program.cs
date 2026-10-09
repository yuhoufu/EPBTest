using System;

namespace AdaptiveControlTests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            if (args.Length == 2 && args[0] == "--independent-stage" &&
                System.IO.File.Exists(System.IO.Path.Combine(root, "REAL-MONITOR-SIMULATION-ONLY.txt")))
            {
                var directory = System.IO.Path.Combine(root, "SafetyWorker");
                var domain = AppDomain.CreateDomain("SimulatedSafetyWorker", null,
                    new AppDomainSetup { ApplicationBase = directory });
                try
                {
                    return domain.ExecuteAssembly(System.IO.Path.Combine(directory,
                        "IndependentRecovery.ProcessTests.exe"), args);
                }
                finally { AppDomain.Unload(domain); }
            }
            return RunMain(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int RunMain(string[] args)
        {
            try
            {
                if (args.Length == 3 && args[0] == "--live-service-simulation")
                    return ServiceControllerSimulation.RunStalledController(args[1], args[2]);
                if (args.Length == 3 && args[0] == "--independent-launch")
                {
                    Console.WriteLine(MTTFTest.Watchdog.Protocol.IndependentInteractiveLauncher.Dispatch(args[1], args[2]));
                    return 0;
                }
                if (args.Length == 3 && args[0] == "--prepare-service-simulation")
                { ServiceControllerSimulation.Prepare(args[1], args[2]); return 0; }
                if (args.Length == 3 && args[0] == "--arm-exited-service-simulation")
                { ServiceControllerSimulation.ArmExitedController(args[1], int.Parse(args[2])); return 0; }
                if (args.Length == 4 && args[0] == "--independent-registration" && args[2] == "--independent-ticket")
                    return ServiceControllerSimulation.Resume(args[1], args[3]);
                if (args.Length == 2 && args[0] == "--safety-worker-matrix")
                {
                    Console.WriteLine("PASS safety worker matrix " + SimulatedSafetyWorker.RunMatrix(args[1]));
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--independent-stage")
                {
                    return SimulatedSafetyWorker.Run(args[1]);
                }
                if (args.Length == 3 && args[0] == "--simulation-resume")
                    return TicketDatabaseSimulationTests.Resume(args[1], args[2]);
                if ((args.Length == 1 || args.Length == 2) && args[0] == "--ticket-database-simulation")
                {
                    if (args.Length == 2)
                        Environment.SetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT", System.IO.Path.GetFullPath(args[1]));
                    var count = TicketDatabaseSimulationTests.Run();
                    if (args.Length == 2)
                        MTTFTest.Watchdog.Protocol.BoundedJson.Write(System.IO.Path.Combine(args[1], "ticket-db-result.json"),
                            new { passed = count, sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                                hardware = "SIMULATED_NO_HARDWARE", database = "REAL_SQLITE_COMMITS" });
                    Console.WriteLine($"PASS ticket database simulation {count}/{count}");
                    return 0;
                }
                if (args.Length == 3 && args[0] == "--independent-worker-child")
                    return IndependentBoundedWorkerTests.Child(args[1], args[2]);
                if (args.Length == 4 && args[0] == "--state-consume")
                    return IndependentProjectStateTests.ConsumeChild(args[1], args[2], long.Parse(args[3]));
                if (args.Length == 2 && (args[0] == "--session-registry" || args[0] == "--registration-seal"))
                {
                    var count = args[0] == "--registration-seal" ? IndependentProjectStateTests.RunRegistrationSealOnly() :
                        IndependentProjectStateTests.RunSessionRegistryOnly();
                    MTTFTest.Watchdog.Protocol.BoundedJson.Write(args[1], new
                    {
                        passed = count, isSystem = System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem,
                        sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId, hardware = "NOT_ACCESSED"
                    });
                    Console.WriteLine($"PASS session registry {count}/{count}");
                    return 0;
                }
                if (args.Length == 1 && args[0] == "--cooperative-stop")
                {
                    IndependentProjectStateTests.RunAll(cooperationOnly: true);
                    return 0;
                }
                var passed = IndependentBoundedWorkerTests.RunAll();
                Console.WriteLine($"PASS independent workers {passed}/{passed}");
                IndependentProjectStateTests.RunAll();
                var simulation = TicketDatabaseSimulationTests.Run();
                Console.WriteLine($"PASS ticket database simulation {simulation}/{simulation}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
