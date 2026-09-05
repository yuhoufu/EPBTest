using System;
using System.IO.Pipes;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>Bounds synchronous pipe write AND read. Closing the exact pipe releases blocked native I/O.</summary>
    public sealed class PipeExchangeDeadline : IDisposable
    {
        private readonly Timer _deadline;
        public PipeExchangeDeadline(PipeStream pipe, int timeoutMs)
        {
            if (pipe == null) throw new ArgumentNullException(nameof(pipe));
            var handle = pipe.SafePipeHandle;
            _deadline = new Timer(_ =>
            {
                // Dispose alone does not cancel a synchronous PipeStream read/write on
                // .NET Framework. Cancel the outstanding kernel request before closing it.
                try { CancelIoEx(handle, IntPtr.Zero); } catch { }
                try { pipe.Dispose(); } catch { }
            }, null, Math.Max(1, timeoutMs), Timeout.Infinite);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CancelIoEx(SafePipeHandle handle, IntPtr overlapped);

        public void Dispose() => _deadline.Dispose();
    }
}
