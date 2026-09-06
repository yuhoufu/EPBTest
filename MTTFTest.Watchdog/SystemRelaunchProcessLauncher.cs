using System;
using System.Diagnostics;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// The only production process-launch/termination port used by the
    /// durable recovery path.  Keeping the OS calls behind this tiny seam
    /// makes it possible to exercise the launch transaction with a fake
    /// launcher without replacing the coordinator or bypassing its journal
    /// transitions.
    /// </summary>
    internal interface ISystemRelaunchProcessLauncher
    {
        Process Start(ProcessStartInfo startInfo);
        void KillExact(Process process);
    }

    internal sealed class SystemRelaunchProcessLauncher : ISystemRelaunchProcessLauncher
    {
        public Process Start(ProcessStartInfo startInfo)
        {
            // This compatibility adapter is compiled only into the legacy
            // migration tests.  Production Host has no raw ProcessStartInfo
            // path; GuardedProcessLauncher is the sole OS boundary.
            throw new NotSupportedException("Legacy launch adapter is test-only; use GuardedProcessLauncher.");
        }

        public void KillExact(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // The caller already closed the durable permit.  A process
                // that cannot be proven stopped is treated as an unknown
                // outcome by the caller and therefore never retried.
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
    }
}
