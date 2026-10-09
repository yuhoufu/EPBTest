using System;
using System.Linq;

namespace MTTFTest.Watchdog
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                args = args ?? Array.Empty<string>();
                if (args.Any(value => string.Equals(
                        value,
                        "--session-host",
                        StringComparison.OrdinalIgnoreCase)))
                    return WatchdogHost.Run(args.Where(value => !string.Equals(
                            value,
                            "--session-host",
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray());
                if (args.Any(value => string.Equals(
                        value,
                        "--console",
                        StringComparison.OrdinalIgnoreCase)))
                    return SupervisorServiceHost.RunConsole();
                if (args.Length > 0)
                    return WatchdogHost.Run(args);
                return SupervisorServiceHost.RunService();
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine(ex); } catch { }
                WatchdogHost.WriteEmergencyLog(ex.ToString(), args);
                return 2;
            }
        }
    }
}
