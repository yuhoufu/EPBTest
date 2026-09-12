using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MTTFTest.RecoveryControl
{
    public static class RecoveryProcessProbe
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int informationClass, IntPtr information,
            int informationLength, out int returnLength);

        public static string ReadBootId()
        {
            // SystemBootEnvironmentInformation starts with the boot GUID. It is
            // independent of wall-clock changes and identical in all sessions.
            var buffer = Marshal.AllocHGlobal(64);
            try
            {
                if (NtQuerySystemInformation(90, buffer, 64, out var length) != 0 || length < 16)
                    throw new IOException("RecoveryBootIdentityUnavailable");
                var bytes = new byte[16];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                var id = new Guid(bytes);
                if (id == Guid.Empty) throw new IOException("RecoveryBootIdentityEmpty");
                return id.ToString("N");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static RecoveryProcessIdentity Current()
        {
            using (var process = Process.GetCurrentProcess())
                return Capture(process, ReadBootId());
        }

        public static ProcessObservation Observe(RecoveryProcessIdentity expected, string bootId)
        {
            if (expected?.IsValid() != true || string.IsNullOrWhiteSpace(bootId))
                return ProcessObservation.Unknown;
            if (!string.Equals(expected.BootId, bootId, StringComparison.OrdinalIgnoreCase))
                return ProcessObservation.Exited;
            try
            {
                using (var process = Process.GetProcessById(expected.ProcessId))
                {
                    if (process.HasExited) return ProcessObservation.Exited;
                    // A reused PID is evidence the old process exited; it does
                    // not authorize terminating the process now at that PID.
                    return expected.Matches(Capture(process, bootId))
                        ? ProcessObservation.ExactAlive : ProcessObservation.Exited;
                }
            }
            catch (ArgumentException) { return ProcessObservation.Exited; }
            catch (InvalidOperationException) { return ProcessObservation.Exited; }
            catch { return ProcessObservation.Unknown; }
        }

        private static RecoveryProcessIdentity Capture(Process process, string bootId) => new RecoveryProcessIdentity
        {
            ProcessId = process.Id,
            StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            ExecutablePath = Path.GetFullPath(process.MainModule.FileName),
            BootId = bootId
        };
    }
}
