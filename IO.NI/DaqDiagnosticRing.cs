using System;
using System.Collections.Generic;
using System.Threading;

namespace IO.NI
{
    internal struct DaqTimingValue
    {
        public DateTime TimestampUtc;
        public string Device;
        public string Kind;
        public long Generation;
        public int BatchSize;
        public int QueueDepth;
        public double CallbackIntervalMs;
        public double EndReadMs;
        public double RearmMs;
        public double QueueAgeMs;
        public double ProcessingMs;
        public double ConvertMs;
        public double FilterMs;
        public double PeakMs;
        public double DiskBatchBuildMs;
        public double DiskDispatchMs;
        public double UiNotifyMs;
        public double PersistenceWaitMs;
        public double RingWriteMs;
        public double SqliteMs;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public long ManagedMemoryBytes;
        public int ProcessId;
        public int ProcessBitness;
        public double ProcessCpuPercent;
        public double SystemCpuPercent;
        public double OtherCpuPercent;
        public long WorkingSetBytes;
        public long PrivateMemoryBytes;
        public long VirtualMemoryBytes;
        public int HandleCount;
        public int ThreadCount;
        public ulong SystemAvailableMemoryBytes;
        public long ProgramDriveFreeBytes;
        public double DiskQueueLength;
        public double DiskReadBytesPerSecond;
        public double DiskWriteBytesPerSecond;
        public int WorkerThreadsAvailable;
        public int IoThreadsAvailable;
        public string GcEtwStatus;
        public double GcPauseDurationMs;
        public DateTime GcPauseUtc;
        public long GcPauseCount;
        public int ControlQueueCapacity;
        public double SubscriberMaxMs;
        public double DriftMs;
        public DateTime ProcessedSampleUtc;
        public long BatchSequence;
        public int ProducerThreadId;
        public long ProducerReentryCount;
        public double SampleLeadMs;
        public double EffectiveSampleRateHz;
        public double EstimatedSkewPpm;
        public double ClockResidualMs;
        public double ClockWindowSeconds;
        public double ClockCorrectionPpm;
        public string ClockState;
        public long EpochId;
        public string InvalidReason;
        public double CandidateRateHz;
        public double CandidateSkewPpm;
        public int OutOfRangeConfirmations;
        public int ResidualConfirmations;
        public string QualityFlags;
        public string Detail;

        public static DaqTimingValue FromRecord(DaqTimingRecord value)
        {
            if (value == null) return default;
            return new DaqTimingValue
            {
                TimestampUtc = value.TimestampUtc,
                Device = value.Device,
                Kind = value.Kind,
                Generation = value.Generation,
                BatchSize = value.BatchSize,
                QueueDepth = value.QueueDepth,
                CallbackIntervalMs = value.CallbackIntervalMs,
                EndReadMs = value.EndReadMs,
                RearmMs = value.RearmMs,
                QueueAgeMs = value.QueueAgeMs,
                ProcessingMs = value.ProcessingMs,
                ConvertMs = value.ConvertMs,
                FilterMs = value.FilterMs,
                PeakMs = value.PeakMs,
                DiskBatchBuildMs = value.DiskBatchBuildMs,
                DiskDispatchMs = value.DiskDispatchMs,
                UiNotifyMs = value.UiNotifyMs,
                PersistenceWaitMs = value.PersistenceWaitMs,
                RingWriteMs = value.RingWriteMs,
                SqliteMs = value.SqliteMs,
                Gc0 = value.Gc0,
                Gc1 = value.Gc1,
                Gc2 = value.Gc2,
                ManagedMemoryBytes = value.ManagedMemoryBytes,
                ProcessId = value.ProcessId,
                ProcessBitness = value.ProcessBitness,
                ProcessCpuPercent = value.ProcessCpuPercent,
                SystemCpuPercent = value.SystemCpuPercent,
                OtherCpuPercent = value.OtherCpuPercent,
                WorkingSetBytes = value.WorkingSetBytes,
                PrivateMemoryBytes = value.PrivateMemoryBytes,
                VirtualMemoryBytes = value.VirtualMemoryBytes,
                HandleCount = value.HandleCount,
                ThreadCount = value.ThreadCount,
                SystemAvailableMemoryBytes = value.SystemAvailableMemoryBytes,
                ProgramDriveFreeBytes = value.ProgramDriveFreeBytes,
                DiskQueueLength = value.DiskQueueLength,
                DiskReadBytesPerSecond = value.DiskReadBytesPerSecond,
                DiskWriteBytesPerSecond = value.DiskWriteBytesPerSecond,
                WorkerThreadsAvailable = value.WorkerThreadsAvailable,
                IoThreadsAvailable = value.IoThreadsAvailable,
                GcEtwStatus = value.GcEtwStatus,
                GcPauseDurationMs = value.GcPauseDurationMs,
                GcPauseUtc = value.GcPauseUtc,
                GcPauseCount = value.GcPauseCount,
                ControlQueueCapacity = value.ControlQueueCapacity,
                SubscriberMaxMs = value.SubscriberMaxMs,
                DriftMs = value.DriftMs,
                ProcessedSampleUtc = value.ProcessedSampleUtc,
                BatchSequence = value.BatchSequence,
                ProducerThreadId = value.ProducerThreadId,
                ProducerReentryCount = value.ProducerReentryCount,
                SampleLeadMs = value.SampleLeadMs,
                EffectiveSampleRateHz = value.EffectiveSampleRateHz,
                EstimatedSkewPpm = value.EstimatedSkewPpm,
                ClockResidualMs = value.ClockResidualMs,
                ClockWindowSeconds = value.ClockWindowSeconds,
                ClockCorrectionPpm = value.ClockCorrectionPpm,
                ClockState = value.ClockState,
                EpochId = value.EpochId,
                InvalidReason = value.InvalidReason,
                CandidateRateHz = value.CandidateRateHz,
                CandidateSkewPpm = value.CandidateSkewPpm,
                OutOfRangeConfirmations = value.OutOfRangeConfirmations,
                ResidualConfirmations = value.ResidualConfirmations,
                QualityFlags = value.QualityFlags,
                Detail = value.Detail
            };
        }
    }

    /// <summary>Allocation-free multi-producer diagnostic history; snapshots allocate off the control path.</summary>
    internal sealed class DaqDiagnosticRing
    {
        private sealed class Slot
        {
            public long PublishedSequence;
            public DaqTimingValue Value;
        }

        private readonly Slot[] _slots;
        private long _nextSequence;

        public DaqDiagnosticRing(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _slots = new Slot[capacity];
            for (var i = 0; i < capacity; i++) _slots[i] = new Slot();
        }

        public void Append(DaqTimingValue value)
        {
            var sequence = Interlocked.Increment(ref _nextSequence);
            var slot = _slots[(int)((sequence - 1) % _slots.Length)];
            Volatile.Write(ref slot.PublishedSequence, 0);
            slot.Value = value;
            Volatile.Write(ref slot.PublishedSequence, sequence);
        }

        public DaqTimingValue[] Snapshot()
        {
            var latest = Interlocked.Read(ref _nextSequence);
            var earliest = Math.Max(1, latest - _slots.Length + 1);
            var records = new List<KeyValuePair<long, DaqTimingValue>>(_slots.Length);
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                var before = Volatile.Read(ref slot.PublishedSequence);
                if (before < earliest || before > latest) continue;
                var value = slot.Value;
                var after = Volatile.Read(ref slot.PublishedSequence);
                if (before != after || after == 0) continue;
                records.Add(new KeyValuePair<long, DaqTimingValue>(after, value));
            }
            records.Sort((x, y) => x.Key.CompareTo(y.Key));
            var result = new DaqTimingValue[records.Count];
            for (var i = 0; i < records.Count; i++) result[i] = records[i].Value;
            return result;
        }
    }
}
