using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal static class RecoveryWorkerRetirement
    {
        internal static bool Retire(RecoveryControlStore store, RecoveryTakeoverTransaction tx, string executable, Action assertMaintenanceAllowed)
        {
            if (assertMaintenanceAllowed == null) throw new ArgumentNullException(nameof(assertMaintenanceAllowed));
            assertMaintenanceAllowed();
            ValidateTarget(tx.Owner, executable);
            store.RequestExpiredWorkerRetirement(tx.TransactionId, tx.Epoch, tx.Owner, DateTime.UtcNow);
            return RetireExactProcess(tx.Owner, executable,
                () =>
                {
                    assertMaintenanceAllowed();
                    store.AssertWorkerRetirement(tx.TransactionId, tx.Epoch, tx.Owner, DateTime.UtcNow);
                });
        }

        internal static bool RetireIdle(RecoveryControlStore store, RecoveryExecutionWorker worker, string executable,
            Action assertMaintenanceAllowed)
        {
            if (assertMaintenanceAllowed == null) throw new ArgumentNullException(nameof(assertMaintenanceAllowed));
            assertMaintenanceAllowed();
            ValidateTarget(worker.Owner, executable);
            store.RequestIdleWorkerRetirement(worker.Owner, DateTime.UtcNow);
            return RetireExactProcess(worker.Owner, executable, () =>
            {
                assertMaintenanceAllowed();
                store.AssertIdleWorkerRetirement(worker.Owner, DateTime.UtcNow);
            });
        }

        private static void ValidateTarget(RecoveryProcessIdentity owner, string executable)
        {
            if (owner?.IsValid() != true || owner.ProcessId == Process.GetCurrentProcess().Id ||
                !string.Equals(Path.GetFileName(executable), "MTTFTest.RecoveryGuard.exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(owner.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RecoveryWorkerRetirementTargetInvalid");
        }

        // One kernel handle is used for identity checks and termination. A PID
        // reused after these checks cannot redirect termination to a new process.
        internal static bool RetireExactProcess(RecoveryProcessIdentity owner, string executable, Action beforeTerminate)
        {
            ValidateTarget(owner, executable);
            if (beforeTerminate == null) throw new ArgumentNullException(nameof(beforeTerminate));
            if (!string.Equals(owner.BootId, RecoveryProcessProbe.ReadBootId(), StringComparison.OrdinalIgnoreCase)) return true;
            using (var handle = OpenProcess(0x00100000 | 0x1000 | 0x0001, false, owner.ProcessId))
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 87) return true;
                    throw new Win32Exception(error, "RecoveryWorkerRetirementOpenFailed");
                }
                if (WaitForSingleObject(handle, 0) == 0) return true;
                if (!GetProcessTimes(handle, out var created, out var exited, out var kernel, out var user))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RecoveryWorkerRetirementTimeUnproven");
                var image = new StringBuilder(32768);
                var length = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RecoveryWorkerRetirementImageUnproven");
                var started = DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks;
                if (started != owner.StartUtcTicks ||
                    !string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
                    return true; // the original identity exited; leave the reused PID alone
                beforeTerminate();
                if (!TerminateProcess(handle, 0xE0030001))
                {
                    if (WaitForSingleObject(handle, 0) == 0) return true;
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RecoveryWorkerRetirementTerminateFailed");
                }
                var wait = WaitForSingleObject(handle, 2000);
                if (wait == 0) return true;
                if (wait == 258) return false;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RecoveryWorkerRetirementWaitFailed");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low; public uint High; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creation, out FileTime exit,
            out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    }
}
