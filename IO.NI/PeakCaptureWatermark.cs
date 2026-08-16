using System;

namespace IO.NI
{
    /// <summary>
    /// 峰值证据窗的正确性水印。所有比较都来自同一 Stopwatch 单调时钟域，
    /// DateTime 只用于外部显示，系统校时不得改变样本是否属于证据窗。
    /// </summary>
    internal sealed class PeakCaptureWatermark
    {
        public long Generation { get; private set; }
        public long StartAcceptedSequence { get; private set; }
        public long StartMonotonicTicks { get; private set; }
        public long CutoffAcceptedSequence { get; private set; }
        public long CutoffMonotonicTicks { get; private set; }
        public long ProcessedSequence { get; private set; }
        public long ProcessedThroughMonotonicTicks { get; private set; }
        public long LastIncludedSampleMonotonicTicks { get; private set; }
        public bool IsFrozen { get; private set; }
        public bool IsGenerationMatched { get; private set; }

        public void Arm(long generation, long acceptedSequence, long monotonicTicks)
        {
            Generation = generation;
            StartAcceptedSequence = Math.Max(0, acceptedSequence);
            StartMonotonicTicks = Math.Max(1, monotonicTicks);
            CutoffAcceptedSequence = 0;
            CutoffMonotonicTicks = 0;
            ProcessedSequence = 0;
            ProcessedThroughMonotonicTicks = 0;
            LastIncludedSampleMonotonicTicks = 0;
            IsFrozen = false;
            IsGenerationMatched = true;
        }

        public void Freeze(long generation, long acceptedSequence, long monotonicTicks)
        {
            if (IsFrozen) return;
            IsFrozen = true;
            IsGenerationMatched = generation == Generation;
            CutoffAcceptedSequence = Math.Max(0, acceptedSequence);
            CutoffMonotonicTicks = Math.Max(StartMonotonicTicks, monotonicTicks);
        }

        /// <summary>
        /// 推进后台处理水印，并返回该样本是否应纳入峰值统计。
        /// 不同 generation 的批次永不推进本捕获窗。
        /// </summary>
        public bool Observe(long generation, long sequence, long sampleMonotonicTicks)
        {
            if (generation != Generation || sampleMonotonicTicks <= 0) return false;

            if (sequence > ProcessedSequence)
                ProcessedSequence = sequence;
            if (sampleMonotonicTicks > ProcessedThroughMonotonicTicks)
                ProcessedThroughMonotonicTicks = sampleMonotonicTicks;

            if (sampleMonotonicTicks < StartMonotonicTicks) return false;
            if (IsFrozen && sampleMonotonicTicks > CutoffMonotonicTicks) return false;

            LastIncludedSampleMonotonicTicks = sampleMonotonicTicks;
            return true;
        }

        public bool IsCutoffCovered =>
            IsFrozen &&
            IsGenerationMatched &&
            ProcessedSequence >= CutoffAcceptedSequence &&
            ProcessedThroughMonotonicTicks >= CutoffMonotonicTicks;

        public double GetEvidenceTailLagMs(long stopwatchFrequency)
        {
            if (!IsFrozen || LastIncludedSampleMonotonicTicks <= 0 || stopwatchFrequency <= 0)
                return double.PositiveInfinity;
            return Math.Max(
                0,
                (CutoffMonotonicTicks - LastIncludedSampleMonotonicTicks) * 1000.0 /
                stopwatchFrequency);
        }
    }
}
