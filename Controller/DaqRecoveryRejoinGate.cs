using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    public readonly struct DaqRecoveryRejoinObservation
    {
        public DaqRecoveryRejoinObservation(
            bool callbacksFresh,
            long generation,
            long processedSequence,
            long callbackGapCount,
            long discontinuityCount,
            bool persistenceClosed,
            bool safeOffEvidence,
            bool watchdogHealthy,
            bool takeoverPending)
        {
            CallbacksFresh = callbacksFresh;
            Generation = generation;
            ProcessedSequence = processedSequence;
            CallbackGapCount = callbackGapCount;
            DiscontinuityCount = discontinuityCount;
            PersistenceClosed = persistenceClosed;
            SafeOffEvidence = safeOffEvidence;
            WatchdogHealthy = watchdogHealthy;
            TakeoverPending = takeoverPending;
        }

        public bool CallbacksFresh { get; }
        public long Generation { get; }
        public long ProcessedSequence { get; }
        public long CallbackGapCount { get; }
        public long DiscontinuityCount { get; }
        public bool PersistenceClosed { get; }
        public bool SafeOffEvidence { get; }
        public bool WatchdogHealthy { get; }
        public bool TakeoverPending { get; }
        public bool IsHealthy => CallbacksFresh && PersistenceClosed && SafeOffEvidence &&
                                 WatchdogHealthy && !TakeoverPending && Generation > 0 &&
                                 ProcessedSequence > 0;
    }

    public static class DaqRecoveryRejoinGate
    {
        public static async Task WaitAsync(
            TimeSpan stableWindow,
            TimeSpan maximumWait,
            Func<DaqRecoveryRejoinObservation> observe,
            CancellationToken token)
        {
            await WaitAsync(
                    stableWindow,
                    maximumWait,
                    observe,
                    Stopwatch.GetTimestamp,
                    Stopwatch.Frequency,
                    cancellation => Task.Delay(20, cancellation),
                    token)
                .ConfigureAwait(false);
        }

        internal static async Task WaitAsync(
            TimeSpan stableWindow,
            TimeSpan maximumWait,
            Func<DaqRecoveryRejoinObservation> observe,
            Func<long> getMonotonicTimestamp,
            long monotonicFrequency,
            Func<CancellationToken, Task> delay,
            CancellationToken token)
        {
            if (stableWindow <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(stableWindow));
            if (maximumWait < stableWindow)
                throw new ArgumentOutOfRangeException(nameof(maximumWait));
            if (observe == null) throw new ArgumentNullException(nameof(observe));
            if (getMonotonicTimestamp == null)
                throw new ArgumentNullException(nameof(getMonotonicTimestamp));
            if (monotonicFrequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
            if (delay == null) throw new ArgumentNullException(nameof(delay));

            var started = getMonotonicTimestamp();
            long stableSince = 0;
            long generation = 0;
            long firstSequence = 0;
            long previousSequence = 0;
            long gapBaseline = 0;
            long discontinuityBaseline = 0;
            while (ElapsedMilliseconds(started, getMonotonicTimestamp(), monotonicFrequency) <=
                   maximumWait.TotalMilliseconds)
            {
                token.ThrowIfCancellationRequested();
                var current = observe();
                var now = getMonotonicTimestamp();
                var regressed = stableSince != 0 &&
                                (current.Generation != generation ||
                                 current.ProcessedSequence < previousSequence ||
                                 current.CallbackGapCount != gapBaseline ||
                                 current.DiscontinuityCount != discontinuityBaseline);
                if (!current.IsHealthy || regressed)
                {
                    stableSince = 0;
                    generation = 0;
                    firstSequence = 0;
                    previousSequence = 0;
                }
                else if (stableSince == 0)
                {
                    stableSince = now;
                    generation = current.Generation;
                    firstSequence = current.ProcessedSequence;
                    previousSequence = current.ProcessedSequence;
                    gapBaseline = current.CallbackGapCount;
                    discontinuityBaseline = current.DiscontinuityCount;
                }
                else
                {
                    previousSequence = current.ProcessedSequence;
                    var stableMs = ElapsedMilliseconds(
                        stableSince, now, monotonicFrequency);
                    if (stableMs >= stableWindow.TotalMilliseconds &&
                        current.ProcessedSequence > firstSequence)
                        return;
                }
                await delay(token).ConfigureAwait(false);
            }
            throw new TimeoutException("DaqRecoveryStableRejoinTimeout");
        }

        private static double ElapsedMilliseconds(long start, long end, long frequency)
        {
            if (end <= start) return 0;
            return (end - start) * 1000.0 / frequency;
        }
    }
}
