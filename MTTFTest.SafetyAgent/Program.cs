using System;

namespace MTTFTest.SafetyAgent
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--independent-launch")
            {
                try
                {
                    Console.WriteLine(MTTFTest.Watchdog.Protocol.IndependentInteractiveLauncher.Dispatch(args[1], args[2]));
                    return 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
            }
            if (args.Length == 2 && args[0] == "--independent-stage")
            {
                try { return IndependentSafetyWorker.Run(args[1]); }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
            }
            SafetyAgentArguments parsed;
            if (!SafetyAgentArguments.TryParse(args, out parsed)) return 64;
            return SafetyAgentRunner.Run(parsed, new ProductionSafetyHardwareFactory());
        }
    }
}
