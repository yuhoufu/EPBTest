using System;
using System.Diagnostics;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Owns the Process handle returned by Process.Start for a pending sidecar
    /// helper.  The handle is deliberately separate from the validated
    /// authority identity: once Attached has promoted the identity, the
    /// runtime can dispose this owner without affecting the live sidecar.
    /// </summary>
    public sealed class SidecarProcessHandleOwner : IDisposable
    {
        private Process _process;
        private int _disposed;
        private static int _liveOwnedCount;

        public SidecarProcessHandleOwner(Process process)
        {
            if (process == null) throw new ArgumentNullException(nameof(process));
            _process = process;
            Interlocked.Increment(ref _liveOwnedCount);
        }

        /// <summary>Number of process handles currently owned by this type.</summary>
        public static int LiveOwnedCount => Volatile.Read(ref _liveOwnedCount);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Returns the owned Process while it is live.  The runtime must not
        /// retain this reference after Dispose and must never call Kill on a
        /// validated authority through this pending-handle owner.
        /// </summary>
        public Process Process => Volatile.Read(ref _disposed) == 0 ? _process : null;

        public int ProcessId => Process?.Id ?? 0;

        /// <summary>
        /// Atomically transfers the owned Process to the caller.  This is only
        /// used by shutdown/failed-start cleanup; the caller becomes
        /// responsible for disposing the returned handle.
        /// </summary>
        public Process Detach()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return null;
            var process = Interlocked.Exchange(ref _process, null);
            Interlocked.Decrement(ref _liveOwnedCount);
            return process;
        }

        public void Dispose()
        {
            var process = Detach();
            if (process == null) return;
            try { process.Dispose(); }
            catch { }
        }
    }
}
