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
                        "--launch-main",
                        StringComparison.OrdinalIgnoreCase)))
                    return SupervisorMainLaunchClient.Run(args);
                if (args.Any(value => string.Equals(
                        value,
                        "--console",
                        StringComparison.OrdinalIgnoreCase)))
                    return SupervisorServiceHost.RunConsole();
                if (args.Length > 0)
                {
                    // Existing protocol regression suites exercise the V2
                    // evidence reader as a child process. Keep that harness
                    // reachable only from a repository Tests\...\bin output
                    // which has no formal-mode marker. Installed/staged V3
                    // directories can never take this branch.
                    if (LegacyWatchdogTestHost.TryRun(args, out var testExitCode))
                        return testExitCode;
                    // V3 has exactly one recovery decision owner: the
                    // LocalSystem Supervisor Recovery Kernel.  The V2
                    // per-session WatchdogHost remains compiled only so the
                    // migration reader and regression tests can reuse its
                    // conservative evidence parsers; it is never a valid
                    // production process role.
                    WatchdogHost.WriteEmergencyLog(
                        "LegacySessionHostDisabledInV3",
                        args);
                    return 78;
                }
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
