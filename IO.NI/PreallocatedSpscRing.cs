using System;
using System.Threading;

namespace IO.NI
{
    /// <summary>
    /// Fixed-capacity single-producer/single-consumer ring. The backing storage is allocated
    /// once and queue operations allocate no nodes. Producer and consumer ownership must stay
    /// on one thread each; observers may read <see cref="Count"/> and <see cref="TryPeek"/>.
    /// </summary>
    internal sealed class PreallocatedSpscRing<T>
    {
        private struct Slot
        {
            public long Version;
            public int Occupied;
            public T Value;
        }

        private readonly Slot[] _slots;
        private long _readSequence;
        private long _writeSequence;

        public PreallocatedSpscRing(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _slots = new Slot[capacity];
        }

        public int Capacity => _slots.Length;

        public int Count
        {
            get
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                var count = write - read;
                if (count <= 0) return 0;
                return count >= _slots.Length ? _slots.Length : (int)count;
            }
        }

        public bool TryEnqueue(T item)
        {
            var write = Volatile.Read(ref _writeSequence);
            var read = Volatile.Read(ref _readSequence);
            if (write - read >= _slots.Length) return false;

            ref var slot = ref _slots[(int)(write % _slots.Length)];
            Interlocked.Increment(ref slot.Version); // odd: value is being changed
            slot.Value = item;
            Volatile.Write(ref slot.Occupied, 1);
            Interlocked.Increment(ref slot.Version); // even: complete value is visible
            // Publish the slot only after its complete value is visible.
            Volatile.Write(ref _writeSequence, write + 1);
            return true;
        }

        public bool TryDequeue(out T item)
        {
            var read = Volatile.Read(ref _readSequence);
            var write = Volatile.Read(ref _writeSequence);
            if (read >= write)
            {
                item = default;
                return false;
            }

            ref var slot = ref _slots[(int)(read % _slots.Length)];
            item = slot.Value;
            Interlocked.Increment(ref slot.Version);
            Volatile.Write(ref slot.Occupied, 0);
            slot.Value = default;
            Interlocked.Increment(ref slot.Version);
            // Release the slot only after the consumer has taken and cleared it.
            Volatile.Write(ref _readSequence, read + 1);
            return true;
        }

        public bool TryPeek(out T item)
        {
            // TryPeek is also used by a watchdog observer. A small seqlock around each
            // preallocated slot prevents that observer from seeing a torn struct while the
            // SPSC consumer clears it or the producer reuses it after wrap-around.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                if (read >= write)
                {
                    item = default;
                    return false;
                }

                ref var slot = ref _slots[(int)(read % _slots.Length)];
                var before = Volatile.Read(ref slot.Version);
                if ((before & 1) != 0) continue;
                if (Volatile.Read(ref slot.Occupied) == 0)
                {
                    // Consumer has taken the slot but has not yet published its new read
                    // sequence. Returning no observation is safer than exposing default(T).
                    item = default;
                    return false;
                }
                var candidate = slot.Value;
                var after = Volatile.Read(ref slot.Version);
                if (before == after &&
                    (after & 1) == 0 &&
                    Volatile.Read(ref slot.Occupied) != 0 &&
                    read == Volatile.Read(ref _readSequence))
                {
                    item = candidate;
                    return true;
                }
            }

            item = default;
            return false;
        }
    }
}
