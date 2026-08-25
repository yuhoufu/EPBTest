using System;
using System.Threading;
using MTTFTest.Watchdog.Client;
using MtEmbTest;

namespace MTEmbTest
{
    /// <summary>
    /// Main_Frm's single UI resource owner.  Binding publishes one immutable
    /// context/target/lease tuple; release is admitted only by a terminal
    /// Runtime receipt and is CAS-once, so concurrent FormClosing/retry paths
    /// cannot dispose a target twice or release it before retention completes.
    /// </summary>
    internal sealed class WinFormsWatchdogUiResourceOwner
    {
        private readonly object _gate = new object();
        private RuntimeTransportSessionContext _context;
        private RuntimeSafetyTargetLease _targetLease;
        private WinFormsWatchdogPostTarget _target;
        private int _releaseCount;

        internal bool HasResources
        {
            get
            {
                lock (_gate)
                    return _context != null || _targetLease != null || _target != null;
            }
        }

        internal int ReleaseCount => Volatile.Read(ref _releaseCount);

        internal bool Publish(
            RuntimeTransportSessionContext context,
            RuntimeSafetyTargetLease targetLease,
            WinFormsWatchdogPostTarget target)
        {
            if (context == null || targetLease == null || target == null)
                return false;
            lock (_gate)
            {
                if (_context != null && !ReferenceEquals(_context, context))
                    return false;
                if (_targetLease != null && !ReferenceEquals(_targetLease, targetLease))
                    return false;
                _context = context;
                _targetLease = targetLease;
                _target = target;
                return true;
            }
        }

        internal bool ReleaseAfterTerminal(RuntimeShutdownReceipt receipt)
        {
            if (receipt == null || !receipt.IsTerminal)
                return false;

            RuntimeTransportSessionContext context;
            RuntimeSafetyTargetLease targetLease;
            WinFormsWatchdogPostTarget target;
            lock (_gate)
            {
                if (_context == null && _targetLease == null && _target == null)
                    return false;
                context = _context;
                if (context != null)
                {
                    if (receipt.SessionLease != context.SessionLease)
                        return false;
                    if (receipt.SessionGeneration != context.SessionGeneration)
                        return false;
                    if (!string.Equals(receipt.SessionId, context.SessionId,
                            StringComparison.Ordinal))
                        return false;
                }
                targetLease = _targetLease;
                target = _target;
                _context = null;
                _targetLease = null;
                _target = null;
                Interlocked.Increment(ref _releaseCount);
            }

            try { targetLease?.Dispose(); } catch { }
            try { target?.Dispose(); } catch { }
            return true;
        }
    }
}
