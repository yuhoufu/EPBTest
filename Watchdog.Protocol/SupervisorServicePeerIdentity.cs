using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace MTTFTest.Watchdog.Protocol
{
    public static class SupervisorServicePeerIdentity
    {
        // SCM is the independent authority for the installed Supervisor process.
        // Do not trust a PID or executable name supplied by the pipe itself.
        public static void Validate(NamedPipeClientStream pipe)
        {
            var scm = OpenSCManager(null, null, 1);
            if (scm == IntPtr.Zero) throw new IOException("SupervisorServiceManagerUnavailable");
            try
            {
                var service = OpenService(scm, "MTTFTestSupervisor", 4);
                if (service == IntPtr.Zero) throw new IOException("SupervisorServiceUnavailable");
                try
                {
                    if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf(typeof(ServiceStatusProcess)), out _) ||
                        status.CurrentState != 4 || status.ProcessId == 0 || status.ProcessId != PipePeerIdentity.ServerProcessId(pipe))
                        throw new IOException("SupervisorPipeIsNotRegisteredService");
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(scm); }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
