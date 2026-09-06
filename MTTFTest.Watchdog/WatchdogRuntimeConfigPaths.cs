using System;
using System.IO;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Keeps the standalone supervisor on the same runtime configuration root
    /// as the ordinary-user WinForms process without adding the application
    /// Config assembly (and its UI dependencies) to the safety service.
    /// </summary>
    internal static class WatchdogRuntimeConfigPaths
    {
        internal const string FormalModeMarkerName = "MTTFTest.UnattendedMode.required";
        internal const string ConfiguredMarkerName = "MTTFTest.FirstRun.configured";

        internal static string ResolveConfigDirectory(
            string executableDirectory,
            string commonApplicationDataDirectory = null)
        {
            var application = Path.GetFullPath(
                string.IsNullOrWhiteSpace(executableDirectory)
                    ? Environment.CurrentDirectory
                    : executableDirectory);
            var installed = File.Exists(Path.Combine(application, FormalModeMarkerName)) ||
                            File.Exists(Path.Combine(application, ConfiguredMarkerName));
            if (!installed) return Path.Combine(application, "Config");

            var common = string.IsNullOrWhiteSpace(commonApplicationDataDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : commonApplicationDataDirectory;
            return Path.Combine(Path.GetFullPath(common), "MTTFTest", "Config");
        }

        internal static string GetPath(string executableDirectory, string fileName)
        {
            return Path.Combine(ResolveConfigDirectory(executableDirectory), fileName);
        }
    }
}
