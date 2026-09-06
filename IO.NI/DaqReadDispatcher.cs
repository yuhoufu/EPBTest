using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace IO.NI
{
    // One reader and one processor per device. Admission never waits for a
    // subscriber; a full queue is a fault with an explicit rejected-read fact.
    internal sealed class DaqReadDispatcher<T> where T : class
    {
        private readonly BlockingCollection<T> _queue;
        private readonly Thread _thread;
        internal readonly ManualResetEventSlim Quiesced = new ManualResetEventSlim(false);
        internal int Depth => _queue.Count;
        internal DaqReadDispatcher(string name, int capacity, Action<T> consume, Action<Exception> fault)
        {
            _queue = new BlockingCollection<T>(capacity);
            _thread = new Thread(() =>
            {
                try { foreach (var frame in _queue.GetConsumingEnumerable()) consume(frame); }
                catch (Exception ex) { fault(ex); }
                finally { _queue.CompleteAdding(); Quiesced.Set(); }
            }) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
        internal bool TryPublish(T frame)
        {
            try { return _queue.TryAdd(frame); }
            catch (InvalidOperationException) { return false; }
        }
        internal void Complete() => _queue.CompleteAdding();
    }

    internal static class DaqBacklogPolicy
    {
        internal static double Milliseconds(int samples, double sampleRate) =>
            sampleRate > 0 && !double.IsNaN(sampleRate) && !double.IsInfinity(sampleRate)
                ? Math.Max(0, samples) * 1000.0 / sampleRate : double.PositiveInfinity;
        internal static bool Reject(int samples, double sampleRate, double maxAgeMs = 100) =>
            Milliseconds(samples, sampleRate) > maxAgeMs;
    }

    // All safety admission fields are frozen from ONE processed control batch.
    internal sealed class DaqControlPublication
    {
        private readonly FastControlBatchMetadata _batch;
        private readonly long _processedTick;
        internal DaqControlPublication(FastControlBatchMetadata batch, long processedTick)
        {
            _batch = batch; _processedTick = processedTick;
        }
        internal static DaqControlPublication Capture(
            ref DaqControlPublication slot, out long now)
        {
            // The comparison clock MUST follow the immutable publication read.
            // Reading it first can turn a concurrently committed fresh batch into
            // an infinite age (processedTick > now) and spuriously trip the rig.
            var publication = Volatile.Read(ref slot);
            now = Stopwatch.GetTimestamp();
            return publication;
        }
        internal void Apply(DaqFreshnessSnapshot result, long currentGeneration, long now, double maxAgeMs)
        {
            result.Generation = _batch.Generation;
            result.LastProcessedSequence = _batch.SourceSequence;
            result.LastControlProcessedMonotonicTicks = _processedTick;
            result.LastArrivalMonotonicTicks = _processedTick;
            result.ProcessedSampleUtc = _batch.SampleUtc;
            result.EffectiveSampleRateHz = _batch.EffectiveSampleRateHz;
            result.ClockState = _batch.ClockState;
            result.BufferedSamples = _batch.BufferedSamples;
            result.DriverBacklogMs = DaqBacklogPolicy.Milliseconds(_batch.BufferedSamples, _batch.EffectiveSampleRateHz);
            result.ReaderLagState = _batch.ReaderLagState;
            result.ConsecutiveFreshBatches = _batch.ConsecutiveFreshBatches;
            result.QualityFlags = _batch.QualityFlags;
            result.AgeMs = result.ControlProcessedAgeMs = Age(_processedTick, now);
            result.SampleAgeMs = Age(_batch.CaptureMonotonicTicks, now);
            result.RejectionReason = _batch.Generation != currentGeneration ? "GenerationMismatch" :
                result.SampleAgeMs > maxAgeMs ? "SampleAgeExceeded" :
                result.AgeMs > maxAgeMs ? "ControlDispatchAgeExceeded" :
                result.DriverBacklogMs > maxAgeMs ? "DriverBacklogExceeded" :
                _batch.ReaderLagState != DaqReaderLagState.Healthy ? "Reader" + _batch.ReaderLagState :
                _batch.ConsecutiveFreshBatches < 3 ? "FreshRejoinPending" :
                (_batch.QualityFlags & FastSignalQualityFlags.SampleStale) != 0 ? "SampleQualityStale" : string.Empty;
            result.IsFresh = result.RejectionReason.Length == 0;
        }
        private static double Age(long tick, long now) => tick <= 0 || tick > now
            ? double.PositiveInfinity : (now - tick) * 1000.0 / Stopwatch.Frequency;
    }
}
