using System;
using System.Diagnostics;
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

    /// <summary>
    /// Allocation-free monotonic rate gate used by non-critical consumers such as UI rendering.
    /// A skipped publication never affects control, persistence, or diagnostic data.
    /// </summary>
    internal sealed class PeriodicDispatchGate
    {
        private readonly long _intervalTicks;
        private long _lastDispatchTick;

        public PeriodicDispatchGate(double maximumRateHz)
        {
            var rateHz = Math.Max(1.0, maximumRateHz);
            _intervalTicks = Math.Max(1L, (long)Math.Ceiling(Stopwatch.Frequency / rateHz));
        }

        public bool TryAcquire(long nowTick)
        {
            if (nowTick <= 0) return false;
            while (true)
            {
                var previous = Interlocked.Read(ref _lastDispatchTick);
                if (previous > 0 && nowTick >= previous && nowTick - previous < _intervalTicks)
                    return false;
                if (Interlocked.CompareExchange(ref _lastDispatchTick, nowTick, previous) == previous)
                    return true;
            }
        }

        public void Reset() => Interlocked.Exchange(ref _lastDispatchTick, 0);
    }

    internal sealed class DaqCallbackProducerGate
    {
        private int _active;
        private long _reentryCount;

        public bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref _active, 1, 0) == 0) return true;
            Interlocked.Increment(ref _reentryCount);
            return false;
        }

        public void Exit()
        {
            Volatile.Write(ref _active, 0);
        }

        public long ReentryCount => Interlocked.Read(ref _reentryCount);
        public bool IsActive => Volatile.Read(ref _active) != 0;
    }

    [Flags]
    public enum FastSignalQualityFlags
    {
        None = 0,
        NonFinite = 1,
        ProducerReentry = 2,
        SequenceDiscontinuity = 4,
        // 数值保留用于兼容旧版证据；V2.10.2.3 起不再产生，也不再作为控制无效标志。
        TimelineFuture = 8,
        GenerationMismatch = 16,
        AdcNearRail = 32,
        DuplicateBatch = 64,
        OutOfOrderBatch = 128,
        MonotonicTickInvalid = 256,
        FilterStateInvalid = 512
    }

    /// <summary>控制器使用的结构化快速电流样本。</summary>
    public readonly struct FastEpbCurrentSample
    {
        public FastEpbCurrentSample(
            int channel,
            double currentA,
            double representativeA,
            DateTime sampleUtc,
            long captureMonotonicTicks,
            long generation,
            long batchSequence,
            FastSignalQualityFlags qualityFlags)
        {
            Channel = channel;
            CurrentA = currentA;
            RepresentativeA = representativeA;
            SampleUtc = sampleUtc.Kind == DateTimeKind.Utc ? sampleUtc : sampleUtc.ToUniversalTime();
            CaptureMonotonicTicks = captureMonotonicTicks;
            Generation = generation;
            BatchSequence = batchSequence;
            QualityFlags = qualityFlags;
        }

        public int Channel { get; }
        public double CurrentA { get; }
        public double RepresentativeA { get; }
        public DateTime SampleUtc { get; }
        public long CaptureMonotonicTicks { get; }
        public long Generation { get; }
        public long BatchSequence { get; }
        public FastSignalQualityFlags QualityFlags { get; }
        public bool IsControlUsable =>
            CaptureMonotonicTicks > 0 &&
            !double.IsNaN(CurrentA) &&
            !double.IsInfinity(CurrentA) &&
            (QualityFlags & (FastSignalQualityFlags.NonFinite |
                             FastSignalQualityFlags.ProducerReentry |
                             FastSignalQualityFlags.SequenceDiscontinuity |
                             FastSignalQualityFlags.GenerationMismatch |
                             FastSignalQualityFlags.DuplicateBatch |
                             FastSignalQualityFlags.OutOfOrderBatch |
                             FastSignalQualityFlags.MonotonicTickInvalid |
                             FastSignalQualityFlags.FilterStateInvalid)) == 0;
    }

    /// <summary>兼容轮询控制所需的最近快速样本及新鲜度。</summary>
    public readonly struct FastCurrentSnapshot
    {
        public FastCurrentSnapshot(FastEpbCurrentSample sample, double ageMs, bool available)
        {
            Sample = sample;
            AgeMs = ageMs;
            Available = available;
        }

        public FastEpbCurrentSample Sample { get; }
        public double AgeMs { get; }
        public bool Available { get; }
        public bool IsFreshAndUsable(double maximumAgeMs) =>
            Available && Sample.IsControlUsable && AgeMs >= 0 && AgeMs <= Math.Max(1, maximumAgeMs);
    }

    internal readonly struct FastControlSampleValue
    {
        public FastControlSampleValue(
            int sourceRow,
            int channel,
            int pressureId,
            double representativeA,
            FastSignalQualityFlags qualityFlags = FastSignalQualityFlags.None)
        {
            SourceRow = sourceRow;
            Channel = channel;
            PressureId = pressureId;
            RepresentativeA = representativeA;
            QualityFlags = qualityFlags;
        }

        // Backward-compatible test helper.
        public FastControlSampleValue(int channel, double amps)
            : this(0, channel, 0, amps)
        {
        }

        public int SourceRow { get; }
        public int Channel { get; }
        public int PressureId { get; }
        public double RepresentativeA { get; }
        public FastSignalQualityFlags QualityFlags { get; }
        public double Amps => RepresentativeA;
    }

    internal readonly struct FastControlBatchMetadata
    {
        public FastControlBatchMetadata(
            long generation,
            long sourceSequence,
            DateTime sampleUtc,
            DateTime callbackArrivalUtc,
            long captureMonotonicTicks,
            long enqueuedMonotonicTicks,
            double sampleLeadMs,
            int producerThreadId,
            FastSignalQualityFlags qualityFlags)
            : this(
                generation,
                sourceSequence,
                sampleUtc,
                callbackArrivalUtc,
                captureMonotonicTicks,
                enqueuedMonotonicTicks,
                sampleLeadMs,
                0,
                ClockState.WarmingUp,
                0,
                0,
                0,
                producerThreadId,
                qualityFlags)
        {
        }

        public FastControlBatchMetadata(
            long generation,
            long sourceSequence,
            DateTime sampleUtc,
            DateTime callbackArrivalUtc,
            long captureMonotonicTicks,
            long enqueuedMonotonicTicks,
            double sampleLeadMs,
            double effectiveSampleRateHz,
            ClockState clockState,
            double estimatedSkewPpm,
            double clockResidualMs,
            double clockWindowSeconds,
            int producerThreadId,
            FastSignalQualityFlags qualityFlags)
        {
            Generation = generation;
            SourceSequence = sourceSequence;
            SampleUtc = sampleUtc.Kind == DateTimeKind.Utc ? sampleUtc : sampleUtc.ToUniversalTime();
            CallbackArrivalUtc = callbackArrivalUtc.Kind == DateTimeKind.Utc
                ? callbackArrivalUtc
                : callbackArrivalUtc.ToUniversalTime();
            CaptureMonotonicTicks = captureMonotonicTicks;
            EnqueuedMonotonicTicks = enqueuedMonotonicTicks;
            SampleLeadMs = sampleLeadMs;
            EffectiveSampleRateHz = effectiveSampleRateHz;
            ClockState = clockState;
            EstimatedSkewPpm = estimatedSkewPpm;
            ClockResidualMs = clockResidualMs;
            ClockWindowSeconds = clockWindowSeconds;
            ProducerThreadId = producerThreadId;
            QualityFlags = qualityFlags;
        }

        public long Generation { get; }
        public long SourceSequence { get; }
        public DateTime SampleUtc { get; }
        public DateTime CallbackArrivalUtc { get; }
        public long CaptureMonotonicTicks { get; }
        public long EnqueuedMonotonicTicks { get; }
        public double SampleLeadMs { get; }
        public double EffectiveSampleRateHz { get; }
        public ClockState ClockState { get; }
        public double EstimatedSkewPpm { get; }
        public double ClockResidualMs { get; }
        public double ClockWindowSeconds { get; }
        public int ProducerThreadId { get; }
        public FastSignalQualityFlags QualityFlags { get; }
    }

    internal enum ControlBatchIdentityResult
    {
        Accepted,
        GenerationChanged,
        Gap,
        Duplicate,
        OutOfOrder,
        MonotonicTickInvalid
    }

    /// <summary>由每设备唯一控制线程持有，验证来源批次身份及回调单调时钟。</summary>
    internal sealed class ControlBatchIdentityValidator
    {
        private long _generation = -1;
        private long _lastSequence;
        private long _lastCaptureTick;

        public void Reset()
        {
            _generation = -1;
            _lastSequence = 0;
            _lastCaptureTick = 0;
        }

        public ControlBatchIdentityResult Validate(FastControlBatchMetadata metadata)
        {
            if (metadata.SourceSequence <= 0)
                return ControlBatchIdentityResult.OutOfOrder;
            if (metadata.CaptureMonotonicTicks <= 0)
                return ControlBatchIdentityResult.MonotonicTickInvalid;
            if (metadata.Generation != _generation)
            {
                _generation = metadata.Generation;
                _lastSequence = metadata.SourceSequence;
                _lastCaptureTick = metadata.CaptureMonotonicTicks;
                return ControlBatchIdentityResult.GenerationChanged;
            }

            if (metadata.SourceSequence == _lastSequence)
                return ControlBatchIdentityResult.Duplicate;
            if (metadata.SourceSequence < _lastSequence)
                return ControlBatchIdentityResult.OutOfOrder;
            if (metadata.CaptureMonotonicTicks <= _lastCaptureTick)
                return ControlBatchIdentityResult.MonotonicTickInvalid;
            if (metadata.SourceSequence != _lastSequence + 1)
                return ControlBatchIdentityResult.Gap;

            Accept(metadata);
            return ControlBatchIdentityResult.Accepted;
        }

        public void Accept(FastControlBatchMetadata metadata)
        {
            _generation = metadata.Generation;
            _lastSequence = metadata.SourceSequence;
            _lastCaptureTick = metadata.CaptureMonotonicTicks;
        }
    }

    /// <summary>
    /// Per-device, preallocated single-producer/single-consumer control ring.
    /// Slots are never overwritten until the consumer advances the read sequence.
    /// </summary>
    internal sealed class ControlBatchRing
    {
        internal const int RawTailCapacity = 20;

        private sealed class Slot
        {
            public Slot(int maximumSamples)
            {
                Samples = new FastControlSampleValue[maximumSamples];
                RawTailVolts = new double[maximumSamples * RawTailCapacity];
            }

            public long PublishedSequence;
            public FastControlBatchMetadata Metadata;
            public int Count;
            public int RawTailCount;
            public readonly FastControlSampleValue[] Samples;
            public readonly double[] RawTailVolts;
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
                    ? slot.Metadata.EnqueuedMonotonicTicks
                    : 0;
            }
        }

        public bool TryEnqueue(
            FastControlBatchMetadata metadata,
            ReadOnlySpan<FastControlSampleValue> samples,
            double[,] raw)
        {
            if (samples.Length > MaximumSamplesPerBatch)
                throw new ArgumentOutOfRangeException(nameof(samples));

            var write = Volatile.Read(ref _writeSequence);
            var read = Volatile.Read(ref _readSequence);
            if (write - read >= Capacity) return false;

            var slot = _slots[(int)(write % Capacity)];
            slot.Metadata = metadata;
            slot.Count = samples.Length;
            slot.RawTailCount = raw == null ? 0 : Math.Min(RawTailCapacity, raw.GetLength(1));
            var rawStart = raw == null ? 0 : raw.GetLength(1) - slot.RawTailCount;
            for (var i = 0; i < samples.Length; i++)
            {
                slot.Samples[i] = samples[i];
                var targetOffset = i * RawTailCapacity;
                for (var j = 0; j < slot.RawTailCount; j++)
                    slot.RawTailVolts[targetOffset + j] = raw[samples[i].SourceRow, rawStart + j];
            }

            Volatile.Write(ref slot.PublishedSequence, write + 1);
            Volatile.Write(ref _writeSequence, write + 1);
            return true;
        }

        // Backward-compatible test helper.
        public bool TryEnqueue(
            long generation,
            DateTime timestamp,
            long enqueuedMonotonicTicks,
            ReadOnlySpan<FastControlSampleValue> samples)
        {
            var metadata = new FastControlBatchMetadata(
                generation,
                Volatile.Read(ref _writeSequence) + 1,
                timestamp,
                timestamp,
                enqueuedMonotonicTicks,
                enqueuedMonotonicTicks,
                0,
                Thread.CurrentThread.ManagedThreadId,
                FastSignalQualityFlags.None);
            return TryEnqueue(metadata, samples, null);
        }

        public bool TryDequeue(
            FastControlSampleValue[] destination,
            double[] rawTailDestination,
            out int count,
            out int rawTailCount,
            out FastControlBatchMetadata metadata)
        {
            if (destination == null || destination.Length < MaximumSamplesPerBatch)
                throw new ArgumentException("Destination is too small.", nameof(destination));
            if (rawTailDestination == null ||
                rawTailDestination.Length < MaximumSamplesPerBatch * RawTailCapacity)
                throw new ArgumentException("Raw-tail destination is too small.", nameof(rawTailDestination));

            while (true)
            {
                var read = Volatile.Read(ref _readSequence);
                var write = Volatile.Read(ref _writeSequence);
                if (read >= write)
                {
                    count = 0;
                    rawTailCount = 0;
                    metadata = default;
                    return false;
                }

                var slot = _slots[(int)(read % Capacity)];
                if (Volatile.Read(ref slot.PublishedSequence) != read + 1)
                {
                    Thread.Yield();
                    continue;
                }

                var localCount = slot.Count;
                var localRawTailCount = slot.RawTailCount;
                var localMetadata = slot.Metadata;
                for (var i = 0; i < localCount; i++)
                {
                    destination[i] = slot.Samples[i];
                    var offset = i * RawTailCapacity;
                    for (var j = 0; j < localRawTailCount; j++)
                        rawTailDestination[offset + j] = slot.RawTailVolts[offset + j];
                }

                if (Interlocked.CompareExchange(ref _readSequence, read + 1, read) != read)
                    continue;

                count = localCount;
                rawTailCount = localRawTailCount;
                metadata = localMetadata;
                return true;
            }
        }

        // Backward-compatible test helper.
        public bool TryDequeue(
            FastControlSampleValue[] destination,
            out int count,
            out long generation,
            out DateTime timestamp,
            out long enqueuedMonotonicTicks)
        {
            var raw = new double[MaximumSamplesPerBatch * RawTailCapacity];
            var result = TryDequeue(destination, raw, out count, out _, out var metadata);
            generation = metadata.Generation;
            timestamp = metadata.SampleUtc;
            enqueuedMonotonicTicks = metadata.EnqueuedMonotonicTicks;
            return result;
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
