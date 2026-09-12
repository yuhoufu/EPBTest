using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace MTTFTest.RecoveryControl
{
    // Uses Windows service ownership, not a process name or a server-supplied
    // identity. The independent Guard needs no business DLL for this check.
    internal static class RecoverySupervisorPeer
    {
        internal const string ServiceName = "MTTFTestSupervisor";

        internal static void Assert(NamedPipeClientStream pipe) => VerifyPeer(pipe, ReadRunningServicePid);

        private static void VerifyPeer(NamedPipeClientStream pipe, Func<uint> servicePid)
        {
            var expected = servicePid();
            if (expected == 0 || !GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actual) || actual != expected)
                throw new IOException("RecoverySupervisorPipeServiceIdentityMismatch");
            using (var process = Process.GetProcessById(checked((int)actual)))
            {
                if (process.HasExited) throw new IOException("RecoverySupervisorServiceExited");
                var start = process.StartTime.ToUniversalTime().Ticks;
                if (servicePid() != actual || process.HasExited || process.StartTime.ToUniversalTime().Ticks != start ||
                    !GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var current) || current != actual)
                    throw new IOException("RecoverySupervisorServiceChangedDuringPeerCheck");
            }
        }

        private static uint ReadRunningServicePid()
        {
            var manager = OpenSCManager(null, null, 0x0001); // SC_MANAGER_CONNECT
            if (manager == IntPtr.Zero) throw new IOException("RecoverySupervisorServiceManagerUnavailable");
            try
            {
                var service = OpenService(manager, ServiceName, 0x0004); // SERVICE_QUERY_STATUS
                if (service == IntPtr.Zero) throw new IOException("RecoverySupervisorServiceUnavailable");
                try
                {
                    if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf(typeof(ServiceStatusProcess)), out var needed) ||
                        needed > Marshal.SizeOf(typeof(ServiceStatusProcess)) || status.CurrentState != 4 ||
                        (status.ServiceType & 0x30) != 0x10 || status.ProcessId == 0)
                        throw new IOException("RecoverySupervisorServiceNotRunningAsOwnProcess");
                    return status.ProcessId;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
            public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
