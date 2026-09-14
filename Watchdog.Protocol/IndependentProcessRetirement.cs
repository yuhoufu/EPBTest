using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MTTFTest.Watchdog.Protocol
{
    // Retains one verified kernel handle from inspection through termination.
    // A PID lookup after validation would risk terminating a reused PID.
    public sealed class IndependentProcessRetirement : IDisposable
    {
        private readonly SafeProcessHandle _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _deadlineMs;
        private bool _disposed;
        private IndependentOperationResult _result;
        public string Detail { get; private set; }

        public IndependentProcessRetirement(IndependentProcessIdentity expected,
            IndependentProcessIdentity durableController, string registeredExecutable, int deadlineMs)
        {
            if (expected == null || durableController == null)
                throw new ArgumentNullException(nameof(expected));
            expected.Validate(); durableController.Validate();
            if (!expected.Matches(durableController) ||
                !string.Equals(Path.GetFullPath(expected.ExecutablePath), Path.GetFullPath(registeredExecutable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("IndependentRetirementAuthorityMismatch");
            if (deadlineMs < 1 || deadlineMs > 10000) throw new ArgumentOutOfRangeException(nameof(deadlineMs));
            if (expected.Pid == Process.GetCurrentProcess().Id)
                throw new InvalidOperationException("IndependentRetirementCannotTargetExecutor");
            _deadlineMs = deadlineMs;
            // PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE
            _process = OpenProcess(0x00101001, false, expected.Pid);
            if (_process.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                _process.Dispose();
                if (error == 87) { _result = IndependentOperationResult.Completed; Detail = "OldProcessAlreadyAbsent"; return; }
                throw new Win32Exception(error, "IndependentRetirementOpenFailed");
            }
            try
            {
                if (Exited()) { _result = IndependentOperationResult.Completed; Detail = "OldProcessAlreadyExited"; return; }
                if (!GetProcessTimes(_process, out var created, out _, out _, out _)) throw new Win32Exception();
                // A reused PID proves this old identity is gone; do not touch its replacement.
                if (DateTime.FromFileTimeUtc(created).Ticks != expected.StartUtcTicks)
                { _result = IndependentOperationResult.Completed; Detail = "OldIdentityGonePidReused"; return; }
                var image = new StringBuilder(32768);
                var length = image.Capacity;
                if (!QueryFullProcessImageName(_process, 0, image, ref length)) throw new Win32Exception();
                if (!ProcessIdToSessionId(expected.Pid, out var session))
                {
                    if (Exited()) { _result = IndependentOperationResult.Completed; Detail = "OldProcessExitedDuringInspection"; return; }
                    throw new Win32Exception();
                }
                if (session != expected.WindowsSessionId ||
                    !string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(registeredExecutable), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentRetirementOsIdentityMismatch");
                if (!TerminateProcess(_process, 124) && !Exited()) throw new Win32Exception();
                Detail = "ExactOldProcessTerminationRequested";
            }
            catch { _process.Dispose(); throw; }
        }

        private bool Exited()
        {
            var wait = WaitForSingleObject(_process, 0);
            if (wait == 0) return true;
            if (wait == 258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IndependentRetirementWaitFailed");
        }

        public IndependentOperationResult Poll()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentProcessRetirement));
            if (_result != IndependentOperationResult.Pending) return _result;
            if (Exited()) { _result = IndependentOperationResult.Completed; Detail = "ExactOldProcessExitConfirmed"; }
            else if (_clock.ElapsedMilliseconds >= _deadlineMs)
            { _result = IndependentOperationResult.Failed; Detail = "ExactOldProcessExitUnconfirmed"; }
            return _result;
        }

        public void Dispose() { if (_disposed) return; _disposed = true; _process?.Dispose(); }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder image, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}
