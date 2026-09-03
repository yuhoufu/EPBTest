using System;
using System.IO;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Test-only adapter for the legacy protocol regression matrix. It is not
    /// a production role and is reachable only beside the repository's
    /// AdaptiveControlTests harness.
    /// </summary>
    internal static class LegacyWatchdogTestHost
    {
        internal static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 78;
            var baseDirectory = Path.GetFullPath(
                AppDomain.CurrentDomain.BaseDirectory ?? string.Empty);
            var testsSegment = Path.DirectorySeparatorChar + "Tests" +
                               Path.DirectorySeparatorChar;
            var isRepositoryTestOutput = baseDirectory.IndexOf(
                testsSegment, StringComparison.OrdinalIgnoreCase) >= 0 &&
                baseDirectory.IndexOf(
                    Path.DirectorySeparatorChar + "bin" +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) >= 0;
            // The AdaptiveControlTests build deliberately copies the complete
            // production runtime closure, including the formal-mode marker.
            // Therefore the marker cannot distinguish a repository regression
            // harness from a staged package. Requiring both the Tests\bin path
            // and the harness executable keeps the legacy host unavailable in
            // production while allowing Debug and Release protocol tests to
            // exercise the conservative V2 evidence reader.
            var regressionHarness = Path.Combine(
                baseDirectory, "AdaptiveControlTests.exe");
            if (!isRepositoryTestOutput || !File.Exists(regressionHarness))
                return false;
            exitCode = WatchdogHost.Run(args ?? Array.Empty<string>());
            return true;
        }
    }
}
