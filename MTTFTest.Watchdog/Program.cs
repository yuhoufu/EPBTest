using System;

namespace MTTFTest.Watchdog
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return WatchdogHost.Run(args); }
            catch (Exception ex)
            {
                WatchdogHost.WriteEmergencyLog(ex.ToString(), args);
                return 2;
            }
        }
    }
}
