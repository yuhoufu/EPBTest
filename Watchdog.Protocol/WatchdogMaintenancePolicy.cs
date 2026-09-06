using System;
using System.IO;

namespace MTTFTest.Watchdog.Protocol
{
    public static class WatchdogMaintenancePolicy
    {
        public static string InhibitPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MTTFTest", "maintenance-inhibit.json");

        // Safety workers remain available while maintenance drains hardware; only new main launches stop.
        public static void AssertMainLaunchAllowed(string inhibitPath = null)
        {
            var path = inhibitPath ?? InhibitPath;
            if (File.Exists(path))
                throw new InvalidOperationException("MaintenanceMainLaunchInhibited: " + path);
        }
    }
}
