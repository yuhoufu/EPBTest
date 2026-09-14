using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    public enum IndependentWorkerState { Running, Completed, Failed, TimedOut }

    // Native calls run outside the executor. Suspend before job assignment so
    // even an immediately spawned descendant belongs to the same cleanup scope.
    // Closing the executor's job handle also retires workers after a host crash.
    public sealed class IndependentBoundedWorker : IDisposable
    {
        private IntPtr _job, _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly int _deadlineMs;
        private readonly bool _sessionLifetime;
        private bool _disposed;
        private IndependentWorkerState _state;
        public int ProcessId { get; private set; }
        public long StartUtcTicks { get; private set; }
        public uint ExitCode { get; private set; }

        public IndependentBoundedWorker(string executable, string arguments, string workingDirectory,
            int deadlineMs, int memoryLimitMiB = 256, Action<int, long> beforeResume = null)
            : this(executable, arguments, workingDirectory, deadlineMs, memoryLimitMiB, beforeResume, false) { }

        // A supervised session has no worker execution deadline. Registration
        // remains bounded; its Job is owned until the Supervisor releases it.
        public static IndependentBoundedWorker StartSession(string executable, string arguments, string workingDirectory,
            Action<int, long> beforeResume)
        {
            if (beforeResume == null) throw new ArgumentNullException(nameof(beforeResume));
            return new IndependentBoundedWorker(executable, arguments, workingDirectory, 10000, 256, beforeResume, true);
        }

        private IndependentBoundedWorker(string executable, string arguments, string workingDirectory,
            int deadlineMs, int memoryLimitMiB, Action<int, long> beforeResume, bool sessionLifetime)
        {
            if (deadlineMs < 1 || deadlineMs > 300000 || memoryLimitMiB < 32 || memoryLimitMiB > 512)
                throw new ArgumentOutOfRangeException(nameof(deadlineMs));
            if (!Path.IsPathRooted(executable) || !File.Exists(executable) || executable.Contains("\""))
                throw new ArgumentException("IndependentWorkerExecutableInvalid");
            _deadlineMs = deadlineMs;
            _sessionLifetime = sessionLifetime;
            PROCESS_INFORMATION info = default;
            try
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) throw new Win32Exception();
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                // KILL_ON_JOB_CLOSE | PROCESS_MEMORY | JOB_MEMORY | ACTIVE_PROCESS
                limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x100 | 0x200 | 0x8;
                limits.BasicLimitInformation.ActiveProcessLimit = 8;
                limits.ProcessMemoryLimit = new UIntPtr((uint)memoryLimitMiB * 1024 * 1024);
                limits.JobMemoryLimit = limits.ProcessMemoryLimit;
                var size = Marshal.SizeOf(limits);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(limits, buffer, false);
                    if (!SetInformationJobObject(_job, 9, buffer, (uint)size)) throw new Win32Exception();
                }
                finally { Marshal.FreeHGlobal(buffer); }
                var startup = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
                var command = new StringBuilder("\"" + executable + "\" " + (arguments ?? string.Empty));
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x00000004 | 0x08000000, IntPtr.Zero, workingDirectory, ref startup, out info))
                    throw new Win32Exception();
                _process = info.hProcess;
                ProcessId = (int)info.dwProcessId;
                if (!AssignProcessToJobObject(_job, _process)) throw new Win32Exception();
                if (!GetProcessTimes(_process, out var created, out _, out _, out _)) throw new Win32Exception();
                StartUtcTicks = DateTime.FromFileTimeUtc(created).Ticks;
                // The callback can durably bind this exact suspended child to
                // its session. A rejected or late registration never executes
                // even the child's first instruction.
                beforeResume?.Invoke(ProcessId, StartUtcTicks);
                if (_clock.ElapsedMilliseconds >= _deadlineMs)
                    throw new TimeoutException("IndependentWorkerRegistrationDeadlineExceeded");
                if (ResumeThread(info.hThread) == uint.MaxValue) throw new Win32Exception();
            }
            catch
            {
                // A process not assigned to the job is still suspended; retire
                // its original handle, never a later PID lookup.
                if (_process != IntPtr.Zero) TerminateProcess(_process, 125);
                Dispose();
                throw;
            }
            finally { if (info.hThread != IntPtr.Zero) CloseHandle(info.hThread); }
        }

        public IndependentWorkerState Poll()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentBoundedWorker));
            if (_state != IndependentWorkerState.Running) return _state;
            if (WaitForSingleObject(_process, 0) == 0)
            {
                if (!GetExitCodeProcess(_process, out var code)) throw new Win32Exception();
                ExitCode = code;
                _state = code == 0 ? IndependentWorkerState.Completed : IndependentWorkerState.Failed;
                // No detached descendant may survive successful parent exit.
                RetireJob();
            }
            else if (!_sessionLifetime && _clock.ElapsedMilliseconds >= _deadlineMs)
            {
                _state = IndependentWorkerState.TimedOut;
                RetireJob();
            }
            return _state;
        }

        private void RetireJob()
        {
            if (_job == IntPtr.Zero) return;
            if (!TerminateJobObject(_job, 124)) throw new Win32Exception();
            var retirement = Stopwatch.StartNew();
            while (true)
            {
                if (!QueryInformationJobObject(_job, 1, out var accounting,
                    (uint)Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), IntPtr.Zero))
                    throw new Win32Exception();
                if (accounting.ActiveProcesses == 0) break;
                if (retirement.ElapsedMilliseconds >= 1000)
                    throw new IOException("IndependentWorkerJobRetirementUnconfirmed");
                Thread.Sleep(10);
            }
            CloseHandle(_job); _job = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
            if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION
        { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS
        { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int type, IntPtr data, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref STARTUPINFO startup, out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(
            IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION information, uint size, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
