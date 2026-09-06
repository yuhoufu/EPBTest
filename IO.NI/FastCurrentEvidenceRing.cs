using System;
using System.Collections.Generic;
using System.Threading;

namespace IO.NI
{
    internal sealed class FastCurrentEvidenceRecord
    {
        public DateTime SampleUtc { get; set; }
        public DateTime CallbackArrivalUtc { get; set; }
        public int Channel { get; set; }
        public string Device { get; set; }
        public long Generation { get; set; }
        public long BatchSequence { get; set; }
        public int ProducerThreadId { get; set; }
        public long CaptureMonotonicTicks { get; set; }
        public long EnqueuedMonotonicTicks { get; set; }
        public double SampleLeadMs { get; set; }
        public double EffectiveSampleRateHz { get; set; }
        public ClockState ClockState { get; set; }
        public double EstimatedSkewPpm { get; set; }
        public double ClockResidualMs { get; set; }
        public double ClockWindowSeconds { get; set; }
        public double RepresentativeA { get; set; }
        public double FilteredA { get; set; }
        public FastSignalQualityFlags QualityFlags { get; set; }
        public double[] RawTailVolts { get; set; } = Array.Empty<double>();
    }

    /// <summary>每通道固定容量快速证据环；控制线程写，快照线程只读。</summary>
    internal sealed class FastCurrentEvidenceHistory
    {
        private sealed class Slot
        {
            public long PublishedSequence;
            public DateTime SampleUtc;
            public DateTime CallbackArrivalUtc;
            public string Device;
            public long Generation;
            public long BatchSequence;
            public int ProducerThreadId;
            public long CaptureMonotonicTicks;
            public long EnqueuedMonotonicTicks;
            public double SampleLeadMs;
            public double EffectiveSampleRateHz;
            public ClockState ClockState;
            public double EstimatedSkewPpm;
            public double ClockResidualMs;
            public double ClockWindowSeconds;
            public double RepresentativeA;
            public double FilteredA;
            public FastSignalQualityFlags QualityFlags;
            public int RawTailCount;
            public readonly double[] RawTailVolts = new double[ControlBatchRing.RawTailCapacity];
        }

        private sealed class ChannelBuffer
        {
            public ChannelBuffer(int capacity)
            {
                Slots = new Slot[capacity];
                for (var i = 0; i < capacity; i++) Slots[i] = new Slot();
            }

            public readonly Slot[] Slots;
            public long NextSequence;
        }

        private readonly ChannelBuffer[] _channels = new ChannelBuffer[13];

        public FastCurrentEvidenceHistory(int capacityPerChannel)
        {
            if (capacityPerChannel < 1) throw new ArgumentOutOfRangeException(nameof(capacityPerChannel));
            for (var channel = 1; channel <= 12; channel++)
                _channels[channel] = new ChannelBuffer(capacityPerChannel);
        }

        public void Append(
            string device,
            FastControlBatchMetadata metadata,
            FastControlSampleValue sample,
            double filteredA,
            double[] rawTail,
            int rawTailOffset,
            int rawTailCount)
        {
            if (sample.Channel < 1 || sample.Channel > 12) return;
            var buffer = _channels[sample.Channel];
            var sequence = Interlocked.Increment(ref buffer.NextSequence);
            var slot = buffer.Slots[(int)((sequence - 1) % buffer.Slots.Length)];
            Volatile.Write(ref slot.PublishedSequence, 0);
            slot.SampleUtc = metadata.SampleUtc;
            slot.CallbackArrivalUtc = metadata.CallbackArrivalUtc;
            slot.Device = device ?? string.Empty;
            slot.Generation = metadata.Generation;
            slot.BatchSequence = metadata.SourceSequence;
            slot.ProducerThreadId = metadata.ProducerThreadId;
            slot.CaptureMonotonicTicks = metadata.CaptureMonotonicTicks;
            slot.EnqueuedMonotonicTicks = metadata.EnqueuedMonotonicTicks;
            slot.SampleLeadMs = metadata.SampleLeadMs;
            slot.EffectiveSampleRateHz = metadata.EffectiveSampleRateHz;
            slot.ClockState = metadata.ClockState;
            slot.EstimatedSkewPpm = metadata.EstimatedSkewPpm;
            slot.ClockResidualMs = metadata.ClockResidualMs;
            slot.ClockWindowSeconds = metadata.ClockWindowSeconds;
            slot.RepresentativeA = sample.RepresentativeA;
            slot.FilteredA = filteredA;
            slot.QualityFlags = metadata.QualityFlags;
            slot.RawTailCount = Math.Max(0, Math.Min(ControlBatchRing.RawTailCapacity, rawTailCount));
            for (var i = 0; i < slot.RawTailCount; i++)
                slot.RawTailVolts[i] = rawTail[rawTailOffset + i];
            Volatile.Write(ref slot.PublishedSequence, sequence);
        }

        public FastCurrentEvidenceRecord[] Snapshot(int channel, long cutoffMonotonicTicks)
        {
            if (channel < 1 || channel > 12) return Array.Empty<FastCurrentEvidenceRecord>();
            var buffer = _channels[channel];
            var latest = Interlocked.Read(ref buffer.NextSequence);
            var earliest = Math.Max(1, latest - buffer.Slots.Length + 1);
            var records = new List<KeyValuePair<long, FastCurrentEvidenceRecord>>(buffer.Slots.Length);
            foreach (var slot in buffer.Slots)
            {
                var before = Volatile.Read(ref slot.PublishedSequence);
                if (before < earliest || before > latest ||
                    slot.CaptureMonotonicTicks < cutoffMonotonicTicks) continue;
                var raw = new double[slot.RawTailCount];
                for (var i = 0; i < raw.Length; i++) raw[i] = slot.RawTailVolts[i];
                var record = new FastCurrentEvidenceRecord
                {
                    SampleUtc = slot.SampleUtc,
                    CallbackArrivalUtc = slot.CallbackArrivalUtc,
                    Channel = channel,
                    Device = slot.Device,
                    Generation = slot.Generation,
                    BatchSequence = slot.BatchSequence,
                    ProducerThreadId = slot.ProducerThreadId,
                    CaptureMonotonicTicks = slot.CaptureMonotonicTicks,
                    EnqueuedMonotonicTicks = slot.EnqueuedMonotonicTicks,
                    SampleLeadMs = slot.SampleLeadMs,
                    EffectiveSampleRateHz = slot.EffectiveSampleRateHz,
                    ClockState = slot.ClockState,
                    EstimatedSkewPpm = slot.EstimatedSkewPpm,
                    ClockResidualMs = slot.ClockResidualMs,
                    ClockWindowSeconds = slot.ClockWindowSeconds,
                    RepresentativeA = slot.RepresentativeA,
                    FilteredA = slot.FilteredA,
                    QualityFlags = slot.QualityFlags,
                    RawTailVolts = raw
                };
                var after = Volatile.Read(ref slot.PublishedSequence);
                if (before == after && after != 0)
                    records.Add(new KeyValuePair<long, FastCurrentEvidenceRecord>(after, record));
            }
            records.Sort((x, y) => x.Key.CompareTo(y.Key));
            var result = new FastCurrentEvidenceRecord[records.Count];
            for (var i = 0; i < result.Length; i++) result[i] = records[i].Value;
            return result;
        }
    }
}
