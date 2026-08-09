using System;

namespace Controller
{
    /// <summary>
    /// Thread-safe two-slot latest-value mailbox. Publishing replaces the previous value in the
    /// selected slot, so a stalled consumer can never accumulate an unbounded history backlog.
    /// </summary>
    public sealed class LatestPairMailbox<T> where T : class
    {
        private readonly object _gate = new object();
        private T _first;
        private T _second;

        public void Publish(int slot, T value)
        {
            if (slot != 0 && slot != 1)
                throw new ArgumentOutOfRangeException(nameof(slot));
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            lock (_gate)
            {
                if (slot == 0) _first = value;
                else _second = value;
            }
        }

        public bool TryTake(out T first, out T second)
        {
            lock (_gate)
            {
                first = _first;
                second = _second;
                _first = null;
                _second = null;
                return first != null || second != null;
            }
        }

        public bool HasPending
        {
            get
            {
                lock (_gate) return _first != null || _second != null;
            }
        }

        internal int PendingSlotCount
        {
            get
            {
                lock (_gate)
                    return (_first == null ? 0 : 1) + (_second == null ? 0 : 1);
            }
        }
    }
}
