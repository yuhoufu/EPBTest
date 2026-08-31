using System;

namespace MTTFTest.SafetyAgent
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            SafetyAgentArguments parsed;
            if (!SafetyAgentArguments.TryParse(args, out parsed)) return 64;
            return SafetyAgentRunner.Run(parsed, new ProductionSafetyHardwareFactory());
        }
    }
}
