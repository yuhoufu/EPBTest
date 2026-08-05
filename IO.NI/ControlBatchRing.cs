using System;
using System.Threading;

namespace IO.NI
{
    internal enum ControlLatencyAction
    {
        Healthy,
        Warning,
        ResynchronizeInactive,
        HardFault
    }

    internal static class ControlLatencyPolicy
    {
        public static ControlLatencyAction Evaluate(
            double oldestAgeMs,
            bool active,
            double warningAgeMs,
            double hardFaultAgeMs)
        {
            if (oldestAgeMs < warningAgeMs) return ControlLatencyAction.Healthy;
            if (!active) return ControlLatencyAction.ResynchronizeInactive;
            return oldestAgeMs >= hardFaultAgeMs
                ? ControlLatencyAction.HardFault
                : ControlLatencyAction.Warning;
        }
    }

    internal readonly struct FastControlSampleValue
    {
        public FastControlSampleValue(int channel, double amps)
        {
            Channel = channel;
            Amps = amps;
        }

        public int Channel { get; }
        public double Amps { get; }
    }

    /// <summary>
    /// Per-device, preallocated single-producer/single-consumer control ring.
    /// Slots are never overwritten until the consumer advances the read sequence.
    /// </summary>
    internal sealed class ControlBatchRing
    {
        private sealed class Slot
        {
            public Slot(int maximumSamples)
            {
                Samples = new FastControlSampleValue[maximumSamples];
            }

            public long PublishedSequence;
            public long Generation;
            public DateTime Timestamp;
            public long EnqueuedMonotonicTicks;
            public int Count;
            public readonly FastControlSampleValue[] Samples;
        }

        private readonly Slot[] _slots;
        private long _readSequence;
        private long _writeSequence;

        public ControlBatchRing(int capacity, int maximumSamplesPerBatch)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maximumSamplesPerBatch < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumSamplesPerBatch));

            Capacity = capacity;
            MaximumSamplesPerBatch = maximumSamplesPerBatch;
            _slots = new Slot[capacity];
            for (var i = 0; i < capacity; i++)
                _slots[i] = new Slot(maximumSamplesPerBatch);
        }

        public int Capacity { get; }
        public int MaximumSamplesPerBatch { get; }

        public int Depth
        {
            get
            {
                var depth = Volatile.Read(ref _writeSequence) - Volatile.Read(ref _readSequence);
                if (depth <= 0) return 0;
                return depth >= int.MaxValue ? int.MaxValue : (int)depth;
            }
        }

        public long OldestEnqueuedMonotonicTicks
        {
            get
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                if (read >= write) return 0;
                var slot = _slots[(int)(read % Capacity)];
                return Volatile.Read(ref slot.PublishedSequence) == read + 1
                    ? slot.EnqueuedMonotonicTicks
                    : 0;
            }
        }

        public bool TryEnqueue(
            long generation,
            DateTime timestamp,
            long enqueuedMonotonicTicks,
            ReadOnlySpan<FastControlSampleValue> samples)
        {
            if (samples.Length > MaximumSamplesPerBatch)
                throw new ArgumentOutOfRangeException(nameof(samples));

            var write = Volatile.Read(ref _writeSequence);
            var read = Volatile.Read(ref _readSequence);
            if (write - read >= Capacity) return false;

            var slot = _slots[(int)(write % Capacity)];
            slot.Generation = generation;
            slot.Timestamp = timestamp;
            slot.EnqueuedMonotonicTicks = enqueuedMonotonicTicks;
            slot.Count = samples.Length;
            for (var i = 0; i < samples.Length; i++)
                slot.Samples[i] = samples[i];

            Volatile.Write(ref slot.PublishedSequence, write + 1);
            Volatile.Write(ref _writeSequence, write + 1);
            return true;
        }

        public bool TryDequeue(
            FastControlSampleValue[] destination,
            out int count,
            out long generation,
            out DateTime timestamp,
            out long enqueuedMonotonicTicks)
        {
            if (destination == null || destination.Length < MaximumSamplesPerBatch)
                throw new ArgumentException("Destination is too small.", nameof(destination));

            while (true)
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                if (read >= write)
                {
                    count = 0;
                    generation = 0;
                    timestamp = default;
                    enqueuedMonotonicTicks = 0;
                    return false;
                }

                var slot = _slots[(int)(read % Capacity)];
                if (Volatile.Read(ref slot.PublishedSequence) != read + 1)
                {
                    Thread.Yield();
                    continue;
                }

                var localCount = slot.Count;
                var localGeneration = slot.Generation;
                var localTimestamp = slot.Timestamp;
                var localEnqueuedTicks = slot.EnqueuedMonotonicTicks;
                for (var i = 0; i < localCount; i++)
                    destination[i] = slot.Samples[i];

                if (Interlocked.CompareExchange(ref _readSequence, read + 1, read) != read)
                    continue;

                count = localCount;
                generation = localGeneration;
                timestamp = localTimestamp;
                enqueuedMonotonicTicks = localEnqueuedTicks;
                return true;
            }
        }

        /// <summary>Discard stale history while retaining the newest batch.</summary>
        public int DiscardAllButLatest()
        {
            while (true)
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                var target = Math.Max(read, write - 1);
                if (target <= read) return 0;
                if (Interlocked.CompareExchange(ref _readSequence, target, read) == read)
                    return (int)Math.Min(int.MaxValue, target - read);
            }
        }

        public void Reset()
        {
            Volatile.Write(ref _readSequence, Volatile.Read(ref _writeSequence));
        }
    }
}
